using System.Text;
using System.Text.Json;
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
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    private readonly List<IMqttClient> _clients = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // DB may not be configured yet (pre-setup). Catch here so the host does not crash.
        try { await ConnectAllSourcesAsync(stoppingToken); }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "MQTT: initial connection attempt failed; will retry.");
        }

        // Keep alive — reconnect any dropped clients every 30 seconds.
        // Also retry initial connection if no clients connected yet (e.g. DB was unavailable at startup).
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await ReconnectStaleAsync(stoppingToken);
                if (_clients.Count == 0)
                    await ConnectAllSourcesAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "MQTT reconnect check failed.");
            }
        }

        // Disconnect gracefully on shutdown
        foreach (var client in _clients)
        {
            try { await client.DisconnectAsync(); } catch { }
            client.Dispose();
        }
    }

    private async Task ConnectAllSourcesAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var sources = await db.DataSources
            .Where(s => s.IsEnabled && s.Type == DataSourceType.Mqtt)
            .ToListAsync(ct);

        foreach (var source in sources)
        {
            try
            {
                var client = await CreateClientAsync(source, ct);
                if (client is not null) _clients.Add(client);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to connect MQTT source {SourceId} ({Name}).", source.Id, source.Name);
            }
        }
    }

    private async Task<IMqttClient?> CreateClientAsync(DataSource source, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(source.Config)) return null;

        MqttSourceConfig? cfg;
        try { cfg = JsonSerializer.Deserialize<MqttSourceConfig>(source.Config, JsonOpts); }
        catch { logger.LogWarning("Source {SourceId}: invalid MQTT config JSON.", source.Id); return null; }

        if (cfg is null || string.IsNullOrWhiteSpace(cfg.Broker)) return null;

        var factory = new MqttClientFactory();
        var client = factory.CreateMqttClient();

        var optionsBuilder = new MqttClientOptionsBuilder()
            .WithTcpServer(cfg.Broker, cfg.Port > 0 ? cfg.Port : 1883);

        if (!string.IsNullOrWhiteSpace(cfg.Username))
            optionsBuilder.WithCredentials(cfg.Username, cfg.Password);

        var options = optionsBuilder.Build();

        client.ApplicationMessageReceivedAsync += async e =>
        {
            var payloadBytes = e.ApplicationMessage.Payload;
            var payload = payloadBytes.Length > 0 ? Encoding.UTF8.GetString(payloadBytes) : string.Empty;
            await HandleMessageAsync(source.Id, source.Config,
                cfg.ChannelMapping ?? [],
                e.ApplicationMessage.Topic,
                payload);
        };

        await client.ConnectAsync(options, ct);

        var subOptions = new MqttClientSubscribeOptionsBuilder()
            .WithTopicFilter(new MqttTopicFilterBuilder().WithTopic(cfg.Topic ?? "#").Build())
            .Build();
        await client.SubscribeAsync(subOptions, ct);

        logger.LogInformation("MQTT: connected to {Broker} for source {SourceId}.", cfg.Broker, source.Id);
        return client;
    }

    private async Task HandleMessageAsync(int sourceId, string? config,
        Dictionary<string, string> channelMapping, string topic, string payload)
    {
        if (!channelMapping.TryGetValue(topic, out var channelName)) return;

        if (!decimal.TryParse(payload, out var rawValue)) return;

        var adjusted = ChannelProcessing.ApplyAndValidate(channelName, rawValue, config);
        if (adjusted is null)
        {
            logger.LogWarning("MQTT source {SourceId}: '{Channel}' = {Value} dropped (out of bounds).",
                sourceId, channelName, rawValue);
            return;
        }

        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var weatherData = scope.ServiceProvider.GetRequiredService<IWeatherDataService>();
            var statusService = scope.ServiceProvider.GetRequiredService<IDataSourceStatusService>();
            var source = await db.DataSources.FirstOrDefaultAsync(s => s.Id == sourceId);
            if (source is null) return;

            var unit = ExtractUnit(config, channelName) ?? "";
            var timestamp = DateTime.UtcNow;

            db.WeatherReadings.Add(new WeatherReading
            {
                ChannelName = channelName,
                Value = adjusted.Value,
                Unit = unit,
                Timestamp = timestamp,
                SourceId = sourceId
            });

            await db.SaveChangesAsync();
            statusService.RecordReading(channelName);
            await weatherData.UpdateChannelRecordAsync(channelName, adjusted.Value, timestamp, sourceId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "MQTT source {SourceId}: failed to persist reading for '{Channel}'.",
                sourceId, channelName);
        }
    }

    private async Task ReconnectStaleAsync(CancellationToken ct)
    {
        for (var i = _clients.Count - 1; i >= 0; i--)
        {
            var client = _clients[i];
            if (!client.IsConnected)
            {
                logger.LogWarning("MQTT client {Index} disconnected; attempting reconnect.", i);
                try { await client.ReconnectAsync(ct); }
                catch (Exception ex) { logger.LogError(ex, "MQTT reconnect failed for client {Index}.", i); }
            }
        }
    }

    private static string? ExtractUnit(string? config, string channelName)
    {
        if (string.IsNullOrWhiteSpace(config)) return null;
        try
        {
            using var doc = JsonDocument.Parse(config);
            if (doc.RootElement.TryGetProperty("channelUnits", out var units) &&
                units.TryGetProperty(channelName, out var unit))
                return unit.GetString();
        }
        catch { }
        return null;
    }

    private sealed class MqttSourceConfig
    {
        public string? Broker { get; init; }
        public int Port { get; init; }
        public string? Topic { get; init; }
        public string? Username { get; init; }
        public string? Password { get; init; }
        public Dictionary<string, string>? ChannelMapping { get; init; }
        public Dictionary<string, string>? ChannelUnits { get; init; }
    }
}
