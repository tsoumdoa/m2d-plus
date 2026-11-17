namespace M2DPlus.Core.Visibility;

/// <summary>A closed interval that encloses a quantity over a region.</summary>
public readonly record struct Enclosure(double Lo, double Hi)
{
    public static Enclosure Empty => new(double.PositiveInfinity, double.NegativeInfinity);
    public static Enclosure Zero => new(0, 0);
    public Enclosure With(double v) => new(Math.Min(Lo, v), Math.Max(Hi, v));
    public Enclosure Join(Enclosure b) => new(Math.Min(Lo, b.Lo), Math.Max(Hi, b.Hi));
    public static Enclosure operator +(Enclosure a, Enclosure b) => new(a.Lo + b.Lo, a.Hi + b.Hi);
    public static Enclosure operator -(Enclosure a) => new(-a.Hi, -a.Lo);
    public static Enclosure operator -(Enclosure a, Enclosure b) => a + -b;
    public static Enclosure operator *(Enclosure a, Enclosure b)
    {
        double p = a.Lo * b.Lo, q = a.Lo * b.Hi, r = a.Hi * b.Lo, s = a.Hi * b.Hi;
        return new(Math.Min(Math.Min(p, q), Math.Min(r, s)), Math.Max(Math.Max(p, q), Math.Max(r, s)));
    }
    public static Enclosure operator *(Enclosure a, double k) => k >= 0 ? new(a.Lo * k, a.Hi * k) : new(a.Hi * k, a.Lo * k);
    public Enclosure Square() => Lo >= 0 ? new(Lo * Lo, Hi * Hi) : Hi <= 0 ? new(Hi * Hi, Lo * Lo) : new(0, Math.Max(Lo * Lo, Hi * Hi));
    public bool HasZero => Lo <= 0 && Hi >= 0;
    /// <summary>Largest magnitude in the interval.</summary>
    public double Magnitude => Math.Max(Math.Abs(Lo), Math.Abs(Hi));
    /// <summary>Widened by a relative and an absolute amount: a guard for the rounding of the arithmetic that made it.</summary>
    public Enclosure Padded(double scale) { double pad = 1e-12 * (Magnitude + scale); return new(Lo - pad, Hi + pad); }
}

/// <summary>
/// Certificates for one surface, taken from its control net (no sampling). The surface is given as piecewise Bezier
/// patches in any rigid frame; every result holds for every point of the region asked about.
///   <see cref="Deviation"/>  how far the surface over a parameter triangle can be from the flat triangle through
///                            its corners;
///   <see cref="Graph"/>      a parameter box projects one-to-one along a view direction, so a view ray from a point
///                            of it never meets it again;
///   <see cref="Fold"/>       a view ray that touches a parameter box meets it only at the touching point.
/// Patches are cut into cells; each keeps an enclosure of S_u, S_v, S_uu, S_uv and S_vv. For a rational surface
/// S = N / w these come from the quotient rule on enclosures of N, w and their derivatives about a local origin.
/// Instances are immutable after <see cref="Create"/> and may be queried from several threads.
/// </summary>
public sealed class SurfaceCertificate
{
    private sealed class Cell
    {
        public readonly Enclosure[] Su = new Enclosure[3], Sv = new Enclosure[3], Suu = new Enclosure[3], Suv = new Enclosure[3], Svv = new Enclosure[3];
        public double Muu, Muv, Mvv;
    }

    private readonly double[] _u, _v, _kinkU, _kinkV;
    private readonly int[] _start, _ku, _kv;
    private readonly List<Cell> _cells = new();
    [ThreadStatic] private static List<Cell>? _scratch;

    public bool Rational { get; }
    public int DegreeU { get; }
    public int DegreeV { get; }
    public int CellCount => _cells.Count;
    public double MinU => _u[0];
    public double MaxU => _u[^1];
    public double MinV => _v[0];
    public double MaxV => _v[^1];

    private SurfaceCertificate(int p, int q, bool rational, double[] u, double[] v, double[] kinkU, double[] kinkV)
    {
        DegreeU = p; DegreeV = q; Rational = rational; _u = u; _v = v; _kinkU = kinkU; _kinkV = kinkV;
        int patches = (u.Length - 1) * (v.Length - 1); _start = new int[patches]; _ku = new int[patches]; _kv = new int[patches];
    }

    /// <summary>
    /// <paramref name="breaksU"/>, <paramref name="breaksV"/>: the parameter values between Bezier patches, ends
    /// included. <paramref name="homogeneous"/>: control points (w x, w y, w z, w), (patches U * degree U + 1) by
    /// (patches V * degree V + 1), V index fastest; neighbouring patches share a row. <paramref name="kinksU"/>,
    /// <paramref name="kinksV"/>: breaks where the surface may have a crease (no second-derivative argument is made
    /// across them). <paramref name="cellSize"/>: patches are halved until their control net spans at most this.
    /// </summary>
    public static SurfaceCertificate Create(int degreeU, int degreeV, double[] breaksU, double[] breaksV, double[] homogeneous, bool rational,
        double[] kinksU, double[] kinksV, double cellSize, int maxHalvings = 5)
    {
        int p = degreeU, q = degreeV, nu = breaksU.Length - 1, nv = breaksV.Length - 1, countU = nu * p + 1, countV = nv * q + 1;
        if (p < 1 || q < 1 || nu < 1 || nv < 1) throw new ArgumentException("A surface needs positive degrees and at least one patch.");
        if (homogeneous.Length != countU * countV * 4) throw new ArgumentException("Control point count does not match the patches.", nameof(homogeneous));
        if (!(cellSize > 0)) throw new ArgumentOutOfRangeException(nameof(cellSize));
        for (int i = 0; i < nu; i++) if (!(breaksU[i + 1] > breaksU[i])) throw new ArgumentException("Breaks must increase.", nameof(breaksU));
        for (int i = 0; i < nv; i++) if (!(breaksV[i + 1] > breaksV[i])) throw new ArgumentException("Breaks must increase.", nameof(breaksV));
        for (int i = 3; i < homogeneous.Length; i += 4) if (!(homogeneous[i] > 0) || !double.IsFinite(homogeneous[i])) throw new ArgumentException("Weights must be positive.", nameof(homogeneous));
        var cert = new SurfaceCertificate(p, q, rational, (double[])breaksU.Clone(), (double[])breaksV.Clone(), (double[])kinksU.Clone(), (double[])kinksV.Clone());
        // Patches are independent: cut and bounded in parallel, then listed in order.
        var cells = new Cell[nu * nv][];
        Parallel.For(0, nu * nv, patch =>
        {
            int a = patch / nv, b = patch % nv; var h = new double[(p + 1) * (q + 1) * 4];
            for (int i = 0; i <= p; i++)
                for (int j = 0; j <= q; j++)
                    Array.Copy(homogeneous, ((a * p + i) * countV + b * q + j) * 4, h, (i * (q + 1) + j) * 4, 4);
            double lenU = 0, lenV = 0;
            for (int i = 0; i <= p; i++)
                for (int j = 0; j <= q; j++)
                {
                    if (i < p) lenU = Math.Max(lenU, Distance(h, i * (q + 1) + j, (i + 1) * (q + 1) + j) * p);
                    if (j < q) lenV = Math.Max(lenV, Distance(h, i * (q + 1) + j, i * (q + 1) + j + 1) * q);
                }
            int ku = 1 << Halvings(lenU, cellSize, maxHalvings), kv = 1 << Halvings(lenV, cellSize, maxHalvings);
            cert._ku[patch] = ku; cert._kv[patch] = kv; var made = cells[patch] = new Cell[ku * kv];
            var strips = new List<double[]> { h };
            for (int n = 1; n < ku; n *= 2) strips = Halve(strips, p, q, true);
            double spanU = (breaksU[a + 1] - breaksU[a]) / ku, spanV = (breaksV[b + 1] - breaksV[b]) / kv;
            for (int iu = 0; iu < ku; iu++)
            {
                var row = new List<double[]> { strips[iu] };
                for (int n = 1; n < kv; n *= 2) row = Halve(row, p, q, false);
                for (int iv = 0; iv < kv; iv++) made[iu * kv + iv] = MakeCell(row[iv], p, q, rational, spanU, spanV);
            }
        });
        for (int patch = 0; patch < cells.Length; patch++) { cert._start[patch] = cert._cells.Count; cert._cells.AddRange(cells[patch]); }
        return cert;
    }

    private static int Halvings(double length, double cellSize, int max) =>
        length <= cellSize ? 0 : Math.Clamp((int)Math.Ceiling(Math.Log2(length / cellSize)), 0, max);

    private static double Distance(double[] h, int a, int b)
    {
        double x = h[a * 4] / h[a * 4 + 3] - h[b * 4] / h[b * 4 + 3], y = h[a * 4 + 1] / h[a * 4 + 3] - h[b * 4 + 1] / h[b * 4 + 3], z = h[a * 4 + 2] / h[a * 4 + 3] - h[b * 4 + 2] / h[b * 4 + 3];
        return Math.Sqrt(x * x + y * y + z * z);
    }

    private static List<double[]> Halve(List<double[]> patches, int p, int q, bool alongU)
    {
        var result = new List<double[]>(patches.Count * 2);
        foreach (var h in patches)
        {   // de Casteljau at the middle, line by line
            var l = new double[h.Length]; var r = new double[h.Length]; int n = alongU ? p : q, m = alongU ? q : p; var t = new double[(n + 1) * 4];
            for (int line = 0; line <= m; line++)
            {
                int At(int k) => (alongU ? k * (q + 1) + line : line * (q + 1) + k) * 4;
                for (int k = 0; k <= n; k++) Array.Copy(h, At(k), t, k * 4, 4);
                for (int level = 0; level <= n; level++)
                {
                    Array.Copy(t, 0, l, At(level), 4); Array.Copy(t, (n - level) * 4, r, At(n - level), 4);
                    for (int k = 0; k < n - level; k++) for (int c = 0; c < 4; c++) t[k * 4 + c] = .5 * (t[k * 4 + c] + t[(k + 1) * 4 + c]);
                }
            }
            result.Add(l); result.Add(r);
        }
        return result;
    }

    private static Cell MakeCell(double[] h, int p, int q, bool rational, double spanU, double spanV)
    {
        var cell = new Cell(); int n = q + 1; double su = p / spanU, sv = q / spanV;
        // About a local origin c, so that S - c stays small and the products below do not cancel large numbers.
        double cx = h[0] / h[3], cy = h[1] / h[3], cz = h[2] / h[3];
        double H(int i, int j, int c) { int o = (i * n + j) * 4; return c == 3 ? h[o + 3] : h[o + c] - (c == 0 ? cx : c == 1 ? cy : cz) * h[o + 3]; }
        Enclosure[] Bound(int iMax, int jMax, double k, (int Di, int Dj, double W)[] stencil)
        {
            var box = new[] { Enclosure.Zero, Enclosure.Zero, Enclosure.Zero, Enclosure.Zero }; bool any = false;
            for (int i = 0; i <= iMax; i++)
                for (int j = 0; j <= jMax; j++)
                    for (int c = 0; c < 4; c++)
                    {
                        double value = 0; foreach (var (di, dj, w) in stencil) value += H(i + di, j + dj, c) * w;
                        box[c] = any ? box[c].With(value * k) : new(value * k, value * k);
                        if (c == 3) any = true;
                    }
            return box;
        }
        var b0 = Bound(p, q, 1, [(0, 0, 1)]);
        var bu = Bound(p - 1, q, su, [(1, 0, 1), (0, 0, -1)]);
        var bv = Bound(p, q - 1, sv, [(0, 1, 1), (0, 0, -1)]);
        var buu = Bound(p - 2, q, su * su * (p - 1) / p, [(2, 0, 1), (1, 0, -2), (0, 0, 1)]);
        var buv = Bound(p - 1, q - 1, su * sv, [(1, 1, 1), (1, 0, -1), (0, 1, -1), (0, 0, 1)]);
        var bvv = Bound(p, q - 2, sv * sv * (q - 1) / q, [(0, 2, 1), (0, 1, -2), (0, 0, 1)]);
        if (!rational)
        {   // w = 1: the derivative control vectors enclose the derivatives directly
            for (int c = 0; c < 3; c++) { cell.Su[c] = bu[c]; cell.Sv[c] = bv[c]; cell.Suu[c] = buu[c]; cell.Suv[c] = buv[c]; cell.Svv[c] = bvv[c]; }
        }
        else
        {
            var inverse = new Enclosure(1 / b0[3].Hi, 1 / b0[3].Lo);
            for (int c = 0; c < 3; c++)
            {   // S - c lies in the hull of the Euclidean control points
                var s0 = Enclosure.Empty;
                for (int i = 0; i <= p; i++) for (int j = 0; j <= q; j++) s0 = s0.With(H(i, j, c) / H(i, j, 3));
                cell.Su[c] = (bu[c] - s0 * bu[3]) * inverse; cell.Sv[c] = (bv[c] - s0 * bv[3]) * inverse;
                cell.Suu[c] = (buu[c] - cell.Su[c] * bu[3] * 2 - s0 * buu[3]) * inverse;
                cell.Svv[c] = (bvv[c] - cell.Sv[c] * bv[3] * 2 - s0 * bvv[3]) * inverse;
                cell.Suv[c] = (buv[c] - cell.Su[c] * bv[3] - cell.Sv[c] * bu[3] - s0 * buv[3]) * inverse;
            }
        }
        foreach (var box in new[] { cell.Su, cell.Sv, cell.Suu, cell.Suv, cell.Svv }) for (int c = 0; c < 3; c++) box[c] = box[c].Padded(0);
        cell.Muu = Norm(cell.Suu); cell.Muv = Norm(cell.Suv); cell.Mvv = Norm(cell.Svv);
        return cell;
    }

    private static double Norm(Enclosure[] box) => Math.Sqrt(box[0].Magnitude * box[0].Magnitude + box[1].Magnitude * box[1].Magnitude + box[2].Magnitude * box[2].Magnitude);

    private List<Cell> Query(double u0, double u1, double v0, double v1)
    {
        var result = _scratch ??= new(); result.Clear(); int nv = _v.Length - 1;
        int a0 = Find(_u, u0), a1 = Find(_u, u1), b0 = Find(_v, v0), b1 = Find(_v, v1);
        for (int a = a0; a <= a1; a++)
            for (int b = b0; b <= b1; b++)
            {
                int patch = a * nv + b, ku = _ku[patch], kv = _kv[patch]; double pu0 = _u[a], pu1 = _u[a + 1], pv0 = _v[b], pv1 = _v[b + 1];
                int i0 = Index((u0 - pu0) / (pu1 - pu0) * ku, ku, false), i1 = Index((u1 - pu0) / (pu1 - pu0) * ku, ku, true);
                int j0 = Index((v0 - pv0) / (pv1 - pv0) * kv, kv, false), j1 = Index((v1 - pv0) / (pv1 - pv0) * kv, kv, true);
                for (int i = i0; i <= i1; i++) for (int j = j0; j <= j1; j++) result.Add(_cells[_start[patch] + i * kv + j]);
            }
        return result;
    }
    // A value that rounds onto a cell boundary takes the cell on both sides (one step outwards).
    private static int Index(double x, int count, bool upper)
    {
        double padded = upper ? x + 1e-9 : x - 1e-9; return Math.Clamp((int)Math.Floor(padded), 0, count - 1);
    }
    private static int Find(double[] breaks, double x)
    {
        int lo = 0, hi = breaks.Length - 2;
        while (lo < hi) { int mid = (lo + hi + 1) / 2; if (breaks[mid] <= x) lo = mid; else hi = mid - 1; }
        return lo;
    }

    /// <summary>A crease lies strictly inside the parameter box.</summary>
    public bool CrossesKink(double u0, double u1, double v0, double v1)
    {
        foreach (double k in _kinkU) if (k > u0 && k < u1) return true;
        foreach (double k in _kinkV) if (k > v0 && k < v1) return true;
        return false;
    }

    /// <summary>
    /// A bound on |S(x) - L(x)| for x in the parameter triangle, L the flat triangle through the surface points at
    /// its corners: a sixth of the largest edge value of (M_uu + M_uv) du² + (M_vv + M_uv) dv², with M bounds on the
    /// second derivatives over the triangle's box. Infinite when the triangle spans a crease.
    /// </summary>
    public double Deviation(double ua, double va, double ub, double vb, double uc, double vc)
    {
        double u0 = Math.Min(ua, Math.Min(ub, uc)), u1 = Math.Max(ua, Math.Max(ub, uc)), v0 = Math.Min(va, Math.Min(vb, vc)), v1 = Math.Max(va, Math.Max(vb, vc));
        if (!(u0 >= _u[0] - 1e-9 * (_u[^1] - _u[0]) && u1 <= _u[^1] + 1e-9 * (_u[^1] - _u[0]) && v0 >= _v[0] - 1e-9 * (_v[^1] - _v[0]) && v1 <= _v[^1] + 1e-9 * (_v[^1] - _v[0]))) return double.PositiveInfinity;
        if (CrossesKink(u0, u1, v0, v1)) return double.PositiveInfinity;
        double muu = 0, muv = 0, mvv = 0;
        foreach (var c in Query(u0, u1, v0, v1)) { muu = Math.Max(muu, c.Muu); muv = Math.Max(muv, c.Muv); mvv = Math.Max(mvv, c.Mvv); }
        double Edge(double x, double y) => (muu + muv) * x * x + (mvv + muv) * y * y;
        return Math.Max(Edge(ub - ua, vb - va), Math.Max(Edge(uc - ub, vc - vb), Edge(ua - uc, va - vc))) / 6 * (1 + 1e-12);
    }

    private static Enclosure Dot(Enclosure[] box, Vertex axis) => box[0] * axis.X + box[1] * axis.Y + box[2] * axis.Z;

    /// <summary>The arc of directions of a 2D box that does not hold the origin: middle angle and half width.</summary>
    private static bool Arc(Enclosure x, Enclosure y, out double mid, out double half)
    {
        mid = half = 0; if (x.HasZero && y.HasZero) return false;
        double reference = Math.Atan2(y.Lo, x.Lo), lo = 0, hi = 0;
        foreach (var (px, py) in new[] { (x.Lo, y.Hi), (x.Hi, y.Lo), (x.Hi, y.Hi) })
        {
            double d = Wrap(Math.Atan2(py, px) - reference); lo = Math.Min(lo, d); hi = Math.Max(hi, d);
        }
        if (hi - lo >= Math.PI - 1e-6) return false;
        mid = reference + (lo + hi) / 2; half = (hi - lo) / 2 + 1e-9; return true;
    }
    private static double Wrap(double a) { while (a > Math.PI) a -= 2 * Math.PI; while (a <= -Math.PI) a += 2 * Math.PI; return a; }

    /// <summary>
    /// True when the surface over the parameter box projects one-to-one onto the plane of the unit vectors
    /// <paramref name="x"/> and <paramref name="y"/> (the drawing axes, in the surface's frame). Over the box the
    /// projected S_u stay in one cone and the projected S_v in another, and every cross product of one with the
    /// other has the same sign. For two points of the box, go along u and then along v: the difference of their
    /// projections is a positive combination of a vector of the first cone and one of the second (with the signs of
    /// the two steps), which cannot vanish.
    /// </summary>
    public bool Graph(double u0, double u1, double v0, double v1, Vertex x, Vertex y)
    {
        var cells = Query(u0, u1, v0, v1); if (cells.Count == 0) return false;
        double um = 0, ulo = 0, uhi = 0, vm = 0, vlo = 0, vhi = 0; bool first = true;
        foreach (var c in cells)
        {
            if (!Arc(Dot(c.Su, x), Dot(c.Su, y), out double cu, out double hu) || !Arc(Dot(c.Sv, x), Dot(c.Sv, y), out double cv, out double hv)) return false;
            if (first) { um = cu; ulo = -hu; uhi = hu; vm = cv; vlo = -hv; vhi = hv; first = false; continue; }
            double du = Wrap(cu - um), dv = Wrap(cv - vm);
            ulo = Math.Min(ulo, du - hu); uhi = Math.Max(uhi, du + hu); vlo = Math.Min(vlo, dv - hv); vhi = Math.Max(vhi, dv + hv);
        }
        if (uhi - ulo >= Math.PI || vhi - vlo >= Math.PI) return false;
        double shift = Wrap(vm - um); vlo += shift; vhi += shift;                       // the V arc from the U reference
        foreach (double turn in new[] { 0, 2 * Math.PI, -2 * Math.PI })
        {
            double a = vlo + turn, b = vhi + turn;
            if (a - uhi > 1e-9 && b - ulo < Math.PI - 1e-9) return true;                // cross(U, V) > 0 everywhere
            if (b - ulo < -1e-9 && a - uhi > -Math.PI + 1e-9) return true;              // cross(U, V) < 0 everywhere
        }
        return false;
    }

    /// <summary>
    /// True when a ray along <paramref name="z"/> that touches the surface over the parameter box, at a point where
    /// the surface normal's direction in the drawing is m = x cos t + y sin t for some t in
    /// [<paramref name="angleLo"/>, <paramref name="angleHi"/>], meets the box nowhere else.
    /// With a = z × m, g = (S - p) · a is the lateral position in the drawing and h = (S - p) · m the height over the
    /// plane through the ray. The ray is where g = 0 and h = 0. One of g_u, g_v keeps its sign over the box, so each
    /// curve g = const is one arc; along it (velocity (-g_v, g_u)) the second derivative of h is
    ///   h_uu g_v² - 2 h_uv g_u g_v + h_vv g_u² + h_u (g_v g_uv - g_u g_vv) + h_v (g_u g_uv - g_v g_uu),
    /// which is enclosed away from zero. So h is strictly convex along the arc through the touching point, where it
    /// and its derivative vanish: it has no other zero. A box that spans a crease is refused.
    /// </summary>
    public bool Fold(double u0, double u1, double v0, double v1, Vertex x, Vertex y, double angleLo, double angleHi)
    {
        if (!(angleHi >= angleLo) || angleHi - angleLo >= Math.PI / 2 || CrossesKink(u0, u1, v0, v1)) return false;
        var cells = Query(u0, u1, v0, v1); if (cells.Count == 0) return false;
        Enclosure cos = Trig(angleLo, angleHi, 0), sin = Trig(angleLo, angleHi, -Math.PI / 2);
        Enclosure gu = Enclosure.Empty, gv = Enclosure.Empty, hu = Enclosure.Empty, hv = Enclosure.Empty, guu = Enclosure.Empty, guv = Enclosure.Empty, gvv = Enclosure.Empty, huu = Enclosure.Empty, huv = Enclosure.Empty, hvv = Enclosure.Empty;
        foreach (var c in cells)
        {
            void Add(Enclosure[] box, ref Enclosure g, ref Enclosure h)
            {
                Enclosure bx = Dot(box, x), by = Dot(box, y);
                g = g.Join(by * cos - bx * sin); h = h.Join(bx * cos + by * sin);       // a = -x sin t + y cos t
            }
            Add(c.Su, ref gu, ref hu); Add(c.Sv, ref gv, ref hv); Add(c.Suu, ref guu, ref huu); Add(c.Suv, ref guv, ref huv); Add(c.Svv, ref gvv, ref hvv);
        }
        if (gu.HasZero && gv.HasZero) return false;
        Enclosure t1 = -gv, t2 = gu;
        var second = huu * t1.Square() + huv * (t1 * t2) * 2 + hvv * t2.Square() + hu * (t1 * -guv + t2 * -gvv) + hv * (t1 * guu + t2 * guv);
        return !second.Padded(0).HasZero;
    }

    /// <summary>Range of cos(t + shift) for t in [lo, hi].</summary>
    private static Enclosure Trig(double lo, double hi, double shift)
    {
        double a = lo + shift, b = hi + shift; var range = new Enclosure(Math.Cos(a), Math.Cos(a)).With(Math.Cos(b));
        for (double k = Math.Ceiling(a / Math.PI); k * Math.PI <= b; k++) range = range.With(Math.Cos(k * Math.PI));
        return new(Math.Max(-1, range.Lo - 1e-15), Math.Min(1, range.Hi + 1e-15));
    }
}
