using System.Text.Json;
using M2DPlus.Core.Sections;
using M2DPlus.Rhino.Sections;
using Rhino.FileIO;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;
using Xunit;

namespace M2DPlus.Rhino.Tests;

[Collection("Rhino")]
public sealed class CrossValidationReproductionTests(RhinoHost host)
{
    [RhinoFact, Trait("Category", "RhinoIntegration")]
    public void FrozenDiagridRoofAreaReproduction() => host.Run(() => {
        if (Environment.GetEnvironmentVariable("M2D_XVAL_REPRO") != "1") return;
        var root = Path.GetFullPath("../../../../../", AppContext.BaseDirectory);
        var data = Path.Combine(root, "hillclimb/c-xval");
        var output = Path.Combine(data, "repro"); Directory.CreateDirectory(output);
        var exportOnly = Environment.GetEnvironmentVariable("M2D_XVAL_EXPORT_ONLY") == "1";
        using var original = File3dm.Read(Path.Combine(data, "fixtures/diagrid-shell.3dm"));
        using var oracles = JsonDocument.Parse(File.ReadAllText(Path.Combine(data, "oracles.json")));
        var reports = new List<object>(); var failures = new List<string>();
        foreach (var name in new[] { "diagrid-iso-1-0", "diagrid-iso-1-3" })
        {
            using var file = new File3dm();
            file.Settings.ModelUnitSystem = original.Settings.ModelUnitSystem;
            file.Settings.ModelAbsoluteTolerance = .0001;
            file.Settings.ModelAngleToleranceRadians = .001;
            var obj = original.Objects.Single(o => o.Attributes.Name == name);
            var body = Assert.IsType<Brep>(obj.Geometry);
            file.Objects.AddBrep(body, obj.Attributes);
            Assert.True(file.Write(Path.Combine(output, name + ".3dm"), 8));
            if (exportOnly) continue;
            var plane = new Plane(Point3d.Origin, Vector3d.XAxis, Vector3d.ZAxis);
            var id = "diagrid-shell/Roof section/" + name;
            var oracle = oracles.RootElement.GetProperty(id);
            var expected = oracle.GetProperty("Area").GetDouble();
            var allowance = Math.Max(2e-5, expected * .002) + oracle.GetProperty("Error").GetDouble();
            using var result = SectionRegionEngine.Compute(new[] { new SectionSource(name, body, SectionStyleSnapshot.Default()) },
                new(plane, .0001, .001), prepareVisibility: true);
            var actual = result.Regions.Where(r => r.Status == SectionStatus.Crossing).Sum(r => r.Area);
            // Additional convergence audit only. Never edits or substitutes the frozen oracle.
            // Parameter-only curve roots avoid overlap-curve fitting; in-solid predicates
            // select material chords. No BrepPlane, caps or region mass properties enter it.
            var bounds = body.GetBoundingBox(Transform.PlaneToPlane(plane, Plane.WorldXY));
            double Estimate(int count, bool parametersOnly)
            {
                double area = 0; var half = bounds.Diagonal.X / 2;
                for (var i = 0; i < count; i++)
                {
                    var t = -Math.PI/2 + Math.PI*(i+.5)/count;
                    var x = bounds.Center.X + half*Math.Sin(t);
                    using var line = new LineCurve(plane.PointAt(x, bounds.Min.Y-1), plane.PointAt(x, bounds.Max.Y+1));
                    Point3d[] points;
                    if (parametersOnly)
                    {
                        Assert.True(Intersection.CurveBrep(line, body, 1e-8, .001, out var roots));
                        points = roots.Select(line.PointAt).ToArray();
                    }
                    else
                    {
                        Intersection.CurveBrep(line, body, 1e-5, out var overlaps, out points);
                        foreach (var overlap in overlaps) overlap.Dispose();
                    }
                    var ys = points.Select(p => { plane.ClosestParameter(p, out _, out var y); return y; }).Order().ToArray();
                    double length = 0;
                    for (var j = 1; j < ys.Length; j++)
                        if (ys[j]-ys[j-1] > 1e-9 && body.IsPointInside(plane.PointAt(x, (ys[j]+ys[j-1])/2), 1e-8, true)) length += ys[j]-ys[j-1];
                    area += length*half*Math.Cos(t)*Math.PI/count;
                }
                return area;
            }
            var refinement = new Dictionary<int, double>();
            foreach (var count in new[] { 128, 256, 512, 1024, 2048, 4096, 8192, 16384, 32768, 65536 })
                refinement[count] = Estimate(count, true);
            reports.Add(new { id, expected, allowance, actual, delta = actual-expected,
                plane = new { origin = new[] { 0, 0, 0 }, x = new[] { 1, 0, 0 }, y = new[] { 0, 0, 1 } },
                tolerance = .0001, angleTolerance = .001, prepareVisibility = true,
                loops = result.Regions.Select(r => new { count=r.Loops.Count, holes=r.Loops.Count(l => l.IsHole) }).ToArray(),
                requiresQuarantine=result.RequiresQuarantine, refinement, pointOverload65536=Estimate(65536, false),
                faces=body.Faces.Select(f => f.UnderlyingSurface().GetType().Name).ToArray() });
            if (Math.Abs(actual-expected)>allowance) failures.Add($"{id}: area {actual:R}, frozen {expected:R} +/- {allowance:R}");
        }
        if (exportOnly) return;
        File.WriteAllText(Path.Combine(output, "audit-net" + Environment.Version.Major + ".json"),
            JsonSerializer.Serialize(reports, new JsonSerializerOptions { WriteIndented=true }));
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }, TimeSpan.FromMinutes(15));
}
