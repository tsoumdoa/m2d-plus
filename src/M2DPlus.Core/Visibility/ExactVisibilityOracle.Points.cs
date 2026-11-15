namespace M2DPlus.Core.Visibility;

public static partial class ExactVisibilityOracle
{
    public sealed record Contact(int Face,double Gap,bool Boundary);
    public sealed record PointResult(VisibilityClass Class,int? BlockingFace,double? BlockingGap,IReadOnlyList<Contact> Contacts);
    public sealed record PairSet(int[] Faces);

    /// <summary>Independent all-pairs reference. Each candidate bound visits every
    /// face; only exact scalar bound rejections are amortised across its samples.
    /// No production index, filtered predicate, early-out or interval union is used.</summary>
    public sealed class AllPairsPoints
    {
        private readonly Lazy<Polygon<Rational>>[] _faces;
        private readonly (double X0,double Y0,double X1,double Y1)[] _bounds;
        private readonly double _tolerance;
        public long CompletePairs { get; private set; }
        public long ExactPointPairs { get; private set; }
        public AllPairsPoints(IReadOnlyList<PlanarFace> faces,double tolerance)
        {
            if(!double.IsFinite(tolerance)||tolerance<=0)throw new ArgumentOutOfRangeException(nameof(tolerance));
            _tolerance=tolerance;_faces=faces.Select(f=>new Lazy<Polygon<Rational>>(()=>new(f))).ToArray();
            _bounds=faces.Select(f=>{var p=f.Loops.SelectMany(l=>l).ToArray();return(p.Min(v=>v.X),p.Min(v=>v.Y),p.Max(v=>v.X),p.Max(v=>v.Y));}).ToArray();
        }
        public PairSet Prepare(double x0,double y0,double x1,double y1,CancellationToken token=default)
        {
            var ids=new List<int>();
            for(int i=0;i<_bounds.Length;i++)
            {
                if((i&255)==0)token.ThrowIfCancellationRequested();var b=_bounds[i];CompletePairs++;
                if(x1<b.X0||x0>b.X1||y1<b.Y0||y0>b.Y1)continue;ids.Add(i);
            }
            return new(ids.ToArray());
        }
        public PointResult OnEdge(CandidateEdge edge,double parameter,PairSet pairs,CancellationToken token=default)
        {
            var a=P<Rational>.From(edge.A);var b=P<Rational>.From(edge.B);var t=Rational.From(parameter);
            return Check(new(a.X+(b.X-a.X)*t,a.Y+(b.Y-a.Y)*t,a.Z+(b.Z-a.Z)*t),pairs,token);
        }
        public PointResult At(Vertex point,PairSet pairs,CancellationToken token=default)=>Check(P<Rational>.From(point),pairs,token);
        private PointResult Check(P<Rational> point,PairSet pairs,CancellationToken token)
        {
            var zero=Rational.From(0);var tolerance=Rational.From(_tolerance);int? blocker=null;double? blockerGap=null;var contacts=new List<Contact>();
            foreach(int fi in pairs.Faces)
            {
                token.ThrowIfCancellationRequested();ExactPointPairs++;var face=_faces[fi].Value;
                if(face.Normal.Z.CompareTo(zero)==0)continue;
                int parity=0;bool boundary=false;
                foreach(var loop in face.Loops)
                {
                    int winding=0;
                    for(int i=0;i<loop.Length;i++)
                    {
                        var a=loop[i];var b=loop[(i+1)%loop.Length];
                        int orientation=((b.X-a.X)*(point.Y-a.Y)-(b.Y-a.Y)*(point.X-a.X)).CompareTo(zero);
                        boundary|=orientation==0&&(point.X-a.X).CompareTo(zero)*(point.X-b.X).CompareTo(zero)<=0&&(point.Y-a.Y).CompareTo(zero)*(point.Y-b.Y).CompareTo(zero)<=0;
                        if(a.Y.CompareTo(point.Y)<=0&&b.Y.CompareTo(point.Y)>0&&orientation>0)winding++;
                        if(a.Y.CompareTo(point.Y)>0&&b.Y.CompareTo(point.Y)<=0&&orientation<0)winding--;
                    }
                    parity+=Math.Abs(winding)&1;
                }
                if(!boundary&&(parity&1)==0)continue;
                var gap=(face.D-P<Rational>.Dot(face.Normal,point))/face.Normal.Z;
                if(gap.CompareTo(tolerance)>0)
                {
                    if(blockerGap is null||gap.Value<blockerGap){blocker=fi;blockerGap=gap.Value;}
                }
                else if(gap.CompareTo(zero-tolerance)>=0)contacts.Add(new(fi,gap.Value,boundary));
            }
            return new(blocker is null?VisibilityClass.Visible:VisibilityClass.Hidden,blocker,blockerGap,contacts);
        }
    }
}
