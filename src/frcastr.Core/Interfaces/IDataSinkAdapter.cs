using frcastr.Core.Entities;

namespace frcastr.Core.Interfaces;

public interface IDataSinkAdapter
{
    string Provider { get; }
    Task UploadAsync(DataSource source,
        IReadOnlyList<(string Channel, decimal Value, string Unit, DateTime Timestamp)> readings,
        CancellationToken ct = default);
}
