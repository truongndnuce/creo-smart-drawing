using Cadflow.Application.Abstractions;
using Cadflow.Domain.Jobs;

namespace Cadflow.Application.Jobs;

/// <summary>Sends the result files of a finished job to the Smart Vault. Safe to repeat: the Vault side is idempotent by job id and file hash.</summary>
public sealed class VaultPublisher(IJobRepository jobs, IArtifactStore store, IVaultClient vault, TimeProvider clock)
{
    /// <summary>Records the outcome in the job log. Never throws for Vault problems: a failed send must not fail a finished job.</summary>
    public async Task<Result<int>> PublishAsync(string jobId, CancellationToken ct = default)
    {
        var job = await jobs.GetAsync(jobId, ct);
        if (job is null) return Result<int>.Fail(ErrorKind.NotFound, "Unknown job.");
        if (job.Status != JobStatus.Succeeded) return Result<int>.Fail(ErrorKind.Conflict, "Only a finished job can be sent to the Vault.");
        var files = job.Artifacts.Where(a => a.Kind != "log")
            .Select(a => new VaultFile(a.Name, a.Kind, a.Size, async c => await store.OpenReadAsync(job.Id, a.Name, c) ?? throw new FileNotFoundException(a.Name)))
            .ToList();
        var props = new Dictionary<string, string> { ["product"] = job.ProductCode, ["requestedBy"] = job.RequestedBy, ["finishedAt"] = job.FinishedAt?.ToString("O") ?? "" };
        foreach (var (k, v) in job.Summary) props["summary." + k] = v.ToString(System.Globalization.CultureInfo.InvariantCulture);
        foreach (var (k, v) in job.Inputs) props["input." + k] = v.ToString(System.Globalization.CultureInfo.InvariantCulture);
        try
        {
            await vault.SendJobResultAsync(job.ProjectId, job.ProductCode, job.Id, files, props, ct);
            job.Note($"Đã gửi {files.Count} file sang Vault.", clock.GetUtcNow());
            await jobs.SaveAsync(job, ct);
            return Result<int>.Success(files.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            job.Note($"Gửi Vault lỗi: {ex.Message}", clock.GetUtcNow());
            await jobs.SaveAsync(job, ct);
            return Result<int>.Fail(ErrorKind.Conflict, $"Vault: {ex.Message}");
        }
    }

    /// <summary>User-triggered resend: needs Designer or higher in the job's project.</summary>
    public async Task<Result<int>> ResendAsync(string jobId, IProjectDirectory projects, ICurrentUser user, CancellationToken ct = default)
    {
        var job = await jobs.GetAsync(jobId, ct);
        var project = job is null ? null : await projects.FindForUserAsync(user.Id, job.ProjectId, ct);
        if (project is null) return Result<int>.Fail(ErrorKind.NotFound, "Unknown job.");
        if (project.Role < ProjectRole.Designer) return Result<int>.Fail(ErrorKind.Forbidden, "Designer role required.");
        return await PublishAsync(jobId, ct);
    }
}
