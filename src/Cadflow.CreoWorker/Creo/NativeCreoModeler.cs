using System.Runtime.InteropServices;

namespace Cadflow.CreoWorker.Creo;

/// <summary>
/// Creo 10 through CreoBridge10.dll (Toolkit C async). Toolkit is single-threaded: create and use this object on ONE dedicated thread
/// (the bridge refuses other threads with -100). Creo must run as Administrator, so the process that hosts this must too.
/// </summary>
public sealed class NativeCreoModeler : ICreoModeler, IDisposable
{
    const string Dll = "CreoBridge10";
    const string Template = "mmns_part_solid_abs";
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int cb_start([MarshalAs(UnmanagedType.LPStr)] string cmd);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int cb_set_workdir([MarshalAs(UnmanagedType.LPStr)] string dir);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int cb_new_part([MarshalAs(UnmanagedType.LPStr)] string template, [MarshalAs(UnmanagedType.LPStr)] string name);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int cb_block([MarshalAs(UnmanagedType.LPStr)] string plane, [MarshalAs(UnmanagedType.LPStr)] string orient, [MarshalAs(UnmanagedType.LPStr)] string refA, [MarshalAs(UnmanagedType.LPStr)] string refB, double x0, double y0, double x1, double y1, double height, double elevation);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int cb_cylinder([MarshalAs(UnmanagedType.LPStr)] string plane, [MarshalAs(UnmanagedType.LPStr)] string orient, [MarshalAs(UnmanagedType.LPStr)] string refA, [MarshalAs(UnmanagedType.LPStr)] string refB, double cx, double cy, double r, double height, double elevation, int flip);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int cb_end();
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern IntPtr cb_last_error();

    const string CreoHome = @"C:\Program Files\PTC\Creo 10.0.1.0";
    bool _open;

    static string Err() => Marshal.PtrToStringAnsi(cb_last_error()) ?? "";
    static void Check(int rc, string what) { if (rc != 0) throw new InvalidOperationException($"{what} failed ({rc}): {Err()}"); }

    /// <summary>Starts Creo (about 12 s) and makes <paramref name="workDir"/> the working directory; copies the part template into it.</summary>
    public NativeCreoModeler(string workDir)
    {
        Directory.CreateDirectory(workDir);
        File.Copy(Path.Combine(CreoHome, @"Common Files\templates", Template + ".prt"), Path.Combine(workDir, Template + ".prt"), true);
        Environment.SetEnvironmentVariable("PRO_COMM_MSG_EXE", @"C:\PROGRA~1\PTC\CREO10~1.0\COMMON~1\X86E_W~1\obj\PRO_CO~1.EXE");
        var bin = Path.Combine(CreoHome, @"Parametric\bin");
        Check(cb_start($"\"{Path.Combine(bin, "parametric.exe")}\" \"{Path.Combine(bin, "parametric.psf")}\""), "cb_start");
        _open = true;
        Check(cb_set_workdir(workDir), "cb_set_workdir");
    }

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int cb_measure_part([MarshalAs(UnmanagedType.LPStr)] string name, [MarshalAs(UnmanagedType.LPStr)] string? step, double[] outValues);
    string _current = "";

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int cb_fit_view();
    /// <summary>Best effort: a failed refit never fails a job.</summary>
    public void FitView() => cb_fit_view();

    public void NewPart(string name) { Check(cb_new_part(Template, name), "cb_new_part"); _current = name; }

    /// <summary>Bounding box of the current part: (minX, minY, minZ, maxX, maxY, maxZ) in mm; optionally also writes a STEP file.</summary>
    public double[] Measure(string? stepPath = null)
    {
        var o = new double[10];
        Check(cb_measure_part(_current, stepPath, o), "cb_measure_part");
        return o[4..10];
    }

    // sketch plane -> (orientation datum, two reference datums)
    static (string orient, string a, string b) Refs(string plane) => plane switch
    {
        "TOP" => ("FRONT", "RIGHT", "FRONT"),
        "RIGHT" => ("TOP", "FRONT", "TOP"),
        "FRONT" => ("TOP", "RIGHT", "TOP"),
        _ => throw new ArgumentException("Unknown sketch plane " + plane),
    };

    public void Block(string plane, double sx0, double sy0, double sx1, double sy1, double height, double elevation)
    {
        var (o, a, b) = Refs(plane);
        Check(cb_block(plane, o, a, b, sx0, sy0, sx1, sy1, height, elevation), "cb_block");
    }

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int cb_prism([MarshalAs(UnmanagedType.LPStr)] string plane, [MarshalAs(UnmanagedType.LPStr)] string orient, [MarshalAs(UnmanagedType.LPStr)] string refA, [MarshalAs(UnmanagedType.LPStr)] string refB, double x0, double y0, double x1, double y1, double cornerR, int hasInner, double ix0, double iy0, double ix1, double iy1, double innerR, double height, double elevation);

    public void Prism(string plane, double sx0, double sy0, double sx1, double sy1, double cornerRadius, InnerLoop? inner, double height, double elevation)
    {
        var (o, a, b) = Refs(plane);
        var l = inner ?? default;
        Check(cb_prism(plane, o, a, b, sx0, sy0, sx1, sy1, cornerRadius, inner.HasValue ? 1 : 0, l.Sx0, l.Sy0, l.Sx1, l.Sy1, l.Radius, height, elevation), "cb_prism");
    }

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int cb_revolve([MarshalAs(UnmanagedType.LPStr)] string plane, [MarshalAs(UnmanagedType.LPStr)] string orient, [MarshalAs(UnmanagedType.LPStr)] string refA, [MarshalAs(UnmanagedType.LPStr)] string refB, double[] xy, int n, double axisX, double axisY0, double axisY1, double offset);

    public void Revolve(string plane, IReadOnlyList<(double X, double Y)> profile, double axisX, double axisY0, double axisY1, double offset)
    {
        var (o, a, b) = Refs(plane);
        var xy = new double[profile.Count * 2];
        for (int i = 0; i < profile.Count; i++) { xy[2 * i] = profile[i].X; xy[2 * i + 1] = profile[i].Y; }
        Check(cb_revolve(plane, o, a, b, xy, profile.Count, axisX, axisY0, axisY1, offset), "cb_revolve");
    }

    public void Cylinder(string plane, double cx, double cy, double radius, double height, double elevation, bool flip)
    {
        var (o, a, b) = Refs(plane);
        Check(cb_cylinder(plane, o, a, b, cx, cy, radius, height, elevation, flip ? 1 : 0), "cb_cylinder");
    }

    public void Dispose() { if (_open) { _open = false; cb_end(); } }
}
