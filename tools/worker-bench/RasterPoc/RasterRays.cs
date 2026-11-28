using System.Text.Json;
using M2DPlus.Core;
using M2DPlus.Measurements;
using M2DPlus.Rhino.Services;
using Rhino;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;

// Representative post-measurement controls, never part of raster classification.
internal static class RasterRays
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, IncludeFields = true };
    internal static void Run(string caseFolder, string resolution, string output)
    {
        if (System.Environment.GetEnvironmentVariable("M2D_RHINO_TESTS") != "1") throw new InvalidOperationException("Set M2D_RHINO_TESTS=1");
        if (File.Exists(output) || Directory.Exists(output)) throw new IOException("Preserve existing ray evidence");
        using var metadata = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(caseFolder, "case.json"))); var header = metadata.RootElement;
        using var metrics = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(caseFolder, resolution, "result.json")));
        var root = System.Environment.GetEnvironmentVariable("M2D_MODELS") ?? Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile), ".t3", "m2d-plus-models");
        var model = RasterBench.Model(root, header.GetProperty("Label").GetString()!);
        if (WorkerCodec.Hash(File.ReadAllBytes(model)) != header.GetProperty("ModelHash").GetString())
            throw new InvalidDataException("Diagnostic model fingerprint mismatch");
        using var mutex = new Mutex(false, @"Local\M2DPlusLocalModelSuiteRhino");
        try { mutex.WaitOne(); } catch (AbandonedMutexException) { }
        try
        {
            using var core = new Rhino.Runtime.InProcess.RhinoCore(["/nosplash", "/notemplate"], Rhino.Runtime.InProcess.WindowStyle.NoWindow);
            if (!WorkerCodec.Fingerprint().Accepts(header.GetProperty("Fingerprint").Deserialize<WorkerFingerprint>(Json)!))
                throw new InvalidDataException("Diagnostic engine fingerprint mismatch");
            using var doc = RhinoDoc.OpenHeadless(model);
            var name = header.GetProperty("View").GetString()!;
            var view = header.TryGetProperty("AuthoredViewArchive", out var archive)
                ? WorkerCodec.Decode<ViewRequest>(System.Text.Encoding.UTF8.GetBytes(archive.GetString()!)) : RasterBench.ReadView(doc, name);
            var units = doc.ModelUnitSystem == UnitSystem.Meters ? DocumentUnits.Metres : DocumentUnits.Millimetres;
            var cheap = BoundingBox.Empty;
            foreach (var obj in doc.Objects.GetObjectList(global::Rhino.DocObjects.ObjectType.AnyObject))
                if (SceneCapture.IsDrawable(doc, obj)) cheap.Union(obj.Geometry.GetBoundingBox(false));
            view = view with { Settings = Settings.Resolve([new(name, name, view.Kind, view.Cut is not null, cheap.Diagonal.Length,
                doc.ModelAbsoluteTolerance, view.Cut?.SourceDepth)], [[header.GetProperty("Settings").GetString()!]], units).Views[0] };
            using var watch = new DocumentWatch(doc); using var cache = new SceneCache();
            using var capture = new SceneCapture(doc, watch, null, cache,
                view.ObjectId == Guid.Empty ? null : new HashSet<Guid> { view.ObjectId }, null, _ => [view]);
            while (!capture.Step(TimeSpan.FromSeconds(30))) { }
            using var lease = capture.Accept(); var scene = lease.Value;
            var inputs = new List<VisibilityInput>(); var before = NativeDrawing.HldCalls;
            try
            {
                using var stopped = NativeDrawing.SolveBorrowedAsync(scene, new(scene.Revision, "raster-rays", name, 1), [view],
                    new() { IsWorker = true, Scheduler = new(WorkerMemoryPolicy.Budget(WorkerMachineMemory.Read())), ObserveNativeInputs = batch =>
                    {
                        foreach (var input in batch) { var owned = input.Geometry.Duplicate(); owned.EnsurePrivateCopy(); inputs.Add(input with { Geometry = owned }); }
                        throw new InvalidOperationException("RASTER_RAY_CAPTURE_STOP");
                    } }, default, null).GetAwaiter().GetResult();
                var digest = WorkerCodec.Hash(WorkerCodec.Encode(inputs.Select(i => new { hash = GeometryFingerprint.Compute(i.Geometry), i.Source.Identity, i.Far }).ToArray()));
                if (NativeDrawing.HldCalls != before || digest != header.GetProperty("InputDigest").GetString() ||
                    scene.SourceCount != header.GetProperty("SceneSources").GetInt32()) throw new InvalidDataException("Diagnostic prepared input mismatch");
                using var hld = WorkerCodec.RestoreDrawing(File.ReadAllBytes(Path.Combine(caseFolder, "hld.json")));
                using var raster = WorkerCodec.RestoreDrawing(File.ReadAllBytes(Path.Combine(caseFolder, resolution, "raster.json")));
                using var spans = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(caseFolder, resolution, "error-spans.json")));
                var frame = hld.Frame.Plane; var toXY = Transform.PlaneToPlane(frame, Plane.WorldXY);
                var box = BoundingBox.Empty;
                foreach (var input in inputs) box.Union(input.Geometry.GetBoundingBox(toXY));
                box.Inflate(Math.Max(hld.Tolerance * 16, box.Diagonal.Length * .01));
                var pixel = metrics.RootElement.GetProperty("Timings").GetProperty("Pixel").GetDouble();
                var meshTolerance = metrics.RootElement.GetProperty("Timings").GetProperty("MeshTolerance").GetDouble();
                var factor = header.GetProperty("MillimetresPerUnit").GetDouble();
                var bodies = inputs.Select((input, i) => (Input: input, Index: i, Bounds: input.Geometry.GetBoundingBox(toXY))).Where(b => b.Input.Geometry is Brep).ToArray();
                var records = new List<object>(); var reach = scene.Bounds.Diagonal.Length * 2; var tolerance = hld.Tolerance;
                object Ray(Point3d point)
                {
                    var local = point; local.Transform(toXY); var details = new List<object>();
                    foreach (var body in bodies)
                    {
                        if (local.X < body.Bounds.Min.X - tolerance || local.X > body.Bounds.Max.X + tolerance ||
                            local.Y < body.Bounds.Min.Y - tolerance || local.Y > body.Bounds.Max.Y + tolerance || local.Z > body.Bounds.Max.Z + tolerance) continue;
                        using var ray = new LineCurve(point + frame.ZAxis * (4 * tolerance), point + frame.ZAxis * reach);
                        var brep = (Brep)body.Input.Geometry;
                        var succeeded = Intersection.CurveBrep(ray, brep, tolerance, out var overlaps, out var hits);
                        try
                        {
                            var depths = (hits ?? []).Select(p => (p - point) * frame.ZAxis).Where(d => d > 4 * tolerance).Order().ToArray();
                            var previous = 4 * tolerance; var interior = 0d;
                            foreach (var depth in depths)
                            {
                                var mid = point + frame.ZAxis * ((previous + depth) * .5);
                                if (brep.IsSolid && brep.IsPointInside(mid, tolerance, true) && brep.ClosestPoint(mid).DistanceTo(mid) > tolerance)
                                    interior += depth - previous;
                                previous = depth;
                            }
                            if (depths.Length > 0 || !succeeded) details.Add(new { Registration = body.Index, body.Input.Source.Identity,
                                Succeeded = succeeded, Solid = brep.IsSolid, DepthsMm = depths.Select(d => d * factor), InteriorMm = interior * factor });
                        }
                        finally { foreach (var overlap in overlaps ?? []) overlap.Dispose(); }
                    }
                    return details;
                }
                foreach (var (kind, drawing) in new[] { ("WrongVisible", raster), ("WrongHidden", hld) })
                foreach (var span in spans.RootElement.GetProperty(kind).Deserialize<DrawingComparison.Span[]>(Json)!.OrderByDescending(s => s.Mm).Take(12))
                {
                    var piece = drawing.Items[span.Piece]; using var flat = (Curve)piece.CopyGeometry();
                    using var part = flat.Trim(span.Start, span.End)!;
                    if (!part.NormalizedLengthParameter(.5, out var t) || piece.DepthGeometry is not Curve depth) continue;
                    var p = depth.PointAt(t); var local = p; local.Transform(toXY);
                    var x = box.Min.X + (Math.Floor((local.X - box.Min.X) / pixel) + .5) * pixel;
                    var y = box.Min.Y + (Math.Floor((local.Y - box.Min.Y) / pixel) + .5) * pixel;
                    var centre = p + frame.XAxis * (x - local.X) + frame.YAxis * (y - local.Y);
                    records.Add(new { Kind = kind, span.Piece, span.Mm, span.Start, span.End, piece.SourceIdentity, piece.Profile,
                        Point = p, PixelMm = pixel * factor, MaximumDepthBiasMm = (2 * tolerance + 2 * meshTolerance + 2 * pixel) * factor,
                        InsideRequestedFrame = NativeDrawing.Inside(p, drawing.Frame, tolerance),
                        CandidateRay = Ray(p), PixelCentreRay = Ray(centre) });
                }
                Directory.CreateDirectory(output);
                File.WriteAllText(Path.Combine(output, "ray-controls.json"), JsonSerializer.Serialize(new
                {
                    SourceCase = Path.GetFullPath(caseFolder), Resolution = resolution, Fingerprint = WorkerCodec.Fingerprint(), InputDigest = digest,
                    Method = "Twelve longest spans per error direction; native trimmed-Brep forward intersections and strict solid interior at interval midpoints, at candidate ray and its raster pixel centre. Representative controls, not a length-integrated oracle; no HLD solve.",
                    Records = records
                }, Json));
                Console.WriteLine($"Wrote {records.Count} representative native ray controls");
            }
            finally { foreach (var input in inputs) input.Geometry.Dispose(); }
        }
        finally { mutex.ReleaseMutex(); }
    }
}
