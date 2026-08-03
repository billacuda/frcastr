namespace frcastr.Core.Interfaces;

/// <summary>Identifies a channel that has stopped reporting. DeviceId is null for station-wide sources.</summary>
public readonly record struct StaleChannel(string ChannelName, int? DeviceId);

public interface IDataSourceStatusService
{
    void RecordReading(string channelName, int? deviceId = null);
    IReadOnlyList<StaleChannel> GetStaleChannels(int thresholdMinutes);

    /// <summary>
    /// Per-device override for how long a channel may go quiet before it counts as stale. Devices
    /// that report on a duty cycle — an ESP32 waking every five minutes — are silent by design for
    /// longer than the station-wide threshold allows.
    /// </summary>
    IReadOnlyList<StaleChannel> GetStaleChannels(
        int defaultThresholdMinutes, IReadOnlyDictionary<int, int> deviceThresholdMinutes);

    DateTime? GetLastReceived(string channelName, int? deviceId = null);

    /// <summary>
    /// Primes the tracker from stored readings at startup. Without this the tracker begins empty
    /// after every restart, so a sensor that died before the restart is not stale until it reports
    /// — which it never will. Existing entries win; live readings are always more current.
    /// </summary>
    void Seed(IEnumerable<KeyValuePair<StaleChannel, DateTime>> lastReceived);
}
