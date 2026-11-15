using M2DPlus.Core;
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
    [RhinoFact] public void FootprintsWithholdOnlyLocalIntervalsAndRetainDepthAndSolvedClass() => host.Run(() =>
    {
        using var line = new LineCurve(new Point3d(-10,0,0), new Point3d(10,0,0));
        using var depth = new LineCurve(new Point3d(-10,0,-3), new Point3d(10,0,-1));
        depth.Domain = line.Domain;
        var source = new PreparedLeaf("line", [2], line);
        using var output = new ViewDrawing();
        output.Pieces.Add(new(line.DuplicateCurve(), source, LineClass.Hidden, depth.DuplicateCurve()) { Profile = true, KernelOwned = true });
        var box = new BoundingBox(new Point3d(-1,-1,0),new Point3d(1,1,0));
        NativeUncertainty.ApplyFootprints(output,[source],false,[],[box],1e-6,default);
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
