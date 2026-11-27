using M2DPlus.Core.Sections;
using M2DPlus.Rhino.Sections;
using M2DPlus.Rhino.Services;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;
using Xunit;

namespace M2DPlus.Rhino.Tests;

[Collection("Rhino")]
public sealed class SectionIntersectionCacheTests(RhinoHost host)
{
    private static Brep Face(Plane plane)
    { using var surface = new PlaneSurface(plane, new Interval(-2, 2), new Interval(-3, 3)); return surface.ToBrep(); }

    [RhinoFact] public void NativeResultsHaveIndependentOwnersAndOutliveTheCache() => host.Run(() =>
    {
        using var first = Face(Plane.WorldXY); using var second = Face(Plane.WorldYZ);
        using var cache = new SectionIntersectionCache();
        var calls = 0;
        FacePairIntersections Compute()
        {
            calls++;
            // BrepBrep can populate serialized input caches. Production supplies fresh DuplicateFace inputs;
            // keep this fixture's fingerprinted face values unchanged in the same way.
            using var nativeFirst = first.DuplicateBrep(); using var nativeSecond = second.DuplicateBrep();
            var success = Intersection.BrepBrep(nativeFirst, nativeSecond, .001, out var curves, out var points);
            return new(success, curves, points);
        }
        using var a = cache.Get(first, second, .001, Compute);
        Assert.True(a.Success); Assert.NotEmpty(a.Curves);
        var before = a.Curves.Select(GeometryFingerprint.Compute).ToArray();
        a.Curves[0].Translate(new Vector3d(100, 0, 0));
        using var b = cache.Get(first, second, .001, Compute);
        Assert.Equal(before, b.Curves.Select(GeometryFingerprint.Compute));
        Assert.Equal(1, calls); Assert.Equal(1, cache.Hits);
        cache.Dispose();
        Assert.Equal(0, cache.Count); Assert.Equal(0, cache.RetainedBytes);
        Assert.All(b.Curves, c => Assert.True(c.IsValid));
        Assert.Throws<ObjectDisposedException>(() => cache.Get(first, second, .001, Compute));
    });

    [RhinoFact] public void OrderedContentAndToleranceKeyEveryResultAndPointArraysAreCopied() => host.Run(() =>
    {
        using var first = Face(Plane.WorldXY); using var second = Face(Plane.WorldYZ);
        using var cache = new SectionIntersectionCache();
        var calls = 0;
        FacePairIntersections Compute() { calls++; return new(true, [], [new Point3d(1, 2, 3)]); }
        using var a = cache.Get(first, second, .001, Compute);
        a.Points[0] = Point3d.Origin;
        using var same = cache.Get(first, second, .001, Compute);
        Assert.Equal(new Point3d(1, 2, 3), same.Points[0]); Assert.Equal(1, calls);
        using var swapped = cache.Get(second, first, .001, Compute);
        using var tolerance = cache.Get(first, second, .002, Compute);
        Assert.True(first.Translate(new Vector3d(.125, 0, 0)));
        using var changed = cache.Get(first, second, .001, Compute);
        Assert.Equal(4, calls); Assert.Equal(4, cache.Count);
    });

    [RhinoFact] public void FailedResultsAndEntriesOverBudgetAreNeverRetained() => host.Run(() =>
    {
        using var first = Face(Plane.WorldXY); using var second = Face(Plane.WorldYZ);
        var calls = 0;
        FacePairIntersections Failed() { calls++; return new(false, [], []); }
        using var failures = new SectionIntersectionCache();
        using var a = failures.Get(first, second, .001, Failed);
        using var b = failures.Get(first, second, .001, Failed);
        Assert.Equal(2, calls); Assert.Equal(0, failures.Count);
        using var bounded = new SectionIntersectionCache(maximumBytes: 16);
        FacePairIntersections Success() { calls++; return new(true, [new LineCurve(Point3d.Origin, new Point3d(1, 2, 3))], []); }
        using var c = bounded.Get(first, second, .001, Success);
        using var d = bounded.Get(first, second, .001, Success);
        Assert.Equal(4, calls); Assert.Equal(0, bounded.Count); Assert.Equal(0, bounded.RetainedBytes);
    });

    [RhinoFact] public void TrimOrientationAndScaleParticipateInRealFaceKeys() => host.Run(() =>
    {
        using var first = Face(Plane.WorldXY); using var second = Face(Plane.WorldYZ);
        using var boundary = new Rectangle3d(Plane.WorldXY, new Interval(-1, 1), new Interval(-2, 2)).ToNurbsCurve();
        using var split = first.Faces[0].Split([boundary], .001);
        Assert.NotNull(split);
        using var trimmed = split.Faces.Single(f => f.Loops.Count == 1).DuplicateFace(false);
        using var reversed = first.DuplicateBrep(); reversed.Flip();
        using var scaled = first.DuplicateBrep(); Assert.True(scaled.Transform(Transform.Scale(Point3d.Origin, 2)));
        using var cache = new SectionIntersectionCache();
        var calls = 0;
        FacePairIntersections Compute() { calls++; return new(true, [], [Point3d.Origin]); }
        using var original = cache.Get(first, second, .001, Compute);
        using var same = cache.Get(first, second, .001, Compute);
        using var trim = cache.Get(trimmed, second, .001, Compute);
        using var orientation = cache.Get(reversed, second, .001, Compute);
        using var scale = cache.Get(scaled, second, .001, Compute);
        Assert.Equal(4, calls); Assert.Equal(4, cache.Count); Assert.Equal(1, cache.Hits);
    });

    [RhinoFact] public void NativeMutationCannotChangeTheStoredInputKey() => host.Run(() =>
    {
        using var first = Face(Plane.WorldXY); using var second = Face(Plane.WorldYZ);
        using var fresh = first.DuplicateBrep(); using var freshSecond = second.DuplicateBrep();
        using var cache = new SectionIntersectionCache();
        var calls = 0;
        FacePairIntersections Compute()
        {
            calls++;
            var success = Intersection.BrepBrep(first, second, .001, out var curves, out var points);
            Assert.True(first.Translate(new Vector3d(10, 0, 0)));
            return new(success, curves, points);
        }
        using var initial = cache.Get(first, second, .001, Compute);
        Assert.True(initial.Success); Assert.NotEmpty(initial.Curves);
        using var repeated = cache.Get(fresh, freshSecond, .001, Compute);
        Assert.Equal(1, calls); Assert.Equal(1, cache.Hits);
        Assert.Equal(initial.Curves.Select(GeometryFingerprint.Compute), repeated.Curves.Select(GeometryFingerprint.Compute));
    });

    [RhinoFact] public void EntryLimitAndInvalidResultsReturnUncachedOwnedResults() => host.Run(() =>
    {
        using var first = Face(Plane.WorldXY); using var second = Face(Plane.WorldYZ);
        using var cache = new SectionIntersectionCache(maximumEntries: 1);
        var calls = 0;
        FacePairIntersections Compute() { calls++; return new(true, [], [new Point3d(calls, 0, 0)]); }
        using var retained = cache.Get(first, second, .001, Compute);
        using var refused = cache.Get(second, first, .001, Compute);
        using var again = cache.Get(second, first, .001, Compute);
        Assert.Equal(3, calls); Assert.Equal(1, cache.Count);
        Assert.Equal(new Point3d(2, 0, 0), refused.Points[0]); Assert.Equal(new Point3d(3, 0, 0), again.Points[0]);
        using var invalidCache = new SectionIntersectionCache();
        FacePairIntersections Invalid() { calls++; return new(true, [new PolylineCurve()], []); }
        using var invalid = invalidCache.Get(first, second, .001, Invalid);
        using var invalidAgain = invalidCache.Get(first, second, .001, Invalid);
        Assert.False(invalid.Curves[0].IsValid); Assert.False(invalidAgain.Curves[0].IsValid);
        Assert.Equal(5, calls); Assert.Equal(0, invalidCache.Count);
    });

    [RhinoFact] public void RepeatedCanonicalFacesKeepDifferentCutVerdictsAndRegions() => host.Run(() =>
    {
        const double tolerance = .001;
        // The synthetic folded prism from SlabClipSectionTests: lower cut sound, upper cut self-intersecting.
        using var mesh = new Mesh();
        foreach (var p in new[] { (-1d, -1d), (1d, -1d), (1d, 1d), (-1d, 1d) }) mesh.Vertices.Add(p.Item1, p.Item2, 0);
        foreach (var p in new[] { (-1d, -1d), (1d, -1d), (-2d, 0d), (-1d, 1d) }) mesh.Vertices.Add(p.Item1, p.Item2, 4);
        for (var i = 0; i < 4; i++)
        {
            var j = (i + 1) % 4;
            mesh.Faces.AddFace(i, j, j + 4); mesh.Faces.AddFace(i, j + 4, i + 4);
        }
        mesh.Faces.AddFace(0, 3, 2, 1); mesh.Faces.AddFace(4, 5, 6); mesh.Faces.AddFace(4, 6, 7);
        mesh.Faces.ConvertQuadsToTriangles(); mesh.Normals.ComputeNormals();
        using var shape = Brep.CreateFromMesh(mesh, true);
        Assert.True(shape.IsValid && shape.IsManifold && shape.IsSolid);
        var placement = Transform.Translation(0, 0, -2);
        using var shifted = shape.DuplicateBrep(); Assert.True(shifted.Transform(placement));
        var sources = new[]
        {
            new SectionSource("lower", shape, SectionStyleSnapshot.Default())
                { Canonical = new("folded", 1, () => shape.DuplicateBrep()) },
            new SectionSource("upper", shifted, SectionStyleSnapshot.Default())
                { Canonical = new("folded", 1, () => shape.DuplicateBrep()) { Placement = placement } }
        };
        var request = new SectionRequest(new Plane(new Point3d(0, 0, 1), Vector3d.ZAxis), tolerance, .001);
        var slabs = SectionValidation.For(request);
        using var uncachedVerdicts = new SectionVerdictCache(); using var cachedVerdicts = new SectionVerdictCache();
        using var intersections = new SectionIntersectionCache();
        var expected = sources.Select(s => SectionGeometry.Verdict(s, tolerance, uncachedVerdicts, slabs)).ToArray();
        var actual = sources.Select(s => SectionGeometry.Verdict(s, tolerance, cachedVerdicts, slabs, intersections)).ToArray();
        Assert.Equal("", expected[0]); Assert.StartsWith("Self-intersecting Brep faces", expected[1]);
        Assert.Equal(expected, actual); Assert.True(intersections.Hits > 0);
        using var baseline = SectionRegionEngine.Compute(sources, request, uncachedVerdicts, default);
        using var candidate = SectionRegionEngine.Compute(sources, request);
        Assert.Single(baseline.Regions); Assert.Single(candidate.Regions);
        Assert.Equal(baseline.Regions[0].Area, candidate.Regions[0].Area);
        Assert.Equal(baseline.Refusals.Select(r => (r.SourceId, GeometryFingerprint.Compute(r.Geometry))),
            candidate.Refusals.Select(r => (r.SourceId, GeometryFingerprint.Compute(r.Geometry))));
    });
}
