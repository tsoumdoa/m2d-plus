using M2DPlus.Core.Sections;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;

namespace M2DPlus.Rhino.Sections;

/// <summary>Transverse slab certificate. No solid membership, trim, cap, or volume oracle.</summary>
internal static class StableSectionCertificate
{
    public const string Route = "D: perturbation stability + outward winding + Bezier slab transversality";
    internal const string PoleRecovery = "geometric sphere pole recovery";
    internal const int PerturbationTolerances = 4;
    private sealed record Loop(double Area, double Perimeter, Point3d Centre, int? Parent, bool Hole);
    internal sealed record Evidence(Curve[] Loops) : IDisposable
    {
        public void Dispose() { foreach (var loop in Loops) loop.Dispose(); }
    }

    public static bool Try(Brep source, SectionRequest request, out string reason, out Evidence? evidence)
    {
        evidence = null;
        reason = "D: requires an outward closed solid.";
        if (!source.IsSolid || source.SolidOrientation != BrepSolidOrientation.Outward) return false;
        var delta = PerturbationTolerances * request.Tolerance;
        try
        {
            // Make face and surface orientation identical on another owned copy.
            // BrepFace.NormalAt already applies the proxy orientation on the tested
            // native host; reversing that normal again rejects correctly wound RHS.
            using var normalized = source.DuplicateBrep();
            normalized.Faces.StandardizeFaceSurfaces();
            if (!normalized.IsValid || normalized.Faces.Any(f => f.OrientationIsReversed))
                throw new SectionFailure("D: cannot standardize outward face orientation.");
            source = normalized;
            // Bounding the whole slab also bounds curvature-induced changes between samples.
            var slope = SlabSlope(source, request, delta, out var poleRecovery);
            var planes = new[] { request.Plane, request.Plane, request.Plane };
            planes[0].Origin -= delta * request.Plane.Normal;
            planes[2].Origin += delta * request.Plane.Normal;
            var sets = new Curve[3][];
            try
            {
                for (var i = 0; i < 3; i++)
                {
                    if (!Intersection.BrepPlane(source, planes[i], request.Tolerance, out var segments, out var points))
                        throw new SectionFailure("D: intersection failed in perturbation slab.");
                    try { sets[i] = SectionGeometry.JoinIntersectionLoops(segments, planes[i], request.Tolerance); }
                    finally { foreach (var segment in segments) segment.Dispose(); }
                    if (points.Length != 0 || sets[i].Length == 0) throw new SectionFailure("D: isolated contact or empty perturbation intersection.");
                }
                var descriptors = new List<Loop[]>();
                for (var i = 0; i < 3; i++)
                {
                    var local = request with { Plane = planes[i] };
                    var regions = SectionGeometry.PlanarRegions(sets[i], local);
                    foreach (var region in regions) region.Dispose();
                    descriptors.Add(Describe(sets[i], planes[i], request.Tolerance));
                    for (var j = 0; j < sets[i].Length; j++) Winding(source, sets[i][j], descriptors[i][j].Hole, local);
                }
                Compare(descriptors[1], descriptors[0], request, delta, slope);
                Compare(descriptors[1], descriptors[2], request, delta, slope);
                evidence = new(sets[1]); sets[1] = Array.Empty<Curve>();
            }
            finally { foreach (var set in sets) if (set is not null) foreach (var loop in set) loop.Dispose(); }
            reason = $"{Route}; delta={delta:G6}, slope<={slope:G6}; loops are the cap boundary." +
                (poleRecovery ? $" {PoleRecovery}; native line confidence requires a separate proof." : "");
            return true;
        }
        catch (SectionFailure ex) { reason = ex.Message; return false; }
    }

    private static Loop[] Describe(Curve[] curves, Plane plane, double tolerance)
    {
        var areas = new double[curves.Length]; var centres = new Point3d[curves.Length];
        for (var i = 0; i < curves.Length; i++)
        {
            using var mass = AreaMassProperties.Compute(curves[i]);
            if (mass is null) throw new SectionFailure("D: loop moments failed.");
            areas[i] = mass.Area; centres[i] = mass.Centroid;
        }
        var nesting = RegionTopology.Nest(areas, (i, j) => { lock (SectionGeometry.CurveIntersectionGate) return Curve.PlanarClosedCurveRelationship(curves[i], curves[j], plane, tolerance) == RegionContainment.BInsideA; });
        return nesting.Select(n => new Loop(areas[n.Index], curves[n.Index].GetLength(), centres[n.Index], n.Parent, n.IsHole)).ToArray();
    }

    private static void Compare(Loop[] centre, Loop[] offset, SectionRequest request, double delta, double slope)
    {
        if (centre.Length != offset.Length) throw new SectionFailure("D: perturbation changed loop count.");
        var map = new int[centre.Length]; var used = new HashSet<int>();
        for (var i = 0; i < centre.Length; i++)
        {
            var p = request.Plane.ClosestPoint(centre[i].Centre);
            var candidates = Enumerable.Range(0, offset.Length).Where(j => !used.Contains(j) && centre[i].Hole == offset[j].Hole)
                .OrderBy(j => p.DistanceTo(request.Plane.ClosestPoint(offset[j].Centre))).ToArray();
            if (candidates.Length == 0) throw new SectionFailure("D: perturbation changed hole parity.");
            var match = candidates[0]; map[i] = match; used.Add(match);
            var bound = Math.Max(centre[i].Perimeter, offset[match].Perimeter) * delta * (1 + slope);
            if (Math.Abs(centre[i].Area - offset[match].Area) > bound + 16 * request.Tolerance * request.Tolerance ||
                p.DistanceTo(request.Plane.ClosestPoint(offset[match].Centre)) > delta * (1 + slope) * 4)
                throw new SectionFailure("D: perturbation area/displacement exceeds slab slope bound.");
        }
        for (var i = 0; i < centre.Length; i++)
            if (centre[i].Parent is { } parent ? offset[map[i]].Parent != map[parent] : offset[map[i]].Parent.HasValue)
                throw new SectionFailure("D: perturbation changed nesting topology.");
    }

    private static void Winding(Brep source, Curve loop, bool hole, SectionRequest request)
    {
        var orientation = loop.ClosedCurveOrientation(request.Plane);
        if (orientation == CurveOrientation.Undefined) throw new SectionFailure("D: undefined loop orientation.");
        var expected = (orientation == CurveOrientation.CounterClockwise ? -1 : 1) * (hole ? -1 : 1);
        for (var span = 0; span < loop.SpanCount; span++)
        foreach (var fraction in new[] { .125, .375, .625, .875 })
        {
            var t = loop.SpanDomain(span).ParameterAt(fraction); var p = loop.PointAt(t); var tangent = loop.TangentAt(t);
            var witnessed = false;
            var closest = double.PositiveInfinity;
            foreach (var face in source.Faces)
            {
                if (!face.ClosestPoint(p, out var u, out var v) ||
                    face.IsPointOnFace(u, v) == PointFaceRelation.Exterior) continue;
                var distance = face.PointAt(u, v).DistanceTo(p);
                closest = Math.Min(closest, distance);
                if (distance > 2 * request.Tolerance) continue;
                var normal = face.NormalAt(u, v); if (face.OrientationIsReversed) normal.Reverse();
                var dot = tangent * Vector3d.CrossProduct(normal, request.Plane.Normal);
                if (Math.Abs(dot) < .01) continue;
                if (Math.Sign(dot) != expected) throw new SectionFailure($"D: outward winding disagrees with nesting (hole={hole}, orientation={orientation}, face={face.FaceIndex}, reversed={face.OrientationIsReversed}, dot={dot:G6}).");
                witnessed = true;
            }
            if (!witnessed) throw new SectionFailure($"D: cannot associate loop direction with an oriented face; nearest trimmed-face distance={closest:G8}, limit={2 * request.Tolerance:G8}.");
        }
    }

    private static double SlabSlope(Brep brep, SectionRequest request, double delta, out bool poleRecovery)
    {
        poleRecovery = false;
        var slope = 0d; var budget = 20000;
        try
        {
            foreach (var face in brep.Faces)
            {
                // A sphere's NURBS poles are singular parameters, not geometric
                // tangencies. Bound its geometric normal throughout the slab instead.
                // Recognition is at roundoff scale, never a fitted approximate sphere.
                // SectionCoordinates has already translated the owned geometry
                // and cut together; measure arithmetic scale in that same local
                // frame. Its world-coordinate precision guard runs before this.
                var box = face.GetBoundingBox(true);
                var roundoff = 256 * 2.2204460492503131e-16 * Math.Max(1,
                    Math.Max(box.Min.DistanceTo(Point3d.Origin), box.Max.DistanceTo(Point3d.Origin)));
                var slab = delta + request.Tolerance;
                var faceBounds = face.GetBoundingBox(Transform.PlaneToPlane(request.Plane, Plane.WorldXY));
                if (faceBounds.IsValid && (faceBounds.Min.Z > slab + roundoff || faceBounds.Max.Z < -slab - roundoff)) continue;
                if (face.TryGetSphere(out var sphere, roundoff))
                {
                    if (Math.Abs(request.Plane.DistanceTo(sphere.Center)) - sphere.Radius > slab + roundoff) continue;
                    poleRecovery |= brep.Vertices.Any(v => Math.Abs(request.Plane.DistanceTo(v.Location)) <= delta + request.Tolerance + roundoff);
                    var distance = Math.Abs(request.Plane.DistanceTo(sphere.Center)) + delta + request.Tolerance + roundoff;
                    var radialSquared = (sphere.Radius - distance) * (sphere.Radius + distance);
                    if (radialSquared <= 0 || Math.Sqrt(radialSquared) <= .02 * sphere.Radius)
                        throw new SectionFailure("D: spherical slab reaches geometric tangency.");
                    slope = Math.Max(slope, distance / Math.Sqrt(radialSquared));
                    continue;
                }
                using var surface = face.ToNurbsSurface();
                surface.Transform(Transform.PlaneToPlane(request.Plane, Plane.WorldXY));
                // ConvertSpanToBezier uses control-net span indices, including empty
                // repeated-knot slots, rather than the count of nonempty spans.
                for (var u = 0; u <= surface.Points.CountU - surface.OrderU; u++)
                for (var v = 0; v <= surface.Points.CountV - surface.OrderV; v++)
                {
                    if (surface.KnotsU[u + surface.OrderU - 2] >= surface.KnotsU[u + surface.OrderU - 1] ||
                        surface.KnotsV[v + surface.OrderV - 2] >= surface.KnotsV[v + surface.OrderV - 1]) continue;
                    using var patch = surface.ConvertSpanToBezier(u, v);
                    if (patch is null) throw new SectionFailure("D: Bezier span conversion failed.");
                    var uDomain = new Interval(surface.KnotsU[u + surface.OrderU - 2], surface.KnotsU[u + surface.OrderU - 1]);
                    var vDomain = new Interval(surface.KnotsV[v + surface.OrderV - 2], surface.KnotsV[v + surface.OrderV - 1]);
                    try { Bound(patch, delta + request.Tolerance, 0, ref budget, ref slope, face, uDomain, vDomain); }
                    catch (SectionFailure ex) { throw new SectionFailure($"{ex.Message} face={face.FaceIndex}, span={u}/{v}."); }
                }
            }
            return slope;
        }
        finally { SectionWork.Slab(20000 - budget, budget < 0); }
    }

    // Rational Bezier derivatives: S_u=(A_u W-A W_u)/W^2. The common positive
    // denominators cancel from the normal angle. Convex hulls bound A, W and
    // their derivative control nets; outward-rounded intervals enclose products.
    private static void Bound(BezierSurface patch, double slab, int depth, ref int budget, ref double slope,
        BrepFace face, Interval uDomain, Interval vDomain)
    {
        if (--budget < 0) throw new SectionFailure("D: Bezier slab subdivision budget exceeded.");
        using var s = patch.ToNurbsSurface();
        var nu = s.Points.CountU; var nv = s.Points.CountV;
        var points = new ControlPoint[nu, nv]; var min = double.PositiveInfinity; var max = double.NegativeInfinity;
        for (var i = 0; i < nu; i++) for (var j = 0; j < nv; j++)
        {
            var p = s.Points.GetControlPoint(i, j); points[i, j] = p;
            if (!double.IsFinite(p.Weight) || p.Weight <= 0) throw new SectionFailure("D: nonpositive rational weight.");
            min = Math.Min(min, p.Location.Z); max = Math.Max(max, p.Location.Z);
        }
        var fuzz = 128 * 2.2204460492503131e-16 * Math.Max(1, Math.Max(Math.Abs(min), Math.Abs(max)));
        if (min > slab + fuzz || max < -slab - fuzz) return;
        var origin = points[0, 0].Location;
        var a = new I[4]; var du = new I[4]; var dv = new I[4];
        double H(int i, int j, int axis)
        {
            var p = points[i, j]; var q = p.Location - origin;
            return axis == 3 ? p.Weight : p.Weight * (axis == 0 ? q.X : axis == 1 ? q.Y : q.Z);
        }
        for (var axis = 0; axis < 4; axis++)
        {
            var values = new List<double>(); var us = new List<double>(); var vs = new List<double>();
            for (var i = 0; i < nu; i++) for (var j = 0; j < nv; j++)
            {
                values.Add(H(i, j, axis));
                if (i + 1 < nu) us.Add((nu - 1) * (H(i + 1, j, axis) - H(i, j, axis)));
                if (j + 1 < nv) vs.Add((nv - 1) * (H(i, j + 1, axis) - H(i, j, axis)));
            }
            a[axis] = I.Hull(values); du[axis] = I.Hull(us); dv[axis] = I.Hull(vs);
        }
        var x = new I[3]; var y = new I[3];
        for (var k = 0; k < 3; k++) { x[k] = du[k] * a[3] - a[k] * du[3]; y[k] = dv[k] * a[3] - a[k] * dv[3]; }
        var nx = x[1] * y[2] - x[2] * y[1]; var ny = x[2] * y[0] - x[0] * y[2]; var nz = x[0] * y[1] - x[1] * y[0];
        var lower = Math.Sqrt(nx.MinAbs * nx.MinAbs + ny.MinAbs * ny.MinAbs);
        var upper = Math.Sqrt(nx.MaxAbs * nx.MaxAbs + ny.MaxAbs * ny.MaxAbs + nz.MaxAbs * nz.MaxAbs);
        if (double.IsFinite(upper) && lower > .02 * upper && upper > 0)
        { slope = Math.Max(slope, nz.MaxAbs / lower); return; }
        // A trimmed-away tangency is not part of the material boundary. Only
        // NURBS faces preserve the Bezier span's parameterization identically.
        // Prove the entire parameter rectangle exterior, never just its samples.
        if (face.UnderlyingSurface() is NurbsSurface && ExteriorRectangle(face, uDomain, vDomain)) return;
        if (depth >= 16) throw new SectionFailure("D: transversality cannot be bounded away from tangency.");
        if (!patch.Split(depth % 2, .5, out var first, out var second)) throw new SectionFailure("D: Bezier subdivision failed.");
        using (first) using (second)
        {
            Bound(first, slab, depth + 1, ref budget, ref slope, face,
                depth % 2 == 0 ? new(uDomain.Min, uDomain.Mid) : uDomain,
                depth % 2 == 1 ? new(vDomain.Min, vDomain.Mid) : vDomain);
            Bound(second, slab, depth + 1, ref budget, ref slope, face,
                depth % 2 == 0 ? new(uDomain.Mid, uDomain.Max) : uDomain,
                depth % 2 == 1 ? new(vDomain.Mid, vDomain.Max) : vDomain);
        }
    }

    private static bool ExteriorRectangle(BrepFace face, Interval u, Interval v)
    {
        if (face.IsPointOnFace(u.Mid, v.Mid) != PointFaceRelation.Exterior) return false;
        var budget = 4096;
        var fuzz = 256 * 2.2204460492503131e-16 * Math.Max(1,
            Math.Max(Math.Max(Math.Abs(u.Min), Math.Abs(u.Max)), Math.Max(Math.Abs(v.Min), Math.Abs(v.Max))));
        bool Disjoint(Curve trim, int depth)
        {
            if (--budget < 0) return false;
            var b = trim.GetBoundingBox(true);
            if (!b.IsValid) return false;
            if (b.Max.X < u.Min - fuzz || b.Min.X > u.Max + fuzz ||
                b.Max.Y < v.Min - fuzz || b.Min.Y > v.Max + fuzz) return true;
            if (depth >= 16) return false;
            using var left = trim.Trim(trim.Domain.Min, trim.Domain.Mid);
            using var right = trim.Trim(trim.Domain.Mid, trim.Domain.Max);
            return left is not null && right is not null && Disjoint(left, depth + 1) && Disjoint(right, depth + 1);
        }
        // A connected rectangle with no trim boundary and one exterior witness
        // is wholly exterior. Failure to separate any boundary keeps the patch
        // in the slab proof; budget exhaustion cannot authorize a cut.
        return face.Loops.SelectMany(l => l.Trims).All(t => Disjoint(t, 0));
    }

    private readonly record struct I(double Lo, double Hi)
    {
        public double MinAbs => Lo > 0 ? Lo : Hi < 0 ? -Hi : 0;
        public double MaxAbs => Math.Max(Math.Abs(Lo), Math.Abs(Hi));
        public static I Hull(List<double> a)
        {
            if (a.Count == 0) return new(0, 0);
            var fuzz = 128 * 2.2204460492503131e-16 * Math.Max(1, a.Max(Math.Abs));
            return new(Math.BitDecrement(a.Min() - fuzz), Math.BitIncrement(a.Max() + fuzz));
        }
        public static I operator -(I a, I b) => new(Math.BitDecrement(a.Lo - b.Hi), Math.BitIncrement(a.Hi - b.Lo));
        public static I operator *(I a, I b)
        {
            var v = new[] { a.Lo * b.Lo, a.Lo * b.Hi, a.Hi * b.Lo, a.Hi * b.Hi };
            return new(Math.BitDecrement(v.Min()), Math.BitIncrement(v.Max()));
        }
    }
}
