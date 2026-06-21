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
    IDataSourceStatusService statusService) : ControllerBase
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
    public IActionResult Moon()
        => Ok(MoonPhaseCalculator.Calculate(DateTime.UtcNow));

    [HttpGet("sun")]
    public async Task<IActionResult> Sun(CancellationToken ct)
    {
        var latStr = await settings.GetAsync("Station.Latitude", ct);
        var lonStr = await settings.GetAsync("Station.Longitude", ct);
        var tzId = await settings.GetAsync("Station.TimeZone", ct);

        if (!double.TryParse(latStr, out var lat) || !double.TryParse(lonStr, out var lon))
            return Ok(null);

        var tz = tzId is not null ? TimeZoneInfo.FindSystemTimeZoneById(tzId) : TimeZoneInfo.Local;
        var result = SolarCalculator.Calculate(lat, lon, DateOnly.FromDateTime(DateTime.UtcNow), tz);
        return Ok(result);
    }

    [HttpGet("alerts")]
    public async Task<IActionResult> Alerts(CancellationToken ct)
    {
        // Serve from the latest AlertCache entry
        var cache = await GetLatestAlertCacheAsync(ct);
        if (cache is null) return Ok(Array.Empty<object>());
        return Content(cache, "application/json");
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
        var d = date is not null && DateTime.TryParse(date, out var parsed)
            ? parsed.Date
            : DateTime.Today;

        var (start, end) = period.ToLower() switch
        {
            "week" => (d.AddDays(-6), d.AddDays(1)),
            "month" => (new DateTime(d.Year, d.Month, 1), new DateTime(d.Year, d.Month, 1).AddMonths(1)),
            _ => (d, d.AddDays(1))
        };

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
        var d = date is not null && DateTime.TryParse(date, out var parsed)
            ? parsed.Date
            : DateTime.Today;

        var (start, end) = period.ToLower() switch
        {
            "week" => (d.AddDays(-6), d.AddDays(1)),
            "month" => (new DateTime(d.Year, d.Month, 1), new DateTime(d.Year, d.Month, 1).AddMonths(1)),
            _ => (d, d.AddDays(1))
        };

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
