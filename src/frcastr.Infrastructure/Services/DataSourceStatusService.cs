using System.Collections.Concurrent;
using System.Collections.ObjectModel;
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
        => GetStaleChannels(thresholdMinutes, ReadOnlyDictionary<int, int>.Empty);

    public IReadOnlyList<StaleChannel> GetStaleChannels(
        int defaultThresholdMinutes, IReadOnlyDictionary<int, int> deviceThresholdMinutes)
    {
        var now = DateTime.UtcNow;
        return _lastReceived
            .Where(kv =>
            {
                var threshold = kv.Key.DeviceId is int id &&
                                deviceThresholdMinutes.TryGetValue(id, out var perDevice) && perDevice > 0
                    ? perDevice
                    : defaultThresholdMinutes;
                return kv.Value < now.AddMinutes(-threshold);
            })
            .Select(kv => kv.Key)
            .ToList();
    }

    public void Seed(IEnumerable<KeyValuePair<StaleChannel, DateTime>> lastReceived)
    {
        foreach (var (channel, timestamp) in lastReceived)
            _lastReceived.TryAdd(channel, timestamp);
    }
}
