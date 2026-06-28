using frcastr.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace frcastr.Web.Pages;

[AllowAnonymous]
public class RadarModel(ISettingsService settings) : PageModel
{
    public string StationLat { get; private set; } = "";
    public string StationLon { get; private set; } = "";

    public async Task OnGetAsync(CancellationToken ct = default)
    {
        StationLat = await settings.GetAsync("Station.Latitude", ct) ?? "";
        StationLon = await settings.GetAsync("Station.Longitude", ct) ?? "";
    }
}
