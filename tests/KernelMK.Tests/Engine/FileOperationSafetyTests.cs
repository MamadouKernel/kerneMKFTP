using System.Text.Json;
using KernelMK.Core;
using KernelMK.Core.Entities;
using KernelMK.Core.StepConfigs;
using KernelMK.Data.Security;
using KernelMK.Engine.Execution;
using KernelMK.Engine.Execution.Executors;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;

namespace KernelMK.Tests.Engine;

public sealed class FileOperationSafetyTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("kmk-file-safety-").FullName;

    private static StepExecutionContext Context(StepType type, object config, CancellationToken ct = default) => new()
    {
        Job = new Job(),
        Execution = new JobExecution(),
        Step = new JobStep { Type = type, ConfigJson = JsonSerializer.Serialize(config) },
        CancellationToken = ct
    };

    private static TransferStepExecutor TransferExecutor() => new(null!,
        new CredentialProtector(new EphemeralDataProtectionProvider()), NullLogger<TransferStepExecutor>.Instance);

    private string CreateSource()
    {
        var source = Path.Combine(_directory, "source.txt");
        File.WriteAllText(source, "original content");
        return source;
    }

    private TransferStepConfig TransferConfig(string source, string? archive = null)
    {
        var remote = Path.Combine(_directory, "remote");
        Directory.CreateDirectory(remote);
        return new TransferStepConfig { LocalPath = source, SmbShare = remote, ArchiveDirectory = archive };
    }

    [Fact]
    public async Task MoveWithinSameDirectoryNeverDeletesSource()
    {
        var source = CreateSource();
        await new FileOpsStepExecutor().ExecuteAsync(Context(StepType.FichierDeplacer,
            new FileOpStepConfig { SourcePath = source, DestinationPath = _directory, Overwrite = true }));
        Assert.True(File.Exists(source));
        Assert.Equal("original content", await File.ReadAllTextAsync(source));
    }

    [Fact]
    public async Task RecursiveCopyDoesNotProcessFilesItHasJustCreated()
    {
        var source = CreateSource();
        var destination = Path.Combine(_directory, "copies");
        Directory.CreateDirectory(destination);
        var result = await new FileOpsStepExecutor().ExecuteAsync(Context(StepType.FichierCopier,
            new FileOpStepConfig { SourcePath = _directory, DestinationPath = destination, Recursive = true }));
        Assert.True(result.Success, result.ErrorOutput);
        Assert.Equal(Path.Combine(destination, "source.txt"), result.FilesProcessedCsv);
        Assert.Equal(await File.ReadAllTextAsync(source), await File.ReadAllTextAsync(Path.Combine(destination, "source.txt")));
    }

    [Fact]
    public async Task TransferArchiveCannotDeleteSourceWhenDirectoriesAreIdentical()
    {
        var source = CreateSource();
        var config = TransferConfig(source, Path.Combine(_directory, "."));
        var result = await TransferExecutor().ExecuteAsync(Context(StepType.TransfertSmb, config));
        Assert.False(result.Success);
        Assert.Contains("différent du dossier source", result.ErrorOutput);
        Assert.Equal("original content", await File.ReadAllTextAsync(source));
    }

    [Fact]
    public async Task TransferReportsFailedArchivingAndPreservesSource()
    {
        var source = CreateSource();
        var archive = Path.Combine(_directory, "blocked-archive");
        await File.WriteAllTextAsync(archive, "This file prevents the archive directory from being created.");
        var result = await TransferExecutor().ExecuteAsync(Context(StepType.TransfertSmb, TransferConfig(source, archive)));
        Assert.False(result.Success);
        Assert.Equal("original content", await File.ReadAllTextAsync(source));
    }

    [Fact]
    public async Task SuccessfulTransferArchivesSourceAndReplacesOldArchive()
    {
        var source = CreateSource();
        var archive = Path.Combine(_directory, "archive");
        Directory.CreateDirectory(archive);
        var archiveFile = Path.Combine(archive, "source.txt");
        await File.WriteAllTextAsync(archiveFile, "old content");
        var config = TransferConfig(source, archive);
        var result = await TransferExecutor().ExecuteAsync(Context(StepType.TransfertSmb, config));
        Assert.True(result.Success, result.ErrorOutput);
        Assert.False(File.Exists(source));
        Assert.Equal("original content", await File.ReadAllTextAsync(archiveFile));
        Assert.Equal("original content", await File.ReadAllTextAsync(Path.Combine(config.SmbShare!, "source.txt")));
    }

    [Fact]
    public async Task CanceledTransferDoesNotCopyAnyFile()
    {
        var source = CreateSource();
        var config = TransferConfig(source);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            TransferExecutor().ExecuteAsync(Context(StepType.TransfertSmb, config, cancellation.Token)));
        Assert.Empty(Directory.GetFiles(config.SmbShare!));
        Assert.True(File.Exists(source));
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
