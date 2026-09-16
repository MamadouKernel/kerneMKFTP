using System.Text.Json;
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
            .ToListAsync();

        return JsonSerializer.Serialize(jobs, new JsonSerializerOptions { WriteIndented = true, ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles });
    }

    public async Task<int> ImportConfigurationAsync(string json, string? importedBy)
    {
        var jobs = JsonSerializer.Deserialize<List<Job>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                   ?? new List<Job>();

        await using var db = await _dbFactory.CreateDbContextAsync();
        // Une seule transaction pour tout l'import : avant ce correctif, chaque job supprimé était validé par
        // son propre SaveChangesAsync avant réinsertion — un échec en cours de boucle pouvait laisser des jobs
        // supprimés sans être réimportés, sans possibilité de retour arrière.
        await using var transaction = await db.Database.BeginTransactionAsync();
        var imported = 0;

        try
        {
            foreach (var job in jobs)
            {
                var existing = await db.Jobs.FirstOrDefaultAsync(j => j.Id == job.Id);
                if (existing is not null)
                {
                    // Le remove et le re-add partagent le même Id : EF Core ne tolère pas de suivre deux
                    // instances différentes sous la même clé en même temps, donc ce SaveChanges intermédiaire
                    // (qui détache l'entité supprimée) reste nécessaire — mais il est maintenant à l'intérieur
                    // de la transaction globale, donc annulé avec le reste en cas d'échec plus loin dans la boucle.
                    db.Jobs.Remove(existing);
                    await db.SaveChangesAsync();
                }

                job.CreatedBy = importedBy;
                job.CreatedAt = DateTime.UtcNow;
                db.Jobs.Add(job);
                imported++;
            }

            await db.SaveChangesAsync();
            await transaction.CommitAsync();
            return imported;
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    /// <summary>Copie physique du fichier SQLite vers un dossier de sauvegarde horodaté.</summary>
    public string BackupDatabaseFile()
    {
        var dbPath = ResolveDatabaseFilePath();

        var backupDir = Path.Combine(AppContext.BaseDirectory, "backups");
        Directory.CreateDirectory(backupDir);

        var backupPath = Path.Combine(backupDir, $"automation-platform_{DateTime.UtcNow:yyyyMMdd_HHmmss}.db");
        File.Copy(dbPath, backupPath, true);
        return backupPath;
    }

    /// <summary>
    /// Nom du fichier "restauration en attente" — un fichier .db déposé ici est appliqué au tout prochain
    /// démarrage de kernelMK (voir Program.cs), pas immédiatement. Remplacer le fichier SQLite en cours
    /// d'utilisation pendant que l'application tourne (connexions ouvertes, verrous Windows) est risqué ; passer
    /// par un redémarrage garantit qu'aucune connexion n'est active au moment du remplacement.
    /// </summary>
    public const string PendingRestoreFileName = "pending-restore.db";

    /// <summary>
    /// Valide et met en attente un fichier de base SQLite fourni par l'utilisateur pour restauration au
    /// prochain démarrage. Ne touche jamais le fichier de base actuellement utilisé par l'application.
    /// </summary>
    public async Task StagePendingDatabaseRestoreAsync(Stream uploadedFile)
    {
        // "SQLite format 3\0" : signature de 16 octets en tête de tout fichier de base SQLite valide — rejette
        // immédiatement un fichier qui n'est manifestement pas une base SQLite plutôt que de le mettre en attente
        // et de casser le démarrage au redémarrage suivant.
        var header = new byte[16];
        var read = await uploadedFile.ReadAsync(header.AsMemory(0, 16));
        if (read < 16 || System.Text.Encoding.ASCII.GetString(header) != "SQLite format 3\0")
        {
            throw new InvalidOperationException("Le fichier fourni ne semble pas être une base de données SQLite valide (en-tête incorrect).");
        }

        await using var fileStream = File.Create(GetPendingRestorePath());
        await fileStream.WriteAsync(header.AsMemory(0, read));
        await uploadedFile.CopyToAsync(fileStream);
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
    private string ResolveDatabaseFilePath()
    {
        var connectionString = _configuration.GetConnectionString("Default") ?? "Data Source=automation-platform.db";
        var dbPath = new SqliteConnectionStringBuilder(connectionString).DataSource;
        if (!Path.IsPathRooted(dbPath)) dbPath = Path.Combine(AppContext.BaseDirectory, dbPath);
        return dbPath;
    }
}
