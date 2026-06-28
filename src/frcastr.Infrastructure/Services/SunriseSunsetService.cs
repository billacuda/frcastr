using System.Globalization;
using CoordinateSharp;
using frcastr.Core.Calculators;
using frcastr.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace frcastr.Infrastructure.Services;

public class SunriseSunsetService(
    ISettingsService settings,
    ILogger<SunriseSunsetService> logger) : ISunriseSunsetService
{
    private (DateOnly Date, SunriseSunsetResult Data)? _cache;

    public async Task<SunriseSunsetResult?> GetTodayAsync(CancellationToken ct = default)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        if (_cache?.Date == today) return _cache.Value.Data;

        var latStr = await settings.GetAsync("Station.Latitude",  ct);
        var lonStr = await settings.GetAsync("Station.Longitude", ct);
        var tzId   = await settings.GetAsync("Station.TimeZone",  ct) ?? "UTC";

        if (!double.TryParse(latStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var lat) ||
            !double.TryParse(lonStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var lon))
            return null;

        try
        {
            var tz = TimeZoneInfo.FindSystemTimeZoneById(tzId);
            var utcDate = new DateTime(today.Year, today.Month, today.Day, 12, 0, 0, DateTimeKind.Utc);

            var coord = new Coordinate(lat, lon, utcDate);
            var cel   = coord.CelestialInfo;

            DateTimeOffset? ToDto(DateTime? utcDt) =>
                utcDt.HasValue
                    ? TimeZoneInfo.ConvertTime(
                        new DateTimeOffset(DateTime.SpecifyKind(utcDt.Value, DateTimeKind.Utc)), tz)
                    : null;

            var sunrise = ToDto(cel.SunRise);
            var sunset  = ToDto(cel.SunSet);

            // CoordinateSharp does not expose golden-hour times, so derive them from the
            // NOAA solar math (sun 6° above the horizon) for this station/date.
            var solar = SolarCalculator.Calculate(lat, lon, today, tz);

            var data = new SunriseSunsetResult(
                Sunrise:           sunrise,
                Sunset:            sunset,
                SolarNoon:         ToDto(cel.SolarNoon),
                Dawn:              ToDto(cel.AdditionalSolarTimes.CivilDawn),
                Dusk:              ToDto(cel.AdditionalSolarTimes.CivilDusk),
                GoldenHourMorning: solar.GoldenHourMorningEnd,
                GoldenHourEvening: solar.GoldenHourEveningStart,
                DayLength:         sunrise.HasValue && sunset.HasValue
                                       ? sunset.Value - sunrise.Value
                                       : null,
                Moonrise:          ToDto(cel.MoonRise),
                Moonset:           ToDto(cel.MoonSet),
                MoonIllumination:  cel.MoonIllum.Fraction,
                MoonPhase:         cel.MoonIllum.PhaseName);

            _cache = (today, data);
            return data;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "CoordinateSharp celestial calculation failed.");
            return null;
        }
    }
}
