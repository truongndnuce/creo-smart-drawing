using Cadflow.Application.Abstractions;
using Cadflow.Domain.Jobs;

namespace Cadflow.Infrastructure.Persistence;

/// <summary>Thread-safe in-memory store: used by tests and the sim run. PostgreSQL adapter comes next.</summary>
public sealed class InMemoryJobRepository : IJobRepository
{
    private readonly object _gate = new();
    private readonly List<Job> _jobs = new();

    public Task AddAsync(Job job, CancellationToken ct = default) { lock (_gate) _jobs.Add(job); return Task.CompletedTask; }

    public Task<Job?> GetAsync(string id, CancellationToken ct = default) { lock (_gate) return Task.FromResult(_jobs.FirstOrDefault(j => j.Id == id)); }

    public Task<IReadOnlyList<Job>> ListAsync(JobFilter f, CancellationToken ct = default)
    {
        lock (_gate)
        {
            IEnumerable<Job> q = _jobs;
            if (f.Status is not null) q = q.Where(j => string.Equals(j.Status.ToString(), f.Status, StringComparison.OrdinalIgnoreCase));
            if (f.ProjectId is not null) q = q.Where(j => j.ProjectId == f.ProjectId);
            if (f.Product is not null) q = q.Where(j => j.ProductCode == f.Product);
            if (f.RequestedBy is not null) q = q.Where(j => j.RequestedBy == f.RequestedBy);
            return Task.FromResult<IReadOnlyList<Job>>(q.OrderByDescending(j => j.CreatedAt).Take(f.Limit).ToList());
        }
    }

    public Task<Job?> ClaimNextAsync(string worker, IReadOnlyCollection<string>? products, IReadOnlyCollection<string>? queues, DateTimeOffset now, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var job = _jobs
                .Where(j => j.Status == JobStatus.Queued
                    && (products is not { Count: > 0 } || products.Contains(j.ProductCode, StringComparer.OrdinalIgnoreCase))
                    && (queues is not { Count: > 0 } || queues.Contains(j.Queue)))
                .OrderBy(j => j.Priority).ThenBy(j => j.CreatedAt)
                .FirstOrDefault();
            job?.Start(worker, now);
            return Task.FromResult(job);
        }
    }

    public Task SaveAsync(Job job, CancellationToken ct = default) => Task.CompletedTask; // same instance already held

    public Task<int> ReclaimStaleAsync(TimeSpan timeout, DateTimeOffset now, CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult(_jobs.Count(j => j.ReclaimIfStale(timeout, now)));
    }
}
