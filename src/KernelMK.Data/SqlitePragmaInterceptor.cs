using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace KernelMK.Data;

/// <summary>
/// Configure chaque connexion SQLite ouverte par EF Core avec un délai d'attente en cas de verrou ("busy_timeout")
/// au lieu de l'échec immédiat par défaut (SQLITE_BUSY) — indispensable dès que plusieurs utilisateurs/requêtes
/// accèdent à la base en même temps (ex. dashboard en rafraîchissement automatique + création d'un job en
/// parallèle) : la connexion en attente d'un verrou patiente jusqu'à 5s au lieu d'échouer aussitôt.
/// Combiné au mode journal WAL (activé une fois au démarrage, voir Program.cs), les lecteurs ne sont plus
/// bloqués par un écrivain en cours — c'était la cause probable des lenteurs "quasiment impossible" constatées
/// en production lors de la création de jobs pendant que d'autres pages interrogeaient la base.
/// </summary>
public class SqlitePragmaInterceptor : DbConnectionInterceptor
{
    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        ApplyPragmas(connection);
    }

    public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout=5000; PRAGMA synchronous=NORMAL;";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void ApplyPragmas(DbConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout=5000; PRAGMA synchronous=NORMAL;";
        command.ExecuteNonQuery();
    }
}
