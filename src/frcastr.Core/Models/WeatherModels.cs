using frcastr.Core.Enums;

namespace frcastr.Core.Models;

public record SolarInfo(
    DateTimeOffset? Sunrise,
    DateTimeOffset SolarNoon,
    DateTimeOffset? Sunset,
    DateTimeOffset? GoldenHourMorningEnd,
    DateTimeOffset? GoldenHourEveningStart,
    TimeSpan DayLength);

public record MoonPhaseInfo(double Phase, string PhaseName, double Illumination, string Icon,
    DateTimeOffset? Moonrise = null, DateTimeOffset? Moonset = null);

public record CurrentReading(
    string ChannelName,
    decimal Value,
    string Unit,
    DateTime Timestamp,
    int SourceId,
    bool IsCalculated = false,
    int? DeviceId = null,
    string? DeviceKey = null,
    string? DeviceName = null);

public record TrendResult(TrendDirection Direction, decimal Delta);

public record HistoryDataPoint(
    DateTime Timestamp,
    string ChannelName,
    decimal Value,
    string Unit,
    int? SourceId,
    int? DeviceId = null);

public record AggregateDataPoint(
    DateTime PeriodStart,
    string ChannelName,
    decimal Avg,
    decimal Min,
    decimal Max,
    int Count,
    string Unit,
    int? SourceId,
    int? DeviceId = null);

public record HistoryResult(
    IReadOnlyList<HistoryDataPoint> RawPoints,
    IReadOnlyList<AggregateDataPoint> AggregatePoints);
