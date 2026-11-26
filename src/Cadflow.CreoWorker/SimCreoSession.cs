using System.Text;
using InventorAddin.Gea;
using InventorAddin.Models;

namespace Cadflow.CreoWorker;

/// <summary>Runs the real box.gea script on the simulation host and writes stand-in files. No Creo involved: proves the whole chain (queue, worker, upload, Vault).</summary>
public sealed class SimCreoSession : ICreoSession
{
    public string Name => "sim";
    private static readonly byte[] Png1x1 = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    public async Task<BuildOutput> BuildAsync(WorkItem job, string workDir, Func<string, string?, Task> progress, CancellationToken ct)
    {
        if (job.ProductCode.Equals("transformer", StringComparison.OrdinalIgnoreCase)) return await BuildTransformerAsync(job, workDir, progress, ct);
        if (!job.ProductCode.Equals("box", StringComparison.OrdinalIgnoreCase)) throw new BuildException($"Product '{job.ProductCode}' is not supported.");
        Directory.CreateDirectory(workDir);
        var scripts = Path.Combine(AppContext.BaseDirectory, "Scripts");
        var script = GeaScript.Compile(await File.ReadAllTextAsync(Path.Combine(scripts, "box.gea"), ct), GeaLibraryResolver.For(scripts, Path.Combine(scripts, "lib")), "box.gea");
        if (script.HasErrors) throw new BuildException("Script error: " + string.Join("; ", script.Diagnostics));

        var inputs = BoxParams.Complete(job.Inputs);
        var issues = BoxParams.Validate(inputs);
        if (issues.Count > 0) throw new BuildException(string.Join("; ", issues.Select(i => i.Message)));

        await progress("model", "Dựng model (sim)");
        var host = new GeaSimulationHost();
        var run = script.RunBuild(script.EvaluateFormNumbers(inputs), host);
        if (!run.Ok) throw new BuildException("Build failed: " + run.Error?.Message);

        var files = new List<OutputFile>();
        string Put(string name, string kind, byte[] bytes) { var p = Path.Combine(workDir, name); File.WriteAllBytes(p, bytes); files.Add(new(name, kind, p)); return p; }

        Put("box.png", "image", Png1x1);
        var L = inputs[BoxParams.Length]; var W = inputs[BoxParams.Width]; var H = inputs[BoxParams.Height];
        var wall = inputs[BoxParams.Wall]; var bottom = inputs[BoxParams.Bottom]; var lid = inputs[BoxParams.LidThickness];
        var bodyMm3 = L * W * H - (L - 2 * wall) * (W - 2 * wall) * (H - bottom);
        var lidMm3 = L * W * lid;
        const double aluKgPerMm3 = 2.7e-6;
        var summary = new Dictionary<string, double> { ["mass"] = Math.Round((bodyMm3 + lidMm3) * aluKgPerMm3, 4), ["sizeX"] = L, ["sizeY"] = W, ["sizeZ"] = H + lid };

        if (job.Kind != "preview")
        {
            await progress("export", "Xuất file (sim)");
            Put("box.step", "step", Encoding.ASCII.GetBytes("ISO-10303-21;\n/* SIMULATED output: no Creo was used */\nEND-ISO-10303-21;\n"));
            var bom = BomWriter.ToCsv(
                [new("BX-BODY", "Box body", "box_body.prt", "Aluminium", 1, Math.Round(bodyMm3 * aluKgPerMm3, 4), bodyMm3 / 1000, L, W, H),
                 new("BX-LID", "Box lid", "box_lid.prt", "Aluminium", 1, Math.Round(lidMm3 * aluKgPerMm3, 4), lidMm3 / 1000, L, W, lid)],
                inputs.ToDictionary(k => k.Key, k => k.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            Put("box.bom.csv", "bom", new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes(bom)).ToArray());
        }
        Put("run.log", "log", Encoding.UTF8.GetBytes(string.Join("\n", host.Trace)));
        return new BuildOutput(summary, files);
    }

    /// <summary>Runs transformer.gea on the simulation host (same script the real Creo build will run); size and mass are rough estimates from the inputs.</summary>
    private static async Task<BuildOutput> BuildTransformerAsync(WorkItem job, string workDir, Func<string, string?, Task> progress, CancellationToken ct)
    {
        var scripts = Path.Combine(AppContext.BaseDirectory, "Scripts");
        var script = GeaScript.Compile(await File.ReadAllTextAsync(Path.Combine(scripts, "transformer.gea"), ct), GeaLibraryResolver.For(scripts, Path.Combine(scripts, "lib")), "transformer.gea");
        if (script.HasErrors) throw new BuildException("Script error: " + string.Join("; ", script.Diagnostics));
        var v = TransformerParams.Complete(job.Inputs);
        var issues = TransformerParams.Validate(v);
        if (issues.Count > 0) throw new BuildException(string.Join("; ", issues.Select(i => i.Message)));
        Directory.CreateDirectory(workDir);

        await progress("model", "Dựng máy biến áp (sim)");
        var host = new GeaSimulationHost();
        var run = script.RunBuild(script.EvaluateFormNumbers(v), host);
        if (!run.Ok) throw new BuildException("Build failed: " + run.Error?.Message);

        double L = v[TransformerParams.TankLength], W = v[TransformerParams.TankWidth], H = v[TransformerParams.TankHeight];
        double bo = v[TransformerParams.BaseOverhang], bt = v[TransformerParams.BaseThickness], ct2 = v[TransformerParams.CoverThickness], co = v[TransformerParams.CoverOverhang];
        double rn = v[TransformerParams.RadiatorCount], rw = v[TransformerParams.RadiatorWidth], rd = v[TransformerParams.RadiatorDepth], rh = v[TransformerParams.RadiatorHeight];
        double cd = v[TransformerParams.ConservatorDiameter], cl = v[TransformerParams.ConservatorLength];
        const double steel = 7.85e-6, shell = 8; // kg/mm3, assumed plate thickness of tank/radiators/conservator
        double baseMm3 = (L + 2 * bo) * (W + 2 * bo) * bt, coverMm3 = (L + 2 * co) * (W + 2 * co) * ct2;
        double tankMm3 = 2 * (L + W) * H * shell + L * W * shell;
        double radMm3 = rn * 2 * (rw * rh + rd * rh) * shell, consMm3 = Math.PI * cd * cl * shell;

        var files = new List<OutputFile>();
        string Put(string name, string kind, byte[] bytes) { var p = Path.Combine(workDir, name); File.WriteAllBytes(p, bytes); files.Add(new(name, kind, p)); return p; }
        Put("transformer.png", "image", Png1x1);
        var summary = new Dictionary<string, double>
        {
            ["mass"] = Math.Round((baseMm3 + coverMm3 + tankMm3 + radMm3 + consMm3) * steel, 1),
            ["sizeX"] = Math.Round(L + 2 * bo), ["sizeY"] = Math.Round(W + 2 * bo + 30 + rd),
            ["sizeZ"] = Math.Round(bt + H + ct2 + v[TransformerParams.BushingLength] + v[TransformerParams.TurretHeight]),
        };
        if (job.Kind != "preview")
        {
            await progress("export", "Xuất file (sim)");
            Put("transformer.step", "step", Encoding.ASCII.GetBytes("ISO-10303-21;\n/* SIMULATED output: no Creo was used */\nEND-ISO-10303-21;\n"));
            var bom = BomWriter.ToCsv(
                [new("TR-BASE", "Base plate", "base", "Steel", 1, Math.Round(baseMm3 * steel, 1), baseMm3 / 1000, L + 2 * bo, W + 2 * bo, bt),
                 new("TR-TANK", "Tank", "tank", "Steel", 1, Math.Round(tankMm3 * steel, 1), tankMm3 / 1000, L, W, H),
                 new("TR-COVER", "Cover", "cover", "Steel", 1, Math.Round(coverMm3 * steel, 1), coverMm3 / 1000, L + 2 * co, W + 2 * co, ct2),
                 new("TR-RAD", "Radiator", "radiator", "Steel", (int)rn, rn > 0 ? Math.Round(radMm3 * steel / rn, 1) : null, null, rw, rd, rh),
                 new("TR-BSH", "Bushing", "bushing", "Porcelain", (int)v[TransformerParams.BushingCount], null, null, v[TransformerParams.BushingDiameter], v[TransformerParams.BushingDiameter], v[TransformerParams.BushingLength]),
                 new("TR-CONS", "Conservator", "conservator", "Steel", 1, Math.Round(consMm3 * steel, 1), null, cl, cd, cd)],
                v.ToDictionary(k => k.Key, k => k.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            Put("transformer.bom.csv", "bom", new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes(bom)).ToArray());
        }
        Put("run.log", "log", Encoding.UTF8.GetBytes(string.Join("\n", host.Trace)));
        return new BuildOutput(summary, files);
    }
}