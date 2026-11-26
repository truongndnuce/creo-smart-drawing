namespace Cadflow.Application.Catalog;

/// <summary>A material the engineer can pick on the order form. Density is in g/cm3.</summary>
public sealed record MaterialInfo(string Code, string Name, string Standard, double Density);

/// <summary>A surface finish / appearance. Color is a CSS hex value used for the swatch on the web page.</summary>
public sealed record FinishInfo(string Code, string Name, string Process, string Color);

/// <summary>The fixed lists behind the Material and Finish choices (one place for the order form and the server check).</summary>
public static class MaterialCatalog
{
    public const string MaterialKey = "PI_Material"; // "PI_" = part information
    public const string FinishKey = "PI_Finish";

    public static readonly IReadOnlyList<MaterialInfo> Materials =
    [
        new("steel-s355", "Structural steel S355", "EN 10025-2", 7.85),
        new("stainless-304", "Stainless steel 304 (1.4301)", "EN 10088-2", 8.00),
        new("alu-6061", "Aluminium 6061-T6", "EN 573-3 / EN 755", 2.70),
        new("abs", "ABS plastic", "ISO 2580", 1.05),
    ];

    public static readonly IReadOnlyList<FinishInfo> Finishes =
    [
        new("natural", "Natural (bare metal)", "None", "#B8BEC4"),
        new("ral7035", "Powder coat RAL 7035 (light grey)", "Powder coating", "#CBD0CC"),
        new("ral5010", "Powder coat RAL 5010 (gentian blue)", "Powder coating", "#0E4C92"),
        new("galvanised", "Hot-dip galvanised", "Zinc coating (EN ISO 1461)", "#9AA3A8"),
        new("black-anodised", "Black anodised", "Anodising", "#1F2328"),
    ];

    public static MaterialInfo? FindMaterial(string? code) => Materials.FirstOrDefault(m => string.Equals(m.Code, code, StringComparison.OrdinalIgnoreCase));
    public static FinishInfo? FindFinish(string? code) => Finishes.FirstOrDefault(f => string.Equals(f.Code, code, StringComparison.OrdinalIgnoreCase));

    /// <summary>Error text when the order carries a Material / Finish that is not in the catalog; null when fine or absent.</summary>
    public static string? Check(IReadOnlyDictionary<string, string>? texts)
    {
        if (texts == null) return null;
        if (texts.TryGetValue(MaterialKey, out var m) && !string.IsNullOrWhiteSpace(m) && FindMaterial(m) == null) return $"Unknown material '{m}'.";
        if (texts.TryGetValue(FinishKey, out var f) && !string.IsNullOrWhiteSpace(f) && FindFinish(f) == null) return $"Unknown finish '{f}'.";
        return null;
    }

    /// <summary>Keeps only the Material / Finish entries, trimmed.</summary>
    public static Dictionary<string, string> Pick(IReadOnlyDictionary<string, string>? texts)
    {
        var r = new Dictionary<string, string>(StringComparer.Ordinal);
        if (texts == null) return r;
        foreach (var key in new[] { MaterialKey, FinishKey })
            if (texts.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v)) r[key] = v.Trim();
        return r;
    }
}
