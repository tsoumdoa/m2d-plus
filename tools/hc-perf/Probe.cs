using System.Diagnostics;
using M2DPlus.Rhino.Services;
using Rhino;
using Rhino.Geometry;
using Rhino.DocObjects;

namespace M2DPlus.HcPerf;

/// <summary>Times the phases of one native HLD call (diagnostic only).</summary>
internal static class Probe
{
    internal static void Classify(string model, string viewName, string? options = null)
    {
        using var document = RhinoDoc.OpenHeadless(model);
        using var cache = new SceneCache(1L << 40);
        using var watch = new DocumentWatch(document);
        using var capture = new SceneCapture(document, watch, cache: cache);
        while (!capture.Step(TimeSpan.FromHours(1))) { }
        using var lease = capture.Accept();
        var scene = lease.Value; var sources = scene.Prepare(CancellationToken.None);
        var view = ViewRequest.Parse(viewName, document);
        var frame = view.Resolve(scene.Bounds, scene.Tolerance);
        M2DPlus.Core.Visibility.Raster.OccluderBuffer? buffer = null;
        M2DPlus.Rhino.Visibility.RasterPrepass.DebugBuffer = b => buffer = b;
        var result = M2DPlus.Rhino.Visibility.RasterPrepass.Classify(sources, view, frame, scene.Bounds, scene.Tolerance,
            options is null ? new M2DPlus.Rhino.Visibility.RasterOptions { CertifyIsolated = true } : System.Text.Json.JsonSerializer.Deserialize<M2DPlus.Rhino.Visibility.RasterOptions>(options)!, CancellationToken.None);
        Console.WriteLine(result.Stats);
        if (buffer is not null)
        {
            var g = buffer.Grid; using var bmp = new System.Drawing.Bitmap(g.Width, g.Height);
            float lo = float.MaxValue, hi = float.MinValue;
            for (var j = 0; j < g.Height; j++) for (var i = 0; i < g.Width; i++) { var f = buffer.Far(i, j); if (!float.IsPositiveInfinity(f)) { lo = Math.Min(lo, f); hi = Math.Max(hi, f); } }
            for (var j = 0; j < g.Height; j++)
                for (var i = 0; i < g.Width; i++)
                {
                    var f = buffer.Far(i, j);
                    var c = float.IsPositiveInfinity(f) ? System.Drawing.Color.Red : System.Drawing.Color.FromArgb(0, (int)(40 + 200 * (f - lo) / Math.Max(1e-6, hi - lo)), 0);
                    bmp.SetPixel(i, g.Height - 1 - j, c);
                }
            var file = Path.Combine(Path.GetTempPath(), $"m2d-raster-{Path.GetFileNameWithoutExtension(model)}-{viewName.Replace(' ', '-')}.png");
            bmp.Save(file); Console.WriteLine(file);
        }
        if (System.Environment.GetEnvironmentVariable("M2D_PROBE_QUIET") == "1") return;
        foreach (var s in sources)
        {
            var state = result.Isolated.Contains(s) ? "isolated" : result.Remaining.Contains(s) ? "native" : result.CulledDirect.Contains(s) ? "culled" : "other";
            Console.WriteLine($"{s.Identity} {s.Geometry.GetType().Name} {state} {s.Geometry.GetBoundingBox(true)}");
        }
    }

    /// <summary>
    /// Evidence for one disagreement: for the edges of <paramref name="sourceId"/> passing near (u, v) in the view frame,
    /// exact ray tests (line-Brep intersection toward the viewer) at dense samples, and every raw native segment of that source.
    /// </summary>
    internal static void EdgeProof(string model, string viewName, string sourceId, double u, double v)
    {
        using var document = RhinoDoc.OpenHeadless(model);
        using var cache = new SceneCache(1L << 40);
        using var watch = new DocumentWatch(document);
        using var capture = new SceneCapture(document, watch, cache: cache);
        while (!capture.Step(TimeSpan.FromHours(1))) { }
        using var lease = capture.Accept();
        var scene = lease.Value; var sources = scene.Prepare(CancellationToken.None);
        var view = ViewRequest.Parse(viewName, document);
        var frame = view.Resolve(scene.Bounds, scene.Tolerance); var plane = frame.Plane; var d = view.Direction;
        var target = sources.First(s => s.Identity == sourceId);
        var brep = target.Geometry as Brep ?? (target.Geometry as Extrusion)?.ToBrep(true) ?? throw new InvalidOperationException("not a Brep");
        var reach = scene.Bounds.Diagonal.Length * 2;
        foreach (var edge in brep.Edges)
        {
            plane.ClosestParameter(edge.PointAtStart, out var su, out var sv); plane.ClosestParameter(edge.PointAtEnd, out var eu, out var ev);
            // Distance from (u, v) to the projected edge.
            double dx = eu - su, dy = ev - sv, l2 = dx * dx + dy * dy, t = l2 > 0 ? Math.Clamp(((u - su) * dx + (v - sv) * dy) / l2, 0, 1) : 0;
            if (Math.Sqrt(Math.Pow(su + t * dx - u, 2) + Math.Pow(sv + t * dy - v, 2)) > .05) continue;
            Console.WriteLine($"edge {edge.EdgeIndex}: {edge.PointAtStart} -> {edge.PointAtEnd}, projected ({su:F4},{sv:F4})-({eu:F4},{ev:F4}), smooth={edge.IsSmoothManifoldEdge(RhinoMath.ToRadians(1))}, valence={edge.Valence}");
            double worst = double.MaxValue;
            for (var k = 0; k <= 200; k++)
            {
                var p = edge.PointAt(edge.Domain.ParameterAt(k / 200.0));
                using var ray = new LineCurve(p - d * (scene.Tolerance * 20), p - d * reach);
                double nearest = double.MaxValue; string? by = null;
                foreach (var other in sources)
                {
                    if (other.Identity == sourceId || other.Geometry is not (Brep or Extrusion)) continue;
                    var ob = other.Geometry as Brep ?? ((Extrusion)other.Geometry).ToBrep(true);
                    if (!global::Rhino.Geometry.Intersect.Intersection.CurveBrep(ray, ob, scene.Tolerance, out var overlaps, out var points)) continue;
                    foreach (var q in points) { var dist = (p - q) * -d; if (dist < nearest) { nearest = dist; by = other.Identity; } }
                    foreach (var c in overlaps) { var dist = (p - c.PointAtStart) * -d; if (dist < nearest) { nearest = dist; by = other.Identity; } }
                }
                if (nearest < worst || k % 50 == 0) Console.WriteLine($"  sample {k}: nearest occluder {(nearest == double.MaxValue ? "none" : $"{nearest:F5} m in front")} by {by?[..8]}");
                worst = Math.Min(worst, nearest);
            }
            Console.WriteLine($"  minimum separation in front over samples: {worst:F5} m");
        }
        // Raw native segments of the source in a full-scene solve.
        using var viewport = new ViewportInfo();
        var radius = Math.Max(scene.Bounds.Diagonal.Length, 1);
        viewport.IsParallelProjection = true;
        viewport.SetCameraLocation(frame.Plane.Origin + frame.Plane.ZAxis * (radius * 4 + 10));
        viewport.SetCameraDirection(view.Direction); viewport.SetCameraUp(frame.Plane.YAxis);
        viewport.SetFrustum(frame.X.Min, frame.X.Max, frame.Y.Min, frame.Y.Max, .001, radius * 10 + 100);
        var parameters = new HiddenLineDrawingParameters { AbsoluteTolerance = scene.Tolerance, Flatten = false, IncludeHiddenCurves = true };
        parameters.SetViewport(viewport);
        foreach (var s in sources) parameters.AddGeometry(s.Geometry, s);
        using var drawing = HiddenLineDrawing.Compute(parameters, false, null, CancellationToken.None)!;
        drawing.WorldToHiddenLine.TryGetInverse(out var inverse);
        foreach (var segment in drawing.Segments)
        {
            if (segment.SegmentVisibility == HiddenLineDrawingSegment.Visibility.Unset) continue;
            if (segment.ParentCurve?.SourceObject?.Tag is not M2DPlus.Rhino.Services.PreparedLeaf leaf || leaf.Identity != sourceId || segment.CurveGeometry is not { } g) continue;
            using var c = g.DuplicateCurve(); c.Transform(inverse);
            plane.ClosestParameter(c.PointAtStart, out var a0, out var b0); plane.ClosestParameter(c.PointAtEnd, out var a1, out var b1);
            Console.WriteLine($"native {segment.SegmentVisibility,-10} parent edge {segment.ParentCurve.SourceObject.ToString()?.Length} ({a0:F4},{b0:F4})-({a1:F4},{b1:F4}) silhouette={segment.ParentCurve.SilhouetteType}");
        }
    }

    internal static void Run(string model, string viewName)
    {
        using var document = RhinoDoc.OpenHeadless(model);
        using var cache = new SceneCache(1L << 40);
        using var watch = new DocumentWatch(document);
        using var capture = new SceneCapture(document, watch, cache: cache);
        while (!capture.Step(TimeSpan.FromHours(1))) { }
        using var lease = capture.Accept();
        var scene = lease.Value; var sources = scene.Prepare(CancellationToken.None);
        var view = ViewRequest.Parse(viewName, document);
        var frame = view.Resolve(scene.Bounds, scene.Tolerance);
        foreach (var (near, far, dist, tol) in new[] { (.001, 10.0, 4.0, 0.0), (1.0, 3.0, 1.0, 0.0), (.001, 10.0, 4.0, 1.0), (.1, 10.0, 4.0, 0.0), (.001, 1000.0, 4.0, 0.0) })
        for (var variant = 0; variant < 1; variant++)
            for (var rep = 0; rep < 2; rep++)
            {
                var t = Stopwatch.StartNew();
                using var viewport = new ViewportInfo();
                var radius = Math.Max(scene.Bounds.Diagonal.Length, 1);
                viewport.IsParallelProjection = true;
                viewport.SetCameraLocation(frame.Plane.Origin + frame.Plane.ZAxis * (radius * dist + (dist > 2 ? 10 : 0)));
                viewport.SetCameraDirection(view.Direction); viewport.SetCameraUp(frame.Plane.YAxis);
                viewport.SetFrustum(frame.X.Min, frame.X.Max, frame.Y.Min, frame.Y.Max, near < 1 ? near : radius * near, radius * far + (far > 5 ? 100 : 0));
                var parameters = new HiddenLineDrawingParameters { AbsoluteTolerance = tol > 0 ? tol : scene.Tolerance, Flatten = false, IncludeHiddenCurves = true };
                parameters.SetViewport(viewport);
                foreach (var s in sources) parameters.AddGeometry(s.Geometry, s);
                var add = t.Elapsed.TotalMilliseconds;
                HiddenLineDrawing? d = variant switch
                {
                    0 => HiddenLineDrawing.Compute(parameters, false, null, CancellationToken.None),
                    1 => HiddenLineDrawing.Compute(parameters, false),
                    2 => HiddenLineDrawing.Compute(parameters, false, new Progress<double>(), new CancellationTokenSource().Token),
                    _ => HiddenLineDrawing.Compute(parameters, true),
                };
                var compute = t.Elapsed.TotalMilliseconds - add;
                var n = d?.Segments.Count() ?? -1; d?.Dispose();
                Console.WriteLine($"near {near} far {far} dist {dist} tol {tol} variant {variant} rep {rep}: add {add:F1} ms, compute {compute:F1} ms, segments {n}, total {t.Elapsed.TotalMilliseconds:F1}");
            }
    }
}
