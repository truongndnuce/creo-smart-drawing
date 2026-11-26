using Cadflow.Application.Abstractions;
using Cadflow.Application.Jobs;
using Cadflow.Domain.Jobs;
using Cadflow.Infrastructure.Identity;
using Cadflow.Infrastructure.Persistence;

namespace Cadflow.Application.Tests;

public class JobFlowTests
{
    private static readonly SeedData Seed = new(
        [new SeedProject("p1", "P1", "Project 1", "creo", new() { ["dev"] = ProjectRole.Designer, ["viewer"] = ProjectRole.Viewer }, ["box"]),
         new SeedProject("p2", "P2", "Project 2", "inventor", new() { ["dev"] = ProjectRole.Designer }, ["box"])],
        new() { ["key"] = "w1" });

    private sealed class Ctx(string user = "dev")
    {
        public readonly InMemoryJobRepository Repo = new();
        public readonly IProjectDirectory Dir = new SeedProjectDirectory(Seed, "creo");
        public readonly TimeProvider Clock = TimeProvider.System;
        public CreateJob Create => new(Repo, Dir, new DevCurrentUser(user, user), Clock);
        public WorkerProtocol Worker => new(Repo, Clock);
    }

    [Fact]
    public async Task Designer_creates_a_job_with_defaults_filled_in()
    {
        var c = new Ctx();
        var r = await c.Create.ExecuteAsync(new("p1", "box", new() { ["BX_Length"] = 120 }, null));
        Assert.True(r.Ok, r.Error?.Message);
        Assert.Equal(120, r.Value!.Inputs["BX_Length"]);
        Assert.Equal(60, r.Value.Inputs["BX_Width"]);
        Assert.Equal(JobStatus.Queued, r.Value.Status);
    }

    [Fact]
    public async Task Viewer_cannot_order()
    {
        var r = await new Ctx("viewer").Create.ExecuteAsync(new("p1", "box", null, null));
        Assert.Equal(ErrorKind.Forbidden, r.Error!.Kind);
    }

    [Fact]
    public async Task A_project_of_another_cad_is_not_visible()
    {
        var r = await new Ctx().Create.ExecuteAsync(new("p2", "box", null, null));
        Assert.Equal(ErrorKind.NotFound, r.Error!.Kind);
    }

    [Fact]
    public async Task Rule_violations_are_reported_per_parameter()
    {
        var r = await new Ctx().Create.ExecuteAsync(new("p1", "box", new() { ["BX_Wall"] = 9 }, null));
        Assert.Equal(ErrorKind.Validation, r.Error!.Kind);
        Assert.Contains(r.Error.Issues!, i => i.Params.Contains("BX_Wall"));
    }

    [Fact]
    public async Task Worker_claims_runs_and_completes_a_job()
    {
        var c = new Ctx();
        var job = (await c.Create.ExecuteAsync(new("p1", "box", null, null))).Value!;
        var item = await c.Worker.ClaimAsync("w1", ["box"], null);
        Assert.Equal(job.Id, item!.Id);
        Assert.Null(await c.Worker.ClaimAsync("w2", null, null)); // nothing left
        Assert.True((await c.Worker.ProgressAsync(job.Id, "w1", "Building", null)).Ok);
        Assert.True((await c.Worker.CompleteAsync(job.Id, "w1", new Dictionary<string, double> { ["mass"] = 1 })).Ok);
        Assert.Equal(JobStatus.Succeeded, (await c.Repo.GetAsync(job.Id))!.Status);
    }

    [Fact]
    public async Task Retryable_failure_requeues_until_attempts_run_out()
    {
        var c = new Ctx();
        var job = (await c.Create.ExecuteAsync(new("p1", "box", null, null))).Value!;
        for (var i = 1; i <= Job.MaxAttempts; i++)
        {
            Assert.NotNull(await c.Worker.ClaimAsync("w1", null, null));
            await c.Worker.FailAsync(job.Id, "w1", "Creo crashed", retryable: true);
        }
        Assert.Equal(JobStatus.Failed, (await c.Repo.GetAsync(job.Id))!.Status);
    }

    [Fact]
    public async Task Another_worker_cannot_finish_a_job()
    {
        var c = new Ctx();
        var job = (await c.Create.ExecuteAsync(new("p1", "box", null, null))).Value!;
        await c.Worker.ClaimAsync("w1", null, null);
        Assert.Equal(ErrorKind.Conflict, (await c.Worker.CompleteAsync(job.Id, "w2", null)).Error!.Kind);
    }

    [Fact]
    public async Task Preview_jobs_are_handed_out_before_generate_jobs()
    {
        var c = new Ctx();
        var gen = (await c.Create.ExecuteAsync(new("p1", "box", null, "generate"))).Value!;
        var prev = (await c.Create.ExecuteAsync(new("p1", "box", null, "preview"))).Value!;
        Assert.Equal(prev.Id, (await c.Worker.ClaimAsync("w1", null, null))!.Id);
        Assert.Equal(gen.Id, (await c.Worker.ClaimAsync("w1", null, null))!.Id);
    }
}
