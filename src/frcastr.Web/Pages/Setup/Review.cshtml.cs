using frcastr.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Configuration;

namespace frcastr.Web.Pages.Setup;

[AllowAnonymous]
public class ReviewModel(
    ISetupService setup,
    ISettingsService settings,
    IConfiguration configuration) : PageModel
{
    public bool IsDbConfigured { get; private set; }
    public string? StationName { get; private set; }
    public string? Latitude { get; private set; }
    public string? Longitude { get; private set; }
    public string? TimeZone { get; private set; }
    public string? AppName { get; private set; }
    public string? PrimaryColor { get; private set; }
    public bool EmailConfigured { get; private set; }
    public string? SmtpHost { get; private set; }
    public string? ErrorMessage { get; private set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        IsDbConfigured = await setup.IsDatabaseConfiguredAsync(ct);
        StationName = await settings.GetAsync("Station.Name", ct);
        Latitude = await settings.GetAsync("Station.Latitude", ct);
        Longitude = await settings.GetAsync("Station.Longitude", ct);
        TimeZone = await settings.GetAsync("Station.TimeZone", ct);
        AppName = configuration["Branding:AppName"] ?? await settings.GetAsync("Branding:AppName", ct);
        PrimaryColor = configuration["Branding:PrimaryColor"] ?? "#0d6efd";
        SmtpHost = await settings.GetAsync("Email.SmtpHost", ct);
        EmailConfigured = !string.IsNullOrWhiteSpace(SmtpHost);
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        try
        {
            await setup.FinalizeSetupAsync(ct);
            return Redirect("/Identity/Account/Login");
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            await OnGetAsync(ct);
            return Page();
        }
    }
}
