using Cadflow.Application.Abstractions;
using Cadflow.Domain.Jobs;

namespace Cadflow.Application.Jobs;

/// <summary>What the worker needs to build: no more.</summary>
public sealed record WorkItem(string Id, string ProductCode, string Kind, IReadOnlyDictionary<string, double> Inputs);

/// <summary>Use cases of the worker protocol (heartbeat/claim/progress/complete/fail). The worker always calls the server.</summary>
public sealed class WorkerProtocol(IJobRepository jobs, TimeProvider clock, VaultPublisher? vault = null)
{
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(2);

    public async Task<WorkItem?> ClaimAsync(string worker, string[]? products, string[]? queues, CancellationToken ct = default)
    {
        await jobs.ReclaimStaleAsync(StaleAfter, clock.GetUtcNow(), ct);
        var job = await jobs.ClaimNextAsync(worker, products, queues, clock.GetUtcNow(), ct);
        return job is null ? null : new WorkItem(job.Id, job.ProductCode, job.Kind, job.Inputs);
    }

    public Task<Result<Job>> ProgressAsync(string id, string worker, string? step, string? message, CancellationToken ct = default) =>
        Mutate(id, j => j.ReportProgress(worker, step, message, clock.GetUtcNow()), ct);

    public Task<Result<Job>> AddArtifactAsync(string id, string worker, ArtifactInfo artifact, CancellationToken ct = default) =>
        Mutate(id, j => j.AddArtifact(worker, artifact), ct);

    public async Task<Result<Job>> CompleteAsync(string id, string worker, IReadOnlyDictionary<string, double>? summary, CancellationToken ct = default)
    {
        var r = await Mutate(id, j => j.Complete(worker, summary ?? new Dictionary<string, double>(), clock.GetUtcNow()), ct);
        if (r.Ok && vault is not null && r.Value!.Kind != JobKinds.Preview) await vault.PublishAsync(id, ct); // previews are not archived
        return r.Ok ? Result<Job>.Success((await jobs.GetAsync(id, ct))!) : r;
    }

    public Task<Result<Job>> FailAsync(string id, string worker, string? error, bool retryable, CancellationToken ct = default) =>
        Mutate(id, j => j.Fail(worker, string.IsNullOrWhiteSpace(error) ? "Unknown error." : error!, retryable, clock.GetUtcNow()), ct);

    private async Task<Result<Job>> Mutate(string id, Action<Job> change, CancellationToken ct)
    {
        var job = await jobs.GetAsync(id, ct);
        if (job is null) return Result<Job>.Fail(ErrorKind.NotFound, "Unknown job.");
        try { change(job); }
        catch (DomainException ex) { return Result<Job>.Fail(ErrorKind.Conflict, ex.Message); }
        await jobs.SaveAsync(job, ct);
        return Result<Job>.Success(job);
    }
}

