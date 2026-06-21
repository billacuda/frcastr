using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using frcastr.Core.Entities;
using frcastr.Core.Interfaces;
using frcastr.Core.Models;
using frcastr.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace frcastr.Infrastructure.BackgroundServices;

public class AlertsRefreshBackgroundService(
    IServiceScopeFactory scopeFactory,
    IHttpClientFactory httpClientFactory,
    ILogger<AlertsRefreshBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(5);
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await RunAsync(stoppingToken); }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Alerts refresh: initial run failed; will retry on next tick.");
        }

        using var timer = new PeriodicTimer(TickInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try { await RunAsync(stoppingToken); }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Alerts refresh tick failed.");
            }
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
        var email = scope.ServiceProvider.GetRequiredService<IEmailService>();

        var cacheRetentionHours = await settings.GetIntAsync("Alerts.CacheRetentionHours", 24, ct);
        var lat = await settings.GetAsync("Station.Latitude", ct);
        var lon = await settings.GetAsync("Station.Longitude", ct);

        if (string.IsNullOrWhiteSpace(lat) || string.IsNullOrWhiteSpace(lon)) return;

        var now = DateTime.UtcNow;
        var alerts = await FetchNwsAlertsAsync(lat, lon, ct);
        var stationName = await settings.GetAsync("Station.Name", ct) ?? "your station";

        // Check for new Extreme/Severe alerts
        var existing = await db.AlertCaches
            .Where(ac => ac.ValidUntil > now)
            .OrderByDescending(ac => ac.FetchedAt)
            .Take(1)
            .Select(ac => ac.AlertsJson)
            .FirstOrDefaultAsync(ct);

        var existingAlerts = DeserializeAlerts(existing);
        var newSevere = alerts.Where(a =>
            (a.Severity == "Extreme" || a.Severity == "Severe") &&
            !existingAlerts.Any(e => e.Event == a.Event && e.Onset == a.Onset))
            .ToList();

        // Store new cache entry
        db.AlertCaches.Add(new AlertCache
        {
            SourceId = 0,   // 0 = NWS system source
            FetchedAt = now,
            AlertsJson = JsonSerializer.Serialize(alerts, JsonOpts),
            ValidUntil = now.AddHours(cacheRetentionHours)
        });
        await db.SaveChangesAsync(ct);

        // Send email for new severe alerts
        if (newSevere.Count > 0 && await email.IsConfiguredAsync(ct))
        {
            var subject = $"⚠ Weather Alert for {stationName}";
            var body = string.Join("<br><br>", newSevere.Select(a =>
                $"<strong>{a.Severity}: {a.Event}</strong><br>{a.Headline}<br>{a.Description}"));
            await email.SendToRecipientsAsync(subject, body, ct);
        }
    }

    private async Task<IReadOnlyList<WeatherAlert>> FetchNwsAlertsAsync(
        string lat, string lon, CancellationToken ct)
    {
        try
        {
            var http = httpClientFactory.CreateClient("nws");
            var response = await http.GetFromJsonAsync<NwsAlertsResponse>(
                $"/alerts/active?point={lat},{lon}", JsonOpts, ct);

            return response?.Features?.Select(f => new WeatherAlert(
                Event: f.Properties?.Event ?? "Unknown",
                Severity: f.Properties?.Severity,
                Headline: f.Properties?.Headline,
                Description: f.Properties?.Description,
                Onset: f.Properties?.Onset ?? DateTime.UtcNow,
                Expires: f.Properties?.Expires))
                .ToList() ?? [];
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "NWS alerts fetch failed.");
            return [];
        }
    }

    private static IReadOnlyList<WeatherAlert> DeserializeAlerts(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<WeatherAlert>>(json, JsonOpts) ?? []; }
        catch { return []; }
    }

    // ── NWS JSON model ────────────────────────────────────────────────────────

    private sealed class NwsAlertsResponse
    {
        [JsonPropertyName("features")] public List<NwsAlertFeature>? Features { get; init; }
    }

    private sealed class NwsAlertFeature
    {
        [JsonPropertyName("properties")] public NwsAlertProps? Properties { get; init; }
    }

    private sealed class NwsAlertProps
    {
        [JsonPropertyName("event")] public string? Event { get; init; }
        [JsonPropertyName("severity")] public string? Severity { get; init; }
        [JsonPropertyName("headline")] public string? Headline { get; init; }
        [JsonPropertyName("description")] public string? Description { get; init; }
        [JsonPropertyName("onset")] public DateTime? Onset { get; init; }
        [JsonPropertyName("expires")] public DateTime? Expires { get; init; }
    }
}
