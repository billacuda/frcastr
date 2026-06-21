using frcastr.Core.Entities;
using frcastr.Core.Models;

namespace frcastr.Core.Interfaces;

public interface IWeatherDataService
{
    Task<IReadOnlyDictionary<string, CurrentReading>> GetCurrentReadingsAsync(CancellationToken ct = default);
    Task<TrendResult> GetTrendAsync(string channelName, int samples = 10, CancellationToken ct = default);
    Task<HistoryResult> GetHistoryAsync(IEnumerable<string> channels, DateTime start, DateTime end, CancellationToken ct = default);
    Task<IReadOnlyList<WeatherChannelRecord>> GetChannelRecordsAsync(CancellationToken ct = default);
    Task UpdateChannelRecordAsync(string channelName, decimal value, DateTime timestamp, int sourceId, CancellationToken ct = default);
}
