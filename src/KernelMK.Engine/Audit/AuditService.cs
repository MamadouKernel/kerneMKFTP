using KernelMK.Core;
using KernelMK.Core.Entities;
using KernelMK.Data;
using KernelMK.Engine.Infrastructure;

namespace KernelMK.Engine.Audit;

public class AuditService
{
    private readonly IDbWriteQueue _writeQueue;

    public AuditService(IDbWriteQueue writeQueue)
    {
        _writeQueue = writeQueue;
    }

    /// <summary>
    /// Empile l'écriture de l'entrée d'audit dans la file — ne bloque jamais sur l'accès à la base (retourne dès
    /// que l'écriture est en mémoire), le worker DbWriteQueueService l'écrit ensuite en base de façon asynchrone.
    /// L'entrée peut donc apparaître dans /audit quelques instants après l'action qui l'a déclenchée, ce qui est
    /// sans conséquence pour un journal d'audit consulté a posteriori.
    /// </summary>
    public Task LogAsync(AuditAction action, string entityType, string? entityId, string? entityName,
        string? userId, string? userName, string? details = null)
    {
        _writeQueue.Enqueue((db, ct) =>
        {
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
            return db.SaveChangesAsync(ct);
        });
        return Task.CompletedTask;
    }
}
