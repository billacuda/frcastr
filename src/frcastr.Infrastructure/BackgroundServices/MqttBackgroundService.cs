using System.Security.Cryptography;
using System.Text;
using frcastr.Core.Entities;
using frcastr.Core.Enums;
using frcastr.Core.Interfaces;
using frcastr.Infrastructure.Data;
using frcastr.Infrastructure.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MQTTnet;

namespace frcastr.Infrastructure.BackgroundServices;

public class MqttBackgroundService(
    IServiceScopeFactory scopeFactory,
    ILogger<MqttBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan ReconcileInterval = TimeSpan.FromSeconds(30);

    /// <summary>Connected clients keyed by DataSource.Id, so sources can be added, edited and removed at runtime.</summary>
    private readonly Dictionary<int, ManagedSource> _sources = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // DB may not be configured yet (pre-setup). Catch here so the host does not crash.
            try { await ReconcileAsync(stoppingToken); }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "MQTT: initial connection attempt failed; will retry.");
            }

            using var timer = new PeriodicTimer(ReconcileInterval);
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try { await ReconcileAsync(stoppingToken); }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogError(ex, "MQTT reconcile failed.");
                }
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
        finally
        {
            // WaitForNextTickAsync throws on stop rather than returning false, so the disconnect
            // has to live in a finally or it never runs.
            foreach (var managed in _sources.Values)
                await DisposeClientAsync(managed);
            _sources.Clear();
        }
    }

    /// <summary>
    /// Brings the live client set in line with the enabled MQTT sources in the database: connects
    /// new ones, reconnects changed ones with fresh config, drops removed or disabled ones, and
    /// reconnects dropped connections. Runs every 30 s, so no restart is needed to add a device.
    /// </summary>
    private async Task ReconcileAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var rows = await db.DataSources
            .Where(s => s.IsEnabled && s.Type == DataSourceType.Mqtt)
            .AsNoTracking()
            .ToListAsync(ct);

        var wanted = rows.ToDictionary(s => s.Id);

        // Remove sources that are gone, disabled, or whose config changed (reconnected below).
        foreach (var id in _sources.Keys.ToList())
        {
            var managed = _sources[id];
            if (wanted.TryGetValue(id, out var row) && ConfigHash(row) == managed.ConfigHash) continue;

            logger.LogInformation("MQTT: dropping client for source {SourceId} ({Reason}).",
                id, wanted.ContainsKey(id) ? "config changed" : "source removed or disabled");
            await DisposeClientAsync(managed);
            _sources.Remove(id);
        }

        // Connect anything not currently held.
        foreach (var row in rows)
        {
            if (_sources.ContainsKey(row.Id)) continue;
            try
            {
                var managed = await CreateClientAsync(row, ct);
                if (managed is not null) _sources[row.Id] = managed;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to connect MQTT source {SourceId} ({Name}).", row.Id, row.Name);
            }
        }

        // Reconnect anything that dropped.
        foreach (var (id, managed) in _sources)
        {
            if (managed.Client.IsConnected) continue;
            logger.LogWarning("MQTT client for source {SourceId} disconnected; attempting reconnect.", id);
            try { await managed.Client.ReconnectAsync(ct); }
            catch (Exception ex) { logger.LogError(ex, "MQTT reconnect failed for source {SourceId}.", id); }
        }
    }

    private async Task<ManagedSource?> CreateClientAsync(DataSource source, CancellationToken ct)
    {
        var cfg = MqttSourceConfig.TryParse(source.Config);
        if (cfg is null)
        {
            logger.LogWarning("Source {SourceId}: invalid or missing MQTT config JSON.", source.Id);
            return null;
        }
        if (string.IsNullOrWhiteSpace(cfg.Broker)) return null;

        var factory = new MqttClientFactory();
        var client = factory.CreateMqttClient();

        // Explicit, readable client id; the random suffix keeps multiple app instances from
        // kicking each other off the broker.
        var clientId = $"frcastr-{source.Id}-{Guid.NewGuid():N}"[..23];

        var optionsBuilder = new MqttClientOptionsBuilder()
            .WithClientId(clientId)
            .WithTcpServer(cfg.Broker, cfg.EffectivePort);

        if (!string.IsNullOrWhiteSpace(cfg.Username))
            optionsBuilder.WithCredentials(cfg.Username, cfg.Password);

        var managed = new ManagedSource(client, source.Id, source.Config, cfg, ConfigHash(source));

        client.ApplicationMessageReceivedAsync += async e =>
        {
            var bytes = e.ApplicationMessage.Payload;
            var payload = bytes.Length > 0 ? Encoding.UTF8.GetString(bytes) : string.Empty;
            try
            {
                await HandleMessageAsync(managed, e.ApplicationMessage.Topic, payload);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "MQTT source {SourceId}: unhandled error processing '{Topic}'.",
                    managed.SourceId, e.ApplicationMessage.Topic);
            }
        };

        await client.ConnectAsync(optionsBuilder.Build(), ct);

        var subBuilder = new MqttClientSubscribeOptionsBuilder()
            .WithTopicFilter(new MqttTopicFilterBuilder().WithTopic(cfg.GetSubscribeFilter()).Build());

        var statusPattern = cfg.GetStatusTopicPattern();
        if (statusPattern is not null)
            subBuilder.WithTopicFilter(new MqttTopicFilterBuilder()
                .WithTopic(statusPattern.ToSubscribeFilter()).Build());

        await client.SubscribeAsync(subBuilder.Build(), ct);

        logger.LogInformation("MQTT: connected to {Broker} for source {SourceId}, subscribed to '{Filter}'.",
            cfg.Broker, source.Id, cfg.GetSubscribeFilter());
        return managed;
    }

    private async Task HandleMessageAsync(ManagedSource managed, string topic, string payload)
    {
        var cfg = managed.Config;

        // Last-will / status messages only flip the device's online flag.
        var statusPattern = cfg.GetStatusTopicPattern();
        if (statusPattern is not null && statusPattern.TryMatch(topic, out var statusDevice, out _)
            && statusDevice is not null)
        {
            await HandleStatusAsync(managed, statusDevice, payload);
            return;
        }

        var pattern = cfg.GetTopicPattern();
        string? deviceKey = null;
        string? measure = null;

        if (pattern is not null)
        {
            if (!pattern.TryMatch(topic, out deviceKey, out measure))
            {
                logger.LogDebug("MQTT source {SourceId}: topic '{Topic}' does not match pattern '{Pattern}'.",
                    managed.SourceId, topic, pattern.Pattern);
                return;
            }
        }

        var parsed = MqttPayloadParser.Parse(cfg, topic, measure, payload);
        // A deviceId in the payload wins over the topic segment.
        deviceKey = string.IsNullOrWhiteSpace(parsed.DeviceId) ? deviceKey : parsed.DeviceId;

        if (parsed.Values.Count == 0)
        {
            logger.LogDebug("MQTT source {SourceId}: no mapped channels in payload for '{Topic}'.",
                managed.SourceId, topic);
            return;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var weatherData = scope.ServiceProvider.GetRequiredService<IWeatherDataService>();
        var statusService = scope.ServiceProvider.GetRequiredService<IDataSourceStatusService>();
        var cacheInvalidator = scope.ServiceProvider.GetService<IWeatherCacheInvalidator>();

        if (!await db.DataSources.AnyAsync(s => s.Id == managed.SourceId)) return;

        var device = await ResolveDeviceAsync(db, managed, deviceKey, parsed.FirmwareVersion);
        if (deviceKey is not null && device is null) return;   // unknown and auto-register disabled
        if (device is not null && !device.IsEnabled)
        {
            logger.LogDebug("MQTT source {SourceId}: device '{Device}' is disabled; dropping message.",
                managed.SourceId, deviceKey);
            return;
        }

        var timestamp = DateTime.UtcNow;
        var accepted = new List<(string Channel, decimal Value)>();

        // The source's fieldMapping names channels for every device on it alike; a device may
        // redirect its own. Applied before validation so calibration offsets and sanity bounds are
        // looked up under the channel the reading actually ends up on.
        var overrides = DeviceChannelOverrides.Parse(device?.ChannelOverrides);

        foreach (var (field, sourceChannel, rawValue, unit) in parsed.Values)
        {
            var channel = DeviceChannelOverrides.Apply(overrides, field, sourceChannel);
            var adjusted = ChannelProcessing.ApplyAndValidate(channel, rawValue, managed.ConfigJson);
            if (adjusted is null)
            {
                logger.LogWarning("MQTT source {SourceId}: '{Channel}' = {Value} dropped (out of bounds).",
                    managed.SourceId, channel, rawValue);
                continue;
            }

            db.WeatherReadings.Add(new WeatherReading
            {
                ChannelName = channel,
                Value = adjusted.Value,
                Unit = string.IsNullOrWhiteSpace(unit) ? cfg.GetUnit(channel) : unit,
                Timestamp = timestamp,
                SourceId = managed.SourceId,
                DeviceId = device?.Id
            });

            accepted.Add((channel, adjusted.Value));
        }

        if (accepted.Count == 0) return;

        if (device is not null)
        {
            device.LastSeenAt = timestamp;
            device.IsOnline = true;
            if (!string.IsNullOrWhiteSpace(parsed.FirmwareVersion))
                device.FirmwareVersion = parsed.FirmwareVersion;
        }

        // One round-trip for every channel in the message.
        await db.SaveChangesAsync();

        foreach (var (channel, _) in accepted)
            statusService.RecordReading(channel, device?.Id);

        // One round trip for the whole message rather than one per channel.
        await weatherData.UpdateChannelRecordsAsync(accepted, timestamp, managed.SourceId, device?.Id);

        // The dashboard's current-conditions response is cached for 15 s; without this the readings
        // that just arrived would not show up until it expired.
        if (cacheInvalidator is not null)
            await cacheInvalidator.InvalidateCurrentAsync();
    }

    private async Task HandleStatusAsync(ManagedSource managed, string deviceKey, string payload)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var device = await db.Devices.FirstOrDefaultAsync(d => d.DeviceId == deviceKey);
        if (device is null) return;

        var online = payload.Trim().Equals("online", StringComparison.OrdinalIgnoreCase);
        if (device.IsOnline == online) return;

        device.IsOnline = online;
        await db.SaveChangesAsync();

        logger.LogInformation("MQTT: device '{Device}' reported {Status}.", deviceKey, online ? "online" : "offline");
    }

    /// <summary>
    /// Finds the Device row for a topic-derived id, registering it on first sight when the source
    /// has autoRegisterDevices enabled (the default).
    /// </summary>
    private async Task<Device?> ResolveDeviceAsync(
        ApplicationDbContext db, ManagedSource managed, string? deviceKey, string? firmware)
    {
        if (string.IsNullOrWhiteSpace(deviceKey)) return null;

        var device = await db.Devices.FirstOrDefaultAsync(d => d.DeviceId == deviceKey);
        if (device is not null)
        {
            if (device.SourceId != managed.SourceId) device.SourceId = managed.SourceId;
            return device;
        }

        if (!managed.Config.AutoRegister)
        {
            logger.LogWarning(
                "MQTT source {SourceId}: unknown device '{Device}' and autoRegisterDevices is off; dropping message.",
                managed.SourceId, deviceKey);
            return null;
        }

        device = new Device
        {
            DeviceId = deviceKey,
            Name = deviceKey,
            Model = "ESP32-S3",
            FirmwareVersion = firmware,
            SourceId = managed.SourceId,
            LastSeenAt = DateTime.UtcNow
        };
        db.Devices.Add(device);
        await db.SaveChangesAsync();

        logger.LogInformation("MQTT source {SourceId}: auto-registered device '{Device}'.",
            managed.SourceId, deviceKey);
        return device;
    }

    /// <summary>Detects admin edits so a changed source is reconnected with fresh config.</summary>
    private static string ConfigHash(DataSource source)
    {
        var raw = $"{source.IsEnabled}|{source.Config}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
    }

    private async Task DisposeClientAsync(ManagedSource managed)
    {
        try { await managed.Client.DisconnectAsync(); }
        catch (Exception ex) { logger.LogDebug(ex, "MQTT: error disconnecting source {SourceId}.", managed.SourceId); }
        managed.Client.Dispose();
    }

    private sealed record ManagedSource(
        IMqttClient Client,
        int SourceId,
        string? ConfigJson,
        MqttSourceConfig Config,
        string ConfigHash);
}
