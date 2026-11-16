using System.Text.Json;
using M2DPlus.Core.Sections;
using M2DPlus.Rhino.Sections;
using Rhino.Geometry;
using Xunit;

namespace M2DPlus.Rhino.Tests;

[Collection("Rhino")]
[Trait("Category", "RhinoIntegration")]
public sealed class CurvedSectionRegressionTests(RhinoHost host)
{
    private const double Tol = .0001;

    [RhinoTheory]
    [InlineData("diagrid-iso-1-0")]
    [InlineData("diagrid-iso-1-3")]
    public void PlanTrimRetainsCertifiedOriginalRimsWithoutUnattributedEdges(string name) => host.Run(() =>
    {
        using var file = global::Rhino.FileIO.File3dm.Read(Path.Combine(AppContext.BaseDirectory,"fixtures","features","diagrid-shell.3dm"));
        var bounds = BoundingBox.Empty;
        foreach(var obj in file.Objects) bounds.Union(obj.Geometry.GetBoundingBox(true));
        var plane = new Plane(new Point3d(0,0,bounds.Min.Z+Math.Min(1.2,bounds.Diagonal.Z*.4)),Vector3d.ZAxis);
        var shape = file.Objects.Single(o=>o.Attributes.Name==name).Geometry;
        using var clip = PhysicalSectionClipper.Clip(new(name,shape,SectionStyleSnapshot.Default()),new(plane,Tol,.001));
        Assert.True(clip.Succeeded,string.Join("; ",clip.Diagnostics.Select(d=>d.Message)));
        Assert.NotEmpty(clip.Pieces);
        Assert.All(clip.Pieces,p=>
        {
            Assert.True(p.Geometry.IsSolid && p.Geometry.IsValid);
            Assert.All(p.Edges,e=>Assert.NotEqual(EdgeOrigin.None,e.Origin));
            Assert.True(p.Geometry.GetBoundingBox(Transform.PlaneToPlane(plane,Plane.WorldXY)).Max.Z<=Tol);
        });
    });

    [RhinoTheory]
    [InlineData("S-split-rim-local")]
    [InlineData("M-partial-rim-world")]
    public void OriginalCurvedPerfRimsPassTheTopologyGuard(string name) => host.Run(() =>
    {
        using var file = global::Rhino.FileIO.File3dm.Read(Path.Combine(AppContext.BaseDirectory, "fixtures", "sections", "shared-rims.3dm"));
        var shape = file.Objects.Single(o => o.Attributes.Name == name).Geometry;
        using var valid = SectionGeometry.Convert(shape, Tol);
        Assert.True(valid.IsValid && valid.IsSolid);
    });

    [RhinoFact]
    public void SplitRimCylinderAndPartialRimAreProvenOnSharedTopology() => host.Run(() =>
    {
        using var cylinder = new Cylinder(new Circle(Plane.WorldXY, .25), 1).ToBrep(true, true);
        foreach (var edge in cylinder.Edges.Where(e => e.IsClosed).OrderByDescending(e => e.EdgeIndex).ToArray())
            Assert.True(cylinder.Edges.SplitEdgeAtParameters(edge.EdgeIndex, new[] { edge.Domain.Mid }) > 0);
        // Native splitting leaves the new vertex tolerances unset until repair.
        cylinder.Compact(); cylinder.Repair(Tol);
        Assert.True(cylinder.IsValid && cylinder.IsSolid);
        using var validated = SectionGeometry.Convert(cylinder, Tol);
        var rim = cylinder.Edges.Where(e => e.IsInPlane(Plane.WorldXY, Tol)).ToArray();
        Assert.Equal(2, rim.Length);
        using var whole = new Circle(Plane.WorldXY, .25).ToNurbsCurve();
        Assert.True(SectionGeometry.CoveredBySharedTopology(whole, rim, Tol));
        // A native overlap may cover 94% of a rim; prove the omitted portion independently.
        using var omitted = whole.Trim(whole.Domain.ParameterAt(.94), whole.Domain.Max);
        Assert.True(SectionGeometry.CoveredBySharedTopology(omitted, rim, Tol));
        using var offRim = new LineCurve(new Point3d(.25, 0, 0), new Point3d(.25, .02, 0));
        Assert.False(SectionGeometry.CoveredBySharedTopology(offRim, rim, Tol));
        using var noFill = SectionRegionEngine.Compute(new[] { new SectionSource("cylinder", cylinder, SectionStyleSnapshot.Default()) },
            new(new Plane(new Point3d(0, 0, .5), Vector3d.ZAxis), Tol, .001));
        Assert.False(noFill.RequiresQuarantine);
        Assert.InRange(Math.Abs(Assert.Single(noFill.Regions).Area - Math.PI / 16), 0, 1e-6);
    });

    [RhinoFact]
    public void GenuinelySelfIntersectingSweepIsStillRejected() => host.Run(() =>
    {
        // Independent witness: opposite walls meet at (1,1,z), away from any shared rim.
        using var rail = new LineCurve(Point3d.Origin, new Point3d(0, 0, 2));
        using var profile = new PolylineCurve(new[] { Point3d.Origin, new Point3d(2, 2, 0),
            new Point3d(2, 0, 0), new Point3d(0, 2, 0), Point3d.Origin });
        var sweeps = Brep.CreateFromSweep(rail, profile, false, Tol);
        try
        {
            var sweep = Assert.Single(sweeps);
            sweep.Faces.SplitKinkyFaces();
            Assert.True(sweep.IsValid && sweep.IsManifold);
            var failure = Assert.Throws<SectionFailure>(() => SectionGeometry.Convert(sweep, Tol));
            Assert.Contains("Self-intersecting Brep faces", failure.Message);
            using var result = SectionRegionEngine.Compute(new[] { new SectionSource("crossed", sweep, SectionStyleSnapshot.Default()) },
                new(new Plane(new Point3d(0, 0, 1), Vector3d.ZAxis), Tol, .001), prepareVisibility: true);
            Assert.True(result.RequiresQuarantine); Assert.Empty(result.Regions);
            Assert.Empty(result.Visibility["crossed"].Pieces);
        }
        finally { foreach (var sweep in sweeps) sweep.Dispose(); }
    });

    [RhinoTheory]
    [InlineData("curved-steel", "RHS-arc", 2, .09, 2)]
    [InlineData("curved-steel", "RHS-arc", 3, .09, 2)]
    [InlineData("curved-steel", "I-beam-filleted-web-flanges", 3, .43862654824574365, 1)]
    [InlineData("curved-facade", "curved-mullion-2", 2, .0025132741228718345, 2)]
    [InlineData("diagrid-shell", "diagrid-iso-0-0", 1, .008595397500221673, 2)]
    [InlineData("diagrid-shell", "diagrid-iso-0-0", 2, .008595397500221673, 2)]
    public void FrozenNormalStationHasAnalyticAreaHolesAndClosedRetainedSolid(string model, string name, int station,
        double area, int loopCount) => host.Run(() =>
    {
        using var file = global::Rhino.FileIO.File3dm.Read(Path.Combine(AppContext.BaseDirectory, "fixtures", "features", model + ".3dm"));
        using var definitions = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "view-cases", "view-" + model + ".json")));
        var spec = definitions.RootElement.GetProperty("views").EnumerateArray()
            .Single(v => v.GetProperty("name").GetString() == $"Normal {name} station {station}").GetProperty("plane");
        double[] Coordinates(string key) => spec.GetProperty(key).EnumerateArray().Select(v => v.GetDouble()).ToArray();
        var o = Coordinates("origin"); var x = Coordinates("x"); var y = Coordinates("y");
        var plane = new Plane(new Point3d(o[0], o[1], o[2]), new Vector3d(x[0], x[1], x[2]), new Vector3d(y[0], y[1], y[2]));
        var geometry = file.Objects.Single(obj => obj.Attributes.Name == name).Geometry;
        using var result = SectionRegionEngine.Compute(new[] { new SectionSource(name, geometry, SectionStyleSnapshot.Default()) },
            new(plane, Tol, .001), prepareVisibility: true);
        Assert.False(result.RequiresQuarantine, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        var region = Assert.Single(result.Regions);
        Assert.InRange(Math.Abs(region.Area - area), 0, 2e-6);
        Assert.Equal(loopCount, region.Loops.Count);
        Assert.Equal(loopCount - 1, region.Loops.Count(l => l.IsHole));
        Assert.NotNull(region.Hatch);
        Assert.All(region.Loops, l => Assert.True(l.Curve.IsClosed && l.Curve.IsInPlane(plane, Tol)));
        var clip = result.Visibility[name];
        Assert.True(clip.Succeeded); Assert.NotEmpty(clip.Pieces);
        Assert.All(clip.Pieces, p =>
        {
            Assert.True(p.Geometry.IsValid && p.Geometry.IsSolid);
            Assert.True(p.Geometry.GetBoundingBox(Transform.PlaneToPlane(plane, Plane.WorldXY)).Max.Z <= Tol);
            Assert.All(p.Edges, e => Assert.NotEqual(EdgeOrigin.None, e.Origin));
        });
        Assert.Contains(result.Diagnostics, d => d.Message.StartsWith(StableSectionCertificate.Route, StringComparison.Ordinal));
    });

    [RhinoFact]
    public void ProjectionDoesNotAuthorizeABowTieOrAnOffPlaneLoop() => host.Run(() =>
    {
        using var crossed = new PolylineCurve(new[] { Point3d.Origin, new Point3d(2, 2, 0),
            new Point3d(2, 0, 0), new Point3d(0, 2, 0), Point3d.Origin });
        Assert.Throws<SectionFailure>(() => SectionGeometry.PlanarRegions(new[] { crossed }, new(Plane.WorldXY, Tol, .001)));
        using var circle = new Circle(new Plane(new Point3d(0, 0, .01), Vector3d.ZAxis), 1).ToNurbsCurve();
        Assert.Throws<SectionFailure>(() => SectionGeometry.PlanarRegions(new[] { circle }, new(Plane.WorldXY, Tol, .001)));
    });
}

