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
        var latStr = await settings.GetAsync("Station.Latitude",  ct);
        var lonStr = await settings.GetAsync("Station.Longitude", ct);
        var tzId   = await settings.GetAsync("Station.TimeZone",  ct) ?? "UTC";

        if (!double.TryParse(latStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var lat) ||
            !double.TryParse(lonStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var lon))
            return null;

        TimeZoneInfo tz;
        try
        {
            tz = TimeZoneInfo.FindSystemTimeZoneById(tzId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unknown time zone id '{TzId}'.", tzId);
            return null;
        }

        // Anchor on the station's local calendar date, not UTC's. Using the UTC date
        // makes the anchor roll over to "tomorrow" while it's still evening at stations
        // west of UTC, which makes CoordinateSharp return tomorrow's sunrise alongside
        // today's sunset — breaking the day/night comparison for hours before sunset.
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz));
        if (_cache?.Date == today) return _cache.Value.Data;

        try
        {
            DateTimeOffset? ToDto(DateTime? utcDt) =>
                utcDt.HasValue
                    ? TimeZoneInfo.ConvertTime(
                        new DateTimeOffset(DateTime.SpecifyKind(utcDt.Value, DateTimeKind.Utc)), tz)
                    : null;

            // CoordinateSharp binds each rise/set/dawn/dusk event to the UTC calendar day
            // of the anchor date it's given, not the station's local calendar day. For a
            // station far enough from UTC, one local day's morning event falls in UTC day D
            // while its evening event falls in UTC day D+1 (or D-1, depending on which side
            // of UTC the station is on) — so anchoring on a single date can pair "today's"
            // sunrise with yesterday's or tomorrow's sunset. Scan the neighboring UTC days
            // and keep whichever result actually converts back to the station's local target
            // date.
            Coordinate CoordFor(DateOnly d) =>
                new(lat, lon, new DateTime(d.Year, d.Month, d.Day, 12, 0, 0, DateTimeKind.Utc));

            var yesterday = CoordFor(today.AddDays(-1));
            var todayCoord = CoordFor(today);
            var tomorrow = CoordFor(today.AddDays(1));
            var neighbors = new[] { yesterday, todayCoord, tomorrow };

            DateTimeOffset? PickForToday(Func<Celestial, DateTime?> selector)
            {
                foreach (var c in neighbors)
                {
                    var dto = ToDto(selector(c.CelestialInfo));
                    if (dto.HasValue && DateOnly.FromDateTime(dto.Value.DateTime) == today) return dto;
                }
                return null;
            }

            var sunrise = PickForToday(c => c.SunRise);
            var sunset  = PickForToday(c => c.SunSet);

            // CoordinateSharp does not expose golden-hour times, so derive them from the
            // NOAA solar math (sun 6° above the horizon) for this station/date.
            var solar = SolarCalculator.Calculate(lat, lon, today, tz);

            var data = new SunriseSunsetResult(
                Sunrise:           sunrise,
                Sunset:            sunset,
                SolarNoon:         PickForToday(c => c.SolarNoon),
                Dawn:              PickForToday(c => c.AdditionalSolarTimes.CivilDawn),
                Dusk:              PickForToday(c => c.AdditionalSolarTimes.CivilDusk),
                GoldenHourMorning: solar.GoldenHourMorningEnd,
                GoldenHourEvening: solar.GoldenHourEveningStart,
                DayLength:         sunrise.HasValue && sunset.HasValue
                                       ? sunset.Value - sunrise.Value
                                       : null,
                Moonrise:          PickForToday(c => c.MoonRise),
                Moonset:           PickForToday(c => c.MoonSet),
                MoonIllumination:  todayCoord.CelestialInfo.MoonIllum.Fraction,
                MoonPhase:         todayCoord.CelestialInfo.MoonIllum.PhaseName);

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
