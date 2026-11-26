using InventorAddin.Models;

namespace Cadflow.Application.Products;

/// <summary>An orderable product: parameters and validation rules (shared with the .gea script through Cadflow.Core).</summary>
public sealed record Product(
    string Code, string Name, string Description, int Version,
    IReadOnlyList<ParameterDef> Parameters,
    Func<IReadOnlyDictionary<string, double>?, Dictionary<string, double>> Complete,
    Func<IReadOnlyDictionary<string, double>, IReadOnlyList<ValidationIssue>> Validate,
    string ScriptFile);

public static class ProductCatalog
{
    public static readonly IReadOnlyList<Product> All =
    [
        new Product("box", "Box with lid", "Parametric box (body + lid).", 1, BoxParams.All, BoxParams.Complete, BoxParams.Validate, "box.gea"),
        new Product("transformer", "Power transformer (outer shell)", "Oil-immersed power transformer: tank, cover, bushings, radiators, conservator, wheels. Core and windings are not modelled.", 1,
            TransformerParams.All, TransformerParams.Complete, TransformerParams.Validate, "transformer.gea"),
    ];

    public static Product? Find(string? code) => All.FirstOrDefault(p => string.Equals(p.Code, code, StringComparison.OrdinalIgnoreCase));
}

