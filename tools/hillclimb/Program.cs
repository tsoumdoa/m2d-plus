using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using M2DPlus.Core.Sections;
using M2DPlus.Rhino.Sections;
using Rhino.FileIO;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;
using Rhino.Runtime.InProcess;

namespace M2DPlus.SectionScore;

internal static class Program
{
    private const double Tol = .0001;
    private sealed record Case(string Id, Brep Geometry, Plane Plane, double? Analytic = null, int? Holes = null, bool NoFill = false);
    private sealed record Truth(double Area, int Loops, int Holes, double Error);
    [STAThread]
    private static void Main(string[] args)
    {
        var resolver = Assembly.LoadFrom(args[0]);
        resolver.GetType("RhinoInside.Resolver", true)!.GetMethod("Initialize", new[] { typeof(string) })!
            .Invoke(null, new object[] { @"C:\Program Files\Rhino 8\System" });
        Run(args);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Run(string[] args)
    {
        using var core = new RhinoCore(new[] { "/nosplash", "/notemplate" }, WindowStyle.NoWindow);
        var root = Path.GetFullPath(args[1]); var iteration = int.Parse(args[2]);
        var cases = new List<Case>();
        foreach (var name in new[] { "curved-steel", "diagrid-shell", "space-frame", "curved-facade", "tangent-continuity", "subd-vs-nurbs" })
        {
            var stem = Path.Combine(root, "tests", "fixtures", "features", name);
            using var file = File3dm.Read(stem + ".3dm");
            using var json = JsonDocument.Parse(File.ReadAllText(stem + ".json"));
            var planes = json.RootElement.GetProperty("views").EnumerateArray().Where(v => v.TryGetProperty("plane", out _))
                .Select(v => (v.GetProperty("name").GetString()!, Parse(v.GetProperty("plane")))).ToList();
            // Sidecars with only cameras also get explicit plan and side controls.
            if (planes.Count == 0) planes.Add(("Plan control", new Plane(new Point3d(0, 0, 1), Vector3d.ZAxis)));
            if (!planes.Any(p => Math.Abs(p.Item2.Normal.Z) < .5)) planes.Add(("Side control", Plane.WorldZX));
            foreach (var obj in file.Objects.Where(o => o.Attributes.Mode != global::Rhino.DocObjects.ObjectMode.InstanceDefinitionObject))
            foreach (var (id, brep) in Expand(file, obj.Geometry, obj.Attributes.Name, Transform.Identity))
            {
                foreach (var (view, plane) in planes) cases.Add(new(name + "/" + id + "/" + view, brep.DuplicateBrep(), plane));
                // Explicit independent torus integral from the generator's exact parameterization.
                if (name == "curved-steel" && id == "oblique-curved-CHS")
                    cases.Add(new("generated/CHS-arc-45", brep.DuplicateBrep(), new Plane(new Point3d(29, 0, 2), Vector3d.XAxis, new Vector3d(0, 1, 1)), TorusArea(4, .6, 1) - TorusArea(4, .45, 1), 1));
                brep.Dispose();
            }
        }
        Generated(cases);
        if (args.Contains("--probe-winding"))
        {
            using (var rootFile = File3dm.Read(Path.Combine(root, "tests", "fixtures", "subd_converted_brep.3dm")))
            {
                var b = (Brep)rootFile.Objects.First(o => o.Geometry is Brep).Geometry;
                Console.WriteLine("root SubD independent oracle " + JsonSerializer.Serialize(Oracle(b, Plane.WorldXY)));
                using var result = SectionRegionEngine.Compute(new[] { new SectionSource("subd", b, SectionStyleSnapshot.Default()) }, new(Plane.WorldXY, Tol, .001));
                Console.WriteLine("root SubD measured " + result.Regions.Sum(r => r.Area).ToString("R") + " loops=" + result.Regions.Sum(r => r.Loops.Count));
            }
            foreach (var item in cases.Where(c => c.Id == "generated/twisted-RHS" || c.Id == "generated/node-a"))
            {
                Console.WriteLine(item.Id + " " + item.Geometry.SolidOrientation);
                Console.WriteLine("exact profile oracle " + JsonSerializer.Serialize(ProfileOracle.Try(item.Geometry, item.Plane, Tol)));
                Intersection.BrepPlane(item.Geometry, item.Plane, Tol, out var fragments, out _);
                var loops = Curve.JoinCurves(fragments, Tol); foreach (var c in fragments) c.Dispose();
                foreach (var loop in loops)
                {
                    using var mass = AreaMassProperties.Compute(loop);
                    var t = loop.Domain.ParameterAt(.37); var p = loop.PointAt(t); var tangent = loop.TangentAt(t);
                    Console.WriteLine($"loop area={mass.Area:R}, direction={loop.ClosedCurveOrientation(item.Plane)}, p={p}, tangent={tangent}, centroid={mass.Centroid}");
                    foreach (var face in item.Geometry.Faces)
                        if (face.ClosestPoint(p, out var u, out var v) && face.PointAt(u, v).DistanceTo(p) < Tol && face.IsPointOnFace(u, v) != PointFaceRelation.Exterior)
                        {
                            var n = face.NormalAt(u, v);
                            Console.WriteLine($"face={face.FaceIndex} reversed={face.OrientationIsReversed} normal={n} dot={tangent * Vector3d.CrossProduct(n, item.Plane.Normal):R} radialDot={n * (p - mass.Centroid):R}");
                        }
                    loop.Dispose();
                }
            }
            foreach (var item in cases) item.Geometry.Dispose();
            return;
        }
        // Expectations are frozen by iteration 0, never inferred from a candidate result.
        Dictionary<string, Truth>? frozen = null;
        var canonicalPath = Path.Combine(root, "hillclimb", "expectations.json");
        var canonical = File.Exists(canonicalPath) && !args.Contains("--regenerate-oracle");
        if (canonical)
        {
            using var reference = JsonDocument.Parse(File.ReadAllText(canonicalPath));
            if (reference.RootElement.GetProperty("rhino").GetString() != global::Rhino.RhinoApp.Version.ToString())
                throw new InvalidOperationException("Frozen oracle requires its recorded exact Rhino build.");
            foreach (var fixture in reference.RootElement.GetProperty("fixtures").EnumerateArray())
            {
                var path = Path.Combine(root, fixture.GetProperty("path").GetString()!);
                var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
                if (hash != fixture.GetProperty("sha256").GetString()) throw new InvalidOperationException("Frozen fixture hash changed: " + path);
            }
            frozen = ReadTruth(reference.RootElement.GetProperty("cases"));
            if (frozen.Count != cases.Count || cases.Any(c => !frozen.ContainsKey(c.Id))) throw new InvalidOperationException("Frozen benchmark case set changed.");
        }
        else if (iteration != 0 || args.Contains("--reuse-oracle"))
        {
            using var baseline = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "artifacts", "hillclimb", "iteration-0", "records.json")));
            frozen = ReadTruth(baseline.RootElement);
            if (iteration != 0 && (frozen.Count != cases.Count || cases.Any(c => !frozen.ContainsKey(c.Id)))) throw new InvalidOperationException("Frozen benchmark case set changed.");
        }
        var output = Path.Combine(root, "artifacts", "hillclimb", "iteration-" + iteration); Directory.CreateDirectory(output);
        var records = new List<object>();
        var failures = args.Where(a => a.StartsWith("--gate-failure=", StringComparison.Ordinal)).Select(a => a[15..]).ToList();
        var costs = new List<double>();
        var uncertain = new Dictionary<string, int>(); var eligible = 0; var certified = 0;
        foreach (var item in cases)
        {
            if (records.Count % 100 == 0) Console.WriteLine($"{DateTime.UtcNow:O}: {records.Count}/{cases.Count}: {item.Id}");
            using var geometry = item.Geometry;
            try
            {
                // Analytic controls carry their mathematical loop/hole counts as well as area;
                // a mesh near tangency must not overrule an exact cylinder chord integral.
                var swept = !canonical && item.Analytic is null && IsSweptProfile(item.Id) && frozen is not null && frozen.TryGetValue(item.Id, out var prior) && prior.Area > prior.Error
                    ? ProfileOracle.Try(geometry, item.Plane, Tol) : null;
                var truth = canonical ? frozen![item.Id] : item.Analytic.HasValue
                    ? new Truth(item.Analytic.Value, item.Analytic.Value > 0 ? item.Holes.GetValueOrDefault() + 1 : 0, item.Holes.GetValueOrDefault(), 2e-5)
                    : swept is { } exact ? new Truth(exact.Area, exact.Loops, exact.Holes, 2e-5)
                    : LongitudinalTube(item) ?? (frozen is not null && frozen.TryGetValue(item.Id, out var cached) ? cached : Oracle(geometry, item.Plane));
                var expected = item.Analytic ?? truth.Area;
                var crossing = !item.NoFill && geometry.IsSolid && expected > truth.Error;
                if (crossing) eligible++;
                var request = new SectionRequest(item.Plane, Tol, .001);
                // Only crossings participate in timing; every other case still runs the gate.
                if (crossing) using (SectionRegionEngine.Compute(new[] { new SectionSource(item.Id, geometry, SectionStyleSnapshot.Default()) }, request)) { }
                var times = new List<double>(); SectionResult? result = null;
                for (var repeat = 0; repeat < (crossing ? 3 : 1); repeat++)
                {
                    result?.Dispose(); var clock = Stopwatch.StartNew();
                    result = SectionRegionEngine.Compute(new[] { new SectionSource(item.Id, geometry, SectionStyleSnapshot.Default()) }, request);
                    times.Add(clock.Elapsed.TotalMilliseconds);
                }
                using var owned = result!;
                var fills = owned.Regions.Where(r => r.Status == SectionStatus.Crossing).ToArray();
                var area = fills.Sum(r => r.Area); var tolerance = item.Analytic.HasValue ? 2e-5 : truth.Error;
                var correct = true;
                if (fills.Length > 0)
                {
                    if (!crossing) { failures.Add(item.Id + ": wrong fill"); correct = false; }
                    if (Math.Abs(area - expected) > tolerance) { failures.Add($"{item.Id}: area {area:R} != {expected:R} +/- {tolerance:R}"); correct = false; }
                    if (fills.Sum(r => r.Loops.Count) != truth.Loops || fills.Sum(r => r.Loops.Count(l => l.IsHole)) != (item.Holes ?? truth.Holes))
                    { failures.Add(item.Id + ": loop/hole mismatch"); correct = false; }
                }
                if (crossing && fills.Length > 0 && correct) certified++;
                times.Sort(); var elapsed = times[times.Count / 2]; if (crossing) costs.Add(elapsed);
                foreach (var diagnostic in owned.Diagnostics.Where(d => d.Status is SectionStatus.Uncertain or SectionStatus.Unsupported))
                    uncertain[diagnostic.Message] = uncertain.GetValueOrDefault(diagnostic.Message) + 1;
                records.Add(new { item.Id, crossing, expected, areaTolerance = tolerance, truth.Loops, truth.Holes, area, fills = fills.Length, ms = elapsed,
                    oracle = canonical ? "frozen audited independent reference" : item.Analytic.HasValue ? "analytic" : swept.HasValue ? "exact surface profile graph integration" : "frozen polygon/profile", diagnostics = owned.Diagnostics });
                File.AppendAllText(Path.Combine(output, "progress.jsonl"), JsonSerializer.Serialize(records[^1]) + Environment.NewLine);
            }
            catch (Exception ex) { failures.Add(item.Id + ": oracle/harness exception " + ex.Message); }
        }
        costs.Sort();
        var score = new { iteration, summary = args.Length > 3 ? args[3] : "baseline", gate = new { passed = failures.Count == 0, failures },
            primary = eligible == 0 ? 0 : (double)certified / eligible,
            secondary = new { certified, crossing = eligible, total = cases.Count, medianMs = costs.Count > 0 ? costs[costs.Count / 2] : 0, uncertain },
            loadNote = "Serial own Rhino.Inside host; shared machine load uncontrolled; warm-up + three consecutive samples per object; no native parallel HLD.",
            rhino = global::Rhino.RhinoApp.Version.ToString(), runtime = Environment.Version.ToString(),
            engineSha256 = Digest(typeof(SectionRegionEngine).Assembly.Location),
            scorerSha256 = Digest(Assembly.GetExecutingAssembly().Location),
            oracleSha256 = canonical ? Digest(canonicalPath) : null };
        File.WriteAllText(Path.Combine(output, "records.json"), JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true }));
        var line = JsonSerializer.Serialize(score); File.AppendAllText(Path.Combine(root, "hillclimb", "scores.jsonl"), line + Environment.NewLine);
        Console.WriteLine(line);
        if (failures.Count > 0) Environment.ExitCode = 1;
    }

    private static Plane Parse(JsonElement p)
    {
        var o = p.GetProperty("origin").EnumerateArray().Select(x => x.GetDouble()).ToArray();
        var x = p.GetProperty("x").EnumerateArray().Select(x => x.GetDouble()).ToArray();
        var y = p.GetProperty("y").EnumerateArray().Select(x => x.GetDouble()).ToArray();
        return new(new Point3d(o[0], o[1], o[2]), new Vector3d(x[0], x[1], x[2]), new Vector3d(y[0], y[1], y[2]));
    }

    private static Dictionary<string, Truth> ReadTruth(JsonElement cases) => cases.EnumerateArray().ToDictionary(e => e.GetProperty("Id").GetString()!, e =>
        new Truth(e.GetProperty("expected").GetDouble(), e.GetProperty("Loops").GetInt32(), e.GetProperty("Holes").GetInt32(), e.GetProperty("areaTolerance").GetDouble()));

    private static string Digest(string path) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private static IEnumerable<(string, Brep)> Expand(File3dm file, GeometryBase source, string id, Transform transform)
    {
        if (source is InstanceReferenceGeometry instance)
        {
            var definition = file.AllInstanceDefinitions.FindId(instance.ParentIdefId);
            foreach (var key in definition.GetObjectIds())
            {
                var obj = file.Objects.FindId(key);
                foreach (var leaf in Expand(file, obj.Geometry, id + "/" + obj.Attributes.Name, transform * instance.Xform)) yield return leaf;
            }
            yield break;
        }
        var brep = source switch { Brep b => b.DuplicateBrep(), Extrusion e => e.ToBrep(), SubD s => s.ToBrep(), Mesh m => Brep.CreateFromMesh(m, true), _ => null };
        if (brep is null) yield break;
        brep.Transform(transform); if (brep.IsSolid && brep.SolidOrientation == BrepSolidOrientation.Inward) brep.Flip();
        yield return (id, brep);
    }

    // Independent oracle: tessellate the exact surfaces, intersect triangle edges with the plane,
    // integrate polygon loops. Never calls BrepPlane, trim/cap, or the product section code.
    private static Truth Oracle(Brep brep, Plane plane)
    {
        var bounds = brep.GetBoundingBox(Transform.PlaneToPlane(plane, Plane.WorldXY));
        if (bounds.Min.Z >= -Tol * .1 || bounds.Max.Z <= Tol * .1) return new(0, 0, 0, 16 * Tol * Tol);
        var parameters = new MeshingParameters { Tolerance = Tol * .1, MinimumEdgeLength = Tol * .1, MaximumEdgeLength = .12,
            RefineGrid = true, GridAngle = .08, JaggedSeams = false };
        var meshes = Mesh.CreateFromBrep(brep, parameters);
        using var mesh = new Mesh(); foreach (var part in meshes) { mesh.Append(part); part.Dispose(); }
        // A finite coplanar face at an end contact is not a crossing material section.
        var distances = mesh.Vertices.Select(p => plane.DistanceTo(p)).ToArray();
        if (distances.Length == 0 || distances.Min() >= -Tol * .1 || distances.Max() <= Tol * .1)
            return new(0, 0, 0, 16 * Tol * Tol);
        var contours = Intersection.MeshPlane(mesh, plane) ?? Array.Empty<Polyline>();
        var fragments = contours.Where(p => p.Length > Tol).Select(p => new PolylineCurve(p)).ToArray();
        Curve[] joined;
        try { joined = Curve.JoinCurves(fragments, Tol * .1); }
        finally { foreach (var fragment in fragments) fragment.Dispose(); }
        var polygons = new List<Polyline>();
        try
        {
            foreach (var curve in joined)
            {
                if (!curve.TryGetPolyline(out var polygon)) throw new InvalidOperationException("Oracle contour is not polygonal.");
                for (var i = 0; i < polygon.Count; i++) polygon[i] = plane.ClosestPoint(polygon[i]);
                if (polygon[0].DistanceTo(polygon[^1]) <= Tol * .1) polygon[^1] = polygon[0];
                if (polygon.IsClosed) polygons.Add(polygon);
                else if (brep.IsSolid && PolygonArea(polygon, plane) > 16 * Tol * Tol)
                    throw new InvalidOperationException("Oracle has an unclosed material contour.");
            }
            var areas = polygons.Select(p => PolygonArea(p, plane)).ToArray(); var holes = 0; var area = 0d;
            for (var i = 0; i < polygons.Count; i++)
            {
                var depth = Enumerable.Range(0, polygons.Count).Count(j => i != j && PolygonContains(polygons[j], polygons[i][0], plane));
                if (depth % 2 != 0) holes++;
                area += depth % 2 == 0 ? areas[i] : -areas[i];
            }
            return new(Math.Max(0, area), polygons.Count, holes, Math.Max(Tol * Tol * 16, polygons.Sum(p => p.Length) * Tol));
        }
        finally { foreach (var curve in joined) curve.Dispose(); }
    }

    private static Truth? LongitudinalTube(Case item)
    {
        if (!item.Id.StartsWith("space-frame/CHS-", StringComparison.Ordinal)) return null;
        // The frozen generator uses the exact .065/.050 circular annulus,
        // rigidly oriented and scaled only along its axis. A longitudinal plane
        // gives two rectangular material strips; no contour stitching is needed.
        foreach (var face in item.Geometry.Faces)
        {
            if (!face.TryGetCylinder(out var cylinder, Tol * .01)) continue;
            var axis = cylinder.Axis;
            if (Math.Abs(axis * item.Plane.Normal) > 1e-9) return null;
            var offset = Math.Abs(item.Plane.DistanceTo(cylinder.Center));
            var r = .065; var inner = .05;
            if (offset >= r) return new(0, 0, 0, 2e-5);
            var positions = item.Geometry.Vertices.Select(v => (v.Location - cylinder.Center) * axis).ToArray();
            var length = positions.Max() - positions.Min();
            var area = 2 * length * (Math.Sqrt(r * r - offset * offset) - (offset < inner ? Math.Sqrt(inner * inner - offset * offset) : 0));
            return new(area, offset < inner ? 2 : 1, 0, 2e-5);
        }
        throw new InvalidOperationException("Frozen CHS profile could not be identified.");
    }

    private static bool IsSweptProfile(string id) => id.Contains("RHS", StringComparison.Ordinal) || id.Contains("I-beam", StringComparison.Ordinal) || id.Contains("helical-stair", StringComparison.Ordinal);

    private static bool PolygonContains(Polyline polygon, Point3d point, Plane plane)
    {
        plane.ClosestParameter(point, out var x, out var y); var inside = false;
        for (var i = 1; i < polygon.Count; i++)
        {
            plane.ClosestParameter(polygon[i - 1], out var ax, out var ay); plane.ClosestParameter(polygon[i], out var bx, out var by);
            if ((ay > y) != (by > y) && x < ax + (y - ay) * (bx - ax) / (by - ay)) inside = !inside;
        }
        return inside;
    }

    private static double PolygonArea(Polyline points, Plane plane)
    {
        var sum = 0d;
        for (var i = 1; i < points.Count; i++)
        {
            plane.ClosestParameter(points[i - 1], out var ax, out var ay); plane.ClosestParameter(points[i], out var bx, out var by);
            sum += ax * by - bx * ay;
        }
        return Math.Abs(sum) / 2;
    }

    private static double TorusArea(double radius, double tube, double slope)
    {
        const int count = 65536; var sum = 0d;
        (double X, double Y) P(int i) { var t = i * 2 * Math.PI / count; var z = tube * Math.Sin(t); return (Math.Sqrt(Math.Pow(radius + tube * Math.Cos(t), 2) - slope * slope * z * z), Math.Sqrt(1 + slope * slope) * z); }
        for (var i = 0; i < count; i++) { var a = P(i); var b = P(i + 1); sum += a.X * b.Y - b.X * a.Y; }
        return Math.Abs(sum) / 2;
    }

    private static void Generated(List<Case> cases)
    {
        using var rail = new Arc(new Plane(Point3d.Origin, Vector3d.ZAxis), 4, Math.PI / 2).ToNurbsCurve();
        rail.Transform(Transform.Rotation(-Math.PI / 4, Vector3d.ZAxis, Point3d.Origin));
        using var pipe = Brep.CreateThickPipe(rail, .45, .6, false, PipeCapMode.Flat, true, Tol, .001).Single();
        foreach (var angle in new[] { 0d, 30d, 60d })
        {
            var radians = angle * Math.PI / 180; var slope = Math.Tan(radians);
            cases.Add(new("generated/CHS-arc-" + angle, pipe.DuplicateBrep(), new Plane(new Point3d(4, 0, 0), Vector3d.XAxis, new Vector3d(0, Math.Sin(radians), Math.Cos(radians))),
                TorusArea(4, .6, slope) - TorusArea(4, .45, slope), 1));
        }
        // Exact tangent cylinder and its tolerance-band neighbors; no silent nudge.
        using var cylinder = new Cylinder(new Circle(new Plane(new Point3d(0, 0, -1), Vector3d.ZAxis), 1), 2).ToBrep(true, true);
        foreach (var offset in new[] { -2d, -.5, 0, .5, 2 })
            cases.Add(new("generated/tangent-pipe/" + offset, cylinder.DuplicateBrep(), new Plane(new Point3d(1 + offset * Tol, 0, 0), Vector3d.XAxis),
                offset < 0 ? 4 * Math.Sqrt(1 - Math.Pow(1 + offset * Tol, 2)) : 0, 0, offset >= 0));
        using var lumps = Brep.CreateFromBox(new BoundingBox(new Point3d(-1, -1, -3), new Point3d(1, 1, -2)));
        using var other = Brep.CreateFromBox(new BoundingBox(new Point3d(-1, -1, 2), new Point3d(1, 1, 3))); lumps.Append(other);
        cases.Add(new("generated/multi-lump-no-crossing", lumps.DuplicateBrep(), Plane.WorldXY, 0, 0, true));
        using var sphere = new Sphere(Point3d.Origin, 1).ToBrep(); cases.Add(new("generated/node-a", sphere.DuplicateBrep(), Plane.WorldXY, Math.PI, 0));
        sphere.Transform(Transform.Translation(.5, 0, 0)); cases.Add(new("generated/node-b-overlap", sphere.DuplicateBrep(), Plane.WorldXY, Math.PI, 0));
        using var open = new Cylinder(new Circle(Plane.WorldXY, 1), 2).ToBrep(false, false);
        cases.Add(new("generated/open-shell", open.DuplicateBrep(), new Plane(new Point3d(0, 0, 1), Vector3d.ZAxis), 0, 0, true));
        // Closed SubD control cage, converted to the exact limit-surface Brep.
        using var box = Mesh.CreateFromBox(new BoundingBox(new Point3d(-2, -2, -1), new Point3d(2, 2, 1)), 2, 2, 2);
        using var subd = SubD.CreateFromMesh(box); var canopy = subd.ToBrep();
        if (!canopy.IsSolid) throw new InvalidOperationException("Generated SubD canopy is not closed.");
        cases.Add(new("generated/closed-SubD-canopy", canopy, Plane.WorldXY));
        // Spline RHS, twist, and filleted I are frozen exact sweeps in curved-steel, tested
        // by every sidecar plane above and additional transverse mid-member planes below.
        foreach (var source in cases.Where(c => c.Id.StartsWith("curved-steel/", StringComparison.Ordinal) && c.Id.EndsWith("/Plan cut", StringComparison.Ordinal) &&
            (c.Id.Contains("RHS-3d-chord") || c.Id.Contains("twisted-RHS") || c.Id.Contains("I-beam"))).ToArray())
        {
            var b = source.Geometry.GetBoundingBox(true);
            var twisted = source.Id.Contains("twisted");
            cases.Add(new("generated/" + source.Id.Split('/')[1], source.Geometry.DuplicateBrep(),
                twisted ? new Plane(b.Center, Vector3d.ZAxis) : new Plane(b.Center, Vector3d.XAxis), twisted ? .09 : null, twisted ? 1 : null));
        }
    }
}
