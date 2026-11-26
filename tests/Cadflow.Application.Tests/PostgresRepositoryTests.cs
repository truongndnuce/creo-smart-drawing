using Cadflow.Domain.Jobs;
using Cadflow.Infrastructure.Persistence;
using Npgsql;

namespace Cadflow.Application.Tests;

/// <summary>
/// Runs against a real PostgreSQL. Set CADFLOW_TEST_PG to a connection string whose Database is a throw-away one
/// (e.g. "Host=localhost;Database=cadflow_creo_test;Username=admin;Password=..."); the database is created when missing. Skipped otherwise.
/// </summary>
public class PostgresRepositoryTests
{
    private static readonly string? Cs = Environment.GetEnvironmentVariable("CADFLOW_TEST_PG");

    private static async Task<PostgresJobRepository?> Repo()
    {
        if (string.IsNullOrWhiteSpace(Cs)) return null;
        await PostgresDatabase.EnsureCreatedAsync(Cs);
        var db = NpgsqlDataSource.Create(Cs);
        await using (var c = db.CreateCommand("DELETE FROM jobs")) await c.ExecuteNonQueryAsync();
        return new PostgresJobRepository(db);
    }

    private static Job NewJob(string product = "box", string kind = JobKinds.Generate) =>
        Job.Create(Guid.NewGuid().ToString("N")[..12], "p1", product, kind, new Dictionary<string, double> { ["BX_Length"] = 100 }, "dev", DateTimeOffset.UtcNow);

    [Fact]
    public async Task Job_round_trips_and_keeps_its_history()
    {
        if (await Repo() is not { } repo) return;
        var job = NewJob();
        await repo.AddAsync(job);
        var claimed = await repo.ClaimNextAsync("w1", ["box"], null, DateTimeOffset.UtcNow);
        Assert.Equal(job.Id, claimed!.Id);
        claimed.ReportProgress("w1", "Building", "step 1", DateTimeOffset.UtcNow);
        claimed.AddArtifact("w1", new ArtifactInfo("a.step", "model", 10));
        claimed.Complete("w1", new Dictionary<string, double> { ["mass"] = 1.5 }, DateTimeOffset.UtcNow);
        await repo.SaveAsync(claimed);

        var back = await repo.GetAsync(job.Id);
        Assert.Equal(JobStatus.Succeeded, back!.Status);
        Assert.Equal(1.5, back.Summary["mass"]);
        Assert.Equal(100, back.Inputs["BX_Length"]);
        Assert.Single(back.Artifacts);
        Assert.True(back.Events.Count >= 3);
    }

    [Fact]
    public async Task Concurrent_workers_never_get_the_same_job()
    {
        if (await Repo() is not { } repo) return;
        for (var i = 0; i < 20; i++) await repo.AddAsync(NewJob());
        var got = await Task.WhenAll(Enumerable.Range(0, 40).Select(i => repo.ClaimNextAsync($"w{i}", null, null, DateTimeOffset.UtcNow)));
        var ids = got.Where(j => j is not null).Select(j => j!.Id).ToList();
        Assert.Equal(20, ids.Count);
        Assert.Equal(20, ids.Distinct().Count());
    }

    [Fact]
    public async Task Stale_running_jobs_go_back_to_the_queue()
    {
        if (await Repo() is not { } repo) return;
        var job = NewJob();
        await repo.AddAsync(job);
        await repo.ClaimNextAsync("w1", null, null, DateTimeOffset.UtcNow.AddMinutes(-10));
        Assert.Equal(1, await repo.ReclaimStaleAsync(TimeSpan.FromMinutes(2), DateTimeOffset.UtcNow));
        Assert.Equal(JobStatus.Queued, (await repo.GetAsync(job.Id))!.Status);
    }
}
