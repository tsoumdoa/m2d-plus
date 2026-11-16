namespace M2DPlus.Core.Visibility;

/// <summary>How a bounding surface takes part in an <see cref="AnalyticCsg"/>.</summary>
public enum CsgRole
{
    /// <summary>The solid lies wholly inside the surface (a tube's outer cylinder, a plane that ends it).</summary>
    Inside,
    /// <summary>The solid lies wholly outside the surface (a bore).</summary>
    Outside,
    /// <summary>The solid lies on either side, depending on the other free surfaces: decided by the solid's cells.</summary>
    Free,
}

/// <summary>One bounding surface of an <see cref="AnalyticCsg"/>.</summary>
/// <param name="Cylinder">true: an infinite circular cylinder about the line Origin + s Direction, whose inside is
/// within Radius of the axis; false: the plane through Origin with normal Direction, whose inside is the side the
/// normal points away from.</param>
/// <param name="Slack">How far the real face may be from this surface (a certified bound from recognition).</param>
public readonly record struct CsgPrimitive(bool Cylinder, Vertex Origin, Vertex Direction, double Radius, CsgRole Role, double Slack = 0);

/// <summary>
/// A solid bounded by planes and circular cylinders: a tube whose ends are cut by planes and by other cylinders (a
/// pipe coped to fit a node). A point is in the solid when it is inside every <see cref="CsgRole.Inside"/> surface,
/// outside every <see cref="CsgRole.Outside"/> surface, and its sides of the <see cref="CsgRole.Free"/> surfaces (one
/// bit each, set when inside) form one of the given cells. A pipe coped against two mitred neighbours is outside
/// "cylinder A and behind the mitre plane" and outside "cylinder B and in front of it": that needs cells, not a plain
/// intersection. A combination of sides that is in neither list of cells is unknown.
///
/// It answers a stronger question than the other solids, because its change points along a candidate are not all
/// closed-form (two cylinders meet in a quartic curve): for a point p, a sideways radius r and a depth range, is the
/// answer the same for every ray that starts within r of p across the view and within that depth range? Each surface
/// is a distance function with Lipschitz constant one, so moving a ray sideways by r moves it by at most r against
/// every surface: the stretch of the ray surely inside a surface is the stretch inside it shrunk by r (and by the
/// surface's slack and the rounding bound), and the stretch possibly inside is the one grown by the same amount.
/// Between those the side is unknown, and a stretch of the ray is surely in the solid only when every way of
/// settling the unknown sides puts it there. The depth rule of <see cref="AnalyticSolid"/> is applied to the sure
/// cover and to the possible cover: Hidden needs sure cover, Visible needs no possible cover, anything else is
/// Unresolved. <see cref="AnalyticOccluders"/> bisects a candidate until every piece is decided this way.
///
/// A candidate of the solid itself lies on one or two of its surfaces (bits of OnSurface, by surface index). On those
/// the ray's start is a root by construction: against a plane the ray is on one side for all of its length, against
/// a cylinder it is inside between its start and the second root, and a ray that only touches (an outline) is
/// outside for all of its length.
/// </summary>
public sealed class AnalyticCsg : AnalyticSolid
{
    private readonly CsgPrimitive[] _primitives; private readonly int[] _bit; private readonly HashSet<int> _in, _out;
    private readonly Vertex _centre; private readonly int _free;
    public IReadOnlyList<CsgPrimitive> Primitives => _primitives;

    /// <param name="inside">Cells of the free surfaces that are in the solid; null when there is no free surface.
    /// Bit k of a cell is the k-th free surface in list order, set when the point is inside it.</param>
    /// <param name="outside">Cells that are not in the solid. Every other cell is unknown.</param>
    public AnalyticCsg(IReadOnlyList<CsgPrimitive> primitives, Vertex min, Vertex max, IEnumerable<int>? inside = null, IEnumerable<int>? outside = null)
    {
        if (primitives.Count is 0 or > 30) throw new ArgumentException("Between 1 and 30 surfaces");
        _primitives = new CsgPrimitive[primitives.Count]; _bit = new int[primitives.Count];
        for (int i = 0; i < primitives.Count; i++)
        {
            var p = primitives[i];
            if (!double.IsFinite(Size(p.Origin) + p.Radius + p.Slack) || !(p.Slack >= 0) || p.Cylinder && !(p.Radius > 0)) throw new ArgumentException("Invalid surface");
            _primitives[i] = p with { Direction = Unit(p.Direction) };
            _bit[i] = p.Role == CsgRole.Free ? _free++ : -1;
        }
        if (_free > 26) throw new ArgumentException("More than 26 free surfaces");
        _in = inside is null ? [] : [.. inside]; _out = outside is null ? [] : [.. outside];
        if (_free == 0) { _in.Clear(); _in.Add(0); _out.Clear(); }
        if (_in.Overlaps(_out) || _in.Concat(_out).Any(c => c < 0 || c >= 1 << _free)) throw new ArgumentException("Invalid cells");
        if (!(min.X <= max.X && min.Y <= max.Y && min.Z <= max.Z) || !double.IsFinite(Size(min) + Size(max))) throw new ArgumentException("Invalid bounds");
        X0 = min.X; Y0 = min.Y; X1 = max.X; Y1 = max.Y; ZMax = max.Z;
        _centre = new((min.X + max.X) / 2, (min.Y + max.Y) / 2, (min.Z + max.Z) / 2);
    }

    public override VisibilityClass Occlusion(Vertex p, double tolerance, int own = 0, bool tangent = false) => Evaluate(p, 0, p.Z, p.Z, tolerance, own, tangent);
    internal override void Events(Vertex a, Vertex d, double tolerance, List<double> ts) { }

    /// <summary>
    /// The class shared by every ray that starts within <paramref name="lateral"/> of p across the view and at a depth
    /// in [zLow, zHigh], or Unresolved when they may differ or the arithmetic cannot tell.
    /// own: surfaces the start lies on (bit i for surface i); tangent: the rays only touch those surfaces.
    /// </summary>
    public VisibilityClass Evaluate(Vertex p, double lateral, double zLow, double zHigh, double tolerance, int own = 0, bool tangent = false)
    {
        if (!(lateral >= 0) || !(zLow <= zHigh) || !double.IsFinite(lateral + zLow + zHigh)) return VisibilityClass.Unresolved;
        if (!Resolvable(p, _centre, tolerance)) return VisibilityClass.Unresolved;
        // Rays start at depth zc; a ray of the set starts within `spread` of that. Every stretch is measured from the
        // ray's own start, so only t ≥ 0 matters.
        int n = _primitives.Length; double zc = (zLow + zHigh) / 2, spread = (zHigh - zLow) / 2; var start = new Vertex(p.X, p.Y, zc);
        // For each surface: the ray is surely inside it on [inLo, inHi] and possibly inside on [outLo, outHi].
        Span<double> inLo = stackalloc double[n], inHi = stackalloc double[n], outLo = stackalloc double[n], outHi = stackalloc double[n];
        Span<double> cuts = stackalloc double[4 * n + 2]; int nc = 0; cuts[nc++] = 0; double error = 0;
        int border = 0;      // free surfaces the rays run in (the start lies on them and the view is along them)
        for (int i = 0; i < n; i++)
        {
            var s = _primitives[i]; bool mine = (own & (1 << i)) != 0;
            var w = Sub(start, s.Origin); double delta = 8 * Epsilon * (Size(w) + s.Radius);
            double il, ih, ol, oh;
            if (!s.Cylinder)
            {
                double ne = s.Direction.Z, f = Dot(s.Direction, w);    // inside: n·(x - o) ≤ 0
                if (mine)
                {   // The start is on the plane: f is zero by construction.
                    if (Math.Abs(ne) < 1e-9)
                    {   // The ray runs in the plane. A surface the solid is wholly inside of is closed; in a free
                        // surface the ray is on the border of two cells, and the solid, being closed, holds it when
                        // either cell is solid.
                        il = ol = 0; ih = oh = double.PositiveInfinity;
                        if (s.Role == CsgRole.Free) border |= 1 << _bit[i];
                    }
                    else if (ne < 0) { il = ol = 0; ih = oh = double.PositiveInfinity; }   // going in
                    else { il = ol = 0; ih = oh = 0; }
                }
                else
                {
                    double margin = s.Slack + lateral * Math.Sqrt(Math.Max(0, 1 - ne * ne)) + delta;
                    if (Math.Abs(ne) < 1e-9)
                    {   // The ray runs level with the plane.
                        (il, ih) = f <= -margin ? (0, double.PositiveInfinity) : (1, 0);
                        (ol, oh) = f <= margin ? (0, double.PositiveInfinity) : (1, 0);
                    }
                    else
                    {
                        double root = -f / ne, m = margin / Math.Abs(ne) + spread;
                        error = Math.Max(error, delta / Math.Abs(ne));
                        if (ne > 0) { il = ol = 0; ih = root - m; oh = root + m; }
                        else { ih = oh = double.PositiveInfinity; il = root + m; ol = root - m; }
                    }
                }
            }
            else
            {
                var u = s.Direction; double wu = Dot(w, u), eu = u.Z, a = 1 - eu * eu;
                var across = Mix(w, -wu, u); var along = new Vertex(-eu * u.X, -eu * u.Y, 1 - eu * u.Z);
                if (a < 1e-12)
                {   // The ray runs along the axis: its distance from the axis is one number.
                    double d = Math.Sqrt(Dot(across, across)), margin = s.Slack + lateral + 4 * delta;
                    if (mine)
                    {   // In its own surface: a ray that only touches is outside; otherwise the surface belongs to the
                        // side the solid is on (closed for a kept inside, in the material for a bore).
                        bool inside = !tangent && s.Role != CsgRole.Outside;
                        (il, ih) = inside ? (0, double.PositiveInfinity) : (1, 0); (ol, oh) = (il, ih);
                        if (!tangent && s.Role == CsgRole.Free) border |= 1 << _bit[i];
                    }
                    else
                    {
                        (il, ih) = d <= s.Radius - margin ? (0, double.PositiveInfinity) : (1, 0);
                        (ol, oh) = d <= s.Radius + margin ? (0, double.PositiveInfinity) : (1, 0);
                    }
                }
                else
                {
                    double b = Dot(across, along), centre = -b / a, errorCentre = 4 * delta / a;
                    if (!(errorCentre < tolerance / 64)) return VisibilityClass.Unresolved;
                    error = Math.Max(error, errorCentre);
                    if (mine)
                    {
                        if (tangent) { il = ol = 1; ih = oh = 0; }      // touches, never inside
                        else
                        {   // Roots 0 and 2·centre. Sideways the second root moves by at most 2·lateral/√a.
                            double other = 2 * centre, play = 2 * lateral / Math.Sqrt(a) + 2 * errorCentre;
                            il = ol = 0; ih = other - play; oh = other + play;
                        }
                    }
                    else
                    {
                        var q = Mix(across, centre, along); double reach = Math.Sqrt(Dot(q, q)), errorQ = delta * (1 + 4 / Math.Sqrt(a));
                        if (!(errorQ < tolerance / 64)) return VisibilityClass.Unresolved;
                        double margin = s.Slack + lateral + errorQ, small = s.Radius - margin, large = s.Radius + margin;
                        if (small > reach) { double h = Math.Sqrt((small - reach) * (small + reach) / a); il = centre - h + spread; ih = centre + h - spread; } else { il = 1; ih = 0; }
                        if (large > reach) { double h = Math.Sqrt((large - reach) * (large + reach) / a); ol = centre - h - spread; oh = centre + h + spread; } else { ol = 1; oh = 0; }
                    }
                }
            }
            if (!(ih > il)) { il = 1; ih = 0; }
            if (!(oh > ol)) { ol = 1; oh = 0; }
            // A surely-inside stretch is always within the possible one.
            if (ih > il && !(ol <= il && oh >= ih)) { ol = Math.Min(ol, il); oh = Math.Max(oh, ih); }
            inLo[i] = il; inHi[i] = ih; outLo[i] = ol; outHi[i] = oh;
            // A surface the whole solid is inside of, and the ray never possibly is: nothing to cover with.
            if (s.Role == CsgRole.Inside && !(oh > Math.Max(ol, 0))) return VisibilityClass.Visible;
            if (ih > il) { if (il > 0) cuts[nc++] = il; if (ih > 0 && double.IsFinite(ih)) cuts[nc++] = ih; }
            if (oh > ol) { if (ol > 0) cuts[nc++] = ol; if (oh > 0 && double.IsFinite(oh)) cuts[nc++] = oh; }
        }
        if (!(error < tolerance / 64)) return VisibilityClass.Unresolved;
        cuts = cuts[..nc]; cuts.Sort();
        // Between consecutive cuts every surface has one state. Sure and possible cover run on as long as the state allows.
        double sureFrom = double.NaN, maybeFrom = double.NaN; bool undecided = false;
        for (int k = 0; k < nc; k++)
        {
            double a = cuts[k], b = k + 1 < nc ? cuts[k + 1] : double.PositiveInfinity;
            if (!(b > a)) continue;
            double at = double.IsFinite(b) ? (a + b) / 2 : a + 1;
            int state = State(at, inLo, inHi, outLo, outHi, border);       // 2: surely in the solid, 1: possibly, 0: surely not
            if (state == 2) { if (double.IsNaN(sureFrom)) sureFrom = a; } else if (!double.IsNaN(sureFrom)) { if (Cover(sureFrom, a, tolerance) - 2 * error >= tolerance / 8) return VisibilityClass.Hidden; sureFrom = double.NaN; }
            if (state != 0) { if (double.IsNaN(maybeFrom)) maybeFrom = a; } else if (!double.IsNaN(maybeFrom)) { if (Cover(maybeFrom, a, tolerance) + 2 * error > tolerance / 16) undecided = true; maybeFrom = double.NaN; }
        }
        if (!double.IsNaN(sureFrom)) return VisibilityClass.Hidden;          // sure cover that never ends
        if (!double.IsNaN(maybeFrom)) undecided = true;
        return undecided ? VisibilityClass.Unresolved : VisibilityClass.Visible;
    }

    /// <summary>The depth rule: cover counts from one tolerance in front of the start.</summary>
    private static double Cover(double from, double to, double tolerance) => to - Math.Max(from, tolerance);

    private int State(double t, ReadOnlySpan<double> inLo, ReadOnlySpan<double> inHi, ReadOnlySpan<double> outLo, ReadOnlySpan<double> outHi, int along)
    {
        int value = 0, unknown = 0; bool possible = true, sure = true;
        for (int i = 0; i < _primitives.Length; i++)
        {
            bool surelyIn = t >= inLo[i] && t <= inHi[i], possiblyIn = t >= outLo[i] && t <= outHi[i];
            switch (_primitives[i].Role)
            {
                case CsgRole.Inside: if (!possiblyIn) return 0; if (!surelyIn) sure = false; break;
                case CsgRole.Outside: if (surelyIn) return 0; if (possiblyIn) sure = false; break;
                default:
                    if ((along & (1 << _bit[i])) != 0) break;       // on the border of this surface: both sides are tried below
                    if (surelyIn) value |= 1 << _bit[i]; else if (possiblyIn) unknown |= 1 << _bit[i];
                    break;
            }
        }
        if (_free != 0)
        {
            // Every way of settling the unknown sides. More than eight unknown at once is not decided.
            if (System.Numerics.BitOperations.PopCount((uint)(unknown | along)) > 8) { sure = false; }
            else
            {
                // Every way of settling the unknown sides must leave the point in the solid; on a border the point is
                // in the (closed) solid when the cell on either side is.
                bool any = false, all = true;
                for (int sub = unknown; ; sub = (sub - 1) & unknown)
                {
                    bool solid = false, maybe = false;
                    for (int side = along; ; side = (side - 1) & along)
                    {
                        int cell = value | sub | side;
                        if (_in.Contains(cell)) solid = true; else if (!_out.Contains(cell)) maybe = true;   // an unknown cell may be either
                        if (side == 0) break;
                    }
                    if (solid || maybe) any = true; if (!solid) all = false;
                    if (sub == 0) break;
                }
                if (!any) possible = false; if (!all) sure = false;
            }
        }
        return !possible ? 0 : sure ? 2 : 1;
    }

    /// <summary>How far inside surface i the point is (negative: outside), as the surface defines its inside.</summary>
    public double Depth(int surface, Vertex x)
    {
        var s = _primitives[surface]; var w = Sub(x, s.Origin);
        if (!s.Cylinder) return -Dot(s.Direction, w);
        double wu = Dot(w, s.Direction); var across = Mix(w, -wu, s.Direction); return s.Radius - Math.Sqrt(Dot(across, across));
    }

    /// <summary>The cell of the free surfaces the point is in; <paramref name="certain"/> is false when the point is
    /// within <paramref name="margin"/> of any surface.</summary>
    public int Cell(Vertex x, double margin, out bool certain, out bool inRequired)
    {
        int cell = 0; certain = true; inRequired = true;
        for (int i = 0; i < _primitives.Length; i++)
        {
            double depth = Depth(i, x); if (Math.Abs(depth) < margin) certain = false;
            switch (_primitives[i].Role)
            {
                case CsgRole.Inside: if (depth < 0) inRequired = false; break;
                case CsgRole.Outside: if (depth > 0) inRequired = false; break;
                default: if (depth > 0) cell |= 1 << _bit[i]; break;
            }
        }
        return cell;
    }

    /// <summary>Is the point in the solid? null when it is within <paramref name="margin"/> of a surface or in an
    /// unknown cell. Used by recognition to compare with the body, never by the visibility predicate.</summary>
    public bool? Contains(Vertex x, double margin)
    {
        int cell = Cell(x, margin, out bool certain, out bool inRequired);
        if (!certain) return null;
        if (!inRequired) return false;
        return _in.Contains(cell) ? true : _out.Contains(cell) ? false : null;
    }
}
