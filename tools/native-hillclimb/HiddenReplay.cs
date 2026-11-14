using System.Text.Json;
using M2DPlus.Core;
using M2DPlus.Rhino.Services;
using Rhino.DocObjects;
using Rhino.FileIO;
using Rhino.Geometry;
namespace NativeHillclimb;
internal static class HiddenReplay
{
    internal static void Run(string input,string output)
    {
        using var metadata=JsonDocument.Parse(File.ReadAllText(Path.ChangeExtension(input,"json")));var root=metadata.RootElement;
        if(Program.Hash(input)!=root.GetProperty("fixtureSha256").GetString()!.ToUpperInvariant())throw new IOException("Reproducer hash differs");
        double[] Values(JsonElement e)=>e.EnumerateArray().Select(x=>x.GetDouble()).ToArray();
        var f=root.GetProperty("frame");var o=Values(f.GetProperty("origin"));var x=Values(f.GetProperty("x"));var y=Values(f.GetProperty("y"));
        var plane=new Plane(new Point3d(o[0],o[1],o[2]),new Vector3d(x[0],x[1],x[2]),new Vector3d(y[0],y[1],y[2]));
        var b=Values(f.GetProperty("sceneBounds"));var bounds=new BoundingBox(new Point3d(b[0],b[1],b[2]),new Point3d(b[3],b[4],b[5]));
        var radius=Math.Max(bounds.Diagonal.Length,1);var frustum=Values(root.GetProperty("frustum"));var tol=root.GetProperty("absoluteTolerance").GetDouble();
        using var file=File3dm.Read(input) ?? throw new IOException("Input read failed");if(file.Objects.Count!=3)throw new IOException("Expected three input geometries");
        ViewDrawing Draw(bool hidden)
        {
            var result=new ViewDrawing();using var vp=new ViewportInfo();vp.IsParallelProjection=true;
            vp.SetCameraLocation(plane.Origin+plane.ZAxis*(radius*4+10));vp.SetCameraDirection(-plane.ZAxis);vp.SetCameraUp(plane.YAxis);
            vp.SetFrustum(frustum[0],frustum[1],frustum[2],frustum[3],.001,radius*10+100);
            var parameters=new HiddenLineDrawingParameters{AbsoluteTolerance=tol,Flatten=false,IncludeHiddenCurves=hidden,IncludeTangentEdges=false,IncludeTangentSeams=false};parameters.SetViewport(vp);
            foreach(var obj in file.Objects)parameters.AddGeometry(obj.Geometry,obj.Attributes.Name);
            using var drawing=HiddenLineDrawing.Compute(parameters,false);drawing.WorldToHiddenLine.TryGetInverse(out var inverse);var flat=Transform.PlanarProjection(plane)*inverse;
            foreach(var segment in drawing.Segments)
            {
                if(segment.SegmentVisibility!=HiddenLineDrawingSegment.Visibility.Visible)continue;
                var geo=segment.CurveGeometry.DuplicateCurve();geo.EnsurePrivateCopy();geo.Transform(flat);
                var source=new PreparedLeaf((string)segment.ParentCurve.SourceObject.Tag,new[]{0},geo);
                result.Pieces.Add(new(geo,source,LineClass.Visible));
            }
            return result;
        }
        using var on=Draw(true);using var off=Draw(false);var result=Locus.Compare(on,off,tol);
        Locus.Save(on,Path.Combine(output,"on-visible.3dm"));Locus.Save(off,Path.Combine(output,"off-visible.3dm"));
        Program.Write(Path.Combine(output,"replay.json"),new{inputCount=file.Objects.Count,negativeRegressionReproduced=result.Differences.Any(d=>d.Kind=="missing-locus" && d.UncoveredLength>tol*8),result});
    }
}
