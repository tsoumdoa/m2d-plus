using M2DPlus.Core;
using M2DPlus.Core.Sections;
using M2DPlus.Rhino.Sections;
using M2DPlus.Rhino.Services;
using Rhino.Geometry;
using Xunit;

namespace M2DPlus.Rhino.Tests;

[Collection("Rhino")]
[Trait("Category", "RhinoIntegration")]
public sealed class ReviewBeta4Tests(RhinoHost host)
{
    [RhinoTheory]
    [InlineData(0d, false)]
    [InlineData(1e6, false)]
    [InlineData(0d, true)]
    [InlineData(1e6, true)]
    public void RefusalIntersectionsAndFallbackFootprintsStayInsideTheCrop(double offset, bool fallback) => host.Run(() =>
    {
        var centre = new Point3d(offset, offset, offset);
        var crop = new Rectangle3d(new Plane(centre, Vector3d.ZAxis), new Interval(-1, 1), new Interval(-1, 1));
        using var box = Brep.CreateFromBox(new BoundingBox(centre+new Vector3d(-10,-.5,-1), centre+new Vector3d(10,.5,1)));
        using var mesh = new Mesh();
        foreach (var p in new[] { new Point3d(-10,-.5,1), new(10,-.5,1), new(10,.5,1), new(-10,.5,1) }) mesh.Vertices.Add(p+new Vector3d(offset,offset,offset));
        mesh.Faces.AddFace(0,1,2,3);
        GeometryBase geometry = fallback ? mesh : box;
        var source = new PreparedLeaf("refused", [0], geometry);
        using var output = new ViewDrawing { Frame = crop, Tolerance = 1e-4 };
        var before = WorkerCodec.Archive(geometry);
        // Deliberately leave the plane origin far from the geometry in XY.
        SectionPreparation.AddRefusedMarker(output, source, geometry, new(new Plane(new Point3d(0,0,offset),Vector3d.ZAxis),1e-4,.001,crop), default);
        Assert.NotEmpty(output.Items); Assert.Empty(output.Regions);
        foreach (var item in output.Items)
        {
            Assert.Equal(LineClass.Unresolved, item.Class);
            using var curve = item.CopyGeometry();
            var b = curve.GetBoundingBox(Transform.PlaneToPlane(crop.Plane, Plane.WorldXY));
            Assert.InRange(b.Min.X, -1-1e-7, 1+1e-7); Assert.InRange(b.Max.X, -1-1e-7, 1+1e-7);
            Assert.InRange(b.Min.Y, -1-1e-7, 1+1e-7); Assert.InRange(b.Max.Y, -1-1e-7, 1+1e-7);
        }
        Assert.Equal(before, WorkerCodec.Archive(geometry));
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        using var empty = new ViewDrawing { Frame = crop, Tolerance = 1e-4 };
        Assert.Throws<OperationCanceledException>(() => SectionPreparation.AddRefusedMarker(empty,source,geometry,new(crop.Plane,1e-4,.001,crop),canceled.Token));
        Assert.Empty(empty.Items);
    });

    [RhinoFact]
    public void FailedUnionKeepsItsSeparateFacesAndDoesNotRefuseAnotherStyleGroup() => host.Run(() =>
    {
        SectionRegion Square(string id, double x, uint colour)
        {
            var curve = new Rectangle3d(new Plane(new Point3d(x,0,0),Vector3d.ZAxis),2,2).ToNurbsCurve();
            return new(id,[new(curve,false,[new(curve.DuplicateCurve(),EdgeOrigin.PhysicalCut)])],null,[id],
                SectionStyleSnapshot.Default(colour),SectionStatus.Crossing,4,null);
        }
        using var a = Square("a",-10,0xff000000); using var b = Square("b",-9,0xff000000);
        using var c = Square("c",7,0xff00ff00); using var d = Square("d",8,0xff00ff00);
        int failed = 0, succeeded = 0;
        using var result = SectionRegionEngine.Restyle([a,b,c,d],new(Plane.WorldXY,1e-4,.001,Cuts:CutMode.Merge),
            (curves,plane,tol) =>
            {
                if (curves.Min(v => v.GetBoundingBox(true).Min.X)<0) { failed++; return null; }
                succeeded++; return Curve.CreateBooleanRegions(curves,plane,false,tol);
            });
        Assert.Equal(1,failed); Assert.Equal(1,succeeded); Assert.Equal(3,result.Regions.Count);
        Assert.All(result.Regions,r=>{ Assert.Equal(SectionStatus.Crossing,r.Status);Assert.NotNull(r.Hatch); });
        Assert.Equal(2,result.Regions.Count(r=>r.SourceIds.Count==1));
        var union = Assert.Single(result.Regions,r=>r.SourceIds.Count==2);
        Assert.Equal(new[]{"c","d"},union.SourceIds); Assert.InRange(Math.Abs(union.Area-6),0,1e-7);
        Assert.Contains(result.Diagnostics,d=>d.Message.Contains("this style group keeps its separate certified faces",StringComparison.Ordinal));
    });

    [RhinoFact]
    public void DistantPlaneOriginStillComputesAndPresentsLocallyWithOriginalHatchOrigin() => host.Run(() =>
    {
        var centre = new Point3d(1032200,1007000,1023900);
        using var column = new Cylinder(new Circle(new Plane(centre-new Vector3d(0,0,1500),Vector3d.ZAxis),200),3000).ToBrep(true,true);
        var plane = new Plane(new Point3d(0,0,centre.Z),Vector3d.ZAxis);
        var style = SectionStyleSnapshot.Default() with {HasStyle=true,Pattern=new("solid",true,[]),PatternOrigin=new(123,45)};
        using var cut = SectionRegionEngine.Compute([new("column",column,style)],new(plane,.01,.001),prepareVisibility:true);
        Assert.Empty(cut.Refusals); var region = Assert.Single(cut.Regions); Assert.NotNull(region.Hatch);
        Assert.InRange(Math.Abs(region.Area-Math.PI*40000),0,.01);
        Assert.InRange(region.Hatch.BasePoint.DistanceTo(plane.PointAt(123,45)),0,1e-7);
        using var shown = SectionRegionEngine.Restyle(cut.Regions,new(plane,.01,.001));
        var final = Assert.Single(shown.Regions); Assert.NotNull(final.Hatch);
        Assert.Equal(style.PatternOrigin,final.Style.PatternOrigin);
        Assert.InRange(final.Hatch.BasePoint.DistanceTo(plane.PointAt(123,45)),0,1e-7);
        Assert.InRange(Math.Abs(final.Area-Math.PI*40000),0,.01);
    });

    [RhinoTheory]
    [InlineData(false, 10)]
    [InlineData(true, 10)]
    [InlineData(false, 0)]
    [InlineData(false, .00005)]
    public void CompoundSourceMarksOnlyTheUnpublishedComponent(bool cutOnly, double depth) => host.Run(() =>
    {
        using var doc = global::Rhino.RhinoDoc.CreateHeadless(null); doc.ModelAbsoluteTolerance=1e-4;
        using var box = Brep.CreateFromBox(new BoundingBox(new Point3d(-2,-2,-1),new Point3d(2,2,1)));
        using var sphere = new Sphere(new Point3d(10,0,1-2e-4),1).ToBrep();
        using var compound = box.DuplicateBrep(); compound.Append(sphere);
        using var cache = new SceneCache();
        using var lease = BaselineTests.Capture(doc,cache,[new(compound,[0],"compound")]);
        var request = ViewRequest.Parse(Plane.WorldXY);
        var resolved = Settings.Resolve([new("compound",request.Name,request.Kind,true,lease.Value.Bounds.Diagonal.Length,1e-4)],
            [[cutOnly ? "cutonly=on" : "cutonly=off", "hidden=on", "farclip=line", "depth="+depth.ToString("R",System.Globalization.CultureInfo.InvariantCulture)]],DocumentUnits.Metres);
        request = request with { Settings = resolved.Views[0] };
        using var batch = BaselineTests.Solve(lease,request);
        var view = Assert.Single(batch.Views); Assert.Null(view.Error);
        if (cutOnly || depth <= 1e-4) { Assert.Equal(0,view.NativeCalls);}
        var region = Assert.Single(view.Regions).Region; Assert.Equal(16,region.Area,8);Assert.NotNull(region.Hatch);
        var markers = view.Items.Where(p=>p.Class==LineClass.Unresolved).ToArray(); Assert.NotEmpty(markers);
        foreach (var marker in markers)
        { using var g=marker.CopyGeometry(); Assert.True(g.GetBoundingBox(true).Min.X>9,view.Info); }
    });

}
