using M2DPlus.Core;
using M2DPlus.Core.Visibility;
using M2DPlus.Rhino.Services;
using Rhino.Geometry;

namespace M2DPlus.HcPerf;

public static partial class CompositionGate
{
    private static void CheckMixedReference()
    {
        using var projected=new LineCurve(Point3d.Origin,new Point3d(1,0,0));
        using var world=new LineCurve(new Point3d(0,0,1),new Point3d(1,0,1));
        var scene=new PlanarExtraction();scene.Edges.Add(new(new(0,0,1),new(1,0,1),"kernel"));
        scene.Faces.Add(new([[new(0,-1,1),new(1,-1,1),new(1,1,1),new(0,1,1)]]));
        SavedView View(LineClass kind)=>new("reference control",null,"",0,1,0,0,0,0,0,[new("kernel",kind,projected.ToJSON(new()),world.ToJSON(new()),true)],[],[]);
        using var original=new Curves(View(LineClass.Unresolved));using var actual=new Curves(View(LineClass.Visible));
        using var contributors=new KernelContributors(scene,Plane.WorldXY);using var trace=new NativeTrace();
        using(var audit=new MixedReference(scene,Plane.WorldXY,.125,false,trace,[],contributors,original,actual))
        {
            var check=audit.Check(new(.5,0,0));
            if(check.Class!=VisibilityClass.Visible||!check.Contact||audit.CompletePairs!=1)throw new InvalidOperationException("Independent contact reference control failed");
        }
        BoundingBox[] bounds=[new(new Point3d(-1,-1,-1),new Point3d(2,1,2))];
        using(var audit=new MixedReference(scene,Plane.WorldXY,.125,false,trace,bounds,contributors,original,actual))
            if(audit.Check(new(.5,0,0)).Class!=VisibilityClass.Unresolved)throw new InvalidOperationException("An indefinite curved occluder was treated as definite");
        trace.Add("kernel",0,false,LineClass.Hidden,world);
        using(var audit=new MixedReference(scene,Plane.WorldXY,.125,false,trace,bounds,contributors,original,actual))
            if(audit.Check(new(.5,0,0)).Class!=VisibilityClass.Hidden)throw new InvalidOperationException("Definite curved Hidden did not decide the union");
    }
    private sealed record ReferenceDecision(VisibilityClass Class,bool MatchedActual,IReadOnlyList<object> Candidates,bool Contact);
    private sealed class MixedReference : IDisposable
    {
        private readonly PlanarExtraction _scene;private readonly Plane _plane;private readonly double _tol;private readonly bool _cut;
        private readonly ExactVisibilityOracle.AllPairsPoints _oracle;private readonly KernelContributors _contributors;private readonly Curves _actual;private readonly Curves _original;
        private readonly IReadOnlySet<string> _nativeSources;
        private readonly IReadOnlyList<BoundingBox> _curvedBounds;
        private readonly List<(string Source,int Edge,bool Duplicate,LineClass Class,Curve World,Curve Projected)> _native=[];
        private readonly RTree _tree=new();private readonly Dictionary<int,ExactVisibilityOracle.PairSet> _kernelPairs=[];private readonly Dictionary<int,ExactVisibilityOracle.PairSet> _nativePairs=[];
        private readonly Dictionary<int,Curve> _actualDepth=[];private readonly Dictionary<int,Curve> _originalDepth=[];private readonly Dictionary<int,ExactVisibilityOracle.PairSet> _originalPairs=[];private readonly Dictionary<string,List<Curve>> _represented=new(StringComparer.Ordinal);
        public long CompletePairs=>_oracle.CompletePairs;public long ExactPointPairs=>_oracle.ExactPointPairs;
        public MixedReference(PlanarExtraction scene,Plane plane,double tol,bool cut,NativeTrace native,IReadOnlyList<BoundingBox> bounds,KernelContributors contributors,Curves original,Curves actual,IReadOnlySet<string>? nativeSources=null)
        {
            _scene=scene;_plane=plane;_tol=tol;_cut=cut;_contributors=contributors;_actual=actual;_original=original;_nativeSources=nativeSources??new HashSet<string>();_curvedBounds=bounds;_oracle=new(scene.Faces,tol);
            foreach(var r in native.Records)
            {
                var projected=r.World.DuplicateCurve();projected.Transform(Transform.PlanarProjection(plane));
                var box=projected.GetBoundingBox(true);box.Inflate(tol);_tree.Insert(box,_native.Count);_native.Add((r.Source,r.Edge,r.Duplicate,r.Class,r.World,projected));
            }
            if(cut)foreach(var (piece,_) in original.Items.Concat(actual.Items))
                if(piece.Class is LineClass.Visible or LineClass.Hidden or LineClass.Unresolved && piece.Depth is not null && GeometryBase.FromJSON(piece.Depth) is Curve world)
                {if(!_represented.TryGetValue(piece.Source,out var curves))_represented.Add(piece.Source,curves=[]);curves.Add(world);}
        }
        private bool Represented(string source,Point3d world)=>!_cut||_represented.TryGetValue(source,out var curves)&&curves.Any(c=>c.ClosestPoint(world,out var t,_tol/16)&&c.PointAt(t).DistanceTo(world)<=_tol/16);
        private Vertex Project(Point3d p){var d=p-_plane.Origin;return new(d*_plane.XAxis,d*_plane.YAxis,d*_plane.ZAxis);}
        private Point3d World(Vertex p)=>_plane.PointAt(p.X,p.Y)+_plane.ZAxis*p.Z;
        private List<(int Index,double Parameter)> NativeAt(Point3d p)
        {
            var ids=new List<int>();_tree.Search(new Sphere(p,_tol),(_,a)=>ids.Add(a.Id));var hits=new List<(int,double)>();
            foreach(int id in ids)if(_native[id].Projected.ClosestPoint(p,out var t,_tol)&&_native[id].Projected.PointAt(t).DistanceTo(p)<=_tol)hits.Add((id,t));return hits;
        }
        private (VisibilityClass Class,int? Record,string Reason) Foreign(Point3d world,Vertex local,IReadOnlyList<(int Index,double Parameter)> hits)
        {
            if(!_curvedBounds.Any(b=>local.X>=b.Min.X-_tol&&local.X<=b.Max.X+_tol&&local.Y>=b.Min.Y-_tol&&local.Y<=b.Max.Y+_tol))return(VisibilityClass.Visible,null,"outside every curved-occluder bound");
            double roundoff=Math.Min(_tol/64,256*2.220446049250313e-16*Math.Max(1,world.DistanceTo(Point3d.Origin)));VisibilityClass? known=null;int? witness=null;
            foreach(var h in hits)
            {
                var r=_native[h.Index];if(r.Edge<0||r.Duplicate||r.Class is not (LineClass.Visible or LineClass.Hidden))continue;
                if(!r.World.ClosestPoint(world,out var t,roundoff)||r.World.PointAt(t).DistanceTo(world)>roundoff)continue;
                var c=r.Class==LineClass.Visible?VisibilityClass.Visible:VisibilityClass.Hidden;
                if(known is not null&&known!=c)return(VisibilityClass.Unresolved,null,"conflicting definite native intervals");known=c;witness=h.Index;
            }
            return(known??VisibilityClass.Unresolved,witness,known is null?"no definite coincident native query":"definite curved-only native query");
        }
        private ExactVisibilityOracle.PointResult Kernel(int id,double t)
        {
            var edge=_scene.Edges[id];
            if(!_kernelPairs.TryGetValue(id,out var pairs)){pairs=_oracle.Prepare(Math.Min(edge.A.X,edge.B.X),Math.Min(edge.A.Y,edge.B.Y),Math.Max(edge.A.X,edge.B.X),Math.Max(edge.A.Y,edge.B.Y));_kernelPairs.Add(id,pairs);}
            return _oracle.OnEdge(edge,t,pairs);
        }
        private ExactVisibilityOracle.PointResult Curved(int id,Point3d world)
        {
            if(!_nativePairs.TryGetValue(id,out var pairs)){var b=_native[id].World.GetBoundingBox(Transform.PlaneToPlane(_plane,Plane.WorldXY));b.Inflate(_tol);pairs=_oracle.Prepare(b.Min.X,b.Min.Y,b.Max.X,b.Max.Y);_nativePairs.Add(id,pairs);}
            return _oracle.At(Project(world),pairs);
        }
        public ReferenceDecision Check(Point3d p)
        {
            var actual=_actual.At(p,_tol,false);Point3d? actualWorld=null;
            if(actual.Index>=0&&actual.Piece?.Depth is { } json&&_actual.Items[actual.Index].Geometry is Curve projected)
            {
                if(!_actualDepth.TryGetValue(actual.Index,out var depth)){depth=(Curve)GeometryBase.FromJSON(json);_actualDepth.Add(actual.Index,depth);}
                if(projected.ClosestPoint(p,out var t,_tol))actualWorld=depth.PointAt(t);
            }
            bool matched=false,visible=false,uncertain=false,any=false,contact=false;var evidence=new List<object>();var hits=NativeAt(p);var kernelPoints=new List<(string Source,Point3d World)>();
            foreach(var candidate in _contributors.Candidates(p,_tol))
            {
                var edge=_scene.Edges[candidate.Edge];double t=candidate.Parameter;var local=new Vertex(edge.A.X+(edge.B.X-edge.A.X)*t,edge.A.Y+(edge.B.Y-edge.A.Y)*t,edge.A.Z+(edge.B.Z-edge.A.Z)*t);var world=World(local);
                if(!Represented(edge.Source,world))continue;any=true;
                kernelPoints.Add((edge.Source,world));
                matched|=actual.Piece?.Source==edge.Source&&actualWorld is { } aw&&aw.DistanceTo(world)<=_tol/16;
                var planar=Kernel(candidate.Edge,t);var foreign=planar.Class==VisibilityClass.Hidden?(VisibilityClass.Hidden,(int?)null,"planar Hidden decides union"):Foreign(world,local,hits);
                var kind=planar.Class==VisibilityClass.Hidden||foreign.Item1==VisibilityClass.Hidden?VisibilityClass.Hidden:foreign.Item1;
                visible|=kind==VisibilityClass.Visible;uncertain|=kind==VisibilityClass.Unresolved;contact|=kind==VisibilityClass.Visible&&planar.Contacts.Count!=0;
                evidence.Add(new{source=edge.Source,edge=candidate.Edge,parameter=t,planar.Class,planar.BlockingFace,planar.BlockingGap,planar.Contacts,curved=foreign.Item1,nativeRecord=foreign.Item2,reason=foreign.Item3,result=kind});
            }
            foreach(var h in hits)
            {
                var r=_native[h.Index];if(r.Edge>=0)continue;var world=r.World.PointAt(h.Parameter);if(!Represented(r.Source,world))continue;any=true;
                matched|=actual.Piece?.Source==r.Source&&actualWorld is { } aw&&aw.DistanceTo(world)<=_tol/16;
                var planar=Curved(h.Index,world);
                var kind=r.Class==LineClass.Hidden&&!r.Duplicate||planar?.Class==VisibilityClass.Hidden?VisibilityClass.Hidden:r.Class==LineClass.Visible&&!r.Duplicate?VisibilityClass.Visible:VisibilityClass.Unresolved;
                visible|=kind==VisibilityClass.Visible;uncertain|=kind==VisibilityClass.Unresolved;contact|=kind==VisibilityClass.Visible&&planar?.Contacts.Count>0;
                evidence.Add(new{source=r.Source,nativeRecord=h.Index,r.Class,r.Duplicate,planar,result=kind});
            }
            // A reduced native scene can omit a candidate that the full scene
            // retained as Unresolved. Include it; absence of a definite curved
            // answer must never be mistaken for Hidden coverage.
            var originals=new List<int>();_original.Tree.Search(new Sphere(p,_tol),(_,a)=>originals.Add(a.Id));
            foreach(int id in originals)
            {
                var (piece,g)=_original.Items[id];if(piece.Class!=LineClass.Unresolved||g is not Curve c||!c.ClosestPoint(p,out var t,_tol)||c.PointAt(t).DistanceTo(p)>_tol)continue;
                if(!_nativeSources.Contains(piece.Source)&&kernelPoints.Any(k=>k.Source==piece.Source))continue;
                any=true;
                if(piece.Depth is null){uncertain=true;evidence.Add(new{source=piece.Source,referencePiece=id,result=VisibilityClass.Unresolved,reason="native reference has no world support"});continue;}
                if(!_originalDepth.TryGetValue(id,out var depth)){depth=(Curve)GeometryBase.FromJSON(piece.Depth);_originalDepth.Add(id,depth);}var world=depth.PointAt(t);
                if(kernelPoints.Any(k=>k.Source==piece.Source&&k.World.DistanceTo(world)<=_tol/16)||hits.Any(h=>_native[h.Index].Edge<0&&_native[h.Index].Source==piece.Source&&_native[h.Index].World.PointAt(h.Parameter).DistanceTo(world)<=_tol/64))continue;
                if(!_originalPairs.TryGetValue(id,out var pairs)){var b=depth.GetBoundingBox(Transform.PlaneToPlane(_plane,Plane.WorldXY));b.Inflate(_tol);pairs=_oracle.Prepare(b.Min.X,b.Min.Y,b.Max.X,b.Max.Y);_originalPairs.Add(id,pairs);}
                var planar=_oracle.At(Project(world),pairs);var kind=planar.Class==VisibilityClass.Hidden?VisibilityClass.Hidden:VisibilityClass.Unresolved;uncertain|=kind==VisibilityClass.Unresolved;
                evidence.Add(new{source=piece.Source,referencePiece=id,planar,result=kind,reason="full-scene candidate absent from the definite subset answer"});
            }
            return new(!matched||!any?VisibilityClass.Unresolved:visible?VisibilityClass.Visible:uncertain?VisibilityClass.Unresolved:VisibilityClass.Hidden,matched,evidence,contact);
        }
        public void Dispose(){foreach(var r in _native)r.Projected.Dispose();foreach(var c in _actualDepth.Values)c.Dispose();foreach(var c in _originalDepth.Values)c.Dispose();foreach(var c in _represented.Values.SelectMany(c=>c))c.Dispose();_tree.Dispose();}
    }
}
