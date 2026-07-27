using frcastr.Core.Interfaces;
using frcastr.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using frcastr.Web.Controllers;

namespace frcastr.Web.Pages.Admin;

[Authorize(Roles = "Administrator")]
public class ChannelsModel(ApplicationDbContext db, IWeatherDataService weatherData) : PageModel
{
    public record Row(string Key, string ChannelName, string? DeviceKey, string? DeviceName,
        decimal Value, string Unit, DateTime Timestamp, bool IsCalculated, string? Label);

    public List<Row> Channels { get; private set; } = [];

    /// <summary>Every stored label, keyed by channel key — lets the view show what a device-scoped
    /// row would inherit from its canonical channel when it has no label of its own.</summary>
    public Dictionary<string, string> Labels { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken ct)
    {
        // Same source as GET /api/admin/channels, so device-scoped keys appear and can be labeled
        // individually — one sensor can be renamed on screen without touching the whole channel.
        var readings = await weatherData.GetCurrentReadingsAsync(ct);

        Labels = await db.Settings
            .Where(s => s.Key.StartsWith(WeatherController.ChannelLabelPrefix))
            .Select(s => new { s.Key, s.Value })
            .ToDictionaryAsync(
                s => s.Key[WeatherController.ChannelLabelPrefix.Length..], s => s.Value, ct);

        Channels = readings
            .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => new Row(
                kv.Key,
                kv.Value.ChannelName,
                kv.Value.DeviceKey,
                kv.Value.DeviceName,
                kv.Value.Value,
                kv.Value.Unit,
                kv.Value.Timestamp,
                kv.Value.IsCalculated,
                Labels.GetValueOrDefault(kv.Key)))
            .ToList();
    }

    /// <summary>What this row falls back to when it carries no label of its own.</summary>
    public string Fallback(Row row) =>
        row.Key != row.ChannelName && Labels.TryGetValue(row.ChannelName, out var canonical)
            ? canonical
            : row.Key;
}
