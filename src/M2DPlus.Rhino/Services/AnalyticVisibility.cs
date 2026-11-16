using System.Runtime.CompilerServices;
using M2DPlus.Core;
using M2DPlus.Core.Visibility;
using Rhino.Geometry;
using Point = Rhino.Geometry.Point;

namespace M2DPlus.Rhino.Services;

/// <summary>
/// The third class of the union composition: exact analytic solids (sphere, cylinder between two planes, hollow
/// cylinder, extruded arc). Recognition is strict and fails closed; a body it refuses stays on native HLD.
///
/// Which recognised sources leave HLD. H(c) is a union over all occluders, so a source S may leave only if nothing is
/// lost on either side of it:
///   as an occluder   S hides kernel edges, native curves, points and analytic candidates. All four are decided here
///                    with the exact solid (Occlude, Append, Point), so S is not needed in HLD to hide anything.
///   as a candidate   S's own lines (rims, outlines) are hidden by planar faces (the kernel point predicate, through
///                    the existing curve/plane events), by analytic solids (exact, including S itself) and by native
///                    bodies. Only HLD knows the last. So S leaves only when no unrecognised body can hide it: none
///                    whose view box overlaps S's in the drawing and reaches in front of S's far side.
/// A recognised source that fails the second test stays in HLD whole, as before. Its exact solid is still used to
/// hide the candidates of the sources that did leave, because HLD never sees those candidates.
/// </summary>
internal sealed class AnalyticVisibility
{
    // ---------------------------------------------------------------- recognition

    internal abstract record Shape;
    internal sealed record SphereShape(Point3d Centre, double Radius) : Shape;
    /// <summary>Caps: a point on each cap plane and its normal pointing out of the solid.</summary>
    internal sealed record CylinderShape(Point3d Origin, Vector3d Axis, double Radius, double InnerRadius, Plane Cap0, Plane Cap1) : Shape;
    /// <summary>Angles are measured from X towards Axis × X.</summary>
    internal sealed record SheetShape(Point3d Origin, Vector3d Axis, Vector3d X, double Radius, double Start, double Sweep, double Height0, double Height1) : Shape;
    /// <summary>A tube cut by planes and cylinders (see <see cref="CutTubeRecognition"/>). Surfaces from index Real on are
    /// the two planes that end the tube and are not faces. FaceBound and EdgeBound are the stated accuracy.</summary>
    internal sealed record CutTubeShape(IReadOnlyList<CutTubeRecognition.Surface> Surfaces, BoundingBox Bounds, IReadOnlyList<CutTubeRecognition.Edge> Edges,
        int[] FaceSurface, int Real, double FaceBound, double EdgeBound, IReadOnlyList<int> Inside, IReadOnlyList<int> Outside) : Shape
    {
        /// <summary>How the cells were learned; for diagnostics.</summary>
        internal string Note { get; init; } = "";
    }

    private sealed class Entry { internal double Tolerance; internal Shape? Shape; internal string? Refusal; }
    private static readonly ConditionalWeakTable<GeometryBase, Entry> Cache = new();

    /// <summary>The exact shape of a whole body, or null. Cached on the owned geometry object.</summary>
    internal static Shape? Recognise(GeometryBase geometry, double tolerance) => Examine(geometry, tolerance).Shape;

    /// <summary>The exact shape of a whole body, or why it is not one (null for geometry that is not a body at all).</summary>
    internal static (Shape? Shape, string? Refusal) Examine(GeometryBase geometry, double tolerance)
    {
        if (geometry is not (Brep or Extrusion or Surface)) return (null, null);
        if (Cache.TryGetValue(geometry, out var entry) && entry.Tolerance == tolerance) return (entry.Shape, entry.Refusal);
        Brep? temporary = null; Shape? shape = null; string? refusal = null;
        try
        {
            var brep = geometry as Brep ?? (temporary = geometry is Extrusion e ? e.ToBrep() : ((Surface)geometry).ToBrep());
            if (brep is null) refusal = "no Brep form";
            else shape = RecogniseBrep(brep, tolerance, out refusal);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { shape = null; refusal = "recognition failed: " + ex.GetType().Name; }
        finally { temporary?.Dispose(); }
        if (shape is null) refusal ??= "not recognised"; else refusal = null;
        Cache.AddOrUpdate(geometry, new Entry { Tolerance = tolerance, Shape = shape, Refusal = refusal });
        return (shape, refusal);
    }

    private static Shape? RecogniseBrep(Brep brep, double tol, out string? why)
    {
        string? reason = null; Shape? No(string text) { reason = text; return null; }
        var shape = Classify(brep, tol, No);
        if (shape is null && reason != PlanarOnly && brep.IsSolid)
        {   // Not one of the plain shapes: perhaps a tube with cut ends.
            string? plain = reason; shape = CutTubeRecognition.Recognise(brep, tol, No);
            if (shape is null) reason = reason == PlanarOnly ? plain : $"{plain}; as a cut tube: {reason}";
        }
        why = reason; return shape;
    }
    internal const string PlanarOnlyReason = PlanarOnly;

    private static Shape? Classify(Brep brep, double tol, Func<string, Shape?> no)
    {
        // Surface types first: cheap, and what decides most bodies. Validity only for those that pass.
        var cylinders = new List<(int Face, Cylinder C)>(); var planes = new List<(int Face, Plane P)>(); var spheres = new List<Sphere>();
        foreach (var face in brep.Faces)
        {
            if (face.TryGetPlane(out var plane, tol)) planes.Add((face.FaceIndex, plane));
            else if (face.TryGetCylinder(out var cylinder, tol)) cylinders.Add((face.FaceIndex, cylinder));
            else if (face.TryGetSphere(out var sphere, tol)) spheres.Add(sphere);
            else return no("a face that is not a plane, cylinder or sphere");
        }
        if (!brep.IsValid) return no("invalid Brep");
        if (!brep.IsSolid) return brep.Faces.Count == 1 && cylinders.Count == 1 ? Sheet(brep, cylinders[0].C, tol, no) : no("open Brep that is not one cylindrical face");
        if (!brep.IsManifold || brep.SolidOrientation == BrepSolidOrientation.None) return no("non-manifold or unoriented solid");
        double volume = Math.Abs(brep.GetVolume()); if (!(volume > 0)) return no("no volume");   // a mirrored instance is inside out
        if (spheres.Count == brep.Faces.Count)
        {
            var s = spheres[0];
            if (!(s.Radius > 8 * tol) || spheres.Any(x => x.Center.DistanceTo(s.Center) > tol || Math.Abs(x.Radius - s.Radius) > tol)) return no("spherical faces of different spheres");
            return Math.Abs(volume - 4.0 / 3 * Math.PI * Math.Pow(s.Radius, 3)) <= 1e-4 * volume ? new SphereShape(s.Center, s.Radius) : no("sphere volume differs from the closed form");
        }
        if (spheres.Count != 0) return no("spherical and other faces in one body");
        if (cylinders.Count == 0) return no(PlanarOnly);
        if (planes.Count < 2) return no("fewer than two planar end faces");
        var axis = cylinders[0].C.Axis; if (!axis.Unitize()) return no("degenerate cylinder axis"); var origin = cylinders[0].C.Center; var line = new Line(origin, origin + axis);
        double outer = cylinders.Max(c => c.C.Radius), inner = cylinders.Min(c => c.C.Radius);
        // Directions are compared as distances over the body's extent, not as angles: an exported pipe whose faces
        // differ by rounding in the eighth decimal is still one pipe, and a real kink on a long pipe is not.
        var box = brep.GetBoundingBox(false); double span = box.IsValid ? box.Diagonal.Length : 0; if (!(span > 0)) return no("no extent");
        bool Along(Vector3d a, Vector3d b) => a.Unitize() && b.Unitize() && Vector3d.CrossProduct(a, b).Length * span <= tol;
        foreach (var (_, c) in cylinders)
        {
            if (!Along(c.Axis, axis) || line.DistanceTo(c.Center, false) > tol) return no("cylindrical faces on different axes");
            if (Math.Abs(c.Radius - outer) > tol && Math.Abs(c.Radius - inner) > tol) return no("more than two radii");
        }
        if (outer - inner <= tol) inner = 0; else if (inner <= 8 * tol || outer - inner <= 8 * tol) return no("wall or bore thinner than eight tolerances");
        if (outer <= 8 * tol) return no("radius below eight tolerances");
        // Every planar face lies on one of exactly two cap planes.
        var caps = new List<Plane>();
        foreach (var (_, p) in planes)
        {
            if (caps.Any(k => Along(k.Normal, p.Normal) && Math.Abs(k.DistanceTo(p.Origin)) <= tol)) continue;
            caps.Add(p); if (caps.Count > 2) return no("end faces on more than two planes");
        }
        if (caps.Count != 2) return no("one end plane only");
        // Each cap meets the axis; the two must not meet inside the outer cylinder.
        var cuts = new double[2]; var reach = new double[2];
        for (int i = 0; i < 2; i++)
        {
            var n = caps[i].Normal; double un = axis * n; if (Math.Abs(un) < 1e-3) return no("an end plane along the axis");
            cuts[i] = ((caps[i].Origin - origin) * n) / un; reach[i] = outer * Math.Sqrt(Math.Max(0, 1 - un * un)) / Math.Abs(un);
        }
        double length = Math.Abs(cuts[1] - cuts[0]);
        if (length <= reach[0] + reach[1] + 8 * tol) return no("end planes meet inside the cylinder");
        double expected = Math.PI * (outer * outer - inner * inner) * length;
        if (Math.Abs(volume - expected) > 1e-4 * expected) return no("volume differs from the closed form (holes, notches or a gap)");
        // Edges: only rims (a cylinder against a cap) may be creases. Anything else must be smooth or a seam.
        var planar = planes.Select(p => p.Face).ToHashSet();
        foreach (var edge in brep.Edges)
        {
            var faces = edge.AdjacentFaces();
            if (faces.Length == 1 && edge.Valence == EdgeAdjacency.Interior) continue;                // seam
            if (faces.Length != 2) return no("an edge that is not shared by two faces");
            if (planar.Contains(faces[0]) != planar.Contains(faces[1]))
            {   // A rim. The lines drawn are the exact rims (cylinder against cap plane), so the stored edge must be
                // that curve to tolerance: a Brep whose edges are looser than the model tolerance is refused.
                for (int k = 0; k <= 8; k++)
                {
                    var q = edge.PointAt(edge.Domain.ParameterAt(k / 8.0)); double radial = line.DistanceTo(q, false);
                    if (Math.Abs(radial - outer) > tol && Math.Abs(radial - inner) > tol || caps.All(c => Math.Abs(c.DistanceTo(q)) > tol))
                        return no("a rim edge further than a tolerance from the exact rim");
                }
                continue;
            }
            if (!edge.IsSmoothManifoldEdge(RhinoMathAngle)) return no("a crease that is not a rim");
        }
        var middle = origin + axis * ((cuts[0] + cuts[1]) / 2); var oriented = new Plane[2];
        for (int i = 0; i < 2; i++)
        {
            var n = caps[i].Normal; var on = origin + axis * cuts[i];
            if ((middle - on) * n > 0) n = -n;
            oriented[i] = new Plane(on, n);
        }
        return new CylinderShape(origin, axis, outer, inner, oriented[0], oriented[1]);
    }
    private const double RhinoMathAngle = Math.PI / 180;

    /// <summary>An extruded arc: one cylindrical face bounded by rulings and by arcs in planes square to the axis.</summary>
    private static Shape? Sheet(Brep brep, Cylinder sheet, double tol, Func<string, Shape?> refuse)
    {
        Shape? no = refuse("open cylindrical face that is not an extruded arc");
        var u = sheet.Axis; if (!u.Unitize() || sheet.Radius <= 8 * tol) return no; var o = sheet.Center;
        var x = sheet.BasePlane.XAxis; x -= u * (x * u); if (!x.Unitize()) return no; var y = Vector3d.CrossProduct(u, x);
        var axis = new Line(o, o + u);
        double h0 = double.PositiveInfinity, h1 = double.NegativeInfinity, phi = double.NaN, sweep = double.NaN; int rulings = 0, arcs = 0;
        static double Mod(double v) { v %= 2 * Math.PI; return v < 0 ? v + 2 * Math.PI : v; }
        foreach (var edge in brep.Edges)
        {
            if (edge.Valence == EdgeAdjacency.Interior) { if (!edge.IsLinear(tol)) return no; continue; }   // seam of a full turn
            if (edge.Valence != EdgeAdjacency.Naked) return no;
            if (edge.IsLinear(tol))
            {
                var d = edge.PointAtEnd - edge.PointAtStart;
                if (!d.Unitize() || d.IsParallelTo(u, 1e-7) == 0 || Math.Abs(axis.DistanceTo(edge.PointAtStart, false) - sheet.Radius) > tol) return no;
                rulings++; continue;
            }
            if (!edge.TryGetArc(out var arc, tol) || Math.Abs(arc.Radius - sheet.Radius) > tol || arc.Plane.Normal.IsParallelTo(u, 1e-7) == 0 || axis.DistanceTo(arc.Center, false) > tol) return no;
            double h = (arc.Center - o) * u; h0 = Math.Min(h0, h); h1 = Math.Max(h1, h); arcs++;
            double Angle(Point3d q) => Math.Atan2((q - o) * y, (q - o) * x);
            double a0 = Angle(edge.PointAtStart), am = Angle(edge.PointAt(edge.Domain.Mid)), a1 = Angle(edge.PointAtEnd);
            double d1 = Mod(am - a0), d2 = edge.IsClosed ? 2 * Math.PI : Mod(a1 - a0);
            double start = d1 <= d2 ? a0 : a1, span = d1 <= d2 ? d2 : 2 * Math.PI - d2;
            if (double.IsNaN(phi)) { phi = start; sweep = span; }
            else if (Math.Abs(span - sweep) > 1e-7 || Math.Min(Mod(start - phi), Mod(phi - start)) > 1e-7 && sweep < 2 * Math.PI - 1e-9) return no;
        }
        if (arcs != 2 || h1 - h0 <= 8 * tol || double.IsNaN(phi) || !(sweep > 1e-6)) return no;
        bool full = sweep >= 2 * Math.PI - 1e-9;
        if (rulings != (full ? 0 : 2)) return no;
        double area = sheet.Radius * sweep * (h1 - h0);
        return Math.Abs(brep.GetArea() - area) <= 1e-4 * area ? new SheetShape(o, u, x, sheet.Radius, phi, full ? 2 * Math.PI : sweep, h0, h1) : null;
    }

    // ---------------------------------------------------------------- planning

    private static bool Body(GeometryBase g) => g is Brep or Mesh or Surface or Extrusion or SubD;
    private static bool Enabled(ViewRequest view, NativeSettings settings) =>
        settings.Analytic && !settings.NativeOnly && view.Settings?.Tangents != true && view.Settings?.Seams != true;

    /// <summary>For each geometry: its shape (null when not recognised), whether it leaves HLD, and for a recognised one
    /// that stays, the index of an unrecognised body that may hide it (-1 when its own box is invalid).</summary>
    private static (Shape? Shape, bool Leaves, int Blocker)[] Plan(IReadOnlyList<GeometryBase> geometries, Plane plane, double tol)
    {
        var plan = new (Shape?, bool, int)[geometries.Count]; var toView = Transform.PlaneToPlane(plane, Plane.WorldXY);
        var foreign = new List<(BoundingBox Box, int Index)>(); bool any = false;
        for (int i = 0; i < geometries.Count; i++)
        {
            var shape = Recognise(geometries[i], tol); plan[i] = (shape, false, -1); any |= shape is not null;
        }
        if (!any) return plan;
        for (int i = 0; i < geometries.Count; i++)
            if (plan[i].Item1 is null && Body(geometries[i])) foreign.Add((geometries[i].GetBoundingBox(toView), i));
        for (int i = 0; i < geometries.Count; i++)
        {
            if (plan[i].Item1 is null) continue;
            var box = geometries[i].GetBoundingBox(toView); int blocker = -1; bool exposed = !box.IsValid;
            if (!exposed)
                foreach (var (f, index) in foreign)
                    if (!f.IsValid || f.Min.X <= box.Max.X + tol && f.Max.X >= box.Min.X - tol &&
                        f.Min.Y <= box.Max.Y + tol && f.Max.Y >= box.Min.Y - tol && f.Max.Z >= box.Min.Z - tol) { exposed = true; blocker = index; break; }
            plan[i] = (plan[i].Item1, !exposed, blocker);
        }
        return plan;
    }

    /// <summary>Would any source of this view leave HLD? Used to choose the composed route for scenes with no planar source.</summary>
    internal static bool Applies(IEnumerable<GeometryBase> geometries, Plane plane, double tolerance, ViewRequest view, NativeSettings settings) =>
        Enabled(view, settings) && Plan(geometries.ToArray(), plane, tolerance).Any(p => p.Leaves);

    // ---------------------------------------------------------------- one view

    /// <param name="Slack">How far the curve may be from the surfaces it lies on (a cut tube's stored edge).</param>
    /// <param name="Snap">Takes a point of the drawn curve to the exact curve it stands for (the intersection of the
    /// two surfaces a stored edge joins, or a cylinder's outline). What is classified is the exact curve; what is drawn
    /// is the stored one, within the body's stated edge bound of it.</param>
    private sealed record Candidate(Curve Curve, VisibilityInput Input, int Solid, int OnSurface, bool Tangent, Func<Point3d, bool> Profile, double Slack = 0,
        IReadOnlyList<(int Solid, int OnSurface)>? Contacts = null, Func<Point3d, Point3d>? Snap = null);

    internal static readonly AnalyticVisibility None = new();
    private readonly AnalyticOccluders _removed = new([]), _all = new([]);
    private readonly List<Candidate> _candidates = [];
    private readonly Plane _plane; private readonly double _tol; private readonly int _recognised;
    internal bool Any => _removed.Count != 0;
    internal string Route => Any ? $"; exact analytic solids={_removed.Count}" : "";
    private readonly List<string> _notes = [];
    internal IEnumerable<string> Diagnostics => (_recognised == 0 ? [] : new[]
        { $"Analytic solids: recognised={_recognised}; exact={_removed.Count}; kept on native HLD because an unrecognised body may hide them={_recognised - _removed.Count}; own candidate curves={_candidates.Count}." })
        .Concat(_notes);

    /// <summary>Info for the owner: which recognised solids stayed on native HLD and the body that may hide each, and
    /// why round bodies were refused. Bodies with planar faces only are not listed. Lists are capped.</summary>
    private void Explain(List<VisibilityInput> curved, (Shape? Shape, bool Leaves, int Blocker)[] plan, double tol)
    {
        const int limit = 8;
        var kept = Enumerable.Range(0, plan.Length).Where(i => plan[i].Shape is not null && !plan[i].Leaves).ToArray();
        if (kept.Length != 0)
            _notes.Add("Analytic solids kept on native HLD: " + string.Join("; ", kept.Take(limit).Select(i =>
                $"{curved[i].Source.Identity} (may be hidden by {(plan[i].Blocker >= 0 ? curved[plan[i].Blocker].Source.Identity : "an unbounded body")})")) +
                (kept.Length > limit ? $"; and {kept.Length - limit} more." : "."));
        var cut = Enumerable.Range(0, plan.Length).Where(i => plan[i].Shape is CutTubeShape).ToArray();
        if (cut.Length != 0)
            _notes.Add("Cut tubes drawn exactly to a stated bound (faces to their plane or cylinder; drawn edges to the surfaces they join): " +
                string.Join("; ", cut.Take(limit).Select(i => { var t = (CutTubeShape)plan[i].Shape!; return $"{curved[i].Source.Identity} faces {t.FaceBound:G3}, edges {t.EdgeBound:G3}"; })) +
                (cut.Length > limit ? $"; and {cut.Length - limit} more." : "."));
        var refused = Enumerable.Range(0, plan.Length).Where(i => plan[i].Shape is null)
            .Select(i => (Index: i, Why: Examine(curved[i].Geometry, tol).Refusal)).Where(r => r.Why is not null && r.Why != PlanarOnly)
            .GroupBy(r => r.Why!, StringComparer.Ordinal).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).ToArray();
        if (refused.Length != 0)
            _notes.Add("Not exact analytic solids (native HLD): " + string.Join("; ", refused.Take(limit).Select(g =>
                $"{g.Count()} x {g.Key} ({string.Join(", ", g.Take(3).Select(r => curved[r.Index].Source.Identity))}{(g.Count() > 3 ? ", ..." : "")})")) +
                (refused.Length > limit ? $"; and {refused.Length - limit} more reasons." : "."));
    }
    private const string PlanarOnly = "planar faces only";

    private AnalyticVisibility() { }

    private Vertex Project(Point3d p) => Project(_plane, p);
    internal static Vertex Project(Plane plane, Point3d p) { var d = p - plane.Origin; return new(d * plane.XAxis, d * plane.YAxis, d * plane.ZAxis); }
    private static Vertex Direction(Plane plane, Vector3d d) => new(d * plane.XAxis, d * plane.YAxis, d * plane.ZAxis);

    /// <summary>The exact solid of a recognised shape in the coordinates of a view plane (Z towards the viewer).</summary>
    internal static AnalyticSolid Solid(Shape shape, Plane plane) => shape switch
    {
        SphereShape s => new AnalyticSphere(Project(plane, s.Centre), s.Radius),
        CylinderShape c => new AnalyticCylinder(Project(plane, c.Origin), Direction(plane, c.Axis), c.Radius, c.InnerRadius,
            [(Project(plane, c.Cap0.Origin), Direction(plane, c.Cap0.Normal)), (Project(plane, c.Cap1.Origin), Direction(plane, c.Cap1.Normal))]),
        SheetShape s => new AnalyticCylinderSheet(Project(plane, s.Origin), Direction(plane, s.Axis), Direction(plane, s.X), s.Radius, s.Start, s.Sweep, s.Height0, s.Height1),
        CutTubeShape t => CutSolid(t, plane),
        _ => throw new InvalidOperationException("Unknown analytic shape")
    };

    private static AnalyticCsg CutSolid(CutTubeShape t, Plane plane)
    {
        var min = new Vertex(double.MaxValue, double.MaxValue, double.MaxValue); var max = new Vertex(double.MinValue, double.MinValue, double.MinValue);
        foreach (var corner in t.Bounds.GetCorners())
        {
            var v = Project(plane, corner);
            min = new(Math.Min(min.X, v.X), Math.Min(min.Y, v.Y), Math.Min(min.Z, v.Z)); max = new(Math.Max(max.X, v.X), Math.Max(max.Y, v.Y), Math.Max(max.Z, v.Z));
        }
        return new AnalyticCsg(t.Surfaces.Select(s => new CsgPrimitive(s.Cylinder, Project(plane, s.Origin), Direction(plane, s.Direction), s.Radius, s.Role, s.Slack)).ToArray(), min, max, t.Inside, t.Outside);
    }

    /// <summary>The surfaces of a shape that another solid's line can lie on, with the bit that names each to the solid.</summary>
    private static IEnumerable<(bool Cylinder, Point3d Origin, Vector3d Direction, double Radius, int Bit)> Faces(Shape shape)
    {
        switch (shape)
        {
            case CylinderShape c:
                yield return (true, c.Origin, c.Axis, c.Radius, AnalyticSolid.Outer);
                if (c.InnerRadius > 0) yield return (true, c.Origin, c.Axis, c.InnerRadius, AnalyticSolid.Inner);
                break;
            case CutTubeShape t:
                for (int i = 0; i < t.Real; i++) yield return (t.Surfaces[i].Cylinder, t.Surfaces[i].Origin, t.Surfaces[i].Direction, t.Surfaces[i].Radius, 1 << i);
                break;
        }
    }

    /// <summary>
    /// Contact: a line of one solid that lies on a surface another solid shares (a pipe coped onto a neighbour lies on
    /// the neighbour's outer surface; mitred members share a rim). Only where a cut tube is involved: the plain
    /// shapes keep their own rule for coincident members (the closed-solid convention).
    /// </summary>
    private void Contacts(List<Shape> shapes)
    {
        if (!shapes.Any(s => s is CutTubeShape)) return;
        var faces = shapes.Select(s => Faces(s).ToArray()).ToArray();
        for (int k = 0; k < _candidates.Count; k++)
        {
            var c = _candidates[k]; var mine = faces[c.Solid].Where(f => (f.Bit & c.OnSurface) != 0).ToArray();
            double near = _tol + c.Slack; List<(int, int)>? contacts = null;
            // The curve that is classified (the exact rim or outline when the candidate snaps), at nine points.
            var points = Enumerable.Range(0, 9).Select(i => { var q = c.Curve.PointAt(c.Curve.Domain.ParameterAt(i / 8.0)); return c.Snap is null ? q : c.Snap(q); }).ToArray();
            for (int other = 0; other < shapes.Count; other++)
            {
                if (other == c.Solid || shapes[other] is not CutTubeShape && shapes[c.Solid] is not CutTubeShape) continue;
                int bits = 0;
                foreach (var f in faces[other])
                {
                    // The same surface as one the candidate lies on (a coped end lies on the neighbour's cylinder).
                    bool same = mine.Any(m => f.Cylinder == m.Cylinder && (f.Cylinder
                        ? Math.Abs(f.Radius - m.Radius) <= near && Vector3d.CrossProduct(f.Direction, m.Direction).Length * Math.Max(1, f.Radius) <= near && new Line(f.Origin, f.Origin + f.Direction).DistanceTo(m.Origin, false) <= near
                        : Vector3d.CrossProduct(f.Direction, m.Direction).Length <= 1e-9 && Math.Abs(new Plane(f.Origin, f.Direction).DistanceTo(m.Origin)) <= near));
                    // Or a different surface the exact curve lies on all the same (the rim of a mitre lies on both
                    // members' cylinders): every point within the graze width of it. Not for an outline: its ray is
                    // tangent to its own cylinder, not to this surface.
                    if (!same && !c.Tangent && c.Slack == 0)
                    {
                        double reach = Math.Max(AnalyticSolid.Graze(_tol), 1e-12 * (1 + f.Radius + Math.Abs(f.Origin.X) + Math.Abs(f.Origin.Y) + Math.Abs(f.Origin.Z)));
                        same = points.All(q =>
                        {
                            var w = q - f.Origin;
                            if (!f.Cylinder) return Math.Abs(w * f.Direction) <= reach;
                            w -= f.Direction * (w * f.Direction); return Math.Abs(w.Length - f.Radius) <= reach;
                        });
                    }
                    if (same) bits |= f.Bit;
                }
                if (bits != 0) (contacts ??= []).Add((other, bits));
            }
            if (contacts is not null) _candidates[k] = c with { Contacts = contacts };
        }
    }

    private AnalyticVisibility(List<VisibilityInput> curved, Plane plane, double tol, List<GeometryBase> owned, CancellationToken token)
    {
        _plane = plane; _tol = tol;
        var plan = Plan(curved.Select(c => c.Geometry).ToArray(), plane, tol);
        _recognised = plan.Count(p => p.Shape is not null);
        Explain(curved, plan, tol);
        if (!plan.Any(p => p.Leaves)) return;
        var all = new List<AnalyticSolid>(); var removed = new List<AnalyticSolid>(); var leaving = new HashSet<VisibilityInput>(); var shapes = new List<Shape>();
        try
        {
            for (int i = 0; i < plan.Length; i++)
            {
                token.ThrowIfCancellationRequested();
                if (plan[i].Shape is not { } shape) continue;
                var solid = Solid(shape, plane); int index = all.Count; all.Add(solid); shapes.Add(shape);
                if (!plan[i].Leaves) continue;
                removed.Add(solid); leaving.Add(curved[i]);
                foreach (var candidate in Candidates(shape, curved[i], index)) { owned.Add(candidate.Curve); _candidates.Add(candidate); }
            }
        }
        catch (ArgumentException)
        {   // A shape the exact solids refuse after all: nothing leaves HLD.
            _candidates.Clear(); return;
        }
        _all = new(all); _removed = new(removed);
        Contacts(shapes);
        curved.RemoveAll(leaving.Contains);
    }

    /// <summary>Takes the sources that may leave HLD out of `curved`. Their candidate curves are added to `owned`.</summary>
    internal static AnalyticVisibility Take(List<VisibilityInput> curved, Plane plane, double tolerance, ViewRequest view, NativeSettings settings,
        List<GeometryBase> owned, CancellationToken token)
    {
        if (!Enabled(view, settings) || curved.Count == 0) return None;
        var before = settings.ObserveExactSolids is null ? null : curved.ToArray();
        var taken = new AnalyticVisibility(curved, plane, tolerance, owned, token);
        if (before is not null && taken.Any) settings.ObserveExactSolids!(before.Except(curved).ToArray());
        return taken;
    }

    /// <summary>The lines native draws for these bodies with tangent edges and seams off: rims and outlines.</summary>
    private IEnumerable<Candidate> Candidates(Shape shape, VisibilityInput input, int solid)
    {
        var e = _plane.ZAxis;
        switch (shape)
        {
            case SphereShape s:
                yield return new(new Circle(new Plane(s.Centre, e), s.Radius).ToNurbsCurve(), input, solid, AnalyticSolid.Outer, true, _ => true);
                break;
            case CylinderShape c:
                foreach (var (radius, surface) in new[] { (c.Radius, AnalyticSolid.Outer), (c.InnerRadius, AnalyticSolid.Inner) })
                {
                    if (radius <= 0) continue;
                    foreach (var cap in new[] { c.Cap0, c.Cap1 })
                    {
                        // A rim is a profile where the cap and the wall beside it face opposite ways.
                        bool capFaces = cap.Normal * e > 0; double sign = surface == AnalyticSolid.Outer ? 1 : -1;
                        bool Profile(Point3d p) { var radial = p - c.Origin; radial -= c.Axis * (radial * c.Axis); return capFaces != (sign * (radial * e) > 0); }
                        yield return new(Rim(c.Origin, c.Axis, radius, cap), input, solid, surface, false, Profile);
                    }
                    var side = Vector3d.CrossProduct(c.Axis, e);
                    if (!side.Unitize()) continue;                                 // seen along the axis: the rims are the outline
                    foreach (int k in new[] { -1, 1 })
                    {
                        var start = c.Origin + side * (k * radius);
                        Point3d End(Plane cap) => start + c.Axis * (((cap.Origin - start) * cap.Normal) / (c.Axis * cap.Normal));
                        Point3d a = End(c.Cap0), b = End(c.Cap1);
                        if (a.DistanceTo(b) > _tol) yield return new(new LineCurve(a, b), input, solid, surface, true, _ => surface == AnalyticSolid.Outer);
                    }
                }
                break;
            case SheetShape s:
                var y = Vector3d.CrossProduct(s.Axis, s.X); bool full = s.Sweep >= 2 * Math.PI;
                Vector3d Radial(double angle) => s.X * Math.Cos(angle) + y * Math.Sin(angle);
                foreach (double h in new[] { s.Height0, s.Height1 })
                {
                    var centre = s.Origin + s.Axis * h; var frame = new Plane(centre, Radial(s.Start), Vector3d.CrossProduct(s.Axis, Radial(s.Start)));
                    yield return new(full ? new Circle(frame, s.Radius).ToNurbsCurve() : new Arc(frame, s.Radius, s.Sweep).ToNurbsCurve(), input, solid, AnalyticSolid.Outer, false, _ => true);
                }
                Curve Ruling(double angle) => new LineCurve(s.Origin + Radial(angle) * s.Radius + s.Axis * s.Height0, s.Origin + Radial(angle) * s.Radius + s.Axis * s.Height1);
                if (!full) foreach (double angle in new[] { s.Start, s.Start + s.Sweep }) yield return new(Ruling(angle), input, solid, AnalyticSolid.Outer, false, _ => true);
                var across = Vector3d.CrossProduct(s.Axis, e);
                if (!across.Unitize()) break;
                foreach (int k in new[] { -1, 1 })
                {
                    double angle = (Math.Atan2(k * (across * y), k * (across * s.X)) - s.Start) % (2 * Math.PI); if (angle < 0) angle += 2 * Math.PI;
                    if (full || angle <= s.Sweep) yield return new(Ruling(s.Start + angle), input, solid, AnalyticSolid.Outer, true, _ => true);
                }
                break;
            case CutTubeShape t:
                // The body's own edges between different surfaces. An edge is a profile where the two faces beside
                // it face opposite ways.
                Vector3d Out(CutTubeRecognition.Surface f, Point3d p)
                {
                    if (!f.Cylinder) return f.Direction * f.Outward;
                    var radial = p - f.Origin; radial -= f.Direction * (radial * f.Direction); return radial * f.Outward;
                }
                foreach (var edge in t.Edges)
                {
                    var pair = Enumerable.Range(0, t.Real).Where(i => (edge.On & (1 << i)) != 0).Select(i => t.Surfaces[i]).ToArray();
                    bool Profile(Point3d p) => pair.Length == 2 && (Out(pair[0], p) * e > 0) != (Out(pair[1], p) * e > 0);
                    // The exact curve behind a stored edge: the intersection of its two surfaces. When every point of
                    // the edge snaps onto it (the surfaces cross cleanly there), that is what is classified, with no
                    // slack; otherwise the stored edge is classified with its measured slack.
                    Func<Point3d, Point3d>? snap = null;
                    if (edge.Exact && pair.Length == 2) snap = q => CutTubeRecognition.Onto(pair[0], pair[1], q) ?? q;
                    yield return new(edge.World.DuplicateCurve(), input, solid, edge.On, false, Profile, edge.Slack, null, snap);
                }
                // Outlines of its cylindrical faces, from Rhino's silhouette of the body: each is a ruling of the face's
                // cylinder, already cut to the face. The ray there touches that cylinder.
                Brep? temporary = null;
                var brep = input.Geometry as Brep ?? (temporary = input.Geometry is Extrusion x ? x.ToBrep() : (input.Geometry as Surface)?.ToBrep());
                try
                {
                    if (brep is null) break;
                    var silhouettes = Silhouette.Compute(brep, SilhouetteType.Tangent, -e, _tol, .001) ?? [];
                    foreach (var silhouette in silhouettes)
                    {
                        if (silhouette?.Curve is null || silhouette.SilhouetteType != SilhouetteType.Tangent) continue;
                        var component = silhouette.GeometryComponentIndex;
                        // An outline that falls on a cylinder's seam is reported against the seam edge, not the face.
                        int surface = -1;
                        if (component.ComponentIndexType == ComponentIndexType.BrepFace && component.Index >= 0 && component.Index < t.FaceSurface.Length) surface = t.FaceSurface[component.Index];
                        else if (component.ComponentIndexType == ComponentIndexType.BrepEdge && component.Index >= 0 && component.Index < brep.Edges.Count)
                        {
                            var beside = brep.Edges[component.Index].AdjacentFaces().Where(f => f < t.FaceSurface.Length).Select(f => t.FaceSurface[f]).Distinct().ToArray();
                            if (beside.Length == 1) surface = beside[0];
                        }
                        if (surface < 0 || !t.Surfaces[surface].Cylinder) continue;
                        if (silhouette.Curve.GetLength() <= _tol) continue;
                        // The exact outline is a ruling of the cylinder, a radius to the side of its axis as seen. Rhino
                        // places it to its tolerance (and the face has its own bound): the curve is snapped onto the ruling
                        // when it is that close, and classified with that slack otherwise.
                        var cylinder = t.Surfaces[surface]; var side = Vector3d.CrossProduct(cylinder.Direction, e); double near = 4 * (_tol + cylinder.Slack);
                        Func<Point3d, Point3d>? onto = null;
                        if (side.Unitize())
                        {
                            Point3d OnOutline(Point3d q) { var w = q - cylinder.Origin; return cylinder.Origin + side * (w * side < 0 ? -cylinder.Radius : cylinder.Radius) + cylinder.Direction * (w * cylinder.Direction); }
                            var c = silhouette.Curve;
                            if (new[] { 0, .25, .5, .75, 1 }.All(f => { var q = c.PointAt(c.Domain.ParameterAt(f)); return OnOutline(q).DistanceTo(q) <= near; })) onto = OnOutline;
                        }
                        yield return new(silhouette.Curve.DuplicateCurve(), input, solid, 1 << surface, true, _ => true, onto is null ? _tol + cylinder.Slack : 0, null, onto);
                    }
                }
                finally { temporary?.Dispose(); }
                break;
        }
    }

    /// <summary>The curve where a cap plane cuts a cylinder about the axis: a circle, or an ellipse for an oblique cap.
    /// Always a NURBS curve: the drawing projects it, and a projected circle is no longer an arc.</summary>
    private static Curve Rim(Point3d origin, Vector3d axis, double radius, Plane cap)
    {
        var n = cap.Normal; double un = axis * n; var centre = origin + axis * (((cap.Origin - origin) * n) / un);
        var minor = Vector3d.CrossProduct(axis, n);
        if (!minor.Unitize() || Math.Abs(Math.Abs(un) - 1) < 1e-12) return new Circle(new Plane(centre, n), radius).ToNurbsCurve();
        var major = Vector3d.CrossProduct(n, minor); major.Unitize();
        return new Ellipse(new Plane(centre, major, minor), radius / Math.Abs(un), radius).ToNurbsCurve();
    }

    // ---------------------------------------------------------------- composition entry points

    /// <summary>Kernel edges: the exact solids hide parts the planar faces left Visible (or could not decide).</summary>
    internal VisibilityResult Occlude(VisibilityResult kernel, IReadOnlyList<CandidateEdge> edges, CancellationToken token)
    {
        if (!Any) return kernel;
        var source = kernel.Intervals; var parts = new EdgeInterval[source.Count][];
        Parallel.For(0, source.Count, new ParallelOptions { MaxDegreeOfParallelism = Math.Min(Environment.ProcessorCount, 8), CancellationToken = token }, i =>
        {
            var interval = source[i];
            if (interval.Class == VisibilityClass.Hidden) { parts[i] = [interval]; return; }
            var e = edges[interval.Edge];
            Vertex At(double t) => new(e.A.X + (e.B.X - e.A.X) * t, e.A.Y + (e.B.Y - e.A.Y) * t, e.A.Z + (e.B.Z - e.A.Z) * t);
            var found = _removed.Intervals(AnalyticCandidate.Straight(At(interval.Start), At(interval.End)), _tol, token: token);
            if (found.Count == 1 && found[0].Class == VisibilityClass.Visible) { parts[i] = [interval]; return; }
            double span = interval.End - interval.Start;
            parts[i] = found.Select(f => new EdgeInterval(interval.Edge, f.Start == 0 ? interval.Start : interval.Start + span * f.Start,
                f.End == 1 ? interval.End : interval.Start + span * f.End,
                f.Class == VisibilityClass.Hidden ? VisibilityClass.Hidden : f.Class == VisibilityClass.Unresolved ? VisibilityClass.Unresolved : interval.Class)).ToArray();
        });
        return kernel with { Intervals = parts.SelectMany(p => p).ToArray() };
    }

    /// <summary>A point source: Hidden if the planar faces or an exact solid hide it, Unresolved if either cannot decide.</summary>
    internal VisibilityClass Point(Point3d world, VisibilityClass planar)
    {
        if (!Any || planar == VisibilityClass.Hidden) return planar;
        var k = _removed.Classify(Project(world), _tol);
        return k == VisibilityClass.Hidden ? VisibilityClass.Hidden : k == VisibilityClass.Unresolved ? VisibilityClass.Unresolved : planar;
    }

    /// <summary>
    /// Native curves are cut where an exact solid that left HLD hides them; then the candidates of those solids are
    /// appended, classified against every recognised solid. Planar faces are applied afterwards by the caller, as for
    /// any native curve. Curves made here are added to `owned`.
    /// </summary>
    internal void Append(List<(Curve Curve, ComposedVisibility.Tag Tag, LineClass Class, bool Profile)> curves, List<GeometryBase> owned, CancellationToken token)
    {
        if (!Any) return;
        var result = new List<(Curve, ComposedVisibility.Tag, LineClass, bool)>(curves.Count + _candidates.Count);
        foreach (var c in curves)
        {
            token.ThrowIfCancellationRequested();
            if (c.Class == LineClass.Hidden) { result.Add(c); continue; }
            // A native curve is only known to the native tolerance: a decision must hold half a tolerance to each side,
            // and an undecided run up to four tolerances between decided neighbours is that curve's change point.
            var pieces = Pieces(c.Curve, _removed, -1, 0, false, _tol / 2, 4 * _tol, token);
            if (pieces.Count == 1 && pieces[0].Class == VisibilityClass.Visible) { result.Add(c); continue; }
            foreach (var piece in pieces)
            {
                var part = CurveIntervals.Trim(c.Curve, piece.Domain); if (part is null) continue;
                owned.Add(part);
                result.Add((part, c.Tag, piece.Class == VisibilityClass.Hidden ? LineClass.Hidden : piece.Class == VisibilityClass.Unresolved ? LineClass.Unresolved : c.Class, c.Profile));
            }
        }
        // Each candidate is its own curve, so cutting chords (Rhino evaluation) and the exact solve (managed code) run
        // in parallel; results are taken in candidate order.
        // A straight candidate seen end-on (the outline of a column in plan) has no drawing: it is passed on whole,
        // unsolved, and the caller's projection test drops it as it drops any projecting segment.
        var projection = Transform.PlanarProjection(_plane); var endOn = new bool[_candidates.Count];
        for (int i = 0; i < endOn.Length; i++)
        {
            if (_candidates[i].Curve is not LineCurve) continue;
            using var flat = _candidates[i].Curve.DuplicateCurve();
            endOn[i] = flat.Transform(projection) && ComposedVisibility.ProjectionResidue(_candidates[i].Curve, flat, _tol);
        }
        var chords = new List<Chord>?[_candidates.Count]; long done = 0;
        var solved = new List<(Interval Domain, VisibilityClass Class)>[chords.Length]; var made = new List<(Curve Curve, ComposedVisibility.Tag Tag, LineClass Class, bool Profile)>();
        Parallel.For(0, chords.Length, new ParallelOptions { MaxDegreeOfParallelism = Math.Min(Environment.ProcessorCount, 8), CancellationToken = token },
            i =>
            {
                if (!endOn[i])
                {
                    var c = _candidates[i];
                    chords[i] = Cut(c.Curve, _all, c.Solid, c.OnSurface, c.Tangent, c.Slack, token, c.Contacts, c.Snap);
                    // A stored edge is known to its slack: its change points are known to a few times that.
                    solved[i] = Solve(c.Curve, chords[i], _all, c.Slack, Math.Max(_tol, 8 * c.Slack), token);
                }
                NativeDrawing.ReportStep(ViewSteps.ExactCurved, Interlocked.Increment(ref done), chords.Length);
            });
        for (int index = 0; index < _candidates.Count; index++)
        {
            token.ThrowIfCancellationRequested();
            var candidate = _candidates[index]; var tag = new ComposedVisibility.Tag(candidate.Input); var pieces = solved[index];
            if (endOn[index])
            {
                var whole = candidate.Curve.DuplicateCurve(); owned.Add(whole);
                made.Add((whole, tag, LineClass.Hidden, false)); continue;
            }
            // A whole closed rim in one class is emitted as two halves: seen edge-on a closed curve projects onto
            // itself, and a piece whose two ends coincide is poor drawing geometry.
            if (pieces.Count == 1 && candidate.Curve.IsClosed)
                pieces = [(new Interval(pieces[0].Domain.Min, pieces[0].Domain.Mid), pieces[0].Class), (new Interval(pieces[0].Domain.Mid, pieces[0].Domain.Max), pieces[0].Class)];
            foreach (var piece in pieces)
            {
                var part = CurveIntervals.Trim(candidate.Curve, piece.Domain); if (part is null) continue;
                owned.Add(part);
                made.Add((part, tag, PlanarExtraction.OutputClass(piece.Class), candidate.Profile(part.PointAt(part.Domain.Mid))));
            }
        }
        result.AddRange(WithoutCoveredHidden(made, owned, token));
        curves.Clear(); curves.AddRange(result);
    }

    /// <summary>
    /// The drawing-locus convention for curves, as the planar loci have it for lines: a Hidden piece that lies exactly
    /// under a Visible or Unresolved piece in the drawing, or under an earlier Hidden one, is not drawn twice. This is
    /// the bottom rim of a column in plan, the far half of a rim seen edge-on, the rims of stacked members.
    /// Straight pieces are left to the locus reconciliation.
    /// </summary>
    private List<(Curve, ComposedVisibility.Tag, LineClass, bool)> WithoutCoveredHidden(
        List<(Curve Curve, ComposedVisibility.Tag Tag, LineClass Class, bool Profile)> made, List<GeometryBase> owned, CancellationToken token)
    {
        var kept = new List<(Curve, ComposedVisibility.Tag, LineClass, bool)>(made.Count);
        var projection = Transform.PlanarProjection(_plane); var flat = new Curve?[made.Count];
        // A piece dropped whole takes no further part: whatever covered it is still there and covers the same stretch,
        // so a stack of n coincident rims costs n comparisons, not n squared.
        var gone = new bool[made.Count];
        using var tree = new RTree();
        try
        {
            for (int i = 0; i < made.Count; i++)
            {
                if (made[i].Curve is LineCurve) continue;
                var p = made[i].Curve.DuplicateCurve(); if (!p.Transform(projection)) { p.Dispose(); continue; }
                flat[i] = p; var box = p.GetBoundingBox(true); box.Inflate(_tol); tree.Insert(box, i);
            }
            for (int i = 0; i < made.Count; i++)
            {
                token.ThrowIfCancellationRequested(); var piece = made[i];
                if (piece.Class != LineClass.Hidden || flat[i] is not { } mine) { kept.Add(piece); continue; }
                var others = new List<int>();
                tree.Search(mine.GetBoundingBox(true), (_, a) => { int j = a.Id; if (j != i && !gone[j] && (made[j].Class != LineClass.Hidden || j < i)) others.Add(j); });
                if (others.Count == 0) { kept.Add(piece); continue; }
                others.Sort();
                var cuts = new SortedSet<double> { mine.Domain.Min, mine.Domain.Max };
                foreach (int j in others)
                    foreach (var end in new[] { flat[j]!.PointAtStart, flat[j]!.PointAtEnd })
                        if (mine.ClosestPoint(end, out double t, _tol)) cuts.Add(t);
                var ends = cuts.ToArray(); double start = double.NaN; gone[i] = true;
                for (int k = 0; k + 1 < ends.Length; k++)
                {
                    // Covered: one other piece lies within half a tolerance at three points of this stretch.
                    bool covered = others.Any(j => new[] { .25, .5, .75 }.All(f => flat[j]!.ClosestPoint(mine.PointAt(ends[k] + (ends[k + 1] - ends[k]) * f), out _, _tol / 2)));
                    if (!covered) gone[i] = false;
                    if (!covered && double.IsNaN(start)) start = ends[k];
                    if ((covered || k + 2 == ends.Length) && !double.IsNaN(start))
                    {
                        double stop = covered ? ends[k] : ends[k + 1];
                        if (start == mine.Domain.Min && stop == mine.Domain.Max) kept.Add(piece);
                        else if (CurveIntervals.Trim(piece.Curve, new Interval(start, stop)) is { } part) { owned.Add(part); kept.Add((part, piece.Tag, piece.Class, piece.Profile)); }
                        start = double.NaN;
                    }
                }
            }
        }
        finally { foreach (var p in flat) p?.Dispose(); }
        return kept;
    }

    /// <summary>Parameter intervals of a world curve by class, in order and without gaps.</summary>
    private List<(Interval Domain, VisibilityClass Class)> Pieces(Curve curve, AnalyticOccluders occluders, int solid, int onSurface, bool tangent,
        double slack, double absorb, CancellationToken token) =>
        Solve(curve, Cut(curve, occluders, solid, onSurface, tangent, slack, token), occluders, slack, absorb, token);

    private sealed record Chord(double A, double B, double Length, AnalyticCandidate Piece);

    /// <summary>The curve as chords in view coordinates; null when no occluder is near it; empty when it has no valid bounds.</summary>
    private List<Chord>? Cut(Curve curve, AnalyticOccluders occluders, int solid, int onSurface, bool tangent, double slack, CancellationToken token,
        IReadOnlyList<(int Solid, int OnSurface)>? contacts = null, Func<Point3d, Point3d>? snap = null)
    {
        Point3d At(double t) => snap is null ? curve.PointAt(t) : snap(curve.PointAt(t));
        var toView = Transform.PlaneToPlane(_plane, Plane.WorldXY); var box = curve.GetBoundingBox(toView); var near = new List<int>();
        if (!box.IsValid) return [];
        occluders.Near(box.Min.X - slack, box.Min.Y - slack, box.Max.X + slack, box.Max.Y + slack, box.Min.Z, _tol, near);
        if (near.Count == 0) return null;
        var chords = new List<Chord>();
        // A candidate of an exact solid can lie on the boundary of another one (the rims of stacked columns in plan, a
        // mitre joint): its chords must follow the curve far closer than the graze width, or the boundary rule would
        // see each chord dip in and out. Native curves are only known to the tolerance and get the slack instead.
        // A curve that is itself only known to its slack needs chords no finer than a quarter of that.
        foreach (var (ta, tb) in Chords(curve, Math.Max(_tol / 8, slack / 4), solid >= 0 && slack == 0 ? AnalyticSolid.Graze(_tol) / 8 : 0, _tol, snap))
        {
            token.ThrowIfCancellationRequested();
            Point3d pa = At(ta), pb = At(tb);
            chords.Add(new(ta, tb, pa.DistanceTo(pb), new(Project(pa), Project(At((ta + tb) / 2)), Project(pb), solid, onSurface, tangent, contacts)));
        }
        return chords;
    }

    private List<(Interval Domain, VisibilityClass Class)> Solve(Curve curve, List<Chord>? chords, AnalyticOccluders occluders, double slack, double absorb, CancellationToken token)
    {
        if (chords is null) return [(curve.Domain, VisibilityClass.Visible)];
        if (chords.Count == 0) return [(curve.Domain, VisibilityClass.Unresolved)];
        var runs = new List<(double A, double B, VisibilityClass Class, double Length)>();
        void Push(double a, double b, VisibilityClass c, double length)
        {
            if (runs.Count > 0 && runs[^1].Class == c) runs[^1] = (runs[^1].A, b, c, runs[^1].Length + length); else runs.Add((a, b, c, length));
        }
        foreach (var chord in chords)
        {
            token.ThrowIfCancellationRequested(); double ta = chord.A, tb = chord.B;
            foreach (var i in occluders.Intervals(chord.Piece, _tol, slack, absorb: 0, token: token))
                Push(i.Start == 0 ? ta : ta + (tb - ta) * i.Start, i.End == 1 ? tb : ta + (tb - ta) * i.End, i.Class, chord.Length * (i.End - i.Start));
        }
        // An undecided run no longer than `absorb` between decided neighbours of different classes is a change point
        // known to that accuracy. Between neighbours of one class, or at an end, it is not a change point. On an exact
        // candidate (no slack) it is then a tie no longer than a tolerance and goes to the neighbour. On a native curve
        // it stays Unresolved: behind a sliver narrower than the slack (an edge-on sheet) the whole hidden stretch is
        // undecided, and giving it to its Visible neighbours would draw a hidden line.
        for (int i = 0; i < runs.Count; i++)
        {
            var run = runs[i];
            if (run.Class != VisibilityClass.Unresolved || run.Length > absorb) continue;
            bool left = i > 0, right = i + 1 < runs.Count;
            bool changePoint = left && right && runs[i - 1].Class != runs[i + 1].Class;
            if (!changePoint && (slack > 0 || run.Length > _tol)) continue;
            if (left && right && runs[i - 1].Class == runs[i + 1].Class)
            { runs[i - 1] = (runs[i - 1].A, runs[i + 1].B, runs[i - 1].Class, runs[i - 1].Length + run.Length + runs[i + 1].Length); runs.RemoveRange(i, 2); i--; }
            else if (left && right)
            {
                double mid = (run.A + run.B) / 2;
                runs[i - 1] = (runs[i - 1].A, mid, runs[i - 1].Class, runs[i - 1].Length + run.Length / 2);
                runs[i + 1] = (mid, runs[i + 1].B, runs[i + 1].Class, runs[i + 1].Length + run.Length / 2); runs.RemoveAt(i); i--;
            }
            else if (left) { runs[i - 1] = (runs[i - 1].A, run.B, runs[i - 1].Class, runs[i - 1].Length + run.Length); runs.RemoveAt(i); i--; }
            else if (right) { runs[i + 1] = (run.A, runs[i + 1].B, runs[i + 1].Class, runs[i + 1].Length + run.Length); runs.RemoveAt(i); i--; }
        }
        return runs.Select(r => (new Interval(r.A, r.B), r.Class)).ToList();
    }

    /// <summary>Parameter pieces whose chord stays within `sag` of the curve. A straight curve is one piece.</summary>
    private static IEnumerable<(double, double)> Chords(Curve curve, double sag, double fit = 0, double shortest = 0, Func<Point3d, Point3d>? snap = null)
    {
        Point3d P(double t) => snap is null ? curve.PointAt(t) : snap(curve.PointAt(t));
        // Distance from the curve to the parabola through a chord's ends and middle (the Core candidate), measured
        // across the curve at the quarter points and doubled: a circle is within three quarters of that everywhere.
        static double Misfit(Point3d pa, Point3d p1, Point3d pm, Point3d p3, Point3d pb)
        {
            double worst = 0;
            for (int side = 0; side < 2; side++)
            {
                var p = side == 0 ? p1 : p3; double t = side == 0 ? .25 : .75;
                for (int step = 0; step < 3; step++)
                {
                    var q = (Vector3d)pa * ((1 - t) * (1 - 2 * t)) + (Vector3d)pm * (4 * t * (1 - t)) + (Vector3d)pb * (t * (2 * t - 1));
                    var d1 = (Vector3d)pa * (4 * t - 3) + (Vector3d)pm * (4 - 8 * t) + (Vector3d)pb * (4 * t - 1);
                    double speed = d1.SquareLength; if (!(speed > 0)) break;
                    t += ((Vector3d)p - q) * d1 / speed;
                }
                var at = (Vector3d)pa * ((1 - t) * (1 - 2 * t)) + (Vector3d)pm * (4 * t * (1 - t)) + (Vector3d)pb * (t * (2 * t - 1));
                worst = Math.Max(worst, ((Vector3d)p - at).Length);
            }
            return 2 * worst;
        }
        if (curve is LineCurve || curve.IsLinear(sag / 4)) { yield return (curve.Domain.Min, curve.Domain.Max); yield break; }
        var stack = new Stack<(double A, double B, Point3d Pa, Point3d Pb, int Depth)>();
        for (int i = curve.SpanCount - 1; i >= 0; i--)
        {
            var d = curve.SpanDomain(i);
            for (int k = 3; k >= 0; k--)   // never a closed chord
            {
                double a = d.ParameterAt(k / 4.0), b = d.ParameterAt((k + 1) / 4.0);
                stack.Push((a, b, P(a), P(b), 0));
            }
        }
        while (stack.Count > 0)
        {
            var (a, b, pa, pb, depth) = stack.Pop(); if (!(b > a)) continue;
            double m = (a + b) / 2; var pm = P(m);
            var line = new Line(pa, pb); bool fine = line.Length > 0 || depth >= 40;
            if (fine && depth < 40)
            {
                fine = line.DistanceTo(pm, true) <= sag;
                if (fine)
                {
                    Point3d p1 = P(a + (b - a) * .25), p3 = P(a + (b - a) * .75);
                    fine = line.DistanceTo(p1, true) <= sag && line.DistanceTo(p3, true) <= sag &&
                        !(fit > 0 && line.Length > shortest && Misfit(pa, p1, pm, p3, pb) > fit);
                }
            }
            if (fine) { yield return (a, b); continue; }
            stack.Push((m, b, pm, pb, depth + 1)); stack.Push((a, m, pa, pm, depth + 1));
        }
    }
}
