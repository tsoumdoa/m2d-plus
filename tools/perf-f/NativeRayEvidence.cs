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
    public void Dispose() { foreach (var body in _bodies) body.Geometry.Dispose(); }
}
#endif
