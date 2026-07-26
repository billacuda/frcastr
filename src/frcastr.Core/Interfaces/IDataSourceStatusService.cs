namespace frcastr.Core.Interfaces;

/// <summary>Identifies a channel that has stopped reporting. DeviceId is null for station-wide sources.</summary>
public readonly record struct StaleChannel(string ChannelName, int? DeviceId);

public interface IDataSourceStatusService
{
    void RecordReading(string channelName, int? deviceId = null);
    IReadOnlyList<StaleChannel> GetStaleChannels(int thresholdMinutes);
    DateTime? GetLastReceived(string channelName, int? deviceId = null);
}
