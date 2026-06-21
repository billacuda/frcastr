using frcastr.Core.Entities;
using frcastr.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace frcastr.Web.Pages.Admin;

[Authorize(Roles = "Administrator")]
public class RolesModel(
    RoleManager<IdentityRole> roleManager,
    ApplicationDbContext db) : PageModel
{
    public static readonly string[] Resources =
    [
        "DataSources", "History", "Alerts",
        "Settings.General", "Settings.Station", "Settings.Branding", "Settings.Auth", "Settings.Dashboard",
        "Widgets", "Webhooks", "Audit"
    ];

    public static readonly string[] Actions = ["Read", "Add", "Edit", "Delete"];

    public record RoleRow(IdentityRole Role, HashSet<(string Resource, string Action)> Perms);

    public List<RoleRow> Roles { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken ct)
    {
        var roles = await roleManager.Roles.ToListAsync(ct);
        var perms = await db.Permissions.ToListAsync(ct);
        foreach (var r in roles)
        {
            var rolePerms = perms
                .Where(p => p.RoleId == r.Id && p.Resource != null && p.Action != null)
                .Select(p => (p.Resource!, p.Action!))
                .ToHashSet();
            Roles.Add(new RoleRow(r, rolePerms));
        }
    }
}
