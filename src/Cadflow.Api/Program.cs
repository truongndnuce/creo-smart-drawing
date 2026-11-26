using System.Text.Json;
using System.Text.Json.Serialization;
using Cadflow.Application.Abstractions;
using Cadflow.Application.Catalog;
using Cadflow.Application.Components;
using Cadflow.Application.Configs;
using Cadflow.Application.Jobs;
using Cadflow.Application.Products;
using Cadflow.Domain.Jobs;
using Cadflow.Infrastructure.Identity;
using Cadflow.Infrastructure.Persistence;
using Cadflow.Infrastructure.Storage;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
});

// Composition root: the only place that knows the concrete adapters.
builder.Services.AddSingleton(TimeProvider.System);
// Own database per app. No connection string = in-memory (tests / quick demo).
var cs = builder.Configuration.GetConnectionString("CadflowCreo");
if (string.IsNullOrWhiteSpace(cs))
{
    builder.Services.AddSingleton<IJobRepository, InMemoryJobRepository>();
    builder.Services.AddSingleton<IConfigRepository, InMemoryConfigRepository>();
}
else
{
    await PostgresDatabase.EnsureCreatedAsync(cs); // creates cadflow_creo when missing, then migrates
    builder.Services.AddSingleton(NpgsqlDataSource.Create(cs));
    builder.Services.AddSingleton<IJobRepository, PostgresJobRepository>();
    builder.Services.AddSingleton<IConfigRepository, PostgresConfigRepository>();
}
builder.Services.AddSingleton<ProductConfigService>();
builder.Services.AddSingleton<WorkerRegistry>();
builder.Services.AddSingleton(_ => ComponentCatalog.FromFolder(Path.Combine(AppContext.BaseDirectory, "Scripts", "lib")));
builder.Services.AddSingleton<IProjectDirectory>(_ =>
{
    var path = Path.Combine(AppContext.BaseDirectory, "seed", "projects.json");
    var seed = JsonSerializer.Deserialize<SeedData>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true, Converters = { new JsonStringEnumConverter() } })!;
    return new SeedProjectDirectory(seed, cadSystem: "creo");
});
builder.Services.AddSingleton<IArtifactStore>(_ => new FolderArtifactStore(
    builder.Configuration["Storage:ArtifactsPath"] is { Length: > 0 } p ? p : Path.Combine(builder.Environment.ContentRootPath, "data", "artifacts")));
builder.Services.AddSingleton<IVaultClient>(_ => new FolderVaultClient(
    builder.Configuration["Vault:OutboxPath"] is { Length: > 0 } v ? v : Path.Combine(builder.Environment.ContentRootPath, "data", "vault-outbox")));
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser>(sp =>
{
    // Development sign-in only: the header, or ?devUser= for plain <img>/<a> requests that cannot send headers. Entra ID replaces this.
    var req = sp.GetRequiredService<IHttpContextAccessor>().HttpContext?.Request;
    var id = req?.Headers["X-Dev-User"].FirstOrDefault() ?? req?.Query["devUser"].FirstOrDefault() ?? "dev";
    return new DevCurrentUser(id, id);
});
builder.Services.AddScoped<CreateJob>();
builder.Services.AddScoped<QueryJobs>();
builder.Services.AddScoped<CancelJob>();
builder.Services.AddScoped<WorkerProtocol>();
builder.Services.AddScoped<Artifacts>();
builder.Services.AddScoped<VaultPublisher>();
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = Artifacts.MaxBytes);

var app = builder.Build();

// Web order: the built fe/ folder (config Web:Root overrides). Absent in API-only deployments.
var webRoot = Path.GetFullPath(builder.Configuration["Web:Root"] is { Length: > 0 } w ? w : Path.Combine(builder.Environment.ContentRootPath, "..", "..", "fe", "dist"));
if (Directory.Exists(webRoot))
{
    var files = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(webRoot);
    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
    app.UseStaticFiles(new StaticFileOptions { FileProvider = files });
}

static IResult ToHttp<T>(Result<T> r, Func<T, IResult> ok) => r.Ok ? ok(r.Value!) : r.Error!.Kind switch
{
    ErrorKind.NotFound => Results.NotFound(new { error = r.Error.Message }),
    ErrorKind.Forbidden => Results.Json(new { error = r.Error.Message }, statusCode: 403),
    ErrorKind.Conflict => Results.Conflict(new { error = r.Error.Message }),
    _ => Results.BadRequest(new { error = r.Error.Message, issues = r.Error.Issues?.Select(i => new { message = i.Message, @params = i.Params }) }),
};

app.MapGet("/api/health", () => new { ok = true });

app.MapGet("/api/projects", async (IProjectDirectory dir, ICurrentUser user) => await dir.ListForUserAsync(user.Id));

app.MapGet("/api/products", () => ProductCatalog.All.Select(p => new { code = p.Code, name = p.Name, description = p.Description, version = p.Version }));
// ?config=<id>: the defaults of the form come from that Configuration instead of the factory values.
app.MapGet("/api/products/{code}", async (string code, string? config, ProductConfigService cfgs) =>
{
    if (ProductCatalog.Find(code) is not { } p) return Results.NotFound();
    var factory = p.Complete(null);
    var defaults = config is { Length: > 0 } && await cfgs.GetAsync(p, config) is { } c ? p.Complete(c.Values) : factory;
    return Results.Ok(new
    {
        code = p.Code, name = p.Name, description = p.Description, version = p.Version,
        parameters = p.Parameters, groups = p.Parameters.Select(x => x.Group).Distinct(), defaults, factory,
    });
});
app.MapPost("/api/products/{code}/validate", (string code, ValidateRequest req) =>
{
    if (ProductCatalog.Find(code) is not { } p) return Results.NotFound();
    var issues = p.Validate(p.Complete(req.Values)).Select(i => new { message = i.Message, @params = i.Params }).ToList();
    return Results.Ok(new { valid = issues.Count == 0, issues });
});

// Configurations: named sets of values (+ operation / CAD conditions) an order can start from.
app.MapGet("/api/config-schema", () => new { statuses = ConfigSchema.Statuses, groups = ConfigSchema.Groups });
app.MapGet("/api/products/{code}/configs", async (string code, ProductConfigService cfgs) =>
    ProductCatalog.Find(code) is { } p ? Results.Ok(await cfgs.ListAsync(p)) : Results.NotFound());
IResult ConfigHttp(ConfigResult r) => r.NotFound ? Results.NotFound(new { error = r.Error }) : r.Error is not null ? Results.BadRequest(new { error = r.Error }) : Results.Ok(r.Config);
app.MapPost("/api/products/{code}/configs", async (string code, ConfigRequest req, ProductConfigService cfgs, ICurrentUser user) =>
    ProductCatalog.Find(code) is { } p ? ConfigHttp(await cfgs.SaveAsync(p, null, req, user.Name)) : Results.NotFound());
app.MapPut("/api/products/{code}/configs/{id}", async (string code, string id, ConfigRequest req, ProductConfigService cfgs, ICurrentUser user) =>
    ProductCatalog.Find(code) is { } p ? ConfigHttp(await cfgs.SaveAsync(p, id, req, user.Name)) : Results.NotFound());
app.MapDelete("/api/products/{code}/configs/{id}", async (string code, string id, ProductConfigService cfgs) =>
    ProductCatalog.Find(code) is { } p ? (await cfgs.DeleteAsync(p, id) ? Results.NoContent() : Results.NotFound()) : Results.NotFound());

app.MapGet("/api/materials", () => new { materials = MaterialCatalog.Materials, finishes = MaterialCatalog.Finishes });

// Components: reusable libraries (Scripts/lib/*.gea).
app.MapGet("/api/components", (ComponentCatalog catalog) => catalog.List());
app.MapGet("/api/components/{name}/gea", (string name, ComponentCatalog catalog) =>
    catalog.Raw(name) is { } text ? Results.Text(text, "text/plain; charset=utf-8") : Results.NotFound(new { error = $"Unknown component '{name}'." }));
app.MapGet("/api/components/{name}/starter", (string name, string? productName, string? prefix, ComponentCatalog catalog) =>
    catalog.Starter(name, productName, prefix) is { Text: { } text } ? Results.Text(text, "text/plain; charset=utf-8")
        : Results.Json(new { error = catalog.Starter(name, productName, prefix).Error }, statusCode: catalog.Starter(name, productName, prefix).Status));

app.MapGet("/api/workers", (WorkerRegistry workers) => workers.List());

app.MapPost("/api/jobs", async (CreateJobRequest req, CreateJob uc) =>
    ToHttp(await uc.ExecuteAsync(new CreateJobCommand(req.ProjectId ?? "", req.ProductCode, req.Inputs, req.Kind, req.TextInputs)), j => Results.Created($"/api/jobs/{j.Id}", j)));
app.MapGet("/api/jobs/{id}", async (string id, QueryJobs q) => ToHttp(await q.GetAsync(id), j => Results.Ok(j)));
app.MapPost("/api/jobs/{id}/cancel", async (string id, CancelJob uc) => ToHttp(await uc.ExecuteAsync(id), j => Results.Ok(j)));

// Filters: ?status=queued,running &kind=generate,export &product= &requestedBy= &projectId= &limit=. Newest first.
static HashSet<string>? List(string? s) => string.IsNullOrWhiteSpace(s) ? null
    : s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(x => x.ToLowerInvariant()).ToHashSet();
static IEnumerable<Cadflow.Domain.Jobs.Job> Narrow(IEnumerable<Cadflow.Domain.Jobs.Job> jobs, string? status, string? kind, string? requestedBy)
{
    if (List(status) is { } st) jobs = jobs.Where(j => st.Contains(j.Status.ToString().ToLowerInvariant()));
    if (List(kind) is { } k) jobs = jobs.Where(j => k.Contains(j.Kind));
    if (!string.IsNullOrWhiteSpace(requestedBy)) jobs = jobs.Where(j => string.Equals(j.RequestedBy, requestedBy.Trim(), StringComparison.OrdinalIgnoreCase));
    return jobs;
}
app.MapGet("/api/jobs", async (string? status, string? kind, string? projectId, string? product, string? requestedBy, int? limit, QueryJobs q) =>
    Narrow(await q.ListAsync(new JobFilter(null, projectId, product, null, 500)), status, kind, requestedBy).Take(Math.Clamp(limit ?? 100, 1, 500)));

// Dashboard numbers over the jobs created within the last ?days= days.
app.MapGet("/api/stats", async (int? days, string? projectId, string? product, string? kind, string? requestedBy, QueryJobs q, TimeProvider clock) =>
    JobStats.Compute(Narrow(await q.ListAsync(new JobFilter(null, projectId, product, null, 500)), null, kind, requestedBy), days ?? 14, clock.GetUtcNow()));

app.MapGet("/api/jobs/{id}/artifacts/{name}", async (string id, string name, Artifacts a) =>
    ToHttp(await a.OpenAsync(id, name), s => Results.File(s, System.Net.Mime.MediaTypeNames.Application.Octet, name)));

app.MapPost("/api/jobs/{id}/vault", async (string id, VaultPublisher v, IProjectDirectory dir, ICurrentUser user) =>
    ToHttp(await v.ResendAsync(id, dir, user), n => Results.Ok(new { sent = n })));

// Worker protocol: same routes as the Inventor server. The worker is identified by its key (checked with the Admin app).
var worker = app.MapGroup("/api/worker");
async Task<string?> WorkerName(HttpRequest r, IProjectDirectory dir)
{
    var name = r.Headers["X-Worker-Key"].FirstOrDefault() is { Length: > 0 } key ? await dir.VerifyWorkerKeyAsync(key) : null;
    if (name is not null) r.HttpContext.RequestServices.GetRequiredService<WorkerRegistry>().Touch(name); // every call proves the worker is alive
    return name;
}

worker.MapPost("/claim", async (HttpRequest http, ClaimRequest req, IProjectDirectory dir, WorkerProtocol wp) =>
    await WorkerName(http, dir) is not { } name ? Results.Unauthorized()
        : await wp.ClaimAsync(name, req.Products, req.Queues) is { } item ? Results.Ok(item) : Results.NoContent());
worker.MapPost("/jobs/{id}/progress", async (string id, HttpRequest http, ProgressRequest req, IProjectDirectory dir, WorkerProtocol wp) =>
    await WorkerName(http, dir) is not { } name ? Results.Unauthorized() : ToHttp(await wp.ProgressAsync(id, name, req.Step, req.Message), _ => Results.NoContent()));
worker.MapPut("/jobs/{id}/artifacts/{name}", async (string id, string name, string? kind, HttpRequest http, IProjectDirectory dir, Artifacts a) =>
{
    if (await WorkerName(http, dir) is not { } w) return Results.Unauthorized();
    try { return ToHttp(await a.UploadAsync(id, w, name, kind, http.Body, http.HttpContext.RequestAborted), i => Results.Ok(i)); }
    catch (InvalidDataException ex) { return Results.Json(new { error = ex.Message }, statusCode: 413); }
});
worker.MapPost("/jobs/{id}/complete", async (string id, HttpRequest http, CompleteRequest req, IProjectDirectory dir, WorkerProtocol wp) =>
    await WorkerName(http, dir) is not { } name ? Results.Unauthorized() : ToHttp(await wp.CompleteAsync(id, name, req.Summary), _ => Results.NoContent()));
worker.MapPost("/jobs/{id}/fail", async (string id, HttpRequest http, FailRequest req, IProjectDirectory dir, WorkerProtocol wp) =>
    await WorkerName(http, dir) is not { } name ? Results.Unauthorized() : ToHttp(await wp.FailAsync(id, name, req.Error, req.Retryable), _ => Results.NoContent()));

app.Run();

public sealed record CreateJobRequest(string? ProjectId, string? ProductCode, Dictionary<string, double>? Inputs, string? Kind, Dictionary<string, string>? TextInputs = null);
public sealed record ValidateRequest(Dictionary<string, double>? Values);
public sealed record ClaimRequest(string[]? Products, string[]? Queues);
public sealed record ProgressRequest(string? Step, string? Message);
public sealed record CompleteRequest(Dictionary<string, double>? Summary);
public sealed record FailRequest(string? Error, bool Retryable);

public partial class Program;




