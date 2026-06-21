using frcastr.Core.Entities;
using frcastr.Core.Interfaces;
using frcastr.Infrastructure.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace frcastr.Infrastructure.Services;

public class PermissionService(
    ApplicationDbContext dbContext,
    UserManager<ApplicationUser> userManager) : IPermissionService
{
    public async Task<bool> HasPermissionAsync(
        string userId, string resource, string action,
        CancellationToken ct = default)
    {
        var user = await userManager.FindByIdAsync(userId);
        if (user is null) return false;

        if (await userManager.IsInRoleAsync(user, "Administrator"))
            return true;

        var userRoles = await userManager.GetRolesAsync(user);
        if (userRoles.Count == 0) return false;

        var roleIds = await dbContext.Roles
            .Where(r => userRoles.Contains(r.Name!))
            .Select(r => r.Id)
            .ToListAsync(ct);

        return await dbContext.Permissions
            .AnyAsync(p => p.RoleId != null
                        && roleIds.Contains(p.RoleId)
                        && p.Resource == resource
                        && p.Action == action,
                      ct);
    }
}
