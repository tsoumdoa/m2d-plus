using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using M2DPlus.Core;
using M2DPlus.Rhino.Sections;
using Rhino.Geometry;

namespace M2DPlus.Rhino.Services;

/// <summary>Schema 3: raw binary transport envelopes; detached scene/drawing JSON is unchanged.</summary>
internal static class WorkerCodec
{
    internal const int Schema = 3;
    internal static JsonSerializerOptions Json { get; } = Options();
    private static JsonSerializerOptions Options()
    {
        var options = new JsonSerializerOptions { NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };
        options.Converters.Add(new WorkerSettingsConverter());
        options.Converters.Add(new ShapeConverter<Point3d>(p => [p.X,p.Y,p.Z], a => new(a[0],a[1],a[2])));
        options.Converters.Add(new ShapeConverter<Vector3d>(p => [p.X,p.Y,p.Z], a => new(a[0],a[1],a[2])));
        options.Converters.Add(new ShapeConverter<Interval>(p => [p.T0,p.T1], a => new(a[0],a[1])));
        options.Converters.Add(new ShapeConverter<Plane>(p => [p.OriginX,p.OriginY,p.OriginZ,p.XAxis.X,p.XAxis.Y,p.XAxis.Z,p.YAxis.X,p.YAxis.Y,p.YAxis.Z,p.ZAxis.X,p.ZAxis.Y,p.ZAxis.Z],
            a => new Plane { Origin = new(a[0],a[1],a[2]), XAxis = new(a[3],a[4],a[5]), YAxis = new(a[6],a[7],a[8]), ZAxis = new(a[9],a[10],a[11]) }));
        options.Converters.Add(new ShapeConverter<Transform>(p => Enumerable.Range(0,16).Select(i => p[i/4,i%4]).ToArray(), a => { var t = new Transform(); for(var i=0;i<16;i++) t[i/4,i%4]=a[i]; return t; }));
        options.Converters.Add(new RectangleConverter());
        return options;
    }
    private sealed class ShapeConverter<T>(Func<T,double[]> pack, Func<double[],T> unpack) : JsonConverter<T>
    {
        public override T Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => unpack(JsonSerializer.Deserialize<double[]>(ref reader, options)!);
        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) => JsonSerializer.Serialize(writer, pack(value), options);
    }
    private sealed record Rectangle(Plane Plane, Interval X, Interval Y);
    private sealed class RectangleConverter : JsonConverter<Rectangle3d>
    {
        public override Rectangle3d Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) { var r=JsonSerializer.Deserialize<Rectangle>(ref reader,options)!;return new(r.Plane,r.X,r.Y); }
        public override void Write(Utf8JsonWriter writer, Rectangle3d value, JsonSerializerOptions options) => JsonSerializer.Serialize(writer,new Rectangle(value.Plane,value.X,value.Y),options);
    }
    internal static byte[] Encode<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Json);
    internal static T Decode<T>(byte[] bytes) => JsonSerializer.Deserialize<T>(bytes, Json) ?? throw new InvalidDataException("Empty worker message");
    internal static string Archive(GeometryBase geometry) => geometry.ToJSON(new global::Rhino.FileIO.SerializationOptions { RhinoVersion = 8, WriteUserData = true });
    internal static GeometryBase Restore(string archive) => global::Rhino.Runtime.CommonObject.FromJSON(archive) as GeometryBase ?? throw new InvalidDataException("Invalid geometry archive");
    internal static WorkerFingerprint Fingerprint() => new(Schema, Environment.Version.Major, RuntimeInformation.ProcessArchitecture.ToString(),
        global::Rhino.RhinoApp.Version.ToString(), Hash(File.ReadAllBytes(typeof(global::Rhino.RhinoApp).Assembly.Location)),
        Hash(Encoding.UTF8.GetBytes(Hash(File.ReadAllBytes(typeof(WorkerCodec).Assembly.Location)) + Hash(File.ReadAllBytes(typeof(SceneCost).Assembly.Location)))),
        Environment.Version.ToString(), global::Rhino.RhinoApp.VersionControlRevision);
    internal static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    internal sealed record Definition(string Archive, string? Conversion);
    internal sealed record Leaf(string Identity, string Key, int[] Path, Transform[] Chain, SourceSection Section, int SourceItem);
    internal sealed record Scene(string Revision, double Tolerance, Dictionary<string,Definition> Definitions, Leaf[] Leaves, Dictionary<string,int> Unsupported);
    internal static byte[] Capture(PreparedScene scene, CancellationToken token)
    {
        var definitions = new Dictionary<string,Definition>();
        foreach (var leaf in scene.TransportLeaves)
        {
            token.ThrowIfCancellationRequested();
            if (definitions.ContainsKey(leaf.Key)) continue;
            using var copy = leaf.Geometry.Value.Copy();
            definitions.Add(leaf.Key,new(Archive(copy),leaf.Geometry.Value.Conversion));
        }
        return Encode(new Scene(scene.Revision,scene.Tolerance,definitions,scene.TransportLeaves.Select(l=>new Leaf(l.Identity,l.Key,l.Path,l.Chain,l.Section,l.SourceItem)).ToArray(),new(scene.Unsupported)));
    }
    internal static PreparedScene RestoreScene(byte[] bytes)
    {
        var dto=Decode<Scene>(bytes);var owners=new Dictionary<string,LeaseEntry<OwnedGeometry>>();var leaves=new List<SceneLeaf>();
        try
        {
            foreach(var d in dto.Definitions) owners.Add(d.Key,new(new OwnedGeometry(Restore(d.Value.Archive),d.Value.Conversion)));
            foreach(var l in dto.Leaves) leaves.Add(new(l.Identity,l.Key,l.Path,l.Chain,owners[l.Key].Acquire(),l.Section){SourceItem=l.SourceItem});
            return new(dto.Revision,leaves,dto.Unsupported,dto.Tolerance);
        }
        catch { foreach(var l in leaves)l.Geometry.Dispose(); throw; }
        finally { foreach(var owner in owners.Values)owner.Dispose(); }
    }
    /// <summary>DenseAudit and its limits travel with every solve: the host decides, never the worker's own environment.</summary>
    internal sealed record Request(RequestIdentity Identity, int ViewIndex, ViewRequest View, bool NativeOnly, bool DenseAudit = false, DenseAuditOptions? DenseAuditOptions = null);
    internal sealed record Reply(RequestIdentity Identity, int ViewIndex, byte[] Drawing);
    internal sealed record Piece(string Geometry, string? Depth, string SourceIdentity, int[] SourcePath, int SourceItem, SourceSection Source,
        LineClass Class, LineClass SolvedClass, bool Profile, bool KernelOwned, bool FarCandidate, DepthInterval Depths, double Intensity);
    internal sealed record Edge(string Geometry, M2DPlus.Core.Sections.EdgeOrigin Origin);
    internal sealed record Loop(string Geometry, bool Hole, Edge[] Edges);
    internal sealed record Region(string Id, Loop[] Loops, string? Hatch, string[] Sources, M2DPlus.Core.Sections.SectionStyleSnapshot Style,
        M2DPlus.Core.Sections.SectionStatus Status, double Area, string? Uncertainty, int[] Path, int SourceItem, SourceSection Source);
    
    internal sealed record Values(string Name, EffectiveSettings? Settings, string? Error, Rectangle3d Frame, QualityReport Report, int HiddenPoints, int DuplicateSegments, int ProjectingSegments, int EmptyNativeSlots, int ZeroLengthNativeSegments, int ToleranceDroppedPieces, int SubToleranceSourceEdges, int SubToleranceBoundsGaps, int UncertainSectionComponents, double Seconds, double MinVisible, int IgnoredObjects, int IgnoredPieces, int RoutingInvalid, bool RoutingQuarantine, string[] UncertainRoutingSources, int KernelEdges, int NativeEdges, int NativeCalls, int FastNativeSources, int CurvedCandidateEdges, int PlanarNativeEdges, int NativeFragments, bool MultipleThreads, double Tolerance);
    internal sealed record Drawing(Values Values, string[] Diagnostics, Dictionary<string,double> Phases, Piece[] Pieces, Region[] Regions);
    internal static byte[] CaptureDrawing(ViewDrawing drawing, bool stable = false)
    {
        var values = new Values(drawing.Name, drawing.Settings, drawing.Error, drawing.Frame, drawing.Report, drawing.HiddenPoints, drawing.DuplicateSegments, drawing.ProjectingSegments, drawing.EmptyNativeSlots, drawing.ZeroLengthNativeSegments, drawing.ToleranceDroppedPieces, drawing.SubToleranceSourceEdges, drawing.SubToleranceBoundsGaps, drawing.UncertainSectionComponents, stable ? 0 : drawing.Seconds, drawing.MinVisible, drawing.IgnoredObjects, drawing.IgnoredPieces, drawing.RoutingInvalid, drawing.RoutingQuarantine, drawing.UncertainRoutingSources.Order(StringComparer.Ordinal).ToArray(), drawing.KernelEdges, drawing.NativeEdges, drawing.NativeCalls, drawing.FastNativeSources, drawing.CurvedCandidateEdges, drawing.PlanarNativeEdges, drawing.NativeFragments, drawing.MultipleThreads, drawing.Tolerance);
        return Encode(new Drawing(values, stable ? [] : drawing.Diagnostics.ToArray(), stable ? new() : new(drawing.Phases.Seconds),
            drawing.Items.Select(p => { using var g=p.CopyGeometry(); return new Piece(Archive(g),p.DepthGeometry is {} d?Archive(d):null,
                p.SourceIdentity,p.SourcePath,p.SourceItem,p.Source,p.Class,p.SolvedClass,p.Profile,p.KernelOwned,p.FarCandidate,p.Depths,p.Intensity); }).ToArray(),
            drawing.Regions.Select(r=>new Region(r.Region.Id,r.Region.Loops.Select(l=>new Loop(Archive(l.Curve),l.IsHole,
                l.Edges.Select(e=>new Edge(Archive(e.Curve),e.Origin)).ToArray())).ToArray(),r.Region.Hatch is {} h?Archive(h):null,
                r.Region.SourceIds.ToArray(),r.Region.Style,r.Region.Status,r.Region.Area,r.Region.Uncertainty,r.SourcePath,r.SourceItem,r.Source)).ToArray()));
    }
    internal static ViewDrawing RestoreDrawing(byte[] bytes)
    {
        var dto=Decode<Drawing>(bytes); var v=dto.Values;
        var drawing=new ViewDrawing { Name=v.Name, Settings=v.Settings, Error=v.Error, Frame=v.Frame, Report=v.Report, HiddenPoints=v.HiddenPoints, DuplicateSegments=v.DuplicateSegments, ProjectingSegments=v.ProjectingSegments, EmptyNativeSlots=v.EmptyNativeSlots, ZeroLengthNativeSegments=v.ZeroLengthNativeSegments, ToleranceDroppedPieces=v.ToleranceDroppedPieces, SubToleranceSourceEdges=v.SubToleranceSourceEdges, SubToleranceBoundsGaps=v.SubToleranceBoundsGaps, UncertainSectionComponents=v.UncertainSectionComponents, Seconds=v.Seconds, MinVisible=v.MinVisible, IgnoredObjects=v.IgnoredObjects, IgnoredPieces=v.IgnoredPieces, RoutingInvalid=v.RoutingInvalid, RoutingQuarantine=v.RoutingQuarantine, KernelEdges=v.KernelEdges, NativeEdges=v.NativeEdges, NativeCalls=v.NativeCalls, FastNativeSources=v.FastNativeSources, CurvedCandidateEdges=v.CurvedCandidateEdges, PlanarNativeEdges=v.PlanarNativeEdges, NativeFragments=v.NativeFragments, MultipleThreads=v.MultipleThreads, Tolerance=v.Tolerance };
        try
        {
            drawing.UncertainRoutingSources.UnionWith(v.UncertainRoutingSources);
            drawing._diagnostics.AddRange(dto.Diagnostics);foreach(var phase in dto.Phases)drawing.Phases.Add(phase.Key,phase.Value);
            foreach(var p in dto.Pieces)
            {
                var g=Restore(p.Geometry);GeometryBase? depth=null;
                try { depth=p.Depth is {} d?Restore(d):null;
                    drawing.Pieces.Add(new(g,new PreparedLeaf(p.SourceIdentity,p.SourcePath,g){Section=p.Source,SourceItem=p.SourceItem},p.Class,depth,p.SolvedClass)
                        {Profile=p.Profile,KernelOwned=p.KernelOwned,FarCandidate=p.FarCandidate,Depths=p.Depths,Intensity=p.Intensity}); }
                catch {g.Dispose();depth?.Dispose();throw;}
            }
            foreach(var r in dto.Regions)
            {
                var loops=new List<SectionLoop>();Hatch? hatch=null;
                try
                {
                    foreach(var l in r.Loops)
                    {
                        var curve=(Curve)Restore(l.Geometry);var edges=new List<SectionEdge>();
                        try {foreach(var e in l.Edges)edges.Add(new((Curve)Restore(e.Geometry),e.Origin));loops.Add(new(curve,l.Hole,edges));}
                        catch {curve.Dispose();foreach(var e in edges)e.Dispose();throw;}
                    }
                    hatch=r.Hatch is {} h?(Hatch)Restore(h):null;
                    drawing.Regions.Add(new(new(r.Id,loops,hatch,r.Sources,r.Style,r.Status,r.Area,r.Uncertainty),r.Path){SourceItem=r.SourceItem,Source=r.Source});
                }
                catch {foreach(var l in loops)l.Dispose();hatch?.Dispose();throw;}
            }
            return drawing;
        }
        catch {drawing.Dispose();throw;}
    }
}
