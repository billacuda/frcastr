using frcastr.Core.Entities;
using frcastr.Core.Interfaces;
using frcastr.Infrastructure.Data;

namespace frcastr.Infrastructure.Services;

public class AuditService(ApplicationDbContext dbContext) : IAuditService
{
    public async Task LogAsync(string eventType, string? userId = null, string? userName = null,
        string? ipAddress = null, string? entityType = null, string? entityId = null,
        string? entityName = null, string? oldValue = null, string? newValue = null,
        string? metadata = null, CancellationToken ct = default)
    {
        dbContext.AuditLogs.Add(new AuditLog
        {
            EventType = eventType,
            UserId = userId,
            UserName = userName,
            IpAddress = ipAddress,
            EntityType = entityType,
            EntityId = entityId,
            EntityName = entityName,
            OldValue = oldValue,
            NewValue = newValue,
            Metadata = metadata
        });

        await dbContext.SaveChangesAsync(ct);
    }
}
