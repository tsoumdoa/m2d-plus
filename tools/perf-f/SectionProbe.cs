using System.Text.Json;
using M2DPlus.Core.Sections;
using M2DPlus.Rhino.Sections;
using M2DPlus.Rhino.Services;
using Rhino.Geometry;

namespace M2DPlus.PerfF;

/// <summary>Read-only inventory and section certificates, separate from native visibility.</summary>
internal static class SectionProbe
{
    internal static void Run(IReadOnlyList<PreparedLeaf> sources, ViewRequest view, double tolerance, string path)
    {
        var rows = new List<object>();
        foreach (var source in sources.Where(s => s.Geometry is Brep or Mesh))
        {
            using var section = SectionRegionEngine.Compute([new(source.Identity, source.Geometry, SectionStyleSnapshot.Default())],
                new(view.Cut!.Plane, tolerance, .001), prepareVisibility: true);
            rows.Add(new { source.Identity, type = source.Geometry.GetType().Name,
                solid = source.Geometry is Brep b ? b.IsSolid : ((Mesh)source.Geometry).IsClosed,
                section.Components, section.Diagnostics,
                regions = section.Regions.Select(r => new { r.Area, loops = r.Loops.Count, holes = r.Loops.Count(l => l.IsHole) }),
                clips = section.Visibility.Values.Select(c => new { c.Succeeded, pieces = c.Pieces.Count }) });
        }
        File.WriteAllText(path, JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));
    }
}
