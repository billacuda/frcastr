using System.Collections.Concurrent;
using frcastr.Core.Interfaces;

namespace frcastr.Infrastructure.Services;

public class DataSourceStatusService : IDataSourceStatusService
{
    // Keyed per (channel, device) so two devices reporting the same canonical channel are tracked
    // independently.
    private readonly ConcurrentDictionary<StaleChannel, DateTime> _lastReceived = new();

    public void RecordReading(string channelName, int? deviceId = null)
        => _lastReceived[new StaleChannel(channelName, deviceId)] = DateTime.UtcNow;

    public DateTime? GetLastReceived(string channelName, int? deviceId = null)
        => _lastReceived.TryGetValue(new StaleChannel(channelName, deviceId), out var ts) ? ts : null;

    public IReadOnlyList<StaleChannel> GetStaleChannels(int thresholdMinutes)
    {
        var cutoff = DateTime.UtcNow.AddMinutes(-thresholdMinutes);
        return _lastReceived
            .Where(kv => kv.Value < cutoff)
            .Select(kv => kv.Key)
            .ToList();
    }
}
