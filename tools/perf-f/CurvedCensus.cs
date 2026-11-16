using System.Text.Json;
using M2DPlus.Rhino.Services;
using Rhino.Geometry;
using Point = Rhino.Geometry.Point;

namespace M2DPlus.PerfF;

/// <summary>
/// Curved-engine study (hillclimb/curved-engine): what the sources Rhino's native HLD receives today are made of.
/// No view solve. Every prepared leaf that is not kernel-eligible is classified by its faces (TryGetPlane/Cylinder/
/// Cone/Sphere/Torus on the untrimmed surface) and by whether the whole body is a strictly recognised analytic solid.
/// </summary>
internal static class CurvedCensus
{
    internal enum Kind { Sphere, Cylinder, Pipe, Cone, Torus, TorusElbow, ArcExtrusion, AnalyticOther, Revolved, Freeform, SubD, Mesh, Curve, Other }

    internal sealed record Shape(Kind Kind, int Faces, int Planar, int Cyl, int Cone, int Sph, int Tor, int Free, bool Solid, double EdgeLength, double Area, string? Exact = null);

    internal static Shape Classify(GeometryBase geometry, double tol)
    {
        if (geometry is Curve c) return new(Kind.Curve, 0, 0, 0, 0, 0, 0, 0, false, c.GetLength(), 0);
        if (geometry is Mesh) return new(Kind.Mesh, 0, 0, 0, 0, 0, 0, 0, false, 0, 0);
        if (geometry is SubD) return new(Kind.SubD, 0, 0, 0, 0, 0, 0, 0, false, 0, 0);
        Brep? brep = geometry switch { Brep b => b, Extrusion e => e.ToBrep(), Surface s => s.ToBrep(), _ => null };
        if (brep is null) return new(Kind.Other, 0, 0, 0, 0, 0, 0, 0, false, 0, 0);
        try
        {
            int planar = 0, cyl = 0, cone = 0, sph = 0, tor = 0, free = 0, revolved = 0;
            var cylinders = new List<Cylinder>(); var spheres = new List<Sphere>(); var cones = new List<Cone>(); var tori = new List<Torus>(); var planes = new List<Plane>();
            foreach (var face in brep.Faces)
            {
                if (face.TryGetPlane(out var plane, tol)) { planar++; planes.Add(plane); }
                else if (face.TryGetCylinder(out var cylinder, tol)) { cyl++; cylinders.Add(cylinder); }
                else if (face.TryGetSphere(out var sphere, tol)) { sph++; spheres.Add(sphere); }
                else if (face.TryGetCone(out var co, tol)) { cone++; cones.Add(co); }
                else if (face.TryGetTorus(out var torus, tol)) { tor++; tori.Add(torus); }
                else { free++; using var surface = face.UnderlyingSurface(); if (surface is RevSurface) revolved++; }
            }
            double length = brep.Edges.Sum(e => e.GetLength()), area = brep.GetArea();
            bool solid = brep.IsSolid; int faces = brep.Faces.Count;
            Kind kind;
            bool SameAxis(Cylinder a, Cylinder b) => a.Axis.IsParallelTo(b.Axis, 1e-6) != 0 && new Line(a.Center, a.Center + a.Axis).DistanceTo(b.Center, false) <= tol;
            if (free > 0) kind = free == revolved ? Kind.Revolved : Kind.Freeform;
            else if (solid && sph == faces && spheres.All(s => s.Center.DistanceTo(spheres[0].Center) <= tol && Math.Abs(s.Radius - spheres[0].Radius) <= tol)) kind = Kind.Sphere;
            else if (solid && cyl > 0 && planar == 2 && cyl + planar == faces && cylinders.All(x => SameAxis(x, cylinders[0])))
            {
                var radii = cylinders.Select(x => x.Radius).OrderBy(r => r).ToArray();
                int distinct = 1; for (int i = 1; i < radii.Length; i++) if (radii[i] - radii[i - 1] > tol) distinct++;
                // The two cap planes must not meet inside the (outer) cylinder: then the body is cylinder ∩ two half-spaces.
                bool capsOk = planes.All(p => Math.Abs(p.Normal * cylinders[0].Axis) > 1e-6);
                kind = !capsOk ? Kind.AnalyticOther : distinct == 1 ? Kind.Cylinder : distinct == 2 ? Kind.Pipe : Kind.AnalyticOther;
            }
            else if (solid && cone > 0 && cyl == 0 && sph == 0 && tor == 0 && planar is 1 or 2 && cones.All(x => x.ApexPoint.DistanceTo(cones[0].ApexPoint) <= tol)) kind = Kind.Cone;
            else if (solid && tor == faces) kind = Kind.Torus;
            else if (solid && tor > 0 && planar == 2 && tor + planar == faces) kind = Kind.TorusElbow;
            else if (cyl > 0 && cone == 0 && sph == 0 && tor == 0 && cylinders.All(x => x.Axis.IsParallelTo(cylinders[0].Axis, 1e-6) != 0)) kind = Kind.ArcExtrusion;
            else kind = Kind.AnalyticOther;
            return new(kind, faces, planar, cyl, cone, sph, tor, free, solid, length, area, CurvedEngine.RecognisedAs(brep, tol));
        }
        finally { if (!ReferenceEquals(brep, geometry)) brep.Dispose(); }
    }

    internal static void Run(PreparedScene scene, IReadOnlyList<PreparedLeaf> prepared, string stem, string output)
    {
        var tol = scene.Tolerance; var perKey = new Dictionary<string, Shape>(StringComparer.Ordinal);
        var rows = new Dictionary<Kind, (int Sources, int Definitions, int Faces, double Length, double Area)>();
        var faceKinds = new int[6]; int kernel = 0, points = 0, curved = 0; var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var leaf in prepared)
        {
            if (leaf.Geometry is Point) { points++; continue; }
            if (leaf.KernelEligible) { kernel++; continue; }
            curved++;
            var key = leaf.Canonical is { } canonical ? canonical.Key + "/" + canonical.Scale.ToString("R") : "leaf/" + leaf.Identity;
            if (!perKey.TryGetValue(key, out var shape)) perKey.Add(key, shape = Classify(leaf.Geometry, tol));
            var row = rows.GetValueOrDefault(shape.Kind);
            rows[shape.Kind] = (row.Sources + 1, row.Definitions + (seen.Add(key) ? 1 : 0), row.Faces + shape.Faces, row.Length + shape.EdgeLength, row.Area + shape.Area);
            faceKinds[0] += shape.Planar; faceKinds[1] += shape.Cyl; faceKinds[2] += shape.Cone; faceKinds[3] += shape.Sph; faceKinds[4] += shape.Tor; faceKinds[5] += shape.Free;
        }
        double totalLength = rows.Values.Sum(r => r.Length);
        // What the prototype's strict recognition would take as an exact solid (sphere, cylinder, tube, extruded arc).
        var exact = new SortedDictionary<string, (int Sources, double Length)>(StringComparer.Ordinal);
        foreach (var leaf in prepared)
        {
            if (leaf.Geometry is Point || leaf.KernelEligible) continue;
            var key = leaf.Canonical is { } canonical ? canonical.Key + "/" + canonical.Scale.ToString("R") : "leaf/" + leaf.Identity;
            var shape = perKey[key]; var name = shape.Exact ?? (shape.Kind == Kind.Curve ? "curve (candidate only)" : "not exact"); var old = exact.GetValueOrDefault(name);
            exact[name] = (old.Sources + 1, old.Length + shape.EdgeLength);
        }
        Console.WriteLine($"=== census {stem}: leaves {prepared.Count}, kernel-eligible (planar) {kernel}, points {points}, curved/native {curved}, distinct curved shapes {perKey.Count}");
        foreach (var (kind, r) in rows.OrderByDescending(r => r.Value.Sources))
            Console.WriteLine($"    {kind,-14} sources {r.Sources,6} ({(double)r.Sources / Math.Max(1, curved):P1}) definitions {r.Definitions,5} faces {r.Faces,7} edge length {r.Length,10:F1} ({r.Length / Math.Max(1e-9, totalLength):P1}) area {r.Area:F1}");
        Console.WriteLine($"    faces on curved sources: planar {faceKinds[0]}, cylinder {faceKinds[1]}, cone {faceKinds[2]}, sphere {faceKinds[3]}, torus {faceKinds[4]}, freeform {faceKinds[5]}");
        Console.WriteLine($"    prototype exact tier: {string.Join("; ", exact.Select(x => $"{x.Key} {x.Value.Sources} ({(double)x.Value.Sources / Math.Max(1, curved):P1} of sources, {x.Value.Length / Math.Max(1e-9, totalLength):P1} of edge length)"))}");
        File.WriteAllText(Path.Combine(output, stem + "-census.json"), JsonSerializer.Serialize(new
        {
            model = stem, leaves = prepared.Count, kernel, points, curved, shapes = perKey.Count,
            exact = exact.ToDictionary(x => x.Key, x => new { x.Value.Sources, x.Value.Length }),
            kinds = rows.ToDictionary(r => r.Key.ToString(), r => new { r.Value.Sources, r.Value.Definitions, r.Value.Faces, r.Value.Length, r.Value.Area }),
            faces = new { planar = faceKinds[0], cylinder = faceKinds[1], cone = faceKinds[2], sphere = faceKinds[3], torus = faceKinds[4], freeform = faceKinds[5] }
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
