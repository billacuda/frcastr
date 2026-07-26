using System.Collections.Concurrent;
using frcastr.Core.Interfaces;
using frcastr.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace frcastr.Infrastructure.BackgroundServices;

public class SensorOfflineBackgroundService(
    IServiceScopeFactory scopeFactory,
    IDataSourceStatusService statusService,
    ILogger<SensorOfflineBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(2);

    // Tracks channels for which an offline email has already been sent
    private readonly ConcurrentDictionary<StaleChannel, DateTime> _notifiedOffline = new();

    // Same, for whole-device offline notifications (keyed by Devices.Id)
    private readonly ConcurrentDictionary<int, DateTime> _notifiedDeviceOffline = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TickInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try { await CheckAsync(stoppingToken); }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Sensor offline check failed.");
            }
        }
    }

    private async Task CheckAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
        var email = scope.ServiceProvider.GetRequiredService<IEmailService>();

        var threshold = await settings.GetIntAsync("Alerts.SensorOfflineThresholdMinutes", 10, ct);
        var stationName = await settings.GetAsync("Station.Name", ct) ?? "Weather Station";
        var cooldown = await settings.GetIntAsync("Alerts.WebhookCooldownMinutes", 30, ct);

        var stale = statusService.GetStaleChannels(threshold);
        var emailReady = await email.IsConfiguredAsync(ct);

        // Resolve device names for stale device-scoped channels so the alert names the device.
        var staleDeviceIds = stale.Where(s => s.DeviceId is not null)
            .Select(s => s.DeviceId!.Value).Distinct().ToList();

        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var deviceNames = staleDeviceIds.Count == 0
            ? []
            : await db.Devices
                .Where(d => staleDeviceIds.Contains(d.Id))
                .ToDictionaryAsync(d => d.Id, d => d.Name, ct);

        foreach (var channel in stale)
        {
            // Only notify if we haven't already or cooldown has passed
            if (_notifiedOffline.TryGetValue(channel, out var notifiedAt) &&
                notifiedAt > DateTime.UtcNow.AddMinutes(-cooldown))
                continue;

            var label = channel.DeviceId is not null && deviceNames.TryGetValue(channel.DeviceId.Value, out var dn)
                ? $"{channel.ChannelName} ({dn})"
                : channel.ChannelName;

            logger.LogWarning("Sensor offline: '{Channel}' has not reported for {Threshold}+ minutes.", label, threshold);

            if (emailReady)
            {
                var subject = $"⚠ Sensor Offline: {label} — {stationName}";
                var lastSeen = statusService.GetLastReceived(channel.ChannelName, channel.DeviceId);
                var body = $"<p>Sensor <strong>{label}</strong> has not reported data for " +
                           $"over {threshold} minutes.</p>" +
                           $"<p>Last received: {lastSeen?.ToString("u") ?? "never"}</p>";
                try
                {
                    await email.SendToRecipientsAsync(subject, body, ct);
                    _notifiedOffline[channel] = DateTime.UtcNow;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to send offline alert email for '{Channel}'.", label);
                }
            }
            else
            {
                _notifiedOffline[channel] = DateTime.UtcNow;
            }
        }

        // Clear offline state for channels that have come back online
        foreach (var channel in _notifiedOffline.Keys.ToList())
        {
            if (!stale.Contains(channel))
                _notifiedOffline.TryRemove(channel, out _);
        }

        await CheckDevicesAsync(db, email, emailReady, threshold, stationName, cooldown, ct);
    }

    /// <summary>
    /// Whole-device offline check. Unlike the channel check above this reads Device.LastSeenAt from
    /// the database, so it still fires after an app restart (the in-memory channel tracker starts
    /// empty and would stay silent until the device reported at least once).
    /// </summary>
    private async Task CheckDevicesAsync(ApplicationDbContext db, IEmailService email,
        bool emailReady, int globalThreshold, string stationName, int cooldown, CancellationToken ct)
    {
        var devices = await db.Devices.Where(d => d.IsEnabled).ToListAsync(ct);
        var now = DateTime.UtcNow;

        foreach (var device in devices)
        {
            var deviceThreshold = device.OfflineThresholdMinutes > 0
                ? device.OfflineThresholdMinutes
                : globalThreshold;

            // Never-seen devices are not "offline" — they have simply not reported yet.
            if (device.LastSeenAt is null) continue;

            var isStale = device.LastSeenAt < now.AddMinutes(-deviceThreshold);
            if (!isStale && device.IsOnline)
            {
                _notifiedDeviceOffline.TryRemove(device.Id, out _);
                continue;
            }

            if (_notifiedDeviceOffline.TryGetValue(device.Id, out var notifiedAt) &&
                notifiedAt > now.AddMinutes(-cooldown))
                continue;

            var reason = device.IsOnline
                ? $"has not reported for over {deviceThreshold} minutes"
                : "reported offline via its MQTT last will";

            logger.LogWarning("Device offline: '{Device}' ({DeviceId}) {Reason}.",
                device.Name, device.DeviceId, reason);

            if (emailReady)
            {
                var subject = $"⚠ Device Offline: {device.Name} — {stationName}";
                var body = $"<p>Device <strong>{device.Name}</strong> (<code>{device.DeviceId}</code>) {reason}.</p>" +
                           $"<p>Last seen: {device.LastSeenAt?.ToString("u") ?? "never"}</p>" +
                           (string.IsNullOrWhiteSpace(device.Location) ? "" : $"<p>Location: {device.Location}</p>");
                try
                {
                    await email.SendToRecipientsAsync(subject, body, ct);
                    _notifiedDeviceOffline[device.Id] = now;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to send offline alert email for device '{Device}'.", device.Name);
                }
            }
            else
            {
                _notifiedDeviceOffline[device.Id] = now;
            }
        }
    }
}
