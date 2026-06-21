using System.Text;
using System.Text.Json;
using frcastr.Core.Enums;
using frcastr.Core.Interfaces;
using frcastr.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace frcastr.Infrastructure.BackgroundServices;

public class WebhookAlertBackgroundService(
    IServiceScopeFactory scopeFactory,
    IHttpClientFactory httpClientFactory,
    ILogger<WebhookAlertBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(1);
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TickInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try { await RunAsync(stoppingToken); }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Webhook alert tick failed.");
            }
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
        var email = scope.ServiceProvider.GetRequiredService<IEmailService>();

        var cooldownMinutes = await settings.GetIntAsync("Alerts.WebhookCooldownMinutes", 30, ct);
        var emailOnTrigger = await settings.GetBoolAsync("Alerts.EmailOnWebhookTrigger", false, ct);
        var now = DateTime.UtcNow;

        var alerts = await db.WebhookAlerts.Where(a => a.IsEnabled).ToListAsync(ct);
        if (alerts.Count == 0) return;

        // Latest reading per channel
        var channels = alerts.Select(a => a.Channel).Distinct().ToList();
        var latestIds = await db.WeatherReadings
            .Where(r => channels.Contains(r.ChannelName))
            .GroupBy(r => r.ChannelName)
            .Select(g => g.Max(r => r.Id))
            .ToListAsync(ct);

        var latestReadings = await db.WeatherReadings
            .Where(r => latestIds.Contains(r.Id))
            .ToDictionaryAsync(r => r.ChannelName, r => r, ct);

        foreach (var alert in alerts)
        {
            if (!latestReadings.TryGetValue(alert.Channel, out var reading))
                continue;

            var triggered = alert.Operator switch
            {
                AlertOperator.Above => reading.Value > alert.Threshold,
                AlertOperator.Below => reading.Value < alert.Threshold,
                _ => false
            };

            if (!triggered) continue;

            alert.LastTriggeredAt = now;

            var cooldownElapsed = alert.LastNotifiedAt is null ||
                                  alert.LastNotifiedAt < now.AddMinutes(-cooldownMinutes);
            if (!cooldownElapsed)
            {
                await db.SaveChangesAsync(ct);
                continue;
            }

            // Fire webhook
            var payload = JsonSerializer.Serialize(new
            {
                channel = alert.Channel,
                value = reading.Value,
                unit = reading.Unit,
                threshold = alert.Threshold,
                @operator = alert.Operator.ToString(),
                triggeredAt = now
            }, JsonOpts);

            try
            {
                var http = httpClientFactory.CreateClient("webhook");
                using var content = new StringContent(payload, Encoding.UTF8, "application/json");
                var resp = await http.PostAsync(alert.WebhookUrl, content, ct);
                if (!resp.IsSuccessStatusCode)
                    logger.LogWarning("Webhook {AlertId} returned {Status}.", alert.Id, resp.StatusCode);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Webhook POST failed for alert {AlertId}.", alert.Id);
            }

            alert.LastNotifiedAt = now;
            await db.SaveChangesAsync(ct);

            if (emailOnTrigger && await email.IsConfiguredAsync(ct))
            {
                var subject = $"Threshold Alert: {alert.Name}";
                var body = $"<p><strong>{alert.Channel}</strong> is {alert.Operator} {alert.Threshold} {alert.Unit}. " +
                           $"Current value: {reading.Value} {reading.Unit}</p>";
                await email.SendToRecipientsAsync(subject, body, ct);
            }
        }
    }
}
