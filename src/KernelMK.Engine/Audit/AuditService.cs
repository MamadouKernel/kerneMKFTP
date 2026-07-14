using KernelMK.Core;
using KernelMK.Core.Entities;
using KernelMK.Data;
using Microsoft.EntityFrameworkCore;

namespace KernelMK.Engine.Audit;

public class AuditService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;

    public AuditService(IDbContextFactory<AppDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task LogAsync(AuditAction action, string entityType, string? entityId, string? entityName,
        string? userId, string? userName, string? details = null)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        db.AuditLogEntries.Add(new AuditLogEntry
        {
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            EntityName = entityName,
            UserId = userId,
            UserName = userName,
            Details = details
        });
        await db.SaveChangesAsync();
    }
}
