// Mesh-tier spike (hillclimb/mesh-tier). Harness only.
//
// FaceCert: certificates for one Brep face taken from the control net of its surface, cut into Bezier cells.
//   Deviation   a bound on the distance between the surface over a parameter triangle and the flat triangle through its
//               corners, from bounds on the second derivatives (polynomial surfaces).
//   Graph       over a parameter box the projected S_u and S_v stay in two cones with a cross product of one sign:
//               the box projects one-to-one, so a view ray from a point of it never meets it again.
//   Fold        along every curve "same lateral position in the drawing" inside the box, the height over the plane
//               through the ray is strictly convex: a ray that touches the box meets it only there.
using System.Diagnostics;
using M2DPlus.Core;
using M2DPlus.PerfF.CurvedCore;
using M2DPlus.Rhino.Services;
using Rhino;
using Rhino.Geometry;

namespace M2DPlus.PerfF;

internal readonly record struct Iv(double Lo, double Hi)
{
    public static Iv Empty => new(double.PositiveInfinity, double.NegativeInfinity);
    public Iv With(double v) => new(Math.Min(Lo, v), Math.Max(Hi, v));
    public static Iv operator +(Iv a, Iv b) => new(a.Lo + b.Lo, a.Hi + b.Hi);
    public static Iv operator -(Iv a) => new(-a.Hi, -a.Lo);
    public static Iv operator *(Iv a, Iv b)
    {
        double p = a.Lo * b.Lo, q = a.Lo * b.Hi, r = a.Hi * b.Lo, s = a.Hi * b.Hi;
        return new(Math.Min(Math.Min(p, q), Math.Min(r, s)), Math.Max(Math.Max(p, q), Math.Max(r, s)));
    }
    public static Iv operator *(Iv a, double k) => k >= 0 ? new(a.Lo * k, a.Hi * k) : new(a.Hi * k, a.Lo * k);
    public Iv Sqr() => Lo >= 0 ? new(Lo * Lo, Hi * Hi) : Hi <= 0 ? new(Hi * Hi, Lo * Lo) : new(0, Math.Max(Lo * Lo, Hi * Hi));
    public bool HasZero => Lo <= 0 && Hi >= 0;
    public double Mag => Math.Max(Math.Abs(Lo), Math.Abs(Hi));
    public double Mig => HasZero ? 0 : Math.Min(Math.Abs(Lo), Math.Abs(Hi));
}

internal sealed class FaceCert
{
    public double[] KinkU = Array.Empty<double>(), KinkV = Array.Empty<double>();
    public bool Crosses(double u0, double u1, double v0, double v1) { foreach (double k in KinkU) if (k > u0 && k < u1) return true; foreach (double k in KinkV) if (k > v0 && k < v1) return true; return false; }
    public bool Ok, Rational; public string Why = ""; public bool? WholeGraph;
    public int P, Q;
    public double[] U = Array.Empty<double>(), V = Array.Empty<double>();   // patch breakpoints
    private int[] _start = Array.Empty<int>(), _ku = Array.Empty<int>(), _kv = Array.Empty<int>();
    public readonly List<Cell> Cells = new();
    public static readonly Dictionary<int, List<(double[] Signature, FaceCert Cert)>> Cache = new(); public static int Hits;

    public sealed class Cell
    {
        public double U0, U1, V0, V1;
        public Iv[] Su = new Iv[3], Sv = new Iv[3], Suu = new Iv[3], Suv = new Iv[3], Svv = new Iv[3];   // enclosures of the derivatives, view coordinates
        public double Muu, Muv, Mvv;
        public bool ConeOk; public double UMid, UHalf, VMid, VHalf;       // arcs of the projected S_u and S_v directions
    }

    /// <summary>surface: the face's surface. Cells cover [du] x [dv] (the face's parameter box).</summary>
    public static FaceCert Build(Surface surface, Interval du, Interval dv, Func<Point3d, V3> project, double cellSize, double tol)
    {
        var cert = new FaceCert();
        using var trimmed = surface.Trim(du, dv) ?? surface.Duplicate() as Surface;
        using var ns = trimmed?.ToNurbsSurface();
        if (ns is null) { cert.Why = "no NURBS form"; return cert; }
        // The mesh's parameters are the surface's; the certificates need the NURBS form to have the same ones.
        for (int i = 0; i <= 4; i++)
            for (int j = 0; j <= 4; j++)
            {
                double u = du.ParameterAt(.02 + .24 * i), v = dv.ParameterAt(.02 + .24 * j);
                if (!ns.Domain(0).IncludesParameter(u) || !ns.Domain(1).IncludesParameter(v) || ns.PointAt(u, v).DistanceTo(surface.PointAt(u, v)) > tol / 100) { cert.Why = "NURBS form has other parameters (" + surface.GetType().Name + ")"; return cert; }
            }
        cert.Rational = ns.IsRational; cert.P = ns.Degree(0); cert.Q = ns.Degree(1);
        // Instances that differ by a translation share one certificate: everything in it is a derivative or a parameter.
        var hash = new HashCode(); hash.Add(cert.P); hash.Add(cert.Q); hash.Add(ns.Points.CountU); hash.Add(ns.Points.CountV); hash.Add(cellSize);
        hash.Add(du.Min); hash.Add(du.Max); hash.Add(dv.Min); hash.Add(dv.Max);
        var first = project(ns.Points.GetControlPoint(0, 0).Location); var signature = new List<double>();
        for (int i = 0; i < ns.Points.CountU; i++) for (int j = 0; j < ns.Points.CountV; j++)
            { var c = ns.Points.GetControlPoint(i, j); var e = project(c.Location) - first; signature.Add(Math.Round(e.X, 7)); signature.Add(Math.Round(e.Y, 7)); signature.Add(Math.Round(e.Z, 7)); signature.Add(c.Weight); }
        foreach (double k in ns.KnotsU) signature.Add(k); foreach (double k in ns.KnotsV) signature.Add(k);
        foreach (double x in signature) hash.Add(x);
        int key = hash.ToHashCode();
        if (Cache.TryGetValue(key, out var hit)) foreach (var (sig, cached) in hit) if (sig.AsSpan().SequenceEqual(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(signature))) { Hits++; return cached; }
        if (!Cache.TryGetValue(key, out hit)) Cache[key] = hit = new();
        hit.Add((signature.ToArray(), cert));
        foreach (int dir in new[] { 0, 1 })
        {
            var domain = ns.Domain(dir); int degree = ns.Degree(dir);
            var interior = (dir == 0 ? ns.KnotsU.ToArray() : ns.KnotsV.ToArray()).Where(k => k > domain.Min && k < domain.Max).ToArray();
            var values = interior.Distinct().ToArray();
            // A knot of full multiplicity may be a crease inside the face: no second-derivative argument holds across it.
            var kinks = values.Where(k => interior.Count(x => x == k) >= degree).ToArray(); if (dir == 0) cert.KinkU = kinks; else cert.KinkV = kinks;
            foreach (double k in values) { if (dir == 0) ns.KnotsU.InsertKnot(k, degree); else ns.KnotsV.InsertKnot(k, degree); }
            var breaks = new List<double> { domain.Min }; breaks.AddRange(values); breaks.Add(domain.Max);
            if (dir == 0) cert.U = breaks.ToArray(); else cert.V = breaks.ToArray();
        }
        int nu = cert.U.Length - 1, nv = cert.V.Length - 1, p = cert.P, q = cert.Q;
        if (ns.Points.CountU != nu * p + 1 || ns.Points.CountV != nv * q + 1) { cert.Why = "not piecewise Bezier after knot insertion"; return cert; }
        cert._start = new int[nu * nv]; cert._ku = new int[nu * nv]; cert._kv = new int[nu * nv];
        var h = new double[(p + 1) * (q + 1) * 4];
        for (int a = 0; a < nu; a++)
            for (int b = 0; b < nv; b++)
            {
                double lenU = 0, lenV = 0;
                for (int i = 0; i <= p; i++)
                    for (int j = 0; j <= q; j++)
                    {
                        var c = ns.Points.GetControlPoint(a * p + i, b * q + j); var e = project(c.Location); double w = cert.Rational ? c.Weight : 1;
                        if (!(w > 0)) { cert.Why = "weight not positive"; return cert; }
                        int o = (i * (q + 1) + j) * 4; h[o] = e.X * w; h[o + 1] = e.Y * w; h[o + 2] = e.Z * w; h[o + 3] = w;
                    }
                for (int i = 0; i <= p; i++) for (int j = 0; j <= q; j++)
                    {
                        if (i < p) lenU = Math.Max(lenU, Dist(h, i * (q + 1) + j, (i + 1) * (q + 1) + j) * p);
                        if (j < q) lenV = Math.Max(lenV, Dist(h, i * (q + 1) + j, i * (q + 1) + j + 1) * q);
                    }
                int lu = Math.Clamp((int)Math.Ceiling(Math.Log2(Math.Max(lenU, 1e-12) / cellSize)), 0, 5), lv = Math.Clamp((int)Math.Ceiling(Math.Log2(Math.Max(lenV, 1e-12) / cellSize)), 0, 5);
                int ku = 1 << lu, kv = 1 << lv, patch = a * nv + b; cert._start[patch] = cert.Cells.Count; cert._ku[patch] = ku; cert._kv[patch] = kv;
                var strips = new List<double[]> { (double[])h.Clone() };
                for (int n = 1; n < ku; n *= 2) strips = strips.SelectMany(x => { var (l, r) = Split(x, p, q, true); return new[] { l, r }; }).ToList();
                var pieces = new double[ku * kv][];
                for (int iu = 0; iu < ku; iu++)
                {
                    var row = new List<double[]> { strips[iu] };
                    for (int n = 1; n < kv; n *= 2) row = row.SelectMany(x => { var (l, r) = Split(x, p, q, false); return new[] { l, r }; }).ToList();
                    for (int iv = 0; iv < kv; iv++) pieces[iu * kv + iv] = row[iv];
                }
                double u0 = cert.U[a], u1 = cert.U[a + 1], v0 = cert.V[b], v1 = cert.V[b + 1];
                for (int iu = 0; iu < ku; iu++)
                    for (int iv = 0; iv < kv; iv++)
                        cert.Cells.Add(MakeCell(pieces[iu * kv + iv], p, q, cert.Rational, u0 + (u1 - u0) * iu / ku, u0 + (u1 - u0) * (iu + 1) / ku, v0 + (v1 - v0) * iv / kv, v0 + (v1 - v0) * (iv + 1) / kv));
            }
        cert.Ok = true; return cert;
    }

    private static double Dist(double[] h, int a, int b)
    {
        double x = h[a * 4] / h[a * 4 + 3] - h[b * 4] / h[b * 4 + 3], y = h[a * 4 + 1] / h[a * 4 + 3] - h[b * 4 + 1] / h[b * 4 + 3], z = h[a * 4 + 2] / h[a * 4 + 3] - h[b * 4 + 2] / h[b * 4 + 3];
        return Math.Sqrt(x * x + y * y + z * z);
    }

    private static (double[] L, double[] R) Split(double[] h, int p, int q, bool alongU)
    {
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
        return (l, r);
    }

    private static Iv[] BoxOf(List<V3> vectors)
    {
        var box = new[] { Iv.Empty, Iv.Empty, Iv.Empty };
        if (vectors.Count == 0) return new[] { new Iv(0, 0), new Iv(0, 0), new Iv(0, 0) };
        foreach (var v in vectors) { box[0] = box[0].With(v.X); box[1] = box[1].With(v.Y); box[2] = box[2].With(v.Z); }
        return box;
    }
    private static double Norm(Iv[] box) => Math.Sqrt(box[0].Mag * box[0].Mag + box[1].Mag * box[1].Mag + box[2].Mag * box[2].Mag);

    private static Cell MakeCell(double[] h, int p, int q, bool rational, double u0, double u1, double v0, double v1)
    {
        var cell = new Cell { U0 = u0, U1 = u1, V0 = v0, V1 = v1 }; int n = q + 1; double su = p / (u1 - u0), sv = q / (v1 - v0);
        if (!rational)
        {
            V3 E(int i, int j) { int o = (i * n + j) * 4; return new(h[o], h[o + 1], h[o + 2]); }
            var du = new List<V3>(); var dv = new List<V3>(); var duu = new List<V3>(); var duv = new List<V3>(); var dvv = new List<V3>();
            for (int i = 0; i < p; i++) for (int j = 0; j <= q; j++) du.Add((E(i + 1, j) - E(i, j)) * su);
            for (int i = 0; i <= p; i++) for (int j = 0; j < q; j++) dv.Add((E(i, j + 1) - E(i, j)) * sv);
            for (int i = 0; i + 2 <= p; i++) for (int j = 0; j <= q; j++) duu.Add((E(i + 2, j) - E(i + 1, j) * 2 + E(i, j)) * (su * su * (p - 1) / p));
            for (int i = 0; i < p; i++) for (int j = 0; j < q; j++) duv.Add((E(i + 1, j + 1) - E(i + 1, j) - E(i, j + 1) + E(i, j)) * (su * sv));
            for (int i = 0; i <= p; i++) for (int j = 0; j + 2 <= q; j++) dvv.Add((E(i, j + 2) - E(i, j + 1) * 2 + E(i, j)) * (sv * sv * (q - 1) / q));
            cell.Su = BoxOf(du); cell.Sv = BoxOf(dv); cell.Suu = BoxOf(duu); cell.Suv = BoxOf(duv); cell.Svv = BoxOf(dvv);
            cell.Muu = duu.Count == 0 ? 0 : duu.Max(x => x.Length); cell.Muv = duv.Count == 0 ? 0 : duv.Max(x => x.Length); cell.Mvv = dvv.Count == 0 ? 0 : dvv.Max(x => x.Length);
            cell.ConeOk = Arc(du.ToArray(), out cell.UMid, out cell.UHalf) && Arc(dv.ToArray(), out cell.VMid, out cell.VHalf);
            return cell;
        }
        // Rational: S = N / w with N and w polynomial. About a local origin c (so that S stays small), the quotient rule
        // on enclosures of N, w and their derivatives: S_u = (N_u - S w_u) / w, S_uu = (N_uu - 2 S_u w_u - S w_uu) / w, ...
        double cx = h[0] / h[3], cy = h[1] / h[3], cz = h[2] / h[3];
        double[] H(int i, int j) { int o = (i * n + j) * 4; double w = h[o + 3]; return new[] { h[o] - cx * w, h[o + 1] - cy * w, h[o + 2] - cz * w, w }; }
        Iv[] Bound(Func<int, int, double[]> term, int iMax, int jMax)
        {
            var box = new[] { Iv.Empty, Iv.Empty, Iv.Empty, Iv.Empty }; bool any = false;
            for (int i = 0; i <= iMax; i++) for (int j = 0; j <= jMax; j++) { var t = term(i, j); any = true; for (int c = 0; c < 4; c++) box[c] = box[c].With(t[c]); }
            return any ? box : new[] { new Iv(0, 0), new Iv(0, 0), new Iv(0, 0), new Iv(0, 0) };
        }
        static double[] Comb(double k, params (double[] X, double W)[] terms) { var r = new double[4]; foreach (var (x, w) in terms) for (int c = 0; c < 4; c++) r[c] += x[c] * w * k; return r; }
        var b0 = Bound((i, j) => H(i, j), p, q);
        var bu = Bound((i, j) => Comb(su, (H(i + 1, j), 1), (H(i, j), -1)), p - 1, q);
        var bv = Bound((i, j) => Comb(sv, (H(i, j + 1), 1), (H(i, j), -1)), p, q - 1);
        var buu = Bound((i, j) => Comb(su * su * (p - 1) / p, (H(i + 2, j), 1), (H(i + 1, j), -2), (H(i, j), 1)), p - 2, q);
        var buv = Bound((i, j) => Comb(su * sv, (H(i + 1, j + 1), 1), (H(i + 1, j), -1), (H(i, j + 1), -1), (H(i, j), 1)), p - 1, q - 1);
        var bvv = Bound((i, j) => Comb(sv * sv * (q - 1) / q, (H(i, j + 2), 1), (H(i, j + 1), -2), (H(i, j), 1)), p, q - 2);
        var inv = new Iv(1 / b0[3].Hi, 1 / b0[3].Lo); var s0 = new Iv[3];
        for (int c = 0; c < 3; c++)
        {   // S - c lies in the hull of the Euclidean control points
            var e = Iv.Empty; for (int i = 0; i <= p; i++) for (int j = 0; j <= q; j++) { var x = H(i, j); e = e.With(x[c] / x[3]); }
            s0[c] = e;
        }
        for (int c = 0; c < 3; c++)
        {
            cell.Su[c] = (bu[c] + -(s0[c] * bu[3])) * inv; cell.Sv[c] = (bv[c] + -(s0[c] * bv[3])) * inv;
            cell.Suu[c] = (buu[c] + -(cell.Su[c] * bu[3] * 2) + -(s0[c] * buu[3])) * inv;
            cell.Svv[c] = (bvv[c] + -(cell.Sv[c] * bv[3] * 2) + -(s0[c] * bvv[3])) * inv;
            cell.Suv[c] = (buv[c] + -(cell.Su[c] * bv[3]) + -(cell.Sv[c] * bu[3]) + -(s0[c] * buv[3])) * inv;
        }
        cell.Muu = Norm(cell.Suu); cell.Muv = Norm(cell.Suv); cell.Mvv = Norm(cell.Svv);
        cell.ConeOk = BoxArc(cell.Su, out cell.UMid, out cell.UHalf) && BoxArc(cell.Sv, out cell.VMid, out cell.VHalf);
        return cell;
    }

    private static bool BoxArc(Iv[] box, out double mid, out double half)
    {
        mid = half = 0; if (box[0].HasZero && box[1].HasZero) return false;
        return Arc(new[] { new V3(box[0].Lo, box[1].Lo, 0), new V3(box[0].Lo, box[1].Hi, 0), new V3(box[0].Hi, box[1].Lo, 0), new V3(box[0].Hi, box[1].Hi, 0) }, out mid, out half);
    }

    private static bool Arc(V3[] vectors, out double mid, out double half)
    {
        mid = half = 0; if (vectors.Length == 0) return false;
        double scale = vectors.Max(v => v.Length); if (!(scale > 0)) return false;
        double reference = double.NaN, lo = 0, hi = 0;
        foreach (var v in vectors)
        {
            double l = Math.Sqrt(v.X * v.X + v.Y * v.Y);
            if (l <= 1e-9 * scale) { if (v.Length <= 1e-9 * scale) continue; return false; }   // a zero vector adds nothing; one along the view has no direction
            double angle = Math.Atan2(v.Y, v.X);
            if (double.IsNaN(reference)) { reference = angle; continue; }
            double d = Wrap(angle - reference); lo = Math.Min(lo, d); hi = Math.Max(hi, d);
        }
        if (double.IsNaN(reference) || hi - lo >= Math.PI - 1e-6) return false;
        mid = reference + (lo + hi) / 2; half = (hi - lo) / 2 + 1e-9; return true;
    }
    private static double Wrap(double a) { while (a > Math.PI) a -= 2 * Math.PI; while (a <= -Math.PI) a += 2 * Math.PI; return a; }

    /// <summary>Cells that meet the parameter box.</summary>
    public void Query(double u0, double u1, double v0, double v1, List<Cell> result)
    {
        result.Clear(); int nv = V.Length - 1;
        int a0 = Find(U, u0), a1 = Find(U, u1), b0 = Find(V, v0), b1 = Find(V, v1);
        for (int a = a0; a <= a1; a++)
            for (int b = b0; b <= b1; b++)
            {
                int patch = a * nv + b, ku = _ku[patch], kv = _kv[patch]; double pu0 = U[a], pu1 = U[a + 1], pv0 = V[b], pv1 = V[b + 1];
                int i0 = Math.Clamp((int)Math.Floor((u0 - pu0) / (pu1 - pu0) * ku), 0, ku - 1), i1 = Math.Clamp((int)Math.Floor((u1 - pu0) / (pu1 - pu0) * ku), 0, ku - 1);
                int j0 = Math.Clamp((int)Math.Floor((v0 - pv0) / (pv1 - pv0) * kv), 0, kv - 1), j1 = Math.Clamp((int)Math.Floor((v1 - pv0) / (pv1 - pv0) * kv), 0, kv - 1);
                for (int i = i0; i <= i1; i++) for (int j = j0; j <= j1; j++) result.Add(Cells[_start[patch] + i * kv + j]);
            }
    }
    private static int Find(double[] breaks, double x)
    {
        int lo = 0, hi = breaks.Length - 2;
        while (lo < hi) { int mid = (lo + hi + 1) / 2; if (breaks[mid] <= x) lo = mid; else hi = mid - 1; }
        return lo;
    }

    /// <summary>Bound on |S(x) - flat triangle| over the parameter triangle (polynomial surfaces): a sixth of the largest
    /// edge value of (Muu + Muv) du² + (Mvv + Muv) dv².</summary>
    public double Deviation(double ua, double va, double ub, double vb, double uc, double vc, List<Cell> scratch)
    {
        if (Crosses(Math.Min(ua, Math.Min(ub, uc)), Math.Max(ua, Math.Max(ub, uc)), Math.Min(va, Math.Min(vb, vc)), Math.Max(va, Math.Max(vb, vc)))) return double.PositiveInfinity;
        Query(Math.Min(ua, Math.Min(ub, uc)), Math.Max(ua, Math.Max(ub, uc)), Math.Min(va, Math.Min(vb, vc)), Math.Max(va, Math.Max(vb, vc)), scratch);
        double muu = 0, muv = 0, mvv = 0;
        foreach (var c in scratch) { muu = Math.Max(muu, c.Muu); muv = Math.Max(muv, c.Muv); mvv = Math.Max(mvv, c.Mvv); }
        double Edge(double x, double y) => (muu + muv) * x * x + (mvv + muv) * y * y;
        return Math.Max(Edge(ub - ua, vb - va), Math.Max(Edge(uc - ub, vc - vb), Edge(ua - uc, va - vc))) / 6;
    }

    /// <summary>The box projects one-to-one along the view direction.</summary>
    public bool Graph(List<Cell> cells)
    {
        if (cells.Count == 0) return false;
        double um = cells[0].UMid, ulo = 0, uhi = 0, vlo = double.NaN, vhi = 0;
        foreach (var c in cells)
        {
            if (!c.ConeOk) return false;
            double d = Wrap(c.UMid - um); ulo = Math.Min(ulo, d - c.UHalf); uhi = Math.Max(uhi, d + c.UHalf);
        }
        if (uhi - ulo >= Math.PI) return false;
        double vm = cells[0].VMid;
        foreach (var c in cells)
        {
            double d = Wrap(c.VMid - vm);
            if (double.IsNaN(vlo)) { vlo = d - c.VHalf; vhi = d + c.VHalf; } else { vlo = Math.Min(vlo, d - c.VHalf); vhi = Math.Max(vhi, d + c.VHalf); }
        }
        if (vhi - vlo >= Math.PI) return false;
        double shift = Wrap(vm - um); vlo += shift; vhi += shift;                     // V arc relative to the U reference
        foreach (double turn in new[] { 0, 2 * Math.PI, -2 * Math.PI })
        {
            double a = vlo + turn, b = vhi + turn;
            if (a - uhi > 1e-9 && b - ulo < Math.PI - 1e-9) return true;              // cross(U, V) > 0 everywhere
            if (b - ulo < -1e-9 && a - uhi > -Math.PI + 1e-9) return true;            // cross(U, V) < 0 everywhere
        }
        return false;
    }

    /// <summary>
    /// Fold certificate over the cells, for a ray along the view (+Z) through a surface point where the unit vector m
    /// (in the drawing plane) is the surface normal's direction. g = lateral position (dot a, a = z x m), h = height
    /// (dot m). Returns E's smallest magnitude (0 if it may vanish) and D, a bound on the depth gained per unit of the
    /// curve parameter along g = const. A second meeting point is then within 2 |eta| D / E of depth, eta = dh along
    /// the curve at the start.
    /// </summary>
    public bool Fold(List<Cell> cells, double mx, double my, out double eMin, out double depthRate)
    {
        eMin = 0; depthRate = double.PositiveInfinity; if (cells.Count == 0) return false;
        double ax = -my, ay = mx;                                                    // a = z x m
        Iv gu = Iv.Empty, gv = Iv.Empty, hu = Iv.Empty, hv = Iv.Empty, zu = Iv.Empty, zv = Iv.Empty, guu = Iv.Empty, guv = Iv.Empty, gvv = Iv.Empty, huu = Iv.Empty, huv = Iv.Empty, hvv = Iv.Empty;
        static Iv Join(Iv a, Iv b) => new(Math.Min(a.Lo, b.Lo), Math.Max(a.Hi, b.Hi));
        static Iv Dot(Iv[] box, double x, double y) => box[0] * x + box[1] * y;
        foreach (var c in cells)
        {
            gu = Join(gu, Dot(c.Su, ax, ay)); hu = Join(hu, Dot(c.Su, mx, my)); zu = Join(zu, c.Su[2]);
            gv = Join(gv, Dot(c.Sv, ax, ay)); hv = Join(hv, Dot(c.Sv, mx, my)); zv = Join(zv, c.Sv[2]);
            guu = Join(guu, Dot(c.Suu, ax, ay)); huu = Join(huu, Dot(c.Suu, mx, my));
            guv = Join(guv, Dot(c.Suv, ax, ay)); huv = Join(huv, Dot(c.Suv, mx, my));
            gvv = Join(gvv, Dot(c.Svv, ax, ay)); hvv = Join(hvv, Dot(c.Svv, mx, my));
        }
        if (gu.HasZero && gv.HasZero) return false;                                   // level curves of g must be graphs
        Iv t1 = -gv, t2 = gu;
        Iv e = huu * t1.Sqr() + huv * (t1 * t2) * 2 + hvv * t2.Sqr() + hu * (t1 * (-guv) + t2 * (-gvv)) + hv * (t1 * guu + t2 * guv);
        if (e.HasZero) return false;
        eMin = e.Mig; depthRate = (zu * t1 + zv * t2).Mag; return true;
    }
}

internal static class MeshTier
{
    /// <summary>
    /// The largest parameter box about a chord (parameters of its ends and middle) on which the own-face certificate
    /// holds: Graph for an edge, Fold for a silhouette (normals: the surface normal's drawing direction at the three
    /// points). Boxes grow by doubling from the given margins; the last that passes is returned.
    /// </summary>
    internal static bool OwnBox(FaceCert cert, bool tangent, Point2d[] uv, (double Mx, double My)[] normals, double marginU, double marginV, List<FaceCert.Cell> scratch, ref long tests, out double u0, out double u1, out double v0, out double v1)
    {
        u0 = u1 = v0 = v1 = 0; bool found = false;
        double du0 = cert.U[0], du1 = cert.U[^1], dv0 = cert.V[0], dv1 = cert.V[^1];
        double a0 = Math.Min(uv[0].X, Math.Min(uv[1].X, uv[2].X)), a1 = Math.Max(uv[0].X, Math.Max(uv[1].X, uv[2].X)), b0 = Math.Min(uv[0].Y, Math.Min(uv[1].Y, uv[2].Y)), b1 = Math.Max(uv[0].Y, Math.Max(uv[1].Y, uv[2].Y));
        if (a0 < du0 - 1e-9 || a1 > du1 + 1e-9 || b0 < dv0 - 1e-9 || b1 > dv1 + 1e-9) return false;
        if (!tangent && cert.WholeGraph is null) { cert.Query(du0, du1, dv0, dv1, scratch); cert.WholeGraph = cert.Graph(scratch); }
        if (!tangent && cert.WholeGraph == true) { u0 = du0; u1 = du1; v0 = dv0; v1 = dv1; return true; }
        for (int k = 0; k < 10; k++)
        {
            double mu = marginU * (1 << k), mv = marginV * (1 << k);
            double x0 = Math.Max(du0, a0 - mu), x1 = Math.Min(du1, a1 + mu), y0 = Math.Max(dv0, b0 - mv), y1 = Math.Min(dv1, b1 + mv);
            if (found && x0 == u0 && x1 == u1 && y0 == v0 && y1 == v1) break;
            cert.Query(x0, x1, y0, y1, scratch); tests++;
            bool pass;
            if (!tangent) pass = cert.Graph(scratch);
            else if (cert.Crosses(x0, x1, y0, y1)) pass = false;                 // the fold argument needs a smooth box
            else { pass = true; foreach (var (mx, my) in normals) if (!cert.Fold(scratch, mx, my, out _, out _)) { pass = false; break; } }
            if (!pass) break;
            u0 = x0; u1 = x1; v0 = y0; v1 = y1; found = true;
        }
        return found;
    }

    /// <summary>The parameter box of the face's outer loop, inside the surface's domain.</summary>
    internal static (Interval U, Interval V) ParameterBox(BrepFace face)
    {
        var box = BoundingBox.Empty; foreach (var trim in face.OuterLoop.Trims) box.Union(trim.GetBoundingBox(true));
        Interval du = face.Domain(0), dv = face.Domain(1); if (!box.IsValid) return (du, dv);
        double pu = du.Length * 1e-6, pv = dv.Length * 1e-6;
        var u = new Interval(Math.Max(du.Min, box.Min.X - pu), Math.Min(du.Max, box.Max.X + pu)); var v = new Interval(Math.Max(dv.Min, box.Min.Y - pv), Math.Min(dv.Max, box.Max.Y + pv));
        return (u.Length > 0 ? u : du, v.Length > 0 ? v : dv);
    }

    /// <summary>What the freeform faces are, whether Rhino's mesh carries surface parameters, how the certified deviation
    /// compares with the measured one, and how good and how dear Rhino's silhouettes are.</summary>
    internal static void Census(PreparedScene scene, IReadOnlyList<PreparedLeaf> prepared, string stem, string output, string[] args)
    {
        double tol = scene.Tolerance; var seen = new HashSet<string>(StringComparer.Ordinal);
        double multiple = 10; foreach (var a in args) if (a.StartsWith("eps:", StringComparison.Ordinal)) multiple = double.Parse(a[4..], System.Globalization.CultureInfo.InvariantCulture);
        double eps = tol * multiple; var direction = ViewRequest.Parse("SW").Direction;
        var plane = new Plane(Point3d.Origin, direction); V3 Project(Point3d p) { var d = p - plane.Origin; return new(d * plane.XAxis, d * plane.YAxis, d * plane.ZAxis); }
        var mp = new MeshingParameters { Tolerance = eps / 2, RelativeTolerance = 0, MinimumEdgeLength = 0, MaximumEdgeLength = 0, GridAngle = RhinoMath.ToRadians(20), GridAspectRatio = 6,
            GridAmplification = 1, GridMinCount = 16, GridMaxCount = 0, RefineAngle = 0, RefineGrid = true, SimplePlanes = true, JaggedSeams = false, DoublePrecision = true };
        var kinds = new SortedDictionary<string, (int Faces, int Trimmed, int Cells, int Triangles)>(StringComparer.Ordinal);
        int definitions = 0, sources = 0, faces = 0, analytic = 0; long triangles = 0, certTriangles = 0, over = 0, refined = 0; double paramWorst = 0, meshSeconds = 0, certSeconds = 0, buildSeconds = 0, silhouetteSeconds = 0;
        var ratios = new List<double>(); var certified = new List<double>(); var measured = new List<double>(); var silErr = new List<double>(); double silLength = 0; int silCurves = 0;
        var scratch = new List<FaceCert.Cell>(); var clock = new Stopwatch();
        foreach (var leaf in prepared)
        {
            if (leaf.KernelEligible || leaf.Geometry is global::Rhino.Geometry.Point or Curve) continue;
            sources++;
            var key = leaf.Canonical is { } canonical ? canonical.Key + "/" + canonical.Scale.ToString("R") : "leaf/" + leaf.Identity;
            if (!seen.Add(key)) continue;
            Brep? brep = leaf.Geometry switch { Brep b => b, Extrusion x => x.ToBrep(), SubD s => s.ToBrep(), Surface s => s.ToBrep(), _ => null };
            if (brep is null) continue;
            if (CurvedEngine.RecognisedAs(brep, tol) is not null) { analytic++; continue; }
            definitions++;
            clock.Restart(); var meshes = Mesh.CreateFromBrep(brep, mp); meshSeconds += clock.Elapsed.TotalSeconds;
            clock.Restart(); var silhouettes = Silhouette.Compute(brep, SilhouetteType.Tangent, direction, tol, RhinoMath.DefaultAngleTolerance); silhouetteSeconds += clock.Elapsed.TotalSeconds;
            foreach (var s in silhouettes)
            {
                if (s.Curve is not { } curve) continue;
                silCurves++; silLength += curve.GetLength();
                int fi = s.GeometryComponentIndex.ComponentIndexType == ComponentIndexType.BrepFace ? s.GeometryComponentIndex.Index : -1;
                if (fi < 0) continue;
                var face = brep.Faces[fi];
                foreach (double t in curve.DivideByCount(8, true) ?? Array.Empty<double>())
                {   // distance on the surface from the curve point to where n . e = 0 (one Newton step across the fold)
                    var point = curve.PointAt(t); if (!face.ClosestPoint(point, out var u, out var v)) continue;
                    double F(double x, double y) => face.NormalAt(x, y) * direction;
                    double hu = face.Domain(0).Length * 1e-5, hv = face.Domain(1).Length * 1e-5, f = F(u, v), fu = (F(u + hu, v) - F(u - hu, v)) / (2 * hu), fv = (F(u, v + hv) - F(u, v - hv)) / (2 * hv), g2 = fu * fu + fv * fv;
                    if (g2 <= 0) continue;
                    silErr.Add(face.PointAt(u - f * fu / g2, v - f * fv / g2).DistanceTo(face.PointAt(u, v)) + face.PointAt(u, v).DistanceTo(point));
                }
            }
            foreach (var face in brep.Faces)
            {
                faces++; bool flat = face.IsPlanar(tol);
                using var surface = face.UnderlyingSurface();
                string kind = flat ? "plane" : face.TryGetCylinder(out _, tol) ? "cylinder" : face.TryGetSphere(out _, tol) ? "sphere" : face.TryGetTorus(out _, tol) ? "torus" : face.TryGetCone(out _, tol) ? "cone" : "freeform";
                clock.Restart();
                var (bu, bv) = ParameterBox(face);
                var cert = flat ? new FaceCert { Why = "plane" } : FaceCert.Build(surface, bu, bv, Project, .1, tol);
                buildSeconds += clock.Elapsed.TotalSeconds;
                string label = $"{kind} / {surface.GetType().Name} / " + (flat ? "-" : cert.Ok ? (cert.Rational ? "rational" : "polynomial") + $" deg {cert.P}x{cert.Q}" : cert.Why);
                var mesh = meshes is not null && meshes.Length == brep.Faces.Count ? meshes[face.FaceIndex] : null; int count = 0;
                if (mesh is not null)
                {
                    mesh.Faces.ConvertQuadsToTriangles(); count = mesh.Faces.Count; triangles += count;
                    if (!flat && mesh.TextureCoordinates.Count == mesh.Vertices.Count)
                    {
                        Interval du = face.Domain(0), dv = face.Domain(1); var uv = new Point2d[mesh.Vertices.Count]; double worst = 0;
                        for (int i = 0; i < uv.Length; i++)
                        {
                            var tc = mesh.TextureCoordinates[i]; uv[i] = new(du.ParameterAt(tc.X), dv.ParameterAt(tc.Y));
                            worst = Math.Max(worst, face.PointAt(uv[i].X, uv[i].Y).DistanceTo(mesh.Vertices.Point3dAt(i)));
                        }
                        if (worst > eps && Environment.GetEnvironmentVariable("PERFF_MT_DEBUG") == "1")
                        {
                            Console.WriteLine($"  tc debug {label}: domain {du} x {dv}, closedU {face.IsClosed(0)} closedV {face.IsClosed(1)}, worst {worst:G4}");
                            for (int i = 0; i < uv.Length; i += Math.Max(1, uv.Length / 6)) { var q = mesh.Vertices.Point3dAt(i); face.ClosestPoint(q, out var cu, out var cv); var tc = mesh.TextureCoordinates[i]; Console.WriteLine($"    tc {tc.X:F4},{tc.Y:F4} -> closest normalised {du.NormalizedParameterAt(cu):F4},{dv.NormalizedParameterAt(cv):F4}"); }
                        }
                        paramWorst = Math.Max(paramWorst, worst);
                        if (cert.Ok && worst <= eps)
                        {
                            clock.Restart(); var bounds = new double[count];
                            for (int t = 0; t < count; t++) { var f = mesh.Faces[t]; bounds[t] = cert.Deviation(uv[f.A].X, uv[f.A].Y, uv[f.B].X, uv[f.B].Y, uv[f.C].X, uv[f.C].Y, scratch) + worst; }
                            certSeconds += clock.Elapsed.TotalSeconds; certTriangles += count;
                            foreach (double b in bounds) { certified.Add(b); if (b > eps) { over++; refined += (long)Math.Pow(4, Math.Ceiling(Math.Log(b / eps, 4))) - 1; } }
                            int stride = Math.Max(1, count / 200);
                            for (int t = 0; t < count; t += stride)
                            {
                                var f = mesh.Faces[t]; double m = 0;
                                foreach (var (x, y, z) in new[] { (1 / 3d, 1 / 3d, 1 / 3d), (.5, .5, 0d), (0d, .5, .5), (.5, 0d, .5) })
                                {
                                    double u = uv[f.A].X * x + uv[f.B].X * y + uv[f.C].X * z, v = uv[f.A].Y * x + uv[f.B].Y * y + uv[f.C].Y * z;
                                    var flatPoint = mesh.Vertices.Point3dAt(f.A) * x + mesh.Vertices.Point3dAt(f.B) * y + mesh.Vertices.Point3dAt(f.C) * z;
                                    m = Math.Max(m, face.PointAt(u, v).DistanceTo(flatPoint));
                                }
                                measured.Add(m); if (m > tol / 100) ratios.Add(bounds[t] / m);
                            }
                        }
                    }
                }
                var row = kinds.GetValueOrDefault(label); kinds[label] = (row.Faces + 1, row.Trimmed + (face.IsSurface ? 0 : 1), row.Cells + cert.Cells.Count, row.Triangles + count);
            }
            if (!ReferenceEquals(brep, leaf.Geometry)) brep.Dispose();
        }
        static double Pick(List<double> list, double q) { if (list.Count == 0) return double.NaN; list.Sort(); return list[Math.Min(list.Count - 1, (int)(q * list.Count))]; }
        Console.WriteLine($"=== mesh-tier census {stem}: curved sources {sources}, distinct non-analytic definitions {definitions} (analytic skipped {analytic}), faces {faces}, eps {eps * 1000:F2} mm");
        foreach (var (label, r) in kinds) Console.WriteLine($"    {label,-70} faces {r.Faces,5} trimmed {r.Trimmed,5} cells {r.Cells,7} triangles {r.Triangles,8}");
        Console.WriteLine($"    Rhino mesh: {triangles} triangles in {meshSeconds:F2} s; vertex vs surface at its texture parameters, worst {paramWorst * 1000:G4} mm");
        Console.WriteLine($"    certificates: build {buildSeconds:F2} s, per-triangle bound {certSeconds:F2} s for {certTriangles} triangles; bound mm median {Pick(certified, .5) * 1000:G4} p90 {Pick(certified, .9) * 1000:G4} p99 {Pick(certified, .99) * 1000:G4} max {Pick(certified, 1) * 1000:G4}; over eps {over} ({(double)over / Math.Max(1, certTriangles):P1}), extra triangles to refine them {refined}");
        Console.WriteLine($"    measured (sampled 4 points on 1 in ~N triangles) mm median {Pick(measured, .5) * 1000:G4} p99 {Pick(measured, .99) * 1000:G4} max {Pick(measured, 1) * 1000:G4}; bound / measured median {Pick(ratios, .5):F2} p90 {Pick(ratios, .9):F2} min {Pick(ratios, 0):F2}");
        Console.WriteLine($"    silhouettes (SW): {silCurves} curves, {silLength:F1} m in {silhouetteSeconds:F2} s; distance to the true fold mm median {Pick(silErr, .5) * 1000:G4} p90 {Pick(silErr, .9) * 1000:G4} p99 {Pick(silErr, .99) * 1000:G4} max {Pick(silErr, 1) * 1000:G4} ({silErr.Count} points; tolerance {tol * 1000:G3} mm)");
    }
}
