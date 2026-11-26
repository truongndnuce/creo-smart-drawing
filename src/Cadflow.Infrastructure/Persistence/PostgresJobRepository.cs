using System.Text.Json;
using Cadflow.Application.Abstractions;
using Cadflow.Domain.Jobs;
using Npgsql;
using NpgsqlTypes;

namespace Cadflow.Infrastructure.Persistence;

/// <summary>PostgreSQL adapter of <see cref="IJobRepository"/>. Claiming uses FOR UPDATE SKIP LOCKED so many workers can poll at once.</summary>
public sealed class PostgresJobRepository(NpgsqlDataSource db) : IJobRepository
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private const string Columns = "id, project_id, product_code, kind, queue, priority, status, inputs, requested_by, step, worker, attempts, error, created_at, started_at, finished_at, last_heartbeat, summary, artifacts, events, text_inputs";

    public async Task AddAsync(Job job, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand($"INSERT INTO jobs ({Columns}) VALUES (@id,@project_id,@product_code,@kind,@queue,@priority,@status,@inputs,@requested_by,@step,@worker,@attempts,@error,@created_at,@started_at,@finished_at,@last_heartbeat,@summary,@artifacts,@events,@text_inputs)");
        Bind(cmd, job.ToSnapshot());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<Job?> GetAsync(string id, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand($"SELECT {Columns} FROM jobs WHERE id = @id");
        cmd.Parameters.AddWithValue("id", id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Read(r) : null;
    }

    public async Task<IReadOnlyList<Job>> ListAsync(JobFilter f, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand($"""
            SELECT {Columns} FROM jobs
            WHERE (@status::text IS NULL OR status = @status) AND (@project::text IS NULL OR project_id = @project)
              AND (@product::text IS NULL OR product_code = @product) AND (@by::text IS NULL OR requested_by = @by)
            ORDER BY created_at DESC LIMIT @limit
            """);
        cmd.Parameters.AddWithValue("status", (object?)f.Status?.ToLowerInvariant() ?? DBNull.Value);
        cmd.Parameters.AddWithValue("project", (object?)f.ProjectId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("product", (object?)f.Product ?? DBNull.Value);
        cmd.Parameters.AddWithValue("by", (object?)f.RequestedBy ?? DBNull.Value);
        cmd.Parameters.AddWithValue("limit", f.Limit);
        var list = new List<Job>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(Read(r));
        return list;
    }

    public async Task<Job?> ClaimNextAsync(string worker, IReadOnlyCollection<string>? products, IReadOnlyCollection<string>? queues, DateTimeOffset now, CancellationToken ct = default)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using var pick = new NpgsqlCommand($"""
            SELECT {Columns} FROM jobs
            WHERE status = 'queued'
              AND (@products::text[] IS NULL OR product_code = ANY(@products))
              AND (@queues::text[] IS NULL OR queue = ANY(@queues))
            ORDER BY priority, created_at
            LIMIT 1 FOR UPDATE SKIP LOCKED
            """, conn, tx);
        pick.Parameters.Add(new NpgsqlParameter("products", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = products is { Count: > 0 } ? products.Select(p => p.ToLowerInvariant()).ToArray() : DBNull.Value });
        pick.Parameters.Add(new NpgsqlParameter("queues", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = queues is { Count: > 0 } ? queues.ToArray() : DBNull.Value });
        Job? job;
        await using (var r = await pick.ExecuteReaderAsync(ct)) job = await r.ReadAsync(ct) ? Read(r) : null;
        if (job is null) return null;
        job.Start(worker, now);
        await using var upd = new NpgsqlCommand(UpdateSql, conn, tx);
        Bind(upd, job.ToSnapshot());
        await upd.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
        return job;
    }

    public async Task SaveAsync(Job job, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(UpdateSql);
        Bind(cmd, job.ToSnapshot());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<int> ReclaimStaleAsync(TimeSpan timeout, DateTimeOffset now, CancellationToken ct = default)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var stale = new List<Job>();
        await using (var q = new NpgsqlCommand($"SELECT {Columns} FROM jobs WHERE status = 'running' AND last_heartbeat < @cutoff FOR UPDATE SKIP LOCKED", conn, tx))
        {
            q.Parameters.AddWithValue("cutoff", now - timeout);
            await using var r = await q.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) stale.Add(Read(r));
        }
        var n = 0;
        foreach (var job in stale)
        {
            if (!job.ReclaimIfStale(timeout, now)) continue;
            await using var upd = new NpgsqlCommand(UpdateSql, conn, tx);
            Bind(upd, job.ToSnapshot());
            await upd.ExecuteNonQueryAsync(ct);
            n++;
        }
        await tx.CommitAsync(ct);
        return n;
    }

    private const string UpdateSql = "UPDATE jobs SET status=@status, step=@step, worker=@worker, attempts=@attempts, error=@error, started_at=@started_at, finished_at=@finished_at, last_heartbeat=@last_heartbeat, summary=@summary, artifacts=@artifacts, events=@events WHERE id=@id";

    private static void Bind(NpgsqlCommand cmd, JobSnapshot s)
    {
        var p = cmd.Parameters;
        p.AddWithValue("id", s.Id);
        p.AddWithValue("project_id", s.ProjectId);
        p.AddWithValue("product_code", s.ProductCode);
        p.AddWithValue("kind", s.Kind);
        p.AddWithValue("queue", s.Queue);
        p.AddWithValue("priority", s.Priority);
        p.AddWithValue("status", s.Status.ToString().ToLowerInvariant());
        p.Add(new NpgsqlParameter("inputs", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(s.Inputs, Json) });
        p.AddWithValue("requested_by", s.RequestedBy);
        p.AddWithValue("step", (object?)s.Step ?? DBNull.Value);
        p.AddWithValue("worker", (object?)s.Worker ?? DBNull.Value);
        p.AddWithValue("attempts", s.Attempts);
        p.AddWithValue("error", (object?)s.Error ?? DBNull.Value);
        p.AddWithValue("created_at", s.CreatedAt);
        p.AddWithValue("started_at", (object?)s.StartedAt ?? DBNull.Value);
        p.AddWithValue("finished_at", (object?)s.FinishedAt ?? DBNull.Value);
        p.AddWithValue("last_heartbeat", (object?)s.LastHeartbeat ?? DBNull.Value);
        p.Add(new NpgsqlParameter("summary", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(s.Summary, Json) });
        p.Add(new NpgsqlParameter("artifacts", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(s.Artifacts, Json) });
        p.Add(new NpgsqlParameter("events", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(s.Events, Json) });
        // Only the INSERT names text_inputs (it never changes after the order is placed).
        if (cmd.CommandText.Contains("@text_inputs"))
            p.Add(new NpgsqlParameter("text_inputs", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(s.TextInputs ?? new Dictionary<string, string>(), Json) });
    }

    private static Job Read(NpgsqlDataReader r)
    {
        T? Opt<T>(string col) where T : struct { var i = r.GetOrdinal(col); return r.IsDBNull(i) ? null : r.GetFieldValue<T>(i); }
        string? Str(string col) { var i = r.GetOrdinal(col); return r.IsDBNull(i) ? null : r.GetString(i); }
        T Doc<T>(string col) => JsonSerializer.Deserialize<T>(r.GetString(r.GetOrdinal(col)), Json)!;
        return Job.Restore(new JobSnapshot(
            r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetInt32(5),
            Doc<Dictionary<string, double>>("inputs"), r.GetString(r.GetOrdinal("requested_by")),
            Enum.Parse<JobStatus>(r.GetString(r.GetOrdinal("status")), ignoreCase: true), Str("step"), Str("worker"), r.GetInt32(r.GetOrdinal("attempts")), Str("error"),
            r.GetFieldValue<DateTimeOffset>(r.GetOrdinal("created_at")), Opt<DateTimeOffset>("started_at"), Opt<DateTimeOffset>("finished_at"), Opt<DateTimeOffset>("last_heartbeat"),
            Doc<Dictionary<string, double>>("summary"), Doc<List<ArtifactInfo>>("artifacts"), Doc<List<JobEvent>>("events"), Doc<Dictionary<string, string>>("text_inputs")));
    }
}
