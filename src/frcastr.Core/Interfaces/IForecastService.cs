using frcastr.Core.Models;

namespace frcastr.Core.Interfaces;

public interface IForecastService
{
    Task<ForecastResult> GetForecastAsync(CancellationToken ct = default);
}
