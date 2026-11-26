using Cadflow.CreoWorker.Creo;
using InventorAddin.Gea;

// Usage: CreoGeaSmoke <workdir>   (Administrator; builds a block + 3 cylinders through CreoGeaHost in real Creo 10 and prints the bounding box)
var dir = args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "creo-smoke");
int rc = 0;
// Watchdog: a Creo dialog can block a Toolkit call forever; this process runs elevated, so it can kill Creo and exit by itself.
var limit = int.TryParse(Environment.GetEnvironmentVariable("SMOKE_TIMEOUT_S"), out var s) ? s : 150;
var hold = int.TryParse(Environment.GetEnvironmentVariable("SMOKE_HOLD_S"), out var hs) ? hs : 0; // keep Creo open this long after the build so a person can look at it
new Thread(() =>
{
    Thread.Sleep((limit + hold) * 1000);
    Console.WriteLine($"WATCHDOG: no result after {limit} s, killing Creo (a dialog is probably open)");
    foreach (var n in new[] { "parametric", "xtop", "pro_comm_msg" })
        foreach (var p in System.Diagnostics.Process.GetProcessesByName(n)) { try { p.Kill(true); } catch { } }
    Environment.Exit(3);
}) { IsBackground = true }.Start();
var t = new Thread(() => { rc = Run(dir, hold); }, maxStackSize: 8 * 1024 * 1024); // Toolkit is single-threaded
t.Start(); t.Join();
return rc;

static GeaValue L(double v) => GeaValue.Length(v);
static Dictionary<string, GeaValue> In(params (string, GeaValue)[] kv) => kv.ToDictionary(x => x.Item1, x => x.Item2);

static int Run(string dir, int hold)
{
    try
    {
        Console.WriteLine("starting Creo ...");
        using var creo = new NativeCreoModeler(dir);
        try { return Body(creo, dir); } finally { if (hold > 0) { Console.WriteLine($"holding Creo open for {hold} s ..."); Thread.Sleep(hold * 1000); } }
    }
    catch (Exception ex) { Console.WriteLine("FAILED: " + ex.Message); return 1; }
}

static int Body(NativeCreoModeler creo, string dir)
{
    {
        if (Environment.GetEnvironmentVariable("SMOKE_ONLY") == "bushing")
        {
            var hb = new CreoGeaHost(creo, "smoke_bushing");
            hb.CreateFeature(GeaBuilderCatalog.Find("Bushing")!, null, In(("CenterX", L(0)), ("CenterD", L(0)), ("Elevation", L(0)), ("Diameter", L(100)), ("Length", L(300)),
                ("TurretHeight", L(60)), ("ShedCount", GeaValue.Int(3)), ("CapHeight", L(20))));
            hb.Regen();
            var bb = creo.Measure();
            Console.WriteLine($"bushing box = ({bb[0]:g},{bb[1]:g},{bb[2]:g})-({bb[3]:g},{bb[4]:g},{bb[5]:g})");
            return 0;
        }
        var host = new CreoGeaHost(creo, "smoke1");
        var block = GeaBuilderCatalog.Find("Block")!; var cyl = GeaBuilderCatalog.Find("Cylinder")!;
        host.CreateFeature(block, null, In(("CenterX", L(0)), ("CenterD", L(0)), ("Elevation", L(0)), ("SizeX", L(200)), ("SizeD", L(100)), ("Height", L(50))));
        host.CreateFeature(cyl, null, In(("CenterX", L(60)), ("CenterD", L(0)), ("Elevation", L(50)), ("Diameter", L(30)), ("Length", L(40)), ("Axis", GeaValue.Text("H"))));
        host.CreateFeature(cyl, null, In(("CenterX", L(0)), ("CenterD", L(-40)), ("Elevation", L(25)), ("Diameter", L(20)), ("Length", L(300)), ("Axis", GeaValue.Text("X"))));
        host.CreateFeature(cyl, null, In(("CenterX", L(-90)), ("CenterD", L(0)), ("Elevation", L(25)), ("Diameter", L(20)), ("Length", L(200)), ("Axis", GeaValue.Text("D"))));
        var b = creo.Measure(Path.Combine(dir, "smoke1.stp"));
        Console.WriteLine($"box = ({b[0]:g},{b[1]:g},{b[2]:g})-({b[3]:g},{b[4]:g},{b[5]:g})");
        Console.WriteLine("expected = (-150,0,-100)-(150,90,100)");
        Console.WriteLine(b.Zip(new double[] { -150, 0, -100, 150, 90, 100 }).All(p => Math.Abs(p.First - p.Second) < 0.01) ? "RESULT OK" : "RESULT MISMATCH");

        // box.gea with its default inputs: Tank (ring + plate, rounded corners) and Cover (lid + lip)
        var scripts = Path.Combine(AppContext.BaseDirectory, "Scripts");
        var s = GeaScript.Compile(File.ReadAllText(Path.Combine(scripts, "box.gea")), GeaLibraryResolver.For(scripts, Path.Combine(scripts, "lib")), "box.gea");
        if (s.HasErrors) { Console.WriteLine("box.gea: " + string.Join("; ", s.Diagnostics)); return 1; }
        var form = s.EvaluateFormNumbers(InventorAddin.Models.BoxParams.Defaults());
        var host2 = new CreoGeaHost(creo, "smoke2");
        var run = s.RunBuild(form, host2);
        Console.WriteLine("box.gea run: " + (run.Ok ? "ok" : "FAILED " + run.Error?.Message));
        foreach (var l in host2.Log) Console.WriteLine("  log: " + l);
        var b2 = creo.Measure(Path.Combine(dir, "smoke2.stp"));
        Console.WriteLine($"box.gea box = ({b2[0]:g},{b2[1]:g},{b2[2]:g})-({b2[3]:g},{b2[4]:g},{b2[5]:g})");
        var d = InventorAddin.Models.BoxParams.Defaults();
        Console.WriteLine($"defaults: length {d[InventorAddin.Models.BoxParams.Length]}, width {d[InventorAddin.Models.BoxParams.Width]}, height {d[InventorAddin.Models.BoxParams.Height]}; all: " + string.Join(", ", d.Select(kv => $"{kv.Key}={kv.Value:g}")));
        if (!run.Ok) return 1;

        // transformer.gea with its default inputs (Blocks, Cylinders, Bushings)
        var st = GeaScript.Compile(File.ReadAllText(Path.Combine(scripts, "transformer.gea")), GeaLibraryResolver.For(scripts, Path.Combine(scripts, "lib")), "transformer.gea");
        if (st.HasErrors) { Console.WriteLine("transformer.gea: " + string.Join("; ", st.Diagnostics)); return 1; }
        var formT = st.EvaluateFormNumbers(InventorAddin.Models.TransformerParams.Defaults());
        var host3 = new CreoGeaHost(creo, "smoke3");
        var runT = st.RunBuild(formT, host3);
        Console.WriteLine("transformer.gea run: " + (runT.Ok ? "ok" : "FAILED " + runT.Error?.Message) + $" ({host3.Bodies.Count} bodies)");
        foreach (var l in host3.Log.Where(l => !l.StartsWith("State"))) Console.WriteLine("  log: " + l);
        if (!runT.Ok) return 1;
        var b3 = creo.Measure(Path.Combine(dir, "smoke3.stp"));
        Console.WriteLine($"transformer box = ({b3[0]:g},{b3[1]:g},{b3[2]:g})-({b3[3]:g},{b3[4]:g},{b3[5]:g})");
        return 0;
    }
}


