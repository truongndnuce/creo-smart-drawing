using System.Diagnostics;
using System.Runtime.InteropServices;

// Usage: DllHost <workdir> <partname> ; env CREO_CMD (and PRO_COMM_MSG_EXE) as in the C spikes. Run as Administrator.
static class Native
{
    const string Dll = "CreoBridge10";
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int cb_version();
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int cb_start([MarshalAs(UnmanagedType.LPStr)] string cmd);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int cb_set_workdir([MarshalAs(UnmanagedType.LPStr)] string dir);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int cb_measure_part([MarshalAs(UnmanagedType.LPStr)] string name, [MarshalAs(UnmanagedType.LPStr)] string? step, double[] outValues);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int cb_new_part([MarshalAs(UnmanagedType.LPStr)] string template, [MarshalAs(UnmanagedType.LPStr)] string name);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int cb_cylinder([MarshalAs(UnmanagedType.LPStr)] string plane, [MarshalAs(UnmanagedType.LPStr)] string orient, [MarshalAs(UnmanagedType.LPStr)] string refA, [MarshalAs(UnmanagedType.LPStr)] string refB, double cx, double cy, double r, double height, double elevation, int flip);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int cb_block([MarshalAs(UnmanagedType.LPStr)] string plane, [MarshalAs(UnmanagedType.LPStr)] string orient, [MarshalAs(UnmanagedType.LPStr)] string refA, [MarshalAs(UnmanagedType.LPStr)] string refB, double x0, double y0, double x1, double y1, double height, double elevation);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void cb_set_strategy(int s);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int cb_end();
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr cb_last_error();
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr cb_info();
}

static class Program
{
    static string Info() => Marshal.PtrToStringAnsi(Native.cb_info()) ?? "";
    static string Err() => Marshal.PtrToStringAnsi(Native.cb_last_error()) ?? "";

    static int Main(string[] args)
    {
        if (args.Length < 2 || Environment.GetEnvironmentVariable("CREO_CMD") is not { Length: > 0 } cmd)
        { Console.WriteLine("usage: DllHost <workdir> <partname>   (env CREO_CMD required)"); return 2; }
        var (dir, part) = (args[0], args[1]);
        Console.WriteLine($"DLL version {Native.cb_version()} loaded in .NET {Environment.Version}, x64={Environment.Is64BitProcess}");

        int rc = 0;
        // Toolkit = single thread: everything runs on this dedicated thread (like the real Runner will).
        var worker = new Thread(() => rc = Run(cmd, dir, part), maxStackSize: 8 * 1024 * 1024) { IsBackground = false };
        worker.Start(); worker.Join();
        return rc;
    }

    static int Run(string cmd, string dir, string part)
    {
        var sw = Stopwatch.StartNew();
        int e = Native.cb_start(cmd);
        Console.WriteLine($"cb_start -> {e} ({sw.ElapsedMilliseconds} ms) {Err()}");
        if (e != 0) return 1;
        try
        {
            Console.WriteLine($"cb_set_workdir -> {Native.cb_set_workdir(dir)}");
            if (part == "block") return Block(dir);

            // Two jobs in the same Creo session, timed.
            for (int job = 1; job <= 2; job++)
            {
                sw.Restart();
                var o = new double[10];
                var step = Path.Combine(dir, $"{part}_job{job}.stp");
                e = Native.cb_measure_part(part, step, o);
                Console.WriteLine($"job {job}: cb_measure_part -> {e} ({sw.ElapsedMilliseconds} ms) {Err()}");
                if (e != 0) return 1;
                Console.WriteLine($"  volume={o[0]:g} mass={o[3]:g} box=({o[4]:g},{o[5]:g},{o[6]:g})-({o[7]:g},{o[8]:g},{o[9]:g}) step exists={File.Exists(step)}");
            }

            // Call from another thread: the bridge must refuse (-100) instead of crashing Creo.
            int wrong = 0;
            var t = new Thread(() => wrong = Native.cb_set_workdir(dir)); t.Start(); t.Join();
            Console.WriteLine($"call from another thread -> {wrong} (expected -100)");
            return 0;
        }
        finally
        {
            sw.Restart();
            Console.WriteLine($"cb_end -> {Native.cb_end()} ({sw.ElapsedMilliseconds} ms) {Err()}");
        }
    }

    // Spike 4: build a 200 x 100 x 50 block from scratch (new part from the Creo template + extruded sketch) and measure it.
    static int Block(string dir)
    {
        int e0 = 0;
        const string tpl = "mmns_part_solid_abs";
        File.Copy(@"C:\Program Files\PTC\Creo 10.0.1.0\Common Files\templates\mmns_part_solid_abs.prt", Path.Combine(dir, tpl + ".prt"), true);
        // Probe how Creo applies direction and the "from" offset: one block (100 x 100 in the sketch, extruded 50) per part,
        // varying the elevation; print the Y span (the extrusion axis on the TOP plane).
        int worst = 0;
        // Cylinder probes: circle centre (30,10) r 15, height 80, elevation 20, per sketch plane and flip.
        foreach (var (label, plane, orient, a, b, flip) in new[] {
            ("Y axis TOP flip0", "TOP", "FRONT", "RIGHT", "FRONT", 0),
            ("X axis RIGHT flip0", "RIGHT", "TOP", "FRONT", "TOP", 0),
            ("X axis RIGHT flip1", "RIGHT", "TOP", "FRONT", "TOP", 1),
            ("Z axis FRONT flip0", "FRONT", "TOP", "RIGHT", "TOP", 0),
            ("Z axis FRONT flip1", "FRONT", "TOP", "RIGHT", "TOP", 1) })
        {
            var name = "cyl_" + label.Replace(' ', '_');
            var e = Native.cb_new_part(tpl, name);
            if (e == 0) e = Native.cb_cylinder(plane, orient, a, b, 30, 10, 15, 80, 20, flip);
            var o = new double[10];
            var m = e == 0 ? Native.cb_measure_part(name, null, o) : -1;
            Console.WriteLine($"{label}: cyl -> {e} {(e == 0 ? Info() : Err())} | measure -> {m}" + (m == 0 ? $" | box X {o[4]:g}..{o[7]:g}, Y {o[5]:g}..{o[8]:g}, Z {o[6]:g}..{o[9]:g}" : ""));
            if (e != 0) worst = 1;
        }
        int ret = worst;
        e0 = ret;
        if (int.TryParse(Environment.GetEnvironmentVariable("CREO_KEEP"), out var s) && s > 0) { Console.WriteLine($"Creo stays open for {s} s ..."); Thread.Sleep(s * 1000); }
        return e0;
    }
}
