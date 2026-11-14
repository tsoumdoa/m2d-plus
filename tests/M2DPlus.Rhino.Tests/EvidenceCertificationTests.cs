using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using M2DPlus.Core.Sections;
using M2DPlus.Rhino.Sections;
using Rhino.FileIO;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;
using Xunit;

namespace M2DPlus.Rhino.Tests;

/// <summary>Independent scanline quadrature of exact CurveBrep roots, never BrepPlane or cap areas.</summary>
[Collection("Rhino")]
public sealed class EvidenceCertificationTests(RhinoHost host)
{
    private const double Tol = .0001;
    private static readonly string[] Models = { "curved-steel", "diagrid-shell", "space-frame", "curved-facade", "tangent-continuity", "subd-vs-nurbs" };
    private sealed record Oracle(double Area, double Error, bool Open, int? Holes = null);
    private sealed record Sample(string Id, bool Crossing, bool Certified, double Expected, double Actual, double Ms, string[] Reasons);
    private static string Root => Environment.GetEnvironmentVariable("M2D_HILLCLIMB_ROOT") ??
        Path.GetFullPath("../../../../../", AppContext.BaseDirectory);

    [RhinoFact, Trait("Category", "RhinoIntegration")]
    public void FullEvidenceScore()
    {
        // Opt-in scoring leaves ordinary licensed acceptance unchanged.
        if (Environment.GetEnvironmentVariable("M2D_HILLCLIMB_ITERATION") is not { } iteration) return;
        var samples = new List<Sample>(); var failures = new List<string>();
        var cachePath = Path.Combine(Root, "hillclimb", "c-xval", "oracles.json");
        var cache = File.Exists(cachePath) ? JsonSerializer.Deserialize<Dictionary<string, Oracle>>(File.ReadAllText(cachePath))! : new();
        foreach (var model in Models)
        host.Run(() => {
            var path = Path.Combine(Root, "hillclimb", "c-xval", "fixtures", model + ".3dm");
            using var file = File3dm.Read(path);
            using var sidecar = JsonDocument.Parse(File.ReadAllText(Path.ChangeExtension(path, ".json")));
            var planes = sidecar.RootElement.GetProperty("views").EnumerateArray().Where(v => v.TryGetProperty("plane", out _))
                .Select(v => (v.GetProperty("name").GetString()!, PlaneFrom(v.GetProperty("plane")))).ToList();
            // Continuity has no section view in its sidecar: add a plan and side cut through its solids.
            if (planes.Count == 0) planes.AddRange(new[] { ("generated-plan", new Plane(new Point3d(0, 0, 1), Vector3d.ZAxis)), ("generated-side", new Plane(new Point3d(0, 5, 0), Vector3d.YAxis)) });
            var geometry = new List<(string Id, Brep Brep)>();
            void Expand(GeometryBase g, string id, Transform transform)
            {
                if (g is InstanceReferenceGeometry instance)
                {
                    var definition = file.AllInstanceDefinitions.FindId(instance.ParentIdefId);
                    foreach (var objectId in definition.GetObjectIds())
                    {
                        var member = file.Objects.FindId(objectId);
                        Expand(member.Geometry, id + "/" + member.Attributes.Name, transform * instance.Xform);
                    }
                    return;
                }
                var b = g switch { Brep brep => brep.DuplicateBrep(), Extrusion e => e.ToBrep(), SubD s => s.ToBrep(), Mesh m => Brep.CreateFromMesh(m, true), _ => null };
                if (b is null) return;
                b.Transform(transform); if (b.IsSolid && b.SolidOrientation == BrepSolidOrientation.Inward) b.Flip();
                geometry.Add((id, b));
            }
            foreach (var obj in file.Objects.Where(o => o.Attributes.Mode != global::Rhino.DocObjects.ObjectMode.InstanceDefinitionObject))
                Expand(obj.Geometry, obj.Attributes.Name, Transform.Identity);
            try
            {
                foreach (var (view, plane) in planes)
                foreach (var (id, b) in geometry)
                {
                    var key = model + "/" + view + "/" + id;
                    if (!cache.TryGetValue(key, out var oracle))
                    {
                        oracle = Integrate(b, plane); cache[key] = oracle;
                        File.WriteAllText(cachePath, JsonSerializer.Serialize(cache, new JsonSerializerOptions { WriteIndented = true }));
                    }
                    Score(key, b, plane, oracle, samples, failures);
                    File.AppendAllText(Path.Combine(Root, "hillclimb", "c-xval", "progress.log"), key + Environment.NewLine);
                }
            }
            finally { foreach (var (_, b) in geometry) b.Dispose(); }
        }, TimeSpan.FromMinutes(30));
        host.Run(() => Generated((id, b, p, o) => Score("generated/" + id, b, p, o ?? Integrate(b, p), samples, failures)), TimeSpan.FromMinutes(10));
        File.WriteAllText(cachePath, JsonSerializer.Serialize(cache, new JsonSerializerOptions { WriteIndented = true }));
        var crossings = samples.Where(s => s.Crossing).ToArray(); var costs = samples.Select(s => s.Ms).Order().ToArray();
        var score = new { iteration = int.Parse(iteration), summary = Environment.GetEnvironmentVariable("M2D_HILLCLIMB_SUMMARY") ?? "score",
            gate = new { passed = failures.Count == 0, failures }, primary = crossings.Length == 0 ? 0 : (double)crossings.Count(s => s.Certified) / crossings.Length,
            secondary = new { medianMs = costs[costs.Length / 2], crossingMedianMs = crossings.Select(s => s.Ms).Order().ElementAt(crossings.Length / 2),
                certified = crossings.Count(s => s.Certified), crossing = crossings.Length, cases = samples.Count,
                uncertain = samples.Where(s => !s.Certified && s.Crossing).SelectMany(s => s.Reasons).GroupBy(s => s).ToDictionary(g => g.Key, g => g.Count()) },
            loadNote = "Serial owned Rhino.Inside; shared machine, no isolation or timing speedup claim", runtime = Environment.Version.ToString(), rhino = global::Rhino.RhinoApp.Version.ToString(),
            fixtures = Models.ToDictionary(m => m, m => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(Root, "hillclimb", "c-xval", "fixtures", m + ".3dm"))))),
            oracle = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(cachePath))),
            engine = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(SectionRegionEngine).Assembly.Location))) };
        File.WriteAllText(Path.Combine(Root, "hillclimb", "c-xval", $"iteration-{iteration}.score.json"), JsonSerializer.Serialize(score));
        if (Environment.GetEnvironmentVariable("M2D_HILLCLIMB_MANAGED_SCORE") != "1")
            File.AppendAllText(Path.Combine(Root, "hillclimb", "c-xval", "scores.jsonl"), JsonSerializer.Serialize(score) + Environment.NewLine);
        File.WriteAllText(Path.Combine(Root, "hillclimb", "c-xval", $"iteration-{iteration}.json"), JsonSerializer.Serialize(samples, new JsonSerializerOptions { WriteIndented = true }));
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    private static void Score(string id, Brep brep, Plane plane, Oracle oracle, List<Sample> samples, List<string> failures)
    {
        var watch = Stopwatch.StartNew();
        using var result = SectionRegionEngine.Compute(new[] { new SectionSource(id, brep, SectionStyleSnapshot.Default()) }, new(plane, Tol, .001), prepareVisibility: true);
        var ms = watch.Elapsed.TotalMilliseconds;
        var filled = result.Regions.Where(r => r.Status == SectionStatus.Crossing).ToArray();
        var actual = filled.Sum(r => r.Area);
        var crossing = !oracle.Open && oracle.Area > Tol * Tol * 16;
        var allowance = Math.Max(2e-5, oracle.Area * .002) + oracle.Error;
        if (filled.Length > 0 && (!crossing || Math.Abs(actual - oracle.Area) > allowance)) failures.Add($"{id}: wrong fill {actual:R}, expected {oracle.Area:R} ±{allowance:R}");
        if (filled.Any(r => r.Loops.Count(l => !l.IsHole) != 1 || r.Loops.Any(l => !l.Curve.IsClosed))) failures.Add(id + ": invalid loops");
        if (filled.Length > 0 && oracle.Holes is { } holes && filled.Sum(r => r.Loops.Count(l => l.IsHole)) != holes) failures.Add(id + ": wrong holes");
        // Independently test material on both sides and in holes of every returned region.
        foreach (var region in filled)
        foreach (var loop in region.Loops)
        {
            var p = loop.Curve.PointAtNormalizedLength(.371);
            if (!brep.ClosestPoint(p, out var closest, out _, out _, out _, Tol * 8, out _) || p.DistanceTo(closest) > Tol * 4) failures.Add(id + ": loop not on source");
        }
        var certified = crossing && filled.Length > 0 && !result.RequiresQuarantine && Math.Abs(actual - oracle.Area) <= allowance;
        samples.Add(new(id, crossing, certified, oracle.Area, actual, ms, result.Diagnostics.Where(d => d.Status is SectionStatus.Uncertain or SectionStatus.Unsupported).Select(d => d.Message).Distinct().ToArray()));
    }

    private static Plane PlaneFrom(JsonElement p)
    {
        static double[] Values(JsonElement a) => a.EnumerateArray().Select(n => n.GetDouble()).ToArray();
        var o = Values(p.GetProperty("origin")); var x = Values(p.GetProperty("x")); var y = Values(p.GetProperty("y"));
        return new(new Point3d(o[0], o[1], o[2]), new Vector3d(x[0], x[1], x[2]), new Vector3d(y[0], y[1], y[2]));
    }

    internal static (double Area, double Error) IndependentMaterialArea(Brep brep, Plane plane)
    {
        var oracle = Integrate(brep, plane);
        return (oracle.Area, oracle.Error);
    }

    private static Oracle Integrate(Brep brep, Plane plane)
    {
        if (!brep.IsSolid) return new(0, 0, true);
        var box = brep.GetBoundingBox(Transform.PlaneToPlane(plane, Plane.WorldXY));
        if (box.Min.Z >= -Tol || box.Max.Z <= Tol) return new(0, 0, false);
        // Integrate exact material chord length; sin substitution regularizes circular end points.
        double Estimate(int count)
        {
            double sum = 0; var half = box.Diagonal.X / 2; var center = box.Center.X;
            for (var i = 0; i < count; i++)
            {
                var t = -Math.PI / 2 + Math.PI * (i + .5) / count;
                var x = center + half * Math.Sin(t);
                using var line = new LineCurve(plane.PointAt(x, box.Min.Y - 1), plane.PointAt(x, box.Max.Y + 1));
                Intersection.CurveBrep(line, brep, Tol * .1, out var overlaps, out var points);
                foreach (var c in overlaps) c.Dispose();
                var ys = points.Select(p => { plane.ClosestParameter(p, out _, out var y); return y; }).Order().ToArray();
                double length = 0;
                for (var j = 0; j + 1 < ys.Length; j++)
                    if (ys[j + 1] - ys[j] > Tol * .01 && brep.IsPointInside(plane.PointAt(x, (ys[j] + ys[j + 1]) / 2), Tol * .1, true)) length += ys[j + 1] - ys[j];
                sum += length * half * Math.Cos(t) * Math.PI / count;
            }
            return sum;
        }
        var previous = Estimate(128); var current = Estimate(256);
        var error = Math.Abs(current - previous);
        for (var n = 512; error > Math.Max(2e-6, current * .0005) && n <= 4096; n *= 2)
        { previous = current; current = Estimate(n); error = Math.Abs(current - previous); }
        return new(current, error, false);
    }

    private static void Generated(Action<string, Brep, Plane, Oracle?> score)
    {
        using var file = File3dm.Read(Path.Combine(Root, "hillclimb", "c-xval", "fixtures", "curved-steel.3dm"));
        Brep Named(string name) => (Brep)file.Objects.First(o => o.Attributes.Name == name).Geometry;
        // New thick torus arcs: independent exact torus parametrization, line-integral quadrature.
        using var rail = new Arc(new Plane(Point3d.Origin, Vector3d.ZAxis), 4, Math.PI / 2).ToNurbsCurve();
        rail.Rotate(-Math.PI / 4, Vector3d.ZAxis, Point3d.Origin);
        var tubes = Brep.CreateThickPipe(rail, .45, .6, false, PipeCapMode.Flat, true, Tol, .001);
        try
        {
            foreach (var angle in new[] { 0d, 30d, 60d })
            {
                var slope = Math.Tan(angle * Math.PI / 180);
                var p = new Plane(new Point3d(4, 0, 0), Vector3d.XAxis, new Vector3d(0, slope, 1));
                double Integral(double radius)
                {
                    var sum = 0d; Point2d At(double t) => new(Math.Sqrt(Math.Pow(4 + radius * Math.Cos(t), 2) - Math.Pow(slope * radius * Math.Sin(t), 2)), Math.Sqrt(1 + slope * slope) * radius * Math.Sin(t));
                    var a = At(0);
                    for (var k = 1; k <= 65536; k++) { var b = At(2 * Math.PI * k / 65536); sum += a.X * b.Y - a.Y * b.X; a = b; }
                    return Math.Abs(sum) / 2;
                }
                score($"CHS-arc-{angle}", tubes.Single(), p, new(Integral(.6) - Integral(.45), 1e-8, false, 1));
            }
        }
        finally { foreach (var b in tubes) b.Dispose(); }
        score("RHS-spline", Named("RHS-3d-chord"), new(new Point3d(3, 11, 1), Vector3d.XAxis), null);
        score("twisted-RHS", Named("twisted-RHS"), new(new Point3d(20, 9, 3), Vector3d.ZAxis), new(.09, 1e-6, false, 1));
        var angleMid = .75;
        score("I-beam-arc-fillets", Named("I-beam-filleted-web-flanges"), new(new Point3d(16 + 4 * Math.Cos(angleMid), 4 * Math.Sin(angleMid), 0), new Vector3d(-Math.Sin(angleMid), Math.Cos(angleMid), 0)), new(.44 - 4 * .04 * .04 * (1 - Math.PI / 4), 2e-5, false, 0));
        foreach (var offset in new[] { 0, -.5, .5, -2d, 2d })
        {
            using var pipe = new Cylinder(new Circle(new Plane(new Point3d(0, 0, -2), Vector3d.ZAxis), 1), 4).ToBrep(true, true);
            var plane = new Plane(new Point3d(1 + offset * Tol, 0, 0), Vector3d.XAxis);
            var expected = offset <= -1 ? 8 * Math.Sqrt(1 - Math.Pow(1 + offset * Tol, 2)) : 0;
            score("pipe-contact-" + offset, pipe, plane, new(expected, 1e-7, false, offset <= -1 ? 0 : null));
        }
        using var a = new Sphere(new Point3d(0, 0, -2), .5).ToBrep();
        using var b2 = new Sphere(new Point3d(0, 0, 2), .5).ToBrep(); a.Append(b2);
        score("multi-lump-straddling-no-crossing", a, Plane.WorldXY, new(0, 0, false));
        using var c = new Sphere(Point3d.Origin, .5).ToBrep(); a.Append(c);
        score("multi-lump-one-crossing", a, Plane.WorldXY, new(Math.PI * .25, 1e-7, false, 0));
        score("node-overlap-a", c, Plane.WorldXY, new(Math.PI * .25, 1e-7, false, 0));
        using var other = new Sphere(new Point3d(.3, 0, 0), .5).ToBrep();
        score("node-overlap-b", other, Plane.WorldXY, new(Math.PI * .25, 1e-7, false, 0));
        using var mesh = Mesh.CreateFromBox(new BoundingBox(new Point3d(-2, -2, -1), new Point3d(2, 2, 1)), 2, 2, 2);
        using var subd = SubD.CreateFromMesh(mesh); using var canopy = subd.ToBrep();
        score("closed-SubD-canopy", canopy, Plane.WorldXY, null);
        using var shell = new Cylinder(new Circle(new Plane(new Point3d(0, 0, -1), Vector3d.ZAxis), 1), 2).ToBrep(false, false);
        score("open-shell", shell, Plane.WorldXY, new(0, 0, true));
    }
}
