using Cadflow.Application.Abstractions;
using Cadflow.Domain.Jobs;

namespace Cadflow.Application.Jobs;

/// <summary>Artifact upload (worker) and download (user with access to the job's project).</summary>
public sealed class Artifacts(IJobRepository jobs, IArtifactStore store, IProjectDirectory projects, ICurrentUser user)
{
    public static readonly string[] Kinds = ["model", "step", "image", "drawing", "bom", "log", "other"];
    public const long MaxBytes = 200L * 1024 * 1024;

    /// <summary>A plain file name: no folders, no dots-only names, no control characters.</summary>
    public static bool IsSafeName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && name.Length <= 150 && name != "." && name != ".." && name == name.Trim()
        && name.IndexOfAny(['/', '\\', ':', '*', '?', '"', '<', '>', '|']) < 0 && !name.Any(char.IsControl);

    public async Task<Result<ArtifactInfo>> UploadAsync(string jobId, string worker, string? name, string? kind, Stream content, CancellationToken ct = default)
    {
        if (!IsSafeName(name)) return Result<ArtifactInfo>.Fail(ErrorKind.Validation, "Invalid file name.");
        kind = string.IsNullOrWhiteSpace(kind) ? "other" : kind.ToLowerInvariant();
        if (!Kinds.Contains(kind)) return Result<ArtifactInfo>.Fail(ErrorKind.Validation, $"Unknown kind '{kind}'.");
        var job = await jobs.GetAsync(jobId, ct);
        if (job is null) return Result<ArtifactInfo>.Fail(ErrorKind.NotFound, "Unknown job.");
        // Only the worker that owns the running job may attach files (checked before writing any bytes).
        if (job.Status != JobStatus.Running || job.Worker != worker)
            return Result<ArtifactInfo>.Fail(ErrorKind.Conflict, "Job is not running on this worker.");

        var size = await store.SaveAsync(jobId, name!, new LimitedStream(content, MaxBytes), ct);
        var info = new ArtifactInfo(name!, kind, size);
        job = await jobs.GetAsync(jobId, ct); // re-read: the job may have changed while the file was uploading
        try { job!.AddArtifact(worker, info); }
        catch (DomainException ex) { return Result<ArtifactInfo>.Fail(ErrorKind.Conflict, ex.Message); }
        await jobs.SaveAsync(job, ct);
        return Result<ArtifactInfo>.Success(info);
    }

    public async Task<Result<Stream>> OpenAsync(string jobId, string name, CancellationToken ct = default)
    {
        var job = await jobs.GetAsync(jobId, ct);
        if (job is null || await projects.FindForUserAsync(user.Id, job.ProjectId, ct) is null) return Result<Stream>.Fail(ErrorKind.NotFound, "Unknown job.");
        if (!job.Artifacts.Any(a => a.Name == name)) return Result<Stream>.Fail(ErrorKind.NotFound, "Unknown file.");
        var s = await store.OpenReadAsync(jobId, name, ct);
        return s is null ? Result<Stream>.Fail(ErrorKind.NotFound, "File is no longer stored.") : Result<Stream>.Success(s);
    }

    /// <summary>Stops reading after <paramref name="max"/> bytes and throws, so one request cannot fill the disk.</summary>
    private sealed class LimitedStream(Stream inner, long max) : Stream
    {
        private long _read;
        public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => Count(await inner.ReadAsync(buffer, ct));
        private int Count(int n) { _read += n; if (_read > max) throw new InvalidDataException("File too large."); return n; }
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long o, SeekOrigin so) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }
}

