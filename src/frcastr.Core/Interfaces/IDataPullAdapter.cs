using frcastr.Core.Entities;

namespace frcastr.Core.Interfaces;

public interface IDataPullAdapter
{
    string Provider { get; }
    Task<IReadOnlyList<(string Channel, decimal Value, string Unit)>> FetchAsync(
        DataSource source, CancellationToken ct = default);
}
