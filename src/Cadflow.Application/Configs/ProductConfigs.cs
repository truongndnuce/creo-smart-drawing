using System.Globalization;
using Cadflow.Application.Products;

namespace Cadflow.Application.Configs;

/// <summary>One input field of the Operation / CAD design condition groups (the UI builds the form and the comparison matrix from this list).</summary>
public sealed record ConfigField(string Name, string Label, string Type, string[]? Options = null, string Default = "", string Unit = "");

public sealed record ConfigGroup(string Key, string Label, ConfigField[] Fields);

/// <summary>The groups of information of a Configuration besides the component values: Operation and CAD design condition.</summary>
public static class ConfigSchema
{
    public static readonly string[] Statuses = ["draft", "active", "deprecated"];

    public static readonly ConfigGroup[] Groups =
    [
        new("operation", "Operation",
        [
            new("mode", "Operating mode", "select", ["Continuous", "Intermittent", "Standby"], "Continuous"),
            new("frequency", "Rated frequency", "select", ["50 Hz", "60 Hz"], "50 Hz"),
            new("ambientMin", "Ambient temperature, min", "number", Default: "-10", Unit: "°C"),
            new("ambientMax", "Ambient temperature, max", "number", Default: "40", Unit: "°C"),
            new("altitude", "Site altitude", "number", Default: "1000", Unit: "m"),
            new("installation", "Installation", "select", ["Indoor", "Outdoor"], "Outdoor"),
            new("designLife", "Design life", "number", Default: "25", Unit: "years"),
        ]),
        new("cad", "CAD design condition",
        [
            new("standard", "Drawing standard", "select", ["ISO", "ANSI", "DIN", "JIS", "GB"], "ISO"),
            new("projection", "Projection", "select", ["First angle", "Third angle"], "First angle"),
            new("units", "Units", "select", ["mm", "inch"], "mm"),
            new("sheet", "Sheet size", "select", ["A0", "A1", "A2", "A3", "A4"], "A3"),
            new("scale", "View scale", "select", ["1:1", "1:2", "1:5", "1:10", "1:20", "1:50", "1:100"], "1:10"),
            new("tolerance", "General tolerance", "select", ["ISO 2768-f", "ISO 2768-m", "ISO 2768-c"], "ISO 2768-m"),
            new("exports", "Export formats", "multi", ["PDF", "DWG", "STEP"], "PDF,DWG,STEP"),
        ]),
    ];

    /// <summary>Normalizes the values sent by the user: drops unknown keys, fills defaults, checks choices and numbers.</summary>
    public static Dictionary<string, string> Normalize(ConfigGroup group, IReadOnlyDictionary<string, string>? input, out string? error)
    {
        error = null;
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var f in group.Fields)
        {
            var v = input != null && input.TryGetValue(f.Name, out var x) ? (x ?? "").Trim() : f.Default;
            switch (f.Type)
            {
                case "number":
                    if (!double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) || !double.IsFinite(d))
                    { error = $"{group.Label}: '{f.Label}' must be a number."; return result; }
                    v = d.ToString(CultureInfo.InvariantCulture);
                    break;
                case "select":
                    if (!f.Options!.Contains(v)) { error = $"{group.Label}: '{f.Label}' must be one of {string.Join(", ", f.Options!)}."; return result; }
                    break;
                case "multi":
                    var picked = v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToArray();
                    if (picked.Length == 0 || picked.Any(p => !f.Options!.Contains(p))) { error = $"{group.Label}: '{f.Label}' needs at least one of {string.Join(", ", f.Options!)}."; return result; }
                    v = string.Join(",", f.Options!.Where(picked.Contains));
                    break;
            }
            result[f.Name] = v;
        }
        return result;
    }

    public static Dictionary<string, string> Defaults(ConfigGroup g) => g.Fields.ToDictionary(f => f.Name, f => f.Default);
}

/// <summary>
/// One Configuration of a product: name, status, operating condition, CAD design condition and the value of every parameter.
/// An order picks one Configuration; the form starts from its values.
/// </summary>
public sealed class ProductConfig
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Status { get; set; } = "draft"; // draft | active | deprecated
    public int Revision { get; set; } = 1;
    public bool IsDefault { get; set; }
    public Dictionary<string, string> Operation { get; set; } = new();
    public Dictionary<string, string> Cad { get; set; } = new();
    public Dictionary<string, double> Values { get; set; } = new();
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string UpdatedBy { get; set; } = "";
}

public sealed record ConfigRequest(string? Name, string? Description, string? Status, bool? IsDefault,
    Dictionary<string, string>? Operation, Dictionary<string, string>? Cad, Dictionary<string, double>? Values);

/// <summary>Persistence port of the Configurations: the whole list of one product is read and written at once (a handful of rows).</summary>
public interface IConfigRepository
{
    Task<List<ProductConfig>?> LoadAsync(string productCode, CancellationToken ct = default);
    Task StoreAsync(string productCode, IReadOnlyList<ProductConfig> list, CancellationToken ct = default);
}

public sealed record ConfigResult(ProductConfig? Config, string? Error, bool NotFound);

/// <summary>Use cases on Configurations. All checks live here so the API and the tests share them.</summary>
public sealed class ProductConfigService(IConfigRepository repo, TimeProvider clock)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    private static string NewId() => Guid.NewGuid().ToString("N")[..10];

    private async Task<List<ProductConfig>> LoadAsync(Product product, CancellationToken ct)
    {
        if (await repo.LoadAsync(product.Code, ct) is { } list) return list;
        // First use: one sample Configuration (the factory defaults) so the list is never empty.
        var now = clock.GetUtcNow();
        var seed = new List<ProductConfig>
        {
            new()
            {
                Id = NewId(), Name = $"{product.Code}-1.0", Description = "Factory defaults", Status = "active", IsDefault = true,
                Operation = ConfigSchema.Defaults(ConfigSchema.Groups[0]), Cad = ConfigSchema.Defaults(ConfigSchema.Groups[1]),
                Values = product.Complete(null), CreatedAt = now, UpdatedAt = now, UpdatedBy = "system",
            },
        };
        await repo.StoreAsync(product.Code, seed, ct);
        return seed;
    }

    public async Task<List<ProductConfig>> ListAsync(Product product, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try { return await LoadAsync(product, ct); } finally { _gate.Release(); }
    }

    public async Task<ProductConfig?> GetAsync(Product product, string id, CancellationToken ct = default) =>
        (await ListAsync(product, ct)).FirstOrDefault(c => c.Id == id);

    /// <summary>Creates (id null) or edits a Configuration.</summary>
    public async Task<ConfigResult> SaveAsync(Product product, string? id, ConfigRequest req, string by, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var list = await LoadAsync(product, ct);
            var existing = id == null ? null : list.FirstOrDefault(c => c.Id == id);
            if (id != null && existing == null) return new(null, "Unknown configuration.", true);

            var name = (req.Name ?? existing?.Name ?? "").Trim();
            if (name.Length is 0 or > 80) return new(null, "The configuration needs a name of 1-80 characters.", false);
            if (list.Any(c => c.Id != id && string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)))
                return new(null, $"A configuration named '{name}' already exists for this product.", false);

            var status = (req.Status ?? existing?.Status ?? "draft").ToLowerInvariant();
            if (!ConfigSchema.Statuses.Contains(status)) return new(null, $"Status must be one of {string.Join(", ", ConfigSchema.Statuses)}.", false);

            var op = ConfigSchema.Normalize(ConfigSchema.Groups[0], req.Operation ?? existing?.Operation, out var e1);
            if (e1 != null) return new(null, e1, false);
            var cad = ConfigSchema.Normalize(ConfigSchema.Groups[1], req.Cad ?? existing?.Cad, out var e2);
            if (e2 != null) return new(null, e2, false);

            var values = product.Complete(req.Values ?? existing?.Values);
            var issues = product.Validate(values);
            if (issues.Count > 0) return new(null, "Component values violate product rules: " + issues[0].Message, false);

            var isDefault = req.IsDefault ?? existing?.IsDefault ?? false;
            if (isDefault && status != "active") return new(null, "Only an active configuration can be the default.", false);

            var now = clock.GetUtcNow();
            var cfg = existing ?? new ProductConfig { Id = NewId(), CreatedAt = now };
            if (existing == null) list.Add(cfg); else cfg.Revision++;
            cfg.Name = name; cfg.Description = (req.Description ?? existing?.Description ?? "").Trim();
            cfg.Status = status; cfg.Operation = op; cfg.Cad = cad; cfg.Values = values;
            cfg.IsDefault = isDefault && status == "active"; cfg.UpdatedAt = now;
            cfg.UpdatedBy = string.IsNullOrWhiteSpace(by) ? "anonymous" : by.Trim()[..Math.Min(80, by.Trim().Length)];
            if (cfg.IsDefault) foreach (var o in list.Where(c => c != cfg)) o.IsDefault = false;
            await repo.StoreAsync(product.Code, list, ct);
            return new(cfg, null, false);
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> DeleteAsync(Product product, string id, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var list = await LoadAsync(product, ct);
            if (list.RemoveAll(c => c.Id == id) == 0) return false;
            await repo.StoreAsync(product.Code, list, ct);
            return true;
        }
        finally { _gate.Release(); }
    }
}
