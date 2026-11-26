using Npgsql;

namespace Cadflow.Infrastructure.Persistence;

/// <summary>Creates the app's own database when missing and migrates its schema (own DB per app; see docs/architecture.md).</summary>
public static class PostgresDatabase
{
    /// <summary>Schema version this build knows. A database with a higher version makes the app refuse to start.</summary>
    public const int SchemaVersion = 2;

    public static async Task EnsureCreatedAsync(string connectionString, CancellationToken ct = default)
    {
        var target = new NpgsqlConnectionStringBuilder(connectionString);
        var name = target.Database ?? throw new InvalidOperationException("Connection string has no Database.");
        var admin = new NpgsqlConnectionStringBuilder(connectionString) { Database = "postgres", Pooling = false };
        await using (var conn = new NpgsqlConnection(admin.ConnectionString))
        {
            await conn.OpenAsync(ct);
            await using var exists = new NpgsqlCommand("SELECT 1 FROM pg_database WHERE datname = @n", conn);
            exists.Parameters.AddWithValue("n", name);
            if (await exists.ExecuteScalarAsync(ct) is null)
            {
                await using var create = new NpgsqlCommand($"CREATE DATABASE \"{name.Replace("\"", "\"\"")}\" ENCODING 'UTF8'", conn);
                await create.ExecuteNonQueryAsync(ct);
            }
        }
        await MigrateAsync(connectionString, ct);
    }

    public static async Task MigrateAsync(string connectionString, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await Exec(conn, tx, "SELECT pg_advisory_xact_lock(727001)", ct); // one migrator at a time
        await Exec(conn, tx, "CREATE TABLE IF NOT EXISTS schema_version (version int NOT NULL)", ct);
        int current;
        await using (var q = new NpgsqlCommand("SELECT COALESCE(MAX(version), 0) FROM schema_version", conn, tx))
            current = (int)(await q.ExecuteScalarAsync(ct) ?? 0);
        if (current > SchemaVersion)
            throw new InvalidOperationException($"Database schema is version {current}, newer than this build ({SchemaVersion}). Update the app.");
        if (current < 1)
        {
            await Exec(conn, tx, """
                CREATE TABLE jobs (
                    id             text PRIMARY KEY,
                    project_id     text NOT NULL,
                    product_code   text NOT NULL,
                    kind           text NOT NULL,
                    queue          text NOT NULL,
                    priority       int  NOT NULL,
                    status         text NOT NULL,
                    inputs         jsonb NOT NULL,
                    requested_by   text NOT NULL,
                    step           text,
                    worker         text,
                    attempts       int  NOT NULL DEFAULT 0,
                    error          text,
                    created_at     timestamptz NOT NULL,
                    started_at     timestamptz,
                    finished_at    timestamptz,
                    last_heartbeat timestamptz,
                    summary        jsonb NOT NULL DEFAULT '{}',
                    artifacts      jsonb NOT NULL DEFAULT '[]',
                    events         jsonb NOT NULL DEFAULT '[]'
                );
                CREATE INDEX jobs_queue_idx ON jobs (priority, created_at) WHERE status = 'queued';
                CREATE INDEX jobs_project_idx ON jobs (project_id, created_at DESC);
                """, ct);
            await Exec(conn, tx, "INSERT INTO schema_version VALUES (1)", ct);
        }
        if (current < 2)
        {
            await Exec(conn, tx, """
                ALTER TABLE jobs ADD COLUMN text_inputs jsonb NOT NULL DEFAULT '{}';
                CREATE TABLE product_configs (
                    product_code text PRIMARY KEY,
                    data         jsonb NOT NULL
                );
                """, ct);
            await Exec(conn, tx, "INSERT INTO schema_version VALUES (2)", ct);
        }
        await tx.CommitAsync(ct);
    }

    private static async Task Exec(NpgsqlConnection c, NpgsqlTransaction t, string sql, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, c, t);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
