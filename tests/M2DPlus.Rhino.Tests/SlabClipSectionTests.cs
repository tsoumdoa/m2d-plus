using M2DPlus.Core;
using M2DPlus.Core.Sections;
using M2DPlus.Rhino.Sections;
using M2DPlus.Rhino.Services;
using Rhino.Geometry;
using Xunit;
using Xunit.Abstractions;

namespace M2DPlus.Rhino.Tests;

[Collection("Rhino")]
[Trait("Category", "RhinoIntegration")]
public sealed class SlabClipSectionTests(RhinoHost host, ITestOutputHelper output)
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
    [InlineData(0, false)]
    [InlineData(5, false)]
    [InlineData(3, false)]
    [InlineData(0, true)]
    [InlineData(5, true)]
    [InlineData(3, true)]
    public void RetainedFoldKeepsSectionAndBackgroundHonest(double farZ, bool cavity) => host.Run(() =>
    {
        using var shape = FoldedPrism();
        if (cavity)
        {
            // A separate, inward shell wholly inside the sound lower body.
            using var hole = Brep.CreateFromBox(new BoundingBox(new Point3d(-.8,-.8,.5), new Point3d(-.6,-.6,2)));
            hole.Flip(); shape.Append(hole);
        }
        var near = new Plane(new Point3d(0,0,1), -Vector3d.ZAxis);
        Plane? far = farZ == 0 ? null : new Plane(new Point3d(0,0,farZ), Vector3d.ZAxis);
        var source = new SectionSource("retained-fold", shape, SectionStyleSnapshot.Default());
        using var verdicts = new SectionVerdictCache();
        var verdict = SectionGeometry.Verdict(source, Tol, verdicts, SectionValidation.For(new(near, Tol, .001), far));
        output.WriteLine($"far={farZ}; cavity={cavity}; verdict={verdict}");
        using var section = SectionRegionEngine.Compute([source], new(near, Tol, .001), prepareVisibility: true, farPlane: far);
        var clip = section.Visibility[source.Id];
        output.WriteLine($"clip={clip.Succeeded}; regions={section.Regions.Count}; " + string.Join("; ", section.Diagnostics.Select(d => d.Message)));
        if (farZ == 3)
        {
            Assert.StartsWith("Self-intersecting Brep faces", verdict);
            Assert.False(clip.Succeeded); Assert.Empty(clip.Pieces); Assert.Empty(section.Regions);
        }
        else
        {
            Assert.Equal("", verdict);
            // Rhino 8.36 Route C succeeds with the remote fold retained. Pin that
            // outcome: the cap/region is analytic, and independent probes below
            // must be hidden by material, with certain visibility outside it.
            Assert.True(clip.Succeeded); Assert.NotEmpty(clip.Pieces);
            Assert.All(clip.Pieces, p => Assert.True(p.Geometry.IsValid && p.Geometry.IsSolid));
            var region = Assert.Single(section.Regions);
            Assert.Equal(cavity ? 2 : 1, region.Loops.Count);
            Assert.Equal(cavity ? 1 : 0, region.Loops.Count(l => l.IsHole));
            Assert.InRange(Math.Abs(region.Area - (2.625 - (cavity ? .04 : 0))), 0, 1e-6);
            using var expected = new PolylineCurve(Polygon(1).Append(Polygon(1)[0]));
            Assert.True(Curve.GetDistancesBetweenCurves(expected, region.Loops.Single(l => !l.IsHole).Curve, Tol/10,
                out var distance, out _, out _, out _, out _, out _) && distance <= Tol);
            bool MaterialAt(Point3d p) => region.Loops.Any(l => !l.IsHole && l.Curve.Contains(p, near, Tol) == PointContainment.Inside) &&
                !region.Loops.Any(l => l.IsHole && l.Curve.Contains(p, near, Tol) == PointContainment.Inside);
            Assert.True(MaterialAt(new(-.9,-.7,1)));
            Assert.Equal(!cavity, MaterialAt(new(-.7,-.7,1)));
            var caps = clip.Pieces.SelectMany(p => p.Geometry.Faces).Where(f =>
                f.TryGetPlane(out var fp, Tol) && Math.Abs(near.DistanceTo(fp.Origin)) <= Tol &&
                Vector3d.CrossProduct(fp.Normal, near.Normal).Length <= .001).ToArray();
            Assert.NotEmpty(caps);
            Assert.InRange(Math.Abs(caps.Sum(f => { using var b = f.DuplicateFace(false); return SectionGeometry.Area(b); }) - region.Area), 0, 1e-6);
        }

        using var doc = global::Rhino.RhinoDoc.CreateHeadless(null); doc.ModelAbsoluteTolerance = Tol;
        using var behind = new LineCurve(new Point3d(-.9,-.9,4.5), new Point3d(-.6,-.9,4.5));
        using var outside = new LineCurve(new Point3d(2.5,0,4.5), new Point3d(3,0,4.5));
        var solidId = doc.Objects.AddBrep(shape);
        var behindId = doc.Objects.AddCurve(behind).ToString();
        var outsideId = doc.Objects.AddCurve(outside).ToString();
        var pointId = doc.Objects.AddPoint(new Point3d(-.7,-.85,4.5)).ToString();
        var outsidePointId = doc.Objects.AddPoint(new Point3d(3,.5,4.5)).ToString();
        string? voidCurveId = null, voidPointId = null;
        if (cavity)
        {
            // Before the cavity's closed roof at z=2, these see the opening in
            // the cap. The material probes above stay outside the hole.
            voidCurveId = doc.Objects.AddLine(new Point3d(-.77,-.75,1.5), new Point3d(-.63,-.75,1.5)).ToString();
            voidPointId = doc.Objects.AddPoint(new Point3d(-.7,-.65,1.5)).ToString();
        }
        var request = ViewRequest.Parse(near);
        // Only the solid participates: the probes remain behind it even when far clips at z=3.
        using var cache = new SceneCache(); using var scene = BaselineTests.Capture(doc, cache);
        request = request with { Cut = request.Cut! with { ScopeEnabled = true, Objects = [solidId],
            SourceDepth = farZ == 0 ? null : farZ - 1, FarClip = farZ == 0 ? FarClipMode.None : FarClipMode.NoLine } };
        using var drawing = BaselineTests.Solve(scene, request);
        var view = Assert.Single(drawing.Views); output.WriteLine(view.Info); Assert.Null(view.Error);
        var background = view.Items.Where(p => p.SourceIdentity == behindId).ToArray();
        if (!clip.Succeeded)
        {
            Assert.NotEmpty(background);
            Assert.All(background, p => Assert.Equal(LineClass.Unresolved, p.Class));
            Assert.DoesNotContain(view.Items, p => p.SourceIdentity == solidId.ToString() && p.Class != LineClass.Unresolved);
            // The established point failure policy suppresses uncertain points;
            // only curves carry Unresolved, and Info declares the failed clip.
            Assert.DoesNotContain(view.Items, p => p.SourceIdentity == pointId);
            Assert.Contains("Section clip failed:", view.Info);
            Assert.Empty(view.Regions);
        }
        else
        {
            Assert.NotEmpty(background); Assert.All(background, p => Assert.Equal(LineClass.Hidden, p.Class));
            Assert.InRange(Math.Abs(background.Sum(p => { using var g = p.CopyGeometry(); return Assert.IsAssignableFrom<Curve>(g).GetLength(); }) - .3), 0, Tol);
            Assert.Equal(1, view.HiddenPoints);
            Assert.DoesNotContain(view.Items, p => p.SourceIdentity == pointId);
            Assert.InRange(Math.Abs(Assert.Single(view.Regions).Region.Area - (cavity ? 2.585 : 2.625)), 0, 1e-6);
            if (cavity)
            {
                var throughVoid = view.Items.Where(p => p.SourceIdentity == voidCurveId).ToArray();
                Assert.NotEmpty(throughVoid); Assert.All(throughVoid, p => Assert.Equal(LineClass.Visible, p.Class));
                Assert.InRange(Math.Abs(throughVoid.Sum(p => { using var g = p.CopyGeometry(); return Assert.IsAssignableFrom<Curve>(g).GetLength(); }) - .14), 0, Tol);
                Assert.Contains(view.Items, p => p.SourceIdentity == voidPointId && p.Class == LineClass.Visible);
            }
        }
        Assert.Contains(view.Items, p => p.SourceIdentity == outsidePointId && p.Class == LineClass.Visible);
        Assert.Contains(view.Items, p => p.SourceIdentity == outsideId && p.Class == LineClass.Visible);
    });

    [RhinoTheory]
    [InlineData(1)]
    public void RemoteFoldPublishesAnalyticSectionAndClosedClip(double z) => host.Run(() =>
    {
        using var shape = FoldedPrism(); using var cache = new SectionVerdictCache();
        var source = new SectionSource("synthetic-fold", shape, SectionStyleSnapshot.Default());
        var slab = Slab(z);
        Assert.Equal("", SectionGeometry.Verdict(source, Tol, cache, slab));
        CheckPublished(source, z, 1, Transform.Identity);
    });

    [RhinoFact]
    public void BorderlineRemoteFoldWithUnresolvedBoundarySectionFallsBack() => host.Run(() =>
    {
        using var shape = FoldedPrism(); using var cache = new SectionVerdictCache();
        var source = new SectionSource("borderline-fold", shape, SectionStyleSnapshot.Default());
        var verdict = SectionGeometry.Verdict(source, Tol, cache, Slab(2.6606666666666667));
        output.WriteLine(verdict);
        Assert.StartsWith("Self-intersecting Brep faces", verdict);
        Assert.Contains("guard unresolved; whole-pair fallback", verdict);
    });

    [RhinoTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void MixedRemoteAndSharedTopologyProofWorksInBothDirections(bool reverse) => host.Run(() =>
    {
        using var curve = new LineCurve(new Point3d(0,0,-.02), new Point3d(0,0,.02));
        if (reverse) curve.Reverse();
        using var shared = new LineCurve(new Point3d(0,0,-.008), new Point3d(0,0,.008));
        Assert.True(SectionGeometry.CoveredBySharedTopology(curve, [shared], Tol, Slab(0), out var excluded));
        Assert.True(excluded); // Recursive successes must trigger the pair guard.
        using var first = new LineCurve(new Point3d(0,0,-.008), new Point3d(0,0,-.002));
        using var last = new LineCurve(new Point3d(0,0,.002), new Point3d(0,0,.008));
        Assert.False(SectionGeometry.CoveredBySharedTopology(curve, [first,last], Tol, Slab(0)));
    });

    [RhinoTheory]
    [InlineData(0, false)]
    [InlineData(.005, false)]
    [InlineData(.0055, true)]
    public void RationalMultiSpanBoundsProtectInteriorDipAndBoundary(double minimum, bool excluded) => host.Run(() =>
    {
        using var curve = new NurbsCurve(3, true, 3, 5);
        var heights = new[] { .02, minimum, minimum, minimum, .02 };
        for (var i = 0; i < heights.Length; i++) curve.Points.SetPoint(i, new Point3d(i-2,0,heights[i]), i == 2 ? 2 : 1);
        for (var i = 0; i < curve.Knots.Count; i++) curve.Knots[i] = i / 2;
        Assert.True(curve.IsValid && curve.IsRational && curve.SpanCount > 1);
        Assert.True(curve.PointAtStart.Z > .005 && curve.PointAtEnd.Z > .005);
        Assert.Equal(minimum, curve.PointAt(1).Z, 12);
        Assert.Equal(excluded, SectionGeometry.CoveredBySharedTopology(curve, [], Tol, Slab(0)));
    });

    [RhinoTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void TranslatedObliqueNearFarBoxesAndIsolatedPointsRespectBothSignedBoundaries(bool reverse) => host.Run(() =>
    {
        var normal = new Vector3d(2d/7,-3d/7,6d/7);
        if (reverse) normal = -normal;
        var near = new Plane(new Point3d(10,-20,30), normal);
        var far = new Plane(near.Origin + 2*normal, -normal);
        var validation = SectionValidation.For(new(near, Tol, .001), far);
        foreach (var plane in new[] { near, far })
        foreach (var sign in new[] { -1, 1 })
        foreach (var distance in new[] { .004, .005, .006 })
        {
            // Degenerate boxes are exactly the native isolated-point predicate.
            var p = plane.Origin + sign * distance * plane.Normal;
            var box = new BoundingBox(p,p);
            Assert.Equal(distance > .005, validation.Excludes(box));
            Assert.Equal(distance < .005, validation.Contains(box));
            // Nondegenerate boxes exercise mixed-sign extrema, rather than just their centres.
            box.Inflate(.0001);
            Assert.Equal(distance > .005, validation.Excludes(box));
            Assert.Equal(distance < .005, validation.Contains(box));
            var corners = box.GetCorners().Select(plane.DistanceTo).ToArray();
            Assert.True(corners.Min() < sign*distance && corners.Max() > sign*distance);
        }
        var crossing = new BoundingBox(new[] { near.Origin - .01*normal, near.Origin + .01*normal });
        Assert.False(validation.Excludes(crossing)); Assert.False(validation.Contains(crossing));
    });

    [RhinoFact]
    public void NativeObliqueTranslatedCutPreservesAnalyticSection() => host.Run(() =>
    {
        using var shape = FoldedPrism();
        var placement = Transform.Translation(12,-17,23) * Transform.Rotation(.7, new Vector3d(1,-2,3), Point3d.Origin);
        Assert.True(shape.Transform(placement));
        var plane = Slab(1).Near; Assert.True(plane.Transform(placement));
        var source = new SectionSource("oblique-fold", shape, SectionStyleSnapshot.Default());
        using var sections = SectionRegionEngine.Compute([source], new(plane, Tol, .001), prepareVisibility: true);
        Assert.True(sections.Visibility[source.Id].Succeeded, string.Join("; ", sections.Diagnostics.Select(d => d.Message)));
        var region = Assert.Single(sections.Regions); Assert.Single(region.Loops);
        Assert.InRange(Math.Abs(region.Area - 2.625), 0, 1e-6);
        using var expected = new PolylineCurve(Polygon(1).Append(Polygon(1)[0])); expected.Transform(placement);
        Assert.True(Curve.GetDistancesBetweenCurves(expected, region.Loops[0].Curve, Tol/10,
            out var distance, out _, out _, out _, out _, out _) && distance <= Tol);
    });

    [RhinoFact]
    public void NonuniformBlockFallbackUsesOwnGeometryAndSlabs() => host.Run(() =>
    {
        using var shape = FoldedPrism(); using var doc = global::Rhino.RhinoDoc.CreateHeadless(null);
        doc.ModelAbsoluteTolerance = Tol;
        var definition = doc.InstanceDefinitions.Add("nonuniform-fold", "", Point3d.Origin, [shape], [new global::Rhino.DocObjects.ObjectAttributes()]);
        doc.Objects.AddInstanceObject(definition, Transform.Scale(Plane.WorldXY, 2, 3, 4));
        using var cache = new SceneCache(); using var scene = BaselineTests.Capture(doc, cache);
        var leaf = Assert.Single(scene.Value.Prepare(default)); Assert.Null(leaf.Canonical);
        var source = new SectionSource("nonuniform-fold", leaf.Geometry, SectionStyleSnapshot.Default());
        using var verdicts = new SectionVerdictCache();
        Assert.Equal("", SectionGeometry.Verdict(source, Tol, verdicts, Slab(4)));
        Assert.StartsWith("Self-intersecting Brep faces", SectionGeometry.Verdict(source, Tol, verdicts, Slab(12)));
        using var sections = SectionRegionEngine.Compute([source], new(Slab(4).Near, Tol, .001), prepareVisibility: true);
        Assert.True(sections.Visibility[source.Id].Succeeded);
        Assert.InRange(Math.Abs(Assert.Single(sections.Regions).Area - 2.625*6), 0, 1e-6);
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
        // A world slab near twice the local fold distinguishes the instance frame
        // from an accidentally untransformed canonical plane.
        var fold = Slab(scale * 2.6626666666666665) with { Margin = .005 * scale };
        Assert.StartsWith("Self-intersecting Brep faces", SectionGeometry.Verdict(source, Tol * scale, verdicts, fold));
        // At fixed physical tolerance the margin does not scale with the block.
        Assert.StartsWith("Self-intersecting Brep faces", SectionGeometry.Verdict(source, Tol, verdicts, Slab(scale * (8d/3) - .004)));
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
    public void ObliqueParallelCurveExcludesInOneNodeAndEquivalentFramesAgree() => host.Run(() =>
    {
        // Retain the previous world-box proof in this test as a node-count
        // baseline; this is a bounded correctness check, never a timing gate.
        (bool Verdict, int Nodes) WorldProof(Curve curve, SectionValidation slab)
        {
            var budget = 262144; var nodes = 0;
            bool Cover(Curve piece, int depth)
            {
                nodes++; if (--budget < 0) return false;
                var box = piece.GetBoundingBox(true);
                if (slab.Excludes(box)) return true;
                if (slab.Contains(box) || depth == 32) return false;
                using var a = piece.Trim(piece.Domain.Min, piece.Domain.Mid);
                using var b = piece.Trim(piece.Domain.Mid, piece.Domain.Max);
                return a is not null && b is not null && Cover(a, depth+1) && Cover(b, depth+1);
            }
            return (Cover(curve, 0), nodes);
        }
        var normal = new Vector3d(1,1,1); normal.Unitize();
        var initial = new Plane(Point3d.Origin, normal);
        using var baseline = new LineCurve(initial.PointAt(-150,0) + .007*normal, initial.PointAt(150,0) + .007*normal);
        var old = WorldProof(baseline, Slab(0) with { Near = initial });
        output.WriteLine($"world-box baseline: verdict={old.Verdict}; nodes={old.Nodes}");
        Assert.True(old.Nodes > 100000);
        foreach (var move in new[] { Transform.Identity, Transform.Translation(10000,-20000,30000), Transform.Mirror(Plane.WorldYZ) })
        foreach (var inPlane in new[] { false, true })
        foreach (var distance in new[] { .007, .005, .005 + 1e-12 })
        {
            var plane = initial; Assert.True(plane.Transform(move));
            using var curve = new LineCurve(initial.PointAt(-150,0) + distance*normal, initial.PointAt(150,0) + distance*normal);
            curve.Transform(move);
            var same = plane;
            if (inPlane) { same.Origin += 16*plane.XAxis - 8*plane.YAxis; same = new Plane(same.Origin, plane.YAxis, -plane.XAxis); }
            // Floating-point re-expression can perturb the equation by a few
            // ULPs. Domain padding covers it; exact equal keys use identical axes.
            var bounds = curve.GetBoundingBox(true);
            var slab = new SectionValidation(same, null, .005).WithBounds(bounds);
            using var work = new SectionWork();
            var verdict = SectionGeometry.CoveredBySharedTopology(curve, [], Tol, slab);
            output.WriteLine($"frame={inPlane}; distance={distance:R}; verdict={verdict}; nodes={work.CoverageNodes}");
            Assert.Equal(distance == .007, verdict);
            if (verdict) Assert.Equal(1, work.CoverageNodes);
        }
        // A nonparallel far plane gets its own canonical frame.
        var far = new Plane(new Point3d(0,0,.007), Vector3d.ZAxis);
        var both = new SectionValidation(initial, far, .005).WithBounds(baseline.GetBoundingBox(true));
        Assert.False(SectionGeometry.CoveredBySharedTopology(baseline, [], Tol, both));
        var canonical = new SectionValidation(Plane.WorldXY, null, .005);
        var authored = canonical with { Near = new Plane(new Point3d(16,8,0), Vector3d.ZAxis) };
        Assert.Equal(canonical.Key, authored.Key);
        Assert.Equal(canonical.Domains(baseline.GetBoundingBox(true))[0].Frame, authored.Domains(baseline.GetBoundingBox(true))[0].Frame);
    });

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
