using M2DPlus.Rhino.Sections;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;
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
        internal override Curve? Extract(Curve curve, Interval interval, Point3d start, Point3d end, Curve other) { Calls++; return null; }
    }
    private static string Pair(Brep shape, int a, int b, SectionValidation? slab, SlabOverlapGuard guard)
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

    [RhinoTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void DuplicatedDomeIsNewGuardWitnessAndFailuresNeverAdmit(bool opposite) => host.Run(() =>
    {
        using var dome = Dome(); using var pair = dome.DuplicateBrep();
        if (opposite) dome.Flip();
        pair.Append(dome);
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


    private static Brep TwoSharedRims(double length, double bottom, bool opposite)
    {
        // Two single-face ruled sheets share their two extrusion rims. The
        // upper rim is remote, so a pair check must invoke the boundary guard.
        using var a = new PolylineCurve(new[] { new Point3d(0,0,bottom), new Point3d(1,0,1), new Point3d(0,0,2) });
        using var b = new PolylineCurve(new[] { new Point3d(0,0,bottom), new Point3d(-1,0,1), new Point3d(0,0,2) });
        using var sa = Surface.CreateExtrusion(a, new Vector3d(0,length,0));
        using var sb = Surface.CreateExtrusion(b, new Vector3d(0,opposite ? -length : length,0));
        if (opposite) sb.Translate(0,length,0);
        using var ba = sa.ToBrep(); using var bb = sb.ToBrep();
        var joined = Brep.JoinBreps([ba, bb], Tol);
        var shape = Assert.Single(joined);
        Assert.Equal(2, shape.Faces.Count);
        return shape;
    }

    [RhinoFact]
    public void UnresolvedGuardPreservesLongSharedEdgeAttributionFrom73() => host.Run(() =>
    {
        using var shape = TwoSharedRims(200, 0, false);
        var bounds = shape.GetBoundingBox(true); var slab = Slabs().WithBounds(bounds);
        var shared = shape.Faces[0].AdjacentEdges().Intersect(shape.Faces[1].AdjacentEdges()).Select(i => (Curve)shape.Edges[i]).ToArray();
        Assert.Equal(2, shared.Length); Assert.All(shared, c => Assert.True(c.GetLength() > 65));
        using var failed = new FailedSection(slab, bounds);
        Assert.Equal("", Pair(shape, 0, 1, slab, failed));
        Assert.True(failed.Calls > 0);
        using var old = new SlabOverlapGuard(null, bounds, Tol);
        Assert.Equal("", Pair(shape, 0, 1, null, old));
        // The removed whole-curve fallback would exhaust its continuous budget.
        Assert.False(SectionGeometry.CoveredBySharedTopology(shared[0], shared, Tol));
    });

    [RhinoFact]
    public void OppositeDirectionSharedOuterSegmentWithRemoteIntersectionAdmits() => host.Run(() =>
    {
        var bounds = new BoundingBox(new Point3d(-2,-1,-2), new Point3d(2,3,4));
        var slab = Slabs().WithBounds(bounds);
        var z = slab.Domains(bounds)[0].Boundary(1).Origin.Z;
        using var shape = TwoSharedRims(2, z, true);
        var shared = shape.Faces[0].AdjacentEdges().Intersect(shape.Faces[1].AdjacentEdges()).Select(i => (Curve)shape.Edges[i]).ToArray();
        using var guard = new SlabOverlapGuard(slab, bounds, Tol);
        var first = guard.Section(shape.Faces[0], slab.Domains(bounds)[0].Boundary(1));
        var second = guard.Section(shape.Faces[1], slab.Domains(bounds)[0].Boundary(1));
        try
        {
            var backwards = false;
            foreach (var a in first.Curves)
            foreach (var b in second.Curves)
            {
                using var events = Intersection.CurveCurve(a, b, Tol, Tol);
                foreach (var e in events.Where(e => e.IsOverlap))
                {
                    output.WriteLine($"shared outer segment: OverlapA={e.OverlapA}; OverlapB={e.OverlapB}");
                    backwards |= e.OverlapB.Length < 0;
                }
            }
            Assert.True(backwards, "Fixture must exercise a native decreasing OverlapB.");
        }
        finally { foreach (var c in first.Curves.Concat(second.Curves)) c.Dispose(); }
        Assert.True(guard.Check(shape.Faces[0], shape.Faces[1], shared, []));
        Assert.Equal("", Pair(shape, 0, 1, slab, guard));
    });

    private sealed class CurveSections(SectionValidation slab, BoundingBox bounds, Curve first, Curve second) : SlabOverlapGuard(slab, bounds, Tol)
    {
        internal override FaceSection Section(BrepFace face, Plane plane) => new(true, [ (face.FaceIndex == 0 ? first : second).DuplicateCurve() ], []);
    }

    [RhinoTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ClosedOverlapFullSingletonAndSeamArcResolve(bool wrapping, bool onTopology) => host.Run(() =>
    {
        using var dome = Dome(); using var pair = dome.DuplicateBrep(); pair.Append(dome);
        var bounds = pair.GetBoundingBox(true); var slab = Slabs().WithBounds(bounds);
        using var closed = new ArcCurve(new Circle(Plane.WorldXY, 1));
        using var other = wrapping ? new ArcCurve(new Arc(new Circle(Plane.WorldXY, 1), new Interval(-.6, .6))) : closed.DuplicateCurve();
        other.Reverse();
        using var guard = new CurveSections(slab, bounds, closed, other);
        using var extracted = guard.Extract(closed, wrapping ? new Interval(.6, 2*Math.PI-.6) : new Interval(0,0),
            other.PointAtStart, other.PointAtEnd, other);
        Assert.NotNull(extracted);
        Assert.InRange(extracted.GetLength(), wrapping ? 1.19 : 6.28, wrapping ? 1.21 : 6.29);
        if (onTopology) Assert.True(guard.Check(pair.Faces[0], pair.Faces[1], [other], []));
        else Assert.StartsWith("Coincident Brep faces", Assert.Throws<SectionFailure>(() => guard.Check(pair.Faces[0], pair.Faces[1], [], [])).Message);
        output.WriteLine($"closed wrapping={wrapping}; topology={onTopology}: resolved overlap");
    });

    [RhinoFact]
    public void ClosedArcExtractionWithNoUniqueCandidateIsUnresolved() => host.Run(() =>
    {
        var bounds = new BoundingBox(new Point3d(-2,-2,-2), new Point3d(2,2,2));
        using var guard = new SlabOverlapGuard(Slabs(), bounds, Tol);
        using var circle = new ArcCurve(new Circle(Plane.WorldXY, 1));
        var interval = new Interval(.6, 2*Math.PI-.6);
        var start = circle.PointAt(interval.T0); var end = circle.PointAt(interval.T1);
        // Both complementary arcs lie on the same full circle: ambiguous.
        using var both = guard.Extract(circle, interval, start, end, circle);
        Assert.Null(both);
        using var remote = new LineCurve(new Point3d(10,0,0), new Point3d(11,0,0));
        using var none = guard.Extract(circle, interval, start, end, remote);
        Assert.Null(none);
        // A full overlap's singleton need not be at the original closed seam.
        var fullStart = circle.PointAt(.7);
        using var full = guard.Extract(circle, new Interval(.7,.7), fullStart, fullStart, circle);
        Assert.NotNull(full); Assert.InRange(full.GetLength(), 6.28, 6.29);
        using var wrongEndpoints = guard.Extract(circle, interval, Point3d.Origin, Point3d.Origin, circle);
        Assert.Null(wrongEndpoints);
    });

    [RhinoFact]
    public void CurvePointEventOffTopologyRefusesAndSharedCrossingAdmits() => host.Run(() =>
    {
        using var dome = Dome(); using var pair = dome.DuplicateBrep(); pair.Append(dome);
        var bounds = pair.GetBoundingBox(true); var slab = Slabs().WithBounds(bounds);
        using var a = new LineCurve(new Point3d(-1,0,0), new Point3d(1,0,0));
        using var b = new LineCurve(new Point3d(0,-1,0), new Point3d(0,1,0));
        using var guard = new CurveSections(slab, bounds, a, b);
        Assert.Contains("curve point event", Assert.Throws<SectionFailure>(() => guard.Check(pair.Faces[0], pair.Faces[1], [], [])).Message);
        Assert.True(guard.Check(pair.Faces[0], pair.Faces[1], [], [Point3d.Origin]));
        Assert.True(guard.Check(pair.Faces[0], pair.Faces[1], [a], []));
    });

    private static Brep NotchCylinderHalf(Brep half, bool upperAngles)
    {
        var start = upperAngles ? 2*Math.PI-.3 : 0;
        var end = upperAngles ? 2*Math.PI : .3;
        using var bottom = new ArcCurve(new Arc(new Circle(new Plane(new Point3d(0,0,1), Vector3d.ZAxis), 1), new Interval(start,end)));
        using var top = new ArcCurve(new Arc(new Circle(new Plane(new Point3d(0,0,3), Vector3d.ZAxis), 1), new Interval(start,end)));
        top.Reverse();
        using var side = new LineCurve(bottom.PointAtEnd, top.PointAtStart);
        using var seam = new LineCurve(top.PointAtEnd, bottom.PointAtStart);
        var loops = Curve.JoinCurves([bottom, side, top, seam], Tol);
        try
        {
            using var split = half.Faces[0].Split(loops, Tol);
            Assert.NotNull(split); Assert.Equal(2, split.Faces.Count);
            return split.Faces.OrderByDescending(f => { using var b = f.DuplicateFace(false); return SectionGeometry.Area(b); }).First().DuplicateFace(false);
        }
        finally { foreach (var c in loops) c.Dispose(); }
    }

    [RhinoFact]
    public void G1CylinderSharedEdgeCrossingGuardPlaneAdmits() => host.Run(() =>
    {
        // Split a cylinder along two generators. The G1 shared edges cross both
        // guard planes. A notch splits the other shared generator into remote
        // pieces, so exclusion must invoke the guard in the real pair check.
        using var leftArc = new ArcCurve(new Arc(new Circle(Plane.WorldXY, 1), new Interval(0, Math.PI)));
        using var rightArc = new ArcCurve(new Arc(new Circle(Plane.WorldXY, 1), new Interval(Math.PI, 2*Math.PI)));
        using var leftSurface = Surface.CreateExtrusion(leftArc, new Vector3d(0,0,4));
        using var rightSurface = Surface.CreateExtrusion(rightArc, new Vector3d(0,0,4));
        using var left = leftSurface.ToBrep(); using var right = rightSurface.ToBrep();
        using var trimmedLeft = NotchCylinderHalf(left, false);
        using var trimmedRight = NotchCylinderHalf(right, true);
        var joined = Brep.JoinBreps([trimmedLeft, trimmedRight], Tol);
        try
        {
            var shape = Assert.Single(joined); Assert.Equal(2, shape.Faces.Count);
            var shared = shape.Faces[0].AdjacentEdges().Intersect(shape.Faces[1].AdjacentEdges()).Select(i => (Curve)shape.Edges[i]).ToArray();
            Assert.Equal(3, shared.Length);
            var bounds = shape.GetBoundingBox(true);
            var slab = new SectionValidation(new Plane(new Point3d(0,0,2), Vector3d.ZAxis), null, .005).WithBounds(bounds);
            using var guard = new CountingGuard(slab, bounds);
            Assert.True(guard.Check(shape.Faces[0], shape.Faces[1], shared, []));
            using var pairGuard = new CountingGuard(slab, bounds);
            Assert.Equal("", Pair(shape, 0, 1, slab, pairGuard));
            Assert.True(pairGuard.Calls > 0, "Remote shared pieces must trigger the G1 boundary guard.");
        }
        finally { foreach (var shape in joined) shape.Dispose(); }
    });
}
