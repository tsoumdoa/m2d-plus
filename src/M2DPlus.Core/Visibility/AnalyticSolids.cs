namespace M2DPlus.Core.Visibility;

/// <summary>
/// Exact occluders for parallel views: sphere, cylinder between two planes (optionally hollow) and an open
/// cylindrical sheet (an extruded arc). View coordinates as everywhere in this namespace: larger Z is nearer.
///
/// The question for a point p is: does the solid hold a stretch of the view ray from p, starting one tolerance in
/// front of p, that is long enough to be a real covering and not a contact? With g the length of that stretch:
///   g ≥ tolerance/8   Hidden
///   g ≤ tolerance/16  Visible. This is the tie rule in depth: a point on or within a tolerance under a surface is in
///                     contact with it, not behind it.
///   otherwise         Unresolved.
/// Sideways the solids are closed, as polygons are in the planar kernel: a ray that runs along an outline, in the
/// plane of a cap seen edge-on or along the edge of a sheet is inside. Otherwise a line behind two stacked columns
/// would show through their joint. "Along" means within tolerance/2^16 (the graze width); outside by twice that is
/// outside; between the two is Unresolved. A ray that only touches a round outline is given a stretch one tolerance
/// each way about the touching point, so the depth rule above still decides it: a touch at the point's own depth
/// stays contact. Bores are open: a ray along a bore's wall is in the material.
/// Everything is computed in binary64 with explicit error bounds. A bound that reaches a window gives Unresolved:
/// nothing is decided by an unproven comparison.
/// </summary>
public abstract class AnalyticSolid
{
    /// <summary>Surfaces of its own solid a candidate lies on.</summary>
    public const int Outer = 1, Inner = 2;
    public double X0 { get; protected init; }
    public double Y0 { get; protected init; }
    public double X1 { get; protected init; }
    public double Y1 { get; protected init; }
    public double ZMax { get; protected init; }

    /// <summary>
    /// own: surfaces of this solid the point is known to lie on (it is moved exactly onto them).
    /// tangent: the point is on an outline of those surfaces, so the ray touches them without crossing.
    /// </summary>
    public abstract VisibilityClass Occlusion(Vertex p, double tolerance, int own = 0, bool tangent = false);

    /// <summary>Parameters on a + t d where Occlusion can change. Extra values only subdivide.</summary>
    internal abstract void Events(Vertex a, Vertex d, double tolerance, List<double> ts);

    internal const double Epsilon = 2.220446049250313e-16;

    internal static VisibilityClass Decide(double g, double error, double tolerance)
    {
        if (!(error < tolerance / 64) || double.IsNaN(g)) return VisibilityClass.Unresolved;
        if (g - error >= tolerance / 8) return VisibilityClass.Hidden;
        if (g + error <= tolerance / 16) return VisibilityClass.Visible;
        return VisibilityClass.Unresolved;
    }

    /// <summary>The stretch a s² + 2 b s + c ≤ 0 of the ray, with the error of its ends.</summary>
    internal readonly record struct Stretch(double Lo, double Hi, double Error, bool Empty)
    {
        public static readonly Stretch None = new(0, 0, 0, true);
        public static readonly Stretch Unknown = new(double.NaN, double.NaN, double.PositiveInfinity, false);
        public bool Undecided => double.IsNaN(Lo);
    }

    /// <summary>The width within which a ray counts as running along a boundary: tolerance/2^16. Far above rounding
    /// error for anything a tolerance can resolve, far below anything a drawing can show, and wide enough that a curved
    /// candidate cut into chords at an eighth of a tolerance follows its curve to a fraction of it.</summary>
    public static double Graze(double tolerance) => tolerance / 65536;

    /// <summary>
    /// The stretch of the view ray inside a round section of the given radius (a cylinder about an axis, or a sphere).
    /// across: from the axis (or centre) to the ray's start, measured across the axis. along: the view direction across
    /// the axis; a = |along|². delta: error of the components of across. onSurface: the start lies on the surface.
    /// Whether the ray is inside at all is decided sideways, at its closest approach to the axis, so the depth of the
    /// start does not cancel and the test holds at any coordinate size a tolerance can resolve.
    /// closed: the surface belongs to the section (a solid's outside); open: it does not (a bore).
    /// </summary>
    internal static Stretch Section(double a, Vertex across, Vertex along, double radius, double delta, double tolerance, bool onSurface, bool closed = true)
    {
        double graze = Graze(tolerance), enter = closed ? -graze : 2 * graze, miss = closed ? -2 * graze : graze;
        if (a < 1e-12)
        {   // The ray runs along the axis: inside or outside for its whole length.
            if (onSurface) return closed ? Whole : Stretch.None;
            if (!(delta < graze / 4)) return Stretch.Unknown;
            double depth = radius - Math.Sqrt(Dot(across, across));
            return depth >= enter ? Whole : depth <= miss ? Stretch.None : Stretch.Unknown;
        }
        double b = Dot(across, along), centre = -b / a, errorCentre = 4 * delta / a;
        if (onSurface)
        {   // |across| is the radius by construction: the roots are 0 and -2b/a.
            double other = 2 * centre; return new(Math.Min(0, other), Math.Max(0, other), 2 * errorCentre, false);
        }
        var q = Mix(across, centre, along); double reach = Math.Sqrt(Dot(q, q)), errorQ = delta * (1 + 4 / Math.Sqrt(a));
        if (!(errorQ < graze / 4)) return Stretch.Unknown;
        double inside = radius - reach;
        if (inside <= miss) return Stretch.None;
        if (inside < enter) return Stretch.Unknown;
        // A closed section that is only touched still holds the touching point: one tolerance each way about it.
        double half = Math.Sqrt(Math.Max(inside, 0) * (radius + reach) / a), error = errorCentre + 4 * Epsilon * (Math.Abs(centre) + half);
        if (closed && half < tolerance) return new(centre - tolerance, centre + tolerance, error, false);
        return new(centre - half, centre + half, half * errorQ / inside + error, false);
    }
    private static readonly Stretch Whole = new(double.NegativeInfinity, double.PositiveInfinity, 0, false);

    internal static void Quadratic(double a, double b, double c, List<double> ts)
    {
        double scale = Math.Abs(a) + Math.Abs(b) + Math.Abs(c);
        if (scale == 0 || !double.IsFinite(scale)) return;
        if (Math.Abs(a) <= 1e-14 * scale) { if (Math.Abs(b) > 1e-14 * scale) Add(-c / b, ts); return; }
        double d = b * b - 4 * a * c;
        if (d < 0) { if (d > -1e-9 * (b * b + Math.Abs(4 * a * c))) Add(-b / (2 * a), ts); return; }
        double q = -.5 * (b + (b >= 0 ? 1 : -1) * Math.Sqrt(d));
        Add(q / a, ts); if (q != 0) Add(c / q, ts);
    }
    internal static void Add(double t, List<double> ts) { if (t > 0 && t < 1) ts.Add(t); }

    /// <summary>Coordinates are exact inputs; what is computed from them is local to the solid. But binary64 must be
    /// able to tell positions a fraction of a tolerance apart at this distance from the origin (as in the planar kernel).</summary>
    internal static bool Resolvable(Vertex p, Vertex o, double tolerance)
    {
        double size = Math.Max(Math.Max(Math.Abs(p.X), Math.Abs(p.Y)), Math.Max(Math.Abs(p.Z), Math.Max(Math.Abs(o.X), Math.Max(Math.Abs(o.Y), Math.Abs(o.Z)))));
        return double.IsFinite(size) && tolerance >= 1e-150 && Math.BitIncrement(size) - size <= tolerance / 64;
    }
    internal static Vertex Sub(Vertex a, Vertex b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    internal static Vertex Mix(Vertex a, double k, Vertex b) => new(a.X + k * b.X, a.Y + k * b.Y, a.Z + k * b.Z);
    internal static double Dot(Vertex a, Vertex b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    internal static double Size(Vertex a) => Math.Abs(a.X) + Math.Abs(a.Y) + Math.Abs(a.Z);
    internal static Vertex Unit(Vertex a)
    {
        double l = Math.Sqrt(Dot(a, a));
        if (!(l > 0) || !double.IsFinite(l)) throw new ArgumentException("Degenerate direction");
        return new(a.X / l, a.Y / l, a.Z / l);
    }

    /// <summary>Events of an infinite cylinder and its rims; shared by the solid and the sheet.</summary>
    internal static void CylinderEvents(Vertex o, Vertex u, double radius, ReadOnlySpan<(Vertex N, double D)> rims, Vertex a, Vertex v, double offset, List<double> ts)
    {
        double eu = u.Z, A = 1 - eu * eu;
        var w = Sub(a, o); double wu = Dot(w, u), vu = Dot(v, u);
        double c2 = Dot(v, v) - vu * vu, c1 = 2 * (Dot(w, v) - wu * vu), c0 = Dot(w, w) - wu * wu - radius * radius;
        Quadratic(c2, c1, c0, ts);                                                            // the ray's foot on the surface
        var q = new Vertex(w.X, w.Y, w.Z + offset); double qu = Dot(q, u);
        Quadratic(c2, 2 * (Dot(q, v) - qu * vu), Dot(q, q) - qu * qu - radius * radius, ts);  // the ray's start on the surface
        double b0 = w.Z - wu * eu, b1 = v.Z - vu * eu;
        double d2 = b1 * b1 - A * c2, d1 = 2 * b0 * b1 - A * c1, d0 = b0 * b0 - A * c0;
        Quadratic(d2, d1, d0, ts);                                                            // the ray tangent to the surface
        foreach (var (n, d) in rims)                                                          // the ray through a rim; n·(x - o) = d
        {
            double ne = n.Z, l0 = A * (d - Dot(n, w)) + ne * b0, l1 = -A * Dot(n, v) + ne * b1;
            Quadratic(ne * ne * d2 - l1 * l1, ne * ne * d1 - 2 * l0 * l1, ne * ne * d0 - l0 * l0, ts);
        }
    }
}

public sealed class AnalyticSphere : AnalyticSolid
{
    private readonly Vertex _c; private readonly double _r;
    public AnalyticSphere(Vertex centre, double radius)
    {
        if (!(radius > 0) || !double.IsFinite(radius + Size(centre))) throw new ArgumentException("Invalid sphere");
        _c = centre; _r = radius; X0 = centre.X - radius; X1 = centre.X + radius; Y0 = centre.Y - radius; Y1 = centre.Y + radius; ZMax = centre.Z + radius;
    }
    public override VisibilityClass Occlusion(Vertex p, double tolerance, int own = 0, bool tangent = false)
    {
        if ((own & Outer) != 0 && tangent) return VisibilityClass.Visible;   // the ray touches a convex body: contact
        if (!Resolvable(p, _c, tolerance)) return VisibilityClass.Unresolved;
        var w = Sub(p, _c); double delta = 8 * Epsilon * Size(w);
        var s = Section(1, w, new(0, 0, 1), _r, delta, tolerance, (own & Outer) != 0);
        if (s.Empty) return VisibilityClass.Visible;
        if (s.Undecided) return VisibilityClass.Unresolved;
        return Decide(s.Hi - Math.Max(s.Lo, tolerance), s.Error, tolerance);
    }
    internal override void Events(Vertex a, Vertex d, double tolerance, List<double> ts)
    {
        var w = Sub(a, _c);
        Quadratic(d.X * d.X + d.Y * d.Y, 2 * (w.X * d.X + w.Y * d.Y), w.X * w.X + w.Y * w.Y - _r * _r, ts);
        var q = new Vertex(w.X, w.Y, w.Z + tolerance * 1.09375);
        Quadratic(Dot(d, d), 2 * Dot(q, d), Dot(q, q) - _r * _r, ts);
    }
}

/// <summary>A cylinder between two cap planes that do not meet inside it; hollow when innerRadius is positive.</summary>
public sealed class AnalyticCylinder : AnalyticSolid
{
    private readonly Vertex _o, _u; private readonly double _r, _ri; private readonly (Vertex N, double D)[] _caps; // inside: N·(x - o) ≤ D
    /// <param name="caps">Each cap: a point on its plane and the normal pointing out of the solid.</param>
    public AnalyticCylinder(Vertex origin, Vertex axis, double radius, double innerRadius, IReadOnlyList<(Vertex Point, Vertex Normal)> caps)
    {
        if (!(radius > 0) || !(innerRadius >= 0) || innerRadius >= radius || caps.Count != 2 || !double.IsFinite(radius + Size(origin))) throw new ArgumentException("Invalid cylinder");
        _o = origin; _u = Unit(axis); _r = radius; _ri = innerRadius; _caps = new (Vertex, double)[2];
        double lo = double.PositiveInfinity, hi = double.NegativeInfinity;
        for (int i = 0; i < 2; i++)
        {
            var n = Unit(caps[i].Normal); double un = Dot(n, _u);
            if (Math.Abs(un) < 1e-9) throw new ArgumentException("A cap parallel to the axis");
            _caps[i] = (n, Dot(n, Sub(caps[i].Point, origin)));
            double cut = _caps[i].D / un, reach = radius * Math.Sqrt(Math.Max(0, 1 - un * un)) / Math.Abs(un);
            lo = Math.Min(lo, cut - reach); hi = Math.Max(hi, cut + reach);
        }
        var p = Mix(origin, lo, _u); var q = Mix(origin, hi, _u);
        X0 = Math.Min(p.X, q.X) - radius; X1 = Math.Max(p.X, q.X) + radius; Y0 = Math.Min(p.Y, q.Y) - radius; Y1 = Math.Max(p.Y, q.Y) + radius; ZMax = Math.Max(p.Z, q.Z) + radius;
    }

    public override VisibilityClass Occlusion(Vertex p, double tolerance, int own = 0, bool tangent = false)
    {
        if ((own & Outer) != 0 && tangent) return VisibilityClass.Visible;   // touching the convex outer surface: contact
        if (!Resolvable(p, _o, tolerance)) return VisibilityClass.Unresolved;
        var w = Sub(p, _o); double wu = Dot(w, _u), eu = _u.Z, a = 1 - eu * eu;
        var across = Mix(w, -wu, _u);                                        // the part of w across the axis
        double size = Size(w), delta = 8 * Epsilon * size;
        var along = new Vertex(-eu * _u.X, -eu * _u.Y, 1 - eu * _u.Z);
        var s = Section(a, across, along, _r, delta, tolerance, (own & Outer) != 0);
        if (s.Empty) return VisibilityClass.Visible;
        if (s.Undecided) return VisibilityClass.Unresolved;
        double lo = s.Lo, hi = s.Hi, error = s.Error;
        foreach (var (n, d) in _caps)
        {
            double ne = n.Z, rhs = d - Dot(n, w), errorRhs = 8 * Epsilon * (Math.Abs(d) + size) + delta;
            if (Math.Abs(ne) < 1e-9)
            {   // A cap seen edge-on: the ray runs level with it. The solid is closed, so a ray in the cap's plane is
                // inside (a flush joint between stacked members must not let anything show through), whatever the
                // sign of rounding noise; beyond the plane by two graze widths it is outside.
                if (!(errorRhs < Graze(tolerance) / 4)) return VisibilityClass.Unresolved;
                if (rhs <= -2 * Graze(tolerance)) return VisibilityClass.Visible;
                if (rhs < -Graze(tolerance)) return VisibilityClass.Unresolved;
                continue;
            }
            double bound = rhs / ne; error = Math.Max(error, errorRhs / Math.Abs(ne));
            if (ne > 0) hi = Math.Min(hi, bound); else lo = Math.Max(lo, bound);
        }
        lo = Math.Max(lo, tolerance);
        if (_ri <= 0 || (own & Inner) != 0 && tangent) return Decide(hi - lo, error, tolerance);
        var bore = Section(a, across, along, _ri, delta, tolerance, (own & Inner) != 0, closed: false);
        if (bore.Empty) return Decide(hi - lo, error, tolerance);
        if (bore.Undecided) return VisibilityClass.Unresolved;
        error = Math.Max(error, bore.Error);
        // What is left of [lo, hi] outside the bore: the longer of the two wall stretches.
        return Decide(Math.Max(Math.Min(hi, bore.Lo) - lo, hi - Math.Max(lo, bore.Hi)), error, tolerance);
    }

    internal override void Events(Vertex a, Vertex d, double tolerance, List<double> ts)
    {
        double offset = tolerance * 1.09375;
        CylinderEvents(_o, _u, _r, _caps, a, d, offset, ts);
        if (_ri > 0) CylinderEvents(_o, _u, _ri, _caps, a, d, offset, ts);
        var w = Sub(a, _o);
        foreach (var (n, k) in _caps)
        {
            double nv = Dot(n, d), rhs = k - Dot(n, w);
            if (nv == 0) continue;
            Add(rhs / nv, ts); Add((rhs - n.Z * offset) / nv, ts);                    // the foot and the start on a cap
            Add((rhs + 1.5 * Graze(tolerance)) / nv, ts);                             // the edge-on window
        }
        var (n1, k1) = _caps[0]; var (n2, k2) = _caps[1];
        if (n1.Z != 0 && n2.Z != 0)
        {   // where the two cap bounds on the ray coincide
            double c0 = (k1 - Dot(n1, w)) * n2.Z - (k2 - Dot(n2, w)) * n1.Z, c1 = -Dot(n1, d) * n2.Z + Dot(n2, d) * n1.Z;
            if (c1 != 0) Add(-c0 / c1, ts);
        }
    }
}

/// <summary>An open piece of a cylinder between two rulings and two arcs across the axis: a sheet without thickness.</summary>
public sealed class AnalyticCylinderSheet : AnalyticSolid
{
    private readonly Vertex _o, _u, _x, _y; private readonly double _r, _phi, _sweep, _h0, _h1; private readonly (Vertex N, double D)[] _rims;
    /// <param name="x">A direction across the axis from which angles are measured; y completes the frame.</param>
    public AnalyticCylinderSheet(Vertex origin, Vertex axis, Vertex x, double radius, double startAngle, double sweep, double height0, double height1)
    {
        if (!(radius > 0) || !(sweep > 0) || sweep > 2 * Math.PI + 1e-9 || !(height1 > height0) || !double.IsFinite(radius + Size(origin) + startAngle)) throw new ArgumentException("Invalid cylinder sheet");
        _o = origin; _u = Unit(axis); _x = Unit(Mix(x, -Dot(x, _u), _u));
        _y = new(_u.Y * _x.Z - _u.Z * _x.Y, _u.Z * _x.X - _u.X * _x.Z, _u.X * _x.Y - _u.Y * _x.X);
        _r = radius; _phi = startAngle; _sweep = Math.Min(sweep, 2 * Math.PI); _h0 = height0; _h1 = height1;
        _rims = [(_u, height0), (_u, height1)];
        var p = Mix(origin, height0, _u); var q = Mix(origin, height1, _u);
        X0 = Math.Min(p.X, q.X) - radius; X1 = Math.Max(p.X, q.X) + radius; Y0 = Math.Min(p.Y, q.Y) - radius; Y1 = Math.Max(p.Y, q.Y) + radius; ZMax = Math.Max(p.Z, q.Z) + radius;
    }

    /// <summary>Is the point of the cylinder at depth s on the ray part of the sheet? The sheet is closed: its edge is on
    /// it; beyond the edge by two graze widths is off.</summary>
    private VisibilityClass On(Vertex w, double s, double tolerance)
    {
        var q = new Vertex(w.X, w.Y, w.Z + s); double h = Dot(q, _u), inside = Math.Min(h - _h0, _h1 - h);
        if (_sweep < 2 * Math.PI)
        {
            double angle = (Math.Atan2(Dot(q, _y), Dot(q, _x)) - _phi) % (2 * Math.PI); if (angle < 0) angle += 2 * Math.PI;
            // Beyond the sweep, measure from the nearer ruling (negative: outside).
            double along = angle <= _sweep ? Math.Min(angle, _sweep - angle) : -Math.Min(angle - _sweep, 2 * Math.PI - angle);
            inside = Math.Min(inside, along * _r);
        }
        return inside >= -Graze(tolerance) ? VisibilityClass.Hidden : inside <= -2 * Graze(tolerance) ? VisibilityClass.Visible : VisibilityClass.Unresolved;
    }

    public override VisibilityClass Occlusion(Vertex p, double tolerance, int own = 0, bool tangent = false)
    {
        if ((own & Outer) != 0 && tangent) return VisibilityClass.Visible;   // the ray touches the cylinder and meets it nowhere else
        if (!Resolvable(p, _o, tolerance)) return VisibilityClass.Unresolved;
        var w = Sub(p, _o); double wu = Dot(w, _u), eu = _u.Z, a = 1 - eu * eu;
        if (a < 1e-12) return VisibilityClass.Visible;                       // seen along the axis a sheet covers nothing
        var across = Mix(w, -wu, _u);
        double delta = 8 * Epsilon * Size(w);
        var s = Section(a, across, new(-eu * _u.X, -eu * _u.Y, 1 - eu * _u.Z), _r, delta, tolerance, (own & Outer) != 0);
        if (s.Empty) return VisibilityClass.Visible;
        if (s.Undecided) return VisibilityClass.Unresolved;
        var result = VisibilityClass.Visible;
        foreach (double root in stackalloc[] { s.Lo, s.Hi })
        {
            // In front by more than a tolerance (the same window as for solids), and on the sheet?
            var front = Decide(root - tolerance, s.Error, tolerance);
            if (front == VisibilityClass.Visible) continue;
            var on = On(w, root, tolerance);
            if (on == VisibilityClass.Visible) continue;
            if (front == VisibilityClass.Hidden && on == VisibilityClass.Hidden) return VisibilityClass.Hidden;
            result = VisibilityClass.Unresolved;
        }
        return result;
    }

    internal override void Events(Vertex a, Vertex d, double tolerance, List<double> ts)
    {
        CylinderEvents(_o, _u, _r, _rims, a, d, tolerance * 1.09375, ts);
        if (_sweep >= 2 * Math.PI) return;
        foreach (double angle in stackalloc[] { _phi, _phi + _sweep })
        {   // the ray through a ruling
            var foot = Mix(Mix(_o, _r * Math.Cos(angle), _x), _r * Math.Sin(angle), _y); double den = d.X * _u.Y - d.Y * _u.X;
            if (den != 0) Add(((foot.X - a.X) * _u.Y - (foot.Y - a.Y) * _u.X) / den, ts);
        }
    }
}

/// <summary>A short piece of a candidate. A and B are on the curve and M is the curve's point at the middle
/// parameter, so points are taken on a parabola that follows the curve far closer than the chord does.</summary>
public readonly record struct AnalyticCandidate(Vertex A, Vertex M, Vertex B, int Solid = -1, int OnSurface = 0, bool Tangent = false)
{
    public static AnalyticCandidate Straight(Vertex a, Vertex b) => new(a, new((a.X + b.X) / 2, (a.Y + b.Y) / 2, (a.Z + b.Z) / 2), b);
    public Vertex At(double t)
    {
        double ka = (1 - t) * (1 - 2 * t), km = 4 * t * (1 - t), kb = t * (2 * t - 1);
        return new(A.X * ka + M.X * km + B.X * kb, A.Y * ka + M.Y * km + B.Y * kb, A.Z * ka + M.Z * km + B.Z * kb);
    }
}

public readonly record struct AnalyticInterval(double Start, double End, VisibilityClass Class);

/// <summary>Union over a set of analytic occluders: a point, or the intervals of a candidate piece.</summary>
public sealed class AnalyticOccluders
{
    private readonly AnalyticSolid[] _solids; private readonly double _x0, _y0, _cell; private readonly int _nx, _ny; private readonly int[] _start = [0, 0], _items = [];
    public int Count => _solids.Length;
    public AnalyticOccluders(IReadOnlyList<AnalyticSolid> solids)
    {
        _solids = solids.ToArray(); _nx = _ny = 1; _cell = 1;
        if (_solids.Length == 0) return;
        _x0 = _solids.Min(s => s.X0); _y0 = _solids.Min(s => s.Y0); double x1 = _solids.Max(s => s.X1), y1 = _solids.Max(s => s.Y1);
        _cell = Math.Max(1e-9, 2 * _solids.Average(s => Math.Max(s.X1 - s.X0, s.Y1 - s.Y0)));
        _nx = Math.Clamp((int)((x1 - _x0) / _cell) + 1, 1, 1024); _ny = Math.Clamp((int)((y1 - _y0) / _cell) + 1, 1, 1024);
        var count = new int[_nx * _ny + 1];
        foreach (var s in _solids) { Cells(s.X0, s.Y0, s.X1, s.Y1, out int i0, out int j0, out int i1, out int j1); for (int j = j0; j <= j1; j++) for (int i = i0; i <= i1; i++) count[j * _nx + i + 1]++; }
        for (int c = 0; c < _nx * _ny; c++) count[c + 1] += count[c];
        _start = (int[])count.Clone(); _items = new int[count[_nx * _ny]];
        for (int id = _solids.Length - 1; id >= 0; id--)
        { var s = _solids[id]; Cells(s.X0, s.Y0, s.X1, s.Y1, out int i0, out int j0, out int i1, out int j1); for (int j = j0; j <= j1; j++) for (int i = i0; i <= i1; i++) _items[--count[j * _nx + i + 1]] = id; }
    }
    private void Cells(double x0, double y0, double x1, double y1, out int i0, out int j0, out int i1, out int j1)
    {
        i0 = Math.Clamp((int)Math.Floor((x0 - _x0) / _cell), 0, _nx - 1); i1 = Math.Clamp((int)Math.Floor((x1 - _x0) / _cell), 0, _nx - 1);
        j0 = Math.Clamp((int)Math.Floor((y0 - _y0) / _cell), 0, _ny - 1); j1 = Math.Clamp((int)Math.Floor((y1 - _y0) / _cell), 0, _ny - 1);
    }

    /// <summary>Solids whose bounds meet the box and that reach in front of zMin.</summary>
    public void Near(double x0, double y0, double x1, double y1, double zMin, double tolerance, List<int> result)
    {
        result.Clear(); if (_solids.Length == 0 || !(x0 <= x1) || !(y0 <= y1)) return;
        Cells(x0, y0, x1, y1, out int i0, out int j0, out int i1, out int j1);
        for (int j = j0; j <= j1; j++)
            for (int i = i0; i <= i1; i++)
                for (int e = _start[j * _nx + i]; e < _start[j * _nx + i + 1]; e++)
                {
                    int id = _items[e]; var s = _solids[id];
                    if (s.X1 < x0 || s.X0 > x1 || s.Y1 < y0 || s.Y0 > y1 || s.ZMax <= zMin + tolerance) continue;
                    if (!result.Contains(id)) result.Add(id);
                }
        result.Sort();
    }

    /// <summary>Hidden if any solid proves it, Unresolved if any solid cannot decide, otherwise Visible.
    /// slack: how far the point itself may be off across the view (0 for exact candidates). A decision must then
    /// hold at the point and one slack to each side.</summary>
    public VisibilityClass Classify(Vertex p, double tolerance, double slack = 0, int except = -1)
    {
        if (!double.IsFinite(p.X + p.Y + p.Z)) return VisibilityClass.Unresolved;
        var near = new List<int>(); Near(p.X - slack, p.Y - slack, p.X + slack, p.Y + slack, p.Z, tolerance, near);
        return Classify(new AnalyticCandidate(p, p, p), p, near, tolerance, slack, except);
    }

    private VisibilityClass Classify(in AnalyticCandidate candidate, Vertex p, List<int> near, double tolerance, double slack, int except)
    {
        var result = VisibilityClass.Visible;
        foreach (int id in near)
        {
            if (id == except) continue;
            bool own = id == candidate.Solid; var solid = _solids[id];
            var k = solid is AnalyticCsg csg && slack > 0
                ? csg.Evaluate(p, slack, p.Z, p.Z, tolerance, own ? candidate.OnSurface : 0, own && candidate.Tangent)
                : solid.Occlusion(p, tolerance, own ? candidate.OnSurface : 0, own && candidate.Tangent);
            for (int side = 0; slack > 0 && solid is not AnalyticCsg && side < 4 && k != VisibilityClass.Unresolved; side++)
            {
                double dx = side == 0 ? slack : side == 1 ? -slack : 0, dy = side == 2 ? slack : side == 3 ? -slack : 0;
                if (solid.Occlusion(new(p.X + dx, p.Y + dy, p.Z), tolerance) != k) k = VisibilityClass.Unresolved;
            }
            if (k == VisibilityClass.Hidden) return VisibilityClass.Hidden;
            if (k == VisibilityClass.Unresolved) result = VisibilityClass.Unresolved;
        }
        return result;
    }

    /// <summary>
    /// Intervals of one candidate piece over [0, 1], in order and without gaps.
    /// Change points are closed-form roots on the chord; each interval is decided at its middle and checked just
    /// inside both ends, and bisected where those disagree. An undecided run no longer than `absorb` between decided
    /// neighbours of different classes is a change point known to that accuracy and is shared between them. The
    /// default is one tolerance: each side moves by half a tolerance at most, the same order as the planar kernel's
    /// endpoint certificate. Between neighbours of one class, or at an end, an undecided run is not a change point: it
    /// is given to the neighbour only when the candidate is exact (no slack), where it is a tie no longer than a
    /// tolerance. With slack it stays Unresolved: it may be a whole hidden stretch the slack could not decide.
    /// </summary>
    public List<AnalyticInterval> Intervals(in AnalyticCandidate candidate, double tolerance, double slack = 0, double absorb = -1, int except = -1, CancellationToken token = default)
    {
        var a = candidate.A; var b = candidate.B; var d = Sub(b, a);
        double length = Math.Sqrt(d.X * d.X + d.Y * d.Y + d.Z * d.Z);
        var output = new List<AnalyticInterval>();
        if (!double.IsFinite(length)) { output.Add(new(0, 1, VisibilityClass.Unresolved)); return output; }
        var near = new List<int>();
        Near(Math.Min(a.X, b.X) - slack, Math.Min(a.Y, b.Y) - slack, Math.Max(a.X, b.X) + slack, Math.Max(a.Y, b.Y) + slack, Math.Min(a.Z, Math.Min(b.Z, candidate.M.Z)), tolerance, near);
        if (except >= 0) near.Remove(except);
        if (near.Count == 0 || length == 0) { output.Add(new(0, 1, near.Count == 0 ? VisibilityClass.Visible : Classify(candidate, candidate.M, near, tolerance, slack, except))); return output; }
        if (absorb < 0) absorb = tolerance;
        double step = tolerance / 4 / length;
        // Solids with closed-form change points are solved between their events. Cut solids (AnalyticCsg) have change
        // points that are not closed-form: they are solved by bisection with a certificate for each piece. The two
        // results are then united.
        var cut = near.Where(id => _solids[id] is AnalyticCsg).ToList();
        if (cut.Count != 0) near = near.Where(id => _solids[id] is not AnalyticCsg).ToList();
        if (near.Count != 0 || cut.Count == 0)
        {
            var ts = new List<double> { 0, 1 };
            foreach (int id in near) _solids[id].Events(a, d, tolerance, ts);
            ts.Sort();
            for (int i = 0; i + 1 < ts.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                if (ts[i + 1] - ts[i] > 0) Refine(candidate, ts[i], ts[i + 1], step, near, tolerance, slack, except, output, 0);
            }
        }
        if (cut.Count != 0)
        {
            var certified = new List<AnalyticInterval>();
            Certify(candidate, 0, 1, step, cut, tolerance, slack, certified, 0, token);
            output = output.Count == 0 ? certified : Unite(output, certified);
        }
        // Join equal neighbours, then share short undecided runs.
        var joined = new List<AnalyticInterval>();
        foreach (var piece in output)
            if (joined.Count > 0 && joined[^1].Class == piece.Class) joined[^1] = joined[^1] with { End = piece.End }; else joined.Add(piece);
        for (int i = 0; i < joined.Count; i++)
        {
            var run = joined[i];
            if (run.Class != VisibilityClass.Unresolved || (run.End - run.Start) * length > absorb) continue;
            bool left = i > 0, right = i + 1 < joined.Count;
            bool changePoint = left && right && joined[i - 1].Class != joined[i + 1].Class;
            if (!changePoint && (slack > 0 || (run.End - run.Start) * length > tolerance)) continue;
            if (left && right)
            {
                if (joined[i - 1].Class == joined[i + 1].Class) { joined[i - 1] = joined[i - 1] with { End = joined[i + 1].End }; joined.RemoveRange(i, 2); i--; }
                else { double mid = (run.Start + run.End) / 2; joined[i - 1] = joined[i - 1] with { End = mid }; joined[i + 1] = joined[i + 1] with { Start = mid }; joined.RemoveAt(i); i--; }
            }
            else if (left) { joined[i - 1] = joined[i - 1] with { End = run.End }; joined.RemoveAt(i); i--; }
            else if (right) { joined[i + 1] = joined[i + 1] with { Start = run.Start }; joined.RemoveAt(i); i--; }
        }
        return joined;
    }

    private void Refine(in AnalyticCandidate candidate, double t0, double t1, double step, List<int> near, double tolerance, double slack, int except, List<AnalyticInterval> output, int depth)
    {
        double span = t1 - t0, inset = Math.Min(step / 2, span / 4);
        var middle = Classify(candidate, candidate.At((t0 + t1) / 2), near, tolerance, slack, except);
        if (span <= step || depth >= 48) { output.Add(new(t0, t1, middle)); return; }
        if (Classify(candidate, candidate.At(t0 + inset), near, tolerance, slack, except) == middle &&
            Classify(candidate, candidate.At(t1 - inset), near, tolerance, slack, except) == middle) { output.Add(new(t0, t1, middle)); return; }
        double tm = (t0 + t1) / 2;
        Refine(candidate, t0, tm, step, near, tolerance, slack, except, output, depth + 1);
        Refine(candidate, tm, t1, step, near, tolerance, slack, except, output, depth + 1);
    }

    /// <summary>
    /// Bisection with a certificate. A piece [t0, t1] of the candidate lies within a sideways radius and a depth range
    /// of its middle; when every cut solid gives one answer for all rays in that set, the whole piece has it. A piece
    /// no longer than a quarter of a tolerance that still has no certificate is decided at its middle, as the
    /// closed-form route decides its shortest pieces.
    /// </summary>
    private void Certify(in AnalyticCandidate candidate, double t0, double t1, double step, List<int> cut, double tolerance, double slack, List<AnalyticInterval> output, int depth, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var p0 = candidate.At(t0); var p1 = candidate.At(t1); var pm = candidate.At((t0 + t1) / 2);
        // A parabola piece stays within the larger end distance plus twice its sag about the middle.
        double sagX = (p0.X + p1.X) / 2 - pm.X, sagY = (p0.Y + p1.Y) / 2 - pm.Y, sagZ = Math.Abs((p0.Z + p1.Z) / 2 - pm.Z);
        double lateral = Math.Max(Math.Sqrt((p0.X - pm.X) * (p0.X - pm.X) + (p0.Y - pm.Y) * (p0.Y - pm.Y)), Math.Sqrt((p1.X - pm.X) * (p1.X - pm.X) + (p1.Y - pm.Y) * (p1.Y - pm.Y)))
            + 2 * Math.Sqrt(sagX * sagX + sagY * sagY);
        lateral = lateral * (1 + 1e-12) + slack;
        double zLow = Math.Min(pm.Z, Math.Min(p0.Z, p1.Z)) - 2 * sagZ, zHigh = Math.Max(pm.Z, Math.Max(p0.Z, p1.Z)) + 2 * sagZ;
        var result = Cut(candidate, pm, lateral, zLow, zHigh, cut, tolerance);
        if (result == VisibilityClass.Unresolved && t1 - t0 > step && depth < 60)
        {
            double tm = (t0 + t1) / 2;
            Certify(candidate, t0, tm, step, cut, tolerance, slack, output, depth + 1, token);
            Certify(candidate, tm, t1, step, cut, tolerance, slack, output, depth + 1, token);
            return;
        }
        if (result == VisibilityClass.Unresolved) result = Cut(candidate, pm, slack, pm.Z, pm.Z, cut, tolerance);
        output.Add(new(t0, t1, result));
    }

    private VisibilityClass Cut(in AnalyticCandidate candidate, Vertex p, double lateral, double zLow, double zHigh, List<int> cut, double tolerance)
    {
        var result = VisibilityClass.Visible;
        foreach (int id in cut)
        {
            bool own = id == candidate.Solid;
            var k = ((AnalyticCsg)_solids[id]).Evaluate(p, lateral, zLow, zHigh, tolerance, own ? candidate.OnSurface : 0, own && candidate.Tangent);
            if (k == VisibilityClass.Hidden) return VisibilityClass.Hidden;
            if (k == VisibilityClass.Unresolved) result = VisibilityClass.Unresolved;
        }
        return result;
    }

    /// <summary>The union of two interval lists over [0, 1]: Hidden if either is, else Unresolved if either is.</summary>
    private static List<AnalyticInterval> Unite(List<AnalyticInterval> first, List<AnalyticInterval> second)
    {
        static VisibilityClass Both(VisibilityClass x, VisibilityClass y) =>
            x == VisibilityClass.Hidden || y == VisibilityClass.Hidden ? VisibilityClass.Hidden : x == VisibilityClass.Unresolved || y == VisibilityClass.Unresolved ? VisibilityClass.Unresolved : VisibilityClass.Visible;
        var united = new List<AnalyticInterval>(first.Count + second.Count); int i = 0, j = 0; double at = 0;
        while (i < first.Count && j < second.Count)
        {
            double end = Math.Min(first[i].End, second[j].End);
            if (end > at) united.Add(new(at, end, Both(first[i].Class, second[j].Class)));
            at = Math.Max(at, end);
            if (first[i].End <= end) i++;
            if (j < second.Count && second[j].End <= end) j++;
        }
        return united;
    }

    private static Vertex Sub(Vertex a, Vertex b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
}
