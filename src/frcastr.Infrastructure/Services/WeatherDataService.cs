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
        // Latest reading per (channel, device) — grouping by channel alone would let one device
        // overwrite another that reports the same canonical channel.
        var latestIds = await dbContext.WeatherReadings
            .GroupBy(r => new { r.ChannelName, r.DeviceId })
            .Select(g => g.Max(r => r.Id))
            .ToListAsync(ct);

        var rows = await dbContext.WeatherReadings
            .Where(r => latestIds.Contains(r.Id))
            .ToListAsync(ct);

        var deviceIds = rows.Where(r => r.DeviceId is not null)
            .Select(r => r.DeviceId!.Value).Distinct().ToList();

        var devices = deviceIds.Count == 0
            ? []
            : await dbContext.Devices
                .Where(d => deviceIds.Contains(d.Id))
                .Select(d => new { d.Id, d.DeviceId, d.Name, d.IsPrimary })
                .ToDictionaryAsync(d => d.Id, ct);

        var result = new Dictionary<string, CurrentReading>();

        foreach (var r in rows)
        {
            if (r.DeviceId is null || !devices.TryGetValue(r.DeviceId.Value, out var device))
            {
                result[r.ChannelName] =
                    new CurrentReading(r.ChannelName, r.Value, r.Unit, r.Timestamp, r.SourceId);
                continue;
            }

            var reading = new CurrentReading(r.ChannelName, r.Value, r.Unit, r.Timestamp, r.SourceId,
                IsCalculated: false, DeviceId: device.Id, DeviceKey: device.DeviceId, DeviceName: device.Name);

            result[ChannelKey.Format(r.ChannelName, device.DeviceId)] = reading;

            // The primary device also answers to the bare canonical key so widgets bound to
            // "temperature.outdoor" keep working when the station's own sensor is a device.
            if (device.IsPrimary)
                result[r.ChannelName] = reading;
        }

        AddCalculatedChannels(result);

        foreach (var deviceKey in devices.Values.Select(d => d.DeviceId))
            AddCalculatedChannels(result, deviceKey);

        return result;
    }

    private static void AddCalculatedChannels(
        Dictionary<string, CurrentReading> channels, string? deviceKey = null)
    {
        var suffix = string.IsNullOrWhiteSpace(deviceKey) ? "" : ChannelKey.Separator + deviceKey;
        AddCalculatedChannelsCore(channels, suffix);
    }

    private static void AddCalculatedChannelsCore(
        Dictionary<string, CurrentReading> channels, string suffix)
    {
        channels.TryGetValue("temperature.outdoor" + suffix, out var tOut);
        channels.TryGetValue("humidity.outdoor" + suffix, out var hOut);
        channels.TryGetValue("temperature.indoor" + suffix, out var tIn);
        channels.TryGetValue("humidity.indoor" + suffix, out var hIn);
        channels.TryGetValue("wind.speed" + suffix, out var wind);

        // Derived channels inherit the device identity of the inputs they were computed from.
        void Put(string channel, decimal value, DateTime ts, CurrentReading from) =>
            channels[channel + suffix] = new CurrentReading(channel, value, "°C", ts, 0,
                IsCalculated: true, DeviceId: from.DeviceId, DeviceKey: from.DeviceKey, DeviceName: from.DeviceName);

        static DateTime Latest(params CurrentReading?[] readings) =>
            readings.Where(r => r is not null).Max(r => r!.Timestamp);

        if (tOut is not null)
        {
            if (hOut is not null)
            {
                var ts = Latest(tOut, hOut);
                var dp = DewPoint((double)tOut.Value, (double)hOut.Value);
                Put("dewpoint.outdoor", (decimal)dp, ts, tOut);

                // Heat index depends on temperature and humidity only — it does not need wind.
                if (tOut.Value >= 27 && hOut.Value >= 40)
                {
                    var hi = HeatIndex((double)tOut.Value, (double)hOut.Value);
                    Put("heatindex.outdoor", (decimal)hi, ts, tOut);
                }
            }

            // Wind chill is the one derived channel that genuinely requires wind.
            if (wind is not null && tOut.Value <= 10 && wind.Value >= 4.8m)
            {
                var wc = WindChill((double)tOut.Value, (double)wind.Value);
                Put("windchill.outdoor", (decimal)wc, Latest(tOut, wind), tOut);
            }

            // Feels-like is published whenever there is an outdoor temperature; humidity and wind
            // refine it when available. Requiring all three meant stations without a wind sensor
            // (an ESP32 reporting only temperature and humidity, say) never got the channel.
            var fl = FeelsLike((double)tOut.Value, (double?)hOut?.Value, (double?)wind?.Value);
            Put("feelslike.outdoor", (decimal)fl, Latest(tOut, hOut, wind), tOut);
        }

        if (tIn is not null && hIn is not null)
        {
            var ts = tIn.Timestamp > hIn.Timestamp ? tIn.Timestamp : hIn.Timestamp;
            var dp = DewPoint((double)tIn.Value, (double)hIn.Value);
            Put("dewpoint.indoor", (decimal)dp, ts, tIn);
        }
    }

    // ── Trend ─────────────────────────────────────────────────────────────────

    public async Task<TrendResult> GetTrendAsync(string channelKey, int samples = 10,
        CancellationToken ct = default)
    {
        var threshold = await settings.GetDecimalAsync("Trend.ThresholdDegrees", 0.5m, ct);

        var (channelName, deviceKey) = ChannelKey.Split(channelKey);
        var deviceId = await ResolveDeviceIdAsync(deviceKey, ct);

        var readings = await dbContext.WeatherReadings
            .Where(r => r.ChannelName == channelName
                     && (deviceId == null || r.DeviceId == deviceId))
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
        IEnumerable<string> channelKeys, DateTime start, DateTime end,
        CancellationToken ct = default)
    {
        // Keys may be device-scoped ("temperature.indoor@greenhouse-01"). Query on the canonical
        // channel names, then filter to the requested devices.
        var keyList = channelKeys.ToList();
        var allChannels = keyList.Count == 0;
        var split = keyList.Select(ChannelKey.Split).ToList();
        var channelList = split.Select(s => s.ChannelName).Distinct().ToList();

        var requestedDeviceKeys = split.Where(s => s.DeviceKey is not null)
            .Select(s => s.DeviceKey!).Distinct().ToList();

        // A device filter only applies when every requested key named a device; a mixed request
        // (some bare, some device-scoped) must not drop the bare channels' station-wide rows.
        var deviceScoped = !allChannels && split.All(s => s.DeviceKey is not null);

        List<int> deviceIds = [];
        if (requestedDeviceKeys.Count > 0)
        {
            deviceIds = await dbContext.Devices
                .Where(d => requestedDeviceKeys.Contains(d.DeviceId))
                .Select(d => d.Id)
                .ToListAsync(ct);

            // Named devices that do not exist: nothing can match.
            if (deviceScoped && deviceIds.Count == 0)
                return new HistoryResult([], []);
        }

        var filterDevices = deviceScoped && deviceIds.Count > 0;
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
                .Where(r => (allChannels || channelList.Contains(r.ChannelName))
                         && (!filterDevices || (r.DeviceId != null && deviceIds.Contains(r.DeviceId.Value)))
                         && r.Timestamp >= rawStart && r.Timestamp <= end)
                .OrderBy(r => r.Timestamp)
                .Select(r => new { r.Timestamp, r.ChannelName, r.Value, r.Unit, r.SourceId, r.DeviceId })
                .ToListAsync(ct);

            rawPoints.AddRange(rows.Select(r =>
                new HistoryDataPoint(DateTime.SpecifyKind(r.Timestamp, DateTimeKind.Utc), r.ChannelName, r.Value, r.Unit, r.SourceId, r.DeviceId)));
        }

        // Hourly tier: [max(start, hourlyCutoff), min(end, rawCutoff)]
        var hourlyStart = start > hourlyCutoff ? start : hourlyCutoff;
        var hourlyEnd = end < rawCutoff ? end : rawCutoff;
        if (hourlyStart < hourlyEnd)
        {
            var rows = await dbContext.WeatherReadingAggregates
                .Where(r => r.Granularity == AggregationGranularity.Hourly
                         && (allChannels || channelList.Contains(r.ChannelName))
                         && (!filterDevices || (r.DeviceId != null && deviceIds.Contains(r.DeviceId.Value)))
                         && r.PeriodStart >= hourlyStart && r.PeriodStart < hourlyEnd)
                .OrderBy(r => r.PeriodStart)
                .Select(r => new { r.PeriodStart, r.ChannelName, r.Avg, r.Min, r.Max, r.Count, r.Unit, r.SourceId, r.DeviceId })
                .ToListAsync(ct);

            aggPoints.AddRange(rows.Select(r =>
                new AggregateDataPoint(DateTime.SpecifyKind(r.PeriodStart, DateTimeKind.Utc), r.ChannelName, r.Avg, r.Min, r.Max, r.Count, r.Unit, r.SourceId, r.DeviceId)));
        }

        // Daily tier: [start, min(end, hourlyCutoff)]
        var dailyEnd = end < hourlyCutoff ? end : hourlyCutoff;
        if (start < dailyEnd)
        {
            var rows = await dbContext.WeatherReadingAggregates
                .Where(r => r.Granularity == AggregationGranularity.Daily
                         && (allChannels || channelList.Contains(r.ChannelName))
                         && (!filterDevices || (r.DeviceId != null && deviceIds.Contains(r.DeviceId.Value)))
                         && r.PeriodStart >= start && r.PeriodStart < dailyEnd)
                .OrderBy(r => r.PeriodStart)
                .Select(r => new { r.PeriodStart, r.ChannelName, r.Avg, r.Min, r.Max, r.Count, r.Unit, r.SourceId, r.DeviceId })
                .ToListAsync(ct);

            aggPoints.AddRange(rows.Select(r =>
                new AggregateDataPoint(DateTime.SpecifyKind(r.PeriodStart, DateTimeKind.Utc), r.ChannelName, r.Avg, r.Min, r.Max, r.Count, r.Unit, r.SourceId, r.DeviceId)));
        }

        return new HistoryResult(rawPoints, aggPoints);
    }

    // ── Channel records ───────────────────────────────────────────────────────

    public async Task<IReadOnlyList<WeatherChannelRecord>> GetChannelRecordsAsync(
        CancellationToken ct = default)
        => await dbContext.WeatherChannelRecords.OrderBy(r => r.ChannelName).ToListAsync(ct);

    public async Task UpdateChannelRecordAsync(string channelName, decimal value,
        DateTime timestamp, int sourceId, CancellationToken ct = default, int? deviceId = null)
    {
        // Records are scoped per device so one sensor's extreme never overwrites another's.
        var record = await dbContext.WeatherChannelRecords
            .FirstOrDefaultAsync(r => r.ChannelName == channelName && r.DeviceId == deviceId, ct);

        if (record is null)
        {
            dbContext.WeatherChannelRecords.Add(new WeatherChannelRecord
            {
                ChannelName = channelName,
                DeviceId = deviceId,
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

    /// <summary>Maps a device key from a channel key ("greenhouse-01") to its Devices.Id.</summary>
    private async Task<int?> ResolveDeviceIdAsync(string? deviceKey, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(deviceKey)) return null;
        return await dbContext.Devices
            .Where(d => d.DeviceId == deviceKey)
            .Select(d => (int?)d.Id)
            .FirstOrDefaultAsync(ct);
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

    /// <summary>NWS heat index (Rothfusz regression with the two published adjustments).</summary>
    /// <remarks>
    /// Deliberately unclamped. The regression climbs steeply at the top end — 40 °C at 90 % RH gives
    /// about 94 °C — but that combination implies a 38 °C dew point, which has never been recorded,
    /// so it means a failing humidity sensor rather than weather. Capping it would hide that while
    /// also truncating genuine extremes: 38 °C at 70 % RH is a real 61 °C heat index, and the world
    /// record is around 81 °C. Individual inputs are already range-checked at ingest by
    /// <see cref="frcastr.Infrastructure.Helpers.ChannelProcessing"/>.
    /// </remarks>
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

        // Official adjustments for the corners where the regression is weakest.
        if (rh < 13 && t is >= 80 and <= 112)
            hi -= (13 - rh) / 4.0 * Math.Sqrt((17 - Math.Abs(t - 95)) / 17.0);
        else if (rh > 85 && t is >= 80 and <= 87)
            hi += (rh - 85) / 10.0 * ((87 - t) / 5.0);

        return (hi - 32) * 5.0 / 9.0; // back to °C
    }

    /// <summary>
    /// Perceived temperature from air temperature, humidity and wind. Humidity and wind are
    /// optional: the calculation degrades to whatever inputs the station actually reports.
    /// </summary>
    /// <remarks>
    /// Three regimes. Wind chill and heat index are the accepted standards at the cold and hot
    /// extremes, so they are used inside their published domains. Everywhere else — which is most
    /// of the year — the Bureau of Meteorology apparent temperature applies, because it varies
    /// continuously with all three inputs. The previous implementation returned the raw air
    /// temperature outside the two extremes, so "feels like" simply mirrored the thermometer.
    /// </remarks>
    private static double FeelsLike(double tempC, double? relHumidity, double? windKmh)
    {
        var wind = windKmh ?? 0;

        // Wind chill needs no humidity, so it still applies on a station without a humidity sensor.
        if (tempC <= 10 && wind >= 4.8)
            return WindChill(tempC, wind);

        // Without humidity there is nothing left that can move the number.
        if (relHumidity is null)
            return tempC;

        if (tempC >= 27 && relHumidity >= 40)
            return HeatIndex(tempC, relHumidity.Value);

        return ApparentTemperature(tempC, relHumidity.Value, wind);
    }

    /// <summary>
    /// Australian Bureau of Meteorology apparent temperature, non-radiation form:
    /// AT = Ta + 0.33e − 0.70ws − 4.00, with vapour pressure e in hPa and wind in m/s.
    /// </summary>
    /// <remarks>
    /// The BOM also publishes a radiation form with a +0.70Q/(ws+10) term. It is deliberately not
    /// used here: Q is net radiation absorbed by the body, not the global irradiance a pyranometer
    /// reports on the <c>solar.radiation</c> channel. Substituting one for the other adds tens of
    /// degrees on a sunny day.
    /// </remarks>
    private static double ApparentTemperature(double tempC, double relHumidity, double windKmh)
    {
        var windMs = windKmh / 3.6;
        var vapourPressure = relHumidity / 100.0 * 6.105
            * Math.Exp(17.27 * tempC / (237.7 + tempC));
        return tempC + 0.33 * vapourPressure - 0.70 * windMs - 4.00;
    }
}
