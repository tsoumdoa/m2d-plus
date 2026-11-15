using M2DPlus.Core.Visibility;
using Rhino.FileIO;
using Rhino.Geometry;

namespace M2DPlus.Rhino.Services;

/// <summary>Owned-value planar extraction shared by production routing and validation.</summary>
public sealed class PlanarExtraction
{
    public static M2DPlus.Core.LineClass OutputClass(VisibilityClass value) => value switch
    {
        VisibilityClass.Visible => M2DPlus.Core.LineClass.Visible,
        VisibilityClass.Hidden => M2DPlus.Core.LineClass.Hidden,
        _ => M2DPlus.Core.LineClass.Unresolved
    };
    public List<PlanarFace> Faces { get; } = [];
    public List<CandidateEdge> Edges { get; } = [];
    public List<bool> Profiles { get; } = [];
    public List<string> Excluded { get; } = [];
    public int EligibleObjects { get; private set; }
    public int OtherObjects { get; private set; }

#if !LICENSE_FREE
    internal static bool MayProduceKernelCandidates(GeometryBase geometry, double tolerance) => geometry switch
    {
        Point => true,
        Brep b => b.Faces.All(f => f.IsPlanar(tolerance)) && b.Edges.All(e => e.IsLinear(tolerance)),
        Mesh m => !RequiresNativeMesh(m,tolerance),
        Curve c => c.TryGetPolyline(out _) || c.IsLinear(tolerance),
        _ => true // Unsupported wrappers are classified by extraction, never skipped.
    };
    private static bool RequiresNativeMesh(Mesh mesh, double tolerance)
    {
        if (Enumerable.Range(0,mesh.TopologyEdges.Count).Any(i => mesh.TopologyEdges.GetConnectedFaces(i).Length > 2)) return true;
        foreach (var face in mesh.Faces.Where(f => !f.IsTriangle))
        {
            var quadPlane = new Plane(mesh.Vertices[face.A],mesh.Vertices[face.B],mesh.Vertices[face.C]);
            if (!quadPlane.IsValid || Math.Abs(quadPlane.DistanceTo(mesh.Vertices[face.D])) > tolerance) return true;
        }
        if (!Enumerable.Range(0,mesh.TopologyEdges.Count).Any(i => mesh.TopologyEdges.GetConnectedFaces(i).Length == 2 && !mesh.TopologyEdges.IsEdgeUnwelded(i))) return false;
        var vertices = mesh.Vertices.ToPoint3dArray();
        return Plane.FitPlaneToPoints(vertices,out var plane) != PlaneFitResult.Success || vertices.Any(p => Math.Abs(plane.DistanceTo(p)) > tolerance);
    }
#endif

    public bool Add(GeometryBase geometry, Transform transform, Plane view, double tolerance, string source, CancellationToken token = default, bool nativeMeshCandidates = false, bool tangents = false)
    {
#if LICENSE_FREE
        if (nativeMeshCandidates) throw new NotSupportedException("Production welded-mesh candidates require RhinoCommon; A2 retains its all-crease census.");
#endif
        var faces = new List<PlanarFace>(); var edges = new List<CandidateEdge>(); var profiles = new List<bool>();
        Vertex Project(Point3d p)
        {
            p.Transform(transform); var d = p - view.Origin;
            return new(d * view.XAxis, d * view.YAxis, d * view.ZAxis);
        }
        void Edge(Point3d a, Point3d b, bool profile = false) { edges.Add(new(Project(a), Project(b), source)); profiles.Add(profile); }
        bool BrepShape(Brep b)
        {
            if (b.Faces.Any(f => !f.IsPlanar(tolerance)) || b.Edges.Any(e => !e.IsLinear(tolerance))) return false;
            foreach (var f in b.Faces)
            {
                token.ThrowIfCancellationRequested();
                var loops = new List<Vertex[]>();
                foreach (var loop in f.Loops)
                {
                    if (loop.Trims.Any(t => t.Edge is null || t.StartVertex is null)) return false;
                    var points = loop.Trims.Select(t => Project(t.StartVertex.Location)).ToArray();
                    if (points.Length < 3) return false;
                    loops.Add(points);
                }
                if (loops.Count == 0) return false;
                faces.Add(new(loops.ToArray()));
            }
            foreach (var e in b.Edges)
            {
                token.ThrowIfCancellationRequested();
                var adj = e.AdjacentFaces();
                if (!tangents && adj.Length == 2 && b.Faces[adj[0]].TryGetPlane(out var p) && b.Faces[adj[1]].TryGetPlane(out var q)
                    && Vector3d.CrossProduct(p.Normal, q.Normal).Length <= 1e-12) continue;
                double Facing(int index)
                {
                    var face = b.Faces[index]; face.TryGetPlane(out var support); support.Transform(transform);
                    return (face.OrientationIsReversed ? -support.Normal : support.Normal) * view.ZAxis;
                }
                bool profile = adj.Length == 1 || adj.Length == 2 && (Facing(adj[0]) > 0) != (Facing(adj[1]) > 0);
                Edge(e.PointAtStart, e.PointAtEnd, profile);
            }
            return true;
        }
        bool ok;
        switch (geometry)
        {
            case Brep b: ok = BrepShape(b); break;
            case Extrusion e:
                using (var b = e.ToBrep()) ok = b is not null && BrepShape(b);
                break;
            case Surface s when s is not ClippingPlaneSurface:
                using (var b = s.ToBrep()) ok = b is not null && BrepShape(b);
                break;
            case Mesh original:
#if !LICENSE_FREE
                // Rhino HLD owns nonmanifold/nonplanar-quad candidates and interpolates
                // smooth silhouettes on welded nonplanar meshes;
                // topology-edge facing transitions are not equivalent candidate loci.
                // Retain the complete mesh in native, including its occlusion.
                if (nativeMeshCandidates && RequiresNativeMesh(original,tolerance)) { ok = false; break; }
#endif
                using (var mesh = original.DuplicateMesh())
                {
                    mesh.Faces.ConvertQuadsToTriangles(); mesh.FaceNormals.ComputeFaceNormals();
                    foreach (var f in mesh.Faces)
                    {
                        token.ThrowIfCancellationRequested();
                        faces.Add(new([[Project(mesh.Vertices[f.A]), Project(mesh.Vertices[f.B]), Project(mesh.Vertices[f.C])]]));
                    }
                    for (int i = 0; i < mesh.TopologyEdges.Count; i++)
                    {
                        token.ThrowIfCancellationRequested();
                        var adj = mesh.TopologyEdges.GetConnectedFaces(i);
                        if (adj.Length == 2)
                        {
                            var n = new Vector3d(mesh.FaceNormals[adj[0]]); var m = new Vector3d(mesh.FaceNormals[adj[1]]);
                            if (Vector3d.CrossProduct(n, m).Length <= 1e-12) continue;
#if !LICENSE_FREE
                            if (nativeMeshCandidates && !mesh.TopologyEdges.IsEdgeUnwelded(i))
                            {
                                // A welded mesh has smooth shading: interior tessellation edges
                                // are occluders, but only facing transitions generate linework.
                                n.Transform(transform); m.Transform(transform);
                                if ((n * view.ZAxis > 0) == (m * view.ZAxis > 0)) continue;
                            }
#endif
                        }
                        bool profile = adj.Length == 1;
                        if (adj.Length == 2)
                        {
                            var p = new Plane(Point3d.Origin, new Vector3d(mesh.FaceNormals[adj[0]])); p.Transform(transform);
                            var q = new Plane(Point3d.Origin, new Vector3d(mesh.FaceNormals[adj[1]])); q.Transform(transform);
                            profile |= (p.Normal * view.ZAxis > 0) != (q.Normal * view.ZAxis > 0);
                        }
                        var line = mesh.TopologyEdges.EdgeLine(i); Edge(line.From, line.To, profile);
                    }
                }
                ok = true; break;
            case Curve c when c.TryGetPolyline(out var polyline):
                for (int i = 1; i < polyline.Count; i++) Edge(polyline[i - 1], polyline[i]);
                ok = true; break;
            case Curve c when c.IsLinear(tolerance): Edge(c.PointAtStart, c.PointAtEnd); ok = true; break;
            default: ok = false; break;
        }
        if (!ok) { Excluded.Add(source + ":" + geometry.GetType().Name); OtherObjects++; return false; }
        Faces.AddRange(faces); Edges.AddRange(edges); Profiles.AddRange(profiles); EligibleObjects++; return true;
    }

    /// <summary>Borrowed definition leaves and cumulative transforms; model must outlive enumeration.</summary>
    public static IEnumerable<(GeometryBase Geometry, Transform Transform, string Source)> Leaves(File3dm model)
    {
        var objects = model.Objects.ToDictionary(o => o.Id);
        var definitions = model.AllInstanceDefinitions.ToDictionary(d => d.Id);
        IEnumerable<(GeometryBase, Transform, string)> Walk(Guid id, Transform x, string path, HashSet<Guid> seen)
        {
            if (!objects.TryGetValue(id, out var obj)) throw new InvalidDataException("Missing definition member");
            string name = path + "/" + (!string.IsNullOrEmpty(obj.Attributes.Name) ? obj.Attributes.Name : id.ToString());
            if (obj.Geometry is InstanceReferenceGeometry instance)
            {
                if (!seen.Add(instance.ParentIdefId) || !definitions.TryGetValue(instance.ParentIdefId, out var def))
                    throw new InvalidDataException("Missing/cyclic definition");
                foreach (var child in def.GetObjectIds())
                    foreach (var leaf in Walk(child, x * instance.Xform, name, new(seen))) yield return leaf;
            }
            else if (obj.Geometry is not ClippingPlaneSurface) yield return (obj.Geometry, x, name);
        }
        foreach (var o in model.Objects)
            if (!o.Attributes.IsInstanceDefinitionObject)
                foreach (var leaf in Walk(o.Id, Transform.Identity, "", [])) yield return leaf;
    }
}
