namespace M2DPlus.Core.Visibility;

/// <summary>One bounding surface of an <see cref="AnalyticCsg"/> and the side of it the solid keeps.</summary>
/// <param name="Cylinder">true: an infinite circular cylinder about the line Origin + s Direction; false: the plane
/// through Origin with normal Direction.</param>
/// <param name="KeepInside">Cylinder: the solid lies inside it (a tube's outer surface) or outside it (a bore, a
/// cutting cylinder). Plane: true keeps the side the normal points away from.</param>
/// <param name="Slack">How far the real face may be from this surface (a certified bound from recognition).</param>
public readonly record struct CsgPrimitive(bool Cylinder, Vertex Origin, Vertex Direction, double Radius, bool KeepInside, double Slack = 0);

/// <summary>
/// A solid that is the intersection of kept sides of planes and circular cylinders: a tube whose ends are cut by
/// planes and by other cylinders (coped pipe ends), a bolt-free mitred member, and so on. Larger Z is nearer.
///
/// It answers a stronger question than the other solids, because its change points along a candidate are not all
/// closed-form (two cylinders meet in a quartic curve): for a point p, a sideways radius r and a depth range, is the
/// answer the same for every ray that starts within r of p across the view and within that depth range? Each surface
/// is a distance function with Lipschitz constant one, so moving a ray sideways by r moves it by at most r against
/// every surface: the stretch of the ray surely inside a surface is the stretch inside it shrunk by r (and by the
/// surface's slack and the rounding bound), and the stretch possibly inside is the one grown by the same amount.
/// Intersecting the sure stretches of all surfaces gives cover that every such ray has; intersecting the possible
/// stretches gives all the cover any of them can have. The depth rule of <see cref="AnalyticSolid"/> is then applied
/// to both: Hidden needs sure cover, Visible needs no possible cover, anything else is Unresolved.
/// <see cref="AnalyticOccluders"/> bisects a candidate until every piece is decided this way.
///
/// A candidate of the solid itself lies on one or two of its surfaces (bits of OnSurface, by surface index). On those
/// the ray's start is a root by construction: against a plane the ray is on one side for all of its length, against
/// a cylinder it is inside between its start and the second root, and a ray that only touches (an outline) is
/// outside for all of its length.
/// </summary>
public sealed class AnalyticCsg : AnalyticSolid
{
    private readonly CsgPrimitive[] _primitives; private readonly Vertex _centre; private readonly double _reach;
    public IReadOnlyList<CsgPrimitive> Primitives => _primitives;

    /// <param name="min">Bounds of the solid in view coordinates.</param>
    public AnalyticCsg(IReadOnlyList<CsgPrimitive> primitives, Vertex min, Vertex max)
    {
        if (primitives.Count is 0 or > 30) throw new ArgumentException("Between 1 and 30 surfaces");
        _primitives = new CsgPrimitive[primitives.Count];
        for (int i = 0; i < primitives.Count; i++)
        {
            var p = primitives[i];
            if (!double.IsFinite(Size(p.Origin) + p.Radius + p.Slack) || !(p.Slack >= 0) || p.Cylinder && !(p.Radius > 0)) throw new ArgumentException("Invalid surface");
            _primitives[i] = p with { Direction = Unit(p.Direction) };
        }
        if (!(min.X <= max.X && min.Y <= max.Y && min.Z <= max.Z) || !double.IsFinite(Size(min) + Size(max))) throw new ArgumentException("Invalid bounds");
        X0 = min.X; Y0 = min.Y; X1 = max.X; Y1 = max.Y; ZMax = max.Z;
        _centre = new((min.X + max.X) / 2, (min.Y + max.Y) / 2, (min.Z + max.Z) / 2); _reach = Size(Sub(max, min));
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
        // Rays start at depth zc; a ray of the set starts within `spread` of that.
        double zc = (zLow + zHigh) / 2, spread = (zHigh - zLow) / 2; var start = new Vertex(p.X, p.Y, zc);
        // Every stretch is measured from the ray's own start, so only t ≥ 0 matters. Sure and possible cover as
        // sorted lists of [lo, hi] on t ≥ 0; both begin as everything.
        Span<double> sure = stackalloc double[Capacity], maybe = stackalloc double[Capacity], scratch = stackalloc double[Capacity];
        int ns = Whole(sure), nm = Whole(maybe); double error = 0;
        for (int i = 0; i < _primitives.Length; i++)
        {
            var s = _primitives[i]; bool mine = (own & (1 << i)) != 0;
            var w = Sub(start, s.Origin); double delta = 8 * Epsilon * (Size(w) + s.Radius);
            double inLo, inHi, outLo, outHi;     // the ray is surely inside the surface on [inLo, inHi] and possibly inside on [outLo, outHi]
            if (!s.Cylinder)
            {
                double ne = s.Direction.Z, f = Dot(s.Direction, w);    // inside (kept when KeepInside): n·(x - o) ≤ 0
                if (mine)
                {   // The start is on the plane: f is zero by construction.
                    if (Math.Abs(ne) < 1e-9 || ne < 0) { inLo = outLo = 0; inHi = outHi = double.PositiveInfinity; }   // in the plane (closed), or going in
                    else { inLo = outLo = 0; inHi = outHi = 0; }
                }
                else
                {
                    double margin = s.Slack + lateral * Math.Sqrt(Math.Max(0, 1 - ne * ne)) + delta;
                    if (Math.Abs(ne) < 1e-9)
                    {   // The ray runs level with the plane.
                        (inLo, inHi) = f <= -margin ? (0, double.PositiveInfinity) : (1, 0);
                        (outLo, outHi) = f <= margin ? (0, double.PositiveInfinity) : (1, 0);
                    }
                    else
                    {
                        double root = -f / ne, m = margin / Math.Abs(ne) + spread;
                        error = Math.Max(error, delta / Math.Abs(ne));
                        if (ne > 0) { inLo = outLo = 0; inHi = root - m; outHi = root + m; }
                        else { inHi = outHi = double.PositiveInfinity; inLo = root + m; outLo = root - m; }
                    }
                }
            }
            else
            {
                var u = s.Direction; double wu = Dot(w, u), eu = u.Z, a = 1 - eu * eu;
                var across = Mix(w, -wu, u); var along = new Vertex(-eu * u.X, -eu * u.Y, 1 - eu * u.Z);
                if (a < 1e-12)
                {   // The ray runs along the axis: its distance from the axis is one number.
                    double d = Math.Sqrt(Dot(across, across)), margin = mine ? 0 : s.Slack + lateral + 4 * delta;
                    if (mine && tangent) { inLo = outLo = 1; inHi = outHi = 0; }
                    else
                    {
                        // On its own surface the ray is in the surface: closed for a kept inside, and in the material for a bore.
                        (inLo, inHi) = mine ? (s.KeepInside ? (0, double.PositiveInfinity) : (1, 0)) : d <= s.Radius - margin ? (0, double.PositiveInfinity) : (1, 0);
                        (outLo, outHi) = mine ? (inLo, inHi) : d <= s.Radius + margin ? (0, double.PositiveInfinity) : (1, 0);
                    }
                }
                else
                {
                    double b = Dot(across, along), centre = -b / a, errorCentre = 4 * delta / a;
                    if (!(errorCentre < tolerance / 64)) return VisibilityClass.Unresolved;
                    error = Math.Max(error, errorCentre);
                    if (mine)
                    {
                        if (tangent) { inLo = outLo = 1; inHi = outHi = 0; }      // touches, never inside
                        else
                        {   // Roots 0 and 2·centre. Sideways the second root moves by at most 2·lateral/√a.
                            double other = 2 * centre, play = 2 * lateral / Math.Sqrt(a) + 2 * errorCentre;
                            inLo = outLo = 0; inHi = other - play; outHi = other + play;
                        }
                    }
                    else
                    {
                        var q = Mix(across, centre, along); double reach = Math.Sqrt(Dot(q, q)), errorQ = delta * (1 + 4 / Math.Sqrt(a));
                        if (!(errorQ < tolerance / 64)) return VisibilityClass.Unresolved;
                        double margin = s.Slack + lateral + errorQ, small = s.Radius - margin, large = s.Radius + margin;
                        if (small > reach) { double h = Math.Sqrt((small - reach) * (small + reach) / a); inLo = centre - h + spread; inHi = centre + h - spread; } else { inLo = 1; inHi = 0; }
                        if (large > reach) { double h = Math.Sqrt((large - reach) * (large + reach) / a); outLo = centre - h - spread; outHi = centre + h + spread; } else { outLo = 1; outHi = 0; }
                    }
                }
            }
            if (s.KeepInside)
            {
                ns = Intersect(sure, ns, inLo, inHi); nm = Intersect(maybe, nm, outLo, outHi);
            }
            else
            {   // Kept outside: surely kept where not possibly inside, possibly kept where not surely inside.
                ns = Remove(sure, ns, outLo, outHi, scratch); nm = Remove(maybe, nm, inLo, inHi, scratch);
            }
            if (nm == 0) return VisibilityClass.Visible;
            if (ns < 0 || nm < 0) return VisibilityClass.Unresolved;   // more pieces than the lists hold
        }
        if (!(error < tolerance / 64)) return VisibilityClass.Unresolved;
        // The depth rule: cover counts from one tolerance in front of the start.
        for (int k = 0; k < ns; k++)
            if (sure[2 * k + 1] - Math.Max(sure[2 * k], tolerance) - 2 * error >= tolerance / 8) return VisibilityClass.Hidden;
        for (int k = 0; k < nm; k++)
            if (maybe[2 * k + 1] - Math.Max(maybe[2 * k], tolerance) + 2 * error > tolerance / 16) return VisibilityClass.Unresolved;
        return VisibilityClass.Visible;
    }

    private const int Capacity = 64;   // 32 intervals
    private static int Whole(Span<double> list) { list[0] = 0; list[1] = double.PositiveInfinity; return 1; }

    /// <summary>list ∩ [lo, hi] on t ≥ 0. Returns the new count, or -1 on overflow.</summary>
    private static int Intersect(Span<double> list, int count, double lo, double hi)
    {
        if (count < 0) return count;
        int n = 0;
        for (int k = 0; k < count; k++)
        {
            double a = Math.Max(list[2 * k], lo), b = Math.Min(list[2 * k + 1], hi);
            if (b > a) { list[2 * n] = a; list[2 * n + 1] = b; n++; }
        }
        return n;
    }

    /// <summary>list \ (lo, hi). Returns the new count, or -1 on overflow.</summary>
    private static int Remove(Span<double> list, int count, double lo, double hi, Span<double> scratch)
    {
        if (count < 0) return count;
        if (!(hi > lo)) return count;
        int n = 0;
        for (int k = 0; k < count; k++)
        {
            double a = list[2 * k], b = list[2 * k + 1];
            if (hi <= a || lo >= b) { if (2 * n + 2 > Capacity) return -1; scratch[2 * n] = a; scratch[2 * n + 1] = b; n++; continue; }
            if (lo > a) { if (2 * n + 2 > Capacity) return -1; scratch[2 * n] = a; scratch[2 * n + 1] = lo; n++; }
            if (hi < b) { if (2 * n + 2 > Capacity) return -1; scratch[2 * n] = hi; scratch[2 * n + 1] = b; n++; }
        }
        scratch[..(2 * n)].CopyTo(list);
        return n;
    }

    /// <summary>Is the point in the solid, by at least <paramref name="margin"/> (positive) or possibly (negative)?
    /// Used by recognition to compare with the body, never by the visibility predicate.</summary>
    public bool Contains(Vertex x, double margin)
    {
        foreach (var s in _primitives)
        {
            var w = Sub(x, s.Origin); double depth;      // positive inside the surface
            if (s.Cylinder) { double wu = Dot(w, s.Direction); var across = Mix(w, -wu, s.Direction); depth = s.Radius - Math.Sqrt(Dot(across, across)); }
            else depth = -Dot(s.Direction, w);
            if (!s.KeepInside) depth = -depth;
            if (depth < margin) return false;
        }
        return true;
    }
}
