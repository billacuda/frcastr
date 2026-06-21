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

    public async Task OnGetAsync(bool kiosk = false, CancellationToken ct = default)
    {
        PollIntervalSeconds = await settings.GetIntAsync("Dashboard.PollIntervalSeconds", 30, ct);
        KioskMode = kiosk || await settings.GetBoolAsync("Dashboard.KioskMode", false, ct);
        Theme = await settings.GetAsync("Display.Theme", ct) ?? "auto";
    }
}
