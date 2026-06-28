using System.Globalization;
using System.Text;
using frcastr.Core.Calculators;
using frcastr.Core.Interfaces;
using frcastr.Core.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OutputCaching;

namespace frcastr.Web.Controllers;

[ApiController]
[Route("api/weather")]
[AllowAnonymous]
public class WeatherController(
    IWeatherDataService weatherData,
    IForecastService forecast,
    ISettingsService settings,
    IDataSourceStatusService statusService,
    ISunriseSunsetService sunriseSunset) : ControllerBase
{
    [HttpGet("current")]
    [OutputCache(PolicyName = "WeatherCurrent")]
    public async Task<IActionResult> Current(CancellationToken ct)
    {
        var threshold = await settings.GetIntAsync("Alerts.SensorOfflineThresholdMinutes", 10, ct);
        var readings = await weatherData.GetCurrentReadingsAsync(ct);
        var stale = statusService.GetStaleChannels(threshold);
        return Ok(new { readings, staleChannels = stale });
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

        var extremes = result.RawPoints
            .GroupBy(p => p.ChannelName)
            .ToDictionary(
                g => g.Key,
                g => new { min = (double)g.Min(p => p.Value), max = (double)g.Max(p => p.Value) });

        foreach (var grp in result.AggregatePoints.GroupBy(p => p.ChannelName))
        {
            if (!extremes.ContainsKey(grp.Key))
                extremes[grp.Key] = new { min = (double)grp.Min(p => p.Min), max = (double)grp.Max(p => p.Max) };
        }

        return Ok(extremes);
    }

    [HttpGet("records")]
    public async Task<IActionResult> Records(CancellationToken ct)
    {
        var records = await weatherData.GetChannelRecordsAsync(ct);
        return Ok(records);
    }

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

        var sb = new StringBuilder("Timestamp,Channel,Value,Unit\r\n");
        foreach (var p in result.RawPoints)
            sb.AppendLine(FormattableString.Invariant(
                $"{p.Timestamp:u},{p.ChannelName},{p.Value},{p.Unit}"));
        foreach (var p in result.AggregatePoints)
            sb.AppendLine(FormattableString.Invariant(
                $"{p.PeriodStart:u},{p.ChannelName},{p.Avg},{p.Unit}"));

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
