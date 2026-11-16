using System.Diagnostics;
using System.Text.Json;
using M2DPlus.Core;
using M2DPlus.Rhino.Services;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

internal static class NativeHldBench
{
    internal static void Run(string model,string output,string variant)
    {
        Directory.CreateDirectory(output);
        // The owned UI host is process-scoped. Rhino's toolbar updater displays
        // a modal dialog from RhinoCore.Dispose even with WindowStyle.Hidden;
        // the isolated harness exits after flushing evidence instead.
        using var core=variant=="command-owned"?null:new Rhino.Runtime.InProcess.RhinoCore(["/nosplash","/notemplate"],Rhino.Runtime.InProcess.WindowStyle.NoWindow);
        var uiCore=variant=="command-owned"?new Rhino.Runtime.InProcess.RhinoCore(["/nosplash","/notemplate"],Rhino.Runtime.InProcess.WindowStyle.Hidden):null;
        using var doc=variant=="command-owned" ? RhinoDoc.Create(null) : RhinoDoc.OpenHeadless(model);
        if(doc is null)throw new InvalidOperationException("RhinoDoc.Open returned null in the owned host.");
        RhinoDoc.ActiveDoc=doc;
        if(variant=="command-owned")
        {
            using var settings=Rhino.FileIO.File3dm.Read(model);
            doc.ModelUnitSystem=settings.Settings.ModelUnitSystem;doc.ModelAbsoluteTolerance=settings.Settings.ModelAbsoluteTolerance;
            doc.ModelAngleToleranceRadians=settings.Settings.ModelAngleToleranceRadians;
            if(!RhinoDoc.ReadFile(model,new Rhino.FileIO.FileReadOptions {ImportMode=true}))throw new IOException("Owned document read failed");
        }
        using var watch=new DocumentWatch(doc);using var cache=new SceneCache();
        var clock=Stopwatch.StartNew();using var capture=new SceneCapture(doc,watch,null,cache);
        while(!capture.Step(TimeSpan.FromMilliseconds(30))){}
        using var lease=capture.Accept();var scene=lease.Value;var captured=clock.Elapsed.TotalSeconds;
        clock.Restart();var sources=scene.Prepare(default);var preparation=clock.Elapsed.TotalSeconds;
        var request=ViewRequest.Parse("Back");var frame=request.Resolve(scene.Bounds,scene.Tolerance);
        using var viewport=new ViewportInfo();NativeViewport.Configure(viewport,frame,request.Direction,scene.Bounds,scene.Tolerance);
        if(variant=="witness")
        {
            using var comparisons=JsonDocument.Parse(File.ReadAllText(Path.Combine(output,"locus-comparisons.json")));
            var rows=new List<object>();
            foreach(string name in new[]{"hidden-off","threads","threads-hidden-off","make2d-like"})
            {
                if(!comparisons.RootElement.TryGetProperty(name,out var comparison))continue;
                using var reference=Rhino.FileIO.File3dm.Read(Path.Combine(output,"baseline.3dm"));
                using var candidate=Rhino.FileIO.File3dm.Read(Path.Combine(output,name+".3dm"));
                foreach(string visibility in new[]{"Visible","Hidden"})
                {
                    using var a=new CurveIndex(reference,visibility);using var b=new CurveIndex(candidate,visibility);
                    foreach(bool reverse in new[]{false,true})
                    foreach(var example in comparison.GetProperty(visibility).GetProperty(reverse?"variantToReference":"referenceToVariant").GetProperty("examples").EnumerateArray())
                    {
                        var query=new Point3d(example[0].GetDouble(),example[1].GetDouble(),0);
                        double referenceDistance=(reverse?b:a).Closest(query,scene.Tolerance,out var exact);
                        double targetDistance=double.IsFinite(referenceDistance)?(reverse?a:b).Closest(exact,1,out _):double.NaN;
                        rows.Add(new{name,visibility,reverse,query=new[]{query.X,query.Y},referenceDistance,
                            point=exact.IsValid?new[]{exact.X,exact.Y}:null,nearestCandidateDistance=double.IsFinite(targetDistance)?targetDistance:(double?)null,
                            targetDistanceLowerBound=double.IsNaN(targetDistance)?(double?)null:Math.Min(1,targetDistance),
                            targetOutsideOneMillimetre=targetDistance>1,
                            confirmed=referenceDistance<=scene.Tolerance/16&&targetDistance>scene.Tolerance*1.125});
                    }
                }
            }
            File.WriteAllText(Path.Combine(output,"exact-curve-witnesses.json"),JsonSerializer.Serialize(rows,new JsonSerializerOptions {NumberHandling=System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals}));
            Console.WriteLine($"Checked {rows.Count} sample witnesses against exact detached NURBS curves");
            return;
        }
        if(variant=="export-command")
        {
            // Make2D's View layout places the original viewport's lower-left
            // corner at (0,0). Undo only that known translation, never a fitted
            // registration, so missing extents cannot disappear in comparison.
            using var archive=Rhino.FileIO.File3dm.Read(Path.Combine(output,"command-output.3dm"));
            using var metadata=JsonDocument.Parse(File.ReadAllText(Path.Combine(output,"command.json")));
            bool hidden=metadata.RootElement.GetProperty("script").GetString()!.Contains("_CreateHiddenLines=_Yes",StringComparison.Ordinal);
            var curves=new List<object>();int tessellationFailures=0;
            foreach(var obj in archive.Objects)
            {
                if(obj.Geometry is not Curve original)continue;
                using var curve=original.DuplicateCurve();
                curve.Transform(Transform.Translation(frame.X.Min,frame.Y.Min,0));
                Polyline poly;
                if(!curve.TryGetPolyline(out poly))
                {
                    using var approximation=curve.ToPolyline(scene.Tolerance/16,.02,0,0);
                    if(approximation is null||!approximation.TryGetPolyline(out poly))
                    {
                        tessellationFailures++;
                        poly=new Polyline(Enumerable.Range(0,33).Select(i=>curve.PointAt(curve.Domain.ParameterAt(i/32d))));
                    }
                }
                curves.Add(new{visibility=hidden?"All":"Visible",source="command",length=curve.GetLength(),points=poly.SelectMany(p=>new[]{p.X,p.Y}).ToArray()});
            }
            File.WriteAllText(Path.Combine(output,"command-curves.json"),JsonSerializer.Serialize(new{registration=new[]{frame.X.Min,frame.Y.Min},hidden,tessellationFailures,curves}));
            Console.WriteLine($"Exported {curves.Count} real command curves; translation=({frame.X.Min:R},{frame.Y.Min:R})");
            return;
        }
        if(variant=="audit")
        {
            // NativeOnly selects the routing reference; it does not disable the
            // audit. Start with zero audit coverage so both measured paths see
            // fresh native classifications rather than an already-vetoed result.
            using var raw=NativeDrawing.SolveBorrowedAsync(scene,new(scene.Revision,"audit","baseline",1),[request],
                new(NativeOnly:true){Verification=new(MaxRayTests:0,MaxSamples:0)},default,null).GetAwaiter().GetResult();
            if(raw.Views[0].Error is {} error)throw new InvalidOperationException(error);
            var bytes=WorkerCodec.CaptureDrawing(raw.Views[0]);var rows=new List<object>();string? expected=null;
            for(int pass=0;pass<6;pass++)
            foreach(bool exhaustive in pass%2==0?new[]{true,false}:new[]{false,true})
            {
                using var drawing=WorkerCodec.RestoreDrawing(bytes);
                clock.Restart();NativeVisibilityVerifier.Apply(sources.Select(s=>new VisibilityInput(s.Geometry,s)).ToArray(),drawing,
                    new(){Verification=new(){ExhaustiveOffsets=exhaustive}},default);
                double seconds=clock.Elapsed.TotalSeconds;
                var hash=WorkerCodec.Hash(WorkerCodec.CaptureDrawing(drawing,true));expected??=hash;
                rows.Add(new{pass,exhaustive,seconds,hash,match=hash==expected,diagnostics=drawing.Diagnostics.Where(d=>d.StartsWith("Native ray verifier:",StringComparison.Ordinal)).ToArray()});
                Console.WriteLine($"Audit exhaustive={exhaustive} pass={pass}: {seconds:F4}s match={hash==expected}");
                File.WriteAllText(Path.Combine(output,"audit.json"),JsonSerializer.Serialize(new{model,fingerprint=WorkerCodec.Fingerprint(),rows}));
                if(hash!=expected)throw new InvalidOperationException("Audit early-exit changed stable drawing bytes");
            }
            return;
        }
        if(variant is "command-headless" or "command-owned")
        {
            var view=doc.Views.ActiveView??doc.Views.Add("M2D native comparison",Rhino.Display.DefinedViewportProjection.Back,new System.Drawing.Rectangle(0,0,1000,800),false);
            if(view is not null){view.ActiveViewport.SetViewProjection(viewport,true);view.ActiveViewport.SetConstructionPlane(frame.Plane);doc.Views.ActiveView=view;}
            var before=doc.Objects.GetObjectList(ObjectType.AnyObject).Select(o=>o.Id).ToHashSet();
            foreach(var obj in doc.Objects.GetObjectList(new ObjectEnumeratorSettings {NormalObjects=true,LockedObjects=true}))
                if(SceneCapture.IsDrawable(doc,obj))obj.Select(true);
            string script=System.Environment.GetEnvironmentVariable("HLD_COMMAND_SCRIPT")??"_-Make2D _Layout=_View _Properties=_ByOutputLayers _CreateHiddenLines=_No _ShowTangents=_No _CreateSceneSilhouette=_No _CreateClippingPlaneIntersections=_No _ShowViewRectangle=_No _GroupOutput=_No _Enter";
            RhinoApp.ClearCommandHistoryWindow();clock.Restart();
            bool ran=RhinoApp.RunScript(doc.RuntimeSerialNumber,script,true);
            var seconds=clock.Elapsed.TotalSeconds;
            var added=doc.Objects.GetObjectList(ObjectType.AnyObject).Where(o=>!before.Contains(o.Id)).ToArray();
            File.WriteAllText(Path.Combine(output,"command.json"),JsonSerializer.Serialize(new{model,variant,doc.IsHeadless,units=doc.ModelUnitSystem.ToString(),tolerance=doc.ModelAbsoluteTolerance,
                scene.SourceCount,viewCreated=view is not null,script,ran,seconds,captured,preparation,added=added.Length,history=RhinoApp.CommandHistoryWindowText,
                layers=added.GroupBy(o=>doc.Layers[o.Attributes.LayerIndex].FullPath).ToDictionary(g=>g.Key,g=>g.Count())}));
            if(added.Length>0)
            {
                using var result=new Rhino.FileIO.File3dm();
                foreach(var obj in added)result.Objects.Add(obj.Geometry,obj.Attributes);
                result.Write(Path.Combine(output,"command-output.3dm"),8);
            }
            Console.WriteLine($"Command headless={doc.IsHeadless}, view={view is not null}, success={ran}, added={added.Length}, seconds={seconds:F3}");
            Console.WriteLine(RhinoApp.CommandHistoryWindowText);
            if(uiCore is not null){GC.KeepAlive(uiCore);System.Environment.Exit(0);}
            return;
        }
        foreach(string experiment in variant.Split(',')) Measure(experiment);

        void Measure(string experiment)
        {
            string file=Path.Combine(output,experiment+".json");
            if(File.Exists(file))throw new IOException("Evidence already exists: "+file);
            var owned=new List<GeometryBase>();
            try
            {
                var ordered=VisibilityInputOrder.Canonical(sources,s=>new(s.Geometry,s)).ToArray();
                int take=experiment.StartsWith("count-",StringComparison.Ordinal)?int.Parse(experiment[6..]):ordered.Length;
                ordered=ordered.Take(take).ToArray();
                var registrations=new List<(GeometryBase Geometry,Transform Transform,string Source)>();
                clock.Restart();
                foreach(var source in ordered)
                {
                    GeometryBase geometry=source.Geometry;var transform=Transform.Identity;
                    if(experiment is "document" or "transforms" or "make2d-like")
                    {
                        var leaf=scene.TransportLeaves.Single(l=>l.Identity==source.Identity);
                        var id=Guid.Parse(leaf.Identity.Split('/')[^1]);
                        geometry=doc.Objects.FindId(id)?.Geometry??throw new InvalidOperationException("Document leaf missing");
                        transform=PreparedScene.Combined(leaf.Chain);
                    }
                    if(experiment=="duplicate") {geometry=geometry.Duplicate();geometry.EnsurePrivateCopy();owned.Add(geometry);}
                    if(experiment=="meshes"&&geometry is Brep brep)
                    {
                        using var parameters=new MeshingParameters {Tolerance=scene.Tolerance,RelativeTolerance=0,MinimumEdgeLength=0};
                        var pieces=Mesh.CreateFromBrep(brep,parameters);var mesh=new Mesh();
                        foreach(var piece in pieces){mesh.Append(piece);piece.Dispose();}geometry=mesh;owned.Add(mesh);
                    }
                    registrations.Add((geometry,transform,source.Identity));
                }
                if(experiment.StartsWith("batch-",StringComparison.Ordinal))
                {
                    int size=int.Parse(experiment[6..]);var grouped=new List<(GeometryBase,Transform,string)>();
                    foreach(var group in registrations.Chunk(size))
                    {
                        var compound=new Brep();
                        foreach(var item in group)
                            if(item.Geometry is Brep brep)compound.Append(brep);else throw new InvalidOperationException("Batch probe is Brep-only");
                        if(!compound.IsValid)throw new InvalidOperationException("Invalid compound Brep");
                        owned.Add(compound);grouped.Add((compound,Transform.Identity,group[0].Source));
                    }
                    registrations=grouped;
                }
                double geometrySeconds=clock.Elapsed.TotalSeconds;
                using var vp=new ViewportInfo(viewport);
                if(experiment is "command-viewport" or "command-camera" or "make2d-like")
                {
                    var localBounds=new BoundingBox(scene.Bounds.GetCorners().Select(p=>{var d=p-scene.Bounds.Center;return new Point3d(d*frame.Plane.XAxis,d*frame.Plane.YAxis,d*frame.Plane.ZAxis);}));
                    var diagonal=localBounds.Diagonal;
                    vp.SetCameraLocation(scene.Bounds.Center+frame.Plane.ZAxis*diagonal.Z);
                    if(experiment!="command-camera")vp.SetFrustum(-.55*diagonal.X,.55*diagonal.X,-.55*diagonal.Y,.55*diagonal.Y,.45*diagonal.Z,1.65*diagonal.Z);
                }
                if(experiment=="viewport10")vp.SetFrustum(frame.X.Min*10,frame.X.Max*10,frame.Y.Min*10,frame.Y.Max*10,scene.Tolerance,scene.Bounds.Diagonal.Length*100);
                var parametersHld=new HiddenLineDrawingParameters {AbsoluteTolerance=scene.Tolerance*(experiment=="tolerance10"?10:1),
                    IncludeHiddenCurves=experiment is not ("hidden-off" or "threads-hidden-off" or "make2d-like"),IncludeTangentEdges=experiment is "tangents" or "tangents-seams",IncludeTangentSeams=experiment is "seams" or "tangents-seams" or "make2d-like",
                    Flatten=experiment=="flatten",OccludingSectionOption=experiment is "occluding" or "make2d-like"};
                parametersHld.SetViewport(vp);
                if(experiment=="clip-outside")parametersHld.AddClippingPlane(new Plane(frame.Plane.Origin+frame.Plane.ZAxis*scene.Bounds.Diagonal.Length,frame.Plane.ZAxis));
                if(experiment=="clip-inside")parametersHld.AddClippingPlane(new Plane(scene.Bounds.Center,frame.Plane.ZAxis));
                clock.Restart();
                foreach(var item in registrations)
                    if(experiment=="make2d-like")parametersHld.AddGeometryAndPlanes(item.Geometry,item.Transform,item.Source,false,[]);
                    else if(!parametersHld.AddGeometry(item.Geometry,item.Transform,item.Source,experiment=="occluding-objects"))throw new InvalidOperationException("HLD rejected geometry");
                double registrationSeconds=clock.Elapsed.TotalSeconds;
                bool threads=experiment.StartsWith("threads",StringComparison.Ordinal)||experiment=="make2d-like";
                clock.Restart();var progress=new HldProgress(clock,Path.Combine(output,experiment+"-progress.json"));
                using var hld=HiddenLineDrawing.Compute(parametersHld,threads,progress,default);
                double seconds=clock.Elapsed.TotalSeconds;
                if(hld is null)throw new InvalidOperationException("HLD returned null");
                File.WriteAllText(Path.Combine(output,experiment+"-compute.json"),JsonSerializer.Serialize(new{model,experiment,seconds,threads,progress.First99Seconds}));
                clock.Restart();
                var curves=new List<object>();var counts=new Dictionary<string,int>();int tessellationFailures=0;
                bool skipTessellation=System.Environment.GetEnvironmentVariable("HLD_SKIP_TESSELLATION")=="1";
                using var archive=new Rhino.FileIO.File3dm();
                foreach(var segment in hld.Segments)
                {
                    string visibility=segment.SegmentVisibility.ToString();counts[visibility]=counts.GetValueOrDefault(visibility)+1;
                    if(segment.SegmentVisibility is not (HiddenLineDrawingSegment.Visibility.Visible or HiddenLineDrawingSegment.Visibility.Hidden))continue;
                    if(segment.CurveGeometry is not {} proxy)continue;
                    using var curve=proxy.DuplicateCurve();curve.Transform(Transform.PlanarProjection(Plane.WorldXY));
                    double[] Points(Curve c)
                    {
                        if(!c.TryGetPolyline(out var poly))
                        {
                            using var p=c.ToPolyline(scene.Tolerance/16,.02,0,0);
                            if(p is null||!p.TryGetPolyline(out poly))
                            {
                                tessellationFailures++;
                                return Enumerable.Range(0,33).SelectMany(i=>{var q=c.PointAt(c.Domain.ParameterAt(i/32d));return new[]{q.X,q.Y};}).ToArray();
                            }
                        }
                        return poly.SelectMany(p=>new[]{p.X,p.Y}).ToArray();
                    }
                    string source=segment.ParentCurve?.SourceObject?.Tag as string??"unknown";
                    var attributes=new ObjectAttributes {Name=source};attributes.SetUserString("visibility",visibility);
                    archive.Objects.AddCurve(curve,attributes);
                    if(!skipTessellation)curves.Add(new {visibility,source,length=curve.GetLength(),points=Points(curve)});
                }
                archive.Write(Path.Combine(output,experiment+".3dm"),8);
                double extractionSeconds=clock.Elapsed.TotalSeconds;
                File.WriteAllText(file,JsonSerializer.Serialize(new{model,experiment,seconds,captured,preparation,geometrySeconds,registrationSeconds,extractionSeconds,
                    modelHash=WorkerCodec.Hash(File.ReadAllBytes(model)),fingerprint=WorkerCodec.Fingerprint(),sourceCount=sources.Count,registered=registrations.Count,
                    parametersHld.AbsoluteTolerance,parametersHld.Flatten,parametersHld.IncludeHiddenCurves,parametersHld.IncludeTangentEdges,parametersHld.IncludeTangentSeams,parametersHld.OccludingSectionOption,threads,
                    progress.First99Seconds,counts,tessellationFailures,skipTessellation,curves}));
                Console.WriteLine($"{Path.GetFileName(model)} {experiment}: {seconds:F3}s; 99% at {progress.First99Seconds:F3}s; {counts.GetValueOrDefault("Visible")+counts.GetValueOrDefault("Hidden")} curves");
            }
            finally {foreach(var geometry in owned)geometry.Dispose();}
        }
    }

    private sealed class HldProgress(Stopwatch clock,string path):IProgress<double>
    {
        private long _last;internal double? First99Seconds;
        public void Report(double value)
        {
            lock(this)
            {
                if(value>=.99&&First99Seconds is null)First99Seconds=clock.Elapsed.TotalSeconds;
                long now=Stopwatch.GetTimestamp();if(Stopwatch.GetElapsedTime(_last,now).TotalSeconds<1)return;_last=now;
                File.WriteAllText(path,JsonSerializer.Serialize(new{seconds=clock.Elapsed.TotalSeconds,progress=value,First99Seconds}));
            }
        }
    }

    private sealed class CurveIndex:IDisposable
    {
        private readonly RTree _tree=new();
        private readonly List<Curve> _curves=[];
        internal CurveIndex(Rhino.FileIO.File3dm archive,string visibility)
        {
            foreach(var obj in archive.Objects)
                if(obj.Geometry is Curve curve&&obj.Attributes.GetUserString("visibility")==visibility)
                {
                    var bounds=curve.GetBoundingBox(true);bounds.Inflate(1e-6);
                    _tree.Insert(bounds,_curves.Count);_curves.Add(curve);
                }
        }
        internal double Closest(Point3d point,double radius,out Point3d closest)
        {
            var ids=new List<int>();
            var offset=new Vector3d(radius,radius,radius);
            _tree.Search(new BoundingBox(point-offset,point+offset),(_,a)=>ids.Add(a.Id));
            double distance=double.PositiveInfinity;closest=Point3d.Unset;
            foreach(int id in ids)
                if(_curves[id].ClosestPoint(point,out double t))
                {
                    var candidate=_curves[id].PointAt(t);double d=candidate.DistanceTo(point);
                    if(d<distance){distance=d;closest=candidate;}
                }
            return distance;
        }
        public void Dispose()=>_tree.Dispose();
    }
}
