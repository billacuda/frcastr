using frcastr.Core.Interfaces;
using frcastr.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace frcastr.Web.Pages;

[AllowAnonymous]
public class IndexModel(ISettingsService settings, ApplicationDbContext db) : PageModel
{
    public int PollIntervalSeconds { get; private set; } = 30;
    public bool KioskMode { get; private set; }
    public string Theme { get; private set; } = "auto";
    public string WindUnit { get; private set; } = "kmh";
    public string PressureUnit { get; private set; } = "hPa";
    public string RainUnit { get; private set; } = "mm";
    public bool TemperatureDecimals { get; private set; }
    public string StationLat { get; private set; } = "";
    public string StationLon { get; private set; } = "";

    /// <summary>
    /// Rendered into frcastrConfig rather than fetched, so the dashboard menu can show the right
    /// state the moment it builds and the grid doesn't wait on a second request to know how many
    /// columns a phone gets.
    /// </summary>
    public bool MobileTwoColumn { get; private set; }

    public async Task OnGetAsync(bool kiosk = false, string dash = "Default", CancellationToken ct = default)
    {
        PollIntervalSeconds = await settings.GetIntAsync("Dashboard.PollIntervalSeconds", 30, ct);
        KioskMode = kiosk || await settings.GetBoolAsync("Dashboard.KioskMode", false, ct);
        Theme = await settings.GetAsync("Display.Theme", ct) ?? "auto";
        WindUnit = await settings.GetAsync("Display.WindUnit", ct) ?? "kmh";
        PressureUnit = await settings.GetAsync("Display.PressureUnit", ct) ?? "hPa";
        RainUnit = await settings.GetAsync("Display.RainUnit", ct) ?? "mm";
        TemperatureDecimals = await settings.GetBoolAsync("Display.TemperatureDecimals", false, ct);
        StationLat = await settings.GetAsync("Station.Latitude", ct) ?? "";
        StationLon = await settings.GetAsync("Station.Longitude", ct) ?? "";

        // Same resolution order as GET /api/dashboard/layout: the shared row wins over a
        // per-owner one.
        MobileTwoColumn = await db.DashboardLayouts
            .Where(l => l.Name == dash)
            .OrderBy(l => l.OwnerId == null ? 0 : 1)
            .Select(l => l.MobileTwoColumn)
            .FirstOrDefaultAsync(ct);
    }
}
