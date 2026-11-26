using System.Collections.Concurrent;
using System.Text.Json;
using Cadflow.Application.Configs;
using Npgsql;
using NpgsqlTypes;

namespace Cadflow.Infrastructure.Persistence;

/// <summary>Memory adapter of <see cref="IConfigRepository"/> (tests / quick demo).</summary>
public sealed class InMemoryConfigRepository : IConfigRepository
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentDictionary<string, string> _data = new();

    public Task<List<ProductConfig>?> LoadAsync(string productCode, CancellationToken ct = default) =>
        Task.FromResult(_data.TryGetValue(productCode, out var s) ? JsonSerializer.Deserialize<List<ProductConfig>>(s, Json) : null);

    public Task StoreAsync(string productCode, IReadOnlyList<ProductConfig> list, CancellationToken ct = default)
    {
        _data[productCode] = JsonSerializer.Serialize(list, Json);
        return Task.CompletedTask;
    }
}

/// <summary>PostgreSQL adapter: one row per product holding the list of its Configurations as jsonb.</summary>
public sealed class PostgresConfigRepository(NpgsqlDataSource db) : IConfigRepository
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<List<ProductConfig>?> LoadAsync(string productCode, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("SELECT data FROM product_configs WHERE product_code = @c");
        cmd.Parameters.AddWithValue("c", productCode);
        return await cmd.ExecuteScalarAsync(ct) is string s ? JsonSerializer.Deserialize<List<ProductConfig>>(s, Json) : null;
    }

    public async Task StoreAsync(string productCode, IReadOnlyList<ProductConfig> list, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("INSERT INTO product_configs (product_code, data) VALUES (@c, @d) ON CONFLICT (product_code) DO UPDATE SET data = EXCLUDED.data");
        cmd.Parameters.AddWithValue("c", productCode);
        cmd.Parameters.Add(new NpgsqlParameter("d", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(list, Json) });
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
