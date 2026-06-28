using System.Text.Json;
using frcastr.Core.Entities;
using frcastr.Core.Enums;
using frcastr.Core.Interfaces;
using frcastr.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace frcastr.Infrastructure.BackgroundServices;

public class ForecastRefreshBackgroundService(
    IServiceScopeFactory scopeFactory,
    ILogger<ForecastRefreshBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan TickInterval      = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan DailyRefreshAfter  = TimeSpan.FromHours(3);
    private static readonly TimeSpan HourlyRefreshAfter = TimeSpan.FromMinutes(30);
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await RunAsync(stoppingToken); }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Forecast refresh: initial run failed; will retry on next tick.");
        }

        using var timer = new PeriodicTimer(TickInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try { await RunAsync(stoppingToken); }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Forecast refresh tick failed.");
            }
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var adapters = scope.ServiceProvider.GetServices<IForecastAdapter>().ToList();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();

        var now = DateTime.UtcNow;
        var cacheRetentionHours = await settings.GetIntAsync("Forecast.CacheRetentionHours", 48, ct);

        var sources = await db.DataSources
            .Where(s => s.IsEnabled && s.Type == DataSourceType.Forecast)
            .ToListAsync(ct);

        foreach (var source in sources)
        {
            var latestCache = await db.ForecastCaches
                .Where(fc => fc.SourceId == source.Id && !fc.IsHourly)
                .OrderByDescending(fc => fc.FetchedAt)
                .FirstOrDefaultAsync(ct);

            var latestHourly = await db.ForecastCaches
                .Where(fc => fc.SourceId == source.Id && fc.IsHourly)
                .OrderByDescending(fc => fc.FetchedAt)
                .FirstOrDefaultAsync(ct);

            // Refresh based on age of last successful fetch, not on ValidUntil.
            // ValidUntil stays long (48 h) as a display-staleness guard; it must not
            // be used to decide when to re-fetch or hourly data gets stuck for 48 h.
            var dailyNeedsRefresh  = latestCache   is null || (now - latestCache.FetchedAt)   >= DailyRefreshAfter;
            var hourlyNeedsRefresh = latestHourly  is null || (now - latestHourly.FetchedAt)  >= HourlyRefreshAfter;

            if (!dailyNeedsRefresh && !hourlyNeedsRefresh) continue;

            var provider = ExtractProvider(source.Config);
            if (provider is null && source.Url?.Contains("api.weather.gov", StringComparison.OrdinalIgnoreCase) == true)
                provider = "nws";
            else if (provider is null && source.Url?.Contains("api.open-meteo.com", StringComparison.OrdinalIgnoreCase) == true)
                provider = "openmeteo";
            else if (provider is null && !string.IsNullOrWhiteSpace(source.Url))
                provider = "generic-json";
            var adapter = provider is not null
                ? adapters.FirstOrDefault(a => a.Provider.Equals(provider, StringComparison.OrdinalIgnoreCase))
                : adapters.FirstOrDefault();

            if (adapter is null)
            {
                logger.LogWarning("No forecast adapter for source {SourceId} ({Name}).", source.Id, source.Name);
                continue;
            }

            try
            {
                // Fetch daily forecast
                if (dailyNeedsRefresh)
                {
                    var periods = await adapter.FetchAsync(source, ct);
                    if (periods.Count > 0)
                    {
                        var json = JsonSerializer.Serialize(periods, JsonOpts);
                        db.ForecastCaches.Add(new ForecastCache
                        {
                            SourceId     = source.Id,
                            FetchedAt    = now,
                            ForecastJson = json,
                            ValidUntil   = now.AddHours(cacheRetentionHours),
                            IsHourly     = false
                        });
                        logger.LogDebug("Refreshed daily forecast for source {SourceId} ({Count} periods).",
                            source.Id, periods.Count);
                    }
                    else
                    {
                        logger.LogWarning("Daily forecast fetch returned 0 periods for source {SourceId}; skipping cache.",
                            source.Id);
                    }
                }

                // Fetch hourly forecast if the adapter supports it
                if (hourlyNeedsRefresh && adapter is IHourlyForecastAdapter hourlyAdapter)
                {
                    var hourly = await hourlyAdapter.FetchHourlyAsync(source, ct);
                    if (hourly.Count > 0)
                    {
                        var hourlyJson = JsonSerializer.Serialize(hourly, JsonOpts);
                        db.ForecastCaches.Add(new ForecastCache
                        {
                            SourceId     = source.Id,
                            FetchedAt    = now,
                            ForecastJson = hourlyJson,
                            ValidUntil   = now.AddHours(cacheRetentionHours),
                            IsHourly     = true
                        });
                        logger.LogDebug("Refreshed hourly forecast for source {SourceId} ({Count} periods).",
                            source.Id, hourly.Count);
                    }
                    else
                    {
                        logger.LogWarning("Hourly forecast fetch returned 0 periods for source {SourceId}; skipping cache.",
                            source.Id);
                    }
                }

                source.LastPolledAt = now;
                source.LastError = null;
                await db.SaveChangesAsync(ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Forecast fetch failed for source {SourceId} ({Name}).", source.Id, source.Name);
                source.LastError = ex.Message;
                source.LastPolledAt = now;
                try { await db.SaveChangesAsync(ct); } catch { }
            }
        }
    }

    private static string? ExtractProvider(string? config)
    {
        if (string.IsNullOrWhiteSpace(config)) return null;
        try
        {
            using var doc = JsonDocument.Parse(config);
            return doc.RootElement.TryGetProperty("provider", out var p) ? p.GetString() : null;
        }
        catch { return null; }
    }
}
