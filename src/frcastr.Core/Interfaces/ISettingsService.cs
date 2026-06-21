namespace frcastr.Core.Interfaces;

public interface ISettingsService
{
    Task<string?> GetAsync(string key, CancellationToken ct = default);
    Task<int> GetIntAsync(string key, int fallback, CancellationToken ct = default);
    Task<bool> GetBoolAsync(string key, bool fallback, CancellationToken ct = default);
    Task<decimal> GetDecimalAsync(string key, decimal fallback, CancellationToken ct = default);
    Task UpsertAsync(string key, string value, string? description = null,
        bool isSystem = false, string? modifiedBy = null, CancellationToken ct = default);
}
