using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;

// Ray query for a single orthographic direction. Each closed solid can occlude
// at an outgoing crossing. Trims are checked in the original face parameter space.
internal sealed class PipeRayFaces : IDisposable
{
    private sealed class Patch
    {
        internal required BrepFace Face;
        internal required Brep Owner;
        internal required Brep Trimmed;
        internal BoundingBox Box;
        internal Point3d Origin;
        internal Vector3d Normal;
        internal double Radius, Denominator;
        internal Vector3d Across;
        internal bool IsPlane, IsCylinder, Outward, Flip;
        internal BoundingBox ViewBox;
        internal RuledRay? Ruled;
        internal CylinderTrims? Chart;
        internal double Quadratic;
    }
    private readonly record struct FaceBlock(Vector256<double> MinX, Vector256<double> MaxX,
        Vector256<double> MinY, Vector256<double> MaxY, Vector256<double> MaxZ);
    // One ray against four cylinder supports, preserving the scalar operation order.
    private sealed class CylinderBlock
    {
        private readonly Vector256<double> ox, oy, oz, nx, ny, nz, ax, ay, az, r2, quadratic, sign;
        internal readonly int Active;
        internal CylinderBlock(Patch[] lanes)
        {
            Vector256<double> Pack(Func<Patch, double> get) => Vector256.Create(get(lanes[0]), get(lanes[1]), get(lanes[2]), get(lanes[3]));
            ox = Pack(f => f.Origin.X); oy = Pack(f => f.Origin.Y); oz = Pack(f => f.Origin.Z);
            nx = Pack(f => f.Normal.X); ny = Pack(f => f.Normal.Y); nz = Pack(f => f.Normal.Z);
            ax = Pack(f => f.Across.X); ay = Pack(f => f.Across.Y); az = Pack(f => f.Across.Z);
            r2 = Pack(f => f.Radius * f.Radius); quadratic = Pack(f => f.IsCylinder && f.Quadratic >= 1e-14 ? f.Quadratic : 1);
            sign = Pack(f => f.Outward ? 1 : -1);
            for (int k = 0; k < 4; k++) if (lanes[k].IsCylinder && lanes[k].Quadratic >= 1e-14) Active |= 1 << k;
        }
        internal Vector256<double> Exits(Point3d p, out int positive)
        {
            var x = Vector256.Create(p.X) - ox; var y = Vector256.Create(p.Y) - oy; var z = Vector256.Create(p.Z) - oz;
            var along = x * nx + y * ny + z * nz;
            x -= along * nx; y -= along * ny; z -= along * nz;
            var b = x * ax + y * ay + z * az; var c = x * x + y * y + z * z - r2;
            var discriminant = b * b - quadratic * c;
            positive = Active & Avx.MoveMask(Avx.CompareGreaterThan(discriminant, Vector256<double>.Zero));
            return (Vector256<double>.Zero - b + sign * Avx.Sqrt(Avx.Max(Vector256<double>.Zero, discriminant))) / quadratic;
        }
    }
    private readonly CylinderBlock[]? cylinderBlocks;
    private readonly FaceBlock[]? blocks;
    private readonly List<Patch> patches = new();
    private readonly Vector3d eye;
    private readonly Vector3d viewX, viewY;
    private readonly double tol, reach;
    internal PipeRayFaces(IEnumerable<Brep> bodies, Vector3d eye, double tol, double reach, int prepThreads)
    {
        var view = new Plane(Point3d.Origin, eye);
        viewX = view.XAxis; viewY = view.YAxis;
        var transform = Transform.PlaneToPlane(view, Plane.WorldXY);
        this.eye = eye; this.tol = tol; this.reach = reach;
        var batch = bodies.ToArray();
        var built = new List<Patch>[batch.Length];
        void Build(int i)
        {
            var body = batch[i];
            var output = built[i] = new();
            foreach (var face in body.Faces)
            {
                var trimmed = face.DuplicateFace(false);
                var patch = new Patch { Face = face, Owner = body, Trimmed = trimmed, Box = trimmed.GetBoundingBox(true) };
                patch.Box.Inflate(tol);
                patch.Flip = face.OrientationIsReversed ^ (body.SolidOrientation == BrepSolidOrientation.Inward);
                patch.ViewBox = trimmed.GetBoundingBox(transform);
                patch.ViewBox.Inflate(tol);
                double u = face.Domain(0).Mid, v = face.Domain(1).Mid;
                var underlying = face.UnderlyingSurface();
                var outward = underlying.NormalAt(u, v);
                if (face.OrientationIsReversed ^ (body.SolidOrientation == BrepSolidOrientation.Inward)) outward.Reverse();
                if (face.TryGetPlane(out var plane, tol))
                {
                    patch.IsPlane = true; patch.Origin = plane.Origin; patch.Normal = plane.Normal;
                    if (patch.Normal * outward < 0) patch.Normal.Reverse();
                    patch.Denominator = eye * patch.Normal;
                }
                else if (face.TryGetCylinder(out var cyl, tol))
                {
                    patch.Chart = CylinderTrims.TryBuild(face, cyl, tol);
                    patch.IsCylinder = true; patch.Origin = cyl.Center; patch.Normal = cyl.Axis; patch.Radius = cyl.Radius;
                    var w = face.PointAt(u, v) - patch.Origin;
                    patch.Outward = outward * (w - (w * patch.Normal) * patch.Normal) > 0;
                    patch.Across = eye - (eye * patch.Normal) * patch.Normal;
                    patch.Quadratic = patch.Across.SquareLength;
                }
                else patch.Ruled = RuledRay.TryBuild(face, eye, tol);
                output.Add(patch);
            }
        }
        Parallel.For(0, batch.Length, new ParallelOptions { MaxDegreeOfParallelism = prepThreads }, Build);
        foreach (var list in built) patches.AddRange(list);
        // Most rays exit through the long cylindrical sides.
        patches.Sort((a, b) => b.Box.Diagonal.SquareLength.CompareTo(a.Box.Diagonal.SquareLength));
        if (Avx2.IsSupported)
        {
            blocks = new FaceBlock[(patches.Count + 3) / 4];
            cylinderBlocks = new CylinderBlock[blocks.Length];
            for (int g = 0; g < blocks.Length; g++)
            {
                if (cylinderBlocks is not null)
                {
                    var lanes = new Patch[4];
                    for (int k = 0; k < 4; k++) lanes[k] = patches[Math.Min(g * 4 + k, patches.Count - 1)];
                    cylinderBlocks[g] = new CylinderBlock(lanes);
                }
                var a = patches[g * 4].ViewBox; var b = patches[Math.Min(g * 4 + 1, patches.Count - 1)].ViewBox;
                var c = patches[Math.Min(g * 4 + 2, patches.Count - 1)].ViewBox; var d = patches[Math.Min(g * 4 + 3, patches.Count - 1)].ViewBox;
                blocks[g] = new(Vector256.Create(a.Min.X, b.Min.X, c.Min.X, d.Min.X), Vector256.Create(a.Max.X, b.Max.X, c.Max.X, d.Max.X),
                    Vector256.Create(a.Min.Y, b.Min.Y, c.Min.Y, d.Min.Y), Vector256.Create(a.Max.Y, b.Max.Y, c.Max.Y, d.Max.Y),
                    Vector256.Create(a.Max.Z, b.Max.Z, c.Max.Z, d.Max.Z));
            }
        }
    }
    internal bool Hidden(Point3d p)
    {
        var line = new Line(p + tol * 2 * eye, p + reach * eye);
        var vector = (Vector3d)p;
        double x = vector * viewX, y = vector * viewY, height = vector * eye;
        int mask = 0, roots = 0;
        Vector256<double> depths = default;
        var vx = Vector256.Create(x); var vy = Vector256.Create(y); var vz = Vector256.Create(height + 2 * tol);
        for (int i = 0; i < patches.Count; i++)
        {
            var f = patches[i];
            if (blocks is not null)
            {
                int lane = i & 3;
                if (lane == 0)
                {
                    var b = blocks[i >> 2];
                    var matchX = Avx.And(Avx.CompareLessThanOrEqual(b.MinX, vx), Avx.CompareLessThanOrEqual(vx, b.MaxX));
                    var matchY = Avx.And(Avx.CompareLessThanOrEqual(b.MinY, vy), Avx.CompareLessThanOrEqual(vy, b.MaxY));
                    mask = Avx.MoveMask(Avx.And(Avx.And(matchX, matchY), Avx.CompareLessThanOrEqual(vz, b.MaxZ)));
                    if (mask == 0) { i += 3; continue; }
                    if (cylinderBlocks is not null && (cylinderBlocks[i >> 2].Active & mask) != 0)
                        depths = cylinderBlocks[i >> 2].Exits(p, out roots);
                    else roots = 0;
                }
                if ((mask & (1 << lane)) == 0) continue;
            }
            else
            {
                var bounds = f.ViewBox;
                if (x < bounds.Min.X || x > bounds.Max.X || y < bounds.Min.Y || y > bounds.Max.Y || height + 2 * tol > bounds.Max.Z) continue;
            }
            double t;
            if (f.IsPlane)
            {
                if (f.Denominator <= 1e-8) continue;
                t = ((f.Origin - p) * f.Normal) / f.Denominator;
            }
            else if (f.IsCylinder && cylinderBlocks is not null)
            {
                if ((roots & (1 << (i & 3))) == 0) continue;
                t = depths.GetElement(i & 3);
            }
            else if (f.IsCylinder)
            {
                if (f.Quadratic < 1e-14) continue;
                var w = p - f.Origin;
                var radial = w - (w * f.Normal) * f.Normal;
                double b = radial * f.Across, c = radial.SquareLength - f.Radius * f.Radius;
                double discriminant = b * b - f.Quadratic * c;
                if (discriminant <= 0) continue;
                t = (-b + (f.Outward ? 1 : -1) * Math.Sqrt(discriminant)) / f.Quadratic;
            }
            else if (f.Ruled is { } ruled)
            {
                if (ruled.Hits(p, f.Face, f.Flip, f.Box, tol, h => MaterialBeforeExit(f, p, h))) return true;
                continue;
            }
            else
            {
                using var ray = new LineCurve(line);
                if (!Intersection.CurveBrep(ray, f.Trimmed, tol, out var overlaps, out var hits))
                    throw new InvalidOperationException("Trimmed face intersection failed");
                foreach (var c in overlaps ?? []) c.Dispose();
                foreach (var h in hits ?? [])
                {
                    if ((h - p) * eye <= 2 * tol || !f.Face.ClosestPoint(h, out var u, out var v)) continue;
                    var n = f.Face.UnderlyingSurface().NormalAt(u, v);
                    if (f.Flip) n.Reverse();
                    if (n * eye > 1e-8 && MaterialBeforeExit(f, p, (h - p) * eye)) return true;
                }
                continue;
            }
            if (t <= 2 * tol || t > reach) continue;
            var q = p + t * eye;
            if (!f.Box.Contains(q)) continue;
            if (f.Chart?.Contains(q) is bool inside)
            {
                if (inside && MaterialBeforeExit(f, p, t)) return true;
                continue;
            }
            if (!f.Face.ClosestPoint(q, out var s, out var z)) continue;
            // ClosestPoint can clamp to the ends of a partially defined cylinder.
            if (f.Face.PointAt(s, z).DistanceTo(q) > tol) continue;
            if (f.Face.IsPointOnFace(s, z) != PointFaceRelation.Exterior && MaterialBeforeExit(f, p, t)) return true;
        }
        return false;
    }
    private bool MaterialBeforeExit(Patch face, Point3d point, double depth)
    {
        if (depth > 64 * tol) return true;
        // A near-start surface hit can graze a trim. Require measurable solid
        // interior in the short interval before treating it as an obstruction.
        var inside = point + (.5 * depth) * eye;
        return face.Owner.IsPointInside(inside, tol, true) &&
            face.Owner.ClosestPoint(inside).DistanceTo(inside) > tol;
    }

    internal void DepthEvents(Curve curve, Brep? owner, List<double> cuts)
    {
        var bounds = curve.GetBoundingBox(true);
        bounds.Inflate(tol);
        // Subdivide original parameter spans. Retain CAD parameters for trimming.
        var stations = new List<(double T, Point3d P)>();
        for (int span = 0; span < curve.SpanCount; span++)
        {
            var d = curve.SpanDomain(span);
            var a = curve.PointAt(d.Min);
            if (stations.Count == 0) stations.Add((d.Min, a));
            void Subdivide(double lo, Point3d p, double hi, Point3d q, int level)
            {
                var mid = .5 * (lo + hi);
                var m = curve.PointAt(mid);
                if (level < 15 && (p.DistanceTo(q) > 60 || m.DistanceTo((p + q) * .5) > .025))
                {
                    Subdivide(lo, p, mid, m, level + 1); Subdivide(mid, m, hi, q, level + 1);
                }
                else stations.Add((hi, q));
            }
            Subdivide(d.Min, a, d.Max, curve.PointAt(d.Max), 0);
        }
        foreach (var f in patches)
        {
            if (ReferenceEquals(f.Owner, owner) || !BoundingBox.Intersection(bounds, f.Box).IsValid) continue;
            void Cut(double t)
            {
                if (f.Box.Contains(curve.PointAt(t))) cuts.Add(t);
            }
            if (f.IsPlane)
            {
                using var xs = Intersection.CurvePlane(curve, new Plane(f.Origin, f.Normal), tol);
                if (xs is null) continue;
                foreach (var hit in xs) { Cut(hit.ParameterA); if (hit.IsOverlap) Cut(hit.OverlapA.T1); }
                continue;
            }
            if (!f.IsCylinder)
            {
                bool ok = Intersection.CurveBrep(curve, f.Trimmed, tol, out var overlaps, out _, out var ts);
                foreach (var c in overlaps ?? [])
                {
                    using (c)
                    {
                        if (curve.ClosestPoint(c.PointAtStart, out var a)) cuts.Add(a);
                        if (curve.ClosestPoint(c.PointAtEnd, out var b)) cuts.Add(b);
                    }
                }
                if (!ok) throw new InvalidOperationException("Depth intersection failed");
                cuts.AddRange(ts ?? []);
                continue;
            }
            double radius2 = f.Radius * f.Radius;
            Vector3d Radial(Point3d p)
            {
                var v = p - f.Origin;
                return v - (v * f.Normal) * f.Normal;
            }
            var previous = stations[0];
            var r0 = Radial(previous.P);
            for (int k = 1; k < stations.Count; k++)
            {
                var next = stations[k];
                var r1 = Radial(next.P);
                double a = r0.SquareLength - radius2, b = r1.SquareLength - radius2;
                double lo = previous.T, hi = next.T;
                if ((a < 0) != (b < 0) && Math.Max(Math.Abs(a), Math.Abs(b)) > tol * f.Radius)
                {
                    // Bracket on the true parametric curve, then refine the crossing.
                    for (int it = 0; it < 20; it++)
                    {
                        var t = .5 * (lo + hi);
                        var residual = Radial(curve.PointAt(t)).SquareLength - radius2;
                        if (Math.Abs(residual) < .1 * tol * f.Radius) { lo = hi = t; break; }
                        if ((a < 0) == (residual < 0)) lo = t; else hi = t;
                    }
                    Cut(.5 * (lo + hi));
                }
                else if (a > tol * f.Radius && b > tol * f.Radius)
                {
                    // Two crossings can fall within one chord near tangency.
                    var delta = r1 - r0; double A = delta.SquareLength, B = r0 * delta;
                    double disc = B * B - A * a;
                    if (A > 0 && disc >= 0)
                    {
                        double first = (-B - Math.Sqrt(disc)) / A, last = (-B + Math.Sqrt(disc)) / A;
                        if (first > 0 && last < 1) { Cut(lo + (hi - lo) * first); Cut(lo + (hi - lo) * last); }
                    }
                }
                previous = next; r0 = r1;
            }
        }
    }
    public void Dispose() { foreach (var f in patches) f.Trimmed.Dispose(); }
}

