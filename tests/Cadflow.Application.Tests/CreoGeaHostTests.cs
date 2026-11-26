using Cadflow.CreoWorker.Creo;
using InventorAddin.Gea;

namespace Cadflow.Application.Tests;

/// <summary>The Creo host turns builder inputs into sketch-plane operations; checked here against a recording modeler (no Creo needed).</summary>
public class CreoGeaHostTests
{
    static Dictionary<string, GeaValue> In(params (string, GeaValue)[] kv) => kv.ToDictionary(x => x.Item1, x => x.Item2);
    static GeaValue L(double v) => GeaValue.Length(v);

    static (CreoGeaHost host, RecordingCreoModeler rec) Make()
    {
        var rec = new RecordingCreoModeler();
        return (new CreoGeaHost(rec, "p1"), rec);
    }

    [Fact]
    public void Block_maps_X_to_minus_sketch_x_and_depth_to_sketch_y()
    {
        var (h, rec) = Make();
        h.CreateFeature(GeaBuilderCatalog.Find("Block")!, null, In(("CenterX", L(100)), ("CenterD", L(10)), ("Elevation", L(5)), ("SizeX", L(40)), ("SizeD", L(20)), ("Height", L(30))));
        Assert.Equal("NewPart p1", rec.Calls[0]);
        Assert.Equal("Block TOP [-120,0]-[-80,20] h=30 elev=5", rec.Calls[1]);
    }

    [Theory]
    [InlineData("H", "Cylinder TOP c=(-100,10) r=15 h=80 elev=20 flip=False")]
    [InlineData("X", "Cylinder RIGHT c=(-10,20) r=15 h=80 elev=60 flip=False")]   // starts at X = 100 - 40
    [InlineData("D", "Cylinder FRONT c=(100,20) r=15 h=80 elev=-30 flip=False")]  // starts at Z = 10 - 40
    public void Cylinder_axis_picks_plane_and_start(string axis, string expected)
    {
        var (h, rec) = Make();
        h.CreateFeature(GeaBuilderCatalog.Find("Cylinder")!, null, In(("CenterX", L(100)), ("CenterD", L(10)), ("Elevation", L(20)), ("Diameter", L(30)), ("Length", L(80)), ("Axis", GeaValue.Text(axis))));
        Assert.Equal(expected, rec.Calls[1]);
    }

    [Fact]
    public void Builders_not_done_yet_report_GEA4021()
    {
        var (h, _) = Make();
        var ex = Assert.Throws<GeaRuntimeException>(() => h.CreateFeature(GeaBuilderCatalog.Find("Radiator")!, null, In()));
        Assert.Equal("GEA4021", ex.Code);
    }

    [Fact]
    public void Bushing_is_one_revolve_on_FRONT_with_the_axis_at_CenterX_and_depth_as_offset()
    {
        var (h, rec) = Make();
        h.CreateFeature(GeaBuilderCatalog.Find("Bushing")!, null, In(("CenterX", L(500)), ("CenterD", L(-200)), ("Elevation", L(1000)), ("Diameter", L(100)), ("Length", L(300)),
            ("TurretHeight", L(60)), ("ShedCount", GeaValue.Int(3)), ("CapHeight", L(20))));
        Assert.Equal("Revolve FRONT n=21 axis x=500 y=1000..1380 offset=-200", rec.Calls[1]);
        var p = rec.LastProfile;
        Assert.Equal((500.0, 1000.0), p[0]);           // starts on the axis at the bottom of the turret
        Assert.Equal((500.0, 1380.0), p[^1]);          // ends on the axis at the top of the cap
        Assert.Equal(560.0, p[1].X);                    // turret radius 0.6 x 100 (x = CenterX + r)
        Assert.All(p, q => Assert.InRange(q.X, 500, 560));
    }

    [Fact]
    public void Bushing_with_a_turret_that_is_too_thin_is_rejected()
    {
        var (h, _) = Make();
        Assert.Throws<GeaRuntimeException>(() => h.CreateFeature(GeaBuilderCatalog.Find("Bushing")!, null,
            In(("Diameter", L(100)), ("Length", L(300)), ("TurretHeight", L(60)), ("ShedCount", GeaValue.Int(3)), ("TurretDiameter", L(30)))));
    }

    [Fact]
    public void Tank_is_a_ring_plus_a_bottom_plate_and_cover_sits_on_its_top()
    {
        var (h, rec) = Make();
        var tank = h.CreateFeature(GeaBuilderCatalog.Find("Tank")!, null, In(("Length", L(400)), ("Width", L(200)), ("Height", L(150)), ("Wall", L(10)), ("Bottom", L(12)),
            ("CornerRadius", L(30)), ("InnerRadius", L(20)), ("SkirtHeight", L(20))))["Body"];
        Assert.Equal("Prism TOP [-200,-100]-[200,100] cr=30 hole [-190,-90]-[190,90] r=20 h=170 elev=0", rec.Calls[1]);
        Assert.Equal("Prism TOP [-200,-100]-[200,100] cr=30 h=12 elev=20", rec.Calls[2]);

        h.CreateFeature(GeaBuilderCatalog.Find("Cover")!, null, In(("Tank", GeaValue.Reference(tank)), ("Thickness", L(15)), ("Lip", L(10)), ("Clearance", L(2)), ("Gap", L(0))));
        Assert.Equal("Prism TOP [-200,-100]-[200,100] cr=30 h=15 elev=170", rec.Calls[3]);
        Assert.Equal("Prism TOP [-188,-88]-[188,88] cr=18 h=10 elev=160", rec.Calls[4]);
    }

    [Fact]
    public void Tank_with_a_wall_that_is_too_thick_is_rejected()
    {
        var (h, _) = Make();
        Assert.Throws<GeaRuntimeException>(() => h.CreateFeature(GeaBuilderCatalog.Find("Tank")!, null,
            In(("Length", L(100)), ("Width", L(60)), ("Height", L(40)), ("Wall", L(30)), ("Bottom", L(5)))));
    }
}
