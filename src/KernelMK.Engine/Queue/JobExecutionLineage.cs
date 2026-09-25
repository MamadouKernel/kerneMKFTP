using System.Text.Json;

namespace KernelMK.Engine.Queue;

/// <summary>Persists ancestry when a job crosses the queue, so cycles cannot hide behind asynchronous calls.</summary>
public static class JobExecutionLineage
{
    private static readonly AsyncLocal<IReadOnlySet<Guid>?> CurrentPath = new();
    public static IReadOnlySet<Guid> Current => CurrentPath.Value ?? new HashSet<Guid>();
    public static string Serialize() => JsonSerializer.Serialize(Current.ToArray());
    public static IReadOnlySet<Guid> Parse(string json) =>
        (JsonSerializer.Deserialize<Guid[]>(json) ?? []).ToHashSet();

    public static IDisposable Enter(Guid jobId, IReadOnlySet<Guid>? ancestors = null)
    {
        var previous = CurrentPath.Value;
        var path = new HashSet<Guid>(ancestors ?? Current);
        if (!path.Add(jobId)) throw new InvalidOperationException("Appel cyclique de job refusé.");
        if (path.Count > 100) throw new InvalidOperationException("La chaîne de jobs dépasse 100 niveaux.");
        CurrentPath.Value = path;
        return new Restore(() => CurrentPath.Value = previous);
    }

    private sealed class Restore(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }
}
