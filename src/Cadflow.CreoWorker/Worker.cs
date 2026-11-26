namespace Cadflow.CreoWorker;

/// <summary>Claim → build → upload → complete, forever. One job at a time (one Creo = one job).</summary>
public sealed class Worker(ServerClient server, ICreoSession session, string workRoot, string[] products, string[] queues, TimeSpan poll)
{
    public async Task RunAsync(CancellationToken ct, bool once = false)
    {
        Log($"Worker started, session={session.Name}");
        while (!ct.IsCancellationRequested)
        {
            WorkItem? job = null;
            try
            {
                job = await server.ClaimAsync(products, queues, ct);
                if (job is null) { if (once) return; await Task.Delay(poll, ct); continue; }
                await RunJobAsync(job, ct);
                if (once) return;
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) // server unreachable etc.: keep the loop alive; the server reclaims a stale job by itself
            {
                Log($"Error: {ex.Message}");
                try { await Task.Delay(poll, ct); } catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task RunJobAsync(WorkItem job, CancellationToken ct)
    {
        Log($"Job {job.Id[..8]} {job.ProductCode}/{job.Kind}");
        var dir = Path.Combine(workRoot, job.Id);
        try
        {
            var result = await session.BuildAsync(job, dir, (step, msg) => server.ProgressAsync(job.Id, step, msg, ct), ct);
            foreach (var f in result.Files) await server.UploadAsync(job.Id, f, ct);
            await server.CompleteAsync(job.Id, result.Summary, ct);
            Log($"Job {job.Id[..8]} done, {result.Files.Count} files");
        }
        catch (BuildException ex) { Log($"Job {job.Id[..8]} failed: {ex.Message}"); await server.FailAsync(job.Id, ex.Message, ex.Retryable, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { Log($"Job {job.Id[..8]} crashed: {ex.Message}"); await server.FailAsync(job.Id, ex.Message, retryable: true, ct); }
        finally { try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { } }
    }

    private static void Log(string m) => Console.WriteLine($"{DateTime.Now:HH:mm:ss} {m}");
}
