using M2DPlus.Core;
using Rhino.Geometry;

namespace M2DPlus.Rhino.Services;

/// <summary>Clear bodies contribute their feature curves and view silhouettes, never a covering surface.</summary>
internal sealed class GlassGeometry : IDisposable
{
    private readonly List<GeometryBase> _owned = new();
    public IReadOnlyList<PreparedLeaf> Sources { get; }
    public GlassGeometry(IReadOnlyList<PreparedLeaf> sources, ViewRequest view)
    {
        if (view.Settings?.Glass != GlassMode.Clear) { Sources = sources; return; }
        var result = new List<PreparedLeaf>();
        try
        {
            foreach (var source in sources)
            {
                if (!source.Section.Transparent || source.Geometry is not (Brep or Mesh)) { result.Add(source); continue; }
                var curves = Features(source.Geometry, view);
                foreach (var curve in curves)
                { _owned.Add(curve);
                    result.Add(source with { Geometry = curve });
                }
            }
            Sources = result;
        }
        catch { Dispose(); throw; }
    }
    internal static Curve[] Features(GeometryBase geometry, ViewRequest view) => Silhouette.Compute(geometry,
        SilhouetteType.Boundary | SilhouetteType.Crease | SilhouetteType.Tangent | SilhouetteType.Projecting |
        (view.Settings?.Tangents == true ? SilhouetteType.NonSilhouetteTangent : SilhouetteType.None) |
        (view.Settings?.Seams == true ? SilhouetteType.NonSilhouetteSeam : SilhouetteType.None),
        view.Direction, view.Settings?.Tolerance ?? .001, .001).Select(s => s.Curve.DuplicateCurve()).ToArray();
    public void Dispose() { foreach (var geometry in _owned) geometry.Dispose(); }
}
