using InventorAddin.Gea;
using InventorAddin.Models;

namespace Cadflow.Application.Tests;

public class TransformerParamsTests
{
    [Fact]
    public void Defaults_are_valid()
    {
        var issues = TransformerParams.Validate(TransformerParams.Defaults());
        Assert.True(issues.Count == 0, string.Join("; ", issues.Select(i => i.Message)));
    }

    [Fact]
    public void Radiators_that_do_not_fit_flag_the_related_parameters()
    {
        var v = TransformerParams.Complete(new Dictionary<string, double> { [TransformerParams.RadiatorCount] = 6, [TransformerParams.RadiatorWidth] = 800 });
        var issue = Assert.Single(TransformerParams.Validate(v), i => i.Message.Contains("radiators do not fit"));
        Assert.Contains(TransformerParams.TankLength, issue.Params);
    }

    [Fact]
    public void Fractional_counts_are_rejected()
    {
        var v = TransformerParams.Complete(new Dictionary<string, double> { [TransformerParams.RadiatorCount] = 2.5 });
        Assert.Contains(TransformerParams.Validate(v), i => i.Message.Contains("whole number", StringComparison.OrdinalIgnoreCase) || i.Message.Contains("integer", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Unknown_keys_are_dropped_and_missing_ones_defaulted()
    {
        var v = TransformerParams.Complete(new Dictionary<string, double> { ["nope"] = 1, [TransformerParams.TankLength] = 4000 });
        Assert.False(v.ContainsKey("nope"));
        Assert.Equal(4000, v[TransformerParams.TankLength]);
        Assert.Equal(1100, v[TransformerParams.TankWidth]);
    }

    /// <summary>The C# rules and the rules inside transformer.gea must agree: the script builds the defaults on the simulation host.</summary>
    [Fact]
    public void Script_builds_the_defaults_on_the_simulation_host()
    {
        var scripts = Path.Combine(AppContext.BaseDirectory, "Scripts");
        var s = GeaScript.Compile(File.ReadAllText(Path.Combine(scripts, "transformer.gea")), GeaLibraryResolver.For(scripts, Path.Combine(scripts, "lib")), "transformer.gea");
        Assert.False(s.HasErrors, string.Join("\n", s.Diagnostics));
        var host = new GeaSimulationHost();
        var form = s.EvaluateFormNumbers(TransformerParams.Defaults());
        Assert.True(form.Ok, string.Join("; ", form.Errors.Select(x => x.Message)));
        var run = s.RunBuild(form, host);
        Assert.True(run.Ok, run.Error?.Message);
        Assert.Contains(host.Objects, o => o.Builder == "Bushing");
    }
}