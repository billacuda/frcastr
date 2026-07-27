using System.Globalization;
using System.Text;
using frcastr.Core.Calculators;
using frcastr.Core.Interfaces;
using frcastr.Core.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OutputCaching;
using frcastr.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace frcastr.Web.Controllers;

[ApiController]
[Route("api/weather")]
[AllowAnonymous]
public class WeatherController(
    IWeatherDataService weatherData,
    IForecastService forecast,
    ISettingsService settings,
    IDataSourceStatusService statusService,
    ISunriseSunsetService sunriseSunset,
    ApplicationDbContext db) : ControllerBase
{
    [HttpGet("current")]
    [OutputCache(PolicyName = "WeatherCurrent")]
    public async Task<IActionResult> Current(CancellationToken ct)
    {
        var threshold = await settings.GetIntAsync("Alerts.SensorOfflineThresholdMinutes", 10, ct);
        var readings = await weatherData.GetCurrentReadingsAsync(ct);
        var stale = statusService.GetStaleChannels(threshold);

        // staleChannels stays a flat array of channel keys, matching how readings are keyed and
        // how widgets bind — the dashboard looks these up as plain strings.
        var deviceIds = stale.Where(s => s.DeviceId is not null)
            .Select(s => s.DeviceId!.Value).Distinct().ToList();

        var devices = deviceIds.Count == 0
            ? []
            : await db.Devices
                .Where(d => deviceIds.Contains(d.Id))
                .Select(d => new { d.Id, d.DeviceId, d.IsPrimary })
                .ToDictionaryAsync(d => d.Id, ct);

        var staleKeys = new List<string>();
        foreach (var s in stale)
        {
            if (s.DeviceId is null || !devices.TryGetValue(s.DeviceId.Value, out var device))
            {
                staleKeys.Add(s.ChannelName);
                continue;
            }

            staleKeys.Add(ChannelKey.Format(s.ChannelName, device.DeviceId));

            // The primary device's readings are also published under the bare canonical key, so a
            // widget still bound to "temperature.indoor" has to go stale with it — otherwise the
            // tile kept showing a last-known value with no warning that the sensor had stopped.
            if (device.IsPrimary && !staleKeys.Contains(s.ChannelName))
                staleKeys.Add(s.ChannelName);
        }

        return Ok(new { readings, staleChannels = staleKeys });
    }

    [HttpGet("trend/{channel}")]
    public async Task<IActionResult> Trend(string channel, int samples = 10, CancellationToken ct = default)
    {
        var sampleCount = await settings.GetIntAsync("Trend.SampleCount", samples, ct);
        var result = await weatherData.GetTrendAsync(channel, sampleCount, ct);
        return Ok(result);
    }

    [HttpGet("forecast")]
    public async Task<IActionResult> Forecast(CancellationToken ct)
    {
        var result = await forecast.GetForecastAsync(ct);
        return Ok(result);
    }

    [HttpGet("moon")]
    [OutputCache(PolicyName = "SunMoon")]
    public async Task<IActionResult> Moon(CancellationToken ct)
    {
        var utcNow = DateTime.UtcNow;
        // Phase, icon, and illumination come from the math calculator (always accurate).
        // Moonrise/moonset and phase name come from sunrisesunset.io when available.
        var phase = MoonPhaseCalculator.Calculate(utcNow);
        var ss    = await sunriseSunset.GetTodayAsync(ct);
        if (ss is not null)
        {
            return Ok(new MoonPhaseInfo(
                Phase:       phase.Phase,
                PhaseName:   ss.MoonPhase ?? phase.PhaseName,
                Illumination: ss.MoonIllumination ?? phase.Illumination,
                Icon:        phase.Icon,
                Moonrise:    ss.Moonrise,
                Moonset:     ss.Moonset));
        }

        // Fallback: math-based moonrise/moonset
        var latStr = await settings.GetAsync("Station.Latitude",  ct);
        var lonStr = await settings.GetAsync("Station.Longitude", ct);
        var tzId   = await settings.GetAsync("Station.TimeZone",  ct);
        if (!double.TryParse(latStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var lat) ||
            !double.TryParse(lonStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var lon))
            return Ok(phase);
        var tz = tzId is not null ? TimeZoneInfo.FindSystemTimeZoneById(tzId) : TimeZoneInfo.Local;
        return Ok(MoonPhaseCalculator.CalculateWithRiseSet(utcNow, lat, lon, tz));
    }

    [HttpGet("sun")]
    [OutputCache(PolicyName = "SunMoon")]
    public async Task<IActionResult> Sun(CancellationToken ct)
    {
        var ss = await sunriseSunset.GetTodayAsync(ct);
        if (ss is not null)
        {
            return Ok(new SolarInfo(
                Sunrise:                  ss.Sunrise,
                SolarNoon:                ss.SolarNoon ?? DateTimeOffset.MinValue,
                Sunset:                   ss.Sunset,
                GoldenHourMorningEnd:     ss.GoldenHourMorning,
                GoldenHourEveningStart:   ss.GoldenHourEvening,
                DayLength:                ss.DayLength ?? TimeSpan.Zero));
        }

        // Fallback: math-based solar calculator
        var latStr = await settings.GetAsync("Station.Latitude",  ct);
        var lonStr = await settings.GetAsync("Station.Longitude", ct);
        var tzId   = await settings.GetAsync("Station.TimeZone",  ct);
        if (!double.TryParse(latStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var lat) ||
            !double.TryParse(lonStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var lon))
            return Ok(null);
        var tz = tzId is not null ? TimeZoneInfo.FindSystemTimeZoneById(tzId) : TimeZoneInfo.Local;
        return Ok(SolarCalculator.Calculate(lat, lon, DateOnly.FromDateTime(DateTime.UtcNow), tz));
    }

    [HttpGet("alerts")]
    public async Task<IActionResult> Alerts(CancellationToken ct)
    {
        // Serve from the latest AlertCache entry
        var cache = await GetLatestAlertCacheAsync(ct);
        if (cache is null) return Ok(Array.Empty<object>());
        return Content(cache, "application/json");
    }

    [HttpGet("daily-extremes")]
    [OutputCache(PolicyName = "WeatherCurrent")]
    public async Task<IActionResult> DailyExtremes(CancellationToken ct)
    {
        var tzId = await settings.GetAsync("Station.TimeZone", ct);
        var tz = tzId is not null ? TimeZoneInfo.FindSystemTimeZoneById(tzId) : TimeZoneInfo.Local;
        var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz);
        var todayLocal = new DateTime(nowLocal.Year, nowLocal.Month, nowLocal.Day, 0, 0, 0, DateTimeKind.Unspecified);
        var startUtc = TimeZoneInfo.ConvertTimeToUtc(todayLocal, tz);
        var result = await weatherData.GetHistoryAsync([], startUtc, startUtc.AddDays(1), ct);

        // Device-scoped keys ("temperature.indoor@indoor-01") are emitted alongside the bare ones,
        // because a widget bound to one device's channel looked itself up by that key and found
        // nothing here — the reason a device-scoped tile showed a live value but no high/low.
        var devices = await db.Devices
            .Select(d => new { d.Id, d.DeviceId, d.IsPrimary })
            .ToListAsync(ct);

        var deviceKeys = devices.ToDictionary(d => d.Id, d => d.DeviceId);
        var primaryId = devices.FirstOrDefault(d => d.IsPrimary)?.Id;

        var extremes = new Dictionary<string, object>();

        // Raw wins over aggregate for the same key, matching the previous precedence.
        void Put(string key, double min, double max)
        {
            if (!extremes.ContainsKey(key)) extremes[key] = new { min, max };
        }

        // A bare channel name answers for the station: sourceless readings plus the primary
        // device's, which is exactly the set GetCurrentReadingsAsync publishes under that key.
        // Pooling every device into it gave one tile its live value from one sensor and its
        // high/low from another — two indoor sensors, and the warmer one set the day's high.
        bool IsStationWide(int? deviceId) => deviceId is null || deviceId == primaryId;

        foreach (var g in result.RawPoints
                     .Where(p => IsStationWide(p.DeviceId))
                     .GroupBy(p => p.ChannelName))
            Put(g.Key, (double)g.Min(p => p.Value), (double)g.Max(p => p.Value));

        foreach (var g in result.RawPoints
                     .Where(p => p.DeviceId is not null)
                     .GroupBy(p => new { p.ChannelName, DeviceId = p.DeviceId!.Value }))
        {
            if (deviceKeys.TryGetValue(g.Key.DeviceId, out var deviceKey))
                Put(ChannelKey.Format(g.Key.ChannelName, deviceKey),
                    (double)g.Min(p => p.Value), (double)g.Max(p => p.Value));
        }

        foreach (var g in result.AggregatePoints
                     .Where(p => IsStationWide(p.DeviceId))
                     .GroupBy(p => p.ChannelName))
            Put(g.Key, (double)g.Min(p => p.Min), (double)g.Max(p => p.Max));

        foreach (var g in result.AggregatePoints
                     .Where(p => p.DeviceId is not null)
                     .GroupBy(p => new { p.ChannelName, DeviceId = p.DeviceId!.Value }))
        {
            if (deviceKeys.TryGetValue(g.Key.DeviceId, out var deviceKey))
                Put(ChannelKey.Format(g.Key.ChannelName, deviceKey),
                    (double)g.Min(p => p.Min), (double)g.Max(p => p.Max));
        }

        return Ok(extremes);
    }

    [HttpGet("records")]
    public async Task<IActionResult> Records(CancellationToken ct)
    {
        var records = await weatherData.GetChannelRecordsAsync(ct);

        // Records are stored per device, so two sensors on one canonical channel produce two rows.
        // Without the device they render as duplicates that differ only in their numbers.
        var deviceIds = records.Where(r => r.DeviceId is not null)
            .Select(r => r.DeviceId!.Value).Distinct().ToList();

        var devices = deviceIds.Count == 0
            ? []
            : await db.Devices
                .Where(d => deviceIds.Contains(d.Id))
                .Select(d => new { d.Id, d.DeviceId, d.Name })
                .ToDictionaryAsync(d => d.Id, ct);

        var result = records.Select(r =>
        {
            var device = r.DeviceId is not null && devices.TryGetValue(r.DeviceId.Value, out var d) ? d : null;
            return new
            {
                r.ChannelName,
                ChannelKey = ChannelKey.Format(r.ChannelName, device?.DeviceId),
                r.DeviceId,
                DeviceKey = device?.DeviceId,
                DeviceName = device?.Name,
                r.AllTimeMax, r.AllTimeMaxAt, r.AllTimeMaxSourceId,
                r.AllTimeMin, r.AllTimeMinAt, r.AllTimeMinSourceId
            };
        });

        return Ok(result);
    }

    /// <summary>
    /// Display labels for channel keys, e.g. { "temperature.indoor": "mqtt" }. Anonymous because
    /// the History page is: channel names are load-bearing (bounds, calculated channels and unit
    /// conversion all key off them), so a label is layered on for display instead of renaming.
    /// </summary>
    [HttpGet("channel-labels")]
    [OutputCache(PolicyName = "SunMoon")]
    public async Task<IActionResult> ChannelLabels(CancellationToken ct)
    {
        var labels = await db.Settings
            .Where(s => s.Key.StartsWith(ChannelLabelPrefix))
            .Select(s => new { s.Key, s.Value })
            .ToListAsync(ct);

        return Ok(labels.ToDictionary(l => l.Key[ChannelLabelPrefix.Length..], l => l.Value));
    }

    /// <summary>Settings key prefix for channel display labels; see AdminController for writes.</summary>
    public const string ChannelLabelPrefix = "Channel.Label.";

    [HttpGet("history")]
    public async Task<IActionResult> History(
        string period = "day",
        string? date = null,
        string? channels = null,
        CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var d = date is not null && DateTime.TryParse(date, out var parsed)
            ? DateTime.SpecifyKind(parsed.Date, DateTimeKind.Utc)
            : now.Date;

        var (start, end) = period.ToLower() switch
        {
            "week"  => (d.AddDays(-6), d.AddDays(1)),
            "month" => (new DateTime(d.Year, d.Month, 1, 0, 0, 0, DateTimeKind.Utc),
                        new DateTime(d.Year, d.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(1)),
            "year"  => (new DateTime(d.Year, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                        new DateTime(d.Year + 1, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
            _       => (d, d.AddDays(1))
        };

        // Never query past the current moment — ensures today's most-recent readings are included
        if (end > now) end = now;

        var channelList = channels?.Split(',', StringSplitOptions.TrimEntries) ?? [];
        var result = await weatherData.GetHistoryAsync(channelList, start, end, ct);
        return Ok(result);
    }

    [HttpGet("export")]
    public async Task<IActionResult> Export(
        string period = "day",
        string? date = null,
        string format = "csv",
        string? channels = null,
        CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var d = date is not null && DateTime.TryParse(date, out var parsed)
            ? DateTime.SpecifyKind(parsed.Date, DateTimeKind.Utc)
            : now.Date;

        var (start, end) = period.ToLower() switch
        {
            "week"  => (d.AddDays(-6), d.AddDays(1)),
            "month" => (new DateTime(d.Year, d.Month, 1, 0, 0, 0, DateTimeKind.Utc),
                        new DateTime(d.Year, d.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(1)),
            "year"  => (new DateTime(d.Year, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                        new DateTime(d.Year + 1, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
            _       => (d, d.AddDays(1))
        };

        if (end > now) end = now;

        var channelList = channels?.Split(',', StringSplitOptions.TrimEntries) ?? [];
        var result = await weatherData.GetHistoryAsync(channelList, start, end, ct);

        // Without a device column two sensors on the same canonical channel are indistinguishable
        // in the file. The channel keeps its canonical name — an export is read by machines, so a
        // display label here would make two exports of the same data disagree.
        string Device(int? deviceId) =>
            deviceId is not null && result.Devices is not null &&
            result.Devices.TryGetValue(deviceId.Value, out var d) ? d.DeviceKey : "";

        var sb = new StringBuilder("Timestamp,Channel,Device,Value,Unit\r\n");
        foreach (var p in result.RawPoints)
            sb.AppendLine(FormattableString.Invariant(
                $"{p.Timestamp:u},{p.ChannelName},{Device(p.DeviceId)},{p.Value},{p.Unit}"));
        foreach (var p in result.AggregatePoints)
            sb.AppendLine(FormattableString.Invariant(
                $"{p.PeriodStart:u},{p.ChannelName},{Device(p.DeviceId)},{p.Avg},{p.Unit}"));

        var filename = $"frcastr-{period}-{d:yyyy-MM-dd}.csv";
        return File(Encoding.UTF8.GetBytes(sb.ToString()), "text/csv", filename);
    }

    private async Task<string?> GetLatestAlertCacheAsync(CancellationToken ct)
    {
        // Injecting ApplicationDbContext directly here would create a circular dep; use a scoped service.
        // AlertsJson is served from in-memory via IAlertCacheReader (stub for now — see WeatherController full wiring)
        return await Task.FromResult<string?>(null);
    }
}
