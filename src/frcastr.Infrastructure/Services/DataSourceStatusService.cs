using System.Collections.Concurrent;
using frcastr.Core.Interfaces;

namespace frcastr.Infrastructure.Services;

public class DataSourceStatusService : IDataSourceStatusService
{
    private readonly ConcurrentDictionary<string, DateTime> _lastReceived = new();

    public void RecordReading(string channelName)
        => _lastReceived[channelName] = DateTime.UtcNow;

    public DateTime? GetLastReceived(string channelName)
        => _lastReceived.TryGetValue(channelName, out var ts) ? ts : null;

    public IReadOnlyList<string> GetStaleChannels(int thresholdMinutes)
    {
        var cutoff = DateTime.UtcNow.AddMinutes(-thresholdMinutes);
        return _lastReceived
            .Where(kv => kv.Value < cutoff)
            .Select(kv => kv.Key)
            .ToList();
    }
}
