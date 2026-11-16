namespace M2DPlus.Core.Visibility;

public static partial class ExactVisibilityOracle
{
    public sealed record Contact(int Face,double Gap,bool Boundary,bool Foreign=false,bool ExactIncidence=false,bool ApproximateIncidence=false);
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
        private readonly IReadOnlyList<PlanarFace> _input;
        private readonly PlanarFace[] _contactInput;
        private readonly Lazy<Polygon<Rational>>[] _contactFaces;
        public long CompletePairs { get; private set; }
        public long ExactPointPairs { get; private set; }
        public AllPairsPoints(IReadOnlyList<PlanarFace> faces,double tolerance)
        {
            if(!double.IsFinite(tolerance)||tolerance<=0)throw new ArgumentOutOfRangeException(nameof(tolerance));
            _input=faces;_tolerance=tolerance;_faces=faces.Select(f=>new Lazy<Polygon<Rational>>(()=>new(f))).ToArray();
            _contactInput=faces.Select(ContactGeometry.Face).ToArray();
            _contactFaces=_contactInput.Select(f=>new Lazy<Polygon<Rational>>(()=>new(f))).ToArray();
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
            return Check(new(a.X+(b.X-a.X)*t,a.Y+(b.Y-a.Y)*t,a.Z+(b.Z-a.Z)*t),pairs,token,edge);
        }
        public PointResult At(Vertex point,PairSet pairs,CancellationToken token=default)=>Check(P<Rational>.From(point),pairs,token);

        /// <summary>Independent contact witness for the comparison gate. Winding
        /// numbers at symbolic half-neighbourhoods, no production event clipper.</summary>
        public bool IsFlushContact(CandidateEdge edge,double parameter,int faceIndex)
        {
            edge=ContactGeometry.Edge(edge);
            var face=_contactFaces[faceIndex].Value;var zero=Rational.From(0);
            if(_input[faceIndex].Source is not {} source||source==edge.Source)return false;
            var a=P<Rational>.From(edge.A);var b=P<Rational>.From(edge.B);var n=face.Normal;
            bool On(P<Rational> p)=>P<Rational>.Dot(n,p-face.Anchor).N.IsZero;
            if(!On(a)||!On(b)||face.Loops.SelectMany(l=>l).Any(p=>!On(p)))return false;
            Rational Abs(Rational v)=>v.N.Sign<0?zero-v:v;
            int drop=Abs(n.X).CompareTo(Abs(n.Y))>=0&&Abs(n.X).CompareTo(Abs(n.Z))>=0?0:Abs(n.Y).CompareTo(Abs(n.Z))>=0?1:2;
            (Rational X,Rational Y) Map(P<Rational> p)=>drop switch{0=>(p.Y,p.Z),1=>(p.X,p.Z),_=>(p.X,p.Y)};
            var start=Map(a);var end=Map(b);var t=Rational.From(parameter);
            var dx=end.X-start.X;var dy=end.Y-start.Y;
            var x=start.X+dx*t;var y=start.Y+dy*t;
            bool Interior(Polygon<Rational> polygon,int side)
            {
                var ux=(zero-dy)*Rational.From(side);var uy=dx*Rational.From(side);int parity=0;
                int Sign(Rational c,Rational e)=>c.N.Sign!=0?c.N.Sign:e.N.Sign;
                foreach(var loop in polygon.Loops)
                {
                    int winding=0;
                    for(int i=0;i<loop.Length;i++)
                    {
                        var p=Map(loop[i]);var q=Map(loop[(i+1)%loop.Length]);
                        int py=Sign(p.Y-y,zero-uy),qy=Sign(q.Y-y,zero-uy);
                        int cross=Sign((q.X-p.X)*(y-p.Y)-(q.Y-p.Y)*(x-p.X),(q.X-p.X)*uy-(q.Y-p.Y)*ux);
                        if(py<=0&&qy>0&&cross>0)winding++;
                        if(py>0&&qy<=0&&cross<0)winding--;
                    }
                    parity+=Math.Abs(winding)&1;
                }
                return (parity&1)!=0;
            }
            bool plus=Interior(face,1),minus=Interior(face,-1);
            var outward=_contactInput[faceIndex].OutwardNormal is {} normal?P<Rational>.From(normal):n;
            foreach(int id in edge.AdjacentFaces)
            {
                var own=_contactFaces[id].Value;var ownOutward=_contactInput[id].OutwardNormal is {} value?P<Rational>.From(value):own.Normal;
                if(P<Rational>.Dot(outward,ownOutward).N.Sign<=0||own.Loops.SelectMany(l=>l).Any(p=>!On(p)))continue;
                if(plus&&Interior(own,-1)||minus&&Interior(own,1))return true;
            }
            return false;
        }
        private PointResult Check(P<Rational> point,PairSet pairs,CancellationToken token,CandidateEdge? edge=null)
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
                bool foreign=edge is not null && edge.AdjacentFaces.Length!=0 && _input[fi].Source is {} source && source!=edge.Source;
                bool exact=false,approximate=false;
                if(foreign)
                {
                    var contactFace=_contactFaces[fi].Value;var contactEdge=ContactGeometry.Edge(edge!);
                    var r0=P<Rational>.Dot(contactFace.Normal,P<Rational>.From(contactEdge.A)-contactFace.Anchor);
                    var r1=P<Rational>.Dot(contactFace.Normal,P<Rational>.From(contactEdge.B)-contactFace.Anchor);
                    exact=r0.N.IsZero&&r1.N.IsZero
                        && contactFace.Loops.SelectMany(l=>l).All(p=>P<Rational>.Dot(contactFace.Normal,p-contactFace.Anchor).N.IsZero);
                    Rational Abs(Rational r)=>r.N.Sign<0?zero-r:r;
                    var band=tolerance*(Abs(contactFace.Normal.X)+Abs(contactFace.Normal.Y)+Abs(contactFace.Normal.Z));
                    approximate=!exact&&Abs(r0).CompareTo(band)<=0&&Abs(r1).CompareTo(band)<=0;
                }
                if(!exact&&gap.CompareTo(tolerance)>0)
                {
                    if(blockerGap is null||gap.Value<blockerGap){blocker=fi;blockerGap=gap.Value;}
                }
                else if(exact||gap.CompareTo(zero-tolerance)>=0)
                {
                    contacts.Add(new(fi,gap.Value,boundary,foreign,exact,approximate));
                }
            }
            return new(blocker is not null?VisibilityClass.Hidden:contacts.Any(c=>c.ApproximateIncidence)?VisibilityClass.Unresolved:VisibilityClass.Visible,blocker,blockerGap,contacts);
        }
    }
}
