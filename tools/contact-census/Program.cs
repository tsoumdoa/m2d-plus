using System.Security.Cryptography;
using System.Text.Json;
using M2DPlus.Core.Visibility;

if (args.Length is <3 or >5) throw new ArgumentException("scene.json tolerance output.json [repeats] [reference-scene.json]");
int repeats=args.Length>=4?int.Parse(args[3],System.Globalization.CultureInfo.InvariantCulture):3;
if (repeats is <1 or >3) throw new ArgumentOutOfRangeException(nameof(repeats));
var scene=JsonSerializer.Deserialize<Scene>(File.ReadAllText(args[0]))!;
double tolerance=double.Parse(args[1],System.Globalization.CultureInfo.InvariantCulture);
var oldFaces=scene.Faces.Select(f=>f with { Source=null }).ToArray();
var oldEdges=scene.Edges.Select(e=>e with { AdjacentFaces=[] }).ToArray();
var timer=System.Diagnostics.Stopwatch.StartNew();
var off=EdgeFaceKernel.Solve(scene.Faces,scene.Edges,new(tolerance,8) { Joints=false });
double offSeconds=timer.Elapsed.TotalSeconds;
var contacts=off.Contacts.Select(c=>c.Edge).ToHashSet();
VisibilityResult? onResult=null;
object Measure(PlanarFace[] faces,CandidateEdge[] edges,bool joints,VisibilityResult? known=null)
{
    var signatures=new List<Dictionary<int,string>>();var seconds=new List<double>();VisibilityResult? last=null;Locus[] loci=[];
    for(int run=0;run<repeats;run++)
    {
        timer.Restart();last=run==0&&known is not null?known:EdgeFaceKernel.Solve(faces,edges,new(tolerance,run==1?1:8) { Joints=joints });
        seconds.Add(run==0&&known is not null?offSeconds:timer.Elapsed.TotalSeconds);
        signatures.Add(last.Intervals.GroupBy(i=>i.Edge).ToDictionary(g=>g.Key,g=>JsonSerializer.Serialize(g)));
        Console.WriteLine($"{Path.GetFileName(args[0])}: joints={joints} run={run} intervals={last.Intervals.Count}");
    }
    loci=VisibilityLoci.Create(edges,last!,tolerance);
    if(ReferenceEquals(edges,scene.Edges)&&joints)onResult=last;
    var retained=last!.Intervals.Select(i=>i.Edge).ToHashSet();
    bool Changed(int edge)=>signatures.Select(s=>s.GetValueOrDefault(edge,"")).Distinct().Count()>1;
    double Length(EdgeInterval i)
    {
        var e=edges[i.Edge];return Math.Sqrt(Math.Pow(e.B.X-e.A.X,2)+Math.Pow(e.B.Y-e.A.Y,2))*(i.End-i.Start);
    }
    var duplicates=loci.GroupBy(l=>{
        var a=(l.X0,l.Y0);var b=(l.X1,l.Y1);return a.CompareTo(b)<=0?(a,b,l.Class):(b,a,l.Class);
    }).Where(g=>g.Count()>1).ToArray();
    return new {
        contactCandidateEdges=contacts.Count,
        contactEdgesByClass=Enum.GetValues<VisibilityClass>().ToDictionary(c=>c.ToString(),c=>last!.Intervals.Where(i=>i.Class==c&&contacts.Contains(i.Edge)).Select(i=>i.Edge).Distinct().Count()),
        removedContactEdges=contacts.Count(e=>!retained.Contains(e)),
        rawIntervals=last!.Intervals.Count,drawingLoci=loci.Length,
        duplicateDrawingEdges=duplicates.Where(g=>g.First().Length>0).Sum(g=>g.Count()-1),
        zeroLengthDrawingLoci=loci.Count(l=>l.Length==0),duplicateGroups=duplicates.Select(g=>g.ToArray()).ToArray(),
        rawRepeatedProjectedLength=last.Intervals.Sum(Length)-loci.Sum(l=>l.Length),
        flickeringEdges=repeats<2?(int?)null:Enumerable.Range(0,edges.Length).Count(Changed),contactFlickeringEdges=repeats<2?(int?)null:contacts.Count(Changed),
        runs=repeats,seconds,faceTests=last.FaceTests,threads=Enumerable.Range(0,repeats).Select(i=>i==1?1:8).ToArray(),drawingSha256=Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(loci)))
    };
}
var result=new {
    metricVersion=2,censusSha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(Scene).Assembly.Location))),
    scope="Planar kernel replay on identical extracted geometry. Before disables contact metadata to reproduce the previous tolerance tie algorithm; native/freeform output is excluded. Optional repeated-input flicker checks use the recorded runs/threads; a single count run makes no flicker claim. This is not a geometry perturbation test.",
    unionScope="The managed projection-only union is a diagnostic, before full mixed-source ownership reconciliation, clipping and presentation. Its duplicateGroups expose even microscopic residues; use repeat.py for final output record duplicates.",
    input=Path.GetFullPath(args[0]),inputSha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(args[0]))),
    coreSha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(EdgeFaceKernel).Assembly.Location))),tolerance,
    edges=scene.Edges.Length,faces=scene.Faces.Length,
    rawCoincidentContactCandidates=scene.Edges.Select((e,i)=>(e,i)).Where(p=>contacts.Contains(p.i)).GroupBy(p=>{
        var a=(p.e.A.X,p.e.A.Y,p.e.A.Z);var b=(p.e.B.X,p.e.B.Y,p.e.B.Z);return a.CompareTo(b)<=0?(a,b):(b,a);
    }).Sum(g=>g.Count()-1),
    contactSpans=Enum.GetValues<ContactKind>().ToDictionary(k=>k.ToString(),k=>off.Contacts.Count(c=>c.Kind==k)),
    contactEdgesByKind=Enum.GetValues<ContactKind>().ToDictionary(k=>k.ToString(),k=>off.Contacts.Where(c=>c.Kind==k).Select(c=>c.Edge).Distinct().Count()),
    before=Measure(oldFaces,oldEdges,true),afterOn=Measure(scene.Faces,scene.Edges,true),afterOff=Measure(scene.Faces,scene.Edges,false,off),
    rigidComparison=CompareReference()
};
File.WriteAllText(args[2],JsonSerializer.Serialize(result,new JsonSerializerOptions { WriteIndented=true }));
object? CompareReference()
{
    if(args.Length<5)return null;
    var reference=JsonSerializer.Deserialize<Scene>(File.ReadAllText(args[4]))!;
    var priorEdges=reference.Edges.Select((e,i)=>(e,i)).GroupBy(p=>p.e.Source).ToDictionary(g=>g.Key,g=>g.Select(p=>p.i).ToArray());
    var currentEdges=scene.Edges.Select((e,i)=>(e,i)).GroupBy(p=>p.e.Source).ToDictionary(g=>g.Key,g=>g.Select(p=>p.i).ToArray());
    var priorFaces=reference.Faces.Select((f,i)=>(f,i)).GroupBy(p=>p.f.Source??"").ToDictionary(g=>g.Key,g=>g.Select(p=>p.i).ToArray());
    var currentFaces=scene.Faces.Select((f,i)=>(f,i)).GroupBy(p=>p.f.Source??"").ToDictionary(g=>g.Key,g=>g.Select(p=>p.i).ToArray());
    var edgeMap=new Dictionary<int,int>();int comparable=0;
    foreach(var (source,now) in currentEdges)
    {
        if(!priorEdges.TryGetValue(source,out var prior)||prior.Length!=now.Length)continue;
        var oldFaces=priorFaces.GetValueOrDefault(source,[]);var newFaces=currentFaces.GetValueOrDefault(source,[]);
        if(oldFaces.Length!=newFaces.Length)continue;
        var pairs=now.Zip(prior).ToArray();
        // Rhino rigid transforms preserve edge/face topology ordering. Also
        // require the selected edges' local adjacency to match; otherwise do
        // not assume that equal candidate counts mean equal physical edges.
        if(pairs.Any(p=>!scene.Edges[p.First].AdjacentFaces.Select(i=>Array.IndexOf(newFaces,i))
            .SequenceEqual(reference.Edges[p.Second].AdjacentFaces.Select(i=>Array.IndexOf(oldFaces,i)))))continue;
        comparable++;foreach(var (current,previous) in pairs)edgeMap.Add(current,previous);
    }
    var baseline=EdgeFaceKernel.Solve(reference.Faces,reference.Edges,new(tolerance,8));
    var visible=baseline.Intervals.Where(i=>i.Class==VisibilityClass.Visible).GroupBy(i=>i.Edge).ToDictionary(g=>g.Key,g=>g.ToArray());
    var newlyUnresolved=onResult!.Intervals.Where(i=>i.Class==VisibilityClass.Unresolved&&edgeMap.TryGetValue(i.Edge,out int prior)&&visible.TryGetValue(prior,out var spans)
        &&spans.Any(v=>Math.Max(v.Start,i.Start)<Math.Min(v.End,i.End))).Select(i=>i.Edge).Distinct().ToArray();
    return new { reference=Path.GetFullPath(args[4]),referenceSha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(args[4]))),
        scope="Native rigidly transformed geometry and view. Compare only shared sources with matching candidate/face counts and local adjacency/order. Planar-tier eligibility/occluder changes are reported separately: this is not whole-scene mixed visibility. Positive-overlap edge/parameter counts are not final drawing records.",
        referenceSources=priorEdges.Count,currentSources=currentEdges.Count,comparableSources=comparable,comparableEdges=edgeMap.Count,
        lostPlanarSources=priorEdges.Keys.Except(currentEdges.Keys).Count(),addedPlanarSources=currentEdges.Keys.Except(priorEdges.Keys).Count(),
        skippedCommonSources=priorEdges.Keys.Intersect(currentEdges.Keys).Count()-comparable,
        visibleToUnresolvedEdges=newlyUnresolved.Length,visibleToUnresolvedContactEdges=newlyUnresolved.Count(contacts.Contains),
        baselineContactKinds=Enum.GetValues<ContactKind>().ToDictionary(k=>k.ToString(),k=>baseline.Contacts.Where(c=>c.Kind==k).Select(c=>c.Edge).Distinct().Count()) };
}
internal sealed record Scene(PlanarFace[] Faces,CandidateEdge[] Edges);
