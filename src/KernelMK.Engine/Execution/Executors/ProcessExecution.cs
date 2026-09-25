using System.Diagnostics;
using System.Text;

namespace KernelMK.Engine.Execution.Executors;

/// <summary>Vide les deux flux sans allocation illimitée et arrête l'arbre de processus sur annulation.</summary>
internal static class ProcessExecution
{
    internal const int MaxCapturedCharacters = 1_048_576;

    internal static async Task<(int ExitCode, string Output, string Error)> RunAsync(
        ProcessStartInfo startInfo, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var process = new Process { StartInfo = startInfo };
        process.Start();
        // Lecture par blocs : ReadLine accumulerait une ligne sans limite avant toute troncature.
        var output = ReadBoundedAsync(process.StandardOutput, cancellationToken);
        var error = ReadBoundedAsync(process.StandardError, cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
            return (process.ExitCode, await output, await error);
        }
        catch
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            // Attendre la sortie évite qu'un processus annulé continue à écrire après le job.
            await process.WaitForExitAsync(CancellationToken.None);
            try { await Task.WhenAll(output, error); } catch (OperationCanceledException) { }
            throw;
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken ct)
    {
        var text = new StringBuilder();
        var buffer = new char[8192];
        var truncated = false;
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), ct)) > 0)
        {
            var keep = Math.Min(read, MaxCapturedCharacters - text.Length);
            if (keep > 0) text.Append(buffer, 0, keep);
            truncated |= keep < read;
        }
        if (truncated) text.Append("\n[Sortie tronquée à 1 Mio de caractères]");
        return text.ToString();
    }
}
