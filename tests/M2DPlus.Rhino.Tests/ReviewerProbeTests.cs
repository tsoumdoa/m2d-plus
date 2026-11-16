using M2DPlus.Core.Sections;
using M2DPlus.Rhino.Sections;
using Rhino.Geometry;
using Xunit;
using Xunit.Abstractions;

namespace M2DPlus.Rhino.Tests;

// Adopted reviewer regression: a capsule (cylinder + hemispherical dome) cut in
// plan well below the dome. The dome's sphere never reaches the section slab.
[Collection("Rhino")]
[Trait("Category", "RhinoIntegration")]
public sealed class ReviewerProbeTests(RhinoHost host, ITestOutputHelper output)
{
    [RhinoTheory]
    [InlineData(0d)]
    [InlineData(1e5)]
    [InlineData(1e6)]
    public void CapsulePlanCutBelowDomeIsCertified(double offset) => host.Run(() =>
    {
        using var cylinder = new Cylinder(new Circle(Plane.WorldXY, .1), 1).ToBrep(true, true);
        using var ball = new Sphere(new Point3d(0, 0, 1), .1).ToBrep();
        var union = Brep.CreateBooleanUnion([cylinder, ball], 1e-6);
        Assert.NotNull(union); using var capsule = Assert.Single(union);
        output.WriteLine($"solid={capsule.IsSolid}; faces={capsule.Faces.Count}; sphere faces={capsule.Faces.Count(f => f.TryGetSphere(out _, 1e-9))}");
        Assert.True(capsule.IsSolid);
        Assert.True(capsule.Transform(Transform.Translation(offset, offset, offset)));
        using var result = SectionRegionEngine.Compute([new SectionSource("capsule", capsule, SectionStyleSnapshot.Default())],
            new SectionRequest(new Plane(new Point3d(offset, offset, offset + .5), Vector3d.ZAxis), 1e-4, .001), prepareVisibility: true);
        foreach (var d in result.Diagnostics) output.WriteLine($"{d.Status}: {d.Message}");
        Assert.False(result.RequiresQuarantine, string.Join("; ", result.Diagnostics.Select(d => d.Status + ": " + d.Message)));
        var region = Assert.Single(result.Regions);
        Assert.InRange(Math.Abs(region.Area - Math.PI * .01), 0, 1e-7);
    });

    // Sol's round-2 scenario: a box with an exact square cut and a near-tangent sphere inside it.
    // Report what the drawing shows at the refused sphere's location (Info-only or Unresolved ink).
    [RhinoTheory]
    [InlineData(0d)]
    [InlineData(1e5)]
    [InlineData(1e6)]
    public void RefusedSourceInsideCertifiedRegionLeavesADrawingMarker(double offset) => host.Run(() =>
    {
        using var document = global::Rhino.RhinoDoc.CreateHeadless(null); document.ModelAbsoluteTolerance = 1e-4;
        using var box = Brep.CreateFromBox(new BoundingBox(new Point3d(-2, -2, -1), new Point3d(2, 2, 1)));
        using var tangent = new Sphere(new Point3d(0, 0, 1 - 2e-4), 1).ToBrep();
        Assert.True(box.Transform(Transform.Translation(offset, offset, offset)));
        Assert.True(tangent.Transform(Transform.Translation(offset, offset, offset)));
        var inputs = new[] { new M2DPlus.Rhino.Services.CaptureInput(box, [0], "certified"), new M2DPlus.Rhino.Services.CaptureInput(tangent, [1], "ambiguous") };
        using var cache = new M2DPlus.Rhino.Services.SceneCache(); using var lease = BaselineTests.Capture(document, cache, inputs);
        using var batch = BaselineTests.Solve(lease, M2DPlus.Rhino.Services.ViewRequest.Parse(new Plane(new Point3d(offset, offset, offset), Vector3d.ZAxis)));
        var view = Assert.Single(batch.Views); Assert.Null(view.Error);
        var footprint = new BoundingBox(new Point3d(offset - .03, offset - .03, offset - 10), new Point3d(offset + .03, offset + .03, offset + 10));
        int unresolvedNear = 0, unresolved = 0;
        foreach (var p in view.Items)
        {
            if (p.Class != M2DPlus.Core.LineClass.Unresolved) continue;
            unresolved++;
            using var g = p.CopyGeometry();
            var b = g.GetBoundingBox(true);
            if (b.Min.X <= footprint.Max.X && b.Max.X >= footprint.Min.X && b.Min.Y <= footprint.Max.Y && b.Max.Y >= footprint.Min.Y) unresolvedNear++;
        }
        output.WriteLine($"regions={view.Regions.Count}; unresolved pieces={unresolved}; unresolved touching sphere cut footprint={unresolvedNear}; classes={string.Join(",", view.Items.GroupBy(p => p.Class).Select(g => g.Key + ":" + g.Count()))}");
        foreach (var d in view.Diagnostics.Where(d => d.Contains("ambiguous", StringComparison.Ordinal))) output.WriteLine("diag: " + d);
        var region=Assert.Single(view.Regions).Region;
        Assert.Equal(16,region.Area,8);Assert.NotNull(region.Hatch);
        Assert.True(unresolvedNear > 0, $"Nothing Unresolved marks the refused source (regions={view.Regions.Count}, unresolved={unresolved}).");
        Assert.Contains(view.Items,p=>p.SourceIdentity=="ambiguous" && p.Class==M2DPlus.Core.LineClass.Unresolved);
        Assert.DoesNotContain(view.Regions,r=>r.Region.SourceIds.Contains("ambiguous"));
    });
}
