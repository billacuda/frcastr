namespace frcastr.Core.Interfaces;

public interface IAuditService
{
    Task LogAsync(string eventType, string? userId = null, string? userName = null,
        string? ipAddress = null, string? entityType = null, string? entityId = null,
        string? entityName = null, string? oldValue = null, string? newValue = null,
        string? metadata = null, CancellationToken ct = default);
}
