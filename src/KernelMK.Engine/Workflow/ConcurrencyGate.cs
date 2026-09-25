namespace KernelMK.Engine.Workflow;

/// <summary>Limite globale et suivi de toutes les exécutions, même concurrentes, d'un job.</summary>
public class ConcurrencyGate
{
    private readonly Dictionary<Guid, int> _runningJobs = new();
    private readonly object _sync = new();
    private readonly int _maxParallelJobs;
    private int _runningCount;

    public ConcurrencyGate(int maxParallelJobs = 20)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxParallelJobs, 1);
        _maxParallelJobs = maxParallelJobs;
    }

    public bool TryEnter(Guid jobId, bool allowConcurrent)
    {
        lock (_sync)
        {
            _runningJobs.TryGetValue(jobId, out var count);
            if ((!allowConcurrent && count > 0) || _runningCount >= _maxParallelJobs)
                return false;
            _runningJobs[jobId] = count + 1;
            _runningCount++;
            return true;
        }
    }

    public void Exit(Guid jobId)
    {
        lock (_sync)
        {
            if (!_runningJobs.TryGetValue(jobId, out var count))
                throw new InvalidOperationException("Le job ne possède aucun créneau d'exécution actif.");
            if (count == 1) _runningJobs.Remove(jobId);
            else _runningJobs[jobId] = count - 1;
            _runningCount--;
        }
    }
}
