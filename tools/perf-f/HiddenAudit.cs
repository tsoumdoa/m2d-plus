#if !PERFF_BASELINE
using System.Text.Json;
using M2DPlus.Rhino.Services;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;

namespace M2DPlus.PerfF;

/// <summary>
/// Adopted independent reviewer wrong-HIDDEN audit of Hidden spans that sit on
/// baseline Unresolved support. Every owned body (planar and curved, self included) x centre and
/// four 4-tolerance offset rays toward the viewer. A witness requires every ray clear of every body
/// (no hit farther than tolerance in front, no failed intersection). Samples detect, never certify.
/// </summary>
internal static class HiddenAudit
{
    internal static void Run(ViewDrawing drawing, string baseline, string output, BoundingBox scene, IReadOnlyList<PreparedLeaf> prepared, Plane? cut = null)
    {
        using var before = JsonDocument.Parse(File.ReadAllText(baseline));
        var oldUnresolved = before.RootElement.GetProperty("pieces").EnumerateArray().Where(p => p.GetProperty("c").GetString() == "Unresolved")
            .GroupBy(p => p.GetProperty("s").GetString()!).ToDictionary(g => g.Key, g => g.Select(p => p.GetProperty("p").EnumerateArray().Select(v => v.GetDouble()).ToArray()).ToArray());
        var oldHidden = before.RootElement.GetProperty("pieces").EnumerateArray().Where(p => p.GetProperty("c").GetString() == "Hidden")
            .GroupBy(p => p.GetProperty("s").GetString()!).ToDictionary(g => g.Key,g => g.Select(p => new
            { Points=p.GetProperty("p").EnumerateArray().Select(v => v.GetDouble()).ToArray(), Exact=p.GetProperty("exact").GetBoolean() }).ToArray());
        var plane = drawing.Frame.Plane; double tol = drawing.Tolerance;
        var xy = Transform.PlaneToPlane(plane, Plane.WorldXY);
        var owned = new List<GeometryBase>();
        try
        {
        var bodies = prepared.Where(s => s.Geometry is Brep or Mesh or Surface or Extrusion).Select(s =>
        {
            GeometryBase g = s.Geometry is Extrusion e ? e.ToBrep() : s.Geometry is Surface f ? f.ToBrep() : s.Geometry;
            if(!ReferenceEquals(g,s.Geometry))owned.Add(g);
            var corners = g.GetBoundingBox(true).GetCorners();
            foreach (ref var p in corners.AsSpan()) p.Transform(xy);
            var box = new BoundingBox(corners); box.Inflate(8 * tol);
            return (s.Identity, Geometry: g, Box: box);
        }).ToArray();
        double length = scene.Diagonal.Length * 4 + 10;
        var witnesses = new List<object>(); int samples = 0, blocked = 0, margin = 0, curves = 0, unchangedHiddenCurves = 0, uncastableRays = 0, roundoffUnchangedHiddenLines = 0;
        long pairs = 0, rays = 0, capContainmentTests = 0, capContainmentHits = 0, capContactMargins = 0;
        foreach (var piece in drawing.Items.Where(p => p.Class == M2DPlus.Core.LineClass.Hidden && oldUnresolved.ContainsKey(p.SourceIdentity)))
        {
            if (piece.DepthGeometry is not Curve curve) continue;
            using var projectedGeometry=piece.CopyGeometry();
            if(projectedGeometry is Curve projectedCurve && oldHidden.TryGetValue(piece.SourceIdentity,out var previousHidden))
            {
                static double[] Flatten(Polyline p)=>p.SelectMany(q=>new[]{q.X,q.Y,q.Z}).ToArray();
                double[] points;
                bool exactPolyline=projectedCurve.TryGetPolyline(out var exact);
                if(exactPolyline)points=Flatten(exact);
                else
                {
                    using var approximation=projectedCurve.ToPolyline(tol/8,.02,0,0);
                    points=approximation is not null && approximation.TryGetPolyline(out var poly) ? Flatten(poly) :
                        new[]{projectedCurve.PointAtStart.X,projectedCurve.PointAtStart.Y,projectedCurve.PointAtStart.Z,projectedCurve.PointAtEnd.X,projectedCurve.PointAtEnd.Y,projectedCurve.PointAtEnd.Z};
                }
                bool identical=previousHidden.Any(p=>p.Points.SequenceEqual(points));
                // Exact two-vertex polylines are continuous straight segments.
                // Matching their endpoints within coordinate roundoff matches the
                // entire support, despite projection's last-bit differences.
                // No model-tolerance or sampled curved-locus equivalence is used.
                bool roundedLine=!identical && exactPolyline && points.Length==6 && previousHidden.Any(p=>
                    p.Exact && p.Points.Length==6 && p.Points.Zip(points,(a,b)=>Math.Abs(a-b)).Max() <=
                    128*2.2204460492503131e-16*Math.Max(p.Points.Max(Math.Abs),points.Max(Math.Abs)));
                if(identical || roundedLine){unchangedHiddenCurves++;if(roundedLine)roundoffUnchangedHiddenLines++;continue;}
            }
            curves++;
            int count = Math.Max(4, (int)Math.Ceiling(curve.GetLength() / Math.Max(8 * tol, drawing.Frame.BoundingBox.Diagonal.Length / 128)));
            foreach (double t in curve.DivideByCount(count, true) ?? [])
            {
                var point = curve.PointAt(t); var projected = plane.ClosestPoint(point);
                bool wasUnresolved = oldUnresolved[piece.SourceIdentity].Any(poly => Enumerable.Range(1, poly.Length / 3 - 1).Any(i =>
                    new Line(new Point3d(poly[(i - 1) * 3], poly[(i - 1) * 3 + 1], poly[(i - 1) * 3 + 2]),
                             new Point3d(poly[i * 3], poly[i * 3 + 1], poly[i * 3 + 2])).DistanceTo(projected, true) <= 2 * tol));
                if (!wasUnresolved) continue;
                samples++;
                bool anyBlocked = false, anyMargin = false; double maxGap = double.NegativeInfinity; string? nearest = null;
                foreach (var offset in new[] { Vector3d.Zero, plane.XAxis * (4 * tol), plane.XAxis * (-4 * tol), plane.YAxis * (4 * tol), plane.YAxis * (-4 * tol) })
                {
                    var p = point + offset; var local = p; local.Transform(xy);
                    // Route C: material in front of the cut plane is removed; never let it block.
                    double reach = length;
                    if (cut is { } cp) { double den = plane.ZAxis * cp.Normal; reach = Math.Abs(den) < 1e-12 ? length : Math.Min(length, ((cp.Origin - p) * cp.Normal) / den); }
                    if (reach <= tol) { anyMargin=true;uncastableRays++;continue; }
                    rays++;
                    using var ray = new LineCurve(p, p + plane.ZAxis * reach);
                    foreach (var body in bodies)
                    {
                        pairs++;
                        if (body.Box.IsValid && (local.X < body.Box.Min.X || local.X > body.Box.Max.X || local.Y < body.Box.Min.Y || local.Y > body.Box.Max.Y || body.Box.Max.Z <= local.Z + tol)) continue;
                        double gap;
                        if (body.Geometry is Mesh mesh)
                        {
                            double d = Intersection.MeshRay(mesh, new Ray3d(p + plane.ZAxis * tol, plane.ZAxis));
                            if (!double.IsFinite(d)) { anyMargin = true; continue; }
                            if (d < 0 || d + tol > reach) continue;
                            gap = d + tol;
                        }
                        else
                        {
                            bool ok = Intersection.CurveBrep(ray, (Brep)body.Geometry, tol, out var overlaps, out var hits);
                            try
                            {
                                if (!ok || hits.Any(h => !h.IsValid) || overlaps.Any(c => !c.IsValid)) { anyMargin = true; continue; }
                                gap = hits.Select(h => (h - p) * plane.ZAxis).Concat(overlaps.SelectMany(c => new[] { (c.PointAtStart - p) * plane.ZAxis, (c.PointAtEnd - p) * plane.ZAxis }))
                                    .DefaultIfEmpty(double.NegativeInfinity).Max();
                            }
                            finally { foreach (var c in overlaps) c.Dispose(); }
                        }
                        // Clipping an original-body ray at the cut can leave its
                        // endpoint INSIDE a solid: there is no original face hit,
                        // but retained material closes at the new section cap.
                        // Query original-solid containment AT the cut,
                        // independently of the product's trim/cap implementation.
                        if (gap <= tol && cut is not null && body.Geometry is Brep { IsSolid: true } solid)
                        {
                            capContainmentTests++;
                            var endpoint = p + plane.ZAxis * reach;
                            if (solid.IsPointInside(endpoint, tol, strictlyIn: true))
                            { gap = Math.Max(gap, reach); capContainmentHits++; }
                            else
                            {
                                capContainmentTests++;
                                if (solid.IsPointInside(endpoint, tol, strictlyIn: false))
                                { anyMargin = true; capContactMargins++; }
                            }
                        }
                        if (gap > maxGap) { maxGap = gap; nearest = body.Identity; }
                        if (gap > 4 * tol) anyBlocked = true; else if (gap > tol) anyMargin = true;
                    }
                }
                if (anyBlocked) blocked++;
                else if (anyMargin) margin++;
                else witnesses.Add(new { piece.SourceIdentity, t, world = new[] { point.X, point.Y, point.Z }, maxGap = double.IsFinite(maxGap) ? maxGap : (double?)null, nearest });
            }
        }
        File.WriteAllText(output, JsonSerializer.Serialize(new
        {
            baseline, tolerance = tol, bodyCount = bodies.Length, curves, samples, bodyRayPairs = pairs, blockedSamples = blocked, marginSamples = margin,
            wrongHidden = witnesses.Count, witnesses, unchangedHiddenCurves, roundoffUnchangedHiddenLines, uncastableRays, capContainmentTests, capContainmentHits, capContactMargins, expectedPairs=rays*bodies.Length,
            clippedAtCut = cut is not null, method = "Reviewer probe: every owned body (planar+curved, self) x centre + four 4-tol offset rays; cut-end containment in original closed solids includes retained cap material; witness = all rays clear (no hit > tol in front, no failure)."
        }));
        }
        finally { foreach(var geometry in owned)geometry.Dispose(); }
    }
}
#endif
