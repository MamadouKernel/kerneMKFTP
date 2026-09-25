using System.Reflection;
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

public sealed class TransferReliabilityTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("kmk-transfer-").FullName;

    private string LocalPath => Path.Combine(_directory, "local", "message.edi");
    private string RemoteDirectory => Path.Combine(_directory, "remote");
    private string RemotePath => Path.Combine(RemoteDirectory, "message.edi");

    private static TransferStepExecutor Executor() => new(null!,
        new CredentialProtector(new EphemeralDataProtectionProvider()), NullLogger<TransferStepExecutor>.Instance);

    private static StepExecutionContext Context(TransferStepConfig config, CancellationToken ct = default) => new()
    {
        Job = new Job(), Execution = new JobExecution(),
        Step = new JobStep { Type = StepType.TransfertSmb, ConfigJson = JsonSerializer.Serialize(config) },
        CancellationToken = ct
    };

    private async Task WriteAsync(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content);
    }

    private void AssertNoTemporaryFiles() => Assert.Empty(
        Directory.EnumerateFiles(_directory, $"{TransferFilePublisher.TemporaryFilePrefix}*", SearchOption.AllDirectories));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InterruptedDownloadNeverPublishesPartialData(bool existingDestination)
    {
        if (existingDestination) await WriteAsync(LocalPath, "previous complete document");

        await Assert.ThrowsAsync<IOException>(() => TransferFilePublisher.PublishAsync(LocalPath,
            async (temporary, ct) =>
            {
                Assert.Equal(Path.GetDirectoryName(LocalPath), Path.GetDirectoryName(temporary));
                await File.WriteAllTextAsync(temporary, "partial", ct);
                Assert.Equal(existingDestination, File.Exists(LocalPath));
                if (existingDestination)
                    Assert.Equal("previous complete document", await File.ReadAllTextAsync(LocalPath, ct));
                throw new IOException("Connection lost before completion");
            }, CancellationToken.None));

        Assert.Equal(existingDestination, File.Exists(LocalPath));
        if (existingDestination) Assert.Equal("previous complete document", await File.ReadAllTextAsync(LocalPath));
        AssertNoTemporaryFiles();
    }

    [Fact]
    public async Task SuccessfulDownloadReplacesDestinationOnlyAfterReceiverCompletes()
    {
        await WriteAsync(LocalPath, "old");
        await TransferFilePublisher.PublishAsync(LocalPath, async (temporary, ct) =>
        {
            await File.WriteAllTextAsync(temporary, "new complete document", ct);
            Assert.Equal("old", await File.ReadAllTextAsync(LocalPath, ct));
        }, CancellationToken.None, expectedLength: 21);

        Assert.Equal("new complete document", await File.ReadAllTextAsync(LocalPath));
        AssertNoTemporaryFiles();
    }

    [Fact]
    public async Task CancellationAfterReceiverReturnsDoesNotPublishOrDeleteOriginal()
    {
        await WriteAsync(LocalPath, "old");
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => TransferFilePublisher.PublishAsync(LocalPath,
            async (temporary, ct) =>
            {
                await File.WriteAllTextAsync(temporary, "new", ct);
                cancellation.Cancel();
            }, cancellation.Token));

        Assert.Equal("old", await File.ReadAllTextAsync(LocalPath));
        AssertNoTemporaryFiles();
    }

    [Fact]
    public async Task CancellationDuringReceiveCleansPartialFile()
    {
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => TransferFilePublisher.PublishAsync(LocalPath,
            async (temporary, ct) =>
            {
                await File.WriteAllTextAsync(temporary, "partial", ct);
                cancellation.Cancel();
                ct.ThrowIfCancellationRequested();
            }, cancellation.Token));

        Assert.False(File.Exists(LocalPath));
        AssertNoTemporaryFiles();
    }

    [Fact]
    public async Task UnexpectedReceivedLengthPreservesDestination()
    {
        await WriteAsync(LocalPath, "old");
        await Assert.ThrowsAsync<IOException>(() => TransferFilePublisher.PublishAsync(LocalPath,
            (temporary, ct) => File.WriteAllTextAsync(temporary, "short", ct),
            CancellationToken.None, expectedLength: 100));
        Assert.Equal("old", await File.ReadAllTextAsync(LocalPath));
        AssertNoTemporaryFiles();
    }

    [Fact]
    public async Task FailedPublicationCleansTemporaryFile()
    {
        Directory.CreateDirectory(LocalPath); // A directory cannot be replaced by the received file.
        var failure = await Record.ExceptionAsync(() => TransferFilePublisher.PublishAsync(LocalPath,
            (temporary, ct) => File.WriteAllTextAsync(temporary, "complete", ct), CancellationToken.None));
        Assert.True(failure is IOException or UnauthorizedAccessException, failure?.ToString());
        Assert.True(Directory.Exists(LocalPath));
        AssertNoTemporaryFiles();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SmbDownloadPublishesThenDeletesSource(bool filtered)
    {
        await WriteAsync(LocalPath, "old");
        await WriteAsync(RemotePath, "new complete document");
        var config = new TransferStepConfig
        {
            LocalPath = filtered ? Path.GetDirectoryName(LocalPath)! : LocalPath,
            SmbShare = RemoteDirectory, Upload = false,
            DeleteRemoteAfterDownload = true, Filter = filtered ? "*.edi" : null
        };
        var result = await Executor().ExecuteAsync(Context(config));
        Assert.True(result.Success, result.ErrorOutput);
        Assert.Equal("new complete document", await File.ReadAllTextAsync(LocalPath));
        Assert.False(File.Exists(RemotePath));
        Assert.Equal(LocalPath, result.FilesProcessedCsv);
        AssertNoTemporaryFiles();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedSmbPublicationKeepsRemoteSource(bool filtered)
    {
        await WriteAsync(RemotePath, "complete source");
        Directory.CreateDirectory(LocalPath);
        var config = new TransferStepConfig
        {
            LocalPath = filtered ? Path.GetDirectoryName(LocalPath)! : LocalPath,
            SmbShare = RemoteDirectory, Upload = false,
            DeleteRemoteAfterDownload = true, Filter = filtered ? "*.edi" : null
        };
        var result = await Executor().ExecuteAsync(Context(config));
        Assert.False(result.Success);
        Assert.Equal("complete source", await File.ReadAllTextAsync(RemotePath));
        AssertNoTemporaryFiles();
    }

    [Fact]
    public async Task SmbDownloadToSameFileCannotDeleteItsSource()
    {
        await WriteAsync(RemotePath, "complete source");
        var config = new TransferStepConfig
        {
            LocalPath = RemotePath, SmbShare = RemoteDirectory,
            Upload = false, DeleteRemoteAfterDownload = true
        };
        var result = await Executor().ExecuteAsync(Context(config));
        Assert.False(result.Success);
        Assert.Equal("complete source", await File.ReadAllTextAsync(RemotePath));
        AssertNoTemporaryFiles();
    }

    [Fact]
    public async Task SmbFilterDoesNotTransferInternalPartialFiles()
    {
        await WriteAsync(RemotePath, "complete source");
        await WriteAsync(Path.Combine(RemoteDirectory, $"{TransferFilePublisher.TemporaryFilePrefix}active.part"), "partial");
        var result = await Executor().ExecuteAsync(Context(new TransferStepConfig
        {
            LocalPath = Path.GetDirectoryName(LocalPath)!, SmbShare = RemoteDirectory,
            Upload = false, Filter = "*"
        }));
        Assert.True(result.Success, result.ErrorOutput);
        Assert.Equal(new[] { LocalPath }, Directory.GetFiles(Path.GetDirectoryName(LocalPath)!));
        Assert.Equal("complete source", await File.ReadAllTextAsync(LocalPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SourceChangedAfterUploadIsNotArchived(bool sameLength)
    {
        await WriteAsync(LocalPath, "initial");
        var snapshot = TransferSourceSnapshot.Capture(LocalPath);
        var archive = Path.Combine(_directory, "archive");
        var archiveFile = Path.Combine(archive, Path.GetFileName(LocalPath));
        await WriteAsync(archiveFile, "last good archive");
        await File.WriteAllTextAsync(LocalPath, sameLength ? "changed" : "newer document has more content");
        // Avoid depending on the timestamp resolution of the underlying filesystem.
        File.SetLastWriteTimeUtc(LocalPath, snapshot.LastWriteTimeUtc.AddSeconds(2));
        var method = typeof(TransferStepExecutor).GetMethod("ArchiveIfRequested", BindingFlags.NonPublic | BindingFlags.Static)!;
        var exception = Assert.Throws<TargetInvocationException>(() => method.Invoke(null, new object[]
        {
            new TransferStepConfig { Upload = true, ArchiveAfterTransfer = true, ArchiveDirectory = archive },
            LocalPath, snapshot
        }));
        Assert.IsType<IOException>(exception.InnerException);
        Assert.True(File.Exists(LocalPath));
        Assert.Equal("last good archive", await File.ReadAllTextAsync(archiveFile));
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
