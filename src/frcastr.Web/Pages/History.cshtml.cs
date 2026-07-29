using frcastr.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace frcastr.Web.Pages;

[AllowAnonymous]
public class HistoryModel(ISettingsService settings) : PageModel
{
    public string WindUnit { get; private set; } = "kmh";
    public string PressureUnit { get; private set; } = "hPa";
    public string RainUnit { get; private set; } = "mm";
    public bool TemperatureDecimals { get; private set; }

    public async Task OnGetAsync(CancellationToken ct = default)
    {
        WindUnit = await settings.GetAsync("Display.WindUnit", ct) ?? "kmh";
        PressureUnit = await settings.GetAsync("Display.PressureUnit", ct) ?? "hPa";
        RainUnit = await settings.GetAsync("Display.RainUnit", ct) ?? "mm";
        TemperatureDecimals = await settings.GetBoolAsync("Display.TemperatureDecimals", false, ct);
    }
}
