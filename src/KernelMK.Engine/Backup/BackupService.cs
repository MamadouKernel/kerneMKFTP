using System.Text.Json;
using KernelMK.Core.Entities;
using KernelMK.Data;
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
        var imported = 0;

        foreach (var job in jobs)
        {
            var existing = await db.Jobs.FirstOrDefaultAsync(j => j.Id == job.Id);
            if (existing is not null)
            {
                db.Jobs.Remove(existing);
                await db.SaveChangesAsync();
            }

            job.CreatedBy = importedBy;
            job.CreatedAt = DateTime.UtcNow;
            db.Jobs.Add(job);
            imported++;
        }

        await db.SaveChangesAsync();
        return imported;
    }

    /// <summary>Copie physique du fichier SQLite vers un dossier de sauvegarde horodaté.</summary>
    public string BackupDatabaseFile()
    {
        var connectionString = _configuration.GetConnectionString("Default") ?? "Data Source=automation-platform.db";
        var dbPath = connectionString.Replace("Data Source=", "", StringComparison.OrdinalIgnoreCase).Trim();
        if (!Path.IsPathRooted(dbPath)) dbPath = Path.Combine(AppContext.BaseDirectory, dbPath);

        var backupDir = Path.Combine(AppContext.BaseDirectory, "backups");
        Directory.CreateDirectory(backupDir);

        var backupPath = Path.Combine(backupDir, $"automation-platform_{DateTime.UtcNow:yyyyMMdd_HHmmss}.db");
        File.Copy(dbPath, backupPath, true);
        return backupPath;
    }
}
