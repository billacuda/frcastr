using frcastr.Core.Entities;
using frcastr.Core.Models;

namespace frcastr.Core.Interfaces;

public interface IForecastAdapter
{
    string Provider { get; }
    Task<IReadOnlyList<ForecastPeriod>> FetchAsync(DataSource source, CancellationToken ct = default);
}
