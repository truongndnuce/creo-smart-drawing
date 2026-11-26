namespace Cadflow.CreoWorker.Creo;

/// <summary>
/// The modelling operations the Creo host needs, in Creo's own terms: a sketch plane (TOP/RIGHT/FRONT), sketch coordinates,
/// an extrusion along the plane normal. <see cref="NativeCreoModeler"/> runs them in Creo 10 through CreoBridge10;
/// <see cref="RecordingCreoModeler"/> only records them (tests).
/// Facts measured in Creo 10 (docs/spike-toolkit.md): sketch to world is TOP: X=-sx, Z=sy; RIGHT: Z=-sx, Y=sy; FRONT: X=sx, Y=sy.
/// An extrusion starts at <c>elevation</c> from the plane along its +axis (Y, X, Z) and runs <c>height</c>; flip goes the other way.
/// </summary>
public interface ICreoModeler
{
    void NewPart(string name);
    /// <summary>Zooms the window so the whole model is visible (call when a model is finished).</summary>
    void FitView();
    void Block(string plane, double sx0, double sy0, double sx1, double sy1, double height, double elevation);
    void Cylinder(string plane, double cx, double cy, double radius, double height, double elevation, bool flip);
    /// <summary>Rounded rectangle (corner radius, 0 = square); with <paramref name="inner"/> the section is a ring (inner rounded rectangle is a hole).</summary>
    void Prism(string plane, double sx0, double sy0, double sx1, double sy1, double cornerRadius, InnerLoop? inner, double height, double elevation);
    /// <summary>
    /// Full revolution of a closed profile (sketch coordinates on <paramref name="plane"/>, itself offset by <paramref name="offset"/> along its normal).
    /// The segment from the last point back to the first lies on the axis: the vertical line sx = <paramref name="axisX"/>, from <paramref name="axisY0"/> to <paramref name="axisY1"/>.
    /// </summary>
    void Revolve(string plane, IReadOnlyList<(double X, double Y)> profile, double axisX, double axisY0, double axisY1, double offset);
}

public readonly record struct InnerLoop(double Sx0, double Sy0, double Sx1, double Sy1, double Radius);

public sealed class RecordingCreoModeler : ICreoModeler
{
    public List<string> Calls { get; } = new();
    public void NewPart(string name) => Calls.Add($"NewPart {name}");
    public void FitView() => Calls.Add("FitView");
    public void Block(string plane, double sx0, double sy0, double sx1, double sy1, double height, double elevation) =>
        Calls.Add($"Block {plane} [{sx0:g},{sy0:g}]-[{sx1:g},{sy1:g}] h={height:g} elev={elevation:g}");
    public void Prism(string plane, double sx0, double sy0, double sx1, double sy1, double cornerRadius, InnerLoop? inner, double height, double elevation) =>
        Calls.Add($"Prism {plane} [{sx0:g},{sy0:g}]-[{sx1:g},{sy1:g}] cr={cornerRadius:g}" +
                  (inner is { } l ? $" hole [{l.Sx0:g},{l.Sy0:g}]-[{l.Sx1:g},{l.Sy1:g}] r={l.Radius:g}" : "") + $" h={height:g} elev={elevation:g}");
    public List<(double X, double Y)> LastProfile { get; private set; } = new();
    public void Revolve(string plane, IReadOnlyList<(double X, double Y)> profile, double axisX, double axisY0, double axisY1, double offset)
    {
        LastProfile = profile.ToList();
        Calls.Add($"Revolve {plane} n={profile.Count} axis x={axisX:g} y={axisY0:g}..{axisY1:g} offset={offset:g}");
    }
    public void Cylinder(string plane, double cx, double cy, double radius, double height, double elevation, bool flip) =>
        Calls.Add($"Cylinder {plane} c=({cx:g},{cy:g}) r={radius:g} h={height:g} elev={elevation:g} flip={flip}");
}
