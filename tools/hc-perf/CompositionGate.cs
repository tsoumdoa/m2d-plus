using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using M2DPlus.Core;
using M2DPlus.Rhino.Services;
using Rhino;
using Rhino.Geometry;
using Point = Rhino.Geometry.Point;

namespace M2DPlus.HcPerf;

/// <summary>Whole production-output gate. Native calls are correctness-only, serial, without a timing mutex.</summary>
public static class CompositionGate
{
    private sealed record SavedPiece(string Source, LineClass Class, string Geometry, string? Depth, bool KernelOwned = false);
    private sealed record SavedView(string Name, string? Error, string Info, double Seconds, int KernelEdges, int NativeEdges, int NativeCalls, int CurvedCandidateEdges, int PlanarNativeEdges, int NativeFragments,
        SavedPiece[] Pieces, double[] RegionAreas, int[] RegionLoops);
    private sealed record Failure(string Direction, string Source, string Expected, string? Actual, double X, double Y, double Z, double? Gap, bool Point, bool KernelOwned);
    private sealed class Curves : IDisposable
    {
        public List<(SavedPiece Piece, GeometryBase Geometry)> Items = [];
        public RTree Tree = new();
        private readonly List<(Point3d A, Point3d B, double Square)?> _lines = [];
        public Curves(SavedView view)
        {
            foreach (var p in view.Pieces)
            {
                var g = (GeometryBase)GeometryBase.FromJSON(p.Geometry); var b = g.GetBoundingBox(true);
                Tree.Insert(b, Items.Count); Items.Add((p, g));
                bool linear = g is LineCurve;
                if (!linear && g is Curve curve && curve.Degree == 1 && curve.SpanCount == 1)
                {
                    using var n = curve.ToNurbsCurve();
                    linear = n.Points.Count == 2 && n.Points[0].Weight > 0 && n.Points[1].Weight > 0;
                }
                if (linear && g is Curve line)
                {
                    var a=line.PointAtStart; var z=line.PointAtEnd; double square=(z-a).SquareLength;
                    _lines.Add(double.IsFinite(square) && square > 0 ? (a,z,square) : null);
                }
                else _lines.Add(null);
            }
        }
        public (LineClass? Class, double? Gap, SavedPiece? Piece, bool KernelOwned) At(Point3d p, double tolerance, bool point, LineClass? category = null)
        {
            var candidates = new List<int>(); Tree.Search(new Sphere(p, tolerance), (_, a) => candidates.Add(a.Id));
            LineClass? best = null; SavedPiece? bestPiece = null; bool kernelOwned = false; double distance = double.PositiveInfinity;
            int Rank(LineClass c) => c == LineClass.Unresolved ? 0 : c == LineClass.Visible ? 1 : c == LineClass.Hidden ? 2 : 3 + (int)c;
            foreach (int i in candidates)
            {
                var (piece, geometry) = Items[i]; double d;
                if (category is { } kind ? piece.Class != kind : piece.Class is LineClass.Cut or LineClass.FarClip) continue;
                if (point && geometry is Point pt) d = pt.Location.DistanceTo(p);
                else if (!point && _lines[i] is { } line)
                {
                    var delta=line.B-line.A; double t=Math.Clamp((p-line.A)*delta/line.Square,0,1);
                    d=(line.A+delta*t).DistanceTo(p);
                }
                else if (!point && geometry is Curve curve && curve.ClosestPoint(p, out var t, tolerance)) d = curve.PointAt(t).DistanceTo(p);
                else continue;
                distance = Math.Min(distance, d);
                if (d <= tolerance && (best is null || Rank(piece.Class) < Rank(best.Value)))
                { best = piece.Class; bestPiece = piece; kernelOwned = piece.KernelOwned; }
                else if (d <= tolerance && best == piece.Class) kernelOwned |= piece.KernelOwned;
            }
            return (best, double.IsFinite(distance) ? distance : null, bestPiece, kernelOwned);
        }
        public void Dispose() { foreach (var p in Items) p.Geometry.Dispose(); Tree.Dispose(); }
    }
    public static void Run(string path, string viewName, string prefix)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(prefix))!);
        using var doc = RhinoDoc.OpenHeadless(path); using var cache = new SceneCache();
        using var watch = new DocumentWatch(doc); using var capture = new SceneCapture(doc, watch, cache: cache);
        var timer = Stopwatch.StartNew(); while (!capture.Step(TimeSpan.FromMinutes(5))) { }
        using var lease = capture.Accept(); double captureSeconds = timer.Elapsed.TotalSeconds;
        var bounds = lease.Value.Bounds;
        timer.Restart(); lease.Value.Prepare(default); double prepareSeconds = timer.Elapsed.TotalSeconds;
        ViewRequest view = viewName switch
        {
            "North" => ViewRequest.Parse(new Vector3d(0, -1, 0)),
            "Long-section" => ViewRequest.Parse(new Plane(bounds.Center, Vector3d.XAxis, Vector3d.ZAxis)),
            "Plan-section" => ViewRequest.Parse(Plane.WorldXY),
            _ => ViewRequest.Parse(viewName)
        };
        if (viewName == "Long-section" && File.Exists(Path.ChangeExtension(path, ".json")))
        {
            using var sidecar = JsonDocument.Parse(File.ReadAllText(Path.ChangeExtension(path, ".json")));
            if (sidecar.RootElement.TryGetProperty("views", out var definitions))
                foreach (var definition in definitions.EnumerateArray())
                {
                    if (!definition.GetProperty("name").GetString()!.Contains("long", StringComparison.OrdinalIgnoreCase)) continue;
                    var named = ViewRequest.Parse(definition.GetProperty("name").GetString(), doc);
                    if (named.Error is null && named.Cut is not null) { view = named; break; }
                    var frame = definition.GetProperty("plane");
                    var o = frame.GetProperty("origin").Deserialize<double[]>()!;
                    var x = frame.GetProperty("x").Deserialize<double[]>()!; var y = frame.GetProperty("y").Deserialize<double[]>()!;
                    view = ViewRequest.Parse(new Plane(new Point3d(o[0],o[1],o[2]), new Vector3d(x[0],x[1],x[2]), new Vector3d(y[0],y[1],y[2]))); break;
                }
        }
        view = view with { Name = viewName };
        var context = new ViewSettingsContext("gate", viewName, view.Kind, view.Cut is not null, bounds.Diagonal.Length * 2, doc.ModelAbsoluteTolerance);
        var settings = Settings.Resolve([context], [["hidden=on", "beyond=off"]], DocumentUnits.Metres);
        view = view with { Settings = settings.Views[0] };
        string Hash(string file) { using var stream = File.OpenRead(file); return Convert.ToHexString(SHA256.HashData(stream)); }
        File.WriteAllText(prefix + ".sources.json", JsonSerializer.Serialize(lease.Value.Prepare(default).Select(s => new {
            s.Identity, type = s.Geometry.GetType().Name, s.Section.LayerPath, bounds = s.Geometry.GetBoundingBox(true),
            name = Guid.TryParse(s.Identity, out var id) ? doc.Objects.FindId(id)?.Attributes.Name : null })));
        var identity = new RequestIdentity(lease.Value.Revision, view.Revision, "composition-gate", 1);
        SavedView Save(ViewDrawing drawing) => new(drawing.Name, drawing.Error, drawing.Info, drawing.Seconds, drawing.KernelEdges, drawing.NativeEdges, drawing.NativeCalls, drawing.CurvedCandidateEdges, drawing.PlanarNativeEdges, drawing.NativeFragments,
            drawing.Items.Select(p => { using var g = p.CopyGeometry(); return new SavedPiece(p.SourceIdentity, p.Class, g.ToJSON(new()), p.DepthGeometry?.ToJSON(new()), p.KernelOwned); })
                .Concat(drawing.Regions.SelectMany(r => r.Region.Loops.Select(l => new SavedPiece(string.Join(";",r.Region.SourceIds), LineClass.Cut, l.Curve.ToJSON(new()), null)))).ToArray(),
            drawing.Regions.Select(r => r.Region.Area).ToArray(), drawing.Regions.Select(r => r.Region.Loops.Count).ToArray());
        var scheduler = new NativeScheduler(16L * 1024 * 1024 * 1024);
        var nativeSourceIds = new HashSet<string>(StringComparer.Ordinal);
        var kernelSourceIds = new HashSet<string>(StringComparer.Ordinal);
        Action<IReadOnlyList<VisibilityInput>> Observe(string suffix, bool routed) => inputs =>
        {
            File.WriteAllText(prefix + suffix, JsonSerializer.Serialize(inputs.Select(i => new { source = i.Source.Identity, path = i.Source.Path,
                geometry = GeometryFingerprint.Compute(i.Geometry), i.KernelCandidate, i.Far })));
            if (routed) foreach (var i in inputs)
                if (i.KernelCandidate) kernelSourceIds.Add(i.Source.Identity);
                else if (i.Geometry is not Point) nativeSourceIds.Add(i.Source.Identity);
        };
        string cacheIdentity = Hash(path) + "|" + view.Revision + (view.Cut is null ? "" : "|canonical-pieces-v1");
        SavedView native;
        var nativeFile = prefix + ".native-output.json"; var hashFile = prefix + ".model.sha256";
        if (File.Exists(nativeFile) && !File.ReadAllText(nativeFile).Contains("no supported parallel") && File.Exists(hashFile) && File.ReadAllText(hashFile) == cacheIdentity)
            native = JsonSerializer.Deserialize<SavedView>(File.ReadAllText(nativeFile))!;
        else
        {
            using var baseline = NativeDrawing.SolveBorrowedAsync(lease.Value, identity, [view], new NativeSettings(NativeOnly: true) { Scheduler = scheduler, ObserveNativeInputs = Observe(".native-order.json", false) }, default, null).GetAwaiter().GetResult();
            using var presented = DrawingPresentation.ApplySnapshot(baseline, identity, [view], [], lease.Value.SourceMetadata);
            native = Save(presented.Views[0]); File.WriteAllText(nativeFile, JsonSerializer.Serialize(native)); File.WriteAllText(hashFile, cacheIdentity);
        }
        if (view.Cut is null && !File.Exists(prefix + ".native-order.json"))
            Observe(".native-order.json", false)(VisibilityInputOrder.Canonical(lease.Value.Prepare(default).Select(s => new VisibilityInput(s.Geometry,s)), i=>i));
        timer.Restart();
        using var batch = NativeDrawing.SolveBorrowedAsync(lease.Value, identity, [view], new NativeSettings { Scheduler = scheduler, ObserveNativeInputs = Observe(".routed-order.json", true), ObserveKernelSources = ids => kernelSourceIds.UnionWith(ids) }, default, null).GetAwaiter().GetResult();
        using var placed = DrawingPresentation.ApplySnapshot(batch, identity, [view], [], lease.Value.SourceMetadata);
        double solveSeconds = timer.Elapsed.TotalSeconds;
        var actual = Save(placed.Views[0]);
        File.WriteAllText(prefix + ".unresolved.json", JsonSerializer.Serialize(batch.Views[0].Items.Where(p => p.Class == LineClass.Unresolved).Select(p => {
            using var g = p.CopyGeometry(); return new { p.SourceIdentity, bounds = g.GetBoundingBox(true), length = g is Curve c ? c.GetLength() : 0 }; }))); File.WriteAllText(prefix + ".kernel-output.json", JsonSerializer.Serialize(actual));
        using var n = new Curves(native); using var k = new Curves(actual);
        double tolerance = doc.ModelAbsoluteTolerance, spacing = .05 * RhinoMath.UnitScale(UnitSystem.Meters, doc.ModelUnitSystem);
        var failures = new List<Failure>(); long samples = 0;
        void Compare(Curves source, Curves target, string direction)
        {
            foreach (var (piece, geometry) in source.Items)
            {
                IEnumerable<Point3d> Samples()
                {
                    if (geometry is Point point) yield return point.Location;
                    else if (geometry is Curve c)
                    {
                        int count = Math.Max(8, (int)Math.Ceiling(c.GetLength() / spacing));
                        var parameters = c.DivideByCount(count, true);
                        if (parameters is null) { yield return c.PointAtStart; yield return c.PointAtEnd; }
                        else foreach (var t in parameters) yield return c.PointAt(t);
                    }
                }
                foreach (var p in Samples())
                {
                    samples++; bool point = geometry is Point;
                    var category = piece.Class is LineClass.Cut or LineClass.FarClip ? (LineClass?)piece.Class : null;
                    var own = source.At(p, tolerance, point, category);
                    var expected = own.Class;
                    var found = target.At(p, tolerance, point, category);
                    if (expected == found.Class) continue;
                    failures.Add(new(direction, own.Piece?.Source ?? piece.Source, expected?.ToString() ?? "missing", found.Class?.ToString(), p.X, p.Y, p.Z, found.Gap, point, direction == "kernel-to-native" ? own.KernelOwned : found.KernelOwned));
                }
            }
        }
        Compare(n, k, "native-to-kernel"); Compare(k, n, "kernel-to-native");
        // One deterministic full-scene order control. It retains every original
        // occluder: unlike dropping planar faces, a class change cannot be an
        // expected loss of planar occlusion. Kernel-owned loci are never excused.
        bool Eligible(Failure f) => !f.KernelOwned && nativeSourceIds.Contains(f.Source) && !kernelSourceIds.Contains(f.Source) &&
            (f.Expected is "Visible" or "Hidden" or "Unresolved") && (f.Actual is null or "Visible" or "Hidden" or "Unresolved");
        var orderWitnesses = new List<object>(); var classified = new HashSet<int>();
        if (failures.Count != 0 && failures.All(Eligible))
        {
            var reverseFile = prefix + ".reversed-native-output.json";
            SavedView reversed;
            var reverseKey = prefix + ".reversed.sha256";
            if (File.Exists(reverseFile) && File.Exists(reverseKey) && File.ReadAllText(reverseKey) == cacheIdentity)
                reversed = JsonSerializer.Deserialize<SavedView>(File.ReadAllText(reverseFile))!;
            else
            {
                using var control = NativeDrawing.SolveBorrowedAsync(lease.Value, identity, [view],
                    new NativeSettings(NativeOnly: true) { Scheduler = scheduler, ReverseNativeForAudit = true,
                        ObserveNativeInputs = Observe(".reversed-order.json", false) }, default, null).GetAwaiter().GetResult();
                using var presentation = DrawingPresentation.ApplySnapshot(control, identity, [view], [], lease.Value.SourceMetadata);
                reversed = Save(presentation.Views[0]); File.WriteAllText(reverseFile, JsonSerializer.Serialize(reversed)); File.WriteAllText(reverseKey, cacheIdentity);
            }
            using var controlCurves = new Curves(reversed);
            string[] Keys(string file) => JsonSerializer.Deserialize<JsonElement[]>(File.ReadAllText(file))!
                .Select(e => e.GetProperty("source").GetString() + "/" + e.GetProperty("path").GetRawText() + "/" + e.GetProperty("geometry").GetString() + "/" + e.GetProperty("Far").ToString()).ToArray();
            bool sameInputs = File.Exists(prefix + ".native-order.json") && File.Exists(prefix + ".reversed-order.json") &&
                Keys(prefix + ".native-order.json").SequenceEqual(Keys(prefix + ".reversed-order.json").AsEnumerable().Reverse());
            File.WriteAllText(prefix + ".order-control.json", JsonSerializer.Serialize(new { sameInputs, orderPolicy=VisibilityInputOrder.Policy,
                baseline=Hash(nativeFile), reversed=Hash(reverseFile), rhino=RhinoApp.Version.ToString(), rhinoCommonSha256=Hash(typeof(RhinoApp).Assembly.Location) }));
            if (sameInputs && reversed.Error is null)
                for (int i=0;i<failures.Count;i++)
                {
                    var f=failures[i]; if(!Eligible(f))continue;
                    var p=new Point3d(f.X,f.Y,f.Z);
                    var canonical=n.At(p,tolerance,f.Point).Class?.ToString();
                    var reverse=controlCurves.At(p,tolerance,f.Point).Class?.ToString();
                    var routed=k.At(p,tolerance,f.Point);
                    if(routed.KernelOwned || canonical==reverse || routed.Class?.ToString()!=reverse)continue;
                    classified.Add(i);
                    orderWitnesses.Add(new { sample=i, classification="native-order-quirk", f.Source, f.X,f.Y,f.Z,
                        canonical, reversed=reverse, routed=reverse, kernelOwned=false, control="same full geometry, same view/tolerance/hidden-on, reverse canonical registration order",
                        outputSha256=Hash(reverseFile) });
                }
        }
        File.WriteAllText(prefix + ".order-witnesses.json", JsonSerializer.Serialize(orderWitnesses));
        var options = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(prefix + ".failures.json", JsonSerializer.Serialize(failures));
        var record = new { model = path, modelSha256 = Hash(path), view = viewName, engineSha256 = Hash(typeof(NativeDrawing).Assembly.Location), coreSha256 = Hash(typeof(M2DPlus.Core.Visibility.EdgeFaceKernel).Assembly.Location),
            rhino = RhinoApp.Version.ToString(), runtime = System.Environment.Version.ToString(), tolerance, spacing, multipleThreads = false, harnessSha256 = Hash(typeof(CompositionGate).Assembly.Location), captureBudgetSeconds = 300, comparison = "exact line-support distance for linear loci; native exact-curve closest point otherwise",
            nativePurpose = "correctness only; no timing mutex; native seconds diagnostic only, not a benchmark", captureSeconds, prepareSeconds, solveSeconds,
            endToEndSeconds = captureSeconds + prepareSeconds + solveSeconds, includesPresentation = true, actual.KernelEdges, actual.NativeEdges, actual.NativeCalls, actual.CurvedCandidateEdges, actual.PlanarNativeEdges, actual.NativeFragments, nativeCountKind = "drawable native parent curves", nativeInfo = native.Info, kernelInfo = actual.Info,
            samples, disagreements = failures.Count, unexplained = failures.Count - classified.Count, classes = new { nativeOrderQuirk = classified.Count, tieConvention = 0, toleranceArtefact = 0, unclassified = failures.Count - classified.Count }, bugs = (int?)null, nativeOrderPolicy = VisibilityInputOrder.Policy,
            passed = native.Error is null && actual.Error is null && failures.Count == classified.Count && native.RegionAreas.SequenceEqual(actual.RegionAreas) && native.RegionLoops.SequenceEqual(actual.RegionLoops),
            nativeError = native.Error, kernelError = actual.Error, native.RegionAreas, actualRegionAreas = actual.RegionAreas };
        File.WriteAllText(prefix + ".gate.json", JsonSerializer.Serialize(record, options));
        Console.WriteLine($"{Path.GetFileName(path)} {viewName}: {solveSeconds:F3}s; kernel={actual.KernelEdges} native={actual.NativeEdges}; disagreements={failures.Count}; error={actual.Error}");
    }
}
