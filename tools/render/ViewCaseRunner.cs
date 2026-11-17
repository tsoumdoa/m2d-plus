// MIT. Manual one-view bridge hosted by tools/models/run.py's owned Rhino worker.
using System.Diagnostics;
using System.Text.Json;
using M2DPlus.Core;
using M2DPlus.Rhino.Services;
using M2DPlus.Rhino.Views;
using Rhino;
using Rhino.FileIO;
using Rhino.Geometry;

namespace M2DPlus.Render;

public static class ViewCaseRunner
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static double[] Numbers(JsonElement value) => value.EnumerateArray().Select(v => v.GetDouble()).ToArray();
    private static Plane PlaneOf(JsonElement p)
    {
        var o = Numbers(p.GetProperty("origin")); var x = Numbers(p.GetProperty("x")); var y = Numbers(p.GetProperty("y"));
        return new(new Point3d(o[0], o[1], o[2]), new Vector3d(x[0], x[1], x[2]), new Vector3d(y[0], y[1], y[2]));
    }
    public static string Run(string modelPath, string descriptor, string output)
    {
        using var json = JsonDocument.Parse(descriptor); var v = json.RootElement;
        using var document = RhinoDoc.OpenHeadless(modelPath) ?? throw new IOException(modelPath);
        var plane = PlaneOf(v.GetProperty("plane")); var crop = Numbers(v.GetProperty("crop")); var depth = v.GetProperty("depth").GetDouble();
        var rect = new Rectangle3d(plane, new Interval(crop[0], crop[1]), new Interval(crop[2], crop[3]));
        var id = Guid.Parse(v.GetProperty("viewBox").GetProperty("objectId").GetString()!);
        var boxRequest = ViewRequest.Parse(id, document);
        var ghBox = ViewRequest.Parse(new Box(plane, rect.X, rect.Y, new Interval(-depth, 0)));
        var ghRectangle = ViewRequest.Parse(rect);
        var ghPlane = ViewRequest.Parse(plane);
        var checks = new Dictionary<string, bool>();
        foreach (var (label, request) in new[] { ("plane", ghPlane), ("rectangle", ghRectangle), ("box", ghBox), ("viewBox", boxRequest) })
        {
            var cut = request.Cut;
            checks[label] = request.Error is null && cut is not null &&
                Math.Abs(plane.DistanceTo(cut.Plane.Origin)) < document.ModelAbsoluteTolerance &&
                cut.Plane.ZAxis * plane.ZAxis > 1 - 1e-10 && cut.Plane.YAxis * plane.YAxis > 1 - 1e-10 &&
                (label is "plane" or "rectangle" || Math.Abs(cut.SourceDepth!.Value - depth) < document.ModelAbsoluteTolerance) &&
                (label == "plane" || cut.Crop is { } r && r.BoundingBox.Min.DistanceTo(rect.BoundingBox.Min) < document.ModelAbsoluteTolerance &&
                    r.BoundingBox.Max.DistanceTo(rect.BoundingBox.Max) < document.ModelAbsoluteTolerance);
        }
        if (checks.Values.Any(ok => !ok)) throw new InvalidDataException("View value / persisted extrusion disagreement: " + JsonSerializer.Serialize(checks));
        using var cache = new SceneCache(); using var watch = new DocumentWatch(document);
        // Names are stable source identities for oracle attribution; view objects are explicitly excluded.
        var inputs = document.Objects.GetObjectList(global::Rhino.DocObjects.ObjectType.AnyObject)
            .Where(o => !o.IsInstanceDefinitionGeometry && o.Geometry is not ClippingPlaneSurface && !ViewBoxes.IsViewBox(o))
            .Select((o, i) => new CaptureInput(o.Id, [0], o.Name) { SourceItem = i }).ToArray();
        var timer = Stopwatch.StartNew();
        var phaseClock = Stopwatch.StartNew(); var phaseLock = new object(); var phase = "m2d/capture";
        var beforeHld = NativeDrawing.HldCalls; double? firstHldObserved = null;
        void Checkpoint()
        {
            lock (phaseLock)
            {
                if (NativeDrawing.HldCalls > beforeHld) firstHldObserved ??= phaseClock.Elapsed.TotalSeconds;
                var reached = phase == "m2d/pre-native" && firstHldObserved is not null ? "m2d/native-or-extraction" : phase;
                var target = output + ".engine-phase.json";
                File.WriteAllText(target + ".tmp", JsonSerializer.Serialize(new { phase = reached,
                    elapsedSeconds = phaseClock.Elapsed.TotalSeconds, nativeHldCalls = NativeDrawing.HldCalls - beforeHld,
                    firstHldObservedSeconds = firstHldObserved, samplePeriodSeconds = 1 }));
                File.Move(target + ".tmp", target, true);
            }
        }
        // Samples only an atomic managed counter, never a live Rhino document/geometry from this timer thread.
        using var phaseTimer = new System.Threading.Timer(_ =>
        {
            try { Checkpoint(); }
            catch (IOException) { /* A concurrent evidence reader must not interrupt native computation. */ }
        }, null, 0, 1000);
        using var capture = new SceneCapture(document, watch, inputs, cache);
        while (!capture.Step(TimeSpan.FromMilliseconds(12))) { }
        var lease = capture.Accept(); double captureSeconds = timer.Elapsed.TotalSeconds;
        lock (phaseLock) phase = "m2d/pre-native";
        Checkpoint();
        var requestView = boxRequest with { Name = v.GetProperty("name").GetString()! };
        using var batch = NativeDrawing.SolveAsync(lease, new(lease.Value.Revision, "view-cases", "main-route-C", 1),
            [requestView], new(), default).GetAwaiter().GetResult();
        var drawing = batch.Views.Single();
        lock (phaseLock) phase = "m2d/result-extraction";
        Checkpoint();
        var loci = new List<object>();
        var extractionFailures = new List<object>();
        var tessellationFallbacks = new List<object>();
        object? Poly(Curve curve, string classification, string source)
        {
            // Preserve an exact existing polyline, including very short line segments.
            if (!curve.TryGetPolyline(out var points))
            {
                using var polyline = curve.ToPolyline(document.ModelAbsoluteTolerance, .03, 0, 0);
                if (polyline is not null) polyline.TryGetPolyline(out points);
            }
            if (points is null || points.Count < 2)
            {
                var fallback = ViewCurveTessellation.Curve(curve, document.ModelAbsoluteTolerance);
                if (!fallback.Success)
                {
                    extractionFailures.Add(new { classification, source, type = curve.GetType().Name,
                        valid = curve.IsValid, reason = fallback.Error });
                    return null;
                }
                points = new Polyline(fallback.Points.Select(p => new Point3d(p[0], p[1], p[2])));
                tessellationFallbacks.Add(new { classification, source, fallback.ErrorBound, fallback.PointOnly,
                    method = "positive-weight rational Bezier control-hull subdivision" });
            }
            return new { @class = classification, source, points = points.Select(p => { plane.ClosestParameter(p, out double x, out double y); return new[] { x, y }; }).ToArray() };
        }
        foreach (var piece in drawing.Items)
        {
            using var geo = piece.CopyGeometry();
            if (geo is Curve curve && Poly(curve, piece.Class.ToString(), piece.SourceIdentity) is { } item) loci.Add(item);
        }
        // Cut branches are separate from line output. Preserve their class as well as holes.
        foreach (var region in drawing.Regions)
            foreach (var loop in region.Region.Loops)
                if (Poly(loop.Curve, "Cut", string.Join(";", region.Region.SourceIds)) is { } item) loci.Add(item);
        File.WriteAllText(output + ".m2d-loci.json", JsonSerializer.Serialize(loci));
        var regions = drawing.Regions.Select(r => new { sources = r.Region.SourceIds, area = r.Region.Area,
            loops = r.Region.Loops.Count, holes = r.Region.Loops.Count(l => l.IsHole), status = r.Region.Status.ToString(), r.Region.Uncertainty }).ToArray();
        File.WriteAllText(output + ".svg", Program.Render(drawing, output + ".png"));
        lock (phaseLock) phase = "complete";
        Checkpoint();
        return JsonSerializer.Serialize(new { status = drawing.Error is not null ? "engine_error" : extractionFailures.Count > 0 ? "output_extraction_failure" : "complete", drawing.Error,
            engine = "main NativeDrawing route C / stability certificate", engineSha256 = Hash(typeof(NativeDrawing).Assembly.Location),
            coreSha256 = Hash(typeof(Settings).Assembly.Location), bridgeSha256 = Hash(typeof(ViewCaseRunner).Assembly.Location),
            seconds = drawing.Seconds, captureSeconds, firstHldObservedSeconds = firstHldObserved, viewSourceChecks = checks, regions, extractionFailures, tessellationFallbacks,
            counts = drawing.Items.GroupBy(p => p.Class).ToDictionary(g => g.Key.ToString(), g => g.Count()),
            unresolved = drawing.Items.Count(p => p.Class == LineClass.Unresolved), drawing.Report, drawing.Info,
            drawing.DuplicateSegments, drawing.EmptyNativeSlots, drawing.ProjectingSegments,
            priority = Process.GetCurrentProcess().PriorityClass.ToString() }, Json);
    }
    private static string Hash(string path) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));

    // Comparison is class-aware, bidirectional and sampled, with explicitly retained failed loci.
    public static string Compare(string expectedPath, string actualPath, double tolerance, double spacing)
    {
        static List<ComparisonLocus> Read(string path)
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path)); var result = new List<ComparisonLocus>();
            foreach (var row in doc.RootElement.EnumerateArray())
            {
                var label = row.GetProperty("class").GetString();
                if (label is not ("Visible" or "Hidden" or "Unresolved" or "Cut")) continue;
                var points = row.GetProperty("points").EnumerateArray().Select(Numbers).ToArray();
                // Cut is compared independently by the caller; drawing ink uses visible precedence.
                var kind = label == "Cut" ? LineClass.Visible : Enum.Parse<LineClass>(label!);
                for (int i = 1; i < points.Length; i++) result.Add(new(points[i-1][0], points[i-1][1], points[i][0], points[i][1], kind, row.GetProperty("source").GetString() ?? ""));
            }
            return result;
        }
        var expected = Read(expectedPath); var actual = Read(actualPath);
        var a = Misses(expected, actual, tolerance, spacing);
        var b = Misses(actual, expected, tolerance, spacing);
        return JsonSerializer.Serialize(new { passed = a + b == 0, tolerance, spacing,
            method = "bidirectional sampled classed polyline loci; Cut treated as visible ink; topology checked separately; not a Hausdorff certificate",
            expectedToActual = new { Missed = a }, actualToExpected = new { Missed = b } }, Json);
    }
    private sealed record ComparisonLocus(double X0,double Y0,double X1,double Y1,LineClass Class,string Source);
    private static int Misses(IReadOnlyList<ComparisonLocus> from,IReadOnlyList<ComparisonLocus> to,double tolerance,double spacing)
    {
        if(tolerance<=0||spacing<=0)throw new ArgumentOutOfRangeException(nameof(tolerance));
        int missed=0;
        foreach(var line in from)
        {
            var a=new Point3d(line.X0,line.Y0,0);var b=new Point3d(line.X1,line.Y1,0);
            int steps=Math.Max(1,(int)Math.Ceiling(a.DistanceTo(b)/spacing));
            for(int i=0;i<=steps;i++)
            {
                var point=a+(b-a)*(i/(double)steps);
                if(!to.Any(l=>l.Class==line.Class&&new Line(new Point3d(l.X0,l.Y0,0),new Point3d(l.X1,l.Y1,0)).ClosestPoint(point,true).DistanceTo(point)<=tolerance))missed++;
            }
        }
        return missed;
    }
}
