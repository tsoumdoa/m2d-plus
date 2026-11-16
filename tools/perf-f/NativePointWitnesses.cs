using System.Text.Json;
using M2DPlus.Core.Visibility;
using Rhino.Geometry;

namespace M2DPlus.PerfF;

/// <summary>Independent point witnesses ON saved native-derived output curves.
/// Does not change drawing confidence or certify an unsampled native interval.</summary>
internal static class NativePointWitnesses
{
    internal static void Run(string prefix)
    {
        using var gate = JsonDocument.Parse(File.ReadAllText(prefix + ".gate.json"));
        double tolerance = gate.RootElement.GetProperty("tolerance").GetDouble();
        using var scene = JsonDocument.Parse(File.ReadAllText(prefix + ".planar-scene.json"));
        var data = scene.RootElement;
        var faces = JsonSerializer.Deserialize<PlanarFace[]>(data.GetProperty("Faces").GetRawText())!;
        double[] Vector(string key) => data.GetProperty(key).EnumerateArray().Select(v => v.GetDouble()).ToArray();
        var o = Vector("origin"); var xx = Vector("x"); var yy = Vector("y");
        var origin = new Point3d(o[0], o[1], o[2]);
        var x = new Vector3d(xx[0], xx[1], xx[2]); var y = new Vector3d(yy[0], yy[1], yy[2]);
        var z = Vector3d.CrossProduct(x, y); z.Unitize(); var plane = new Plane(origin, x, y);
        using var failures = JsonDocument.Parse(File.ReadAllText(prefix + ".failures.json"));
        using var actual = JsonDocument.Parse(File.ReadAllText(prefix + ".kernel-output.json"));
        var samples = JsonSerializer.Deserialize<int[]>(File.ReadAllText(prefix + ".native-point-requests.json"))!;
        var points = new List<object>();
        foreach (int sample in samples)
        {
            var failure = failures.RootElement[sample]; string source = failure.GetProperty("Source").GetString()!;
            var target = new Point3d(failure.GetProperty("X").GetDouble(), failure.GetProperty("Y").GetDouble(), failure.GetProperty("Z").GetDouble());
            int index = -1;
            foreach (var piece in actual.RootElement.GetProperty("Pieces").EnumerateArray())
            {
                index++;
                if (piece.GetProperty("Source").GetString() != source || piece.GetProperty("Class").GetInt32() != 0) continue;
                using var projected = (Curve)GeometryBase.FromJSON(piece.GetProperty("Geometry").GetString());
                using var worldCurve = (Curve)GeometryBase.FromJSON(piece.GetProperty("Depth").GetString());
                if (!projected.ClosestPoint(target, out var parameter, tolerance) || projected.PointAt(parameter).DistanceTo(target) > tolerance) continue;
                double step = projected.Domain.Length * tolerance / 64 / Math.Max(tolerance, projected.GetLength());
                var parameters = new[] { parameter, parameter - step, parameter + step, parameter - step / 8,
                    parameter + step / 8, parameter - step / 64, parameter + step / 64 };
                var oracle = new ExactVisibilityOracle.AllPairsPoints(faces, tolerance);
                var bounds = worldCurve.GetBoundingBox(Transform.PlaneToPlane(plane, Plane.WorldXY));
                var pairs = oracle.Prepare(bounds.Min.X - tolerance, bounds.Min.Y - tolerance, bounds.Max.X + tolerance, bounds.Max.Y + tolerance);
                foreach (double t in parameters)
                {
                    if (!worldCurve.Domain.IncludesParameter(t)) continue;
                    var world = worldCurve.PointAt(t); var projectedWorld = plane.ClosestPoint(world);
                    double distance = projectedWorld.DistanceTo(target);
                    if (distance > tolerance / 2 || projectedWorld.DistanceTo(projected.PointAt(t)) > tolerance / 64) continue;
                    var delta = world - origin; var local = new Vertex(delta * x, delta * y, delta * z);
                    var check = oracle.At(local, pairs);
                    points.Add(new { sample, source, piece = index, parameter = t, distance, world = new[] { world.X, world.Y, world.Z },
                        local, check, completePairs = oracle.CompletePairs, exactPointPairs = oracle.ExactPointPairs });
                }
            }
        }
        File.WriteAllText(prefix + ".native-point-candidates.json", JsonSerializer.Serialize(points));
    }
}
