using frcastr.Core.Entities;
using frcastr.Core.Models;

namespace frcastr.Core.Interfaces;

public interface IWeatherDataService
{
    /// <summary>
    /// Keyed by channel key: "temperature.outdoor" for station-wide readings,
    /// "temperature.outdoor@greenhouse-01" for a specific device. See <see cref="ChannelKey"/>.
    /// </summary>
    Task<IReadOnlyDictionary<string, CurrentReading>> GetCurrentReadingsAsync(CancellationToken ct = default);

    /// <summary><paramref name="channelKey"/> may carry an "@device-id" suffix to scope to one device.</summary>
    Task<TrendResult> GetTrendAsync(string channelKey, int samples = 10, CancellationToken ct = default);

    /// <summary>
    /// Each entry of <paramref name="channelKeys"/> may carry an "@device-id" suffix.
    /// <paramref name="maxPointsPerTier"/> caps how many rows each of the raw, hourly and daily
    /// tiers may return, bounding the memory an anonymous caller can ask the server to allocate.
    /// </summary>
    Task<HistoryResult> GetHistoryAsync(IEnumerable<string> channelKeys, DateTime start, DateTime end,
        CancellationToken ct = default, int? maxPointsPerTier = null);

    /// <summary>
    /// Per-month averages for one channel across its whole history: mean daily high, mean daily
    /// low and mean reading, per year and pooled all-time. <paramref name="channelKey"/> may carry
    /// an "@device-id" suffix to scope to one device.
    /// </summary>
    Task<MonthlyStatsResult> GetMonthlyStatsAsync(string channelKey, CancellationToken ct = default);

    Task<IReadOnlyList<WeatherChannelRecord>> GetChannelRecordsAsync(CancellationToken ct = default);
    Task UpdateChannelRecordAsync(string channelName, decimal value, DateTime timestamp, int sourceId, CancellationToken ct = default, int? deviceId = null);

    /// <summary>
    /// Every channel in one message, in a single round trip. The per-channel overload above saves
    /// once per call, which on an MQTT payload carrying temperature, humidity and battery meant
    /// three read-modify-write cycles for what is one arriving message.
    /// </summary>
    Task UpdateChannelRecordsAsync(IReadOnlyCollection<(string ChannelName, decimal Value)> readings,
        DateTime timestamp, int sourceId, int? deviceId = null, CancellationToken ct = default);
}
