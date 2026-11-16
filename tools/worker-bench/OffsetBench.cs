using System.Diagnostics;
using M2DPlus.Core;
using M2DPlus.Core.Sections;
using M2DPlus.Rhino.Sections;
using M2DPlus.Rhino.Services;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

internal static class OffsetBench
{
    private static Brep Box(double x, double y, double z) => Brep.CreateFromBox(new BoundingBox(Point3d.Origin,new Point3d(x,y,z)));
    private static Brep Stairs()
    {
        var points=new List<Point3d>{Point3d.Origin,new(0,3000,0),new(0,3000,1800)};
        for(var i=9;i>=0;i--){points.Add(new(0,i*300,(i+1)*180));points.Add(new(0,i*300,i*180));}
        using var profile=new PolylineCurve(points);using var surface=Surface.CreateExtrusion(profile,Vector3d.XAxis*1000);
        using var open=surface.ToBrep();open.Faces.SplitKinkyFaces();
        return open.CapPlanarHoles(.01) ?? throw new InvalidOperationException("Stair generation failed");
    }
    internal static void Run(string output, string mode)
    {
        Directory.CreateDirectory(output);
        using var core = new Rhino.Runtime.InProcess.RhinoCore(["/nosplash","/notemplate"],Rhino.Runtime.InProcess.WindowStyle.NoWindow);
        if(mode.StartsWith("profile:",StringComparison.Ordinal))
        {
            using var document=RhinoDoc.OpenHeadless(mode[8..]);using var profileCache=new SceneCache();using var profileWatch=new DocumentWatch(document);
            using var profileCapture=new SceneCapture(document,profileWatch,null,profileCache);while(!profileCapture.Step(TimeSpan.FromSeconds(1))){}
            using var profileLease=profileCapture.Accept();var prepared=profileLease.Value.Prepare(default);var rows=new List<object>();
            foreach(var source in prepared)
            {
                var timer=Stopwatch.StartNew();var verdict=SectionGeometry.Verdict(new(source.Identity,source.Geometry,source.Section.Style){Canonical=source.Canonical},.01,profileLease.Value.SectionVerdicts);
                var seconds=timer.Elapsed.TotalSeconds;
                rows.Add(new{source.Identity,type=source.Geometry.GetType().Name,bounds=source.Geometry.GetBoundingBox(true).ToString(),seconds,verdict});
                if(seconds>.1)Console.WriteLine($"{source.Identity}: {seconds:F3}s {source.Geometry.GetBoundingBox(true)} {verdict}");
                File.WriteAllBytes(Path.Combine(output,"validation.json"),WorkerCodec.Encode(rows));
            }
            return;
        }
        if(mode=="probe")
        {
            var rows=new List<object>();
            foreach(var offset in new[]{0d,1e5,1e6})
            foreach(var kind in new[]{"box","round","freeform"})
            {
                using var body=kind=="box"?Box(4000,200,3000):kind=="round"?new Cylinder(new Circle(200),3000).ToBrep(true,true):Brep.CreateFromSphere(new Sphere(new Point3d(0,0,1500),500));
                body.Transform(Transform.Translation(offset,offset,offset));
                var timer=Stopwatch.StartNew();
                using(var converted=SectionGeometry.Convert(body,.01)){}
                var validation=timer.Elapsed.TotalSeconds;timer.Restart();
                using var sections=SectionRegionEngine.Compute([new SectionSource(kind,body,SectionStyleSnapshot.Default())],new(new Plane(new Point3d(offset,offset,offset+1500),Vector3d.ZAxis),.01,.001),prepareVisibility:true);
                rows.Add(new{offset,kind,validation,sections=timer.Elapsed.TotalSeconds,regions=sections.Regions.Count,diagnostics=sections.Diagnostics});
                File.WriteAllBytes(Path.Combine(output,"probe.json"),WorkerCodec.Encode(rows));
                Console.WriteLine($"{offset} {kind}: validation={validation:F3}s sections={timer.Elapsed.TotalSeconds:F3}s");
            }
            return;
        }
        var shift=double.Parse(mode.Split(':')[0],System.Globalization.CultureInfo.InvariantCulture);
        var count=mode.Contains(':')?int.Parse(mode.Split(':')[1]):10;
        var model=Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile),".t3","m2d-plus-models","offset",$"building-1700-v2-{shift:G}.3dm");
        Directory.CreateDirectory(Path.GetDirectoryName(model)!);
        using var doc=RhinoDoc.CreateHeadless(null);doc.ModelUnitSystem=UnitSystem.Millimeters;doc.ModelAbsoluteTolerance=.01;
        using var wall=Box(4000,200,3000);using var slab=Box(4500,4500,200);using var column=Box(400,400,3000);
        using var round=new Cylinder(new Circle(200),3000).ToBrep(true,true);
        using var stair=Stairs();using var door=Box(900,60,2100);using var window=Box(1200,80,1200);
        using var freeform=Brep.CreateFromSphere(new Sphere(new Point3d(0,0,1500),500));
        freeform.Transform(Transform.Scale(Plane.WorldXY,1.3,.8,1.15)); // Closed rational freeform (triaxial ellipsoid).
        var shapes=new[]{wall,slab,column,round,stair,door,window,freeform};
        var definitions=shapes.Select((s,i)=>doc.InstanceDefinitions.Add("offset-shape-"+i,"",Point3d.Origin,[s],[new ObjectAttributes()])).ToArray();
        var kinds=new Dictionary<int,int>();
        for(var floor=0;floor<10;floor++) for(var cell=0;cell<17;cell++) for(var part=0;part<10;part++)
        {
            var kind=part switch{0 or 1 or 2=>0,3=>1,4=>2,5=>3,6=>cell==0?4:2,7=>5,8=>6,_=>floor==0&&cell<3?7:2};
            kinds[kind]=kinds.GetValueOrDefault(kind)+1;
            var x=shift+(cell%6)*6000;var y=shift+(cell/6)*6500;var z=shift+floor*3200;
            var placement=Transform.Translation(x+(part%3)*1100,y+(part/3)*500,z+(kind==6?900:kind==4?500:0));
            if(kind is 5 or 6)doc.Objects.AddInstanceObject(definitions[kind],placement);
            else {using var copy=shapes[kind].DuplicateBrep();copy.Transform(placement);doc.Objects.AddBrep(copy);}
        }
        if(!File.Exists(model))
        {
            var originModel=Path.Combine(Path.GetDirectoryName(model)!,"building-1700-v2-0.3dm");
            using var original=shift!=0&&File.Exists(originModel)?RhinoDoc.OpenHeadless(originModel):null;
            if(original is not null)
                foreach(var obj in original.Objects.GetObjectList(ObjectType.AnyObject).Where(o=>!o.IsInstanceDefinitionGeometry).ToArray())
                    if(original.Objects.Transform(obj.Id,Transform.Translation(shift,shift,shift),true)==Guid.Empty)throw new InvalidOperationException("Offset copy failed");
            if(!(original??doc).WriteFile(model,new Rhino.FileIO.FileWriteOptions{FileVersion=8}))throw new InvalidOperationException("Model save failed");
            File.WriteAllBytes(model+".generation.json",WorkerCodec.Encode(new{generator="tools/worker-bench/OffsetBench.cs",units="mm",tolerance=.01,shift,count=1700,kinds,source=original is not null?originModel:null,sha256=WorkerCodec.Hash(File.ReadAllBytes(model))}));
        }
        // Replay the saved model, including its stable source/definition ids. Never benchmark
        // fresh random identities against an earlier archive of an otherwise identical model.
        using var savedDocument=RhinoDoc.OpenHeadless(model);
        using var watch=new DocumentWatch(savedDocument);using var cache=new SceneCache();
        var clock=Stopwatch.StartNew();using var capture=new SceneCapture(savedDocument,watch,null,cache);while(!capture.Step(TimeSpan.FromSeconds(1))){}
        using var lease=capture.Accept();var scene=lease.Value;var captureSeconds=clock.Elapsed.TotalSeconds;
        var views=Enumerable.Range(0,count).Select(i=>ViewRequest.Parse(new Plane(new Point3d(shift,shift,shift+1500+i*3200),Vector3d.ZAxis)) with{Name="Plan "+i}).ToArray();
        var resolved=Settings.Resolve(views.Select((v,i)=>new ViewSettingsContext(i.ToString(),v.Name,v.Kind,true,scene.Bounds.Diagonal.Length,scene.Tolerance)).ToArray(),[["hidden = on"]],DocumentUnits.Millimetres);
        views=views.Select((v,i)=>v with{Settings=resolved.Views[i]}).ToArray();
        var identity=new RequestIdentity(scene.Revision,"offset-plans","default",1);
        File.WriteAllBytes(Path.Combine(output,"scene.json"),WorkerCodec.Capture(scene,default));
        File.WriteAllBytes(Path.Combine(output,"identity.json"),WorkerCodec.Encode(WorkerCodec.Fingerprint()));
        File.WriteAllBytes(Path.Combine(output,"cost.json"),WorkerCodec.Encode(scene.Cost));
        var timings=new List<object>();
        for(var i=0;i<views.Length;i++)
        {
            File.WriteAllBytes(Path.Combine(output,$"request-{i}.json"),WorkerCodec.Encode(new WorkerCodec.Request(identity,i,views[i],false)));
            clock.Restart();using var batch=NativeDrawing.SolveBorrowedAsync(scene,identity,[views[i]],new(){IsWorker=true,Scheduler=new(12L*1024*1024*1024)},default,null).GetAwaiter().GetResult();
            var seconds=clock.Elapsed.TotalSeconds;var view=batch.Views[0];
            var sectionClock=Stopwatch.StartNew();var nativeSectionCurves=0;
            foreach(var source in scene.Prepare(default).Where(s=>s.Geometry is Brep))
            {
                Rhino.Geometry.Intersect.Intersection.BrepPlane((Brep)source.Geometry,views[i].Cut!.Plane,scene.Tolerance,out var curves,out _);
                nativeSectionCurves+=curves.Length;foreach(var curve in curves)curve.Dispose();
            }
            var nativeSectionSeconds=sectionClock.Elapsed.TotalSeconds;
            var stable=WorkerCodec.CaptureDrawing(view,true);File.WriteAllBytes(Path.Combine(output,$"drawing-{i}.json"),stable);File.WriteAllText(Path.Combine(output,$"hash-{i}.txt"),WorkerCodec.Hash(stable));
            timings.Add(new{i,seconds,nativeSectionSeconds,nativeSectionCurves,view.Error,pieces=view.Items.Count,unresolved=view.Items.Count(p=>p.Class==LineClass.Unresolved),regions=view.Regions.Count,phases=view.Phases.Seconds,info=view.Info,verdicts=scene.SectionVerdicts.Computations,localValidations=scene.SectionVerdicts.LocalComputations});
            File.WriteAllBytes(Path.Combine(output,"offset.json"),WorkerCodec.Encode(new{model,shift,captureSeconds,fingerprint=WorkerCodec.Fingerprint(),scene.SourceCount,scene.Cost,timings}));
            Console.WriteLine($"Plan {i}: {seconds:F3}s; {view.Error}; {string.Join(", ",view.Phases.Seconds.Select(p=>$"{p.Key}={p.Value:F3}"))}");
        }
    }
}

