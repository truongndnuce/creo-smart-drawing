namespace Cadflow.Application.Abstractions;

/// <summary>Where artifact bytes live (temporary store of this app; the Vault gets its own copy via IVaultClient).</summary>
public interface IArtifactStore
{
    /// <summary>Writes the stream (replacing any previous file with the same name) and returns the number of bytes stored.</summary>
    Task<long> SaveAsync(string jobId, string name, Stream content, CancellationToken ct = default);
    /// <summary>Null when the file does not exist.</summary>
    Task<Stream?> OpenReadAsync(string jobId, string name, CancellationToken ct = default);
}
