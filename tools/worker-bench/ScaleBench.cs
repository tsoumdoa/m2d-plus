using System.Diagnostics;
using System.Text.Json;
using M2DPlus.Core;
using M2DPlus.Rhino.Services;
using Rhino;
using Rhino.Geometry;

internal static class ScaleBench
{
    internal static void Compare(string left,string right,string output)
    {
        using var core = new Rhino.Runtime.InProcess.RhinoCore(["/nosplash", "/notemplate"], Rhino.Runtime.InProcess.WindowStyle.NoWindow);
        using var a=WorkerCodec.RestoreDrawing(File.ReadAllBytes(left));using var b=WorkerCodec.RestoreDrawing(File.ReadAllBytes(right));
        var regions=new List<object>();
        if(a.Regions.Count!=b.Regions.Count)throw new InvalidOperationException("Region topology differs");
        for(int i=0;i<a.Regions.Count;i++)
        {
            var x=a.Regions[i].Region;var y=b.Regions[i].Region;
            if(x.Loops.Count!=y.Loops.Count)throw new InvalidOperationException("Loop topology differs");
            double maximum=0;
            for(int j=0;j<x.Loops.Count;j++)
            {
                var c=x.Loops[j].Curve;var d=y.Loops[j].Curve;
                double Distance(Curve from,Curve to)
                {
                    double value=0;
                    for(int k=0;k<=256;k++)
                    {
                        var p=from.PointAtNormalizedLength(k/256d);
                        if(!to.ClosestPoint(p,out var t))throw new InvalidOperationException("Closest point failed");
                        value=Math.Max(value,p.DistanceTo(to.PointAt(t)));
                    }
                    return value;
                }
                maximum=Math.Max(maximum,Math.Max(Distance(c,d),Distance(d,c)));
            }
            regions.Add(new{i,areaDelta=Math.Abs(x.Area-y.Area),maximumSampledCurveDistance=maximum,loops=x.Loops.Count});
        }
        File.WriteAllText(output,JsonSerializer.Serialize(new{left,right,piecesA=a.Items.Count,piecesB=b.Items.Count,regions,
            note="Sampled geometric diagnostic only; not a whole-curve certificate and not a waiver of failed byte equality."}));
    }
    internal static void Run(string model, string output, string indices)
    {
        Directory.CreateDirectory(output);
        using var core = new Rhino.Runtime.InProcess.RhinoCore(["/nosplash", "/notemplate"], Rhino.Runtime.InProcess.WindowStyle.NoWindow);
        using var doc = RhinoDoc.OpenHeadless(model);
        if (System.Environment.GetEnvironmentVariable("SCALE_BENCH_TOLERANCE") is {} authoredTolerance)
            doc.ModelAbsoluteTolerance=double.Parse(authoredTolerance,System.Globalization.CultureInfo.InvariantCulture);
        if (indices.StartsWith("generate", StringComparison.Ordinal))
        {
            if (doc.ModelUnitSystem != UnitSystem.Meters) throw new InvalidOperationException("Expected metre source");
            var before = doc.Objects.GetObjectList(Rhino.DocObjects.ObjectType.AnyObject).Count();
            doc.AdjustModelUnitSystem(UnitSystem.Millimeters, true);
            doc.ModelAbsoluteTolerance = .01;
            double offset = indices.Contains(':') ? double.Parse(indices.Split(':')[1], System.Globalization.CultureInfo.InvariantCulture) : 0;
            if (offset != 0)
                foreach (var obj in doc.Objects.GetObjectList(new Rhino.DocObjects.ObjectEnumeratorSettings { NormalObjects=true, LockedObjects=true, HiddenObjects=true }).ToArray())
                    if (!obj.IsInstanceDefinitionGeometry) doc.Objects.Transform(obj.Id, Transform.Translation(offset,offset,offset),true);
            string target = Path.Combine(output, Path.GetFileName(model));
            if (File.Exists(target)) throw new IOException("Never overwrite model evidence: " + target);
            if (!doc.Write3dmFile(target, new Rhino.FileIO.FileWriteOptions())) throw new IOException("Model write failed");
            File.WriteAllText(target + ".generation.json", JsonSerializer.Serialize(new { source = model, sourceHash = WorkerCodec.Hash(File.ReadAllBytes(model)), target,
                method = "RhinoDoc.AdjustModelUnitSystem(Millimeters, scale:true): factor 1000 about world origin; includes definitions, instance placements and named views", offset, tolerance = .01, before,
                after = doc.Objects.GetObjectList(Rhino.DocObjects.ObjectType.AnyObject).Count() }));
            return;
        }
        if (indices=="capture6k")
        {
            // Measure host capture, without changing the model file or capture implementation.
            using var box=Brep.CreateFromBox(new BoundingBox(Point3d.Origin,new Point3d(100,100,100)));
            int count=doc.Objects.GetObjectList(new Rhino.DocObjects.ObjectEnumeratorSettings {NormalObjects=true,LockedObjects=true}).Count(o=>SceneCapture.IsDrawable(doc,o));
            for(int i=count;i<6000;i++) doc.Objects.AddBrep(box);
        }
        using var watch = new DocumentWatch(doc); using var cache = new SceneCache();
        var clock = Stopwatch.StartNew();
        using var capture = new SceneCapture(doc, watch, null, cache);
        while (!capture.Step(TimeSpan.FromMilliseconds(8))) { }
        using var lease = capture.Accept(); var scene = lease.Value;
        double captureSeconds = clock.Elapsed.TotalSeconds;
        double surveyOffset=0;
        if (File.Exists(model+".generation.json"))
        {
            using var generation=JsonDocument.Parse(File.ReadAllBytes(model+".generation.json"));
            if (generation.RootElement.TryGetProperty("offset",out var authoredOffset)) surveyOffset=authoredOffset.GetDouble();
        }
        var views = new[] { "Back", "Front", "Right", "Left", "Top", "SW" }.Select(v => ViewRequest.Parse(v)).Concat(new[] {
            ViewRequest.Parse(new Plane(scene.Bounds.Center, Vector3d.XAxis, Vector3d.ZAxis)) with { Name = "Long section" },
            ViewRequest.Parse(new Plane(new Point3d(scene.Bounds.Center.X, scene.Bounds.Center.Y, surveyOffset+1.2 * RhinoMath.UnitScale(UnitSystem.Meters, doc.ModelUnitSystem)), Vector3d.ZAxis)) with { Name = "Plan 1.2m" }
        }).ToArray();
        var units = doc.ModelUnitSystem == UnitSystem.Millimeters ? DocumentUnits.Millimetres : DocumentUnits.Metres;
        var resolved = Settings.Resolve(views.Select((v,i) => new ViewSettingsContext(i.ToString(), v.Name, v.Kind, v.Cut is not null, scene.Bounds.Diagonal.Length, scene.Tolerance)).ToArray(), [["hidden = on"]], units);
        views = views.Select((v,i) => v with { Settings = resolved.Views[i] }).ToArray();
        var identity = new RequestIdentity(scene.Revision, "scale-views", "production", 1);
        File.WriteAllBytes(Path.Combine(output,"scene.json"), WorkerCodec.Capture(scene,default));
        File.WriteAllBytes(Path.Combine(output,"identity.json"), WorkerCodec.Encode(WorkerCodec.Fingerprint()));
        File.WriteAllBytes(Path.Combine(output,"cost.json"), WorkerCodec.Encode(scene.Cost));
        for (int i=0;i<views.Length;i++) File.WriteAllBytes(Path.Combine(output,$"request-{i}.json"), WorkerCodec.Encode(new WorkerCodec.Request(identity,i,views[i],false)));
        var rows = new List<object>();
        void Save() => File.WriteAllText(Path.Combine(output,"scale.json"),JsonSerializer.Serialize(new {model,units=doc.ModelUnitSystem.ToString(),tolerance=doc.ModelAbsoluteTolerance,captureSeconds,
            capture.InitialEnumerationMilliseconds,capture.MaximumSliceMilliseconds,capture.ActiveSeconds,scene.SourceCount,scene.Cost,bounds=scene.Bounds.ToString(),fingerprint=WorkerCodec.Fingerprint(),rows}));
        Save();
        bool raw = indices.StartsWith("raw:",StringComparison.Ordinal) || indices.StartsWith("threaded:",StringComparison.Ordinal);
        bool threaded = indices.StartsWith("threaded:",StringComparison.Ordinal);
        bool productionOnly = indices.StartsWith("production:",StringComparison.Ordinal) || raw;
        indices = indices.Replace("production:", "", StringComparison.Ordinal).Replace("raw:", "", StringComparison.Ordinal).Replace("threaded:", "", StringComparison.Ordinal);
        var selected = indices == "all" ? Enumerable.Range(0,8) : indices is "capture6k" or "capture" ? [] : indices.Split(',').Select(int.Parse);
        foreach (int i in selected)
        {
            if (raw)
            {
                var frame = views[i].Resolve(scene.Bounds, scene.Tolerance);
                using var viewport = new Rhino.DocObjects.ViewportInfo();
                double radius = Math.Max(1,scene.Bounds.Diagonal.Length);
                viewport.IsParallelProjection = true;
                viewport.SetCameraLocation(frame.Plane.Origin+frame.Plane.ZAxis*(radius*4+10));
                viewport.SetCameraDirection(views[i].Direction); viewport.SetCameraUp(frame.Plane.YAxis);
                viewport.SetFrustum(frame.X.Min,frame.X.Max,frame.Y.Min,frame.Y.Max,.001,radius*10+100);
                var parameters = new HiddenLineDrawingParameters { AbsoluteTolerance=scene.Tolerance, Flatten=false, IncludeHiddenCurves=true, IncludeTangentEdges=false, IncludeTangentSeams=false };
                parameters.SetViewport(viewport);
                foreach (var source in VisibilityInputOrder.Canonical(scene.Prepare(default),s=>new(s.Geometry,s))) parameters.AddGeometry(source.Geometry,source.Identity);
                clock.Restart(); using var hld = HiddenLineDrawing.Compute(parameters,threaded);
                double seconds = clock.Elapsed.TotalSeconds;
                if (hld is null) throw new InvalidOperationException("Native proxy failed");
                rows.Add(new {i, views[i].Name, native=true, threaded, seconds, segments=hld.Segments.Count()});
                Save();
                Console.WriteLine($"{Path.GetFileName(model)} {i} raw HLD threaded={threaded}: {seconds:F3}s");
                continue;
            }
            foreach (bool native in productionOnly ? new[] {false} : new[] { false, true })
            {
                clock.Restart();
                using var batch = NativeDrawing.SolveBorrowedAsync(scene,identity,[views[i]],new() { NativeOnly = native, IsWorker = true, Scheduler = new(12L*1024*1024*1024) },default,new ProgressSink()).GetAwaiter().GetResult();
                double seconds = clock.Elapsed.TotalSeconds; var drawing = batch.Views[0];
                if (drawing.Error is not null) throw new InvalidOperationException(drawing.Error);
                rows.Add(new { i, drawing.Name, native, seconds, pieces=drawing.Items.Count, regions=drawing.Regions.Count, phases=drawing.Phases.Seconds, info=drawing.Info });
                Save();
                if (!native)
                {
                    var stable = WorkerCodec.CaptureDrawing(drawing,true);
                    File.WriteAllBytes(Path.Combine(output,$"drawing-{i}.json"),stable);
                    File.WriteAllText(Path.Combine(output,$"hash-{i}.txt"),WorkerCodec.Hash(stable));
                }
                Console.WriteLine($"{Path.GetFileName(model)} {i} native={native}: {seconds:F3}s; {string.Join(", ",drawing.Phases.Seconds.Select(p=>$"{p.Key}={p.Value:F3}"))}");
            }
        }
    }
    private sealed class ProgressSink : IProgress<JobProgress>
    {
        private string? _last;
        // The same view/step/percentage the GH status footer shows (row 1 view, row 2 step).
        public void Report(JobProgress p)
        {
            if (p.Step is not {} step) return;
            var detail = $"{p.View}: {step}" + (p.StepFraction is double f ? $" {(int)Math.Floor(Math.Clamp(f,0,1)*100)} %" : "");
            if (detail != _last) { _last=detail; Console.WriteLine(detail); }
        }
    }
}
