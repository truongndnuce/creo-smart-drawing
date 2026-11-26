using System.Collections.Concurrent;

namespace Cadflow.Application.Jobs;

public sealed record WorkerStatus(string Name, DateTimeOffset LastSeen, bool Online);

/// <summary>Remembers which workers called the server lately (claim, progress...). Memory only: a restart empties it and workers reappear on their next call.</summary>
public sealed class WorkerRegistry(TimeProvider clock)
{
    /// <summary>A worker building a long job only calls on progress, so the window is generous.</summary>
    public static readonly TimeSpan OnlineWindow = TimeSpan.FromSeconds(90);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _seen = new();

    public void Touch(string name) => _seen[name] = clock.GetUtcNow();

    public IReadOnlyList<WorkerStatus> List()
    {
        var now = clock.GetUtcNow();
        return _seen.Select(kv => new WorkerStatus(kv.Key, kv.Value, now - kv.Value <= OnlineWindow)).OrderBy(w => w.Name).ToList();
    }
}
