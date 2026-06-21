using System.ComponentModel.DataAnnotations;
using frcastr.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace frcastr.Web.Pages.Setup;

[AllowAnonymous]
public class DatabaseModel(ISetupService setup) : PageModel
{
    [BindProperty] public InputModel Input { get; set; } = new();
    public string? TestMessage { get; private set; }
    public bool TestSuccess { get; private set; }

    public void OnGet() { }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        if (!ModelState.IsValid) return Page();

        try
        {
            await setup.SetupDatabaseAsync(
                Input.Server!, Input.Database!,
                Input.UseWindowsAuth ? null : Input.Username,
                Input.UseWindowsAuth ? null : Input.Password,
                ct);

            return RedirectToPage("Admin");
        }
        catch (Exception ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            return Page();
        }
    }

    public async Task<IActionResult> OnPostTestAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(Input.Server) || string.IsNullOrWhiteSpace(Input.Database))
        {
            TestMessage = "Server name and database name are required.";
            return Page();
        }

        try
        {
            using var conn = new Microsoft.Data.SqlClient.SqlConnection(
                BuildTestConnectionString(Input.Server, Input.Database,
                    Input.UseWindowsAuth ? null : Input.Username,
                    Input.UseWindowsAuth ? null : Input.Password));
            await conn.OpenAsync(ct);
            TestMessage = "Connection successful.";
            TestSuccess = true;
        }
        catch (Exception ex)
        {
            TestMessage = $"Connection failed: {ex.Message}";
        }

        return Page();
    }

    private static string BuildTestConnectionString(string server, string db, string? user, string? pass)
    {
        var b = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder
        {
            DataSource = server,
            InitialCatalog = db,
            TrustServerCertificate = true,
            Encrypt = true
        };
        if (!string.IsNullOrWhiteSpace(user) && !string.IsNullOrWhiteSpace(pass))
        {
            b.UserID = user;
            b.Password = pass;
        }
        else
        {
            b.IntegratedSecurity = true;
        }
        return b.ConnectionString;
    }

    public class InputModel
    {
        [Required] public string? Server { get; set; } = ".";
        [Required] public string? Database { get; set; } = "frcastr";
        public bool UseWindowsAuth { get; set; } = true;
        public string? Username { get; set; }
        public string? Password { get; set; }
    }
}
