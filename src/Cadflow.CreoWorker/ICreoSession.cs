namespace Cadflow.CreoWorker;

public sealed record WorkItem(string Id, string ProductCode, string Kind, Dictionary<string, double> Inputs);
public sealed record OutputFile(string Name, string Kind, string Path);
public sealed record BuildOutput(Dictionary<string, double> Summary, List<OutputFile> Files);

/// <summary>One CAD session that can build a product. Implementations: SimCreoSession (no Creo needed), NativeCreoSession (CreoBridge10, later).</summary>
public interface ICreoSession
{
    string Name { get; }
    /// <summary>Builds the job in <paramref name="workDir"/>; throws <see cref="BuildException"/> for a failure that retrying cannot fix (bad inputs).</summary>
    Task<BuildOutput> BuildAsync(WorkItem job, string workDir, Func<string, string?, Task> progress, CancellationToken ct);
}

public sealed class BuildException(string message, bool retryable = false) : Exception(message) { public bool Retryable { get; } = retryable; }
