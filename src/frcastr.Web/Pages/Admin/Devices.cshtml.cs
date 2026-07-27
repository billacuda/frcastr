using System.Text.Json;
using System.Text.Json.Serialization;
using frcastr.Core.Entities;
using frcastr.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace frcastr.Web.Pages.Admin;

[Authorize(Roles = "Administrator")]
public class DevicesModel(ApplicationDbContext db) : PageModel
{
    public List<Device> Devices { get; private set; } = [];
    public Dictionary<int, string> SourceNames { get; private set; } = [];

    /// <summary>Minutes of silence before a device is shown as stale, when it sets no override.</summary>
    public int GlobalThresholdMinutes { get; private set; } = 10;

    /// <summary>Canonical channels reported by more than one enabled device, to the device names.</summary>
    public Dictionary<string, List<string>> SharedChannels { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken ct)
    {
        Devices = await db.Devices.OrderBy(d => d.Name).ToListAsync(ct);

        SourceNames = await db.DataSources
            .Select(s => new { s.Id, s.Name })
            .ToDictionaryAsync(s => s.Id, s => s.Name, ct);

        await LoadSharedChannelsAsync(ct);

        var setting = await db.Settings
            .Where(s => s.Key == "Alerts.SensorOfflineThresholdMinutes")
            .Select(s => s.Value)
            .FirstOrDefaultAsync(ct);

        if (int.TryParse(setting, out var minutes) && minutes > 0)
            GlobalThresholdMinutes = minutes;
    }

    /// <summary>
    /// Two sensors filing under one canonical channel is the condition behind merged history and
    /// records that overwrite each other, and nothing else surfaces it. Distinct over the
    /// (DeviceId, ChannelName, Timestamp) index, so this stays an index scan rather than a table
    /// scan of every reading.
    /// </summary>
    private async Task LoadSharedChannelsAsync(CancellationToken ct)
    {
        var enabledIds = Devices.Where(d => d.IsEnabled).Select(d => d.Id).ToList();
        if (enabledIds.Count < 2) return;

        var pairs = await db.WeatherReadings
            .Where(r => r.DeviceId != null && enabledIds.Contains(r.DeviceId!.Value))
            .Select(r => new { r.ChannelName, DeviceId = r.DeviceId!.Value })
            .Distinct()
            .ToListAsync(ct);

        var names = Devices.ToDictionary(d => d.Id, d => d.Name);

        SharedChannels = pairs
            .GroupBy(p => p.ChannelName)
            .Where(g => g.Count() > 1)
            .OrderBy(g => g.Key)
            .ToDictionary(
                g => g.Key,
                g => g.Select(p => names.TryGetValue(p.DeviceId, out var n) ? n : "?")
                      .OrderBy(n => n).ToList());
    }

    public bool IsStale(Device device)
    {
        if (device.LastSeenAt is null) return false;
        var threshold = device.OfflineThresholdMinutes > 0
            ? device.OfflineThresholdMinutes
            : GlobalThresholdMinutes;
        return device.LastSeenAt < DateTime.UtcNow.AddMinutes(-threshold);
    }

    public static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        ReferenceHandler = ReferenceHandler.IgnoreCycles
    };
}
