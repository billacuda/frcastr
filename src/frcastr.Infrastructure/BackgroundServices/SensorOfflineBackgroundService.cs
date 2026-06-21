using System.Collections.Concurrent;
using frcastr.Core.Interfaces;
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
    private readonly ConcurrentDictionary<string, DateTime> _notifiedOffline = new();

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

        foreach (var channel in stale)
        {
            // Only notify if we haven't already or cooldown has passed
            if (_notifiedOffline.TryGetValue(channel, out var notifiedAt) &&
                notifiedAt > DateTime.UtcNow.AddMinutes(-cooldown))
                continue;

            logger.LogWarning("Sensor offline: '{Channel}' has not reported for {Threshold}+ minutes.", channel, threshold);

            if (await email.IsConfiguredAsync(ct))
            {
                var subject = $"⚠ Sensor Offline: {channel} — {stationName}";
                var body = $"<p>Sensor <strong>{channel}</strong> has not reported data for " +
                           $"over {threshold} minutes.</p>" +
                           $"<p>Last received: {statusService.GetLastReceived(channel)?.ToString("u") ?? "never"}</p>";
                try
                {
                    await email.SendToRecipientsAsync(subject, body, ct);
                    _notifiedOffline[channel] = DateTime.UtcNow;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to send offline alert email for '{Channel}'.", channel);
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
    }
}
