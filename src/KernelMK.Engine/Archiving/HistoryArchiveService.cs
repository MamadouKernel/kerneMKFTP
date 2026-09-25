using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using KernelMK.Core;
using KernelMK.Core.Entities;
using KernelMK.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace KernelMK.Engine.Archiving;

public sealed class HistoryArchiveOptions
{
    public const string SectionName = "HistoryArchive";
    public bool Enabled { get; set; } = true;
    public string? Directory { get; set; }
    public int RetentionDays { get; set; } = 90;
    public int BatchSize { get; set; } = 1000;
    public int RunEveryHours { get; set; } = 24;
}

public sealed record HistoryArchiveResult(int ExecutionsArchived, int StepLogsArchived, string? ArchivePath);

/// <summary>Archives only completed execution history. An archive and its SHA-256 sidecar are committed before any SQLite row is deleted.</summary>
public sealed class HistoryArchiveService(
    IDbContextFactory<AppDbContext> factory,
    IConfiguration configuration,
    ILogger<HistoryArchiveService> logger)
{
    private const int MaxRetentionDays = 3650;
    private const int MaxBatchSize = 5000;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    public async Task<HistoryArchiveResult> ArchiveOnceAsync(CancellationToken cancellationToken = default)
    {
        var options = configuration.GetSection(HistoryArchiveOptions.SectionName).Get<HistoryArchiveOptions>() ?? new();
        if (!options.Enabled) return new(0, 0, null);
        var retentionDays = Math.Clamp(options.RetentionDays, 7, MaxRetentionDays);
        var batchSize = Math.Clamp(options.BatchSize, 1, MaxBatchSize);
        var cutoff = DateTime.UtcNow.AddDays(-retentionDays);

        await using var readDb = await factory.CreateDbContextAsync(cancellationToken);
        var executions = await readDb.JobExecutions.AsNoTracking()
            .Where(e => e.FinishedAt != null && e.FinishedAt < cutoff && e.Status != JobStatus.EnCours)
            .OrderBy(e => e.FinishedAt).ThenBy(e => e.Id).Take(batchSize)
            .Include(e => e.StepLogs).AsSplitQuery().ToListAsync(cancellationToken);
        if (executions.Count == 0) return new(0, 0, null);

        var archiveDirectory = ResolveArchiveDirectory(options.Directory);
        Directory.CreateDirectory(archiveDirectory);
        var archivePath = Path.Combine(archiveDirectory, $"history-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.json.gz");
        var temporaryPath = archivePath + ".tmp";
        var hashPath = archivePath + ".sha256";
        try
        {
            await WriteArchiveAsync(temporaryPath, cutoff, executions, cancellationToken);
            File.Move(temporaryPath, archivePath);
            var checksum = await ComputeSha256Async(archivePath, cancellationToken);
            await File.WriteAllTextAsync(hashPath + ".tmp", checksum + "  " + Path.GetFileName(archivePath), cancellationToken);
            File.Move(hashPath + ".tmp", hashPath);
            if (!string.Equals(checksum, await ComputeSha256Async(archivePath, cancellationToken), StringComparison.Ordinal))
                throw new IOException("Contrôle d'intégrité de l'archive impossible.");

            var executionIds = executions.Select(e => e.Id).ToArray();
            var stepCount = executions.Sum(e => e.StepLogs.Count);
            await using var writeDb = await factory.CreateDbContextAsync(cancellationToken);
            await using var transaction = await writeDb.Database.BeginTransactionAsync(cancellationToken);
            await writeDb.StepExecutionLogs.Where(log => executionIds.Contains(log.JobExecutionId)).ExecuteDeleteAsync(cancellationToken);
            await writeDb.JobExecutions.Where(e => executionIds.Contains(e.Id) && e.FinishedAt != null && e.FinishedAt < cutoff && e.Status != JobStatus.EnCours).ExecuteDeleteAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            logger.LogInformation("{Count} exécution(s) et {StepCount} log(s) archivés dans {ArchivePath}.", executions.Count, stepCount, archivePath);
            return new(executions.Count, stepCount, archivePath);
        }
        catch
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            if (File.Exists(hashPath + ".tmp")) File.Delete(hashPath + ".tmp");
            throw;
        }
    }

    private static string ResolveArchiveDirectory(string? configuredDirectory) =>
        string.IsNullOrWhiteSpace(configuredDirectory)
            ? Path.Combine(AppContext.BaseDirectory, "archives", "history")
            : Path.GetFullPath(configuredDirectory);

    private static async Task WriteArchiveAsync(string temporaryPath, DateTime cutoff, List<JobExecution> executions, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.WriteThrough);
        await using var gzip = new GZipStream(stream, CompressionLevel.SmallestSize);
        var archive = new HistoryArchiveDocument(1, DateTime.UtcNow, cutoff, executions.Select(e => new ArchivedExecution(
            e.Id, e.JobId, e.StartedAt, e.FinishedAt, e.Status, e.TriggeredBy, e.ServerName, e.ExecutionAccount, e.ReturnCode, e.Message, e.AttemptNumber,
            e.StepLogs.OrderBy(log => log.Order).Select(log => new ArchivedStepLog(log.Id, log.JobStepId, log.StepName, log.Order, log.StartedAt, log.FinishedAt, log.Status, log.ReturnCode, log.Output, log.ErrorOutput, log.FilesProcessedCsv, log.AttemptNumber)).ToArray())).ToArray());
        await JsonSerializer.SerializeAsync(gzip, archive, JsonOptions, cancellationToken);
        await gzip.FlushAsync(cancellationToken);
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
    }

    private sealed record HistoryArchiveDocument(int FormatVersion, DateTime ArchivedAtUtc, DateTime RetentionCutoffUtc, ArchivedExecution[] Executions);
    private sealed record ArchivedExecution(Guid Id, Guid JobId, DateTime StartedAt, DateTime? FinishedAt, JobStatus Status, string TriggeredBy, string? ServerName, string? ExecutionAccount, int? ReturnCode, string? Message, int AttemptNumber, ArchivedStepLog[] StepLogs);
    private sealed record ArchivedStepLog(Guid Id, Guid JobStepId, string StepName, int Order, DateTime StartedAt, DateTime? FinishedAt, StepExecutionStatus Status, int? ReturnCode, string? Output, string? ErrorOutput, string? FilesProcessedCsv, int AttemptNumber);
}
