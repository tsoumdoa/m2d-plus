using M2DPlus.Core.Sections;
using M2DPlus.Rhino.Sections;
using M2DPlus.Rhino.Services;
using Rhino.Geometry;
using Xunit;

namespace M2DPlus.Rhino.Tests;

[Collection("Rhino")]
[Trait("Category", "RhinoIntegration")]
public sealed class SlabClipSectionTests(RhinoHost host)
{
    private const double Tol = .001;
    private static SectionValidation Slab(double z) => SectionValidation.For(new(
        new Plane(new Point3d(0, 0, z), Vector3d.ZAxis), Tol, .001));

    // Single-span ruled sides: their boxes span the low, sound cut even though
    // the upper ring folds through itself. Closed/manifold topology is deliberate.
    private static Brep FoldedPrism()
    {
        using var mesh = new Mesh();
        foreach (var p in new[] { (-1d, -1d), (1d, -1d), (1d, 1d), (-1d, 1d) }) mesh.Vertices.Add(p.Item1, p.Item2, 0);
        foreach (var p in new[] { (-1d, -1d), (1d, -1d), (-2d, 0d), (-1d, 1d) }) mesh.Vertices.Add(p.Item1, p.Item2, 4);
        for (var i = 0; i < 4; i++)
        {
            var j = (i + 1) % 4;
            mesh.Faces.AddFace(i, j, j + 4); mesh.Faces.AddFace(i, j + 4, i + 4);
        }
        mesh.Faces.AddFace(0, 3, 2, 1);
        mesh.Faces.AddFace(4, 5, 6); mesh.Faces.AddFace(4, 6, 7);
        mesh.Faces.ConvertQuadsToTriangles(); mesh.Normals.ComputeNormals();
        var result = Brep.CreateFromMesh(mesh, true);
        Assert.True(result.IsValid && result.IsManifold && result.IsSolid);
        return result;
    }

    [RhinoFact]
    public void InsideOutsideCrossingTangentAndFarBoundaryRetainContinuousProof() => host.Run(() =>
    {
        var slab = Slab(0);
        bool Check(Curve c, SectionValidation? v = null) => SectionGeometry.CoveredBySharedTopology(c, [], Tol, v ?? slab);
        using var inside = new LineCurve(new Point3d(-1, 0, .004), new Point3d(1, 0, .004));
        using var outside = new LineCurve(new Point3d(-1, 0, .006), new Point3d(1, 0, .006));
        using var crossing = new LineCurve(new Point3d(-1, 0, -.01), new Point3d(1, 0, .01));
        using var tangent = new ArcCurve(new Arc(new Point3d(-1, 0, 1.005), new Point3d(0, 0, .005), new Point3d(1, 0, 1.005)));
        Assert.False(Check(inside)); Assert.True(Check(outside)); Assert.False(Check(crossing)); Assert.False(Check(tangent));
        Assert.False(Check(outside, slab with { Far = new Plane(new Point3d(0, 0, .006), -Vector3d.ZAxis) }));
        Assert.False(slab.Excludes(BoundingBox.Unset));
        Assert.False(slab.Excludes(new BoundingBox(new Point3d(0, 0, .005), new Point3d(1, 1, .005))));
    });

    // First remote fold intersection begins at z=8/3. The six-vertex
    // section for t=z/4 has area 4 - 6t + 2t^2 (shoelace formula).
    private static Point3d[] Polygon(double z)
    {
        var t = z / 4;
        return [new(-1,-1,z), new(1,-1,z), new(1-3*t,-1+t,z),
            new(1-3*t,1-t,z), new(1-2*t,1,z), new(-1,1,z)];
    }

    [RhinoTheory]
    [InlineData(1)]
    [InlineData(2.6606666666666667)]
    public void RemoteFoldPublishesAnalyticSectionAndClosedClip(double z) => host.Run(() =>
    {
        using var shape = FoldedPrism(); using var cache = new SectionVerdictCache();
        var source = new SectionSource("synthetic-fold", shape, SectionStyleSnapshot.Default());
        var slab = Slab(z);
        Assert.Equal("", SectionGeometry.Verdict(source, Tol, cache, slab));
        CheckPublished(source, z, 1, Transform.Identity);
    });

    [RhinoTheory]
    [InlineData(2.6626666666666665)] // defect just inside the 5-tolerance slab
    [InlineData(2.6616666666666666)] // exact boundary must remain relevant
    [InlineData(3)] // the intersection crosses the slab
    public void FoldAtInsideBoundaryAndCrossingStillRefuses(double z) => host.Run(() =>
    {
        using var shape = FoldedPrism(); using var cache = new SectionVerdictCache();
        var source = new SectionSource("synthetic-fold", shape, SectionStyleSnapshot.Default());
        Assert.StartsWith("Self-intersecting Brep faces", SectionGeometry.Verdict(source, Tol, cache, Slab(z)));
        using var result = SectionRegionEngine.Compute([source], new(Slab(z).Near, Tol, .001), prepareVisibility: true);
        Assert.Empty(result.Regions); Assert.NotEmpty(result.Refusals); Assert.False(result.Visibility[source.Id].Succeeded);
    });

    [RhinoFact]
    public void FarPlaneProtectsItsOwnMaterialBoundary() => host.Run(() =>
    {
        using var shape = FoldedPrism(); using var cache = new SectionVerdictCache();
        var source = new SectionSource("synthetic-fold", shape, SectionStyleSnapshot.Default());
        var near = Slab(5);
        Assert.Equal("", SectionGeometry.Verdict(source, Tol, cache, near));
        Assert.StartsWith("Self-intersecting Brep faces", SectionGeometry.Verdict(source, Tol, cache,
            near with { Far = new Plane(new Point3d(0, 0, 2.6626666666666665), -Vector3d.ZAxis) }));
    });

    [RhinoTheory]
    [InlineData(2, false)]
    [InlineData(2, true)]
    public void ScaledAndMirroredBlocksRetainLocalSlabsAndVisibility(double scale, bool mirror) => host.Run(() =>
    {
        using var shape = FoldedPrism(); using var doc = global::Rhino.RhinoDoc.CreateHeadless(null);
        doc.ModelAbsoluteTolerance = Tol;
        var definition = doc.InstanceDefinitions.Add("synthetic-fold", "", Point3d.Origin, [shape], [new global::Rhino.DocObjects.ObjectAttributes()]);
        var placement = Transform.Scale(Point3d.Origin, scale);
        if (mirror) placement *= Transform.Mirror(Plane.WorldYZ);
        doc.Objects.AddInstanceObject(definition, placement);
        using var sceneCache = new SceneCache(); using var lease = BaselineTests.Capture(doc, sceneCache);
        var leaf = Assert.Single(lease.Value.Prepare(default));
        var source = new SectionSource("synthetic-block", leaf.Geometry, SectionStyleSnapshot.Default()) { Canonical = leaf.Canonical };
        Assert.NotNull(source.Canonical);
        using var verdicts = new SectionVerdictCache();
        Assert.Equal("", SectionGeometry.Verdict(source, Tol, verdicts, Slab(scale)));
        CheckPublished(source, 1, scale, placement);
    });

    private static void CheckPublished(SectionSource source, double localZ, double scale, Transform placement)
    {
        var z = localZ * scale;
        using var sections = SectionRegionEngine.Compute([source], new(Slab(z).Near, Tol, .001), prepareVisibility: true);
        var clip = sections.Visibility[source.Id];
        Assert.True(clip.Succeeded, string.Join("; ", sections.Diagnostics.Select(d => d.Message)));
        Assert.NotEmpty(clip.Pieces);
        Assert.All(clip.Pieces, p => Assert.True(p.Geometry.IsValid && p.Geometry.IsSolid));
        var region = Assert.Single(sections.Regions);
        Assert.Single(region.Loops); Assert.False(region.Loops[0].IsHole); Assert.True(region.Loops[0].Curve.IsClosed);
        var t = localZ / 4;
        Assert.InRange(Math.Abs(region.Area - (4 - 6*t + 2*t*t) * scale*scale), 0, 1e-6);
        using var analyticLoop = new PolylineCurve(Polygon(localZ).Append(Polygon(localZ)[0]));
        analyticLoop.Transform(placement);
        Assert.True(Curve.GetDistancesBetweenCurves(analyticLoop, region.Loops[0].Curve, Tol/10,
            out var distance, out _, out _, out _, out _, out _) && distance <= Tol);

        // Independent analytic edge loci for the retained polyhedron, including
        // its two noncoplanar triangle creases. Hidden and visible are both on.
        var expected = new List<Curve>();
        void Edge(Point3d a, Point3d b)
        {
            a.Transform(placement); b.Transform(placement); a.Z = 0; b.Z = 0;
            if (a.DistanceTo(b) > Tol) expected.Add(new LineCurve(a,b));
        }
        var bottom = new[] { new Point3d(-1,-1,0), new Point3d(1,-1,0), new Point3d(1,1,0), new Point3d(-1,1,0) };
        for (var i = 0; i < 4; i++) Edge(bottom[i], bottom[(i+1)%4]);
        var top = Polygon(localZ);
        for (var i = 0; i < top.Length; i++) Edge(top[i], top[(i+1)%top.Length]);
        Edge(bottom[2], top[3]); Edge(bottom[1], top[2]);
        try
        {
            using var doc = global::Rhino.RhinoDoc.CreateHeadless(null); doc.ModelAbsoluteTolerance = Tol;
            foreach (var piece in clip.Pieces) doc.Objects.AddBrep(piece.Geometry);
            using var cache = new SceneCache(); using var scene = BaselineTests.Capture(doc, cache);
            using var drawing = NativeDrawing.SolveBorrowedAsync(scene.Value, new("synthetic", "top", "test", 1),
                [EngineNativeTests.View("hidden=on")], new(), default, null).GetAwaiter().GetResult();
            var view = Assert.Single(drawing.Views); Assert.Null(view.Error); Assert.NotEmpty(view.Items);
            var geometry = view.Items.Select(p => p.CopyGeometry()).ToArray();
            var actual = geometry.OfType<Curve>().ToArray();
            try
            {
                foreach (var c in actual) c.Transform(Transform.PlanarProjection(Plane.WorldXY));
                foreach (var c in actual) Assert.True(SectionGeometry.CoveredBySharedTopology(c, expected.ToArray(), Tol), $"Unexpected projected locus {c.PointAtStart} -> {c.PointAtEnd}; frame={view.Frame.Plane}");
                foreach (var c in expected) Assert.True(SectionGeometry.CoveredBySharedTopology(c, actual, Tol), "Missing analytic projected locus");
            }
            finally { foreach (var g in geometry) g.Dispose(); }
        }
        finally { foreach (var c in expected) c.Dispose(); }
    }

    [RhinoFact]
    public void PolicyIdentityIsCapturedInValidationAndTimingKeys() => host.Run(() =>
    {
        if (Environment.GetEnvironmentVariable("M2D_EXPECT_RUNTIME") is { } expectedRuntime)
            Assert.Equal(int.Parse(expectedRuntime), Environment.Version.Major);
        const string policy = "near-cut-intersection-portions-v1";
        var slab = Slab(0);
        Assert.Equal(policy, SectionValidation.Policy);
        Assert.StartsWith(policy + "|", slab.Key);
        Assert.Equal(slab.Key, slab.Transform(Transform.Identity).Key);
        Assert.NotEqual(slab.Key, Slab(1).Key);
        var view = ViewRequest.Parse(Plane.WorldXY);
        var settings = new NativeSettings();
        var fingerprint = WorkerCodec.Fingerprint();
        var expected = WorkerCodec.Hash(WorkerCodec.Encode(new { fingerprint, Policy = HldProjection.Policy, settings.CullSources,
            View = view with { Settings = null }, Settings = view.Settings?.VisibilityKey() })
            .Concat(System.Text.Encoding.UTF8.GetBytes(policy)).ToArray());
        Assert.Equal(expected, WorkerCodec.TimingKey(view, settings, fingerprint));
        using var shape = FoldedPrism(); using var cache = new SectionVerdictCache();
        var source = new SectionSource("synthetic-cache", shape, SectionStyleSnapshot.Default());
        Assert.Equal("", SectionGeometry.Verdict(source, Tol, cache, Slab(1)));
        Assert.StartsWith("Self-intersecting Brep faces", SectionGeometry.Verdict(source, Tol, cache, Slab(3)));
        Assert.Equal("", SectionGeometry.Verdict(source, Tol, cache, Slab(1)));
        Assert.Equal(2, cache.LocalComputations);
    });
}
