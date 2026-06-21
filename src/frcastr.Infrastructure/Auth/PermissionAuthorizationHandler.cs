using frcastr.Core.Auth;
using frcastr.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using System.Security.Claims;

namespace frcastr.Infrastructure.Auth;

public class PermissionAuthorizationHandler(IPermissionService permissionService)
    : AuthorizationHandler<PermissionRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        PermissionRequirement requirement)
    {
        var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null) return;

        if (await permissionService.HasPermissionAsync(userId, requirement.Resource, requirement.Action))
            context.Succeed(requirement);
    }
}
