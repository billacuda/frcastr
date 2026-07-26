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

    public async Task OnGetAsync(CancellationToken ct)
    {
        Devices = await db.Devices.OrderBy(d => d.Name).ToListAsync(ct);

        SourceNames = await db.DataSources
            .Select(s => new { s.Id, s.Name })
            .ToDictionaryAsync(s => s.Id, s => s.Name, ct);

        var setting = await db.Settings
            .Where(s => s.Key == "Alerts.SensorOfflineThresholdMinutes")
            .Select(s => s.Value)
            .FirstOrDefaultAsync(ct);

        if (int.TryParse(setting, out var minutes) && minutes > 0)
            GlobalThresholdMinutes = minutes;
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
