using System.Text.Json;
using KernelMK.Core;
using KernelMK.Core.Entities;
using KernelMK.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace KernelMK.Engine.Backup;

/// <summary>Sauvegarde/restauration de la configuration (jobs, déclencheurs, notifications) — section 3.1 et 5.4 du cahier des charges.</summary>
public class BackupService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IConfiguration _configuration;

    public BackupService(IDbContextFactory<AppDbContext> dbFactory, IConfiguration configuration)
    {
        _dbFactory = dbFactory;
        _configuration = configuration;
    }

    public async Task<string> ExportConfigurationAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var jobs = await db.Jobs
            .Include(j => j.Steps)
            .Include(j => j.Triggers)
            .Include(j => j.Dependencies)
            .Include(j => j.NotificationRules)
            .AsNoTracking()
            .AsSplitQuery()
            .ToListAsync();

        return JsonSerializer.Serialize(jobs, new JsonSerializerOptions { WriteIndented = true, ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles });
    }

    public async Task<int> ImportConfigurationAsync(string json, string? importedBy)
    {
        var jobs = JsonSerializer.Deserialize<List<Job>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                   ?? new List<Job>();

        await using var db = await _dbFactory.CreateDbContextAsync();
        if (jobs.Count > 5000 || jobs.Any(j => j.Id == Guid.Empty || string.IsNullOrWhiteSpace(j.Name))
            || jobs.Select(j => j.Id).Distinct().Count() != jobs.Count)
            throw new InvalidOperationException("Configuration invalide : identifiants dupliqués, nom absent ou plus de 5000 jobs.");
        await using var transaction = await db.Database.BeginTransactionAsync();
        foreach (var job in jobs)
        {
            // Import is a configuration update, never a delete-and-recreate of execution history.
            var existing = await db.Jobs.Include(j => j.Steps).Include(j => j.Triggers)
                .Include(j => j.Dependencies).Include(j => j.NotificationRules).AsSplitQuery()
                .SingleOrDefaultAsync(j => j.Id == job.Id);
            if (await db.JobExecutions.AnyAsync(e => e.JobId == job.Id && e.Status == JobStatus.EnCours))
                throw new InvalidOperationException($"Le job '{job.Name}' est en cours d'exécution. Arrêtez-le avant l'import.");

            job.Executions.Clear();
            job.Enabled = false; // Imported scripts/schedules require deliberate review before activation.
            job.NextRunAt = null;
            foreach (var step in job.Steps) { step.Job = null; step.Credential = null; step.JobId = job.Id; }
            foreach (var trigger in job.Triggers) { trigger.Job = null; trigger.JobId = job.Id; trigger.NextRunAt = null; }
            foreach (var dependency in job.Dependencies) { dependency.Job = null; dependency.DependsOnJob = null; dependency.JobId = job.Id; }
            foreach (var rule in job.NotificationRules) { rule.Job = null; rule.JobId = job.Id; }
            if (existing is null)
            {
                job.CreatedBy = importedBy;
                job.CreatedAt = DateTime.UtcNow;
                job.LastRunAt = null;
                job.LastStatus = JobStatus.EnAttente;
                db.Jobs.Add(job);
            }
            else
            {
                job.CreatedAt = existing.CreatedAt;
                job.CreatedBy = existing.CreatedBy;
                job.LastRunAt = existing.LastRunAt;
                job.LastStatus = existing.LastStatus;
                job.UpdatedAt = DateTime.UtcNow;
                job.UpdatedBy = importedBy;
                db.JobSteps.RemoveRange(existing.Steps);
                db.JobTriggers.RemoveRange(existing.Triggers);
                db.JobDependencies.RemoveRange(existing.Dependencies);
                db.NotificationRules.RemoveRange(existing.NotificationRules);
                await db.SaveChangesAsync();
                db.Entry(existing).CurrentValues.SetValues(job);
                db.JobSteps.AddRange(job.Steps);
                db.JobTriggers.AddRange(job.Triggers);
                db.JobDependencies.AddRange(job.Dependencies);
                db.NotificationRules.AddRange(job.NotificationRules);
            }
        }
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return jobs.Count;
    }

    /// <summary>Consistent online SQLite snapshot, including committed WAL transactions.</summary>
    public string BackupDatabaseFile()
    {
        var backupDir = Path.Combine(AppContext.BaseDirectory, "backups");
        Directory.CreateDirectory(backupDir);
        var backupPath = Path.Combine(backupDir, $"automation-platform_{DateTime.UtcNow:yyyyMMdd_HHmmss_fff}_{Guid.NewGuid():N}.db");
        CreateDatabaseSnapshot(ResolveDatabaseFilePath(), backupPath);
        return backupPath;
    }

    public const string PendingRestoreFileName = "pending-restore.db";

    public static void CreateDatabaseSnapshot(string sourcePath, string destinationPath)
    {
        using var source = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = sourcePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ConnectionString);
        using var destination = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = destinationPath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ConnectionString);
        source.Open();
        destination.Open();
        source.BackupDatabase(destination);
        using var command = destination.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=DELETE;";
        command.ExecuteNonQuery();
    }

    public async Task StagePendingDatabaseRestoreAsync(Stream uploadedFile)
    {
        var pendingPath = GetPendingRestorePath();
        var temporaryPath = pendingPath + "." + Guid.NewGuid().ToString("N") + ".upload";
        try
        {
            await using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await uploadedFile.ReadAsync(buffer)) > 0)
                {
                    total += read;
                    if (total > 500_000_000) throw new InvalidOperationException("La sauvegarde dépasse 500 Mo.");
                    await output.WriteAsync(buffer.AsMemory(0, read));
                }
                await output.FlushAsync();
            }
            ValidateDatabase(temporaryPath);
            File.Move(temporaryPath, pendingPath, overwrite: true);
        }
        finally { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
    }

    public static void ValidateDatabase(string databasePath)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = databasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA trusted_schema=OFF;";
        command.ExecuteNonQuery();
        command.CommandText = "PRAGMA integrity_check;";
        if (!string.Equals(command.ExecuteScalar()?.ToString(), "ok", StringComparison.Ordinal))
            throw new InvalidOperationException("La sauvegarde SQLite est corrompue.");
        command.CommandText = "SELECT name FROM sqlite_master WHERE type='table';";
        var tables = new HashSet<string>(StringComparer.Ordinal);
        using (var reader = command.ExecuteReader())
            while (reader.Read()) tables.Add(reader.GetString(0));
        string[] required = ["Jobs", "JobSteps", "JobTriggers", "JobExecutions", "AspNetUsers", "AspNetRoles", "AspNetUserRoles", "__EFMigrationsHistory"];
        if (required.Any(table => !tables.Contains(table)))
            throw new InvalidOperationException("Ce fichier n'est pas une sauvegarde kernelMK compatible.");
        // Reject backups from a newer unknown schema before replacing the current database.
        var knownMigrations = typeof(AppDbContext).Assembly.GetTypes()
            .Select(t => t.GetCustomAttributes(typeof(Microsoft.EntityFrameworkCore.Migrations.MigrationAttribute), false)
                .OfType<Microsoft.EntityFrameworkCore.Migrations.MigrationAttribute>().FirstOrDefault()?.Id)
            .Where(id => id is not null).ToHashSet(StringComparer.Ordinal);
        command.CommandText = "SELECT MigrationId FROM __EFMigrationsHistory;";
        using var migrations = command.ExecuteReader();
        var count = 0;
        while (migrations.Read())
        {
            count++;
            if (!knownMigrations.Contains(migrations.GetString(0)))
                throw new InvalidOperationException("Cette sauvegarde utilise une version de schéma inconnue. Mettez à jour l'application avant de la restaurer.");
        }
        if (count == 0) throw new InvalidOperationException("Historique des migrations absent de la sauvegarde.");
    }

    /// <summary>Only called before any application connection/worker starts.</summary>
    public static bool ApplyPendingDatabaseRestore(IConfiguration configuration)
    {
        var dbPath = ResolveDatabaseFilePath(configuration);
        var pendingPath = Path.Combine(Path.GetDirectoryName(dbPath)!, PendingRestoreFileName);
        if (!File.Exists(pendingPath)) return false;
        ValidateDatabase(pendingPath);
        if (File.Exists(dbPath))
        {
            var backupDirectory = Path.Combine(AppContext.BaseDirectory, "backups");
            Directory.CreateDirectory(backupDirectory);
            CreateDatabaseSnapshot(dbPath, Path.Combine(backupDirectory, $"avant-restauration_{DateTime.UtcNow:yyyyMMdd_HHmmss_fff}_{Guid.NewGuid():N}.db"));
            // Recover/checkpoint the previous WAL before removing its sidecars; old WAL pages must never
            // be replayed onto the newly restored database.
            using var current = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = dbPath, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ConnectionString);
            current.Open();
            using var checkpoint = current.CreateCommand();
            checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            if (Convert.ToInt32(checkpoint.ExecuteScalar()) != 0)
                throw new IOException("La base est utilisée par un autre processus. Restauration annulée.");
        }
        foreach (var suffix in new[] { "-wal", "-shm" })
            if (File.Exists(dbPath + suffix)) File.Delete(dbPath + suffix);
        File.Move(pendingPath, dbPath, overwrite: true);
        return true;
    }

    /// <summary>Annule une restauration précédemment mise en attente (si l'admin change d'avis avant le redémarrage).</summary>
    public bool CancelPendingDatabaseRestore()
    {
        var stagingPath = GetPendingRestorePath();
        if (!File.Exists(stagingPath)) return false;
        File.Delete(stagingPath);
        return true;
    }

    public bool HasPendingDatabaseRestore() => File.Exists(GetPendingRestorePath());

    /// <summary>
    /// Même dossier que le fichier de base lui-même (pas "DataDirectory", utilisé ailleurs seulement pour le
    /// trousseau de clés) — c'est ce même chemin que Program.cs relit au démarrage suivant pour appliquer la
    /// restauration en attente ; les deux doivent impérativement rester cohérents.
    /// </summary>
    private string GetPendingRestorePath()
    {
        var dbPath = ResolveDatabaseFilePath();
        var dbDir = Path.GetDirectoryName(dbPath) ?? AppContext.BaseDirectory;
        return Path.Combine(dbDir, PendingRestoreFileName);
    }

    /// <summary>
    /// Extrait le chemin de fichier réel de la chaîne de connexion SQLite. Avant ce correctif, seul le préfixe
    /// "Data Source=" était retiré : avec la chaîne réelle de production "Data Source=App_Data/....db;Cache=Shared",
    /// le suffixe ";Cache=Shared" restait collé au chemin, provoquant un FileNotFoundException (fichier "...db;Cache=Shared"
    /// introuvable) qui faisait planter tout le circuit Blazor — sauvegarde et restauration étaient donc totalement
    /// inutilisables dès qu'un paramètre supplémentaire suivait "Data Source=" dans la chaîne de connexion.
    /// </summary>
    private string ResolveDatabaseFilePath() => ResolveDatabaseFilePath(_configuration);

    private static string ResolveDatabaseFilePath(IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Default") ?? "Data Source=automation-platform.db";
        var dbPath = new SqliteConnectionStringBuilder(connectionString).DataSource;
        if (!Path.IsPathRooted(dbPath)) dbPath = Path.Combine(AppContext.BaseDirectory, dbPath);
        return dbPath;
    }
}
