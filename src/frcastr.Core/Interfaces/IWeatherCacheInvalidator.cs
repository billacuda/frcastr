namespace frcastr.Core.Interfaces;

/// <summary>
/// Drops the cached current-conditions response so freshly ingested readings appear immediately.
/// Implemented in the web layer over the output cache; abstracted here so Infrastructure does not
/// need a dependency on ASP.NET Core.
/// </summary>
public interface IWeatherCacheInvalidator
{
    Task InvalidateCurrentAsync(CancellationToken ct = default);
}
