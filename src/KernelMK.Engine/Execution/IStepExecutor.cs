using KernelMK.Core;

namespace KernelMK.Engine.Execution;

public interface IStepExecutor
{
    IReadOnlyCollection<StepType> SupportedTypes { get; }
    Task<StepExecutionResult> ExecuteAsync(StepExecutionContext context);
}
