using frcastr.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace frcastr.Web.Pages;

[AllowAnonymous]
public class IndexModel(ISettingsService settings) : PageModel
{
    public int PollIntervalSeconds { get; private set; } = 30;
    public bool KioskMode { get; private set; }
    public string Theme { get; private set; } = "auto";
    public string WindUnit { get; private set; } = "kmh";
    public string PressureUnit { get; private set; } = "hPa";
    public string RainUnit { get; private set; } = "mm";
    public string StationLat { get; private set; } = "";
    public string StationLon { get; private set; } = "";

    public async Task OnGetAsync(bool kiosk = false, CancellationToken ct = default)
    {
        PollIntervalSeconds = await settings.GetIntAsync("Dashboard.PollIntervalSeconds", 30, ct);
        KioskMode = kiosk || await settings.GetBoolAsync("Dashboard.KioskMode", false, ct);
        Theme = await settings.GetAsync("Display.Theme", ct) ?? "auto";
        WindUnit = await settings.GetAsync("Display.WindUnit", ct) ?? "kmh";
        PressureUnit = await settings.GetAsync("Display.PressureUnit", ct) ?? "hPa";
        RainUnit = await settings.GetAsync("Display.RainUnit", ct) ?? "mm";
        StationLat = await settings.GetAsync("Station.Latitude", ct) ?? "";
        StationLon = await settings.GetAsync("Station.Longitude", ct) ?? "";
    }
}
