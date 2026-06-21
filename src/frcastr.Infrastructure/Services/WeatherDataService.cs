using frcastr.Core.Entities;
using frcastr.Core.Enums;
using frcastr.Core.Interfaces;
using frcastr.Core.Models;
using frcastr.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace frcastr.Infrastructure.Services;

public class WeatherDataService(
    ApplicationDbContext dbContext,
    ISettingsService settings) : IWeatherDataService
{
    // ── Current readings ──────────────────────────────────────────────────────

    public async Task<IReadOnlyDictionary<string, CurrentReading>> GetCurrentReadingsAsync(
        CancellationToken ct = default)
    {
        var latestIds = await dbContext.WeatherReadings
            .GroupBy(r => r.ChannelName)
            .Select(g => g.Max(r => r.Id))
            .ToListAsync(ct);

        var rows = await dbContext.WeatherReadings
            .Where(r => latestIds.Contains(r.Id))
            .ToListAsync(ct);

        var result = rows.ToDictionary(
            r => r.ChannelName,
            r => new CurrentReading(r.ChannelName, r.Value, r.Unit, r.Timestamp, r.SourceId));

        AddCalculatedChannels(result);
        return result;
    }

    private static void AddCalculatedChannels(Dictionary<string, CurrentReading> channels)
    {
        channels.TryGetValue("temperature.outdoor", out var tOut);
        channels.TryGetValue("humidity.outdoor", out var hOut);
        channels.TryGetValue("temperature.indoor", out var tIn);
        channels.TryGetValue("humidity.indoor", out var hIn);
        channels.TryGetValue("wind.speed", out var wind);

        if (tOut is not null && hOut is not null)
        {
            var ts = tOut.Timestamp > hOut.Timestamp ? tOut.Timestamp : hOut.Timestamp;
            var dp = DewPoint((double)tOut.Value, (double)hOut.Value);
            channels["dewpoint.outdoor"] = new CurrentReading("dewpoint.outdoor", (decimal)dp, "°C", ts, 0, true);

            if (wind is not null)
            {
                var feelsTs = ts > wind.Timestamp ? ts : wind.Timestamp;
                var fl = FeelsLike((double)tOut.Value, (double)hOut.Value, (double)wind.Value);
                channels["feelslike.outdoor"] = new CurrentReading("feelslike.outdoor", (decimal)fl, "°C", feelsTs, 0, true);

                if ((double)tOut.Value <= 10 && (double)wind.Value >= 4.8)
                {
                    var wc = WindChill((double)tOut.Value, (double)wind.Value);
                    channels["windchill.outdoor"] = new CurrentReading("windchill.outdoor", (decimal)wc, "°C", feelsTs, 0, true);
                }

                if ((double)tOut.Value >= 27 && (double)hOut.Value >= 40)
                {
                    var hi = HeatIndex((double)tOut.Value, (double)hOut.Value);
                    channels["heatindex.outdoor"] = new CurrentReading("heatindex.outdoor", (decimal)hi, "°C", ts, 0, true);
                }
            }
        }

        if (tIn is not null && hIn is not null)
        {
            var ts = tIn.Timestamp > hIn.Timestamp ? tIn.Timestamp : hIn.Timestamp;
            var dp = DewPoint((double)tIn.Value, (double)hIn.Value);
            channels["dewpoint.indoor"] = new CurrentReading("dewpoint.indoor", (decimal)dp, "°C", ts, 0, true);
        }
    }

    // ── Trend ─────────────────────────────────────────────────────────────────

    public async Task<TrendResult> GetTrendAsync(string channelName, int samples = 10,
        CancellationToken ct = default)
    {
        var threshold = await settings.GetDecimalAsync("Trend.ThresholdDegrees", 0.5m, ct);

        var readings = await dbContext.WeatherReadings
            .Where(r => r.ChannelName == channelName)
            .OrderByDescending(r => r.Id)
            .Take(samples)
            .Select(r => r.Value)
            .ToListAsync(ct);

        if (readings.Count < 2)
            return new TrendResult(TrendDirection.Steady, 0m);

        var delta = readings[0] - readings[^1]; // newest − oldest in the window

        var direction = delta > threshold ? TrendDirection.Rising
            : delta < -threshold ? TrendDirection.Falling
            : TrendDirection.Steady;

        return new TrendResult(direction, delta);
    }

    // ── History (tiered routing) ──────────────────────────────────────────────

    public async Task<HistoryResult> GetHistoryAsync(
        IEnumerable<string> channels, DateTime start, DateTime end,
        CancellationToken ct = default)
    {
        var channelList = channels.ToList();
        var rawRetention = await settings.GetIntAsync("Weather.RawRetentionDays", 30, ct);
        var hourlyRetention = await settings.GetIntAsync("Weather.HourlyRetentionDays", 365, ct);

        var now = DateTime.UtcNow;
        var rawCutoff = now.AddDays(-rawRetention);
        var hourlyCutoff = now.AddDays(-hourlyRetention);

        var rawPoints = new List<HistoryDataPoint>();
        var aggPoints = new List<AggregateDataPoint>();

        // Raw tier: [max(start, rawCutoff), end]
        var rawStart = start > rawCutoff ? start : rawCutoff;
        if (rawStart < end)
        {
            var rows = await dbContext.WeatherReadings
                .Where(r => channelList.Contains(r.ChannelName)
                         && r.Timestamp >= rawStart && r.Timestamp <= end)
                .OrderBy(r => r.Timestamp)
                .Select(r => new { r.Timestamp, r.ChannelName, r.Value, r.Unit, r.SourceId })
                .ToListAsync(ct);

            rawPoints.AddRange(rows.Select(r =>
                new HistoryDataPoint(r.Timestamp, r.ChannelName, r.Value, r.Unit, r.SourceId)));
        }

        // Hourly tier: [max(start, hourlyCutoff), min(end, rawCutoff)]
        var hourlyStart = start > hourlyCutoff ? start : hourlyCutoff;
        var hourlyEnd = end < rawCutoff ? end : rawCutoff;
        if (hourlyStart < hourlyEnd)
        {
            var rows = await dbContext.WeatherReadingAggregates
                .Where(r => r.Granularity == AggregationGranularity.Hourly
                         && channelList.Contains(r.ChannelName)
                         && r.PeriodStart >= hourlyStart && r.PeriodStart < hourlyEnd)
                .OrderBy(r => r.PeriodStart)
                .Select(r => new { r.PeriodStart, r.ChannelName, r.Avg, r.Min, r.Max, r.Count, r.Unit, r.SourceId })
                .ToListAsync(ct);

            aggPoints.AddRange(rows.Select(r =>
                new AggregateDataPoint(r.PeriodStart, r.ChannelName, r.Avg, r.Min, r.Max, r.Count, r.Unit, r.SourceId)));
        }

        // Daily tier: [start, min(end, hourlyCutoff)]
        var dailyEnd = end < hourlyCutoff ? end : hourlyCutoff;
        if (start < dailyEnd)
        {
            var rows = await dbContext.WeatherReadingAggregates
                .Where(r => r.Granularity == AggregationGranularity.Daily
                         && channelList.Contains(r.ChannelName)
                         && r.PeriodStart >= start && r.PeriodStart < dailyEnd)
                .OrderBy(r => r.PeriodStart)
                .Select(r => new { r.PeriodStart, r.ChannelName, r.Avg, r.Min, r.Max, r.Count, r.Unit, r.SourceId })
                .ToListAsync(ct);

            aggPoints.AddRange(rows.Select(r =>
                new AggregateDataPoint(r.PeriodStart, r.ChannelName, r.Avg, r.Min, r.Max, r.Count, r.Unit, r.SourceId)));
        }

        return new HistoryResult(rawPoints, aggPoints);
    }

    // ── Channel records ───────────────────────────────────────────────────────

    public async Task<IReadOnlyList<WeatherChannelRecord>> GetChannelRecordsAsync(
        CancellationToken ct = default)
        => await dbContext.WeatherChannelRecords.OrderBy(r => r.ChannelName).ToListAsync(ct);

    public async Task UpdateChannelRecordAsync(string channelName, decimal value,
        DateTime timestamp, int sourceId, CancellationToken ct = default)
    {
        var record = await dbContext.WeatherChannelRecords
            .FirstOrDefaultAsync(r => r.ChannelName == channelName, ct);

        if (record is null)
        {
            dbContext.WeatherChannelRecords.Add(new WeatherChannelRecord
            {
                ChannelName = channelName,
                AllTimeMax = value,
                AllTimeMaxAt = timestamp,
                AllTimeMaxSourceId = sourceId,
                AllTimeMin = value,
                AllTimeMinAt = timestamp,
                AllTimeMinSourceId = sourceId
            });
        }
        else
        {
            if (value > record.AllTimeMax)
            {
                record.AllTimeMax = value;
                record.AllTimeMaxAt = timestamp;
                record.AllTimeMaxSourceId = sourceId;
            }
            if (value < record.AllTimeMin)
            {
                record.AllTimeMin = value;
                record.AllTimeMinAt = timestamp;
                record.AllTimeMinSourceId = sourceId;
            }
        }

        await dbContext.SaveChangesAsync(ct);
    }

    // ── Thermodynamic formulas (all units: °C, %, km/h) ──────────────────────

    private static double DewPoint(double tempC, double relHumidity)
    {
        const double a = 17.625, b = 243.04;
        var gamma = a * tempC / (b + tempC) + Math.Log(relHumidity / 100.0);
        return b * gamma / (a - gamma);
    }

    private static double WindChill(double tempC, double windKmh)
        => 13.12 + 0.6215 * tempC
         - 11.37 * Math.Pow(windKmh, 0.16)
         + 0.3965 * tempC * Math.Pow(windKmh, 0.16);

    private static double HeatIndex(double tempC, double relHumidity)
    {
        var t = tempC * 9.0 / 5.0 + 32; // °F
        var rh = relHumidity;
        var hi = -42.379
            + 2.04901523 * t
            + 10.14333127 * rh
            - 0.22475541 * t * rh
            - 0.00683783 * t * t
            - 0.05481717 * rh * rh
            + 0.00122874 * t * t * rh
            + 0.00085282 * t * rh * rh
            - 0.00000199 * t * t * rh * rh;
        return (hi - 32) * 5.0 / 9.0; // back to °C
    }

    private static double FeelsLike(double tempC, double relHumidity, double windKmh)
    {
        if (tempC <= 10 && windKmh >= 4.8)
            return WindChill(tempC, windKmh);
        if (tempC >= 27 && relHumidity >= 40)
            return HeatIndex(tempC, relHumidity);
        return tempC;
    }
}
