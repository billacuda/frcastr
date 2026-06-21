using frcastr.Core.Entities;
using frcastr.Core.Interfaces;
using frcastr.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace frcastr.Infrastructure.Services;

public class SettingsService(ApplicationDbContext dbContext) : ISettingsService
{
    public async Task<string?> GetAsync(string key, CancellationToken ct = default)
        => (await dbContext.Settings.FirstOrDefaultAsync(s => s.Key == key, ct))?.Value;

    public async Task<int> GetIntAsync(string key, int fallback, CancellationToken ct = default)
    {
        var raw = await GetAsync(key, ct);
        return int.TryParse(raw, out var v) ? v : fallback;
    }

    public async Task<bool> GetBoolAsync(string key, bool fallback, CancellationToken ct = default)
    {
        var raw = await GetAsync(key, ct);
        return bool.TryParse(raw, out var v) ? v : fallback;
    }

    public async Task<decimal> GetDecimalAsync(string key, decimal fallback, CancellationToken ct = default)
    {
        var raw = await GetAsync(key, ct);
        return decimal.TryParse(raw, out var v) ? v : fallback;
    }

    public async Task UpsertAsync(string key, string value, string? description = null,
        bool isSystem = false, string? modifiedBy = null, CancellationToken ct = default)
    {
        var existing = await dbContext.Settings.FirstOrDefaultAsync(s => s.Key == key, ct);
        if (existing is null)
        {
            dbContext.Settings.Add(new Setting
            {
                Key = key,
                Value = value,
                Description = description,
                IsSystemSetting = isSystem,
                LastModifiedBy = modifiedBy
            });
        }
        else
        {
            existing.Value = value;
            existing.LastModifiedAt = DateTime.UtcNow;
            if (description is not null) existing.Description = description;
            if (modifiedBy is not null) existing.LastModifiedBy = modifiedBy;
        }

        await dbContext.SaveChangesAsync(ct);
    }
}
