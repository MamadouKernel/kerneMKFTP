using System.Diagnostics;

namespace KernelMK.Web.Services;

public interface IDashboardSnapshotSource
{
    Task<DashboardSnapshot> LoadAsync(int periodHours, CancellationToken cancellationToken);
}

public sealed record DashboardCacheStatistics(long Hits, long Misses, long Loads, long LoadFailures,
    long CoalescedRequests, double LastLoadMilliseconds, int Entries, int Capacity,
    int TimeToLiveSeconds, DateTime? OldestSnapshotAt, DateTime? NextExpirationAt)
{
    public long Requests => Hits + Misses;
    public long AvoidedLoads => Hits + CoalescedRequests;
    public double ReuseRate => Requests == 0 ? 0 : Math.Clamp(100.0 * AvoidedLoads / Requests, 0, 100);
    public double HitRate => Hits + Misses == 0 ? 0 : 100.0 * Hits / (Hits + Misses);
}

/// <summary>
/// Process-wide, fixed-key cache. Concurrent callers share one bounded load per period;
/// cancelling a browser request never cancels the shared refresh for other callers.
/// Expired values are never presented as current after a loader failure.
/// </summary>
public sealed class DashboardSnapshotCache(IDashboardSnapshotSource source, TimeProvider timeProvider)
{
    private sealed class Slot
    {
        public DashboardSnapshot? Value;
        public DateTimeOffset ExpiresAt;
        public Task<DashboardSnapshot>? InFlight;
        public long InFlightVersion;
    }
    private readonly Dictionary<int, Slot> _slots = new() { [24] = new(), [168] = new(), [720] = new() };
    private readonly object _sync = new();
    private long _hits, _misses, _loads, _failures, _coalesced, _version;
    private double _lastLoadMilliseconds;
    public static TimeSpan TimeToLive { get; } = TimeSpan.FromSeconds(15);

    public async Task<DashboardSnapshot> GetAsync(int periodHours = 24, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TaskCompletionSource<DashboardSnapshot>? completion = null;
            Task<DashboardSnapshot> pending;
            Slot slot;
            long version;
            lock (_sync)
            {
                if (!_slots.TryGetValue(periodHours, out slot!))
                    throw new ArgumentOutOfRangeException(nameof(periodHours), "Périodes autorisées : 24, 168 ou 720 heures.");
                if (slot.Value is not null && slot.ExpiresAt > timeProvider.GetUtcNow())
                {
                    _hits++;
                    return slot.Value;
                }
                _misses++;
                if (slot.InFlight is not null)
                {
                    _coalesced++;
                    pending = slot.InFlight;
                }
                else
                {
                    completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    pending = slot.InFlight = completion.Task;
                    slot.InFlightVersion = _version;
                }
                version = slot.InFlightVersion;
            }
            if (completion is not null)
                _ = PopulateAsync(periodHours, slot, version, completion);
            try
            {
                var snapshot = await pending.WaitAsync(cancellationToken).ConfigureAwait(false);
                lock (_sync)
                {
                    if (version == _version) return snapshot;
                }
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested && IsInvalidated(version))
            {
                // A failure from an obsolete refresh must not hide the replacement refresh.
            }
            // Drain the obsolete load before retrying: invalidation never starts parallel
            // database loads for the same period, and no caller receives stale results.
        }
    }

    private bool IsInvalidated(long version)
    {
        lock (_sync) return version != _version;
    }
    public void Invalidate()
    {
        lock (_sync)
        {
            _version++;
            foreach (var slot in _slots.Values)
            {
                slot.Value = null;
                slot.ExpiresAt = default;
            }
        }
    }

    public DashboardCacheStatistics GetStatistics()
    {
        lock (_sync)
        {
            var live = _slots.Values.Where(s => s.Value is not null && s.ExpiresAt > timeProvider.GetUtcNow()).ToArray();
            return new(_hits, _misses, _loads, _failures, _coalesced, _lastLoadMilliseconds,
                live.Length, _slots.Count, (int)TimeToLive.TotalSeconds,
                live.Select(s => (DateTime?)s.Value!.GeneratedAt).Min(),
                live.Select(s => (DateTime?)s.ExpiresAt.UtcDateTime).Min());
        }
    }

    private async Task PopulateAsync(int hours, Slot slot, long version, TaskCompletionSource<DashboardSnapshot> completion)
    {
        var started = Stopwatch.GetTimestamp();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            var snapshot = await source.LoadAsync(hours, timeout.Token).ConfigureAwait(false);
            lock (_sync)
            {
                if (version == _version)
                {
                    slot.Value = snapshot;
                    slot.ExpiresAt = timeProvider.GetUtcNow() + TimeToLive;
                }
                _loads++;
                _lastLoadMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                slot.InFlight = null;
            }
            completion.TrySetResult(snapshot);
        }
        catch (Exception error)
        {
            lock (_sync)
            {
                _failures++;
                _lastLoadMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                slot.InFlight = null;
            }
            completion.TrySetException(error);
            // Observe even if all waiting circuits were cancelled before completion.
            _ = completion.Task.Exception;
        }
    }
}
