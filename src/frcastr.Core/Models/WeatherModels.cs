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

/// <summary>
/// One calendar month of a channel: the mean of that month's daily highs, the mean of its daily
/// lows, and the mean of every reading in it. A month still being lived in reports the days it
/// has, so <see cref="Days"/> says how much the averages are standing on.
/// </summary>
public record MonthlyStat(int Month, decimal AvgHigh, decimal Avg, decimal AvgLow, int Days);

/// <summary>A single year's months. Only months carrying data are listed.</summary>
public record MonthlyYearStats(int Year, IReadOnlyList<MonthlyStat> Months);

/// <summary>
/// Every year is returned in one response rather than one fetch per year: the whole history of a
/// channel collapses to a few numbers per month, so the year selector switches instantly and the
/// all-time column is already there. <see cref="AllTime"/> pools each month across every year —
/// recomputed from the underlying daily figures, not averaged from the per-year averages, so a
/// month with three days in one year cannot outweigh a full month in another.
/// </summary>
public record MonthlyStatsResult(
    string ChannelKey,
    string Unit,
    IReadOnlyList<MonthlyYearStats> Years,
    IReadOnlyList<MonthlyStat> AllTime);

public record HistoryResult(
    IReadOnlyList<HistoryDataPoint> RawPoints,
    IReadOnlyList<AggregateDataPoint> AggregatePoints,
    // Device id -> identity, for the DeviceIds appearing on the points above. A lookup rather than
    // a device key stamped on every point: a day of raw readings runs to tens of thousands of rows,
    // and repeating the key on each one bloats the response for nothing. Callers compose the
    // channel key themselves, the same way ChannelKey.Format does.
    IReadOnlyDictionary<int, DeviceRef>? Devices = null);

/// <summary>
/// A channel's surviving high or low, and where it came from. Used to rebuild an all-time record
/// from the data still standing behind it — after a purge, or after a source is no longer allowed
/// to hold one.
/// </summary>
public record ChannelExtreme(decimal Value, DateTime At, int? SourceId);
