using frcastr.Core.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace frcastr.Web.Pages.Admin;

[Authorize(Roles = "Administrator")]
public class UsersModel(
    UserManager<ApplicationUser> userManager,
    RoleManager<IdentityRole> roleManager) : PageModel
{
    public record UserRow(ApplicationUser User, IList<string> Roles);

    public List<UserRow> Users { get; private set; } = [];
    public List<string> AllRoles { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken ct)
    {
        var users = await userManager.Users.ToListAsync(ct);
        AllRoles = await roleManager.Roles.Select(r => r.Name!).ToListAsync(ct);
        foreach (var u in users)
            Users.Add(new UserRow(u, await userManager.GetRolesAsync(u)));
        Users = [.. Users.OrderBy(r => r.User.Email)];
    }
}
