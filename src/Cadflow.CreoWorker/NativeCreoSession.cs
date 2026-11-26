using System.Collections.Concurrent;
using Cadflow.CreoWorker.Creo;
using InventorAddin.Gea;
using InventorAddin.Models;

namespace Cadflow.CreoWorker;

/// <summary>
/// Builds jobs in a real Creo 10 through CreoBridge10. Toolkit is single-threaded, so ONE dedicated thread owns Creo for the whole life of the
/// worker; jobs are queued onto it. Creo is started on the first job and stays open (a person can watch the model appear). Needs Administrator.
/// </summary>
public sealed class NativeCreoSession(string creoWorkDir) : ICreoSession, IDisposable
{
    public string Name => "native";
    private readonly BlockingCollection<Action> _queue = new();
    private Thread? _thread;
    private NativeCreoModeler? _creo;

    private void EnsureThread()
    {
        if (_thread != null) return;
        _thread = new Thread(() => { foreach (var a in _queue.GetConsumingEnumerable()) a(); }, 8 * 1024 * 1024) { IsBackground = true, Name = "creo-toolkit" };
        _thread.Start();
    }

    private Task<T> OnCreoThread<T>(Func<T> f)
    {
        EnsureThread();
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Add(() => { try { tcs.SetResult(f()); } catch (Exception ex) { tcs.SetException(ex); } });
        return tcs.Task;
    }

    public async Task<BuildOutput> BuildAsync(WorkItem job, string workDir, Func<string, string?, Task> progress, CancellationToken ct)
    {
        var code = job.ProductCode.ToLowerInvariant();
        if (code is not ("box" or "transformer")) throw new BuildException($"Product '{job.ProductCode}' is not supported.");
        var scripts = Path.Combine(AppContext.BaseDirectory, "Scripts");
        var script = GeaScript.Compile(await File.ReadAllTextAsync(Path.Combine(scripts, code + ".gea"), ct), GeaLibraryResolver.For(scripts, Path.Combine(scripts, "lib")), code + ".gea");
        if (script.HasErrors) throw new BuildException("Script error: " + string.Join("; ", script.Diagnostics));

        Dictionary<string, double> inputs; List<string> issues;
        if (code == "box") { inputs = BoxParams.Complete(job.Inputs); issues = BoxParams.Validate(inputs).Select(i => i.Message).ToList(); }
        else { inputs = TransformerParams.Complete(job.Inputs); issues = TransformerParams.Validate(inputs).Select(i => i.Message).ToList(); }
        if (issues.Count > 0) throw new BuildException(string.Join("; ", issues));

        Directory.CreateDirectory(workDir);
        var partName = $"{code}_{job.Id.Replace("-", "")[..8]}".ToLowerInvariant();
        var stepPath = Path.Combine(workDir, code + ".stp");
        await progress("model", "Dựng model trong Creo");

        var (box, log, ok, error) = await OnCreoThread(() =>
        {
            _creo ??= new NativeCreoModeler(creoWorkDir);
            var host = new CreoGeaHost(_creo, partName);
            var run = script.RunBuild(script.EvaluateFormNumbers(inputs), host);
            host.Regen(); // refit the view so the whole object is visible
            if (!run.Ok) return (new double[6], host.Log, false, run.Error?.Message);
            return (_creo.Measure(stepPath), host.Log, true, (string?)null);
        });
        if (!ok) throw new BuildException("Build failed: " + error);

        var files = new List<OutputFile>();
        if (File.Exists(stepPath)) files.Add(new(code + ".stp", "step", stepPath));
        var logPath = Path.Combine(workDir, "run.log");
        await File.WriteAllTextAsync(logPath, string.Join("\n", log), ct);
        files.Add(new("run.log", "log", logPath));
        // Creo axes: X = length, Y = up, Z = depth (see docs/spike-toolkit.md)
        var summary = new Dictionary<string, double>
        {
            ["sizeX"] = Math.Round(box[3] - box[0], 2), ["sizeY"] = Math.Round(box[5] - box[2], 2), ["sizeZ"] = Math.Round(box[4] - box[1], 2),
        };
        return new BuildOutput(summary, files);
    }

    public void Dispose() { _queue.CompleteAdding(); try { _creo?.Dispose(); } catch { } }
}

