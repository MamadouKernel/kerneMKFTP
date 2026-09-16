using System.Data;
using System.Data.Common;
using System.Text;
using System.Text.Json;
using KernelMK.Core;
using KernelMK.Core.StepConfigs;
using Microsoft.Data.Sqlite;
using Microsoft.Data.SqlClient;

namespace KernelMK.Engine.Execution.Executors;

/// <summary>Exécution de requêtes SQL, procédures stockées, sur SQL Server ou SQLite (section 4.2 "Bases de données").</summary>
public class DatabaseStepExecutor : IStepExecutor
{
    private const int MaxPreviewRows = 50;

    public IReadOnlyCollection<StepType> SupportedTypes { get; } = new[] { StepType.ScriptSql, StepType.BaseDeDonneesRequete };

    public async Task<StepExecutionResult> ExecuteAsync(StepExecutionContext context)
    {
        var config = JsonSerializer.Deserialize<SqlStepConfig>(context.Step.ConfigJson)
                      ?? throw new InvalidOperationException("Configuration SQL invalide.");

        DbConnection connection;
        if (config.Provider.Equals("SqlServer", StringComparison.OrdinalIgnoreCase))
        {
            if (context.ResolvedCredential is { Host: not null } cred)
            {
                // Un credential de type "Base de données" est sélectionné : la connexion est construite à
                // partir de ses identifiants déchiffrés plutôt que d'exiger un mot de passe SQL Server en
                // clair dans ConnectionString — avant ce correctif, le credential BaseDeDonnees n'était utilisé
                // par aucune étape, malgré son existence dans le coffre-fort.
                if (string.IsNullOrWhiteSpace(config.Database))
                {
                    return StepExecutionResult.Fail("Un credential est sélectionné mais aucun nom de base (\"Database\") n'est renseigné dans la configuration de l'étape.");
                }
                var server = cred.Port is > 0 ? $"{cred.Host},{cred.Port}" : cred.Host;
                var builder = new SqlConnectionStringBuilder
                {
                    DataSource = server,
                    InitialCatalog = config.Database,
                    UserID = cred.Username ?? string.Empty,
                    Password = cred.Secret ?? string.Empty,
                    TrustServerCertificate = true
                };
                connection = new SqlConnection(builder.ConnectionString);
            }
            else
            {
                connection = new SqlConnection(config.ConnectionString);
            }
        }
        else if (config.Provider.Equals("Sqlite", StringComparison.OrdinalIgnoreCase))
        {
            connection = new SqliteConnection(config.ConnectionString);
        }
        else
        {
            // Avant ce correctif, un fournisseur mal orthographié (ex: "MySql", "Sql Server") basculait
            // silencieusement sur SQLite et échouait avec une erreur SQLite trompeuse au lieu de signaler
            // clairement le vrai problème (fournisseur non supporté).
            return StepExecutionResult.Fail(
                $"Fournisseur de base de données inconnu : \"{config.Provider}\". Valeurs acceptées : \"SqlServer\" ou \"Sqlite\" (sensible uniquement à l'orthographe, pas à la casse).");
        }

        try
        {
            await using (connection)
            {
                await connection.OpenAsync(context.CancellationToken);

                using var command = connection.CreateCommand();
                command.CommandText = config.CommandText;
                command.CommandTimeout = config.CommandTimeoutSeconds;
                command.CommandType = config.IsStoredProcedure ? CommandType.StoredProcedure : CommandType.Text;

                // Avant ce correctif, toute requête (y compris un SELECT) passait par ExecuteNonQueryAsync, qui
                // renvoie toujours -1 pour un SELECT et n'expose aucune des lignes retournées — l'étape
                // "réussissait" silencieusement sans jamais donner accès aux données demandées.
                var trimmed = config.CommandText.TrimStart();
                var looksLikeQuery = !config.IsStoredProcedure &&
                    (trimmed.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) ||
                     trimmed.StartsWith("WITH", StringComparison.OrdinalIgnoreCase) ||
                     trimmed.StartsWith("PRAGMA", StringComparison.OrdinalIgnoreCase));

                if (looksLikeQuery)
                {
                    return await ExecuteQueryAsync(command, context.CancellationToken);
                }

                var rowsAffected = await command.ExecuteNonQueryAsync(context.CancellationToken);
                return StepExecutionResult.Ok($"{rowsAffected} ligne(s) affectée(s).", rowsAffected);
            }
        }
        catch (Exception ex)
        {
            return StepExecutionResult.Fail(ex.Message);
        }
    }

    private static async Task<StepExecutionResult> ExecuteQueryAsync(DbCommand command, CancellationToken ct)
    {
        await using var reader = await command.ExecuteReaderAsync(ct);
        var columnNames = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToArray();

        var preview = new StringBuilder();
        preview.AppendLine(string.Join(" | ", columnNames));

        var rowCount = 0;
        while (await reader.ReadAsync(ct))
        {
            rowCount++;
            if (rowCount <= MaxPreviewRows)
            {
                var values = Enumerable.Range(0, reader.FieldCount)
                    .Select(i => reader.IsDBNull(i) ? "" : reader.GetValue(i)?.ToString() ?? "");
                preview.AppendLine(string.Join(" | ", values));
            }
        }

        var summary = rowCount > MaxPreviewRows
            ? $"{rowCount} ligne(s) retournée(s) (aperçu des {MaxPreviewRows} premières) :\n{preview}"
            : $"{rowCount} ligne(s) retournée(s) :\n{preview}";
        return StepExecutionResult.Ok(summary, rowCount);
    }
}
