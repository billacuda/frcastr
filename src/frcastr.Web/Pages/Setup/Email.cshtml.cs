using frcastr.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace frcastr.Web.Pages.Setup;

[AllowAnonymous]
public class EmailModel(ISetupService setup) : PageModel
{
    [BindProperty] public InputModel Input { get; set; } = new();

    public void OnGet() { }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(Input.SmtpHost) && string.IsNullOrWhiteSpace(Input.FromAddress))
        {
            ModelState.AddModelError(nameof(Input.FromAddress), "From Address is required when SMTP Host is set.");
            return Page();
        }

        try
        {
            await setup.SaveEmailAsync(Input.SmtpHost, Input.Port, Input.UseSsl,
                Input.Username, Input.Password, Input.FromAddress, ct);
            return RedirectToPage("Branding");
        }
        catch (Exception ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            return Page();
        }
    }

    public async Task<IActionResult> OnPostSkipAsync(CancellationToken ct)
    {
        try
        {
            await setup.SaveEmailAsync(null, 587, true, null, null, null, ct);
            return RedirectToPage("Branding");
        }
        catch (Exception ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            return Page();
        }
    }

    public class InputModel
    {
        public string? SmtpHost { get; set; }
        public int Port { get; set; } = 587;
        public bool UseSsl { get; set; } = true;
        public string? Username { get; set; }
        public string? Password { get; set; }
        public string? FromAddress { get; set; }
    }
}
