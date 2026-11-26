using M2DPlus.Rhino.Sections;
using Rhino.Geometry;
using Xunit;
using Xunit.Abstractions;

namespace M2DPlus.Rhino.Tests;

[Collection("Rhino")]
[Trait("Category", "RhinoIntegration")]
public sealed class SlabOverlapGuardTests(RhinoHost host, ITestOutputHelper output)
{
    private const double Tol = .001;
    private static SectionValidation Slabs(bool far = false) => new(Plane.WorldXY,
        far ? new Plane(new Point3d(0, 0, 2), -Vector3d.ZAxis) : null, .005);
    private sealed class NoGuard(SectionValidation slab, BoundingBox bounds) : SlabOverlapGuard(slab, bounds, Tol)
    {
        internal int Calls { get; private set; }
        internal override bool Check(BrepFace a, BrepFace b, Curve[] shared, Point3d[] points) { Calls++; return true; }
    }
    private sealed class CountingGuard(SectionValidation slab, BoundingBox bounds) : SlabOverlapGuard(slab, bounds, Tol)
    {
        internal int Calls { get; private set; }
        internal override FaceSection Section(BrepFace face, Plane plane) { Calls++; return base.Section(face, plane); }
    }
    private sealed class FailedSection(SectionValidation slab, BoundingBox bounds) : SlabOverlapGuard(slab, bounds, Tol)
    {
        internal int Calls { get; private set; }
        internal override FaceSection Section(BrepFace face, Plane plane) { Calls++; return new(false, [], []); }
    }
    private sealed class FailedExtraction(SectionValidation slab, BoundingBox bounds) : SlabOverlapGuard(slab, bounds, Tol)
    {
        internal int Calls { get; private set; }
        internal override Curve? Extract(Curve curve, Interval interval) { Calls++; return null; }
    }
    private static string Pair(Brep shape, int a, int b, SectionValidation slab, SlabOverlapGuard guard)
    {
        try { SectionGeometry.ValidateFacePair(shape.Faces[a], shape.Faces[b], Tol, slab, guard); return ""; }
        catch (SectionFailure ex) { return ex.Message; }
    }

    private static Brep Connected(bool rotateSeam)
    {
        // Revolve a folded radial/height polygon. The two r=1 walls have different
        // end rings (-2,6) and (-1,5); their overlap crosses z=0, with no shared edges.
        var profile = new[] { (1d,-1d), (2d,-1d), (2d,6d), (1d,6d), (1d,-2d), (.5,-2d), (.5,5d), (1d,5d), (1d,-1d) };
        var faces = new List<Brep>();
        try
        {
            for (var i = 0; i + 1 < profile.Length; i++)
            {
                using var line = new LineCurve(new Point3d(profile[i].Item1, 0, profile[i].Item2),
                    new Point3d(profile[i+1].Item1, 0, profile[i+1].Item2));
                using var revolved = RevSurface.Create(line, new Line(Point3d.Origin, Vector3d.ZAxis));
                var face = revolved.ToBrep();
                if (rotateSeam && i == 7) face.Transform(Transform.Rotation(.71, Vector3d.ZAxis, Point3d.Origin));
                faces.Add(face);
            }
            var joined = Brep.JoinBreps(faces, Tol);
            Assert.Single(joined);
            return joined[0];
        }
        finally { foreach (var face in faces) face.Dispose(); }
    }


    [RhinoTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void CylindersAndConnectedFoldRefuseOnNativeSeam(bool rotate, bool reverse) => host.Run(() =>
    {
        foreach (var far in new[] { false, true })
        foreach (var connected in new[] { false, true })
        {
            using var cylinder = new Cylinder(new Circle(new Plane(new Point3d(0,0,-1), Vector3d.ZAxis), 1), 7).ToBrep(false, false);
            using var duplicate = cylinder.DuplicateBrep();
            if (rotate) duplicate.Transform(Transform.Rotation(.71, Vector3d.ZAxis, Point3d.Origin));
            using var shape = connected ? Connected(rotate) : cylinder.DuplicateBrep();
            if (!connected) shape.Append(duplicate);
            if (reverse) shape.Flip();
            var target = shape.Faces.Where(f => f.TryGetCylinder(out var c, Tol) && Math.Abs(c.Radius-1) < Tol).ToArray();
            Assert.Equal(2, target.Length);
            var slab = Slabs(far).WithBounds(shape.GetBoundingBox(true));
            using var guard = new SlabOverlapGuard(slab, shape.GetBoundingBox(true), Tol);
            using var bypass = new NoGuard(slab, shape.GetBoundingBox(true));
            Assert.StartsWith("Self-intersecting Brep faces", Pair(shape, target[0].FaceIndex, target[1].FaceIndex, slab, guard));
            Assert.StartsWith("Self-intersecting Brep faces", Pair(shape, target[0].FaceIndex, target[1].FaceIndex, slab, bypass));
            // Same refusal with the guard bypassed pins the native seam mechanism.
            output.WriteLine($"connected={connected}; rotated={rotate}; reversed={reverse}; far={far}: native seam refusal");
        }
    });

    // One smooth nonperiodic quadratic patch: all trims are at z=2, while
    // its interior bulges below z=0. No seam or trim intersects the slab.
    private static Brep Dome()
    {
        using var surface = NurbsSurface.Create(3, false, 3, 3, 3, 3);
        surface.KnotsU.CreateUniformKnots(1); surface.KnotsV.CreateUniformKnots(1);
        for (var u = 0; u < 3; u++)
        for (var v = 0; v < 3; v++) surface.Points.SetPoint(u, v, new Point3d(u-1, v-1, u == 1 && v == 1 ? -10 : 2));
        return surface.ToBrep();
    }

    [RhinoFact]
    public void DuplicatedDomeIsNewGuardWitnessAndFailuresNeverAdmit() => host.Run(() =>
    {
        using var dome = Dome(); using var pair = dome.DuplicateBrep(); pair.Append(dome);
        var bounds = pair.GetBoundingBox(true); var slab = Slabs().WithBounds(bounds);
        using var bypass = new NoGuard(slab, bounds);
        Assert.Equal("", Pair(pair, 0, 1, slab, bypass)); Assert.Equal(1, bypass.Calls);
        using var guard = new CountingGuard(slab, bounds);
        var verdict = Pair(pair, 0, 1, slab, guard); output.WriteLine("exact dome: " + verdict);
        Assert.StartsWith("Coincident Brep faces", verdict);
        var extracted = guard.Calls;
        Assert.StartsWith("Coincident Brep faces", Pair(pair, 0, 1, slab, guard));
        Assert.Equal(extracted, guard.Calls); // Sections cached per face/plane.
        using var failed = new FailedSection(slab, bounds);
        Assert.StartsWith("Self-intersecting Brep faces", Pair(pair, 0, 1, slab, failed)); Assert.True(failed.Calls > 0);
        using var extraction = new FailedExtraction(slab, bounds);
        Assert.StartsWith("Self-intersecting Brep faces", Pair(pair, 0, 1, slab, extraction)); Assert.True(extraction.Calls > 0);
    });

    [RhinoFact]
    public void BoundedCoplanarAreaCrossingSlabRefuses() => host.Run(() =>
    {
        using var first = new PlaneSurface(Plane.WorldYZ, new Interval(-2,2), new Interval(-1,6)).ToBrep();
        using var second = new PlaneSurface(Plane.WorldYZ, new Interval(-1,3), new Interval(-1,6)).ToBrep();
        using var shape = first.DuplicateBrep(); shape.Append(second);
        var slab = Slabs().WithBounds(shape.GetBoundingBox(true));
        using var guard = new SlabOverlapGuard(slab, shape.GetBoundingBox(true), Tol);
        Assert.StartsWith("Self-intersecting Brep faces", Pair(shape, 0, 1, slab, guard));
    });

    [RhinoTheory]
    [InlineData(false, -1)]
    [InlineData(false, 1)]
    [InlineData(true, -1)]
    [InlineData(true, 1)]
    public void RealSharedCircularRimAtOuterBoundaryAdmits(bool far, int sign) => host.Run(() =>
    {
        // Include distant parent bounds first, so the rim lies at the actual
        // expanded boundary, rather than the nominal +/-5tol plane.
        var bounds = new BoundingBox(new Point3d(-2,-2,-2), new Point3d(2,2,4));
        var slab = Slabs(far).WithBounds(bounds);
        var domain = slab.Domains(bounds)[far ? 1 : 0];
        var z = domain.Boundary(sign).Origin.Z;
        using var lower = new Cylinder(new Circle(new Plane(new Point3d(0,0,z-1), Vector3d.ZAxis), 1), 1).ToBrep(false, false);
        using var upper = new Cylinder(new Circle(new Plane(new Point3d(0,0,z), Vector3d.ZAxis), 1), 1).ToBrep(false, false);
        var joined = Brep.JoinBreps(new[] { lower, upper }, Tol);
        try
        {
            var shape = Assert.Single(joined); Assert.Equal(2, shape.Faces.Count);
            var shared = shape.Faces[0].AdjacentEdges().Intersect(shape.Faces[1].AdjacentEdges()).Select(i => (Curve)shape.Edges[i]).ToArray();
            Assert.Single(shared);
            using var guard = new SlabOverlapGuard(slab, bounds, Tol);
            // Exercise the guard explicitly even when all native curves are
            // recognized as shared before any exclusion occurs.
            Assert.True(guard.Check(shape.Faces[0], shape.Faces[1], shared, []));
            Assert.Equal("", Pair(shape, 0, 1, slab, guard));
        }
        finally { foreach (var shape in joined) shape.Dispose(); }
    });

    [RhinoFact]
    public void ShallowNearCoincidenceAndClosedInteriorAreUncertifiedControls() => host.Run(() =>
    {
        using var dome = Dome(); dome.Transform(Transform.Scale(Plane.WorldXY, 1000, 1000, 1));
        using var almost = dome.DuplicateBrep(); almost.Transform(Transform.Translation(0,0,Tol/4));
        using var pair = dome.DuplicateBrep(); pair.Append(almost);
        var slab = Slabs().WithBounds(pair.GetBoundingBox(true));
        using var guard = new SlabOverlapGuard(slab, pair.GetBoundingBox(true), Tol);
        output.WriteLine("near-coincident shallow patch: " + Pair(pair, 0, 1, slab, guard));
        // Record only: native near-coincident slicing is not certified.
        using var sphere = new Sphere(Point3d.Origin, .001).ToBrep();
        using var closed = sphere.DuplicateBrep(); closed.Append(sphere);
        using var closedGuard = new SlabOverlapGuard(slab, closed.GetBoundingBox(true), Tol);
        var verdict = Pair(closed, 0, 1, slab, closedGuard);
        output.WriteLine("closed coincident component inside slab: " + verdict);
        // This fixture's native seam refuses; it does not prove detection of
        // closed area components with no reported boundary or seam.
        Assert.StartsWith("Self-intersecting Brep faces", verdict);
    });
    private sealed class TangencySection(SectionValidation slab, BoundingBox bounds, Point3d tangent) : SlabOverlapGuard(slab, bounds, Tol)
    {
        internal int PointCalls { get; private set; }
        internal override FaceSection Section(BrepFace face, Plane plane)
        {
            if (Math.Abs(plane.DistanceTo(tangent)) > 1e-12) return base.Section(face, plane);
            PointCalls++;
            return new(true, [], [tangent]);
        }
    }

    [RhinoFact]
    public void OuterPlaneIsolatedTangencyRefusesThroughPointRule() => host.Run(() =>
    {
        using var dome = Dome(); using var pair = dome.DuplicateBrep(); pair.Append(dome);
        var bounds = pair.GetBoundingBox(true);
        var width = Slabs().WithBounds(bounds).Domains(bounds)[0].HalfWidth;
        var slab = new SectionValidation(new Plane(new Point3d(0,0,-1-width), Vector3d.ZAxis), null, .005).WithBounds(bounds);
        using var guard = new TangencySection(slab, bounds, new Point3d(0,0,-1));
        Assert.StartsWith("Coincident Brep faces", Pair(pair, 0, 1, slab, guard));
        Assert.Equal(2, guard.PointCalls);
        // Native point extraction is not complete on Rhino 8.36. Inject the
        // exact geometric tangency to pin the rule independently of omission.
        using var native = new SlabOverlapGuard(slab, bounds, Tol);
        var section = native.Section(pair.Faces[0], slab.Domains(bounds)[0].Boundary(1));
        try { output.WriteLine($"native tangency: success={section.Success}; curves={section.Curves.Length}; points={section.Points.Length}"); }
        finally { foreach (var c in section.Curves) c.Dispose(); }
    });

}
