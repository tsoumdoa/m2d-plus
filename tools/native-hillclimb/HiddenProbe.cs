using System.Diagnostics;
using System.Reflection;
using M2DPlus.Core;
using M2DPlus.Rhino.Services;
using Rhino;
using Rhino.DocObjects;
using Rhino.FileIO;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;

namespace NativeHillclimb;
internal static class HiddenProbe
{
    internal static void Run(string model,string output)
    {
        var clock=Stopwatch.StartNew();
        using var doc=RhinoDoc.OpenHeadless(model); using var cache=new SceneCache(); using var watch=new DocumentWatch(doc);
        using var capture=new SceneCapture(doc,watch,cache:cache);
        while(!capture.Step(TimeSpan.FromSeconds(300))) { }
        using var lease=capture.Accept();var scene=lease.Value;var sources=scene.Prepare(default);
        var request=ViewRequest.Parse("North elevation",doc); if(request.Error is not null) request=ViewRequest.Parse("Back") with{Name="North elevation"};
        var method=typeof(NativeDrawing).GetMethod("SolveView",BindingFlags.NonPublic|BindingFlags.Static)!;
        ViewDrawing Draw(IReadOnlyList<PreparedLeaf> input,bool hidden,double? tolerance=null)
        {
            var ctx=new ViewSettingsContext("probe",request.Name,request.Kind,false,scene.Bounds.Diagonal.Length,tolerance??scene.Tolerance);
            var resolved=Settings.Resolve(new[]{ctx},new[]{new[]{$"hidden={(hidden?"on":"off")}","farclip=none","beyond=off","tangents=off","seams=off"}},DocumentUnits.Metres);
            using var ct=new CancellationTokenSource(TimeSpan.FromSeconds(20));
            return (ViewDrawing)method.Invoke(null,new object[]{scene,input,request with{Settings=resolved.Views[0]},new NativeSettings(false),ct.Token})!;
        }
        if(System.Environment.GetEnvironmentVariable("M2D_HIDDEN_PROBE_REUSE") is { } reuse)
        {
            using var previous=System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(reuse,"probe-result.json")));
            var ids=previous.RootElement.GetProperty("selected").EnumerateArray().Select(e=>e.GetProperty("Identity").GetString()).ToHashSet();
            var subset=sources.Where(q=>ids.Contains(q.Identity)).ToArray();
            var id=previous.RootElement.GetProperty("sourceId").GetString()!;
            using var saved=HiddenAudit.Load(Path.Combine(reuse,"hidden-on.3dm"),"Visible");
            var expected=previous.RootElement.GetProperty("targetLength").GetDouble();
            var candidate=saved.Items.Where(p=>p.SourceIdentity==id).First(p=>{using var g=p.CopyGeometry();return g is Curve c && Math.Abs(c.GetLength()-expected)<scene.Tolerance*.01;});
            using var curve=(Curve)candidate.CopyGeometry();
            var rows=new List<object>();
            foreach(var factor in new[]{1d,.1,.01})
            {
                using var on=Draw(subset,true,scene.Tolerance*factor);using var off=Draw(subset,false,scene.Tolerance*factor);
                rows.Add(new{factor,nativeTolerance=scene.Tolerance*factor,onMissing=Missing(curve,on,scene.Tolerance),offMissing=Missing(curve,off,scene.Tolerance)});
            }
            Program.Write(Path.Combine(output,"tolerance-diagnostic.json"),new{warning="Diagnostic changes native tolerance; not an accepted fixed-settings optimization",rows,
                direction=new[]{request.Direction.X,request.Direction.Y,request.Direction.Z},rawOn=Raw(subset,true,scene,request,id,curve),rawOff=Raw(subset,false,scene,request,id,curve)});
            return;
        }
        using var originalOn=Draw(sources,true);using var originalOff=Draw(sources,false);
        using var a=Visible(originalOn);using var b=Visible(originalOff);
        var original=Locus.Compare(a,b,scene.Tolerance);Program.Write(Path.Combine(output,"original.json"),original);
        var first=original.Differences.FirstOrDefault(d=>d.Kind=="missing-locus" && d.UncoveredLength>scene.Tolerance*8);
        if(first is null) {Program.Write(Path.Combine(output,"probe-result.json"),new{reproduced=false,reason="No substantial missing visible curve"});return;}
        using var entire=a.Items[first.Index].CopyGeometry();var parameters=System.Text.Json.JsonSerializer.Deserialize<double[]>(first.Detail)!;
        using var target=((Curve)entire).Trim(new Interval(parameters[0],parameters[1]));
        var sourceId=first.Source; var frame=request.Resolve(scene.Bounds,scene.Tolerance);var project=Transform.PlanarProjection(frame.Plane);
        var box=target.GetBoundingBox(true);box.Inflate(scene.Tolerance*64);
        bool Intersects(BoundingBox x) => x.IsValid && x.Max.X>=box.Min.X && x.Min.X<=box.Max.X && x.Max.Y>=box.Min.Y && x.Min.Y<=box.Max.Y && x.Max.Z>=box.Min.Z && x.Min.Z<=box.Max.Z;
        var shortlist=sources.Where(s=>Intersects(s.Geometry.GetBoundingBox(project)) || s.Identity==sourceId).ToList();
        var trials=new List<object>(); var calls=0;
        bool Reproduces(IReadOnlyList<PreparedLeaf> set)
        {
            if(clock.Elapsed.TotalSeconds>180 || calls>=60) return false;
            calls++;
            using var on=Draw(set,true);using var off=Draw(set,false);
            var onMissing=Missing(target,on,scene.Tolerance); var offMissing=Missing(target,off,scene.Tolerance);
            var pass=on.Error is null && off.Error is null && onMissing<scene.Tolerance && offMissing>scene.Tolerance*8;
            trials.Add(new{calls,count=set.Count,onMissing,offMissing,pass});
            Program.Write(Path.Combine(output,"minimization.json"),trials); return pass;
        }
        var selected=Reproduces(shortlist)?shortlist:sources.ToList();
        for(var chunks=2;chunks<=32 && selected.Count>1 && clock.Elapsed.TotalSeconds<180;)
        {
            var width=(int)Math.Ceiling((double)selected.Count/chunks); var reduced=false;
            for(var start=0;start<selected.Count;start+=width)
            {
                var candidate=selected.Where((s,i)=>s.Identity==sourceId || i<start || i>=start+width).ToList();
                if(candidate.Count==selected.Count || candidate.Count==0) continue;
                if(Reproduces(candidate)) {selected=candidate;chunks=2;reduced=true;break;}
            }
            if(!reduced) chunks*=2;
        }
        using var finalOn=Draw(selected,true);using var finalOff=Draw(selected,false);
        Locus.Save(finalOn,Path.Combine(output,"hidden-on.3dm"));Locus.Save(finalOff,Path.Combine(output,"hidden-off.3dm"));
        using(var fixture=new File3dm())
        {
            fixture.Settings.ModelAbsoluteTolerance=scene.Tolerance;
            foreach(var source in selected)
            {using var attrs=new ObjectAttributes{Name=source.Identity}; attrs.SetUserString("layer",source.Section.LayerPath);fixture.Objects.Add(source.Geometry,attrs);}
            using var targetAttrs=new ObjectAttributes{Name="diagnostic-target"};targetAttrs.SetUserString("not-an-input","true");
            // Keep the diagnostic curve in a separate file, never an occluding input.
            fixture.Write(Path.Combine(output,"input.3dm"),8);
            using var marker=new File3dm();using var ownedTarget=target.ToNurbsCurve();ownedTarget.EnsurePrivateCopy();
            if(marker.Objects.AddCurve(ownedTarget,targetAttrs)==Guid.Empty || !marker.Write(Path.Combine(output,"target.3dm"),8)) throw new IOException("Target export failed");
        }
        var rawOn=Raw(selected,true,scene,request,sourceId,target);var rawOff=Raw(selected,false,scene,request,sourceId,target);
        Program.Write(Path.Combine(output,"probe-result.json"),new{reproduced=Missing(target,finalOn,scene.Tolerance)<scene.Tolerance && Missing(target,finalOff,scene.Tolerance)>scene.Tolerance*8,
            calls,seconds=clock.Elapsed.TotalSeconds,originalCount=sources.Count,shortlist=shortlist.Count,remaining=selected.Count,targetLength=target.GetLength(),sourceId,
            onMissing=Missing(target,finalOn,scene.Tolerance),offMissing=Missing(target,finalOff,scene.Tolerance),
            frame=new{origin=new[]{frame.Plane.OriginX,frame.Plane.OriginY,frame.Plane.OriginZ},x=new[]{frame.Plane.XAxis.X,frame.Plane.XAxis.Y,frame.Plane.XAxis.Z},y=new[]{frame.Plane.YAxis.X,frame.Plane.YAxis.Y,frame.Plane.YAxis.Z},sceneBounds=new[]{scene.Bounds.Min.X,scene.Bounds.Min.Y,scene.Bounds.Min.Z,scene.Bounds.Max.X,scene.Bounds.Max.Y,scene.Bounds.Max.Z}},
            selected=selected.Select(s=>new{s.Identity,layer=s.Section.LayerPath,type=s.Geometry.GetType().Name,faces=s.Geometry is Brep br?br.Faces.Count:0,bounds=s.Geometry.GetBoundingBox(true).ToString()}),rawOn,rawOff});
    }
    private static ViewDrawing Visible(ViewDrawing original)
    {
        var view=new ViewDrawing();foreach(var p in original.Items.Where(p=>p.Class==LineClass.Visible))
        {var geo=p.CopyGeometry();view.Pieces.Add(new(geo,new PreparedLeaf(p.SourceIdentity,new[]{0},geo),LineClass.Visible));}return view;
    }
    private static double Missing(Curve target,ViewDrawing view,double tol)
    {
        var spans=new List<Interval>();var bounds=target.GetBoundingBox(true);bounds.Inflate(tol);
        foreach(var p in view.Items.Where(p=>p.Class==LineClass.Visible))
        {
            using var g=p.CopyGeometry();if(g is not Curve c) continue;
            var b=c.GetBoundingBox(true); if(b.Max.X<bounds.Min.X||b.Min.X>bounds.Max.X||b.Max.Y<bounds.Min.Y||b.Min.Y>bounds.Max.Y||b.Max.Z<bounds.Min.Z||b.Min.Z>bounds.Max.Z)continue;
            using var hits=Intersection.CurveCurve(target,c,tol*.5,tol*.5);if(hits is not null)spans.AddRange(hits.Where(h=>h.IsOverlap).Select(h=>h.OverlapA));
        }
        var t=target.Domain.Min;double missing=0;
        foreach(var span in spans.OrderBy(s=>s.Min)){if(span.Min>t)missing+=target.GetLength(new Interval(t,span.Min));t=Math.Max(t,span.Max);}
        if(t<target.Domain.Max) missing+=target.GetLength(new Interval(t,target.Domain.Max));return missing;
    }
    private static object[] Raw(IReadOnlyList<PreparedLeaf> sources,bool hidden,PreparedScene scene,ViewRequest request,string sourceId,Curve target)
    {
        var frame=request.Resolve(scene.Bounds,scene.Tolerance);var radius=Math.Max(scene.Bounds.Diagonal.Length,1);
        using var vp=new ViewportInfo();vp.IsParallelProjection=true;vp.SetCameraLocation(frame.Plane.Origin+frame.Plane.ZAxis*(radius*4+10));vp.SetCameraDirection(request.Direction);vp.SetCameraUp(frame.Plane.YAxis);
        vp.SetFrustum(frame.X.Min,frame.X.Max,frame.Y.Min,frame.Y.Max,.001,radius*10+100);
        var parameters=new HiddenLineDrawingParameters{AbsoluteTolerance=scene.Tolerance,Flatten=false,IncludeHiddenCurves=hidden,IncludeTangentEdges=false,IncludeTangentSeams=false};parameters.SetViewport(vp);
        foreach(var s in sources)parameters.AddGeometry(s.Geometry,s);
        using var drawing=HiddenLineDrawing.Compute(parameters,false);var result=new List<object>();
        drawing.WorldToHiddenLine.TryGetInverse(out var inverse);
        var flat=Transform.PlanarProjection(frame.Plane)*inverse;
        foreach(var s in drawing.Segments)
        {
            if(s.SegmentVisibility==HiddenLineDrawingSegment.Visibility.Unset)continue;
            if(s.ParentCurve?.SourceObject?.Tag is not PreparedLeaf leaf || leaf.Identity!=sourceId)continue;
            if(s.CurveGeometry is not { } proxy) continue;
            using var projected=proxy.DuplicateCurve();projected.Transform(flat);
            using var overlap=Intersection.CurveCurve(projected,target,scene.Tolerance*.5,scene.Tolerance*.5);
            var intersects=overlap?.Any(h=>h.IsOverlap)==true;
            object? ray=null;
            if(intersects)
            {
                var hit=overlap!.First(h=>h.IsOverlap);var point=proxy.PointAt(hit.OverlapA.Mid);point.Transform(inverse);
                var direction=-request.Direction;
                var hits=Intersection.RayShoot(sources.Select(q=>q.Geometry),new Ray3d(point+direction*(scene.Tolerance*4),direction),1);
                var blockers=sources.Where(q=>q.Identity!=sourceId && Experiments.Box(q.Geometry,out _)).Select(q=>new {q.Identity,b=q.Geometry.GetBoundingBox(true)})
                    .Where(q=>point.Y<q.b.Min.Y && point.X>q.b.Min.X+scene.Tolerance && point.X<q.b.Max.X-scene.Tolerance && point.Z>q.b.Min.Z+scene.Tolerance && point.Z<q.b.Max.Z-scene.Tolerance).Select(q=>q.Identity).ToArray();
                var exact=new List<object>();
                using var line=new LineCurve(point+direction*(scene.Tolerance*4),point+direction*(radius*10));
                foreach(var q in sources)
                {
                    if(q.Geometry is not Brep brep)continue;
                    var success=Intersection.CurveBrep(line,brep,scene.Tolerance*.0001,out var overlaps,out var intersections);
                    exact.Add(new{q.Identity,success,points=intersections.Select(p=>new[]{p.X,p.Y,p.Z}).ToArray()});
                    foreach(var overlapCurve in overlaps)overlapCurve.Dispose();
                }
                ray=new {point=new[]{point.X,point.Y,point.Z},direction=new[]{direction.X,direction.Y,direction.Z},exactNorthBoxBlockers=blockers,brepRayHits=hits.Select(p=>new {p.GeometryIndex,p.BrepFaceIndex,point=new[]{p.Point.X,p.Point.Y,p.Point.Z}}).ToArray(),explicitToleranceBrepIntersections=exact};
            }
            var component=s.ParentCurve.SourceObjectComponentIndex;
            result.Add(new{visibility=s.SegmentVisibility.ToString(),componentType=component.ComponentIndexType.ToString(),componentIndex=component.Index,kind=s.ParentCurve.SilhouetteType.ToString(),domain=proxy.Domain.ToString(),length=proxy.GetLength(),targetOverlap=intersects,projectedStart=projected.PointAtStart.ToString(),projectedEnd=projected.PointAtEnd.ToString(),ray});
        }
        return result.ToArray();
    }
}



