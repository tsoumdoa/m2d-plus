using M2DPlus.Core.Sections;
using M2DPlus.Rhino.Sections;
using Rhino.Geometry;
using Xunit;

namespace M2DPlus.Rhino.Tests;

[Collection("Rhino")]
public sealed class StableSectionTests(RhinoHost host)
{
    private const double Tol = .0001;
    private static SectionSource Source(Brep b) => new("curved", b, SectionStyleSnapshot.Default());

    [RhinoFact, Trait("Category", "RhinoIntegration")]
    public void SphereEquatorNeedsNoOffPlaneVerticesAndHasAnalyticArea() => host.Run(() => {
        using var sphere = new Sphere(Point3d.Origin, 1).ToBrep();
        using var result = SectionRegionEngine.Compute(new[] { Source(sphere) }, new(Plane.WorldXY, Tol, .001));
        Assert.False(result.RequiresQuarantine, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        var region = Assert.Single(result.Regions);
        Assert.InRange(Math.Abs(region.Area - Math.PI), 0, 2e-6);
        Assert.Single(region.Loops); Assert.NotNull(region.Hatch);
        Assert.Contains(result.Diagnostics, d => d.Message.StartsWith("D: perturbation stability", StringComparison.Ordinal));
        using var clip = PhysicalSectionClipper.Clip(Source(sphere), new(Plane.WorldXY, Tol, .001));
        Assert.True(clip.Succeeded, string.Join("; ", clip.Diagnostics.Select(d => d.Message)));
        Assert.All(clip.Pieces, p => Assert.True(p.Geometry.IsValid && p.Geometry.IsSolid));
    });

    [RhinoFact, Trait("Category", "RhinoIntegration")]
    public void CurvedTubeCutHasVoidAndIndependentTorusIntegral() => host.Run(() => {
        using var arc = new Arc(Plane.WorldXY, 4, Math.PI / 2).ToNurbsCurve();
        arc.Transform(Transform.Rotation(-Math.PI / 4, Vector3d.ZAxis, Point3d.Origin));
        var pipes = Brep.CreateThickPipe(arc, .45, .6, false, PipeCapMode.Flat, true, Tol, .001);
        try
        {
            var plane = new Plane(new Point3d(4, 0, 0), Vector3d.XAxis, new Vector3d(0, 1, 1));
            using var result = SectionRegionEngine.Compute(new[] { Source(Assert.Single(pipes)) }, new(plane, Tol, .001));
            Assert.False(result.RequiresQuarantine, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
            var region = Assert.Single(result.Regions); Assert.Equal(2, region.Loops.Count); Assert.Single(region.Loops, l => l.IsHole);
            Assert.InRange(Math.Abs(region.Area - .7029040164326703), 0, 2e-5);
        }
        finally { foreach (var pipe in pipes) pipe.Dispose(); }
    });

    [RhinoFact, Trait("Category", "RhinoIntegration")]
    public void TangentAndNearTangentSphereNeverGetsFill() => host.Run(() => {
        using var sphere = new Sphere(Point3d.Origin, 1).ToBrep();
        foreach (var offset in new[] { -2d, -.5, 0, .5, 2 })
        {
            var plane = new Plane(new Point3d(0, 0, 1 + offset * Tol), Vector3d.ZAxis);
            using var result = SectionRegionEngine.Compute(new[] { Source(sphere) }, new(plane, Tol, .001));
            Assert.Empty(result.Regions);
        }
    });

    [RhinoFact, Trait("Category", "RhinoIntegration")]
    public void OpenFreeformHasOnlyOutlines() => host.Run(() => {
        // Remove a cap from a generated curved pipe to keep a genuinely open shell.
        using var rail = new Arc(Plane.WorldXY, 4, Math.PI / 2).ToNurbsCurve();
        var parts = Brep.CreatePipe(rail, .2, false, PipeCapMode.None, true, Tol, .001);
        try
        {
            var plane = new Plane(new Point3d(3, 0, 0), Vector3d.XAxis);
            using var result = SectionRegionEngine.Compute(parts.Select((b, i) => new SectionSource(i.ToString(), b, SectionStyleSnapshot.Default())), new(plane, Tol, .001));
            Assert.NotEmpty(result.Regions);
            Assert.All(result.Regions, r => { Assert.Equal(SectionStatus.OutlineOnly, r.Status); Assert.Null(r.Hatch); Assert.Equal(0, r.Area); });
        }
        finally { foreach (var part in parts) part.Dispose(); }
    });

    [RhinoFact, Trait("Category", "RhinoIntegration")]
    public void TwistedRhsKeepsItsExactMidStationProfileAndHole() => host.Run(() => {
        using var file = global::Rhino.FileIO.File3dm.Read(Path.Combine(AppContext.BaseDirectory, "fixtures", "features", "curved-steel.3dm"));
        var solid = (Brep)file.Objects.Single(o => o.Attributes.Name == "twisted-RHS").Geometry;
        using var result = SectionRegionEngine.Compute(new[] { Source(solid) }, new(new Plane(new Point3d(20, 9, 3), Vector3d.ZAxis), Tol, .001));
        Assert.False(result.RequiresQuarantine, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        var region = Assert.Single(result.Regions);
        Assert.InRange(Math.Abs(region.Area - (.4 * .6 - .3 * .5)), 0, 2e-5);
        Assert.Equal(2, region.Loops.Count); Assert.Single(region.Loops, l => l.IsHole);
    });
}
