using Cadflow.Application.Abstractions;
using Cadflow.Application.Catalog;
using Cadflow.Application.Products;
using Cadflow.Domain.Jobs;

namespace Cadflow.Application.Jobs;

public sealed record CreateJobCommand(string ProjectId, string? ProductCode, Dictionary<string, double>? Inputs, string? Kind, Dictionary<string, string>? TextInputs = null);

/// <summary>Use case: a designer orders a product in a project.</summary>
public sealed class CreateJob(IJobRepository jobs, IProjectDirectory projects, ICurrentUser user, TimeProvider clock)
{
    public async Task<Result<Job>> ExecuteAsync(CreateJobCommand cmd, CancellationToken ct = default)
    {
        var project = await projects.FindForUserAsync(user.Id, cmd.ProjectId, ct);
        if (project is null) return Result<Job>.Fail(ErrorKind.NotFound, "Unknown project.");
        if (project.Role < ProjectRole.Designer) return Result<Job>.Fail(ErrorKind.Forbidden, "You need the Designer role to order.");

        var product = ProductCatalog.Find(string.IsNullOrWhiteSpace(cmd.ProductCode) ? "box" : cmd.ProductCode);
        if (product is null) return Result<Job>.Fail(ErrorKind.NotFound, $"Unknown product '{cmd.ProductCode}'.");
        if (!project.EnabledProducts.Contains(product.Code, StringComparer.OrdinalIgnoreCase))
            return Result<Job>.Fail(ErrorKind.Forbidden, $"Product '{product.Code}' is not enabled for this project.");

        var kind = string.IsNullOrWhiteSpace(cmd.Kind) ? JobKinds.Generate : cmd.Kind.Trim().ToLowerInvariant();
        if (kind is not (JobKinds.Generate or JobKinds.Preview))
            return Result<Job>.Fail(ErrorKind.Validation, $"Unknown job kind '{cmd.Kind}'. Use '{JobKinds.Generate}' or '{JobKinds.Preview}'.");

        var values = product.Complete(cmd.Inputs);
        var issues = product.Validate(values);
        if (issues.Count > 0)
            return Result<Job>.Fail(ErrorKind.Validation, "The order violates product rules.", issues.Select(i => new ParamIssue(i.Message, i.Params)).ToList());

        if (MaterialCatalog.Check(cmd.TextInputs) is { } badText) return Result<Job>.Fail(ErrorKind.Validation, badText);

        var job = Job.Create(Guid.NewGuid().ToString("N")[..12], project.Id, product.Code, kind, values, user.Name, clock.GetUtcNow(), MaterialCatalog.Pick(cmd.TextInputs));
        await jobs.AddAsync(job, ct);
        return Result<Job>.Success(job);
    }
}

/// <summary>Use cases: read jobs (only of projects the user belongs to).</summary>
public sealed class QueryJobs(IJobRepository jobs, IProjectDirectory projects, ICurrentUser user)
{
    public async Task<Result<Job>> GetAsync(string id, CancellationToken ct = default)
    {
        var job = await jobs.GetAsync(id, ct);
        if (job is null || await projects.FindForUserAsync(user.Id, job.ProjectId, ct) is null) return Result<Job>.Fail(ErrorKind.NotFound, "Unknown job.");
        return Result<Job>.Success(job);
    }

    public async Task<IReadOnlyList<Job>> ListAsync(JobFilter filter, CancellationToken ct = default)
    {
        var mine = (await projects.ListForUserAsync(user.Id, ct)).Select(p => p.Id).ToHashSet();
        if (filter.ProjectId is not null && !mine.Contains(filter.ProjectId)) return [];
        var all = await jobs.ListAsync(filter, ct);
        return all.Where(j => mine.Contains(j.ProjectId)).ToList();
    }
}

/// <summary>Use case: stop a waiting or running job (Designer or higher in its project).</summary>
public sealed class CancelJob(IJobRepository jobs, IProjectDirectory projects, ICurrentUser user, TimeProvider clock)
{
    public async Task<Result<Job>> ExecuteAsync(string id, CancellationToken ct = default)
    {
        var job = await jobs.GetAsync(id, ct);
        if (job is null || await projects.FindForUserAsync(user.Id, job.ProjectId, ct) is not { } project) return Result<Job>.Fail(ErrorKind.NotFound, "Unknown job.");
        if (project.Role < ProjectRole.Designer) return Result<Job>.Fail(ErrorKind.Forbidden, "You need the Designer role to cancel.");
        try { job.Cancel(clock.GetUtcNow()); }
        catch (DomainException ex) { return Result<Job>.Fail(ErrorKind.Conflict, ex.Message); }
        await jobs.SaveAsync(job, ct);
        return Result<Job>.Success(job);
    }
}
