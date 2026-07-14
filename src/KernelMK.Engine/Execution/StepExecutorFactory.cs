using KernelMK.Core;

namespace KernelMK.Engine.Execution;

public class StepExecutorFactory
{
    private readonly Dictionary<StepType, IStepExecutor> _executorsByType;

    public StepExecutorFactory(IEnumerable<IStepExecutor> executors)
    {
        _executorsByType = new Dictionary<StepType, IStepExecutor>();
        foreach (var executor in executors)
        {
            foreach (var type in executor.SupportedTypes)
            {
                _executorsByType[type] = executor;
            }
        }
    }

    public IStepExecutor Resolve(StepType type)
    {
        if (_executorsByType.TryGetValue(type, out var executor)) return executor;
        throw new NotSupportedException($"Aucun exécuteur enregistré pour le type d'étape {type}.");
    }
}
