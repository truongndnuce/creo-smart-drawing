using InventorAddin.Gea;

namespace Cadflow.Core.Tests;

/// <summary>box.gea compiles and runs on the simulation host: no CAD needed.</summary>
public class BoxScriptTests
{
    private static string BoxGea() => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Scripts", "box.gea"));

    [Fact]
    public void Box_script_compiles_without_errors()
    {
        var s = GeaScript.Compile(BoxGea());
        Assert.False(s.HasErrors, string.Join("\n", s.Diagnostics));
    }

    [Fact]
    public void Box_script_builds_body_and_lid_with_default_values()
    {
        var s = GeaScript.Compile(BoxGea());
        Assert.False(s.HasErrors, string.Join("\n", s.Diagnostics));
        var host = new GeaSimulationHost();
        var run = s.RunBuild(s.EvaluateFormNumbers(null), host);
        Assert.True(run.Ok, run.Error?.Message);
        Assert.NotEmpty(host.Objects);
    }
}
