using M2DPlus.Core;
using M2DPlus.Core.Sections;
using M2DPlus.Rhino.Services;
using Rhino.Geometry;
using Xunit;
using Point = Rhino.Geometry.Point;
using System.Text.Json;
using M2DPlus.Rhino.Sections;
using Rhino;

namespace M2DPlus.Rhino.Tests;

[Collection("Rhino")]
[Trait("Category", "RhinoIntegration")]
public sealed class UncertaintyReductionTests(RhinoHost host)
{
    [RhinoFact] public void RestoredWrongHiddenIsVetoedAndItsLengthIsNotLost() => host.Run(() =>
    {
        using var line=new LineCurve(new Point3d(-10,0,0),new Point3d(10,0,0));
        var source=new PreparedLeaf("line",[0],line);
        using var drawing=new ViewDrawing { Frame=new Rectangle3d(Plane.WorldXY,20,20),Tolerance=1e-6 };
        drawing.Pieces.Add(new(line.DuplicateCurve(),source,LineClass.Hidden,line.DuplicateCurve()));
        NativeUncertainty.ApplyFootprints(drawing,[source],false,[],[new(new Point3d(-1,-1,0),new Point3d(1,1,0))],1e-6,default);
        Assert.All(drawing.Items,p=>Assert.Equal(LineClass.Unresolved,p.Class));
        Assert.Equal(20,drawing.Items.Sum(p=>{using var g=p.CopyGeometry();return ((Curve)g).GetLength();}),8);
        Assert.Contains(drawing.Diagnostics,d=>d.Contains("false-hidden samples=",StringComparison.Ordinal) && !d.Contains("false-hidden samples=0;",StringComparison.Ordinal));
    });

    [RhinoFact] public void DenseMeshFootprintsHaveABoundedConservativeFallback() => host.Run(() =>
    {
        using var mesh=Mesh.CreateFromPlane(Plane.WorldXY,new(-10,10),new(-10,10),32,32);
        Assert.True(mesh.Faces.Count>SectionDrawing.MaximumMeshFootprints);
        var box=Assert.Single(SectionDrawing.SectionFootprints(mesh,Plane.WorldXY));
        Assert.All(mesh.Vertices,v=>Assert.True(box.Contains((Point3d)v)));
    });
    [RhinoTheory]
    [InlineData(true,"02ec87ed-4697-5ec8-8168-d13efed0a4b4",-2.3853466355070316,-2.992283317636817,4.639632959298169)]
    [InlineData(false,"b25cea92-867e-55b7-8b19-5037a96d08ef",-.9684725027918946,.2064561171318786,6.060678806885097)]
    public void RecoveredSectionsDoNotRestoreWrongNativeVisible(bool plan,string identity,double x,double y,double z) => host.Run(() =>
    {
        using var document=RhinoDoc.OpenHeadless(Path.Combine(AppContext.BaseDirectory,"fixtures","features","diagrid-shell.3dm"));
        using var cache=new SceneCache();using var lease=BaselineTests.Capture(document,cache);
        var bounds=lease.Value.Bounds;
        var plane=plan ? new Plane(new Point3d(0,0,bounds.Min.Z+Math.Min(1.2,bounds.Diagonal.Z*.4)),Vector3d.ZAxis) :
            new Plane(new Point3d(0,bounds.Center.Y,0),Vector3d.XAxis,Vector3d.ZAxis);
        using var batch=BaselineTests.Solve(lease,ViewRequest.Parse(plane));
        var view=Assert.Single(batch.Views);Assert.Null(view.Error);Assert.NotEmpty(view.Regions);
        // Archived independent five-ray witnesses have positive gaps on every
        // ray (minimum .061 m plan / .206 m long). A better section certificate
        // must never re-authorize this native Visible locus.
        var point=plane.ClosestPoint(new Point3d(x,y,z));
        Assert.DoesNotContain(view.Items,p=>
        {
            if(p.Class!=LineClass.Visible || p.SourceIdentity!=identity)return false;
            using var geometry=p.CopyGeometry();
            return geometry is Curve c && c.ClosestPoint(point,out var t) && c.PointAt(t).DistanceTo(point)<=2e-4;
        });
        Assert.Contains(view.Diagnostics,d=>d.StartsWith("Section recovery visibility proofs:",StringComparison.Ordinal));
        if(!plan)
        {
            var node=Assert.Single(view.Regions,r=>r.Region.SourceIds.Contains("af9b7ed5-f639-5199-9492-5e230b4147d5")).Region;
            Assert.InRange(Math.Abs(node.Area-Math.PI*.16*.16),0,2e-6);Assert.Single(node.Loops);Assert.NotNull(node.Hatch);
        }
    });

    [RhinoFact] public void MeshFootprintsCoverFacesWithoutQuarantiningTheSpaceBetweenPanels() => host.Run(() =>
    {
        using var mesh = new Mesh();
        foreach (var x in new[] { -5d,5d })
        {
            var i=mesh.Vertices.Count;
            mesh.Vertices.Add(x,0,-1);mesh.Vertices.Add(x+1,0,1);mesh.Vertices.Add(x,1,1);
            mesh.Faces.AddFace(i,i+1,i+2);
        }
        var boxes=SectionDrawing.SectionFootprints(mesh,Plane.WorldXY);
        Assert.Equal(2,boxes.Length);
        Assert.DoesNotContain(boxes,b=>b.Contains(Point3d.Origin));
        foreach (var f in mesh.Faces)
            foreach(var i in new[]{f.A,f.B,f.C})
                Assert.Contains(boxes,b=>b.Contains(Plane.WorldXY.ClosestPoint(mesh.Vertices[i])));
        using var removed=mesh.DuplicateMesh();removed.Transform(Transform.Translation(0,0,3));
        using var clip=PhysicalSectionClipper.Clip(new("mesh",removed,SectionStyleSnapshot.Default()),new(Plane.WorldXY,1e-4,.001));
        Assert.False(clip.Succeeded);Assert.Empty(clip.Pieces);Assert.Empty(clip.ContactFeatures);
        using var crossing=PhysicalSectionClipper.Clip(new("mesh",mesh,SectionStyleSnapshot.Default()),new(Plane.WorldXY,1e-4,.001));
        Assert.False(crossing.Succeeded);Assert.Empty(crossing.Pieces);
        using var line=new LineCurve(new Point3d(-1,0,0),new Point3d(1,0,0));
        using var body=new Sphere(new Point3d(0,0,1),.5).ToBrep();
        var source=new PreparedLeaf("line",[0],line);
        using var drawing=new ViewDrawing{Frame=new Rectangle3d(Plane.WorldXY,10,10)};
        drawing.Pieces.Add(new(line.DuplicateCurve(),source,LineClass.Visible,line.DuplicateCurve()));
        NativeUncertainty.ApplyFootprints(drawing,[source,new("body",[1],body)],false,[],boxes,1e-4,default,
            proofRegions:[mesh.GetBoundingBox(Transform.PlanarProjection(Plane.WorldXY))]);
        Assert.All(drawing.Items,p=>Assert.Equal(LineClass.Unresolved,p.Class));
    });

    [RhinoFact] public void CertifiedMaterialRegionSurvivesAnotherSourcesAmbiguousSection() => host.Run(() =>
    {
        using var document = RhinoDoc.CreateHeadless(null); document.ModelAbsoluteTolerance = 1e-4;
        using var box = Brep.CreateFromBox(new BoundingBox(new Point3d(-2,-2,-1), new Point3d(2,2,1)));
        // A real near-tangency is inside the box's footprint, but cannot alter
        // the box's exact square material intersection at z=0.
        using var tangent = new Sphere(new Point3d(0,0,1-2e-4), 1).ToBrep();
        var inputs = new[] { new CaptureInput(box,[0],"certified"), new CaptureInput(tangent,[1],"ambiguous") };
        using var cache = new SceneCache(); using var lease = BaselineTests.Capture(document,cache,inputs);
        using var batch = BaselineTests.Solve(lease,ViewRequest.Parse(Plane.WorldXY));
        var view = Assert.Single(batch.Views); Assert.Null(view.Error);
        var region = Assert.Single(view.Regions).Region;
        Assert.Equal(new[]{"certified"},region.SourceIds); Assert.Equal(16,region.Area,8);
        Assert.Single(region.Loops); Assert.NotNull(region.Hatch);
        Assert.Contains(view.Diagnostics,d=>d.Contains("ambiguous: Uncertain:",StringComparison.Ordinal));
        Assert.DoesNotContain(view.Regions,r=>r.Region.SourceIds.Contains("ambiguous"));
    });

    [RhinoFact] public void FootprintsWithholdOnlyLocalIntervalsAndRetainDepthAndSolvedClass() => host.Run(() =>
    {
        using var line = new LineCurve(new Point3d(-10,0,0), new Point3d(10,0,0));
        using var depth = new LineCurve(new Point3d(-10,0,-3), new Point3d(10,0,-1));
        using var blocker = Brep.CreateFromBox(new BoundingBox(new Point3d(-11,-2,0),new Point3d(11,2,1)));
        depth.Domain = line.Domain;
        var source = new PreparedLeaf("line", [2], line);
        using var output = new ViewDrawing { Tolerance=1e-6,Frame=new Rectangle3d(Plane.WorldXY,20,20) };
        output.Pieces.Add(new(line.DuplicateCurve(), source, LineClass.Hidden, depth.DuplicateCurve()) { Profile = true, KernelOwned = true });
        var box = new BoundingBox(new Point3d(-1,-1,0),new Point3d(1,1,0));
        NativeUncertainty.ApplyFootprints(output,[source,new("blocker",[3],blocker)],false,[],[box],1e-6,default);
        Assert.Equal(3,output.Items.Count);
        Assert.Equal(20,output.Items.Sum(p => { using var g=p.CopyGeometry(); return ((Curve)g).GetLength(); }),9);
        Assert.Equal(2.000004,output.Items.Where(p => p.Class == LineClass.Unresolved).Sum(p => { using var g=p.CopyGeometry(); return ((Curve)g).GetLength(); }),8);
        Assert.All(output.Items,p =>
        {
            Assert.True(p.Profile); Assert.True(p.KernelOwned); Assert.Equal(LineClass.Hidden,p.SolvedClass);
            using var g=p.CopyGeometry(); var c=Assert.IsAssignableFrom<Curve>(g);
            var world=Assert.IsAssignableFrom<Curve>(p.DepthGeometry);
            Assert.Equal(c.Domain,world.Domain); Assert.Equal(-2+c.PointAtStart.X*.1,world.PointAtStart.Z,10);
            if(p.Class==LineClass.Hidden) Assert.False(NativeUncertainty.Affects(box,c.GetBoundingBox(true),1e-6));
        });
    });

    [RhinoFact] public void UnboundedSourceAndViewQuarantinesRemainConservativeAndPointsAreWithheld() => host.Run(() =>
    {
        using var line = new LineCurve(Point3d.Origin,new Point3d(10,0,0));
        var source = new PreparedLeaf("line",[0],line);
        foreach(bool wholeView in new[]{false,true})
        {
            using var output=new ViewDrawing();
            output.Pieces.Add(new(line.DuplicateCurve(),source,LineClass.Visible));
            output.Pieces.Add(new(new Point(new Point3d(8,0,0)),source,LineClass.Visible));
            NativeUncertainty.ApplyFootprints(output,[source],wholeView,wholeView ? [] : ["line"],[],1e-6,default);
            Assert.Equal(LineClass.Unresolved,Assert.Single(output.Items).Class);
        }
    });

    [RhinoFact] public void CurvedFootprintPartitionCoversTheWholeCurveAndEveryConfidentBoundIsDisjoint() => host.Run(() =>
    {
        using var curve=new Circle(Plane.WorldXY,4).ToNurbsCurve();
        var box=new BoundingBox(new Point3d(2,-2,0),new Point3d(5,2,0));
        var parts=NativeUncertainty.Partition(curve,[box],1e-6,default).ToArray();
        try
        {
            Assert.Contains(parts,p=>p.Affected); Assert.Contains(parts,p=>!p.Affected);
            Assert.InRange(Math.Abs(curve.GetLength(1e-10)-parts.Sum(p=>p.Curve.GetLength(1e-10))),0,1e-8);
            Assert.Equal(curve.Domain.Min,parts[0].Curve.Domain.Min); Assert.Equal(curve.Domain.Max,parts[^1].Curve.Domain.Max);
            Assert.All(parts.Zip(parts.Skip(1)),p=>Assert.Equal(p.First.Curve.Domain.Max,p.Second.Curve.Domain.Min));
            Assert.All(parts.Where(p=>!p.Affected),p=>Assert.False(NativeUncertainty.Affects(box,p.Curve.GetBoundingBox(true),1e-6)));
        }
        finally { foreach(var p in parts)p.Curve.Dispose(); }
        using var canceled=new CancellationTokenSource(); canceled.Cancel();
        Assert.Throws<OperationCanceledException>(()=>NativeUncertainty.Partition(curve,[box],1e-6,canceled.Token).ToArray());
    });

    [RhinoFact] public void DisjointUnsetRecordDoesNotRestoreVisibleWithoutAnAllBodyContinuousProof() => host.Run(() =>
    {
        using var line=new LineCurve(new Point3d(-2,0,0),new Point3d(2,0,0));
        using var occluder=new Sphere(new Point3d(0,0,1),.5).ToBrep();
        var source=new PreparedLeaf("line",[0],line); var body=new PreparedLeaf("sphere",[1],occluder);
        var offRecord=new BoundingBox(new Point3d(10,10,0),new Point3d(11,11,0));
        using var output=new ViewDrawing {Frame=new Rectangle3d(Plane.WorldXY,10,10)};
        output.Pieces.Add(new(line.DuplicateCurve(),source,LineClass.Visible,line.DuplicateCurve()));
        NativeUncertainty.ApplyFootprints(output,[source,body],false,[],[offRecord],1e-6,default,localSources:new HashSet<string>{"line"});
        Assert.All(output.Items,p=>Assert.Equal(LineClass.Unresolved,p.Class));
        var candidate=new BoundingBox(new Point3d(-2,0,0),new Point3d(2,0,0));
        Assert.False(NativeUncertainty.Clear(candidate,[occluder.GetBoundingBox(true)],1e-6));
        Assert.True(NativeUncertainty.Clear(candidate,[new BoundingBox(new Point3d(-1,-1,-2),new Point3d(1,1,0))],1e-6));
        Assert.False(NativeUncertainty.Clear(candidate,[BoundingBox.Unset],1e-6));
        Assert.True(NativeUncertainty.Clear(candidate,[offRecord],1e-6));
    });

    [RhinoFact] public void FailedNativeTrimRollsBackTheWholePartitionToUnresolvedWithoutLosingLength() => host.Run(() =>
    {
        using var line=new LineCurve(new Point3d(-10,0,0),new Point3d(10,0,0));
        var box=new BoundingBox(new Point3d(-1,-1,0),new Point3d(1,1,0));
        int calls=0;
        var parts=NativeUncertainty.Partition(line,[box],1e-6,default,
            (curve,a,b)=>++calls==2 ? null : curve.Trim(a,b)).ToArray();
        try
        {
            Assert.Equal(2,calls);
            var part=Assert.Single(parts);Assert.True(part.Affected);
            Assert.Equal(line.Domain,part.Curve.Domain);Assert.Equal(20,part.Curve.GetLength(),10);
        }
        finally { foreach(var part in parts)part.Curve.Dispose(); }
    });

    [RhinoFact] public void RationalWitnessCertificateHandlesDegreeAndKnotDifferencesButRejectsInteriorDepthCrossing() => host.Run(() =>
    {
        using var a=new Arc(new Point3d(-1,0,0),new Point3d(0,1,0),new Point3d(1,0,0)).ToNurbsCurve();
        using var b=a.ToNurbsCurve();
        b.IncreaseDegree(4); b.Knots.InsertKnot(b.Domain.Mid); b.Transform(Transform.Translation(0,0,1));
        Assert.True(ComposedVisibility.FrontWitness(a,b,Plane.WorldXY,1e-6));
        b.Transform(Transform.Translation(0,0,-2));
        Assert.False(ComposedVisibility.FrontWitness(a,b,Plane.WorldXY,1e-6));
        using var dip=a.ToNurbsCurve();
        // Endpoints agree, but positive rational weights expose a behind interior.
        var cp=dip.Points[1]; dip.Points.SetPoint(1,new Point3d(cp.Location.X,cp.Location.Y,-1),cp.Weight);
        Assert.False(ComposedVisibility.FrontWitness(a,dip,Plane.WorldXY,1e-6));
        using var shifted=a.ToNurbsCurve(); shifted.IncreaseDegree(4);
        shifted.Transform(Transform.Translation(1e-3,0,1));
        Assert.False(ComposedVisibility.FrontWitness(a,shifted,Plane.WorldXY,1e-6));
    });

    [RhinoFact] public void TiltedCurvedDuplicateHasAWholeSpanOwnerWithADifferentRationalBasis() => host.Run(() =>
    {
        using var arc=new Arc(new Point3d(-1,0,0),new Point3d(0,1,1),new Point3d(1,0,0)).ToNurbsCurve();
        using var witness=arc.ToNurbsCurve(); witness.IncreaseDegree(4); witness.Knots.InsertKnot(witness.Domain.Mid);
        witness.Transform(Transform.Translation(0,0,.1));
        var duplicate=new ComposedVisibility.NativeRecord(arc,new(new(arc,new("candidate",[0],arc))),LineClass.Unresolved,false,true);
        var owner=new ComposedVisibility.NativeRecord(witness,new(new(witness,new("owner",[1],witness))),LineClass.Visible,false,false);
        var owned=new List<GeometryBase>();
        try
        {
            var result=ComposedVisibility.ResolveDuplicates([duplicate],Plane.WorldXY,1e-6,owned,default,[owner]).ToArray();
            Assert.NotEmpty(result); Assert.All(result,p=>Assert.Equal(LineClass.Hidden,p.Class));
            Assert.InRange(Math.Abs(arc.GetLength(1e-10)-result.Sum(p=>p.World.GetLength(1e-10))),0,1e-8);
        }
        finally { foreach(var g in owned)g.Dispose(); }
    });

    [RhinoTheory]
    [InlineData("curved-facade","curved-mullion-2",2,.0025132741228718345)]
    [InlineData("diagrid-shell","diagrid-iso-0-0",1,.008595397500221673)]
    public void CertifiedHollowSectionsRetainAnalyticRegionsAcrossNativeAndComposedCandidateRoutes(string model,string name,int station,double area) => host.Run(() =>
    {
        using var file=global::Rhino.FileIO.File3dm.Read(Path.Combine(AppContext.BaseDirectory,"fixtures","features",model+".3dm"));
        using var definitions=JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"fixtures","view-cases","view-"+model+".json")));
        var spec=definitions.RootElement.GetProperty("views").EnumerateArray().Single(v=>v.GetProperty("name").GetString()==$"Normal {name} station {station}").GetProperty("plane");
        double[] V(string key)=>spec.GetProperty(key).EnumerateArray().Select(v=>v.GetDouble()).ToArray();
        var o=V("origin");var x=V("x");var y=V("y");
        var plane=new Plane(new Point3d(o[0],o[1],o[2]),new Vector3d(x[0],x[1],x[2]),new Vector3d(y[0],y[1],y[2]));
        using var shape=SectionGeometry.Convert(file.Objects.Single(obj=>obj.Attributes.Name==name).Geometry,1e-4);
        foreach(bool companion in new[]{false,true})
        {
            using var document=RhinoDoc.CreateHeadless(null); document.ModelAbsoluteTolerance=1e-4;
            document.Objects.AddBrep(shape);
            if(companion)
            {
                var p=plane.Origin-plane.Normal*.5+plane.XAxis*2;
                document.Objects.AddLine(p,p+plane.YAxis*.5); // Non-occluding kernel candidate, no material change.
            }
            using var cache=new SceneCache(); using var lease=BaselineTests.Capture(document,cache);
            var view=ViewRequest.Parse(plane);
            using var batch=NativeDrawing.SolveBorrowedAsync(lease.Value,new(lease.Value.Revision,view.Revision,"region-certificate",1),[view],new(),default,null).GetAwaiter().GetResult();
            var drawing=Assert.Single(batch.Views); Assert.Null(drawing.Error);
            var region=Assert.Single(drawing.Regions).Region;
            Assert.InRange(Math.Abs(region.Area-area),0,2e-6);
            Assert.Equal(2,region.Loops.Count);Assert.Single(region.Loops,l=>l.IsHole);Assert.NotNull(region.Hatch);
        }
    });
}
