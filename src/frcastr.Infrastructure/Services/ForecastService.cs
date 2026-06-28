using System.Text.Json;
using frcastr.Core.Enums;
using frcastr.Core.Interfaces;
using frcastr.Core.Models;
using frcastr.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace frcastr.Infrastructure.Services;

public class ForecastService(ApplicationDbContext dbContext) : IForecastService
{
    private static readonly JsonSerializerOptions JsonOpts =
        new(JsonSerializerDefaults.Web);

    public async Task<ForecastResult> GetForecastAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;

        var caches = await dbContext.ForecastCaches
            .Include(fc => fc.Source)
            .Where(fc => fc.ValidUntil > now && fc.Source.IsEnabled
                      && fc.Source.Type == DataSourceType.Forecast)
            .OrderByDescending(fc => fc.FetchedAt)
            .ToListAsync(ct);

        // Daily: keep most recent non-hourly cache per source
        var latestDaily = caches
            .Where(fc => !fc.IsHourly)
            .GroupBy(fc => fc.SourceId)
            .Select(g => g.First())
            .ToList();

        var perSource = new List<SourceForecast>();
        foreach (var cache in latestDaily)
        {
            var periods = DeserializePeriods(cache.ForecastJson);
            if (periods.Count > 0)
                perSource.Add(new SourceForecast(cache.SourceId, cache.Source.Name, periods));
        }

        var aggregated = Aggregate(perSource.Select(s => s.Periods).ToList());

        // Hourly: keep most recent hourly cache per source, pass through without 6-hour bucketing
        var latestHourly = caches
            .Where(fc => fc.IsHourly)
            .GroupBy(fc => fc.SourceId)
            .Select(g => g.First())
            .ToList();

        var hourlyPeriods = new List<IReadOnlyList<ForecastPeriod>>();
        foreach (var cache in latestHourly)
        {
            var periods = DeserializePeriods(cache.ForecastJson);
            if (periods.Count > 0) hourlyPeriods.Add(periods);
        }
        var aggregatedHourly = hourlyPeriods.Count == 1
            ? hourlyPeriods[0]
            : AggregateHourly(hourlyPeriods);

        return new ForecastResult(perSource, aggregated, aggregatedHourly);
    }

    private static IReadOnlyList<ForecastPeriod> DeserializePeriods(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<ForecastPeriod>>(json, JsonOpts) ?? [];
        }
        catch
        {
            return [];
        }
    }

    // For hourly data: aggregate across sources per 1-hour bucket.
    private static IReadOnlyList<ForecastPeriod> AggregateHourly(
        IReadOnlyList<IReadOnlyList<ForecastPeriod>> allSourcePeriods)
    {
        if (allSourcePeriods.Count == 0) return [];
        if (allSourcePeriods.Count == 1) return allSourcePeriods[0];

        var all = allSourcePeriods.SelectMany(p => p).ToList();
        var buckets = all.GroupBy(p => new DateTime(
            p.PeriodStart.Year, p.PeriodStart.Month, p.PeriodStart.Day,
            p.PeriodStart.Hour, 0, 0, DateTimeKind.Utc));

        var result = new List<ForecastPeriod>();
        foreach (var bucket in buckets.OrderBy(b => b.Key))
        {
            var periods = bucket.ToList();
            result.Add(new ForecastPeriod(
                PeriodStart: bucket.Key,
                PeriodEnd: bucket.Key.AddHours(1),
                IsDaytime: periods.First().IsDaytime,
                Temperature: RejectedAverage(periods.Select(p => p.Temperature)),
                Condition: Mode(periods.Select(p => p.Condition)),
                PrecipChance: RejectedAverage(periods.Select(p => p.PrecipChance)),
                WindSpeed: RejectedAverage(periods.Select(p => p.WindSpeed)),
                WindDirection: Mode(periods.Select(p => p.WindDirection))));
        }
        return result;
    }

    // Outlier-rejected average aggregation across sources for each time bucket.
    private static IReadOnlyList<ForecastPeriod> Aggregate(
        IReadOnlyList<IReadOnlyList<ForecastPeriod>> allSourcePeriods)
    {
        if (allSourcePeriods.Count == 0) return [];
        if (allSourcePeriods.Count == 1) return allSourcePeriods[0];

        var all = allSourcePeriods.SelectMany(p => p).ToList();

        // Group by 6-hour bucket × isDaytime
        var buckets = all.GroupBy(p => (
            Bucket: new DateTime(
                p.PeriodStart.Year, p.PeriodStart.Month, p.PeriodStart.Day,
                (p.PeriodStart.Hour / 6) * 6, 0, 0, DateTimeKind.Utc),
            p.IsDaytime));

        var result = new List<ForecastPeriod>();
        foreach (var bucket in buckets.OrderBy(b => b.Key.Bucket))
        {
            var periods = bucket.ToList();
            result.Add(new ForecastPeriod(
                PeriodStart: bucket.Key.Bucket,
                PeriodEnd: bucket.Key.Bucket.AddHours(6),
                IsDaytime: bucket.Key.IsDaytime,
                Temperature: RejectedAverage(periods.Select(p => p.Temperature)),
                Condition: Mode(periods.Select(p => p.Condition)),
                PrecipChance: RejectedAverage(periods.Select(p => p.PrecipChance)),
                WindSpeed: RejectedAverage(periods.Select(p => p.WindSpeed)),
                WindDirection: Mode(periods.Select(p => p.WindDirection))));
        }

        return result;
    }

    private static double? RejectedAverage(IEnumerable<double?> values)
    {
        var list = values.Where(v => v.HasValue).Select(v => v!.Value).ToList();
        if (list.Count == 0) return null;
        if (list.Count < 3) return list.Average();

        var mean = list.Average();
        var stddev = Math.Sqrt(list.Average(v => (v - mean) * (v - mean)));
        var filtered = stddev > 0
            ? list.Where(v => Math.Abs(v - mean) <= 1.5 * stddev).ToList()
            : list;

        return filtered.Count > 0 ? filtered.Average() : list.Average();
    }

    private static string? Mode(IEnumerable<string?> values)
        => values
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .GroupBy(v => v)
            .OrderByDescending(g => g.Count())
            .FirstOrDefault()?.Key;
}
