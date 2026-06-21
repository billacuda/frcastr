using System.ComponentModel.DataAnnotations;
using frcastr.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace frcastr.Web.Pages.Setup;

[AllowAnonymous]
public class BrandingModel(ISetupService setup) : PageModel
{
    [BindProperty] public InputModel Input { get; set; } = new();

    public void OnGet() { }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        if (!ModelState.IsValid) return Page();

        try
        {
            await setup.SaveBrandingAsync(Input.AppName!, Input.PrimaryColor!, ct);
            return RedirectToPage("Review");
        }
        catch (Exception ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            return Page();
        }
    }

    public class InputModel
    {
        [Required] public string? AppName { get; set; } = "frcastr";
        [Required, RegularExpression(@"^#[0-9a-fA-F]{6}$", ErrorMessage = "Enter a valid hex color (e.g. #0d6efd).")]
        public string? PrimaryColor { get; set; } = "#0d6efd";
    }
}
