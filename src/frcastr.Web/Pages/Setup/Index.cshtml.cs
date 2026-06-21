using frcastr.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace frcastr.Web.Pages.Setup;

[AllowAnonymous]
public class IndexModel(ISetupService setup, ISettingsService settings) : PageModel
{
    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (await setup.IsSetupCompleteAsync(ct))
            return RedirectToPage("/Index");

        if (!await setup.IsDatabaseConfiguredAsync(ct))
            return RedirectToPage("Database");

        if (!await setup.IsAdminCreatedAsync(ct))
            return RedirectToPage("Admin");

        if (!await setup.IsStationConfiguredAsync(ct))
            return RedirectToPage("Station");

        var emailSeen = await settings.GetAsync("Email.OutboundConfigured", ct) is not null;
        if (!emailSeen)
            return RedirectToPage("Email");

        if (!await setup.IsBrandingConfiguredAsync(ct))
            return RedirectToPage("Branding");

        return RedirectToPage("Review");
    }
}
