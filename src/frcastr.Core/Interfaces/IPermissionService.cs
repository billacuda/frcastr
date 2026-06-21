namespace frcastr.Core.Interfaces;

public interface IPermissionService
{
    Task<bool> HasPermissionAsync(string userId, string resource, string action, CancellationToken ct = default);
}
