using Cadflow.Application.Abstractions;

namespace Cadflow.Infrastructure.Identity;

/// <summary>Development stand-in for the Entra identity: a fixed user, overridable per request with the X-Dev-User header (see the Api layer).</summary>
public sealed class DevCurrentUser(string id, string name) : ICurrentUser
{
    public string Id { get; } = id;
    public string Name { get; } = name;
}

public sealed record SeedProject(string Id, string Code, string Name, string CadSystem, Dictionary<string, ProjectRole> Members, string[] EnabledProducts);
public sealed record SeedData(List<SeedProject> Projects, Dictionary<string, string> WorkerKeys);

/// <summary>Development stand-in for the Admin app API: projects, roles and worker keys from a seed file. Only Creo projects are listed.</summary>
public sealed class SeedProjectDirectory(SeedData data, string cadSystem) : IProjectDirectory
{
    public Task<IReadOnlyList<ProjectInfo>> ListForUserAsync(string userId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ProjectInfo>>(data.Projects
            .Where(p => p.CadSystem == cadSystem && p.Members.ContainsKey(userId))
            .Select(p => new ProjectInfo(p.Id, p.Code, p.Name, p.CadSystem, p.Members[userId], p.EnabledProducts)).ToList());

    public async Task<ProjectInfo?> FindForUserAsync(string userId, string projectId, CancellationToken ct = default) =>
        (await ListForUserAsync(userId, ct)).FirstOrDefault(p => p.Id == projectId);

    public Task<string?> VerifyWorkerKeyAsync(string key, CancellationToken ct = default) =>
        Task.FromResult(data.WorkerKeys.TryGetValue(key, out var name) ? name : null);
}
