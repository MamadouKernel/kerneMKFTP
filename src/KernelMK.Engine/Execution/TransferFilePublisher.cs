namespace KernelMK.Engine.Execution;

/// <summary>Publishes a completed transfer without exposing or replacing a destination with partial data.</summary>
public static class TransferFilePublisher
{
    public const string TemporaryFilePrefix = ".kernelmk-transfer-";

    public static async Task PublishAsync(string destinationPath,
        Func<string, CancellationToken, Task> receiveAsync, CancellationToken ct,
        long? expectedLength = null)
    {
        ct.ThrowIfCancellationRequested();
        var destination = Path.GetFullPath(destinationPath);
        var directory = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $"{TemporaryFilePrefix}{Guid.NewGuid():N}.part");
        // Reserve a unique file in the destination directory: the final move stays on the same volume.
        using (new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }

        try
        {
            await receiveAsync(temporaryPath, ct);
            ct.ThrowIfCancellationRequested();
            using (var completed = new FileStream(temporaryPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                if (expectedLength is >= 0 && completed.Length != expectedLength.Value)
                    throw new IOException($"Transfert incomplet : {completed.Length} octet(s) reçus au lieu de {expectedLength.Value}.");
                completed.Flush(flushToDisk: true);
            }
            ct.ThrowIfCancellationRequested();
            File.Move(temporaryPath, destination, overwrite: true);
        }
        finally
        {
            // The original destination survives every failure before publication. Cleanup must not
            // hide the transfer failure when a disk/share becomes unavailable during error handling.
            try { File.Delete(temporaryPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public static async Task CopyAsync(string sourcePath, string destinationPath, CancellationToken ct)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(destinationPath), comparison))
            throw new InvalidOperationException("La source et la destination du transfert doivent être différentes.");

        var snapshot = TransferSourceSnapshot.Capture(sourcePath);
        await PublishAsync(destinationPath, async (temporaryPath, token) =>
        {
            await using (var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                             81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var output = new FileStream(temporaryPath, FileMode.Truncate, FileAccess.Write, FileShare.None,
                             81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await input.CopyToAsync(output, token);
                await output.FlushAsync(token);
            }
            snapshot.EnsureUnchanged(sourcePath);
            File.SetLastWriteTimeUtc(temporaryPath, snapshot.LastWriteTimeUtc);
        }, ct, snapshot.Length);
    }
}

/// <summary>Detects ordinary source changes before a successful upload is allowed to archive its input.</summary>
public readonly record struct TransferSourceSnapshot(long Length, DateTime LastWriteTimeUtc, DateTime CreationTimeUtc)
{
    public static TransferSourceSnapshot Capture(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists) throw new FileNotFoundException("Le fichier source du transfert est introuvable.", path);
        return new(file.Length, file.LastWriteTimeUtc, file.CreationTimeUtc);
    }

    public void EnsureUnchanged(string path)
    {
        if (!File.Exists(path) || Capture(path) != this)
            throw new IOException($"Le fichier source '{path}' a changé pendant le transfert ; il est conservé et ne doit pas être archivé ou supprimé.");
    }
}
