namespace frcastr.Core.Interfaces;

public interface IDataSourceStatusService
{
    void RecordReading(string channelName);
    IReadOnlyList<string> GetStaleChannels(int thresholdMinutes);
    DateTime? GetLastReceived(string channelName);
}
