using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using M2DPlus.Core;
using M2DPlus.Rhino.Services;
using M2DPlus.Core.Sections;
using Rhino;
using Rhino.Geometry;
using Rhino.Runtime.InProcess;
using Point = Rhino.Geometry.Point;

namespace M2DPlus.Render;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        var resolver = Assembly.LoadFrom(args[0]);
        resolver.GetType("RhinoInside.Resolver", true)!.GetMethod("Initialize", new[] { typeof(string) })!
            .Invoke(null, new object[] { @"C:\Program Files\Rhino 8\System" });
        Run(args);
    }

    // Keep Rhino references out of the bootstrap JIT, until the resolver is registered.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Run(string[] args)
    {
        using var core = new RhinoCore(new[] { "/nosplash", "/notemplate" }, WindowStyle.NoWindow);
        var output = Path.GetFullPath(args[2], args[1]); Directory.CreateDirectory(output);
        if (args[3] == "view-component")
        {
            Assembly.LoadFrom(@"C:\Program Files\Rhino 8\Plug-ins\Grasshopper\GH_IO.dll");
            Assembly.LoadFrom(@"C:\Program Files\Rhino 8\Plug-ins\Grasshopper\Grasshopper.dll");
            ViewCaseComponent.Run(args[4], args[5], args[6], output); return;
        }
        if (args[3] == "fingerprints") { Benchmark(args.Skip(4), output); return; }
        IRenderEngine engine = args[3] switch { "m3" => new M3Engine(), "m2d" or "m2" => new M2DEngine(), "raw" => new RawHldEngine(), _ => throw new ArgumentException("Unknown engine adapter (m2d or raw)") };
        var cards = new StringBuilder(); var records = new List<object>();
        var process = Process.GetCurrentProcess(); var engineSha256 = FileHash(typeof(NativeDrawing).Assembly.Location);
        var modelFiles = args.Skip(4).ToList();
        if (args[3] == "m3") modelFiles.Add(RangeControl(output));
        foreach (var file in modelFiles)
        {
            var fixtureSha256 = FileHash(file); var cpuStart = process.TotalProcessorTime.TotalMilliseconds;
            var timer = Stopwatch.StartNew();
            using var document = RhinoDoc.OpenHeadless(file);
            using var cache = new SceneCache();
            using var watch = new DocumentWatch(document);
            using var capture = new SceneCapture(document, watch, cache: cache);
            while (!capture.Step(TimeSpan.FromMilliseconds(12))) { }
            var lease = capture.Accept(); var captureMs = timer.Elapsed.TotalMilliseconds; var cost = lease.Value.Cost;
            var bounds = lease.Value.Bounds;
            var views = args[3] is "m2" or "m3" ? CutViews(bounds) : new[] { "Top", "Front", "Right", "SW" }.Select(v => ViewRequest.Parse(v)).ToArray();
            if (args[3] == "m3" && Path.GetFileName(file) == "range-control.3dm")
                views = new[] { ViewRequest.Parse(Plane.WorldXY) with { Name = "Range-control" } };
            else if (args[3] == "m3")
            {
                var along = new Vector3d(1, .18, 0); along.Unitize();
                views = new[] { views[0], ViewRequest.Parse(new Plane(new Point3d(bounds.Center.X, bounds.Center.Y, 0), along, Vector3d.ZAxis)) with { Name = "Long-section" } };
            }
            using var batch = engine.Draw(lease, views);
            foreach (var view in batch.Views)
            {
                var name = Path.GetFileNameWithoutExtension(file) + "-" + view.Name;
                var svg = Render(view, Path.Combine(output, name + ".png"));
                File.WriteAllText(Path.Combine(output, name + ".svg"), svg);
                cards.Append($"<article><h2>{WebUtility.HtmlEncode(name)}</h2><div class='pair'><div>{svg}</div><div class='dark'>{svg}</div></div><p>{(view.Info.Contains("Uncertain:", StringComparison.Ordinal) ? "SOURCE UNCERTAINTY: some geometry is withheld; inspect Info. " : "")}Read + capture {captureMs:F1} ms · routing/extraction {view.Seconds * 1000:F1} ms · {view.Items.Count} pieces</p><details><summary>Info</summary><pre>{WebUtility.HtmlEncode(view.Info)}</pre></details></article>");
                records.Add(new { file, fixtureSha256, view.Name, captureMs, capture.MaximumSliceMilliseconds,
                    cost, view.Seconds, view.KernelEdges, view.CurvedCandidateEdges, view.PlanarNativeEdges, view.NativeCalls, count = view.Items.Count, classes = view.Items.GroupBy(p => p.Class).ToDictionary(g => g.Key.ToString(), g => g.Count()), intensities = view.Items.Select(p => p.Intensity).Distinct().Order().ToArray(), regions = view.Regions.Count, view.Info, wallMs = timer.Elapsed.TotalMilliseconds,
                    processCpuMs = process.TotalProcessorTime.TotalMilliseconds - cpuStart, peakWorkingSet = process.PeakWorkingSet64,
                    tolerance = document.ModelAbsoluteTolerance, units = document.ModelUnitSystem.ToString(), cache = "fresh per file; shared across views" });
                Console.WriteLine($"{name}: {view.Seconds:F3}s, {view.Items.Count} pieces, {view.Error ?? "ok"}");
            }
        }
        File.WriteAllText(Path.Combine(output, "results.json"), JsonSerializer.Serialize(new { engine = engine.Name, engineSha256, rhino = RhinoApp.Version.ToString(), runtime = Environment.Version.ToString(), multipleThreads = false, workers = 0, records }, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(Path.Combine(output, "index.html"), "<!doctype html><html><meta charset='utf-8'><title>M2D+ renders</title><style>body{margin:24px;background:#eee;color:#222;font:14px system-ui}h1{font-size:25px}main{display:grid;grid-template-columns:repeat(auto-fit,minmax(560px,1fr));gap:20px}article{background:white;padding:16px;border:1px solid #ccc;border-radius:8px}h2{font-size:17px}.pair{display:flex}.pair>div{width:50%;background:white}.pair svg{width:100%;height:260px}.pair .dark{background:#15191e}.pair svg{color:#000}.dark svg{color:#f5f5f5}.dark .point{fill:#f5f5f5}pre{white-space:pre-wrap;font-size:12px}</style><h1>M2D+ section renders</h1><p>" + WebUtility.HtmlEncode(engine.Name) + " · serial HLD, multipleThreads=false · applied class weights (cut 3 px, profile 2 px, edge 1 px), dashed hidden, Beyond 55% ink, continuous depth fade, far-clip lines, orange unresolved, cobalt frame · light / dark</p><main>" + cards + "</main></html>");
        Console.WriteLine(Path.Combine(output, "index.html"));
    }

    private static string RangeControl(string output)
    {
        using var doc = RhinoDoc.CreateHeadless(null); doc.ModelAbsoluteTolerance = .00001;
        using var bottom = new Rectangle3d(new Plane(new Point3d(0, 0, -3), Vector3d.ZAxis), new Interval(-2, 2), new Interval(-2, 2)).ToNurbsCurve();
        using var top = new Rectangle3d(new Plane(new Point3d(0, 0, 1), Vector3d.ZAxis), new Interval(-.5, .5), new Interval(-.5, .5)).ToNurbsCurve();
        var lofts = Brep.CreateFromLoft(new[] { bottom, top }, Point3d.Unset, Point3d.Unset, LoftType.Straight, false);
        try { using var solid = lofts[0].CapPlanarHoles(.00001); doc.Objects.AddBrep(solid); }
        finally { foreach (var loft in lofts) loft.Dispose(); }
        doc.Objects.AddLine(new Point3d(3, 0, -.5), new Point3d(6, 0, -2));
        var file = Path.Combine(output, "range-control.3dm");
        if (!doc.Write3dmFile(file, new global::Rhino.FileIO.FileWriteOptions())) throw new InvalidOperationException("Could not write analytic render control.");
        return file;
    }

    private static ViewRequest[] CutViews(BoundingBox bounds)
    {
        var z = bounds.Min.Z + Math.Min(1.2, bounds.Diagonal.Z * .4);
        var plan = new Plane(new Point3d(0, 0, z), Vector3d.XAxis, Vector3d.YAxis);
        var section = new Plane(new Point3d(0, bounds.Center.Y, 0), Vector3d.XAxis, Vector3d.ZAxis);
        var boxPlane = Plane.WorldXY;
        var box = new Box(boxPlane, new Interval(bounds.Min.X, bounds.Max.X), new Interval(bounds.Min.Y, bounds.Max.Y), new Interval(bounds.Min.Z, z));
        return new[] { ViewRequest.Parse(plan) with { Name = "Plan-storey" }, ViewRequest.Parse(section) with { Name = "Long-section" },
            ViewRequest.Parse(box) with { Name = "Box" }, ViewRequest.Parse(new Rectangle3d(plan,
                new Interval(bounds.Min.X + bounds.Diagonal.X * .25, bounds.Max.X - bounds.Diagonal.X * .25),
                new Interval(bounds.Min.Y, bounds.Max.Y))) with { Name = "Cropped-plan" } };
    }

    private static void Benchmark(IEnumerable<string> files, string output)
    {
        var records = new List<object>();
        foreach (var file in files)
        {
            using var document = RhinoDoc.OpenHeadless(file);
            var geometry = document.Objects.GetObjectList(global::Rhino.DocObjects.ObjectType.AnyObject).Select(o => o.Geometry).ToArray();
            long memory = 0, faces = 0, triangles = 0;
            foreach (var g in geometry) { memory += (long)g.MemoryEstimate(); if (g is Brep b) faces += b.Faces.Count; if (g is Mesh m) triangles += m.Faces.TriangleCount + 2L * m.Faces.QuadCount; }
            var timer = Stopwatch.StartNew(); var allocated = GC.GetAllocatedBytesForCurrentThread();
            foreach (var g in geometry) _ = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(g.ToJSON(new global::Rhino.FileIO.SerializationOptions()))));
            var jsonMs = timer.Elapsed.TotalMilliseconds; var jsonAllocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
            timer.Restart(); allocated = GC.GetAllocatedBytesForCurrentThread();
            foreach (var g in geometry) _ = GeometryFingerprint.Compute(g);
            var binaryMs = timer.Elapsed.TotalMilliseconds; var binaryAllocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
            var cost = new SceneCost(memory, faces, triangles, geometry.Length);
            records.Add(new { file, fixtureSha256 = FileHash(file), objects = geometry.Length, memory, faces, triangles, jsonMs, binaryMs, jsonAllocated, binaryAllocated, estimate1 = cost.Estimate(1), estimate6 = cost.Estimate(6), admitted6 = cost.Admitted(6, NativeScheduler.Global.BudgetBytes) });
            Console.WriteLine(JsonSerializer.Serialize(records[^1]));
        }
        File.WriteAllText(Path.Combine(output, "fingerprints.json"), JsonSerializer.Serialize(new { rhino = RhinoApp.Version.ToString(), runtime = Environment.Version.ToString(), engineSha256 = FileHash(typeof(NativeDrawing).Assembly.Location), records }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string FileHash(string file)
    { using var stream = File.OpenRead(file); return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)); }

    internal static string Render(ViewDrawing view, string png)
    {
        var frame = view.Frame.IsValid ? view.Frame : new Rectangle3d(Plane.WorldXY, new Interval(-1, 1), new Interval(-1, 1));
        var width = Math.Max(frame.Width, 1e-6); var height = Math.Max(frame.Height, 1e-6);
        var scale = Math.Min(744 / width, 504 / height);
        PointF Map(Point3d p) { frame.Plane.ClosestParameter(p, out var x, out var y); return new((float)(28 + (x - frame.X.Min) * scale), (float)(28 + (frame.Y.Max - y) * scale)); }
        using var bitmap = new Bitmap(800, 560); using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.White); graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var svg = new StringBuilder("<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 800 560' fill='none' stroke-width='1.1'>");
        foreach (var entry in view.Regions)
        {
            var region = entry.Region;
            using var path = new GraphicsPath(FillMode.Alternate); var commands = new StringBuilder();
            foreach (var loop in region.Loops)
            {
                using var polyline = loop.Curve.ToPolyline(Math.Max(width, height) / 3000, .03, 0, Math.Max(width, height) / 80);
                if (polyline is null || !polyline.TryGetPolyline(out var points) || points.Count < 2) continue;
                var mapped = points.Select(Map).ToArray();
                if (loop.Curve.IsClosed && mapped.Length >= 3) path.AddPolygon(mapped); else path.AddLines(mapped);
                commands.Append("M" + string.Join("L", mapped.Select(p => $"{p.X:F3},{p.Y:F3}")) + (loop.Curve.IsClosed ? "Z" : ""));
            }
            string Colour(uint argb) => $"#{argb & 0xffffff:x6}";
            void Fill(uint argb)
            {
                using var brush = new SolidBrush(Color.FromArgb(unchecked((int)argb))); graphics.FillPath(brush, path);
                svg.Append($"<path d='{commands}' fill='{Colour(argb)}' fill-rule='evenodd' stroke='none'/>");
            }
            if (region.Hatch is not null)
            {
                if (region.Style.Background != BackgroundMode.None) Fill(region.Style.BackgroundColour);
                if (region.Style.Pattern is { Solid: true }) Fill(region.Style.PatternColour);
                else if (region.Style.Pattern is { } pattern)
                {
                    var id = "h" + svg.Length;
                    svg.Append($"<defs><clipPath id='{id}'><path d='{commands}' clip-rule='evenodd'/></clipPath></defs><g clip-path='url(#{id})'>");
                    var state = graphics.Save(); graphics.SetClip(path);
                    using var pen = new Pen(Color.FromArgb(unchecked((int)region.Style.PatternColour)), .8f);
                    foreach (var line in pattern.Lines)
                    {
                        var angle = region.Style.Rotation + line.Angle; var dx = Math.Cos(angle); var dy = -Math.Sin(angle);
                        var spacing = Math.Max(2, Math.Abs(-line.Offset.X * Math.Sin(line.Angle) + line.Offset.Y * Math.Cos(line.Angle)) * region.Style.Scale * scale);
                        if (line.Dashes.Count != 0) pen.DashStyle = DashStyle.Dash;
                        for (var offset = -1100d; offset <= 1100; offset += spacing)
                        {
                            var x = 400 - dy * offset; var y = 280 + dx * offset;
                            var a = new PointF((float)(x - dx * 1100), (float)(y - dy * 1100)); var b = new PointF((float)(x + dx * 1100), (float)(y + dy * 1100));
                            graphics.DrawLine(pen, a, b); svg.Append($"<path stroke='{Colour(region.Style.PatternColour)}' d='M{a.X:F3},{a.Y:F3}L{b.X:F3},{b.Y:F3}'/>");
                        }
                    }
                    graphics.Restore(state); svg.Append("</g>");
                }
            }
            if (region.Style.Boundary.Visible)
            {
                using var pen = new Pen(Color.FromArgb(unchecked((int)region.Style.Boundary.Colour)), (float)(view.Weights[LineClass.Cut].PreviewPixels * region.Style.Boundary.WidthScale)); graphics.DrawPath(pen, path);
                svg.Append($"<path d='{commands}' stroke='{Colour(region.Style.Boundary.Colour)}' stroke-width='{view.Weights[LineClass.Cut].PreviewPixels * region.Style.Boundary.WidthScale:F2}'/>");
            }
        }
        // Hidden first; solid visible strokes remain legible at coincident boundaries.
        foreach (var item in view.Items.OrderBy(i => i.Class == LineClass.Hidden ? 0 : 1))
        {
            using var geometry = item.CopyGeometry();
            var kind = item.Profile && item.Class == LineClass.Visible ? LineClass.Profile : item.Class;
            var weight = view.Weights[kind];
            var intensity = item.Intensity * weight.InkIntensity;
            var baseColour = kind == LineClass.Unresolved ? Color.FromArgb(224, 122, 0) : Color.Black;
            var colour = kind == LineClass.Unresolved ? "#E07A00" : "currentColor";
            var css = kind == LineClass.Unresolved ? "unresolved" : "ink";
            using var pen = new Pen(DrawingBake.Blend(baseColour, Color.White, intensity), weight.PreviewPixels);
            if (item.Class == LineClass.Hidden) pen.DashStyle = DashStyle.Dash;
            if (geometry is Curve curve)
            {
                using var polyline = curve.ToPolyline(Math.Max(width, height) / 3000, .03, 0, Math.Max(width, height) / 80);
                if (polyline is null || !polyline.TryGetPolyline(out var points) || points.Count < 2) continue;
                var mapped = points.Select(Map).ToArray(); graphics.DrawLines(pen, mapped);
                svg.Append($"<polyline class='{css}' stroke='{colour}' stroke-opacity='{intensity:F4}' stroke-width='{weight.PreviewPixels}'{(item.Class == LineClass.Hidden ? " stroke-dasharray='5 4'" : "")} points='{string.Join(" ", mapped.Select(p => $"{p.X:F3},{p.Y:F3}"))}'/>");
            }
            else if (geometry is Point point)
            {
                var p = Map(point.Location); using var brush = new SolidBrush(pen.Color); graphics.FillEllipse(brush, p.X - 2, p.Y - 2, 4, 4);
                svg.Append($"<circle class='point' cx='{p.X:F3}' cy='{p.Y:F3}' r='2' fill='{colour}'/>");
            }
        }
        using var framePen = new Pen(ColorTranslator.FromHtml("#1F6FD1"), .7f);
        graphics.DrawRectangle(framePen, 28, 28, (float)(width * scale), (float)(height * scale));
        svg.Append($"<rect x='28' y='28' width='{width * scale:F3}' height='{height * scale:F3}' stroke='#1F6FD1' stroke-width='.7'/></svg>");
        bitmap.Save(png, System.Drawing.Imaging.ImageFormat.Png); return svg.ToString();
    }
}

// A future raw-HLD or alternate-build adapter implements the same owned-result boundary.
internal interface IRenderEngine
{
    string Name { get; }
    DrawingBatch Draw(LeaseEntry<PreparedScene>.Lease scene, IReadOnlyList<ViewRequest> views);
}
internal sealed class M2DEngine : IRenderEngine
{
    public string Name => "M2D+ capture/NativeDrawing";
    public DrawingBatch Draw(LeaseEntry<PreparedScene>.Lease scene, IReadOnlyList<ViewRequest> views) =>
        NativeDrawing.SolveAsync(scene, new RequestIdentity(scene.Value.Revision, "render", "default", 1), views, new(), CancellationToken.None).GetAwaiter().GetResult();
}

internal sealed class M3Engine : IRenderEngine
{
    public string Name => "M2D+ planar kernel / curved native; M3 presentation (integration gate pending)";
    public DrawingBatch Draw(LeaseEntry<PreparedScene>.Lease scene, IReadOnlyList<ViewRequest> views)
    {
        var bounds = scene.Value.Bounds;
        var configured = views.Select((v, i) =>
        {
            var depth = Math.Max(.001, -bounds.GetCorners().Min(v.Cut!.Plane.DistanceTo));
            if (v.Name == "Range-control") depth = 2;
            var context = new ViewSettingsContext($"render/{i}", v.Name, v.Kind, true, depth, scene.Value.Tolerance);
            var settings = Settings.Resolve(new[] { context }, new[] { new[] { $"depth={(v.Name == "Range-control" ? 2 : i == 0 ? depth + scene.Value.Tolerance * 10 : depth * .7):R}", $"beyond={depth * .3:R}",
                "farclip=line", "fade=on", "fade.start=10", "fade.end=100", "fade.limit=30", "scale=1:100" } }, DocumentUnits.Metres).Views[0];
            return v with { Settings = settings };
        }).ToArray();
        using var cache = new DrawingCache();
        return cache.SolveAsync(scene, new(scene.Value.Revision, "render", "m3", 1), configured, new(), Array.Empty<Plane>(), default).GetAwaiter().GetResult();
    }
}
