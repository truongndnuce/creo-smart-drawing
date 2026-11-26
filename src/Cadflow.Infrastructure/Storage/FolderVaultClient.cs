using System.Security.Cryptography;
using System.Text.Json;
using Cadflow.Application.Abstractions;

namespace Cadflow.Infrastructure.Storage;

/// <summary>Temporary Vault: writes &lt;root&gt;/&lt;project&gt;/&lt;product&gt;-&lt;jobId&gt;/{image,drawing,model,bom}/ plus manifest.json.
/// Idempotent: a file with the same content hash is left untouched, so resending never duplicates anything. Replaced by an HTTP client for the real Smart Vault app.</summary>
public sealed class FolderVaultClient(string root) : IVaultClient
{
    private static readonly Dictionary<string, string> Folders = new()
    { ["image"] = "image", ["drawing"] = "drawing", ["model"] = "model", ["step"] = "model", ["bom"] = "bom" };

    public async Task SendJobResultAsync(string projectId, string productCode, string jobId, IReadOnlyList<VaultFile> files,
        IReadOnlyDictionary<string, string> properties, CancellationToken ct = default)
    {
        var jobDir = Path.Combine(root, Safe(projectId), $"{Safe(productCode)}-{Safe(jobId)}");
        var manifest = new List<object>();
        foreach (var f in files)
        {
            if (!Folders.TryGetValue(f.Kind, out var sub)) continue;
            var dir = Path.Combine(jobDir, sub);
            Directory.CreateDirectory(dir);
            var target = Path.Combine(dir, Safe(f.Name));
            var tmp = target + "." + Guid.NewGuid().ToString("N") + ".part";
            try
            {
                await using (var src = await f.Open(ct))
                await using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
                    await src.CopyToAsync(dst, ct);
                var hash = await HashAsync(tmp, ct);
                if (File.Exists(target) && await HashAsync(target, ct) == hash) File.Delete(tmp); // already there
                else File.Move(tmp, target, overwrite: true);
                manifest.Add(new { name = f.Name, kind = f.Kind, folder = sub, size = new FileInfo(target).Length, sha256 = hash });
            }
            finally { if (File.Exists(tmp)) File.Delete(tmp); }
        }
        Directory.CreateDirectory(jobDir);
        await File.WriteAllTextAsync(Path.Combine(jobDir, "manifest.json"),
            JsonSerializer.Serialize(new { projectId, productCode, jobId, properties, files = manifest }, new JsonSerializerOptions { WriteIndented = true }), ct);
    }

    private static async Task<string> HashAsync(string path, CancellationToken ct)
    {
        await using var s = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(s, ct)).ToLowerInvariant();
    }

    private static string Safe(string s) =>
        s.Length == 0 || s is "." or ".." || s.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ? throw new ArgumentException($"Unsafe path part '{s}'.") : s;
}
