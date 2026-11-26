using InventorAddin.Gea;

namespace Cadflow.CreoWorker.Creo;

/// <summary>
/// IGeaHost for Creo: runs the .gea builders through an <see cref="ICreoModeler"/>.
/// Script coordinates: X along the tank, D (depth) -> world Z, Elevation -> world Y (up).
/// Builders done: Block, Cylinder, Tank, Cover (all additive: a tank is a ring plus a bottom plate, a cover a lid plus a lip; one Creo part, bodies are merged).
/// Bushing and Radiator throw GEA4021 until implemented.
/// </summary>
public sealed class CreoGeaHost : IGeaHost
{
    public sealed class Body
    {
        public string Name { get; set; } = "";
        public string Builder { get; init; } = "";
        public Dictionary<string, GeaValue> Attributes { get; } = new(StringComparer.Ordinal);
        public string? Appearance { get; set; }
    }

    readonly ICreoModeler _m;
    int _counter;

    public CreoGeaHost(ICreoModeler modeler, string partName)
    {
        _m = modeler;
        _m.NewPart(partName);
    }

    public Dictionary<string, GeaValue> Parameters { get; } = new(StringComparer.Ordinal);
    public List<Body> Bodies { get; } = new();
    public List<string> Log { get; } = new();

    public void SyncParameters(IReadOnlyList<KeyValuePair<string, GeaValue>> values) { foreach (var (k, v) in values) Parameters[k] = v; }
    public void SetParam(string name, GeaValue value) => Parameters[name] = value;
    public void SetParamExpr(string name, string expression) =>
        throw new GeaRuntimeException("GEA4021", "SET_PARAM_EXPR is not supported by the Creo host yet.");

    public GeaValue GetParam(string name, GeaType type)
    {
        if (!Parameters.TryGetValue(name, out var v)) throw new GeaRuntimeException("GEA4099", $"Parameter '{name}' does not exist.");
        if (v.Type == type) return v;
        if (v.IsNumeric && type is GeaType.Integer or GeaType.Double or GeaType.Length or GeaType.Angle) return GeaValue.Number(type, v.Num);
        throw new GeaRuntimeException("GEA2002", $"Parameter '{name}' is {GeaUnits.TypeName(v.Type)}, it cannot be read as {GeaUnits.TypeName(type)}.");
    }

    static double N(IReadOnlyDictionary<string, GeaValue> i, string key, double fallback = 0) => i.TryGetValue(key, out var v) ? v.Num : fallback;

    public IReadOnlyDictionary<string, GeaRef> CreateFeature(BuilderManifest builder, GeaRef? on, IReadOnlyDictionary<string, GeaValue> inputs)
    {
        var extra = new Dictionary<string, GeaValue>(StringComparer.Ordinal);
        bool turretSame = false;
        try
        {
            switch (builder.Name)
            {
                case "Block": MakeBlock(inputs); break;
                case "Cylinder": MakeCylinder(inputs); break;
                case "Tank": MakeTank(inputs, extra); break;
                case "Cover": MakeCover(inputs, extra); break;
                case "Bushing": MakeBushing(inputs, out turretSame); break;
                default: throw new GeaRuntimeException("GEA4021", $"Builder '{builder.Name}' is not implemented in the Creo host yet.");
            }
        }
        catch (GeaRuntimeException) { throw; }
        catch (Exception ex) { throw new GeaRuntimeException("GEA4099", $"Creo failed in {builder.Name}: {ex.Message}"); }

        var body = new Body { Name = $"Body{++_counter}", Builder = builder.Name };
        Bodies.Add(body);
        var props = new Dictionary<string, GeaValue>(inputs, StringComparer.Ordinal);
        foreach (var (k, v) in extra) props[k] = v;
        return builder.Outputs.ToDictionary(o => o.Name, o => new GeaRef("BODY", body, builder.Name, props));
    }

    void MakeBlock(IReadOnlyDictionary<string, GeaValue> i)
    {
        double cx = N(i, "CenterX"), cd = N(i, "CenterD"), elev = N(i, "Elevation"), sx = N(i, "SizeX"), sd = N(i, "SizeD"), h = N(i, "Height");
        if (sx <= 0 || sd <= 0 || h <= 0) throw new GeaRuntimeException("GEA4099", "Block: SizeX, SizeD, Height must be positive.");
        double cr = N(i, "CornerRadius");
        // sketch on TOP: world X = -sx, world Z = sy
        if (cr > 0) _m.Prism("TOP", -(cx + sx / 2), cd - sd / 2, -(cx - sx / 2), cd + sd / 2, cr, null, h, elev);
        else _m.Block("TOP", -(cx + sx / 2), cd - sd / 2, -(cx - sx / 2), cd + sd / 2, h, elev);
    }

    /// <summary>
    /// Bushing profile (r, h) as in the Inventor host: turret, porcelain core with sheds shrinking upwards, optional cap. Starts and ends on the axis (r = 0).
    /// A separate turret body is not made in Creo yet (one revolve; the Turret output is the same body).
    /// </summary>
    public static List<(double R, double H)> BushingProfile(double hBase, double dia, double length, double turretH, int sheds, double turretDia, double turretTaper, double capHeight)
    {
        double rTurret = turretDia > 1e-6 ? turretDia / 2 : 0.6 * dia;
        double rCore = 0.2 * dia, rShed = 0.5 * dia;
        if (rTurret <= rCore + 1e-6) throw new GeaRuntimeException("GEA4099", "Bushing: TurretDiameter must be greater than 0.4 x Diameter (the porcelain core).");
        turretTaper = Math.Clamp(turretTaper, 0, 0.9 * turretH);
        double hTurretTop = hBase + turretH, pitch = length / sheds;
        var pts = new List<(double R, double H)> { (0, hBase), (rTurret, hBase) };
        if (turretTaper > 1e-6)
        {
            double rNeck = Math.Max(rCore * 1.4, rTurret * 0.4);
            pts.Add((rTurret, hTurretTop - turretTaper));
            pts.Add((rNeck, hTurretTop));
        }
        else pts.Add((rTurret, hTurretTop));
        pts.Add((rCore, hTurretTop));
        for (int i = 0; i < sheds; i++)
        {
            double h = hTurretTop + i * pitch;
            double tip = rShed * (1 - (sheds > 1 ? 0.3 * i / (sheds - 1) : 0));
            pts.Add((rCore, h + 0.15 * pitch));
            pts.Add((tip, h + 0.15 * pitch));
            pts.Add((tip, h + 0.25 * pitch));
            pts.Add((rCore, h + 0.60 * pitch));
        }
        double hTop = hTurretTop + length;
        pts.Add((rCore, hTop));
        if (capHeight > 1e-6)
        {
            double rCap = 0.32 * dia;
            pts.Add((rCap, hTop));
            pts.Add((rCap, hTop + capHeight * 0.5));
            pts.Add((rCore * 0.5, hTop + capHeight));
            pts.Add((0, hTop + capHeight));
        }
        else pts.Add((0, hTop));
        return pts;
    }

    void MakeBushing(IReadOnlyDictionary<string, GeaValue> i, out bool turretSame)
    {
        double cx = N(i, "CenterX"), cd = N(i, "CenterD"), elev = N(i, "Elevation"), dia = N(i, "Diameter"), len = N(i, "Length"), turret = N(i, "TurretHeight");
        int sheds = (int)Math.Round(N(i, "ShedCount"));
        if (dia <= 0 || len <= 0 || turret <= 0) throw new GeaRuntimeException("GEA4099", "Bushing: Diameter, Length, TurretHeight must be positive.");
        if (sheds < 1 || sheds > 100) throw new GeaRuntimeException("GEA4099", "Bushing: ShedCount must be from 1 to 100.");
        if (N(i, "TurretDiameter") < 0 || N(i, "TurretTaper") < 0 || N(i, "CapHeight") < 0)
            throw new GeaRuntimeException("GEA4099", "Bushing: TurretDiameter, TurretTaper, CapHeight must not be negative.");
        var prof = BushingProfile(elev, dia, len, turret, sheds, N(i, "TurretDiameter"), N(i, "TurretTaper"), N(i, "CapHeight"));
        // sketch on FRONT (X = sx, Y = sy), moved to depth cd; the axis is the vertical line x = cx
        _m.Revolve("FRONT", prof.Select(p => (cx + p.R, p.H)).ToList(), cx, prof[0].H, prof[^1].H, cd);
        turretSame = true;
        if (i.TryGetValue("SeparateTurret", out var sep) && sep.Bool) Log.Add("Bushing: SeparateTurret ignored (one body in Creo for now)");
    }

    // Open-top tank = ring (outer rounded rectangle with the cavity as a hole) from 0 to skirt+height, plus a solid plate of thickness Bottom on top of the skirt.
    void MakeTank(IReadOnlyDictionary<string, GeaValue> i, Dictionary<string, GeaValue> extra)
    {
        double len = N(i, "Length"), wid = N(i, "Width"), hgt = N(i, "Height"), wall = N(i, "Wall"), bottom = N(i, "Bottom");
        double cr = N(i, "CornerRadius"), ir = N(i, "InnerRadius"), skirt = N(i, "SkirtHeight");
        if (len <= 0 || wid <= 0 || hgt <= 0) throw new GeaRuntimeException("GEA4099", "Tank: Length, Width, Height must be positive.");
        if (wall <= 0 || 2 * wall >= Math.Min(len, wid)) throw new GeaRuntimeException("GEA4099", "Tank: Wall must be positive and smaller than half of the shorter side.");
        if (bottom <= 0 || bottom >= hgt) throw new GeaRuntimeException("GEA4099", "Tank: Bottom must be positive and smaller than Height.");
        if (cr < 0 || cr > Math.Min(len, wid) / 2) throw new GeaRuntimeException("GEA4099", "Tank: CornerRadius must not exceed half of the shorter side.");
        if (skirt < 0) throw new GeaRuntimeException("GEA4099", "Tank: SkirtHeight must not be negative.");
        double inX = len - 2 * wall, inD = wid - 2 * wall;
        ir = Math.Min(Math.Max(ir, 0), Math.Min(inX, inD) / 2);
        var hole = new InnerLoop(-inX / 2, -inD / 2, inX / 2, inD / 2, ir);
        _m.Prism("TOP", -len / 2, -wid / 2, len / 2, wid / 2, cr, hole, skirt + hgt, 0);
        _m.Prism("TOP", -len / 2, -wid / 2, len / 2, wid / 2, cr, null, bottom, skirt);
        extra["Top"] = GeaValue.Length(skirt + hgt);
        extra["InnerRadius"] = GeaValue.Length(ir);
    }

    // Lid on the opening (rounded rectangle) plus a lip hanging down into the tank.
    void MakeCover(IReadOnlyDictionary<string, GeaValue> i, Dictionary<string, GeaValue> extra)
    {
        var tank = i["Tank"].Ref ?? throw new GeaRuntimeException("GEA4099", "Cover: the Tank input is empty.");
        if (tank.Builder != "Tank") throw new GeaRuntimeException("GEA4099", "Cover: the Tank input must be a tank created by builder:Tank.");
        double T(string k) => tank.Props.TryGetValue(k, out var v) ? v.Num : 0;
        double len = T("Length"), wid = T("Width"), wall = T("Wall"), cr = T("CornerRadius"), ir = T("InnerRadius"), top = T("Top");
        double thick = N(i, "Thickness"), lip = N(i, "Lip"), clr = N(i, "Clearance"), gap = N(i, "Gap");
        if (thick <= 0) throw new GeaRuntimeException("GEA4099", "Cover: Thickness must be positive.");
        if (lip < 0 || clr < 0 || gap < 0) throw new GeaRuntimeException("GEA4099", "Cover: Lip, Clearance, Gap must not be negative.");
        double lipX = len - 2 * wall - 2 * clr, lipD = wid - 2 * wall - 2 * clr;
        if (lip > 1e-6 && (lipX <= 0 || lipD <= 0)) throw new GeaRuntimeException("GEA4099", "Cover: the clearance is too large, there is no room left for the lid lip.");
        double h0 = top + gap;
        _m.Prism("TOP", -len / 2, -wid / 2, len / 2, wid / 2, cr, null, thick, h0);
        if (lip > 1e-6) _m.Prism("TOP", -lipX / 2, -lipD / 2, lipX / 2, lipD / 2, Math.Max(ir - clr, 0), null, lip, h0 - lip);
        extra["Top"] = GeaValue.Length(h0 + thick);
    }

    void MakeCylinder(IReadOnlyDictionary<string, GeaValue> i)
    {
        double cx = N(i, "CenterX"), cd = N(i, "CenterD"), elev = N(i, "Elevation"), r = N(i, "Diameter") / 2, len = N(i, "Length");
        var axis = i.TryGetValue("Axis", out var a) && !string.IsNullOrEmpty(a.Str) ? a.Str!.ToUpperInvariant() : "D";
        switch (axis)
        {
            case "H": // vertical, bottom at Elevation: sketch on TOP
                _m.Cylinder("TOP", -cx, cd, r, len, elev, false); break;
            case "X": // along X, symmetric about CenterX, axis at Elevation: sketch on RIGHT (Z=-sx, Y=sy), starts at X = cx - len/2
                _m.Cylinder("RIGHT", -cd, elev, r, len, cx - len / 2, false); break;
            case "D": // along the depth (Z), symmetric about CenterD: sketch on FRONT (X=sx, Y=sy), starts at Z = cd - len/2
                _m.Cylinder("FRONT", cx, elev, r, len, cd - len / 2, false); break;
            default: throw new GeaRuntimeException("GEA4001", $"Cylinder Axis must be D, X or H (got '{axis}').");
        }
    }

    static Body B(GeaRef r) => (Body)r.Handle;
    public void SetRefName(GeaRef target, string name)
    {
        var self = B(target);
        self.Name = GeaNames.Resolve(name, n => Bodies.Any(o => o != self && o.Name == n));
    }
    public string GetRefName(GeaRef target) => B(target).Name;
    public void SetRefAttr(GeaRef target, string key, GeaValue value) => B(target).Attributes[key] = value;
    public GeaValue? GetRefAttr(GeaRef target, string key) => B(target).Attributes.TryGetValue(key, out var v) ? v : null;
    public void SetAppearance(GeaRef target, string appearance) => B(target).Appearance = appearance;
    public void Regen() => _m.FitView();
    public void Progress(GeaProgressKind kind, string? text, double? percent) => Log.Add($"{kind} {text} {percent}");
    public void Print(string message, int line) => Log.Add($"PRINT ({line}) {message}");
}
