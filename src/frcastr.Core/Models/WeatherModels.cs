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

/// <summary>Identifies a device referenced by history points, for composing channel keys.</summary>
public record DeviceRef(string DeviceKey, string DeviceName);

public record HistoryResult(
    IReadOnlyList<HistoryDataPoint> RawPoints,
    IReadOnlyList<AggregateDataPoint> AggregatePoints,
    // Device id -> identity, for the DeviceIds appearing on the points above. A lookup rather than
    // a device key stamped on every point: a day of raw readings runs to tens of thousands of rows,
    // and repeating the key on each one bloats the response for nothing. Callers compose the
    // channel key themselves, the same way ChannelKey.Format does.
    IReadOnlyDictionary<int, DeviceRef>? Devices = null);
