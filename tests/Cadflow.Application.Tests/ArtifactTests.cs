using System.Text;
using Cadflow.Application.Abstractions;
using Cadflow.Application.Jobs;
using Cadflow.Domain.Jobs;
using Cadflow.Infrastructure.Identity;
using Cadflow.Infrastructure.Persistence;
using Cadflow.Infrastructure.Storage;

namespace Cadflow.Application.Tests;

public class ArtifactTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cadflow-art-" + Guid.NewGuid().ToString("N"));
    private readonly InMemoryJobRepository _repo = new();
    private readonly SeedProjectDirectory _dir2 = new(new(
        [new SeedProject("p1", "P1", "P1", "creo", new() { ["dev"] = ProjectRole.Designer, ["viewer"] = ProjectRole.Viewer }, ["box"])],
        new() { ["k"] = "w1" }), "creo");

    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    private Artifacts Svc(string user = "dev") => new(_repo, new FolderArtifactStore(_dir), _dir2, new DevCurrentUser(user, user));
    private static MemoryStream Bytes(string s) => new(Encoding.UTF8.GetBytes(s));

    private async Task<string> RunningJob(string worker = "w1")
    {
        var c = new CreateJob(_repo, _dir2, new DevCurrentUser("dev", "dev"), TimeProvider.System);
        var id = (await c.ExecuteAsync(new("p1", "box", null, null))).Value!.Id;
        await new WorkerProtocol(_repo, TimeProvider.System).ClaimAsync(worker, null, null);
        return id;
    }

    [Fact]
    public async Task Worker_uploads_then_user_downloads()
    {
        var id = await RunningJob();
        var up = await Svc().UploadAsync(id, "w1", "box.step", "step", Bytes("hello"));
        Assert.True(up.Ok, up.Error?.Message);
        Assert.Equal(5, up.Value!.Size);
        Assert.Contains(_repo.GetAsync(id).Result!.Artifacts, a => a.Name == "box.step" && a.Kind == "step");

        var dl = await Svc("viewer").OpenAsync(id, "box.step");
        Assert.True(dl.Ok);
        using var r = new StreamReader(dl.Value!);
        Assert.Equal("hello", await r.ReadToEndAsync());
    }

    [Theory]
    [InlineData("../evil.txt")] [InlineData("a/b.txt")] [InlineData("..")] [InlineData("c:x.txt")] [InlineData("")]
    public async Task Unsafe_names_are_rejected(string name)
    {
        var id = await RunningJob();
        var r = await Svc().UploadAsync(id, "w1", name, "other", Bytes("x"));
        Assert.Equal(ErrorKind.Validation, r.Error!.Kind);
    }

    [Fact]
    public async Task Another_worker_cannot_upload()
    {
        var id = await RunningJob("w1");
        var r = await Svc().UploadAsync(id, "w2", "a.txt", "other", Bytes("x"));
        Assert.Equal(ErrorKind.Conflict, r.Error!.Kind);
        Assert.False(Directory.Exists(Path.Combine(_dir, id))); // nothing written
    }

    [Fact]
    public async Task Oversized_upload_is_cut_off_and_not_recorded()
    {
        var id = await RunningJob();
        var big = new MemoryStream(new byte[(int)Artifacts.MaxBytes + 1]);
        await Assert.ThrowsAsync<InvalidDataException>(() => Svc().UploadAsync(id, "w1", "big.bin", "other", big));
        Assert.Empty(_repo.GetAsync(id).Result!.Artifacts);
        Assert.False(File.Exists(Path.Combine(_dir, id, "big.bin")));
    }

    [Fact]
    public async Task Unknown_file_and_outsider_get_not_found()
    {
        var id = await RunningJob();
        await Svc().UploadAsync(id, "w1", "a.txt", "other", Bytes("x"));
        Assert.Equal(ErrorKind.NotFound, (await Svc().OpenAsync(id, "nope.txt")).Error!.Kind);
        Assert.Equal(ErrorKind.NotFound, (await Svc("stranger").OpenAsync(id, "a.txt")).Error!.Kind);
    }
}

public class VaultTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cadflow-vault-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private sealed class FailingVault : IVaultClient
    {
        public Task SendJobResultAsync(string p, string pr, string j, IReadOnlyList<VaultFile> f, IReadOnlyDictionary<string, string> props, CancellationToken ct = default) => throw new IOException("vault down");
    }

    private (InMemoryJobRepository repo, SeedProjectDirectory dir, FolderArtifactStore store) Setup() => (new(), new(new(
        [new SeedProject("p1", "P1", "P1", "creo", new() { ["dev"] = ProjectRole.Designer, ["viewer"] = ProjectRole.Viewer }, ["box"])], new() { ["k"] = "w1" }), "creo"),
        new FolderArtifactStore(Path.Combine(_root, "art")));

    private async Task<string> FinishedJob(InMemoryJobRepository repo, SeedProjectDirectory dir, FolderArtifactStore store, IVaultClient vault, string kind = "generate")
    {
        var user = new DevCurrentUser("dev", "dev");
        var id = (await new CreateJob(repo, dir, user, TimeProvider.System).ExecuteAsync(new("p1", "box", null, kind))).Value!.Id;
        var wp = new WorkerProtocol(repo, TimeProvider.System, new VaultPublisher(repo, store, vault, TimeProvider.System));
        await wp.ClaimAsync("w1", null, null);
        var art = new Artifacts(repo, store, dir, user);
        await art.UploadAsync(id, "w1", "box.step", "step", new MemoryStream("STEP"u8.ToArray()));
        await art.UploadAsync(id, "w1", "box.png", "image", new MemoryStream("PNG"u8.ToArray()));
        await art.UploadAsync(id, "w1", "run.log", "log", new MemoryStream("log"u8.ToArray()));
        var r = await wp.CompleteAsync(id, "w1", new Dictionary<string, double> { ["mass"] = 0.2 });
        Assert.True(r.Ok);
        return id;
    }

    [Fact]
    public async Task Completed_job_lands_in_the_outbox_with_a_manifest_and_no_log()
    {
        var (repo, dir, store) = Setup();
        var id = await FinishedJob(repo, dir, store, new FolderVaultClient(Path.Combine(_root, "vault")));
        var jobDir = Path.Combine(_root, "vault", "p1", $"box-{id}");
        Assert.True(File.Exists(Path.Combine(jobDir, "model", "box.step")));
        Assert.True(File.Exists(Path.Combine(jobDir, "image", "box.png")));
        Assert.False(File.Exists(Path.Combine(jobDir, "other", "run.log")));
        Assert.Contains("\"sha256\"", await File.ReadAllTextAsync(Path.Combine(jobDir, "manifest.json")));
        Assert.Contains(repo.GetAsync(id).Result!.Events, e => e.Message.StartsWith("Đã gửi 2 file"));
    }

    [Fact]
    public async Task Sending_twice_does_not_duplicate_files()
    {
        var (repo, dir, store) = Setup();
        var vault = new FolderVaultClient(Path.Combine(_root, "vault"));
        var id = await FinishedJob(repo, dir, store, vault);
        var pub = new VaultPublisher(repo, store, vault, TimeProvider.System);
        Assert.True((await pub.PublishAsync(id)).Ok);
        Assert.Equal(2, Directory.GetFiles(Path.Combine(_root, "vault", "p1", $"box-{id}"), "*", SearchOption.AllDirectories).Count(f => !f.EndsWith("manifest.json")));
    }

    [Fact]
    public async Task Vault_failure_does_not_fail_the_job_and_can_be_resent()
    {
        var (repo, dir, store) = Setup();
        var id = await FinishedJob(repo, dir, store, new FailingVault());
        var job = (await repo.GetAsync(id))!;
        Assert.Equal(JobStatus.Succeeded, job.Status);
        Assert.Contains(job.Events, e => e.Message.StartsWith("Gửi Vault lỗi"));
        var good = new VaultPublisher(repo, store, new FolderVaultClient(Path.Combine(_root, "vault")), TimeProvider.System);
        Assert.True((await good.ResendAsync(id, dir, new DevCurrentUser("dev", "dev"))).Ok);
        Assert.Equal(ErrorKind.Forbidden, (await good.ResendAsync(id, dir, new DevCurrentUser("viewer", "viewer"))).Error!.Kind);
    }

    [Fact]
    public async Task Previews_are_not_archived()
    {
        var (repo, dir, store) = Setup();
        var id = await FinishedJob(repo, dir, store, new FolderVaultClient(Path.Combine(_root, "vault")), "preview");
        Assert.False(Directory.Exists(Path.Combine(_root, "vault")));
    }
}
