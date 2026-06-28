using frcastr.Core.Entities;
using frcastr.Core.Models;

namespace frcastr.Core.Interfaces;

public interface IHourlyForecastAdapter
{
    Task<IReadOnlyList<ForecastPeriod>> FetchHourlyAsync(DataSource source, CancellationToken ct = default);
}
