using Cadflow.Domain.Jobs;

namespace Cadflow.Application.Abstractions;

public sealed record JobFilter(string? Status = null, string? ProjectId = null, string? Product = null, string? RequestedBy = null, int Limit = 100);

/// <summary>Persistence port. Implementations: PostgreSQL (production), in-memory (tests / sim).</summary>
public interface IJobRepository
{
    Task AddAsync(Job job, CancellationToken ct = default);
    Task<Job?> GetAsync(string id, CancellationToken ct = default);
    Task<IReadOnlyList<Job>> ListAsync(JobFilter filter, CancellationToken ct = default);
    /// <summary>Atomically hands the most urgent queued job (lowest priority number, then oldest) to the worker, or null. Must be safe under concurrent callers.</summary>
    Task<Job?> ClaimNextAsync(string worker, IReadOnlyCollection<string>? products, IReadOnlyCollection<string>? queues, DateTimeOffset now, CancellationToken ct = default);
    Task SaveAsync(Job job, CancellationToken ct = default);
    /// <summary>Returns running jobs whose heartbeat is older than <paramref name="timeout"/> to the queue.</summary>
    Task<int> ReclaimStaleAsync(TimeSpan timeout, DateTimeOffset now, CancellationToken ct = default);
}

public enum ProjectRole { Viewer = 1, Designer = 2, Reviewer = 3, ProjectAdmin = 4 }

public sealed record ProjectInfo(string Id, string Code, string Name, string CadSystem, ProjectRole Role, IReadOnlyList<string> EnabledProducts);

/// <summary>Who is calling (from the Entra token in production, from a dev setting in development).</summary>
public interface ICurrentUser
{
    string Id { get; }
    string Name { get; }
}

/// <summary>Port to the Admin app: which projects a user belongs to, the role, the enabled products. Reads only.</summary>
public interface IProjectDirectory
{
    Task<IReadOnlyList<ProjectInfo>> ListForUserAsync(string userId, CancellationToken ct = default);
    Task<ProjectInfo?> FindForUserAsync(string userId, string projectId, CancellationToken ct = default);
    /// <summary>Checks a worker key with the Admin app; returns the worker name or null when the key is not valid for this CAD.</summary>
    Task<string?> VerifyWorkerKeyAsync(string key, CancellationToken ct = default);
}

/// <summary>One result file; <paramref name="Open"/> gives a fresh read stream (the caller disposes it).</summary>
public sealed record VaultFile(string Name, string Kind, long Size, Func<CancellationToken, Task<Stream>> Open);

/// <summary>Port to Smart Vault: sends the result files of a finished job.</summary>
public interface IVaultClient
{
    Task SendJobResultAsync(string projectId, string productCode, string jobId, IReadOnlyList<VaultFile> files, IReadOnlyDictionary<string, string> properties, CancellationToken ct = default);
}

