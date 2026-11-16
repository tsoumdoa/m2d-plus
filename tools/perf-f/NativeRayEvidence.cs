#if !PERFF_BASELINE
using System.Text.Json;
using M2DPlus.Rhino.Services;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;

namespace M2DPlus.PerfF;

/// <summary>Gate evidence: all owned curved bodies, no production spatial index or HLD decision.</summary>
internal sealed class NativeRayEvidence : IDisposable
{
    private readonly List<(string Source, GeometryBase Geometry, string Fingerprint)> _bodies = [];
    internal void Capture(IReadOnlyList<VisibilityInput> inputs)
    {
        foreach (var i in inputs.Where(i => !i.KernelCandidate && i.Geometry is Brep or Mesh or Surface or Extrusion))
        {
            var copy = i.Geometry.Duplicate(); copy.EnsurePrivateCopy();
            _bodies.Add((i.Source.Identity,copy,GeometryFingerprint.Compute(copy)));
        }
    }
    internal void Write(string pointsPath, string output, Plane plane, BoundingBox scene, double tolerance)
    {
        var points = JsonSerializer.Deserialize<double[][]>(File.ReadAllText(pointsPath))!;
        var results = new List<object>(); int pairs = 0;
        double length = scene.Diagonal.Length * 4 + 10;
        var toXY = Transform.PlaneToPlane(plane,Plane.WorldXY);
        var bounds = _bodies.Select(b =>
        {
            // Project the full world box, not a tessellated/tight surface box.
            var corners = b.Geometry.GetBoundingBox(true).GetCorners();
            foreach (ref var p in corners.AsSpan()) p.Transform(toXY);
            var box = new BoundingBox(corners); box.Inflate(8*tolerance); return box;
        }).ToArray();
        foreach (var xyz in points)
        {
            var point = new Point3d(xyz[0],xyz[1],xyz[2]);
            bool clear = true; var evidence = new List<object>();
            foreach (var offset in new[] {Vector3d.Zero,plane.XAxis*(4*tolerance),plane.XAxis*(-4*tolerance),plane.YAxis*(4*tolerance),plane.YAxis*(-4*tolerance)})
            {
                var origin = point + offset;
                using var ray = new LineCurve(origin,origin+plane.ZAxis*length);
                for (int bi=0;bi<_bodies.Count;bi++)
                {
                    var body = _bodies[bi]; var box = bounds[bi]; var local = origin; local.Transform(toXY);
                    pairs++; GeometryBase geometry = body.Geometry; Brep? temporary = null;
                    if (box.IsValid && (local.X<box.Min.X || local.X>box.Max.X || local.Y<box.Min.Y || local.Y>box.Max.Y || box.Max.Z<local.Z-tolerance))
                    {
                        evidence.Add(new {body.Source,body.Fingerprint,success=true,clear=true,excludedBy="conservative scalar box"});
                        continue;
                    }
                    try
                    {
                        if (geometry is Extrusion extrusion) geometry = temporary = extrusion.ToBrep();
                        else if (geometry is Surface surface) geometry = temporary = surface.ToBrep();
                        if (geometry is Mesh mesh)
                        {
                            double d = Intersection.MeshRay(mesh,new Ray3d(origin+plane.ZAxis*tolerance,plane.ZAxis));
                            bool success = double.IsFinite(d); bool bodyClear = success && d < 0;
                            clear &= bodyClear;
                            evidence.Add(new {body.Source, body.Fingerprint, success, clear=bodyClear, gap=success&&d>=0?(double?)(d+tolerance):null});
                            continue;
                        }
                        bool ok = Intersection.CurveBrep(ray,(Brep)geometry,tolerance,out var overlaps,out var hits);
                        try
                        {
                            bool valid = ok && hits.All(p => p.IsValid) && overlaps.All(c => c.IsValid);
                            var gaps = hits.Where(p => p.IsValid).Select(p => (p-origin)*plane.ZAxis).ToArray();
                            bool bodyClear = valid && gaps.All(g => g <= tolerance) &&
                                overlaps.All(c => (c.PointAtStart-origin)*plane.ZAxis <= tolerance && (c.PointAtEnd-origin)*plane.ZAxis <= tolerance);
                            clear &= bodyClear;
                            evidence.Add(new {body.Source, body.Fingerprint, success=valid, clear=bodyClear, gaps, overlaps=overlaps.Length});
                        }
                        finally { foreach (var c in overlaps) c.Dispose(); }
                    }
                    finally { temporary?.Dispose(); }
                }
            }
            results.Add(new {world=xyz,clear,bodyRayPairs=_bodies.Count*5,evidence});
        }
        File.WriteAllText(output,JsonSerializer.Serialize(new { tolerance, bodyCount=_bodies.Count, bodyRayPairs=pairs,
            bodies=_bodies.Select((b,i) => new {b.Source,b.Fingerprint,bounds=bounds[i]}), results,
            method="all owned curved bodies x centre and four tolerance-offset rays; conservative scalar box exclusions, no index, no HLD classification" }));
    }
    internal void AuditRestored(ViewDrawing drawing, string baseline, string output, BoundingBox scene)
    {
        using var before = JsonDocument.Parse(File.ReadAllText(baseline));
        // Section fixes can restore a source that emitted no baseline curve at
        // all. The optional broader audit includes that newly generated ink too.
        bool allNew = Environment.GetEnvironmentVariable("PERFF_AUDIT_ALL_NEW") == "1";
        var sources = before.RootElement.GetProperty("pieces").EnumerateArray().Where(p => allNew || p.GetProperty("c").GetString() == "Unresolved")
            .Select(p => p.GetProperty("s").GetString()!).ToHashSet(StringComparer.Ordinal);
        if (allNew) sources.UnionWith(drawing.Items.Where(p => p.Class == M2DPlus.Core.LineClass.Visible).Select(p => p.SourceIdentity));
        var oldUnresolved = before.RootElement.GetProperty("pieces").EnumerateArray().Where(p => p.GetProperty("c").GetString() == "Unresolved")
            .GroupBy(p => p.GetProperty("s").GetString()!).ToDictionary(g => g.Key,g => g.Select(p => p.GetProperty("p").EnumerateArray().Select(v => v.GetDouble()).ToArray()).ToArray());
        var oldVisible = before.RootElement.GetProperty("pieces").EnumerateArray().Where(p => p.GetProperty("c").GetString() == "Visible")
            .GroupBy(p => p.GetProperty("s").GetString()!).ToDictionary(g => g.Key,g => g.Select(p => p.GetProperty("p").EnumerateArray().Select(v => v.GetDouble()).ToArray()).ToArray());
        var plane = drawing.Frame.Plane; double tol = drawing.Tolerance;
        var xy = Transform.PlaneToPlane(plane,Plane.WorldXY);
        var boxes = _bodies.Select(b =>
        {
            var corners = b.Geometry.GetBoundingBox(true).GetCorners();
            foreach (ref var p in corners.AsSpan()) p.Transform(xy);
            var box = new BoundingBox(corners); box.Inflate(8*tol); return box;
        }).ToArray();
        var failures = new List<object>(); long pairs = 0, excluded = 0; int samples = 0, uncertain = 0, curves = 0, unchangedVisibleCurves = 0;
        double length = scene.Diagonal.Length * 4 + 10;
        foreach (var piece in drawing.Items.Where(p => p.Class == M2DPlus.Core.LineClass.Visible && sources.Contains(p.SourceIdentity)))
        {
            if (piece.DepthGeometry is not Curve curve) continue;
            using var projectedGeometry = piece.CopyGeometry();
            if (projectedGeometry is Curve projectedCurve && oldVisible.TryGetValue(piece.SourceIdentity,out var oldCurves))
            {
                double[] Flatten(Polyline poly) => poly.SelectMany(p => new[]{p.X,p.Y,p.Z}).ToArray();
                double[] points;
                if(projectedCurve.TryGetPolyline(out var exact)) points=Flatten(exact);
                else
                {
                    using var approximation=projectedCurve.ToPolyline(tol/8,.02,0,0);
                    points=approximation is not null && approximation.TryGetPolyline(out var poly) ? Flatten(poly) :
                        new[]{projectedCurve.PointAtStart.X,projectedCurve.PointAtStart.Y,projectedCurve.PointAtStart.Z,projectedCurve.PointAtEnd.X,projectedCurve.PointAtEnd.Y,projectedCurve.PointAtEnd.Z};
                }
                if(oldCurves.Any(p=>p.SequenceEqual(points))) { unchangedVisibleCurves++; continue; }
            }
            curves++;
            int count = Math.Max(4,(int)Math.Ceiling(curve.GetLength()/Math.Max(8*tol,drawing.Frame.BoundingBox.Diagonal.Length/128)));
            foreach (double t in curve.DivideByCount(count,true) ?? throw new InvalidOperationException("Audit subdivision failed"))
            {
                var point = curve.PointAt(t); var projected = plane.ClosestPoint(point);
                bool wasUnresolved = oldUnresolved.TryGetValue(piece.SourceIdentity,out var oldUncertain) && oldUncertain.Any(poly => Enumerable.Range(1,poly.Length/3-1).Any(i =>
                {
                    var a=new Point3d(poly[(i-1)*3],poly[(i-1)*3+1],poly[(i-1)*3+2]);
                    var b=new Point3d(poly[i*3],poly[i*3+1],poly[i*3+2]);
                    return new Line(a,b).DistanceTo(projected,true)<=2*tol;
                }));
                if(!allNew && !wasUnresolved) continue;
                samples++; bool inconclusive = false;
                for (int bi=0;bi<_bodies.Count;bi++)
                {
                    var body = _bodies[bi]; var box = boxes[bi]; int hidden = 0; var gaps = new List<double>();
                    foreach (var offset in new[] {Vector3d.Zero,plane.XAxis*(4*tol),plane.XAxis*(-4*tol),plane.YAxis*(4*tol),plane.YAxis*(-4*tol)})
                    {
                        pairs++; var p = point+offset; var local=p; local.Transform(xy);
                        if(box.IsValid && (local.X<box.Min.X || local.X>box.Max.X || local.Y<box.Min.Y || local.Y>box.Max.Y || box.Max.Z<=local.Z+tol))
                        { excluded++; continue; }
                        GeometryBase geometry=body.Geometry; Brep? temporary=null;
                        try
                        {
                            if(geometry is Extrusion e) geometry=temporary=e.ToBrep();
                            else if(geometry is Surface s) geometry=temporary=s.ToBrep();
                            if(geometry is Mesh mesh)
                            {
                                double distance=Intersection.MeshRay(mesh,new Ray3d(p+plane.ZAxis*tol,plane.ZAxis));
                                if(!double.IsFinite(distance)) inconclusive=true;
                                else if(distance+tol>4*tol) { hidden++; gaps.Add(distance+tol); }
                                else if(distance>=0) inconclusive=true;
                                continue;
                            }
                            using var ray=new LineCurve(p,p+plane.ZAxis*length);
                            bool ok=Intersection.CurveBrep(ray,(Brep)geometry,tol,out var overlaps,out var hits);
                            try
                            {
                                if(!ok || hits.Any(h=>!h.IsValid) || overlaps.Any(c=>!c.IsValid)) { inconclusive=true; continue; }
                                double gap=hits.Select(h=>(h-p)*plane.ZAxis).DefaultIfEmpty(0).Max();
                                if(gap>4*tol) { hidden++; gaps.Add(gap); }
                                else if(gap>tol || overlaps.Any(c=>(c.PointAtStart-p)*plane.ZAxis>tol || (c.PointAtEnd-p)*plane.ZAxis>tol)) inconclusive=true;
                            }
                            finally { foreach(var c in overlaps)c.Dispose(); }
                        }
                        finally { temporary?.Dispose(); }
                    }
                    if(hidden==5) failures.Add(new {piece.SourceIdentity,t,world=new[]{point.X,point.Y,point.Z},occluder=body.Source,gaps});
                    else if(hidden!=0) inconclusive=true;
                }
                if(inconclusive) uncertain++;
            }
        }
        File.WriteAllText(output,JsonSerializer.Serialize(new {baseline,allNew,curves,unchangedVisibleCurves,samples,bodyCount=_bodies.Count,bodyRayPairs=pairs,
            expectedPairs=(long)samples*_bodies.Count*5,excluded,inconclusiveSamples=uncertain,wrongVisible=failures.Count,failures,
            method="Every owned body x centre and four offset rays; full world-box exclusions; no spatial index, native visibility labels or work budget. Samples detect contradictions, never certify unsampled spans."}));
    }
    public void Dispose() { foreach (var body in _bodies) body.Geometry.Dispose(); }
}
#endif
