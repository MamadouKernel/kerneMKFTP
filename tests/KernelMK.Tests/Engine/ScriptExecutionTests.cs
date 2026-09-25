using System.Diagnostics;
using System.Text.Json;
using KernelMK.Core;
using KernelMK.Core.Entities;
using KernelMK.Core.StepConfigs;
using KernelMK.Engine.Execution;
using KernelMK.Engine.Execution.Executors;

namespace KernelMK.Tests.Engine;

public class ScriptExecutionTests
{
    private static StepExecutionContext Context(string script, CancellationToken ct = default) => new()
    {
        Job = new Job(),
        Execution = new JobExecution(),
        Step = new JobStep
        {
            Type = StepType.ScriptPowerShell,
            ConfigJson = JsonSerializer.Serialize(new ScriptStepConfig { InlineScript = script })
        },
        CancellationToken = ct
    };

    [Fact]
    public async Task CancelStopsOperatingSystemProcess()
    {
        var pidFile = Path.Combine(Path.GetTempPath(), $"kmk-test-pid-{Guid.NewGuid():N}.txt");
        using var cancellation = new CancellationTokenSource();
        Process? process = null;
        var run = new ScriptStepExecutor().ExecuteAsync(Context(
            $"$PID | Set-Content -LiteralPath '{pidFile.Replace("'", "''")}'; Start-Sleep -Seconds 60", cancellation.Token));
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!File.Exists(pidFile) || new FileInfo(pidFile).Length == 0)
                await Task.Delay(25, deadline.Token);
            process = Process.GetProcessById(int.Parse((await File.ReadAllTextAsync(pidFile, deadline.Token)).Trim()));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await run.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.True(process.HasExited);
        }
        finally
        {
            cancellation.Cancel();
            if (process is { HasExited: false }) process.Kill(true);
            process?.Dispose();
            if (File.Exists(pidFile)) File.Delete(pidFile);
        }
    }

    [Fact]
    public async Task VeryLargeSingleLineOutputIsDrainedAndTruncated()
    {
        var result = await new ScriptStepExecutor().ExecuteAsync(Context("[Console]::Out.Write(('x' * 1200000))"))
            .WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(result.Success);
        Assert.Contains("Sortie tronquée", result.Output);
        Assert.InRange(result.Output!.Length, 1_048_576, 1_048_700);
    }
}
