namespace frcastr.Core.Interfaces;

public interface ISunriseSunsetService
{
    Task<SunriseSunsetResult?> GetTodayAsync(CancellationToken ct = default);
}

public record SunriseSunsetResult(
    DateTimeOffset? Sunrise,
    DateTimeOffset? Sunset,
    DateTimeOffset? SolarNoon,
    DateTimeOffset? Dawn,
    DateTimeOffset? Dusk,
    DateTimeOffset? GoldenHourMorning,
    DateTimeOffset? GoldenHourEvening,
    TimeSpan?       DayLength,
    DateTimeOffset? Moonrise,
    DateTimeOffset? Moonset,
    double?         MoonIllumination,
    string?         MoonPhase);
