using System.Diagnostics;
using M2DPlus.Rhino.Services;
using Rhino.Geometry;

namespace NativeHillclimb;
internal sealed class Experiments : IDisposable
{
    internal IReadOnlyList<PreparedLeaf> Sources { get; private set; } = Array.Empty<PreparedLeaf>();
    internal int Removed, Changed;
    internal double Seconds;
    private readonly List<GeometryBase> _owned = new();
    internal static Experiments Prepare(string mode, IReadOnlyList<PreparedLeaf> sources, PreparedScene scene, ViewRequest view)
    {
        var timer = Stopwatch.StartNew(); var result = new Experiments { Sources = sources };
        if (mode == "boxmesh")
        {
            result.Sources = sources.Select(s =>
            {
                if (!Box(s.Geometry, out var box)) return s;
                var mesh = Mesh.CreateFromBox(box, 1, 1, 1);
                if(mesh is null || !mesh.IsValid || !mesh.IsClosed) { mesh?.Dispose(); return s; }
                result._owned.Add(mesh); result.Changed++;
                return s with { Geometry = mesh };
            }).ToArray();
        }
        if (mode == "cull" && view.Cut is null && (view.Settings is null || view.Settings.FarClip == M2DPlus.Core.FarClipMode.None))
        {
            if (view.Settings is { } effective && view.Named is { } named) view = view with { Named = named with { Crop = effective.Crop } };
            var tolerance = view.Settings?.Tolerance ?? scene.Tolerance;
            var hidden = view.Settings?.Hidden ?? M2DPlus.Core.Settings.DefaultHidden;
            var frame = view.Resolve(scene.Bounds, tolerance);
            if(hidden && view.Named is not {Crop:true}) { result.Seconds=timer.Elapsed.TotalSeconds; return result; }
            var scale = scene.Bounds.IsValid ? scene.Bounds.GetCorners().Max(p => Math.Max(Math.Abs(p.X),Math.Max(Math.Abs(p.Y),Math.Abs(p.Z)))) : 1;
            scale = Math.Max(scale,Math.Max(Math.Abs(frame.Plane.OriginX),Math.Max(Math.Abs(frame.Plane.OriginY),Math.Abs(frame.Plane.OriginZ))));
            var margin = tolerance * 64 + Math.Max(1,scale) * 512 * Math.ScaleB(1d,-52);
            if(hidden && scene.Bounds.IsValid && scene.Bounds.GetCorners().All(p=>
            {
                frame.Plane.ClosestParameter(p,out var u,out var v);
                return u>=frame.X.Min+margin && u<=frame.X.Max-margin && v>=frame.Y.Min+margin && v<=frame.Y.Max-margin;
            })) { result.Seconds=timer.Elapsed.TotalSeconds; return result; }
            var local = Transform.PlaneToPlane(frame.Plane, Plane.WorldXY);
            var boxes = hidden ? Array.Empty<PreparedLeaf>() : sources.Where(s => !(view.Settings?.Glass == M2DPlus.Core.GlassMode.Clear && s.Section.Transparent) && Box(s.Geometry, out _)).ToArray();
            var faces = new List<Face>();
            // Only full boxes are certifiers. Keep them all, including mutually occluded boxes.
            foreach (var source in boxes)
            {
                Box(source.Geometry, out var box);
                var c = box.GetCorners();
                foreach (var ids in new[] { new[] {0,1,2,3}, new[] {4,5,6,7}, new[] {0,1,5,4}, new[] {1,2,6,5}, new[] {2,3,7,6}, new[] {3,0,4,7} })
                {
                    var a = c[ids[0]]; var b = c[ids[1]]; var d = c[ids[3]];
                    var u = b - a; var v = d - a; var plane = new Plane(a, u, v);
                    if (!plane.IsValid || !double.IsFinite(plane.Normal * frame.Plane.ZAxis) || Math.Abs(plane.Normal * frame.Plane.ZAxis) < 1e-8) continue;
                    var bounds = BoundingBox.Empty;
                    foreach (var id in ids) { var p = c[id]; p.Transform(local); bounds.Union(new Point3d(p.X, p.Y, 0)); }
                    faces.Add(new(plane, u.Length, v.Length, bounds));
                }
            }
            using var index = new RTree();
            for (var i = 0; i < faces.Count; i++) index.Insert(faces[i].Bounds, i);
            var certifiers = boxes.Select(s => s.Identity).ToHashSet(StringComparer.Ordinal);
            var retained = new List<PreparedLeaf>();
            foreach (var source in sources)
            {
                if(!source.Geometry.IsValid) { retained.Add(source); continue; }
                var bounds = source.Geometry.GetBoundingBox(true); var projected = source.Geometry.GetBoundingBox(local);
                // Parallel projection: a disjoint, expanded footprint cannot cast a ray into the crop.
                if (view.Named is { Crop: true } && projected.IsValid &&
                    (projected.Max.X < frame.X.Min - margin || projected.Min.X > frame.X.Max + margin || projected.Max.Y < frame.Y.Min - margin || projected.Min.Y > frame.Y.Max + margin))
                { result.Removed++; continue; }
                if (hidden || certifiers.Contains(source.Identity) || !bounds.IsValid || !projected.IsValid)
                { retained.Add(source); continue; }
                var center = projected.Center; center.Z = 0;
                var candidates = new List<int>(); index.Search(new Sphere(center, margin), (_, e) => candidates.Add(e.Id));
                var corners = bounds.GetCorners();
                var occluded = candidates.Any(i => faces[i].Covers(corners, frame.Plane.ZAxis, margin));
                if (occluded) result.Removed++; else retained.Add(source);
            }
            result.Sources = retained;
        }
        result.Seconds = timer.Elapsed.TotalSeconds; return result;
    }
    private sealed record Face(Plane Plane, double U, double V, BoundingBox Bounds)
    {
        internal bool Covers(Point3d[] corners, Vector3d towardCamera, double margin)
        {
            var denominator = Plane.Normal * towardCamera;
            var scale = Math.Max(1, Math.Max(U,V));
            foreach (var p in corners.Append(Plane.Origin)) scale = Math.Max(scale,Math.Max(Math.Abs(p.X),Math.Max(Math.Abs(p.Y),Math.Abs(p.Z))));
            // Deliberately loose floating-point envelope, amplified for grazing rays.
            // Fall back by failing the strict interior test when coordinates/conditioning dominate.
            margin += scale * 512 * Math.ScaleB(1d,-52) / (denominator * denominator);
            if(!double.IsFinite(margin)) return false;
            foreach (var p in corners)
            {
                var t = -Plane.DistanceTo(p) / denominator;
                if (!double.IsFinite(t) || t <= margin) return false;
                var hit = p + towardCamera * t;
                if(!hit.IsValid || !Plane.ClosestParameter(hit, out var u, out var v) || !double.IsFinite(u) || !double.IsFinite(v)) return false;
                if (u <= margin || u >= U - margin || v <= margin || v >= V - margin) return false;
            }
            return true;
        }
    }
    // Restrict the experiment to exact world-axis boxes: no tolerance-based shape recognition.
    internal static bool Box(GeometryBase geometry, out Box box)
    {
        box = default;
        if (geometry is not Brep { IsSolid: true, IsValid: true } b || b.Faces.Count != 6 || b.Vertices.Count != 8 || b.Edges.Count != 12) return false;
        var bounds = b.GetBoundingBox(true); var corners = bounds.GetCorners();
        if (!bounds.IsValid || bounds.Diagonal.X <= 0 || bounds.Diagonal.Y <= 0 || bounds.Diagonal.Z <= 0) return false;
        if (b.Vertices.Any(v => !corners.Contains(v.Location)) || corners.Any(p => !b.Vertices.Any(v => v.Location == p))) return false;
        var pairs = new HashSet<(int,int)>();
        foreach(var edge in b.Edges)
        {
            if(!edge.IsLinear(0)) return false;
            var a=Array.IndexOf(corners,edge.PointAtStart); var z=Array.IndexOf(corners,edge.PointAtEnd);
            if(a<0 || z<0 || Enumerable.Range(0,3).Count(i=>corners[a][i]!=corners[z][i])!=1 || !pairs.Add((Math.Min(a,z),Math.Max(a,z)))) return false;
        }
        var sides = new HashSet<int>();
        foreach (var face in b.Faces)
        {
            // A merely planar NURBS surface can have nonlinear UV parameterisation.
            // Only affine PlaneSurfaces plus exact trim/edge agreement certify a box.
            using var surface = face.UnderlyingSurface();
            if (!Affine(surface)) return false;
            if (!face.TryGetPlane(out var plane, 0) || face.Loops.Count != 1 || face.OuterLoop.Trims.Count != 4) return false;
            var n = plane.Normal;
            if (!((Math.Abs(n.X) == 1 && n.Y == 0 && n.Z == 0) || (Math.Abs(n.Y) == 1 && n.X == 0 && n.Z == 0) || (Math.Abs(n.Z) == 1 && n.X == 0 && n.Y == 0))) return false;
            var axis = n.X != 0 ? 0 : n.Y != 0 ? 1 : 2;
            var coordinate = plane.Origin[axis];
            var side = coordinate == bounds.Min[axis] ? axis * 2 : coordinate == bounds.Max[axis] ? axis * 2 + 1 : -1;
            if (side < 0 || !sides.Add(side)) return false;
            var faceEdges = new HashSet<int>();
            foreach (var trim in face.OuterLoop.Trims)
            {
                if (!trim.IsLinear(0) || trim.Edge is not { } edge || !faceEdges.Add(edge.EdgeIndex)) return false;
                var a = trim.PointAtStart; var z = trim.PointAtEnd;
                var first = face.PointAt(a.X,a.Y); var last = face.PointAt(z.X,z.Y);
                if (!((first == edge.PointAtStart && last == edge.PointAtEnd) || (first == edge.PointAtEnd && last == edge.PointAtStart))) return false;
            }
        }
        box = new Box(bounds); return true;
    }
    private static bool Affine(Surface surface)
    {
        if (surface is PlaneSurface) return true;
        if (surface is not NurbsSurface { IsRational:false } n || n.Degree(0) != 1 || n.Degree(1) != 1 || n.Points.CountU != 2 || n.Points.CountV != 2) return false;
        var a=n.Points.GetControlPoint(0,0).Location; var b=n.Points.GetControlPoint(1,0).Location;
        var c=n.Points.GetControlPoint(0,1).Location; var d=n.Points.GetControlPoint(1,1).Location;
        var u=0;var v=0;var constant=0;
        for(var axis=0;axis<3;axis++)
        {
            if(a[axis]==b[axis] && a[axis]==c[axis] && a[axis]==d[axis]) constant++;
            else if(a[axis]==c[axis] && b[axis]==d[axis]) u++;
            else if(a[axis]==b[axis] && c[axis]==d[axis]) v++;
            else return false;
        }
        return u==1 && v==1 && constant==1;
    }
    public void Dispose() { foreach (var geometry in _owned) geometry.Dispose(); }
}
