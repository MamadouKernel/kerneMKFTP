using System.Collections.Concurrent;

namespace KernelMK.Engine.Workflow;

/// <summary>Bloque le lancement simultané d'un même job (section 8 "Concurrence") et limite le nombre global de jobs en parallèle.</summary>
public class ConcurrencyGate
{
    private readonly ConcurrentDictionary<Guid, byte> _runningJobs = new();
    private readonly SemaphoreSlim _globalLimit;

    public ConcurrencyGate(int maxParallelJobs = 20)
    {
        _globalLimit = new SemaphoreSlim(maxParallelJobs, maxParallelJobs);
    }

    public bool TryEnter(Guid jobId, bool allowConcurrent)
    {
        if (!allowConcurrent && !_runningJobs.TryAdd(jobId, 0))
        {
            return false;
        }

        if (!_globalLimit.Wait(0))
        {
            if (!allowConcurrent) _runningJobs.TryRemove(jobId, out _);
            return false;
        }

        return true;
    }

    public void Exit(Guid jobId)
    {
        _runningJobs.TryRemove(jobId, out _);
        _globalLimit.Release();
    }
}
