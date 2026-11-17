using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using M2DPlus.Core;
using M2DPlus.Core.Sections;
using M2DPlus.Rhino.Sections;
using M2DPlus.Rhino.Services;
using Rhino.Geometry;
using Xunit;

namespace M2DPlus.Rhino.Tests;

[Collection("Rhino")]
public sealed class WorkerCodecStateTests(RhinoHost host)
{
    // Every stored instance member must have an explicit wire representation or a checked reconstruction.
    // Compiler-generated backing fields identify get-only state too (not only public setters).
    private static string[] State(Type type)=>type.GetFields(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic)
        .Select(f=>f.IsDefined(typeof(CompilerGeneratedAttribute))?f.Name[1..f.Name.IndexOf('>')]:f.Name).Order(StringComparer.Ordinal).ToArray();
    [RhinoFact] public void EveryStoredMemberHasAProtocolRepresentationAndRoundTrips()=>host.Run(()=>
    {
        var pieceWire=typeof(WorkerCodec.Piece).GetProperties().Select(p=>p.Name).ToHashSet();
        var pieceMap=new Dictionary<string,string>{{"_geometry","Geometry"},{"_path","SourcePath"},{"DepthGeometry","Depth"},{"GeometricKey","Geometry"}};
        foreach(var name in State(typeof(DrawingPiece)))Assert.True(pieceWire.Contains(pieceMap.GetValueOrDefault(name)??name),"Missing DrawingPiece wire state: "+name);
        var viewWire=typeof(WorkerCodec.Values).GetProperties().Select(p=>p.Name).Concat(new[]{"Pieces","Regions","Phases","_diagnostics"}).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(State(typeof(ViewDrawing)),viewWire);

        var settings=Settings.Resolve([new("wire","wire",ViewSourceKind.Plane,true,200,.001)],[["hidden=off","min visible=5 mm"]],DocumentUnits.Millimetres).Views[0];
        using var source=new ViewDrawing{Settings=settings};
        using var geometry=new LineCurve(new Point3d(1,2,3),new Point3d(9,11,13));
        var piece=new DrawingPiece(geometry.Duplicate(),new("source/id",[7,9],geometry){SourceItem=13},LineClass.Hidden,geometry.Duplicate());
        var seed=30;
        foreach(var typeAndValue in new[]{(typeof(ViewDrawing),(object)source),(typeof(DrawingPiece),(object)piece)})
        {
            foreach(var property in typeAndValue.Item1.GetProperties(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic).Where(p=>p.SetMethod is not null))
            {
                object value=property.PropertyType==typeof(int)?++seed:property.PropertyType==typeof(double)?++seed+.125:
                    property.PropertyType==typeof(bool)?true:property.PropertyType==typeof(string)?"wire/"+property.Name:
                    property.PropertyType==typeof(LineClass)?LineClass.Ignored:property.PropertyType==typeof(Rectangle3d)?new Rectangle3d(new Plane(new Point3d(2,3,4),Vector3d.XAxis,Vector3d.YAxis),new Interval(-4,8),new Interval(1,13)):
                    property.PropertyType==typeof(DepthInterval)?new DepthInterval(2,9):property.PropertyType==typeof(EffectiveSettings)?settings:
                    property.PropertyType==typeof(QualityReport)?new QualityReport("wire",3,4,new Dictionary<string,int>{{"type",5}},["normalised"],true,"wire domain"):
                    throw new InvalidOperationException("Seed the new state type: "+property.Name);
                property.SetValue(typeAndValue.Item2,value);
            }
        }
        source.RoutingInvalid=37;source.RoutingQuarantine=true;
        source.Pieces.Add(piece);source._diagnostics.Add("wire diagnostic");source.Phases.Add("wire phase",.1875);
        source.Regions.Add(new(new("region",[],null,["source/id"],SectionStyleSnapshot.Default(),SectionStatus.Contact,7,"test"),[3,5]){SourceItem=19});
        using var restored=WorkerCodec.RestoreDrawing(WorkerCodec.CaptureDrawing(source));
        foreach(var name in State(typeof(ViewDrawing)))CompareMember(source,restored,name);
        foreach(var name in State(typeof(DrawingPiece)))CompareMember(piece,restored.Items.Single(),name);
    });
    private static void CompareMember(object original,object restored,string name)
    {
        object? Read(object instance)=>instance.GetType().GetProperty(name,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic)?.GetValue(instance)??
            instance.GetType().GetField(name,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic)?.GetValue(instance);
        if(name=="_geometry" || name=="DepthGeometry")
        {
            var a=(GeometryBase?)Read(original);var b=(GeometryBase?)Read(restored);
            Assert.Equal(a is null?null:WorkerCodec.Archive(a),b is null?null:WorkerCodec.Archive(b));return;
        }
        if(name=="Pieces")return; // Enumerated separately below, including every piece backing field.
        if(name=="Regions")
        {
            var a=WorkerCodec.Decode<WorkerCodec.Drawing>(WorkerCodec.CaptureDrawing((ViewDrawing)original)).Regions;
            var b=WorkerCodec.Decode<WorkerCodec.Drawing>(WorkerCodec.CaptureDrawing((ViewDrawing)restored)).Regions;
            Assert.Equal(WorkerCodec.Encode(a),WorkerCodec.Encode(b));return;
        }
        if(name=="Phases") {Assert.Equal(WorkerCodec.Encode(((ViewDrawing)original).Phases.Seconds),WorkerCodec.Encode(((ViewDrawing)restored).Phases.Seconds));return;}
        var left=Read(original);var right=Read(restored);
        Assert.True(JsonSerializer.Serialize(left,left?.GetType()??typeof(object),WorkerCodec.Json)==JsonSerializer.Serialize(right,right?.GetType()??typeof(object),WorkerCodec.Json),"Did not round-trip: "+original.GetType().Name+"."+name);
    }
}
