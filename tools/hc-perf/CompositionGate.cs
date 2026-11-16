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
public static partial class CompositionGate
{
    private sealed record SavedPiece(string Source, LineClass Class, string Geometry, string? Depth, bool KernelOwned = false);
    private sealed record SavedView(string Name, string? Error, string Info, double Seconds, int KernelEdges, int NativeEdges, int NativeCalls, int CurvedCandidateEdges, int PlanarNativeEdges, int NativeFragments,
        SavedPiece[] Pieces, double[] RegionAreas, int[] RegionLoops, IReadOnlyDictionary<string, double>? Phases = null);
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
        public (LineClass? Class, double? Gap, SavedPiece? Piece, bool KernelOwned, int Index) At(Point3d p, double tolerance, bool point, LineClass? category = null)
        {
            var candidates = new List<int>(); Tree.Search(new Sphere(p, tolerance), (_, a) => candidates.Add(a.Id));
            LineClass? best = null; SavedPiece? bestPiece = null; bool kernelOwned = false; double distance = double.PositiveInfinity;int bestIndex=-1;
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
                // Every coincident kernel contributor disqualifies a native-order
                // exception, including contributors below the winning class.
                if (d <= tolerance) kernelOwned |= piece.KernelOwned;
                if (d <= tolerance && (best is null || Rank(piece.Class) < Rank(best.Value)))
                { best = piece.Class; bestPiece = piece; bestIndex=i; }
            }
            return (best, double.IsFinite(distance) ? distance : null, bestPiece, kernelOwned,bestIndex);
        }
        public void Dispose() { foreach (var p in Items) p.Geometry.Dispose(); Tree.Dispose(); }
    }
    private sealed class NativeTrace : IDisposable
    {
        private readonly List<(string Source,int Edge,bool Duplicate,LineClass Class,Curve World)> _records = [];
        public IReadOnlyList<(string Source,int Edge,bool Duplicate,LineClass Class,Curve World)> Records=>_records;
        public void Add(string source,int edge,bool duplicate,LineClass kind,Curve world)
        {
            var copy = world.DuplicateCurve(); copy.EnsurePrivateCopy(); _records.Add((source,edge,duplicate,kind,copy));
        }
        public void Save(string path) => File.WriteAllText(path,JsonSerializer.Serialize(_records.Select(r => new {
            r.Source,r.Edge,r.Duplicate,r.Class,World=r.World.ToJSON(new()) })));
        public void Dispose() { foreach (var record in _records) record.World.Dispose(); }
    }
    private sealed class KernelContributors : IDisposable
    {
        private readonly List<(Point3d A,Vector3d Delta,double Square,int Edge)> _lines = [];
        private readonly RTree _tree = new();
        public KernelContributors(PlanarExtraction? scene, Plane plane)
        {
            if (scene is null) return;
            for(int ei=0;ei<scene.Edges.Count;ei++)
            {
                var edge=scene.Edges[ei];
                var a=plane.PointAt(edge.A.X,edge.A.Y);var b=plane.PointAt(edge.B.X,edge.B.Y);var delta=b-a;double square=delta.SquareLength;
                if (!double.IsFinite(square) || square==0) continue;
                _tree.Insert(new BoundingBox(new[] {a,b}),_lines.Count);_lines.Add((a,delta,square,ei));
            }
        }
        public bool At(Point3d p,double tolerance)
        {
            bool found=false;
            _tree.Search(new Sphere(p,tolerance),(_,a)=>
            {
                var line=_lines[a.Id];double t=Math.Clamp((p-line.A)*line.Delta/line.Square,0,1);
                found|=(line.A+line.Delta*t).DistanceTo(p)<=tolerance;
            });
            return found;
        }
        public List<(int Edge,double Parameter)> Candidates(Point3d p,double tolerance)
        {
            var found=new List<(int,double)>();
            _tree.Search(new Sphere(p,tolerance),(_,a)=>{var line=_lines[a.Id];double t=Math.Clamp((p-line.A)*line.Delta/line.Square,0,1);if((line.A+line.Delta*t).DistanceTo(p)<=tolerance)found.Add((line.Edge,t));});
            return found;
        }
        public void Dispose()=>_tree.Dispose();
    }
    private static void CheckKernelContributorVeto()
    {
        using var line = new LineCurve(Point3d.Origin,new Point3d(1,0,0));
        string geometry = line.ToJSON(new());
        var view = new SavedView("veto probe",null,"",0,1,1,1,1,0,1,
            [new("native",LineClass.Visible,geometry,null),new("kernel",LineClass.Hidden,geometry,null,true)],[],[]);
        using var curves = new Curves(view);
        var result = curves.At(new Point3d(.5,0,0),1e-6,false);
        if (result.Class != LineClass.Visible || !result.KernelOwned)
            throw new InvalidOperationException("Native-order audit lost a lower-ranked coincident kernel contributor");
        var scene = new PlanarExtraction(); scene.Edges.Add(new(new(1,0,0),new(0,0,0),"kernel"));
        using var contributors = new KernelContributors(scene,Plane.WorldXY);
        if (!contributors.At(new(.5,0,0),1e-6))
            throw new InvalidOperationException("Native-order audit lost a kernel contributor removed by locus reconciliation");
    }
    public static void Run(string path, string viewName, string prefix, bool freshNative = false, bool measurePublication = false, bool traceNative = false, bool joints = true)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(prefix))!);
        CheckKernelContributorVeto();
        CheckMixedReference();
        var publication = measurePublication ? new PublicationProbe() : null;
        using var doc = RhinoDoc.OpenHeadless(path); using var cache = new SceneCache();
        using var watch = new DocumentWatch(doc);
        var timer = Stopwatch.StartNew();
        using var capture = new SceneCapture(doc, watch, cache: cache);
        while (!capture.Step(TimeSpan.FromMinutes(5))) { }
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
        var settings = Settings.Resolve([context], [["hidden=on", "beyond=off", $"joints={(joints ? "on" : "off")}"]], DocumentUnits.Metres);
        view = view with { Settings = settings.Views[0] };
        string Hash(string file) { using var stream = File.OpenRead(file); return Convert.ToHexString(SHA256.HashData(stream)); }
        File.WriteAllText(prefix + ".sources.json", JsonSerializer.Serialize(lease.Value.Prepare(default).Select(s => new {
            s.Identity, type = s.Geometry.GetType().Name, s.Section.LayerPath, bounds = s.Geometry.GetBoundingBox(true),
            name = Guid.TryParse(s.Identity, out var id) ? doc.Objects.FindId(id)?.Attributes.Name : null })));
        var identity = new RequestIdentity(lease.Value.Revision, view.Revision, "composition-gate", 1);
        SavedView Save(ViewDrawing drawing) => new(drawing.Name, drawing.Error, drawing.Info, drawing.Seconds, drawing.KernelEdges, drawing.NativeEdges, drawing.NativeCalls, drawing.CurvedCandidateEdges, drawing.PlanarNativeEdges, drawing.NativeFragments,
            drawing.Items.Select(p => { using var g = p.CopyGeometry(); return new SavedPiece(p.SourceIdentity, p.Class, g.ToJSON(new()), p.DepthGeometry?.ToJSON(new()), p.KernelOwned); })
                .Concat(drawing.Regions.SelectMany(r => r.Region.Loops.Select(l => new SavedPiece(string.Join(";",r.Region.SourceIds), LineClass.Cut, l.Curve.ToJSON(new()), null)))).ToArray(),
            drawing.Regions.Select(r => r.Region.Area).ToArray(), drawing.Regions.Select(r => r.Region.Loops.Count).ToArray(), drawing.Phases.Seconds);
        var scheduler = new NativeScheduler(16L * 1024 * 1024 * 1024);
        var nativeSourceIds = new HashSet<string>(StringComparer.Ordinal);
        var kernelSourceIds = new HashSet<string>(StringComparer.Ordinal);
        using var nativeTrace = new NativeTrace();
        PlanarExtraction? observedScene = null; Plane observedFrame = Plane.Unset;
        var curvedBounds = new List<BoundingBox>();
        Action<IReadOnlyList<VisibilityInput>> Observe(string suffix, bool routed) => inputs =>
        {
            File.WriteAllText(prefix + suffix, JsonSerializer.Serialize(inputs.Select(i => new { source = i.Source.Identity, path = i.Source.Path,
                geometry = GeometryFingerprint.Compute(i.Geometry), i.KernelCandidate, i.Far })));
            if (routed) foreach (var i in inputs)
            {
                if (i.KernelCandidate) kernelSourceIds.Add(i.Source.Identity);
                else if (i.Geometry is not Point) nativeSourceIds.Add(i.Source.Identity);
                if(!i.KernelCandidate && observedFrame.IsValid && i.Geometry is Brep or Mesh or Surface or Extrusion)
                    curvedBounds.Add(i.Geometry.GetBoundingBox(Transform.PlaneToPlane(observedFrame,Plane.WorldXY)));
            }
        };
        string cacheIdentity = Hash(path) + "|" + view.Revision + (view.Cut is null ? "" : "|canonical-pieces-v1");
        SavedView native;
        var nativeFile = prefix + ".native-output.json"; var hashFile = prefix + ".model.sha256";
        double nativeSolveSeconds = 0;
        var nativeTimer = Stopwatch.StartNew();
        if (!freshNative && File.Exists(nativeFile) && !File.ReadAllText(nativeFile).Contains("no supported parallel") && File.Exists(hashFile) && File.ReadAllText(hashFile) == cacheIdentity)
            native = JsonSerializer.Deserialize<SavedView>(File.ReadAllText(nativeFile))!;
        else
        {
            using var baseline = NativeDrawing.SolveBorrowedAsync(lease.Value, identity, [view], new NativeSettings(NativeOnly: true) { Scheduler = scheduler, ObserveNativeInputs = Observe(".native-order.json", false) }, default, null).GetAwaiter().GetResult();
            using var presented = DrawingPresentation.ApplySnapshot(baseline, identity, [view], [], lease.Value.SourceMetadata);
            publication?.Publish(presented,capture.Groups,capture.Colours);
            nativeSolveSeconds = nativeTimer.Elapsed.TotalSeconds;
            File.WriteAllText(prefix + ".native-raw-output.json", JsonSerializer.Serialize(Save(baseline.Views[0])));
            native = Save(presented.Views[0]); File.WriteAllText(nativeFile, JsonSerializer.Serialize(native)); File.WriteAllText(hashFile, cacheIdentity);
        }
        if (view.Cut is null && !File.Exists(prefix + ".native-order.json"))
            Observe(".native-order.json", false)(VisibilityInputOrder.Canonical(lease.Value.Prepare(default).Select(s => new VisibilityInput(s.Geometry,s)), i=>i));
        timer.Restart();
        using var batch = NativeDrawing.SolveBorrowedAsync(lease.Value, identity, [view], new NativeSettings { Scheduler = scheduler, ObserveNativeInputs = Observe(".routed-order.json", true), ObserveKernelSources = ids => kernelSourceIds.UnionWith(ids),
            ObservePlanarScene = (extraction, plane) => { observedScene = extraction; observedFrame = plane; },
            ObserveCompositionRecord = (source,edge,duplicate,kind,world) => nativeTrace.Add(source,edge,duplicate,kind,world) }, default, null).GetAwaiter().GetResult();
        using var placed = DrawingPresentation.ApplySnapshot(batch, identity, [view], [], lease.Value.SourceMetadata);
        publication?.Publish(placed,capture.Groups,capture.Colours);
        double solveSeconds = timer.Elapsed.TotalSeconds;
        var actual = Save(placed.Views[0]);
        if (traceNative) nativeTrace.Save(prefix+".foreign-records.json");
        File.WriteAllText(prefix + ".routed-raw-output.json", JsonSerializer.Serialize(Save(batch.Views[0])));
        if (observedScene is { } extraction) File.WriteAllText(prefix + ".planar-scene.json", JsonSerializer.Serialize(new {
            extraction.Faces, extraction.Edges, origin = new[] {observedFrame.OriginX,observedFrame.OriginY,observedFrame.OriginZ},
            x = new[] {observedFrame.XAxis.X,observedFrame.XAxis.Y,observedFrame.XAxis.Z}, y = new[] {observedFrame.YAxis.X,observedFrame.YAxis.Y,observedFrame.YAxis.Z},
            z = new[] {observedFrame.ZAxis.X,observedFrame.ZAxis.Y,observedFrame.ZAxis.Z} }));
        File.WriteAllText(prefix + ".unresolved.json", JsonSerializer.Serialize(batch.Views[0].Items.Where(p => p.Class == LineClass.Unresolved).Select(p => {
            using var g = p.CopyGeometry(); return new { p.SourceIdentity, bounds = g.GetBoundingBox(true), length = g is Curve c ? c.GetLength() : 0 }; }))); File.WriteAllText(prefix + ".kernel-output.json", JsonSerializer.Serialize(actual));
        using var n = new Curves(native); using var k = new Curves(actual);
        // Locus union can remove a lower-class contributor. Preserve its routing
        // footprint for the native-only exception veto, independently of ownership.
        using var contributors = new KernelContributors(observedScene,observedFrame);
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
                    failures.Add(new(direction, own.Piece?.Source ?? piece.Source, expected?.ToString() ?? "missing", found.Class?.ToString(), p.X, p.Y, p.Z, found.Gap, point, contributors.At(p,tolerance) || (direction == "kernel-to-native" ? own.KernelOwned : found.KernelOwned)));
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
        int contactEdges=0,contactJointSuppressed=0,exactCheckDecided=0,exactInconclusive=0,exactContradictions=0;
        using var reference=observedScene is null?null:new MixedReference(observedScene,observedFrame,tolerance,view.Cut is not null,nativeTrace,curvedBounds,contributors,n,k,nativeSourceIds,joints);
        using(var witnesses=new StreamWriter(prefix+".exact-witnesses.jsonl"))
            if(reference is not null)for(int i=0;i<failures.Count;i++)
            {
                var f=failures[i];if(f.Point)continue;
                string? nativeClass=f.Direction=="native-to-kernel"?f.Expected:f.Actual;
                string? routedClass=f.Direction=="native-to-kernel"?f.Actual:f.Expected;
                bool unknown=nativeClass=="Unresolved";
                bool droppedJoint=!joints&&routedClass is null&&nativeClass is "Visible" or "Hidden" or "Unresolved";
                if(!droppedJoint&&(routedClass is not ("Visible" or "Hidden")||!(unknown||nativeClass=="Hidden"&&routedClass=="Visible")))continue;
                var check=reference.Check(new(f.X,f.Y,f.Z));string? classification=null;
                if(droppedJoint&&check.Suppressed){classification="contactJointSuppressed";contactJointSuppressed++;}
                else if(check.Class.ToString()==routedClass)
                {
                    if(unknown){classification="exactCheckDecided";exactCheckDecided++;}
                    else if(check.Contact){classification="contactEdge";contactEdges++;}
                }
                if(classification is not null)classified.Add(i);
                else if(check.Class==M2DPlus.Core.Visibility.VisibilityClass.Unresolved)exactInconclusive++;
                else if(check.Class.ToString()!=routedClass)exactContradictions++;
                witnesses.WriteLine(JsonSerializer.Serialize(new{sample=i,classification,nativeClass,routedClass,check}));
            }
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
            string reverseOutputHash = Hash(reverseFile);
            File.WriteAllText(prefix + ".order-control.json", JsonSerializer.Serialize(new { sameInputs, orderPolicy=VisibilityInputOrder.Policy,
                baseline=Hash(nativeFile), reversed=reverseOutputHash, rhino=RhinoApp.Version.ToString(), rhinoCommonSha256=Hash(typeof(RhinoApp).Assembly.Location) }));
            if (sameInputs && reversed.Error is null)
                for (int i=0;i<failures.Count;i++)
                {
                    var f=failures[i]; if(classified.Contains(i)||!Eligible(f))continue;
                    var p=new Point3d(f.X,f.Y,f.Z);
                    var canonical=n.At(p,tolerance,f.Point).Class?.ToString();
                    var reverse=controlCurves.At(p,tolerance,f.Point).Class?.ToString();
                    var routed=k.At(p,tolerance,f.Point);
                    if(routed.KernelOwned || canonical==reverse || routed.Class?.ToString()!=reverse)continue;
                    classified.Add(i);
                    orderWitnesses.Add(new { sample=i, classification="native-order-quirk", f.Source, f.X,f.Y,f.Z,
                        canonical, reversed=reverse, routed=reverse, kernelOwned=false, control="same full geometry, same view/tolerance/hidden-on, reverse canonical registration order",
                        outputSha256=reverseOutputHash });
                }
        }
        File.WriteAllText(prefix + ".order-witnesses.json", JsonSerializer.Serialize(orderWitnesses));
        var options = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(prefix + ".failures.json", JsonSerializer.Serialize(failures));
        var record = new { model = path, modelSha256 = Hash(path), view = viewName, engineSha256 = Hash(typeof(NativeDrawing).Assembly.Location), coreSha256 = Hash(typeof(M2DPlus.Core.Visibility.EdgeFaceKernel).Assembly.Location),
            rhino = RhinoApp.Version.ToString(), runtime = System.Environment.Version.ToString(), tolerance, spacing, multipleThreads = false, harnessSha256 = Hash(typeof(CompositionGate).Assembly.Location), captureBudgetSeconds = 300, comparison = "exact line-support distance for linear loci; native exact-curve closest point otherwise",
            nativePurpose = "correctness only; no timing mutex; native seconds diagnostic only, not a benchmark", captureSeconds, prepareSeconds, solveSeconds,
            endToEndSeconds = captureSeconds + prepareSeconds + solveSeconds, includesPresentation = true, includesGhPublication = measurePublication, publicationAdapter = "actual component Emit / GH volatile trees; excludes scheduling, downstream solves and redraw", nativeSolveSeconds, nativeEndToEndSeconds = captureSeconds + prepareSeconds + nativeSolveSeconds, nativePhases = native.Phases, routedPhases = actual.Phases, actual.KernelEdges, actual.NativeEdges, actual.NativeCalls, actual.CurvedCandidateEdges, actual.PlanarNativeEdges, actual.NativeFragments, nativeCountKind = "drawable native parent curves", nativeInfo = native.Info, kernelInfo = actual.Info,
            contactRuleVersion=1,joints,contactRule="planar foreign-face exact incidence; seated and joints-on flush visible; approximate gaps never receive a tie exception; native contacts retain unverified native classification",
            samples, disagreements = failures.Count, unexplained = failures.Count - classified.Count, classes = new { nativeOrderQuirk = orderWitnesses.Count, contactEdges, contactJointSuppressed, tieConvention=0, exactCheckDecided, toleranceArtefact = 0, unclassified = failures.Count - classified.Count }, exactInconclusive, exactContradictions, exactCompletePairs=reference?.CompletePairs??0,exactPointPairs=reference?.ExactPointPairs??0,
            exactEvidenceSha256=Hash(prefix+".exact-witnesses.jsonl"),
            bugs = native.Error is not null || actual.Error is not null || exactContradictions!=0 || !native.RegionAreas.SequenceEqual(actual.RegionAreas) || !native.RegionLoops.SequenceEqual(actual.RegionLoops) ? 1 : failures.Count == classified.Count ? (int?)0 : null, nativeOrderPolicy = VisibilityInputOrder.Policy,
            passed = native.Error is null && actual.Error is null && exactContradictions==0 && failures.Count == classified.Count && native.RegionAreas.SequenceEqual(actual.RegionAreas) && native.RegionLoops.SequenceEqual(actual.RegionLoops),
            nativeError = native.Error, kernelError = actual.Error, native.RegionAreas, actualRegionAreas = actual.RegionAreas };
        File.WriteAllText(prefix + ".gate.json", JsonSerializer.Serialize(record, options));
        Console.WriteLine($"{Path.GetFileName(path)} {viewName}: {solveSeconds:F3}s; kernel={actual.KernelEdges} native={actual.NativeEdges}; disagreements={failures.Count}; error={actual.Error}");
    }
}
