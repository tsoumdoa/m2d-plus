using M2DPlus.Core;
using M2DPlus.Core.Visibility;
using M2DPlus.Rhino.Services;
using Rhino;
using Rhino.Geometry;
using Xunit;

namespace M2DPlus.Rhino.Tests;

[Collection("Rhino")]
[Trait("Category", "RhinoIntegration")]
public sealed class ContactDrawingTests(RhinoHost host)
{
    private static Brep Box(double x0,double y0,double z0,double x1,double y1,double z1) =>
        Brep.CreateFromBox(new BoundingBox(new Point3d(x0,y0,z0),new Point3d(x1,y1,z1)));
    private static EffectiveSettings SettingsFor(bool joints,double tolerance) => Settings.Resolve(
        [new("contact","Top",ViewSourceKind.Direction,false,10000,tolerance)],
        [[$"joints={(joints ? "on" : "off")}","hidden=on","beyond=off"]],DocumentUnits.Metres).Views[0];
    private static ViewDrawing Draw(GeometryBase[] objects,bool joints,double tolerance=1e-6,Plane? plane=null)
    {
        var result=new ViewDrawing { Frame=new Rectangle3d(plane??Plane.WorldXY,new Interval(-10000,10000),new Interval(-10000,10000)),Tolerance=tolerance };
        var input=objects.Select((g,i)=>new VisibilityInput(g,new PreparedLeaf(i.ToString(),[i],g))).ToArray();
        ComposedVisibility.Solve(input,result,ViewRequest.Parse("Top") with { Settings=SettingsFor(joints,tolerance) },new(),default);
        return result;
    }
    private static DrawingPiece[] At(ViewDrawing drawing,Point3d point) => drawing.Items.Where(p=>
    {
        using var geometry=p.CopyGeometry();return geometry is Curve c && c.ClosestPoint(point,out var t,1e-7) && c.PointAt(t).DistanceTo(point)<1e-7;
    }).ToArray();

    [RhinoTheory] [InlineData(1)] [InlineData(1000)]
    public void FlushPanelsHaveOneStableJointAndNoJointWithOff(double scale) => host.Run(()=>
    {
        using var a=Box(-2,0,0,0,2,1);using var b=Box(0,0,0,2,2,1);
        a.Transform(Transform.Scale(Point3d.Origin,scale));b.Transform(Transform.Scale(Point3d.Origin,scale));
        using var on=Draw([a,b],true,1e-6*scale);using var off=Draw([a,b],false,1e-6*scale);
        var joint=Assert.Single(At(on,new(0,scale,0)));Assert.Equal(LineClass.Visible,joint.Class);Assert.Equal("0",joint.SourceIdentity);
        Assert.Empty(At(off,new(0,scale,0)));Assert.Equal(0,on.NativeCalls);Assert.Equal(0,off.NativeCalls);
        using var repeat=Draw([a,b],true,1e-6*scale);
        Assert.Equal(WorkerCodec.CaptureDrawing(on,true),WorkerCodec.CaptureDrawing(repeat,true));
    });

    [RhinoTheory] [InlineData(1)] [InlineData(1000)]
    public void StackedWallJointsAndAxonometricBearingEdgesObeyTheRules(double scale) => host.Run(()=>
    {
        var plane=new Plane(Point3d.Origin,Vector3d.XAxis,Vector3d.ZAxis);
        using var lower=Box(0,0,0,2,2,1);using var upper=Box(0,0,1,2,2,2);
        var transform=Transform.Scale(Point3d.Origin,scale);lower.Transform(transform);upper.Transform(transform);
        using var on=Draw([lower,upper],true,1e-6*scale,plane);
        using var off=Draw([lower,upper],false,1e-6*scale,plane);
        var joint=Assert.Single(At(on,new(scale,0,scale)));
        Assert.Equal(LineClass.Visible,joint.Class);Assert.Equal("0",joint.SourceIdentity);
        Assert.Empty(At(off,new(scale,0,scale)));
        using var slab=Box(-1,-1,-1,3,2,0);using var wall=Box(0,0,0,2,.2,2);
        using var occluder=Box(-1,-3,-.5,4,-.5,4);
        slab.Transform(transform);wall.Transform(transform);occluder.Transform(transform);
        var axon=new Plane(Point3d.Origin,new Vector3d(1,-2,1.5));var foot=new Point3d(scale,0,0);
        using var seated=Draw([wall,slab],false,1e-6*scale,axon);
        using var hidden=Draw([wall,slab,occluder],false,1e-6*scale,axon);
        var bearing=Assert.Single(At(seated,axon.ClosestPoint(foot)));
        Assert.Equal(LineClass.Visible,bearing.Class);Assert.Equal("0",bearing.SourceIdentity);
        var support=Assert.IsAssignableFrom<Curve>(bearing.DepthGeometry);
        Assert.True(support.ClosestPoint(foot,out var t));Assert.True(support.PointAt(t).DistanceTo(foot)<1e-7*scale);
        Assert.Equal(LineClass.Hidden,Assert.Single(At(hidden,axon.ClosestPoint(foot))).Class);
    });

    [RhinoFact] public void CoincidentBlocksAreDrawnOnceWithoutSuppressingTheirOutline() => host.Run(()=>
    {
        using var a=Box(0,0,0,2,2,1);using var b=a.DuplicateBrep();
        using var result=Draw([a,b],false);
        Assert.Equal(LineClass.Visible,Assert.Single(At(result,new(1,0,0))).Class);
        Assert.Equal("0",Assert.Single(At(result,new(1,0,0))).SourceIdentity);
        Assert.Equal(8,result.Items.Where(p=>p.Class==LineClass.Visible).Sum(p=>{using var g=p.CopyGeometry();return ((Curve)g).GetLength();}),8);
    });

    [RhinoFact] public void MirroredBlockNormalsPreserveFlushContinuity() => host.Run(()=>
    {
        using var doc=RhinoDoc.CreateHeadless(null);doc.ModelAbsoluteTolerance=1e-6;
        using var a=Box(0,0,0,2,2,1);using var b=Box(0,0,0,2,2,1);
        int definition=doc.InstanceDefinitions.Add("contact mirror","",Point3d.Origin,[a],[new global::Rhino.DocObjects.ObjectAttributes()]);
        doc.Objects.AddInstanceObject(definition,Transform.Mirror(Plane.WorldYZ));doc.Objects.AddBrep(b);
        using var cache=new SceneCache();using var lease=BaselineTests.Capture(doc,cache);
        var prepared=lease.Value.Prepare(default);Assert.Equal(2,prepared.Count);
        var scene=new PlanarExtraction();
        foreach(var leaf in prepared)
        {
            Assert.Equal(BrepSolidOrientation.Outward,Assert.IsType<Brep>(leaf.Geometry).SolidOrientation);
            Assert.True(scene.Add(leaf.Geometry,Transform.Identity,Plane.WorldXY,1e-6,leaf.Identity));
        }
        Assert.All(scene.Faces,f=>{Assert.NotNull(f.OutwardNormal);Assert.NotNull(f.WorldOutwardNormal);});
        var result=EdgeFaceKernel.Solve(scene.Faces,scene.Edges,new(1e-6) {Joints=false});
        Assert.Contains(result.Contacts,c=>c.Kind==ContactKind.Flush);
        Assert.DoesNotContain(result.Intervals,i=>{
            var e=scene.Edges[i.Edge];return e.A.X==0&&e.B.X==0&&e.A.Z==1&&e.B.Z==1;
        });
        var identity=new RequestIdentity(lease.Value.Revision,"mirror","joints",1);
        using var on=NativeDrawing.SolveBorrowedAsync(lease.Value,identity,[ViewRequest.Parse("Top") with {Settings=SettingsFor(true,1e-6)}],new(),default,null).GetAwaiter().GetResult();
        using var off=NativeDrawing.SolveBorrowedAsync(lease.Value,identity,[ViewRequest.Parse("Top") with {Settings=SettingsFor(false,1e-6)}],new(),default,null).GetAwaiter().GetResult();
        Assert.Single(At(on.Views[0],on.Views[0].Frame.Plane.ClosestPoint(new(0,1,1))));
        Assert.Empty(At(off.Views[0],off.Views[0].Frame.Plane.ClosestPoint(new(0,1,1))));
    });

    [RhinoTheory] [InlineData(false)] [InlineData(true)]
    public void OpenAndArbitrarilyOrientedGeometryNeverSuppliesOutwardNormalsOrDropsBearingEdges(bool flip) => host.Run(()=>
    {
        using var bottom=Brep.CreateFromCornerPoints(new(0,0,0),new(2,0,0),new(2,.2,0),new(0,.2,0),1e-6);
        using var upright=Brep.CreateFromCornerPoints(new(0,0,0),new(2,0,0),new(2,0,2),new(0,0,2),1e-6);
        using var wall=Assert.Single(Brep.JoinBreps([bottom,upright],1e-6));
        var bottomFace=wall.Faces.Single(f=>{f.TryGetPlane(out var p);return Math.Abs(p.Normal.Z)>.9;});
        bottomFace.TryGetPlane(out var support);
        if((bottomFace.OrientationIsReversed?-support.Normal:support.Normal).Z<0)wall.Flip();
        if(flip)wall.Flip();
        using var surface=bottom.Surfaces[0].Duplicate();
        using var mesh=new Mesh();mesh.Vertices.Add(0,0,0);mesh.Vertices.Add(2,0,0);mesh.Vertices.Add(2,.2,0);mesh.Vertices.Add(0,.2,0);
        mesh.Faces.AddFace(0,1,2,3);if(flip)mesh.Flip(true,true,true);
        using var inward=Box(0,0,0,2,2,1);inward.Flip();
        foreach(var geometry in new GeometryBase[]{wall,surface,mesh,inward})
        {
            var extracted=new PlanarExtraction();Assert.True(extracted.Add(geometry,Transform.Identity,Plane.WorldXY,1e-6,"open"));
            Assert.All(extracted.Faces,f=>{Assert.Null(f.OutwardNormal);Assert.Null(f.WorldOutwardNormal);});
        }
        using var slab=Box(-1,-1,-1,3,2,0);
        var axon=new Plane(Point3d.Origin,new Vector3d(1,-2,1.5));
        using var drawing=Draw([wall,slab],false,1e-6,axon);
        var bearing=Assert.Single(At(drawing,axon.ClosestPoint(new(1,0,0))));
        Assert.Equal(LineClass.Visible,bearing.Class);Assert.Equal("0",bearing.SourceIdentity);
    });

    [RhinoFact] public void SectionCutsAndHatchesThroughFlushAndSeatedContactsIgnoreJoints() => host.Run(()=>
    {
        using var doc=RhinoDoc.CreateHeadless(null);doc.ModelAbsoluteTolerance=1e-6;
        using var a=Box(-2,0,0,0,2,2);using var b=Box(0,0,0,2,2,2);using var slab=Box(-3,-1,-1,3,3,0);
        doc.Objects.AddBrep(a);doc.Objects.AddBrep(b);doc.Objects.AddBrep(slab);
        using var cache=new SceneCache();using var lease=BaselineTests.Capture(doc,cache);
        ViewRequest Section(bool joints)
        {
            var v=ViewRequest.Parse(new Plane(new Point3d(0,1,0),Vector3d.XAxis,Vector3d.ZAxis));
            return v with {Settings=Settings.Resolve([new("section",v.Name,v.Kind,true,20,1e-6)],
                [[$"joints={(joints?"on":"off")}","hidden=on","beyond=off","depth=5","hatch=black"]],DocumentUnits.Metres).Views[0]};
        }
        var identity=new RequestIdentity(lease.Value.Revision,"contact section","joints",1);
        using var on=NativeDrawing.SolveBorrowedAsync(lease.Value,identity,[Section(true)],new(),default,null).GetAwaiter().GetResult();
        using var off=NativeDrawing.SolveBorrowedAsync(lease.Value,identity,[Section(false)],new(),default,null).GetAwaiter().GetResult();
        Assert.Null(on.Views[0].Error);Assert.Null(off.Views[0].Error);
        Assert.Equal(3,on.Views[0].Regions.Count);Assert.All(on.Views[0].Regions,r=>Assert.NotNull(r.Region.Hatch));
        byte[] Regions(ViewDrawing d)=>WorkerCodec.Encode(WorkerCodec.Decode<WorkerCodec.Drawing>(WorkerCodec.CaptureDrawing(d,true)).Regions);
        Assert.Equal(Regions(on.Views[0]),Regions(off.Views[0]));
    });

    [RhinoFact] public void FastStillRunsPlanarJointVisibilityAndMustKeepItsCacheKey() => host.Run(()=>
    {
        using var doc=RhinoDoc.CreateHeadless(null);doc.ModelAbsoluteTolerance=1e-6;
        using var a=Box(-2,0,0,0,2,1);using var b=Box(0,0,0,2,2,1);doc.Objects.AddBrep(a);doc.Objects.AddBrep(b);
        using var cache=new SceneCache();using var lease=BaselineTests.Capture(doc,cache);
        ViewRequest View(bool joints)=>ViewRequest.Parse("Top") with {Settings=Settings.Resolve([new("fast","Top",ViewSourceKind.Direction,false,10,1e-6)],
            [["engine=fast",$"joints={(joints?"on":"off")}","hidden=on","beyond=off"]],DocumentUnits.Metres).Views[0]};
        Assert.NotEqual(View(true).Settings!.VisibilityKey(),View(false).Settings!.VisibilityKey());
        using var drawings=new DrawingCache();var identity=new RequestIdentity(lease.Value.Revision,"fast contact","joints",1);
        using var on=drawings.SolveAsync(BaselineTests.Capture(doc,cache),identity,[View(true)],new(),[],default).GetAwaiter().GetResult();
        using var off=drawings.SolveAsync(BaselineTests.Capture(doc,cache),identity,[View(false)],new(),[],default).GetAwaiter().GetResult();
        Assert.Single(At(on.Views[0],on.Views[0].Frame.Plane.ClosestPoint(new(0,1,1))));
        Assert.Empty(At(off.Views[0],off.Views[0].Frame.Plane.ClosestPoint(new(0,1,1))));
    });

    [RhinoFact] public void CutOnlyJointsChangesReuseTheVisibilityCache() => host.Run(()=>
    {
        using var doc=RhinoDoc.CreateHeadless(null);doc.ModelAbsoluteTolerance=1e-6;
        using var box=Box(-1,-1,-1,1,1,1);doc.Objects.AddBrep(box);
        using var cache=new SceneCache();using var lease=BaselineTests.Capture(doc,cache);using var drawings=new DrawingCache();
        ViewRequest View(bool joints)=>ViewRequest.Parse(Plane.WorldXY) with {Settings=Settings.Resolve(
            [new("cut","cut",ViewSourceKind.Plane,true,10,1e-6)],
            [["cutonly=on",$"joints={(joints?"on":"off")}"]],DocumentUnits.Metres).Views[0]};
        var identity=new RequestIdentity(lease.Value.Revision,"cut contact","joints",1);
        using var on=drawings.SolveAsync(BaselineTests.Capture(doc,cache),identity,[View(true)],new(),[],default).GetAwaiter().GetResult();
        var solves=NativeDrawing.VisibilitySolves;
        using var off=drawings.SolveAsync(BaselineTests.Capture(doc,cache),identity,[View(false)],new(),[],default).GetAwaiter().GetResult();
        Assert.Equal(solves,NativeDrawing.VisibilitySolves);Assert.Null(off.Views[0].Error);
        Assert.Equal(on.Views[0].Regions.Count,off.Views[0].Regions.Count);
    });

    [RhinoFact] public void WallOnSlabAndTJunctionRetainSeatedContactsAndSplitFlushContacts() => host.Run(()=>
    {
        using var slab=Box(-1,-1,-1,3,2,0);using var wall=Box(0,0,0,2,.2,2);
        var extraction=new PlanarExtraction();Assert.True(extraction.Add(wall,Transform.Identity,Plane.WorldXY,1e-6,"wall"));
        Assert.True(extraction.Add(slab,Transform.Identity,Plane.WorldXY,1e-6,"slab"));
        var kernel=EdgeFaceKernel.Solve(extraction.Faces,extraction.Edges,new(1e-6) { Joints=false });
        Assert.Contains(kernel.Contacts,c=>c.Kind==ContactKind.Seated);
        Assert.DoesNotContain(kernel.Contacts,c=>c.Kind==ContactKind.Flush);
        using var a=Box(-2,0,0,0,2,1);using var b=Box(0,.5,0,2,1.5,1);
        using var on=Draw([a,b],true);using var off=Draw([a,b],false);
        Assert.Single(At(on,new(0,1,0)));Assert.Empty(At(off,new(0,1,0)));
        Assert.Single(At(off,new(0,.25,0)));Assert.Single(At(off,new(0,1.75,0)));
    });

    [RhinoFact] public void NearFlushOverToleranceAndRotatedGeometryDoNotReceiveAFalseFlushProof() => host.Run(()=>
    {
        using var a=Box(-2,0,0,0,2,1);using var b=Box(0,0,.00101,2,2,1.00101);
        var extraction=new PlanarExtraction();extraction.Add(a,Transform.Identity,Plane.WorldXY,.001,"A");extraction.Add(b,Transform.Identity,Plane.WorldXY,.001,"B");
        var kernel=EdgeFaceKernel.Solve(extraction.Faces,extraction.Edges,new(.001) { Joints=false });
        Assert.DoesNotContain(kernel.Contacts,c=>c.Kind==ContactKind.Flush);
        using var c=Box(0,0,0,2,2,1);
        var rotation=Transform.Rotation(.37,Vector3d.ZAxis,Point3d.Origin);a.Transform(rotation);c.Transform(rotation);
        using var first=Draw([a,c],true);using var second=Draw([a,c],true);
        Assert.Equal(WorkerCodec.CaptureDrawing(first,true),WorkerCodec.CaptureDrawing(second,true));
        Assert.Null(first.Error);
    });

    [RhinoFact] public void NativeOnlyContactsDiscloseTheirUncertifiedTieBehaviour() => host.Run(()=>
    {
        // An ellipsoid stays on native HLD; a sphere would be an exact analytic solid.
        using var surface=NurbsSurface.CreateFromSphere(new Sphere(Point3d.Origin,1));
        Assert.True(surface.Transform(Transform.Scale(Plane.WorldXY,1,1,1.5)));
        using var ellipsoid=surface.ToBrep();
        using var drawing=Draw([ellipsoid],false);
        Assert.Contains(drawing.Diagnostics,d=>d.Contains("Native/freeform contact classification is not certified",StringComparison.Ordinal));
    });

    [RhinoFact] public void ContactSettingsAndResultsAreByteIdenticalInPersistentWorkers() => host.Run(()=>
    {
        using var doc=RhinoDoc.CreateHeadless(null);doc.ModelAbsoluteTolerance=1e-6;
        using var a=Box(-2,0,0,0,2,1);using var b=Box(0,0,0,2,2,1);doc.Objects.AddBrep(a);doc.Objects.AddBrep(b);
        using var cache=new SceneCache();using var lease=BaselineTests.Capture(doc,cache);
        ViewRequest[] views=[ViewRequest.Parse("Top") with {Settings=SettingsFor(true,1e-6)},ViewRequest.Parse("Top") with {Settings=SettingsFor(false,1e-6)}];
        var identity=new RequestIdentity(lease.Value.Revision,"contacts","joints",1);
        using var local=NativeDrawing.SolveBorrowedAsync(lease.Value,identity,views,new(),default,null).GetAwaiter().GetResult();
        using var pool=new DrawingWorkerPool(new() {Count=1,Directory=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../src/M2DPlus.Worker/bin/Debug/net"+Environment.Version.Major+".0-windows"))});
        using var worker=pool.SolveAsync(lease.Value,identity,views,new(),default).GetAwaiter().GetResult();
        using var repeat=pool.SolveAsync(lease.Value,identity,views,new(),default).GetAwaiter().GetResult();
        for(int i=0;i<views.Length;i++)
        {
            Assert.Null(worker.Views[i].Error);
            Assert.Equal(WorkerCodec.CaptureDrawing(local.Views[i],true),WorkerCodec.CaptureDrawing(worker.Views[i],true));
            Assert.Equal(WorkerCodec.CaptureDrawing(worker.Views[i],true),WorkerCodec.CaptureDrawing(repeat.Views[i],true));
        }
        Assert.Single(At(worker.Views[0],worker.Views[0].Frame.Plane.ClosestPoint(new(0,1,1))));
        Assert.Empty(At(worker.Views[1],worker.Views[1].Frame.Plane.ClosestPoint(new(0,1,1))));
    });
}
