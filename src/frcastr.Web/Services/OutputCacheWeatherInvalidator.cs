using frcastr.Core.Interfaces;
using Microsoft.AspNetCore.OutputCaching;

namespace frcastr.Web.Services;

/// <summary>
/// Evicts the "weather-current" output cache tag (see the WeatherCurrent policy in Program.cs) so
/// background ingestion can publish readings to the dashboard without waiting for expiry.
/// </summary>
public class OutputCacheWeatherInvalidator(IOutputCacheStore store) : IWeatherCacheInvalidator
{
    public Task InvalidateCurrentAsync(CancellationToken ct = default)
        => store.EvictByTagAsync("weather-current", ct).AsTask();
}
