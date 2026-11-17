using System.Diagnostics;
using System.Text.Json;
using M2DPlus.Core;
using M2DPlus.Rhino.Services;
using Rhino.Geometry;

namespace M2DPlus.PerfF;

/// <summary>One owned host; generated round-column scene, with no document or model writes.</summary>
internal static class RoundCoverBench
{
    internal static void Run(string output)
    {
        Directory.CreateDirectory(output);
        var owned = new List<GeometryBase>(); var input = new List<VisibilityInput>(); const int stacks = 200;
        try
        {
            for (int i = 0; i < stacks; i++)
                for (int k = 0; k < 2; k++)
                {
                    var centre = new Point3d(i % 20 * 300, i / 20 * 300, k * 1000);
                    var body = new Cylinder(new Circle(new Plane(centre, Vector3d.ZAxis), 100), 1000).ToBrep(true, true);
                    body.Rotate((i * .371 + k * .823) % (2 * Math.PI), Vector3d.ZAxis, centre);
                    owned.Add(body); input.Add(new(body, new PreparedLeaf($"column-{i}-{k}", [i, k], body)));
                }
            var rows = new List<object>();
            for (int run = 0; run < 3; run++)
            {
                using var drawing = new ViewDrawing { Tolerance = .001, Frame = new(Plane.WorldXY, new Interval(-200, 6000), new Interval(-200, 3000)) };
                var watch = Stopwatch.StartNew();
                string route = ComposedVisibility.Solve(input.ToArray(), drawing, ViewRequest.Parse("Top"), new(), default);
                watch.Stop();
                double Length(LineClass c) => drawing.Items.Where(p => p.Class == c).Sum(p => { using var g = p.CopyGeometry(); return g is Curve curve ? curve.GetLength() : 0; });
                rows.Add(new { run, wall = watch.Elapsed.TotalSeconds, drawing.NativeCalls, route, visible = Length(LineClass.Visible), hidden = Length(LineClass.Hidden), unresolved = Length(LineClass.Unresolved), info = drawing.Diagnostics });
                Console.WriteLine($"round-column model: {2 * stacks} columns, run {run}, {watch.Elapsed.TotalSeconds:F4}s; native {drawing.NativeCalls}; H {Length(LineClass.Hidden):F6}; U {Length(LineClass.Unresolved):F6}");
            }
            using var u = new Circle(new Plane(new Point3d(0, 0, -1), Vector3d.ZAxis), 100).ToNurbsCurve();
            using var v = new Circle(Plane.WorldXY, 100).ToNurbsCurve(); v.ChangeClosedCurveSeam(.371);
            using var uf = u.DuplicateCurve(); uf.Transform(Transform.PlanarProjection(Plane.WorldXY));
            var pairWatch = Stopwatch.StartNew(); int proved = 0;
            for (int i = 0; i < 200; i++)
                if (BoundedCurveCover.Covers(u, uf, u.Domain, [new(v, v, 0)], Plane.WorldXY, .001, 0, true)) proved++;
            pairWatch.Stop();
            Console.WriteLine($"different-seam circles: {proved}/200 proved, {pairWatch.Elapsed.TotalSeconds:F4}s total");
            File.WriteAllText(Path.Combine(output, "round-cover-bench.json"), JsonSerializer.Serialize(new { columns = 2 * stacks, tolerance = .001, rows, differentSeams = new { pairs = 200, proved, wall = pairWatch.Elapsed.TotalSeconds } }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally { foreach (var g in owned) g.Dispose(); }
    }
}
