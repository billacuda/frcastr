using System.ComponentModel.DataAnnotations;
using frcastr.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace frcastr.Web.Pages.Setup;

[AllowAnonymous]
public class StationModel(ISetupService setup) : PageModel
{
    [BindProperty] public InputModel Input { get; set; } = new();
    public IReadOnlyList<TimeZoneInfo> TimeZones { get; private set; } = [];

    public void OnGet()
    {
        TimeZones = TimeZoneInfo.GetSystemTimeZones();
        Input.TimeZone = TimeZoneInfo.Local.Id;
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        TimeZones = TimeZoneInfo.GetSystemTimeZones();
        if (!ModelState.IsValid) return Page();

        try
        {
            await setup.SaveStationAsync(
                Input.Name!, Input.Latitude, Input.Longitude, Input.Elevation, Input.TimeZone!, ct);
            return RedirectToPage("Email");
        }
        catch (Exception ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            return Page();
        }
    }

    public class InputModel
    {
        [Required] public string? Name { get; set; } = "My Weather Station";
        [Required, Range(-90, 90)] public decimal Latitude { get; set; }
        [Required, Range(-180, 180)] public decimal Longitude { get; set; }
        public decimal Elevation { get; set; }
        [Required] public string? TimeZone { get; set; }
    }
}
