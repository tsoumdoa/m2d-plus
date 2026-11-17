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
        /// <summary>Faces too small or too close to another surface to be compared with the cells.</summary>
        internal int Unchecked { get; init; }
        /// <summary>The largest slack of a surface: its faces' bound, and how far its edges and their stored
        /// tolerances let the real faces run past the exact surfaces.</summary>
        internal double SurfaceBound { get; init; }
    }

    /// <summary>What is known of one geometry object. Pending: none of the plain shapes, but it could be a cut tube and
    /// that costly recognition has not run for it yet.</summary>
    private sealed class Entry { internal double Tolerance; internal Shape? Shape; internal string? Refusal; internal bool Pending; }
    private static readonly ConditionalWeakTable<GeometryBase, Entry> Cache = new();

    /// <summary>Cut-tube results by geometry content: a section hands over a new copy of the same clipped piece on
    /// every solve. Least recently used entries go first when it is full.</summary>
    private static readonly Recent ByContent = new(8192);
    private sealed class Recent(int capacity)
    {
        private readonly Dictionary<string, (Shape? Shape, string? Refusal, long Used)> _held = new(StringComparer.Ordinal); private long _clock;
        internal bool TryGet(string key, out (Shape? Shape, string? Refusal) value)
        {
            lock (_held)
            {
                if (!_held.TryGetValue(key, out var held)) { value = default; return false; }
                _held[key] = (held.Shape, held.Refusal, ++_clock); value = (held.Shape, held.Refusal); return true;
            }
        }
        internal void Set(string key, (Shape? Shape, string? Refusal) value)
        {
            lock (_held)
            {
                if (_held.Count >= capacity && !_held.ContainsKey(key))
                    foreach (var old in _held.OrderBy(k => k.Value.Used).Take(capacity / 4).Select(k => k.Key).ToArray()) _held.Remove(old);
                _held[key] = (value.Shape, value.Refusal, ++_clock);
            }
        }
    }

    /// <summary>The exact shape of a whole body, or null. Cached on the owned geometry object.</summary>
    internal static Shape? Recognise(GeometryBase geometry, double tolerance) => Examine(geometry, tolerance).Shape;

    /// <summary>The exact shape of a whole body, or why it is not one (null for geometry that is not a body at all).</summary>
    internal static (Shape? Shape, string? Refusal) Examine(GeometryBase geometry, double tolerance, CancellationToken token = default)
    {
        if (geometry is not (Brep or Extrusion or Surface)) return (null, null);
        var entry = Look(geometry, tolerance, true, token);
        return (entry.Shape, entry.Refusal);
    }

    /// <summary>The plain shapes always; the cut tube only when `deep` (see <see cref="Entry.Pending"/>).</summary>
    private static Entry Look(GeometryBase geometry, double tolerance, bool deep, CancellationToken token)
    {
        if (!Cache.TryGetValue(geometry, out var entry) || entry.Tolerance != tolerance)
        {
            entry = new Entry { Tolerance = tolerance }; Brep? temporary = null;
            try
            {
                var brep = geometry as Brep ?? (temporary = geometry is Extrusion e ? e.ToBrep() : ((Surface)geometry).ToBrep());
                if (brep is null) entry.Refusal = "no Brep form";
                else
                {
                    string? reason = null; Shape? No(string text) { reason = text; return null; }
                    entry.Shape = Classify(brep, tolerance, No); entry.Refusal = reason;
                    entry.Pending = entry.Shape is null && reason != PlanarOnly && CutTubeRecognition.Plausible(brep, tolerance);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { entry.Shape = null; entry.Pending = false; entry.Refusal = "recognition failed: " + ex.GetType().Name; }
            finally { temporary?.Dispose(); }
            if (entry.Shape is null) entry.Refusal ??= "not recognised"; else entry.Refusal = null;
            Cache.AddOrUpdate(geometry, entry);
        }
        if (!deep || !entry.Pending) return entry;
        lock (entry)
        {
            if (!entry.Pending) return entry;
            Brep? temporary = null; string? plain = entry.Refusal;
            try
            {
                var brep = geometry as Brep ?? (temporary = geometry is Extrusion e ? e.ToBrep() : ((Surface)geometry).ToBrep());
                // Recognition asks Rhino thousands of point-in-solid questions: its result is also kept by content.
                string key = GeometryFingerprint.Compute(brep!) + "@" + tolerance.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
                if (!ByContent.TryGet(key, out var held))
                {
                    string? reason = null; Shape? No(string text) { reason = text; return null; }
                    var found = CutTubeRecognition.Recognise(brep!, tolerance, No, token); held = (found, reason);
                    ByContent.Set(key, held);
                }
                entry.Shape = held.Shape; entry.Refusal = held.Shape is not null ? null : held.Refusal == PlanarOnly ? plain : $"{plain}; as a cut tube: {held.Refusal}";
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { entry.Shape = null; entry.Refusal = $"{plain}; as a cut tube: recognition failed: {ex.GetType().Name}"; }
            finally { temporary?.Dispose(); }
            entry.Pending = false;
        }
        return entry;
    }
    internal const string PlanarOnlyReason = PlanarOnly;
    private const string OverBudget = "a possible cut tube beyond this view's recognition budget";

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
    /// the index of an unrecognised body that may hide it (-1 when there is none, or when its own box is invalid).
    /// A recognised solid that an unrecognised body may hide leaves HLD all the same when `mixed` is set: its lines are
    /// then also given to HLD as curves, which hide nothing, and HLD says where the unrecognised bodies hide them.
    /// Otherwise (the default) it stays in HLD whole.</summary>
    /// <param name="budget">How many possible cut tubes get the costly recognition in this view: the first that many
    /// in input order, whatever is cached, so that the drawing does not depend on what was solved before. The others
    /// stay on native HLD and Info says so.</param>
    private static (Shape? Shape, bool Leaves, int Blocker, string? Refusal)[] Plan(IReadOnlyList<GeometryBase> geometries, Plane plane, double tol, bool mixed, int budget, CancellationToken token)
    {
        var plan = new (Shape?, bool, int, string?)[geometries.Count]; var toView = Transform.PlaneToPlane(plane, Plane.WorldXY);
        var foreign = new List<(BoundingBox Box, int Index)>(); bool any = false;
        // Each geometry object once, whatever number of sources share it; different objects in parallel.
        var distinct = new List<GeometryBase>(); var seen = new HashSet<GeometryBase>(ReferenceEqualityComparer.Instance);
        foreach (var g in geometries) if (g is Brep or Extrusion or Surface && seen.Add(g)) distinct.Add(g);
        var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Min(Environment.ProcessorCount, 8), CancellationToken = token };
        var entries = new Entry[distinct.Count];
        Parallel.For(0, distinct.Count, options, i => entries[i] = Look(distinct[i], tol, false, token));
        var eligible = new List<int>(); var over = new HashSet<GeometryBase>(ReferenceEqualityComparer.Instance); int possible = 0;
        for (int i = 0; i < distinct.Count; i++)
        {
            // Possible cut tubes are counted from the scene alone: a body recognised in an earlier solve counts too.
            bool candidate = entries[i].Pending || entries[i].Shape is CutTubeShape || entries[i].Refusal?.Contains("; as a cut tube: ", StringComparison.Ordinal) == true;
            if (!candidate) continue;
            if (possible++ < budget) eligible.Add(i); else over.Add(distinct[i]);
        }
        Parallel.For(0, eligible.Count, options, k => entries[eligible[k]] = Look(distinct[eligible[k]], tol, true, token));
        var byObject = new Dictionary<GeometryBase, Entry>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < distinct.Count; i++) byObject[distinct[i]] = entries[i];
        for (int i = 0; i < geometries.Count; i++)
        {
            if (!byObject.TryGetValue(geometries[i], out var entry)) { plan[i] = (null, false, -1, null); continue; }
            bool skipped = over.Contains(geometries[i]);
            var shape = skipped ? null : entry.Shape; plan[i] = (shape, false, -1, skipped ? OverBudget : shape is null ? entry.Refusal : null); any |= shape is not null;
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
            plan[i] = (plan[i].Item1, !exposed || mixed && blocker >= 0, blocker, null);
        }
        return plan;
    }

    /// <summary>Would any source of this view leave HLD? Used to choose the composed route for scenes with no planar source.</summary>
    internal static bool Applies(IEnumerable<GeometryBase> geometries, Plane plane, double tolerance, ViewRequest view, NativeSettings settings, CancellationToken token = default) =>
        Enabled(view, settings) && Plan(geometries.ToArray(), plane, tolerance, Mixed(view, settings), settings.CutTubeBudget, token).Any(p => p.Leaves);

    /// <summary>Fast mode may switch native hidden curves off; the mixed route needs HLD's answer for every stretch of a
    /// line, so in fast mode a recognised solid that an unrecognised body may hide stays in HLD as before.</summary>
    private static bool Mixed(ViewRequest view, NativeSettings settings) => settings.AnalyticMixed && view.Settings?.Engine != EngineMode.Fast;

    // ---------------------------------------------------------------- one view

    /// <param name="Slack">How far the curve may be from the surfaces it lies on (a cut tube's stored edge).</param>
    /// <param name="Snap">Takes a point of the drawn curve to the exact curve it stands for (the intersection of the
    /// two surfaces a stored edge joins, or a cylinder's outline). What is classified is the exact curve; what is drawn
    /// is the stored one, within the body's stated edge bound of it.</param>
    /// <param name="Gap">How far across the view the drawn curve may be from the exact curve that is classified; Depth:
    /// how far along it. A decision must hold for every ray within those of the exact one, so that it is the drawn
    /// line's class too. A point that does not snap (null) is not decided.</param>
    private sealed record Candidate(Curve Curve, VisibilityInput Input, int Solid, int OnSurface, bool Tangent, Func<Point3d, bool> Profile, double Slack = 0,
        IReadOnlyList<(int Solid, int OnSurface)>? Contacts = null, Func<Point3d, Point3d?>? Snap = null, double Gap = 0, double Depth = 0)
    {
        /// <summary>The sideways radius its decisions must hold over.</summary>
        public double Loose => Math.Max(Slack, Gap);
        /// <summary>An unrecognised body may hide this line: native HLD is asked as well.</summary>
        public bool Native { get; init; }
    }
    /// <summary>What native HLD said about a stretch of a candidate it was asked about.</summary>
    private enum Said { Visible, Hidden, Unresolved, Covered }

    internal static readonly AnalyticVisibility None = new();
    private readonly AnalyticOccluders _removed = new([]), _all = new([]);
    private readonly List<Candidate> _candidates = [];
    private readonly Plane _plane; private readonly double _tol; private readonly int _recognised, _beside;
    // The candidates given to HLD as curves, by the input each was given as, and what came back for each.
    private readonly Dictionary<VisibilityInput, int> _asked = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<int, List<(Interval Domain, Said Said)>> _said = [];
    private bool[] _endOn = [];
    internal bool Any => _removed.Count != 0;
    internal string Route => Any ? $"; exact analytic solids={_removed.Count}" : "";
    private readonly List<string> _notes = [];
    internal IEnumerable<string> Diagnostics => (_recognised == 0 ? [] : new[]
        { $"Analytic solids: recognised={_recognised}; exact={_removed.Count}; of those, beside unrecognised bodies (lines also checked by native HLD)={_beside}; kept on native HLD because an unrecognised body may hide them={_recognised - _removed.Count}; own candidate curves={_candidates.Count}; given to native HLD={_asked.Count}." })
        .Concat(_notes);

    /// <summary>Info for the owner: which recognised solids stayed on native HLD and the body that may hide each, and
    /// why round bodies were refused. Bodies with planar faces only are not listed. Lists are capped.</summary>
    private void Explain(List<VisibilityInput> curved, (Shape? Shape, bool Leaves, int Blocker, string? Refusal)[] plan, double tol, int budget)
    {
        const int limit = 8;
        var beside = Enumerable.Range(0, plan.Length).Where(i => plan[i].Shape is not null && plan[i].Leaves && plan[i].Blocker >= 0).ToArray();
        if (beside.Length != 0)
            _notes.Add("Exact solids beside bodies that are not exact (native HLD decides where those bodies hide their lines): " + string.Join("; ", beside.Take(limit).Select(i =>
                $"{curved[i].Source.Identity} (beside {curved[plan[i].Blocker].Source.Identity})")) +
                (beside.Length > limit ? $"; and {beside.Length - limit} more." : "."));
        var kept = Enumerable.Range(0, plan.Length).Where(i => plan[i].Shape is not null && !plan[i].Leaves).ToArray();
        if (kept.Length != 0)
            _notes.Add("Analytic solids kept on native HLD: " + string.Join("; ", kept.Take(limit).Select(i =>
                $"{curved[i].Source.Identity} (may be hidden by {(plan[i].Blocker >= 0 ? curved[plan[i].Blocker].Source.Identity : "an unbounded body")})")) +
                (kept.Length > limit ? $"; and {kept.Length - limit} more." : "."));
        var cut = Enumerable.Range(0, plan.Length).Where(i => plan[i].Shape is CutTubeShape).ToArray();
        if (cut.Length != 0)
            _notes.Add("Cut tubes, recognised from point-in-solid evidence at sample points (not a proof that the body is that tube) and drawn to a stated bound " +
                "(faces: certified distance of each face from its plane or cylinder; edges: distance of the drawn edges from the exact curves classified; " +
                "surfaces: the looseness allowed for at each surface, stored edge and vertex tolerances included): " +
                string.Join("; ", cut.Take(limit).Select(i => { var t = (CutTubeShape)plan[i].Shape!; return $"{curved[i].Source.Identity} faces {t.FaceBound:G3}, edges {t.EdgeBound:G3}, surfaces {t.SurfaceBound:G3}"; })) +
                (cut.Length > limit ? $"; and {cut.Length - limit} more." : "."));
        int over = plan.Count(p => p.Refusal == OverBudget);
        if (over != 0)
            _notes.Add($"Cut-tube recognition budget: the first {budget} possible cut tubes of this view were examined; {over} more stay on native HLD unexamined.");
        var refused = Enumerable.Range(0, plan.Length).Where(i => plan[i].Shape is null)
            .Select(i => (Index: i, Why: plan[i].Refusal)).Where(r => r.Why is not null && r.Why != PlanarOnly && r.Why != OverBudget)
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
    private static IEnumerable<(bool Cylinder, Point3d Origin, Vector3d Direction, double Radius, int Bit, double Slack)> Faces(Shape shape)
    {
        switch (shape)
        {
            case CylinderShape c:
                yield return (true, c.Origin, c.Axis, c.Radius, AnalyticSolid.Outer, 0);
                if (c.InnerRadius > 0) yield return (true, c.Origin, c.Axis, c.InnerRadius, AnalyticSolid.Inner, 0);
                break;
            case CutTubeShape t:
                for (int i = 0; i < t.Real; i++) yield return (t.Surfaces[i].Cylinder, t.Surfaces[i].Origin, t.Surfaces[i].Direction, t.Surfaces[i].Radius, 1 << i, t.Surfaces[i].Slack);
                break;
        }
    }

    /// <summary>
    /// Contact: a line of one solid that lies on a surface another solid shares (a pipe coped onto a neighbour lies on
    /// the neighbour's outer surface; mitred members share a rim). Only where a cut tube is involved: the plain
    /// shapes keep their own rule for coincident members (the closed-solid convention).
    /// </summary>
    private void Contacts(List<Shape> shapes, CancellationToken token)
    {
        if (!shapes.Any(s => s is CutTubeShape)) return;
        var faces = shapes.Select(s => Faces(s).ToArray()).ToArray();
        var toView = Transform.PlaneToPlane(_plane, Plane.WorldXY); var nearby = new List<int>();
        for (int k = 0; k < _candidates.Count; k++)
        {
            token.ThrowIfCancellationRequested();
            var c = _candidates[k];
            // A curve near a body that is not on any surface of its own (an outline Rhino gave that is not the exact
            // one) has no contact rule: it is classified as a foreign curve.
            if (c.Solid < 0) continue;
            var mine = faces[c.Solid].Where(f => (f.Bit & c.OnSurface) != 0).ToArray();
            double near = _tol + c.Slack; List<(int, int)>? contacts = null;
            // Only solids whose bounds in the view the curve's box meets can share a surface with it.
            var box = c.Curve.GetBoundingBox(toView); if (!box.IsValid) continue;
            _all.Near(box.Min.X - near, box.Min.Y - near, box.Max.X + near, box.Max.Y + near, double.NegativeInfinity, _tol, nearby);
            if (nearby.Count == 0 || nearby.Count == 1 && nearby[0] == c.Solid) continue;
            // The curve that is classified (the exact rim or outline when the candidate snaps), at nine points.
            var points = Enumerable.Range(0, 9).Select(i => { var q = c.Curve.PointAt(c.Curve.Domain.ParameterAt(i / 8.0)); return c.Snap is null ? q : c.Snap(q) ?? q; }).ToArray();
            foreach (int other in nearby)
            {
                if (other == c.Solid || shapes[other] is not CutTubeShape && shapes[c.Solid] is not CutTubeShape) continue;
                int bits = 0;
                foreach (var f in faces[other])
                {
                    // The same surface as one the candidate lies on (a coped end lies on the neighbour's cylinder). Two
                    // bodies' surfaces that agree to within what each is known to (their slacks) cannot be told apart:
                    // they are one surface, in contact, and contact is not cover.
                    bool same = mine.Any(m =>
                    {
                        double close = near + f.Slack + m.Slack;
                        return f.Cylinder == m.Cylinder && (f.Cylinder
                            ? Math.Abs(f.Radius - m.Radius) <= close && Vector3d.CrossProduct(f.Direction, m.Direction).Length * Math.Max(1, f.Radius) <= close && new Line(f.Origin, f.Origin + f.Direction).DistanceTo(m.Origin, false) <= close
                            : Vector3d.CrossProduct(f.Direction, m.Direction).Length <= 1e-9 && Math.Abs(new Plane(f.Origin, f.Direction).DistanceTo(m.Origin)) <= close);
                    });
                    // Or a different surface the exact curve lies on all the same (the rim of a mitre lies on both
                    // members' cylinders): every point within the graze width of it. Not for an outline: its ray is
                    // tangent to its own cylinder, not to this surface.
                    if (!same && !c.Tangent && c.Slack == 0)
                    {
                        double reach = Math.Max(Math.Max(AnalyticSolid.Graze(_tol), f.Slack), 1e-12 * (1 + f.Radius + Math.Abs(f.Origin.X) + Math.Abs(f.Origin.Y) + Math.Abs(f.Origin.Z)));
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

    private AnalyticVisibility(List<VisibilityInput> curved, Plane plane, double tol, bool mixed, int budget, List<GeometryBase> owned, CancellationToken token)
    {
        _plane = plane; _tol = tol;
        var plan = Plan(curved.Select(c => c.Geometry).ToArray(), plane, tol, mixed, budget, token);
        _recognised = plan.Count(p => p.Shape is not null);
        Explain(curved, plan, tol, budget);
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
                foreach (var candidate in Candidates(shape, curved[i], index)) { owned.Add(candidate.Curve); _candidates.Add(candidate with { Native = plan[i].Blocker >= 0 }); }
            }
        }
        catch (ArgumentException)
        {   // A shape the exact solids refuse after all: nothing leaves HLD.
            _candidates.Clear(); return;
        }
        _all = new(all); _removed = new(removed);
        Contacts(shapes, token);
        curved.RemoveAll(leaving.Contains);
        // A straight candidate seen end-on (the outline of a column in plan) has no drawing.
        var projection = Transform.PlanarProjection(_plane); _endOn = new bool[_candidates.Count];
        for (int i = 0; i < _endOn.Length; i++)
        {
            if (_candidates[i].Curve is not LineCurve) continue;
            using var flat = _candidates[i].Curve.DuplicateCurve();
            _endOn[i] = flat.Transform(projection) && ComposedVisibility.ProjectionResidue(_candidates[i].Curve, flat, _tol);
        }
        // The mixed route. The lines of an exact solid that an unrecognised body may hide go to HLD as curves of the
        // same source. A curve hides nothing, so HLD's answer for them is about the bodies still in HLD alone; what the
        // exact solids and the planar faces hide is decided here and by the caller, and the answers are united.
        _beside = Enumerable.Range(0, plan.Length).Count(i => plan[i].Leaves && plan[i].Blocker >= 0);
        for (int i = 0; i < _candidates.Count; i++)
        {
            if (!_candidates[i].Native || _endOn[i]) continue;
            var input = new VisibilityInput(_candidates[i].Curve, _candidates[i].Input.Source, _candidates[i].Input.Far);
            _asked.Add(input, i); curved.Add(input);
        }
    }

    /// <summary>A stretch of a candidate that HLD reported as a duplicate lying under another native line in front of
    /// it: that line is drawn there, this one is not.</summary>
    internal void Covered(ComposedVisibility.Tag tag, Curve world)
    {
        if (_asked.TryGetValue(tag.Input, out int index)) Heard(index, world, Said.Covered);
    }

    /// <summary>Records what HLD said for a piece of a candidate. The piece is HLD's copy of part of the curve: it is
    /// placed on the candidate by its two ends and its middle, each within four tolerances of it; a piece that cannot
    /// be placed is ignored, and the stretch it stood for counts as not answered.</summary>
    private void Heard(int index, Curve piece, Said said)
    {
        var curve = _candidates[index].Curve; var domain = curve.Domain; double near = 4 * _tol;
        if (!curve.ClosestPoint(piece.PointAtStart, out double ta, near) || !curve.ClosestPoint(piece.PointAtEnd, out double tb, near) ||
            !curve.ClosestPoint(piece.PointAt(piece.Domain.Mid), out double tm, near)) return;
        if (!_said.TryGetValue(index, out var list)) _said.Add(index, list = []);
        if (!curve.IsClosed)
        {
            double low = Math.Min(ta, tb), high = Math.Max(ta, tb);
            if (tm >= low && tm <= high && high > low) list.Add((new Interval(low, high), said));
            return;
        }
        // Closed: the piece runs from ta through tm to tb, one way round or the other, possibly across the seam.
        double U(double t) => (t - domain.Min) / domain.Length; static double Turn(double v) { v %= 1; return v < 0 ? v + 1 : v; }
        double ua = Turn(U(ta)), um = Turn(U(tm)), ub = Turn(U(tb)), start, sweep;
        if (Turn(um - ua) <= Turn(ub - ua)) { start = ua; sweep = Turn(ub - ua); } else { start = ub; sweep = Turn(ua - ub); }
        if (sweep < 1e-12 && piece.GetLength() > curve.GetLength() / 2) { list.Add((domain, said)); return; }     // all the way round
        if (sweep <= 0) return;
        double T(double u) => domain.Min + u * domain.Length;
        if (start + sweep <= 1) list.Add((new Interval(T(start), T(start + sweep)), said));
        else { list.Add((new Interval(T(start), domain.Max), said)); list.Add((new Interval(domain.Min, T(start + sweep - 1)), said)); }
    }

    /// <summary>
    /// The union for a candidate HLD was asked about. Hidden where either side says Hidden. Where HLD says the line lies
    /// under another native line, it is not drawn. Otherwise Visible only where both say Visible: a stretch HLD gave
    /// no answer for is Unresolved, as is one either side could not decide.
    /// </summary>
    private List<(Interval Domain, VisibilityClass? Class)> United(int index, List<(Interval Domain, VisibilityClass Class)> exact)
    {
        var curve = _candidates[index].Curve; var domain = curve.Domain;
        var heard = (_said.GetValueOrDefault(index) ?? []).OrderBy(h => h.Domain.Min).ThenBy(h => h.Domain.Max).ToList();
        // HLD's pieces follow one another along the curve; their ends are placed by closest point, so a gap or an
        // overshoot at the ends of under a quarter of a tolerance is closed.
        bool Close(double a, double b) => curve.PointAt(a).DistanceTo(curve.PointAt(b)) <= _tol / 4;
        for (int k = 0; k < heard.Count; k++)
        {
            var h = heard[k]; double low = h.Domain.Min, high = h.Domain.Max;
            if (k == 0 && low > domain.Min && Close(domain.Min, low)) low = domain.Min;
            double next = k + 1 < heard.Count ? heard[k + 1].Domain.Min : domain.Max;
            if (next > high && Close(high, next)) high = next;
            heard[k] = (new Interval(low, high), h.Said);
        }
        var ends = new SortedSet<double> { domain.Min, domain.Max };
        foreach (var e in exact) { ends.Add(e.Domain.Min); ends.Add(e.Domain.Max); }
        foreach (var h in heard) { ends.Add(Math.Clamp(h.Domain.Min, domain.Min, domain.Max)); ends.Add(Math.Clamp(h.Domain.Max, domain.Min, domain.Max)); }
        var cuts = ends.ToArray(); var result = new List<(Interval, VisibilityClass?)>();
        for (int k = 0; k + 1 < cuts.Length; k++)
        {
            double middle = (cuts[k] + cuts[k + 1]) / 2;
            var mine = VisibilityClass.Unresolved;
            foreach (var e in exact) if (e.Domain.Min <= middle && middle <= e.Domain.Max) { mine = e.Class; break; }
            bool hidden = false, covered = false, unresolved = false, answered = false;
            foreach (var h in heard)
            {
                if (h.Domain.Min > middle || h.Domain.Max < middle) continue;
                answered = true; hidden |= h.Said == Said.Hidden; covered |= h.Said == Said.Covered; unresolved |= h.Said == Said.Unresolved;
            }
            VisibilityClass? kind = mine == VisibilityClass.Hidden || hidden ? VisibilityClass.Hidden : covered ? null :
                mine == VisibilityClass.Unresolved || unresolved || !answered ? VisibilityClass.Unresolved : VisibilityClass.Visible;
            if (result.Count > 0 && result[^1].Item2 == kind) result[^1] = (new Interval(result[^1].Item1.Min, cuts[k + 1]), kind);
            else result.Add((new Interval(cuts[k], cuts[k + 1]), kind));
        }
        return result;
    }

    /// <summary>Takes the sources that may leave HLD out of `curved`. Their candidate curves are added to `owned`.</summary>
    internal static AnalyticVisibility Take(List<VisibilityInput> curved, Plane plane, double tolerance, ViewRequest view, NativeSettings settings,
        List<GeometryBase> owned, CancellationToken token)
    {
        if (!Enabled(view, settings) || curved.Count == 0) return None;
        var before = settings.ObserveExactSolids is null ? null : curved.ToArray();
        var taken = new AnalyticVisibility(curved, plane, tolerance, Mixed(view, settings), settings.CutTubeBudget, owned, token);
        if (before is not null && taken.Any) settings.ObserveExactSolids!(before.Except(curved).ToArray());
        return taken;
    }

    /// <summary>A copy the composition can project and trim: a line stays a line, anything else becomes a NURBS curve
    /// (arcs and polycurves do not survive the planar projection as they are).</summary>
    private static Curve Drawable(Curve curve) => curve is LineCurve ? curve.DuplicateCurve() : curve.ToNurbsCurve() ?? curve.DuplicateCurve();

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
                    Func<Point3d, Point3d?>? snap = null;
                    if (edge.Exact && pair.Length == 2) snap = q => CutTubeRecognition.Onto(pair[0], pair[1], q);
                    // The drawn edge is within its gap of that curve, in any direction.
                    yield return new(Drawable(edge.World), input, solid, edge.On, false, Profile, edge.Slack, null, snap, edge.Exact ? edge.Gap : 0, edge.Exact ? edge.Gap : edge.Slack);
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
                        // Seen along its axis a cylinder has no outline of its own: its rims are the outline.
                        if (side.Length < 1e-6 || !side.Unitize()) continue;
                        // The exact outline is the ruling a radius to one side of the axis. In the drawing the curve Rhino
                        // gives is off it by (w·side ∓ radius), and it is nearer or further than it by w·depth: both are
                        // linear in the point, so the curve's control points bound them for the whole curve. On a face
                        // that is only near its cylinder the curve can be millimetres round the surface from the exact
                        // ruling and still be the same line of the drawing to within the face's bound.
                        var drawn = Drawable(silhouette.Curve); double off = double.PositiveInfinity, deep = 0; int sign = 0;
                        var depth = e - cylinder.Direction * (cylinder.Direction * e);
                        if (drawn is NurbsCurve net)
                        {
                            off = 0;
                            foreach (var point in net.Points)
                            {
                                if (!(point.Weight > 0)) { off = double.PositiveInfinity; break; }
                                var w = point.Location - cylinder.Origin; double s = w * side; int here = s < 0 ? -1 : 1;
                                if (sign == 0) sign = here; else if (sign != here) { off = double.PositiveInfinity; break; }
                                off = Math.Max(off, Math.Abs(s - here * cylinder.Radius)); deep = Math.Max(deep, Math.Abs(w * depth));
                            }
                        }
                        else if (drawn is LineCurve line)
                        {
                            off = 0;
                            foreach (var q in new[] { line.PointAtStart, line.PointAtEnd })
                            {
                                var w = q - cylinder.Origin; double s = w * side; int here = s < 0 ? -1 : 1;
                                if (sign == 0) sign = here; else if (sign != here) { off = double.PositiveInfinity; break; }
                                off = Math.Max(off, Math.Abs(s - here * cylinder.Radius)); deep = Math.Max(deep, Math.Abs(w * depth));
                            }
                        }
                        // A curve Rhino gives that is not on the exact outline (a dent's own outline) is not known to
                        // touch the cylinder: it is classified as a curve near the body, with slack and no surface of its own.
                        if (!(off <= near)) { yield return new(drawn, input, -1, 0, false, _ => true, _tol + cylinder.Slack); continue; }
                        int outlineSide = sign;
                        Point3d? OnOutline(Point3d q) { var w = q - cylinder.Origin; return cylinder.Origin + side * (outlineSide * cylinder.Radius) + cylinder.Direction * (w * cylinder.Direction); }
                        // Rhino places the curve to its tolerance: that is added to both bounds.
                        yield return new(drawn, input, solid, 1 << surface, true, _ => true, 0, null, OnOutline, off + _tol, deep + _tol);
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
            // A change point against a solid with slack is known no better than that slack.
            var found = _removed.Intervals(AnalyticCandidate.Straight(At(interval.Start), At(interval.End)), _tol, absorb: Math.Max(_tol, 8 * _removed.Slack), token: token);
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
            // HLD's answer for a candidate it was asked about is united with the exact answer below.
            if (_asked.TryGetValue(c.Tag.Input, out int asked))
            { Heard(asked, c.Curve, c.Class == LineClass.Hidden ? Said.Hidden : c.Class == LineClass.Visible ? Said.Visible : Said.Unresolved); continue; }
            if (c.Class == LineClass.Hidden) { result.Add(c); continue; }
            // A native curve is only known to the native tolerance: a decision must hold half a tolerance to each side,
            // and an undecided run up to four tolerances between decided neighbours is that curve's change point.
            var pieces = Pieces(c.Curve, _removed, -1, 0, false, _tol / 2, Math.Max(4 * _tol, 8 * (_tol / 2 + _removed.Slack)), token);
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
        var endOn = _endOn;
        var chords = new List<Chord>?[_candidates.Count]; long done = 0;
        var solved = new List<(Interval Domain, VisibilityClass Class)>[chords.Length]; var made = new List<(Curve Curve, ComposedVisibility.Tag Tag, LineClass Class, bool Profile)>();
        var known = new List<double>();      // for each piece made: how far its drawn curve may be from the exact one
        Parallel.For(0, chords.Length, new ParallelOptions { MaxDegreeOfParallelism = Math.Min(Environment.ProcessorCount, 8), CancellationToken = token },
            i =>
            {
                if (!endOn[i])
                {
                    var c = _candidates[i];
                    chords[i] = Cut(c.Curve, _all, c.Solid, c.OnSurface, c.Tangent, c.Loose, token, c.Contacts, c.Snap, c.Slack == 0 && c.Gap == 0);
                    // A drawn edge is known to its slack or gap, and a solid's surface to its own slack: change points
                    // are known to a few times that.
                    solved[i] = Solve(c.Curve, chords[i], _all, c.Loose, Math.Max(_tol, 8 * (c.Loose + _all.Slack)), token, c.Depth);
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
                made.Add((whole, tag, LineClass.Hidden, false)); known.Add(0); continue;
            }
            // A whole closed rim in one class is emitted as two halves: seen edge-on a closed curve projects onto
            // itself, and a piece whose two ends coincide is poor drawing geometry.
            if (_asked.Count != 0 && candidate.Native)
                pieces = United(index, pieces).Where(p => p.Class is not null).Select(p => (p.Domain, p.Class!.Value)).ToList();
            // (The whole curve: its one piece spans the domain, to rounding.)
            if (pieces.Count == 1 && candidate.Curve.IsClosed && pieces[0].Domain.Length >= candidate.Curve.Domain.Length * (1 - 1e-9))
                pieces = [(new Interval(pieces[0].Domain.Min, pieces[0].Domain.Mid), pieces[0].Class), (new Interval(pieces[0].Domain.Mid, pieces[0].Domain.Max), pieces[0].Class)];
            foreach (var piece in pieces)
            {
                var part = CurveIntervals.Trim(candidate.Curve, piece.Domain); if (part is null) continue;
                owned.Add(part);
                made.Add((part, tag, PlanarExtraction.OutputClass(piece.Class), candidate.Profile(part.PointAt(part.Domain.Mid)))); known.Add(candidate.Loose);
            }
        }
        result.AddRange(WithoutCoveredHidden(made, known, owned, token));
        curves.Clear(); curves.AddRange(result);
    }

    /// <summary>
    /// The drawing-locus convention for curves, as the planar loci have it for lines: a Hidden piece that lies exactly
    /// under a Visible or Unresolved piece in the drawing, or under an earlier Hidden one, is not drawn twice. This is
    /// the bottom rim of a column in plan, the far half of a rim seen edge-on, the rims of stacked members.
    /// Straight pieces are left to the locus reconciliation.
    /// An Unresolved piece that lies exactly under a Visible piece of the same source, no nearer the viewer than it, is
    /// not drawn either: whichever class it has, that source draws a Visible line there (the lower edge of a cope seen
    /// square-on lies under the upper one, and every ray from it passes through the upper edge itself).
    /// Two drawn curves are one locus when they agree to half a tolerance plus what each is known to.
    /// </summary>
    private List<(Curve, ComposedVisibility.Tag, LineClass, bool)> WithoutCoveredHidden(
        List<(Curve Curve, ComposedVisibility.Tag Tag, LineClass Class, bool Profile)> made, List<double> known, List<GeometryBase> owned, CancellationToken token)
    {
        double Depth(Point3d p) => (p - _plane.Origin) * _plane.ZAxis;
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
                flat[i] = p; var box = p.GetBoundingBox(true); box.Inflate(_tol + known[i]); tree.Insert(box, i);
            }
            for (int i = 0; i < made.Count; i++)
            {
                token.ThrowIfCancellationRequested(); var piece = made[i];
                if (piece.Class == LineClass.Visible || flat[i] is not { } mine) { kept.Add(piece); continue; }
                bool undecided = piece.Class != LineClass.Hidden;
                var others = new List<int>(); var reach = mine.GetBoundingBox(true); reach.Inflate(known[i]);
                tree.Search(reach, (_, a) =>
                {
                    int j = a.Id; if (j == i || gone[j]) return;
                    if (undecided ? made[j].Class == LineClass.Visible && made[j].Tag.Input.Source.Identity == piece.Tag.Input.Source.Identity : made[j].Class != LineClass.Hidden || j < i) others.Add(j);
                });
                if (others.Count == 0) { kept.Add(piece); continue; }
                others.Sort();
                var cuts = new SortedSet<double> { mine.Domain.Min, mine.Domain.Max };
                foreach (int j in others)
                    foreach (var end in new[] { flat[j]!.PointAtStart, flat[j]!.PointAtEnd })
                        if (mine.ClosestPoint(end, out double t, _tol + known[i] + known[j])) cuts.Add(t);
                var ends = cuts.ToArray(); double start = double.NaN; gone[i] = true;
                for (int k = 0; k + 1 < ends.Length; k++)
                {
                    // Covered: one other piece lies within half a tolerance (and what the two are known to) at three
                    // points of this stretch; for an undecided piece, no further from the viewer than it there.
                    bool covered = others.Any(j => new[] { .25, .5, .75 }.All(f =>
                    {
                        double t = ends[k] + (ends[k + 1] - ends[k]) * f;
                        if (!flat[j]!.ClosestPoint(mine.PointAt(t), out double tj, _tol / 2 + known[i] + known[j])) return false;
                        return !undecided || Depth(made[j].Curve.PointAt(tj)) >= Depth(piece.Curve.PointAt(t)) - _tol - known[i] - known[j];
                    }));
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

    /// <param name="Undecided">A point of this chord could not be taken to the exact curve it stands for.</param>
    private sealed record Chord(double A, double B, double Length, AnalyticCandidate Piece, bool Undecided = false);

    /// <summary>The curve as chords in view coordinates; null when no occluder is near it; empty when it has no valid bounds.</summary>
    private List<Chord>? Cut(Curve curve, AnalyticOccluders occluders, int solid, int onSurface, bool tangent, double slack, CancellationToken token,
        IReadOnlyList<(int Solid, int OnSurface)>? contacts = null, Func<Point3d, Point3d?>? snap = null, bool? fine = null)
    {
        Point3d? At(double t) => snap is null ? curve.PointAt(t) : snap(curve.PointAt(t));
        var toView = Transform.PlaneToPlane(_plane, Plane.WorldXY); var box = curve.GetBoundingBox(toView); var near = new List<int>();
        if (!box.IsValid) return [];
        occluders.Near(box.Min.X - slack, box.Min.Y - slack, box.Max.X + slack, box.Max.Y + slack, box.Min.Z, _tol, near);
        if (near.Count == 0) return null;
        var chords = new List<Chord>();
        // A candidate of an exact solid can lie on the boundary of another one (the rims of stacked columns in plan, a
        // mitre joint): its chords must follow the curve far closer than the graze width, or the boundary rule would
        // see each chord dip in and out. Native curves are only known to the tolerance and get the slack instead.
        // A curve that is itself only known to its slack needs chords no finer than a quarter of that.
        foreach (var (ta, tb) in Chords(curve, Math.Max(_tol / 8, slack / 4), solid >= 0 && (fine ?? slack == 0) ? AnalyticSolid.Graze(_tol) / 8 : 0, _tol, snap))
        {
            token.ThrowIfCancellationRequested();
            if (At(ta) is not { } pa || At(tb) is not { } pb || At((ta + tb) / 2) is not { } pm)
            {   // No exact curve here to classify: the stretch is not decided.
                Point3d ra = curve.PointAt(ta), rb = curve.PointAt(tb);
                chords.Add(new(ta, tb, ra.DistanceTo(rb), AnalyticCandidate.Straight(Project(ra), Project(rb)), true)); continue;
            }
            chords.Add(new(ta, tb, pa.DistanceTo(pb), new(Project(pa), Project(pm), Project(pb), solid, onSurface, tangent, contacts)));
        }
        return chords;
    }

    private List<(Interval Domain, VisibilityClass Class)> Solve(Curve curve, List<Chord>? chords, AnalyticOccluders occluders, double slack, double absorb, CancellationToken token, double depth = 0)
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
            if (chord.Undecided) { Push(ta, tb, VisibilityClass.Unresolved, chord.Length); continue; }
            foreach (var i in occluders.Intervals(chord.Piece, _tol, slack, absorb: 0, token: token, depth: depth))
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
    private static IEnumerable<(double, double)> Chords(Curve curve, double sag, double fit = 0, double shortest = 0, Func<Point3d, Point3d?>? snap = null)
    {
        Point3d P(double t) { var q = curve.PointAt(t); return snap is null ? q : snap(q) ?? q; }
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
