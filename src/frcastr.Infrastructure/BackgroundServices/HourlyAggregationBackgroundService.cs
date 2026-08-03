using frcastr.Core.Enums;
using frcastr.Core.Interfaces;
using frcastr.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace frcastr.Infrastructure.BackgroundServices;

public class HourlyAggregationBackgroundService(
    IServiceScopeFactory scopeFactory,
    ILogger<HourlyAggregationBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var now = DateTime.UtcNow;
            var next = now.Date.AddHours(now.Hour + 1).AddMinutes(2);

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
                logger.LogError(ex, "Hourly aggregation failed.");
            }
        }
    }

    private async Task RunAsync(ApplicationDbContext db, ISettingsService settings,
        CancellationToken ct)
    {
        var rawRetention = await settings.GetIntAsync("Weather.RawRetentionDays", 30, ct);

        // Snapped down to the top of the hour. The retention instant lands mid-hour, and rolling up
        // a partial bucket is unrecoverable: the bucket is written from the few minutes that fell
        // before the instant, its readings are deleted, and on the next run the rest of that hour
        // is grouped into the same bucket, skipped by the dedup below because a row already exists,
        // and then deleted too. Only whole hours are ever aggregated or pruned.
        var cutoff = HourStart(DateTime.UtcNow.AddDays(-rawRetention));

        if (!await db.WeatherReadings.AnyAsync(r => r.Timestamp < cutoff, ct))
            return;

        // Aggregate in SQL
        var groups = await db.WeatherReadings
            .Where(r => r.Timestamp < cutoff)
            // Unit is not part of the key. A source that spells the same unit two ways inside one
            // hour ("C" and "°C", say) would otherwise split the bucket in two, and one bucket is
            // exactly what the unique index on the aggregates table now enforces — the split would
            // fail the insert and wedge aggregation rather than produce a tidier row.
            .GroupBy(r => new
            {
                r.ChannelName, r.SourceId, r.DeviceId,
                Year = r.Timestamp.Year,
                Month = r.Timestamp.Month,
                Day = r.Timestamp.Day,
                Hour = r.Timestamp.Hour
            })
            .Select(g => new
            {
                g.Key,
                Avg = g.Average(r => r.Value),
                Min = g.Min(r => r.Value),
                Max = g.Max(r => r.Value),
                Count = g.Count(),
                Unit = g.Max(r => r.Unit)
            })
            .ToListAsync(ct);

        if (groups.Count == 0) return;

        var newAggregates = groups.Select(g => new frcastr.Core.Entities.WeatherReadingAggregate
        {
            ChannelName = g.Key.ChannelName,
            SourceId = g.Key.SourceId,
            DeviceId = g.Key.DeviceId,
            Granularity = AggregationGranularity.Hourly,
            PeriodStart = new DateTime(g.Key.Year, g.Key.Month, g.Key.Day, g.Key.Hour, 0, 0, DateTimeKind.Utc),
            Avg = g.Avg,
            Min = g.Min,
            Max = g.Max,
            Count = g.Count,
            // Non-null: Unit is a required column and a group always has at least one row.
            Unit = g.Unit!
        }).ToList();

        // Dedup: skip buckets that already have an aggregate row
        var minBucket = newAggregates.Min(a => a.PeriodStart);
        var maxBucket = newAggregates.Max(a => a.PeriodStart);

        var existing = (await db.WeatherReadingAggregates
            .Where(a => a.Granularity == AggregationGranularity.Hourly
                     && a.PeriodStart >= minBucket && a.PeriodStart <= maxBucket)
            .Select(a => new { a.ChannelName, a.SourceId, a.DeviceId, a.PeriodStart })
            .ToListAsync(ct))
            .Select(x => (x.ChannelName, x.SourceId, x.DeviceId, x.PeriodStart))
            .ToHashSet();

        var toInsert = newAggregates
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

            await db.WeatherReadings.Where(r => r.Timestamp < cutoff).ExecuteDeleteAsync(ct);
            await tx.CommitAsync(ct);

            logger.LogInformation(
                "Hourly aggregation: inserted {Count} aggregate rows, pruned raw readings before {Cutoff:u}.",
                toInsert.Count, cutoff);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    private static DateTime HourStart(DateTime utc)
        => new(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, DateTimeKind.Utc);
}
