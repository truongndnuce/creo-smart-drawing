using Cadflow.CreoWorker;

// Usage: CreoWorker [--once]   Config via environment: CADFLOW_SERVER, CADFLOW_WORKER_KEY, CADFLOW_BACKEND (sim|native), CADFLOW_WORK_DIR
var server = Environment.GetEnvironmentVariable("CADFLOW_SERVER") ?? "http://localhost:18090/";
var key = Environment.GetEnvironmentVariable("CADFLOW_WORKER_KEY") ?? "dev-worker-key";
var backend = Environment.GetEnvironmentVariable("CADFLOW_BACKEND") ?? "sim";
var work = Environment.GetEnvironmentVariable("CADFLOW_WORK_DIR") ?? Path.Combine(Path.GetTempPath(), "cadflow-creo-work");

ICreoSession session = backend switch
{
    "sim" => new SimCreoSession(),
    "native" => new NativeCreoSession(Path.Combine(work, "creo")), // needs Administrator; Creo opens on the first job and stays open
    _ => throw new NotSupportedException($"Backend '{backend}' is not available yet (native Creo comes with CreoBridge10).")
};

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
await new Worker(new ServerClient(server, key), session, work, ["box", "transformer"], ["filter", "build"], TimeSpan.FromSeconds(2))
    .RunAsync(cts.Token, once: args.Contains("--once"));


