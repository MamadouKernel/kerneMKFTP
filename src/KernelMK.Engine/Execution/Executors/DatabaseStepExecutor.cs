using System.Data;
using System.Data.Common;
using System.Text.Json;
using KernelMK.Core;
using KernelMK.Core.StepConfigs;
using Microsoft.Data.Sqlite;
using Microsoft.Data.SqlClient;

namespace KernelMK.Engine.Execution.Executors;

/// <summary>Exécution de requêtes SQL, procédures stockées, sur SQL Server ou SQLite (section 4.2 "Bases de données").</summary>
public class DatabaseStepExecutor : IStepExecutor
{
    public IReadOnlyCollection<StepType> SupportedTypes { get; } = new[] { StepType.ScriptSql, StepType.BaseDeDonneesRequete };

    public async Task<StepExecutionResult> ExecuteAsync(StepExecutionContext context)
    {
        var config = JsonSerializer.Deserialize<SqlStepConfig>(context.Step.ConfigJson)
                      ?? throw new InvalidOperationException("Configuration SQL invalide.");

        try
        {
            using DbConnection connection = config.Provider.Equals("SqlServer", StringComparison.OrdinalIgnoreCase)
                ? new SqlConnection(config.ConnectionString)
                : new SqliteConnection(config.ConnectionString);

            await connection.OpenAsync(context.CancellationToken);

            using var command = connection.CreateCommand();
            command.CommandText = config.CommandText;
            command.CommandTimeout = config.CommandTimeoutSeconds;
            command.CommandType = config.IsStoredProcedure ? CommandType.StoredProcedure : CommandType.Text;

            var rowsAffected = await command.ExecuteNonQueryAsync(context.CancellationToken);
            return StepExecutionResult.Ok($"{rowsAffected} ligne(s) affectée(s).", rowsAffected);
        }
        catch (Exception ex)
        {
            return StepExecutionResult.Fail(ex.Message);
        }
    }
}
