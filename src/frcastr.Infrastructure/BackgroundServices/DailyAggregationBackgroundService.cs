using frcastr.Core.Enums;
using frcastr.Core.Interfaces;
using frcastr.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace frcastr.Infrastructure.BackgroundServices;

public class DailyAggregationBackgroundService(
    IServiceScopeFactory scopeFactory,
    ILogger<DailyAggregationBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan RunAt = new(1, 5, 0); // 01:05 UTC

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var now = DateTime.UtcNow;
            var todayRunTime = now.Date + RunAt;
            var next = now < todayRunTime ? todayRunTime : todayRunTime.AddDays(1);

            try { await Task.Delay(next - now, stoppingToken); }
            catch (OperationCanceledException) { break; }

            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
                await RunAsync(db, settings, stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Daily aggregation failed.");
            }
        }
    }

    private async Task RunAsync(ApplicationDbContext db, ISettingsService settings,
        CancellationToken ct)
    {
        var hourlyRetention = await settings.GetIntAsync("Weather.HourlyRetentionDays", 365, ct);
        var dailyRetentionDays = await settings.GetIntAsync("Weather.DailyRetentionDays", 0, ct);
        var recordRetentionDays = await settings.GetIntAsync("Records.RetentionDays", 0, ct);

        var hourlyCutoff = DateTime.UtcNow.AddDays(-hourlyRetention);

        // ── Aggregate hourly → daily ──────────────────────────────────────────

        if (await db.WeatherReadingAggregates.AnyAsync(
                a => a.Granularity == AggregationGranularity.Hourly && a.PeriodStart < hourlyCutoff, ct))
        {
            var groups = await db.WeatherReadingAggregates
                .Where(a => a.Granularity == AggregationGranularity.Hourly && a.PeriodStart < hourlyCutoff)
                .GroupBy(a => new
                {
                    a.ChannelName, a.SourceId, a.DeviceId, a.Unit,
                    Year = a.PeriodStart.Year,
                    Month = a.PeriodStart.Month,
                    Day = a.PeriodStart.Day
                })
                .Select(g => new
                {
                    g.Key,
                    // Weighted average of hourly averages by count
                    WeightedSum = g.Sum(a => a.Avg * a.Count),
                    TotalCount = g.Sum(a => a.Count),
                    Min = g.Min(a => a.Min),
                    Max = g.Max(a => a.Max)
                })
                .ToListAsync(ct);

            var dailyRows = groups.Select(g => new frcastr.Core.Entities.WeatherReadingAggregate
            {
                ChannelName = g.Key.ChannelName,
                SourceId = g.Key.SourceId,
                DeviceId = g.Key.DeviceId,
                Granularity = AggregationGranularity.Daily,
                PeriodStart = new DateTime(g.Key.Year, g.Key.Month, g.Key.Day, 0, 0, 0, DateTimeKind.Utc),
                Avg = g.TotalCount > 0 ? g.WeightedSum / g.TotalCount : 0,
                Min = g.Min,
                Max = g.Max,
                Count = g.TotalCount,
                Unit = g.Key.Unit
            }).ToList();

            // Dedup
            var minBucket = dailyRows.Min(a => a.PeriodStart);
            var maxBucket = dailyRows.Max(a => a.PeriodStart);

            var existing = (await db.WeatherReadingAggregates
                .Where(a => a.Granularity == AggregationGranularity.Daily
                         && a.PeriodStart >= minBucket && a.PeriodStart <= maxBucket)
                .Select(a => new { a.ChannelName, a.SourceId, a.DeviceId, a.PeriodStart })
                .ToListAsync(ct))
                .Select(x => (x.ChannelName, x.SourceId, x.DeviceId, x.PeriodStart))
                .ToHashSet();

            var toInsert = dailyRows
                .Where(a => !existing.Contains((a.ChannelName, a.SourceId, a.DeviceId, a.PeriodStart)))
                .ToList();

            await using var tx = await db.Database.BeginTransactionAsync(ct);
            try
            {
                if (toInsert.Count > 0)
                {
                    await db.WeatherReadingAggregates.AddRangeAsync(toInsert, ct);
                    await db.SaveChangesAsync(ct);
                }

                await db.WeatherReadingAggregates
                    .Where(a => a.Granularity == AggregationGranularity.Hourly && a.PeriodStart < hourlyCutoff)
                    .ExecuteDeleteAsync(ct);

                await tx.CommitAsync(ct);
                logger.LogInformation(
                    "Daily aggregation: inserted {Count} daily rows, pruned hourly rows before {Cutoff:u}.",
                    toInsert.Count, hourlyCutoff);
            }
            catch
            {
                await tx.RollbackAsync(ct);
                throw;
            }
        }

        // ── Prune expired daily aggregates ────────────────────────────────────
        if (dailyRetentionDays > 0)
        {
            var dailyCutoff = DateTime.UtcNow.AddDays(-dailyRetentionDays);
            var deleted = await db.WeatherReadingAggregates
                .Where(a => a.Granularity == AggregationGranularity.Daily && a.PeriodStart < dailyCutoff)
                .ExecuteDeleteAsync(ct);
            if (deleted > 0)
                logger.LogInformation("Pruned {Count} expired daily aggregate rows.", deleted);
        }

        // ── Prune expired channel records ─────────────────────────────────────
        if (recordRetentionDays > 0)
        {
            var recordCutoff = DateTime.UtcNow.AddDays(-recordRetentionDays);
            var deleted = await db.WeatherChannelRecords
                .Where(r => r.AllTimeMaxAt < recordCutoff && r.AllTimeMinAt < recordCutoff)
                .ExecuteDeleteAsync(ct);
            if (deleted > 0)
                logger.LogInformation("Pruned {Count} expired channel record rows.", deleted);
        }

        // ── Prune expired forecast / alert cache ──────────────────────────────
        await db.ForecastCaches.Where(fc => fc.ValidUntil < DateTime.UtcNow).ExecuteDeleteAsync(ct);
        await db.AlertCaches.Where(ac => ac.ValidUntil < DateTime.UtcNow).ExecuteDeleteAsync(ct);
    }
}
