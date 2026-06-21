namespace frcastr.Core.Models;

public record ForecastPeriod(
    DateTime PeriodStart,
    DateTime PeriodEnd,
    bool IsDaytime,
    double? Temperature,
    string? Condition,
    double? PrecipChance,
    double? WindSpeed,
    string? WindDirection);

public record SourceForecast(int SourceId, string SourceName, IReadOnlyList<ForecastPeriod> Periods);

public record ForecastResult(
    IReadOnlyList<SourceForecast> PerSource,
    IReadOnlyList<ForecastPeriod> Aggregated);
