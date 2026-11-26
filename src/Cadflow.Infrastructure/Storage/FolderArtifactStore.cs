using Cadflow.Application.Abstractions;

namespace Cadflow.Infrastructure.Storage;

/// <summary>Artifacts as plain files: &lt;root&gt;/&lt;jobId&gt;/&lt;name&gt;. Written to a temp file first so a half-uploaded file is never visible.</summary>
public sealed class FolderArtifactStore(string root) : IArtifactStore
{
    private string PathOf(string jobId, string name)
    {
        // Defence in depth: the use case already validates names, the adapter refuses to leave its root anyway.
        var full = Path.GetFullPath(Path.Combine(root, jobId, name));
        var rootFull = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase) || Path.GetFileName(full) != name || jobId.AsSpan().IndexOfAny('/', '\\') >= 0)
            throw new ArgumentException("Path escapes the artifact store.");
        return full;
    }

    public async Task<long> SaveAsync(string jobId, string name, Stream content, CancellationToken ct = default)
    {
        var path = PathOf(jobId, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + "." + Guid.NewGuid().ToString("N") + ".part";
        try
        {
            await using (var f = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                await content.CopyToAsync(f, ct);
            File.Move(tmp, path, overwrite: true);
            return new FileInfo(path).Length;
        }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
    }

    public Task<Stream?> OpenReadAsync(string jobId, string name, CancellationToken ct = default)
    {
        var path = PathOf(jobId, name);
        return Task.FromResult<Stream?>(File.Exists(path) ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true) : null);
    }
}
