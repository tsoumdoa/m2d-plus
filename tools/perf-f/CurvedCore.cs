// Curved-engine prototype core (hillclimb/curved-engine). Deliberately free of Rhino types: plain doubles in view
// coordinates (X, Y in the drawing, larger Z closer to the viewer, parallel projection along +Z).
//
// Two certified occlusion tests for a candidate chord, combined as a union over occluders:
//   Tier 1  exact analytic solids (ball, cylinder between two planes, hollow cylinder). The hidden set of a straight
//           chord is found from closed-form event parameters and one exact predicate per event interval.
//   Tier 2  triangle meshes with a chordal bound eps. A chord is Hidden only when a whole front depth component of
//           one sheet is crossed an odd number of times; Visible only when no triangle within eps can be in front;
//           anything else is bisected and finally Unresolved. See FINDINGS.md for the argument.
namespace M2DPlus.PerfF.CurvedCore;

public readonly record struct V3(double X, double Y, double Z)
{
    public static V3 operator +(V3 a, V3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static V3 operator -(V3 a, V3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static V3 operator *(V3 a, double k) => new(a.X * k, a.Y * k, a.Z * k);
    public static double Dot(V3 a, V3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    public double Length => Math.Sqrt(Dot(this, this));
    public V3 Unit() { double l = Length; return l > 0 ? this * (1 / l) : this; }
}

public enum Cls : byte { Visible, Hidden, Unresolved }

/// <summary>Why a piece has its class. 0 exact analytic hidden, 1 mesh hidden, 2 visible, 3 unresolved near own surface,
/// 4 unresolved near another surface.</summary>
public readonly record struct Piece(double T0, double T1, Cls Class, byte Why);

/// <summary>A short piece of a candidate curve. A and B are on the curve, M is the true curve point at the middle
/// parameter, so A-M-B (a parabola) follows the curve far closer than the straight chord.</summary>
public struct Chord
{
    public V3 A, M, B;
    public int Sheet;          // own mesh sheet, or -1
    public int F0, F1;         // own faces the candidate lies on, or -1
    public double S0, S1;      // depth along the view ray up to which each own face is known not to be crossed again
    public bool X0, X1;        // the limit is exact for the whole face (analytic surface); otherwise it only covers the
                               // part of the face that is depth-connected to the candidate (local rule)
    public int Solid;          // own analytic solid, or -1
    public int OnSurface;      // analytic surfaces of the own solid the candidate lies on (Solid.Outer / Inner bits)
    public bool Tangent;       // a tangent silhouette: the view ray touches OnSurface without crossing it
    public readonly V3 At(double t) => A * ((1 - t) * (1 - 2 * t)) + M * (4 * t * (1 - t)) + B * (t * (2 * t - 1));
    public readonly V3 Straight(double t) => A + (B - A) * t;
}

// ---------------------------------------------------------------- Tier 1: exact analytic solids

public abstract class Solid
{
    public const int Outer = 1, Inner = 2;
    public double X0, Y0, X1, Y1, ZMax;
    /// <summary>Is there a point of the solid on the view ray from p, more than tol in front of p?
    /// skip: surfaces the ray is known to touch without crossing (own tangent silhouette).</summary>
    public abstract bool Hides(V3 p, double tol, int skip);
    /// <summary>Chord parameters where Hides can change along a + t v. Extra values only subdivide.</summary>
    public abstract void Events(V3 a, V3 v, double tol, List<double> ts);
    /// <summary>Move a point known to lie on the given surfaces exactly onto them.</summary>
    public abstract V3 Snap(V3 p, int onSurface);

    protected static void Quadratic(double a, double b, double c, List<double> ts)
    {
        double scale = Math.Abs(a) + Math.Abs(b) + Math.Abs(c);
        if (scale == 0 || !double.IsFinite(scale)) return;
        if (Math.Abs(a) <= 1e-14 * scale) { if (Math.Abs(b) > 1e-14 * scale) Add(-c / b, ts); return; }
        double d = b * b - 4 * a * c;
        if (d < 0) { if (d > -1e-12 * b * b) Add(-b / (2 * a), ts); return; }
        double q = -.5 * (b + (b >= 0 ? 1 : -1) * Math.Sqrt(d));
        Add(q / a, ts); if (q != 0) Add(c / q, ts);
    }
    protected static void Add(double t, List<double> ts) { if (t > 0 && t < 1) ts.Add(t); }
}

public sealed class Ball : Solid
{
    private readonly V3 _c; private readonly double _r;
    public Ball(V3 centre, double radius) { _c = centre; _r = radius; X0 = centre.X - radius; X1 = centre.X + radius; Y0 = centre.Y - radius; Y1 = centre.Y + radius; ZMax = centre.Z + radius; }
    public override bool Hides(V3 p, double tol, int skip)
    {
        if ((skip & Outer) != 0) return false; // tangent to a convex body: never crossed
        double dx = p.X - _c.X, dy = p.Y - _c.Y, d2 = _r * _r - dx * dx - dy * dy;
        return d2 > 0 && _c.Z + Math.Sqrt(d2) - p.Z > tol;
    }
    public override void Events(V3 a, V3 v, double tol, List<double> ts)
    {
        var w = a - _c;
        Quadratic(v.X * v.X + v.Y * v.Y, 2 * (w.X * v.X + w.Y * v.Y), w.X * w.X + w.Y * w.Y - _r * _r, ts);       // outline
        var q = w + new V3(0, 0, tol);
        Quadratic(V3.Dot(v, v), 2 * V3.Dot(q, v), V3.Dot(q, q) - _r * _r, ts);                                    // start point on the sphere
    }
    public override V3 Snap(V3 p, int onSurface) => (onSurface & Outer) == 0 ? p : _c + (p - _c).Unit() * _r;
}

/// <summary>Cylinder (optionally hollow) between two planes that do not meet inside it: {outer} ∩ {half-spaces} \ {inner}.</summary>
public sealed class Tube : Solid
{
    private readonly V3 _o, _u; private readonly double _r, _ri; private readonly (V3 N, double D)[] _planes; // inside: N·x <= D
    public Tube(V3 origin, V3 axis, double radius, double innerRadius, (V3 N, double D)[] planes, V3[] corners)
    {
        _o = origin; _u = axis.Unit(); _r = radius; _ri = innerRadius; _planes = planes;
        X0 = corners.Min(c => c.X); X1 = corners.Max(c => c.X); Y0 = corners.Min(c => c.Y); Y1 = corners.Max(c => c.Y); ZMax = corners.Max(c => c.Z);
    }
    private bool Interval(V3 p, double radius, out double lo, out double hi)
    {
        var w = p - _o; double eu = _u.Z, wu = V3.Dot(w, _u);
        double a = 1 - eu * eu, b = w.Z - wu * eu, c = V3.Dot(w, w) - wu * wu - radius * radius;
        lo = double.NegativeInfinity; hi = double.PositiveInfinity;
        if (a < 1e-14) return c < 0;
        double d = b * b - a * c; if (d <= 0) return false;
        double s = Math.Sqrt(d); lo = (-b - s) / a; hi = (-b + s) / a; return true;
    }
    public override bool Hides(V3 p, double tol, int skip)
    {
        if ((skip & Outer) != 0) return false;
        if (!Interval(p, _r, out var lo, out var hi)) return false;
        foreach (var (n, d) in _planes)
        {
            double ne = n.Z, rhs = d - V3.Dot(n, p);
            // A cap seen exactly edge-on: a ray running in (or within a tolerance under) its plane is in contact with the
            // solid, not behind it. The tie rule makes that Visible, and it must not depend on the sign of rounding noise.
            if (Math.Abs(ne) < 1e-12) { if (rhs <= tol) return false; }
            else if (ne > 0) hi = Math.Min(hi, rhs / ne); else lo = Math.Max(lo, rhs / ne);
        }
        lo = Math.Max(lo, tol);
        const double sliver = 1e-9;
        if (hi - lo <= sliver) return false;
        if (_ri <= 0 || (skip & Inner) != 0 || !Interval(p, _ri, out var li, out var hj)) return true;
        return Math.Min(hi, li) - lo > sliver || hi - Math.Max(lo, hj) > sliver;
    }
    private void Surface(V3 a, V3 v, double tol, double radius, List<double> ts) => CylinderEvents(_o, _u, radius, _planes, a, v, tol, ts);
    /// <summary>Chord parameters where the view ray's relation to a cylinder and its rims can change.</summary>
    internal static void CylinderEvents(V3 _o, V3 _u, double radius, (V3 N, double D)[] _planes, V3 a, V3 v, double tol, List<double> ts)
    {
        double eu = _u.Z, A = 1 - eu * eu;
        var w = a - _o; double wu = V3.Dot(w, _u), vu = V3.Dot(v, _u);
        double c2 = V3.Dot(v, v) - vu * vu, c1 = 2 * (V3.Dot(w, v) - wu * vu), c0 = V3.Dot(w, w) - wu * wu - radius * radius;
        Quadratic(c2, c1, c0, ts);                                                           // the ray's foot on the surface
        var q = w + new V3(0, 0, tol); double qu = V3.Dot(q, _u);
        Quadratic(c2, 2 * (V3.Dot(q, v) - qu * vu), V3.Dot(q, q) - qu * qu - radius * radius, ts); // start point on the surface
        double b0 = w.Z - wu * eu, b1 = v.Z - vu * eu;
        double d2 = b1 * b1 - A * c2, d1 = 2 * b0 * b1 - A * c1, d0 = b0 * b0 - A * c0;
        Quadratic(d2, d1, d0, ts);                                                           // silhouette of the surface
        foreach (var (n, d) in _planes)                                                      // the ray through a rim (surface ∩ plane)
        {
            double ne = n.Z, l0 = A * (d - V3.Dot(n, a)) + ne * b0, l1 = -A * V3.Dot(n, v) + ne * b1;
            Quadratic(ne * ne * d2 - l1 * l1, ne * ne * d1 - 2 * l0 * l1, ne * ne * d0 - l0 * l0, ts);
        }
    }
    public override void Events(V3 a, V3 v, double tol, List<double> ts)
    {
        Surface(a, v, tol, _r, ts); if (_ri > 0) Surface(a, v, tol, _ri, ts);
        var q = a + new V3(0, 0, tol);
        foreach (var (n, d) in _planes)
        {
            double nv = V3.Dot(n, v);
            if (nv != 0) { Add((d - V3.Dot(n, q)) / nv, ts); Add((d - V3.Dot(n, a)) / nv, ts); }
        }
        for (int i = 0; i < _planes.Length; i++)
            for (int j = i + 1; j < _planes.Length; j++)
            {
                var (n1, d1) = _planes[i]; var (n2, d2) = _planes[j];
                if (n1.Z == 0 || n2.Z == 0) continue;
                // (d1 - n1·p)/n1z = (d2 - n2·p)/n2z
                double k0 = (d1 - V3.Dot(n1, a)) * n2.Z - (d2 - V3.Dot(n2, a)) * n1.Z, k1 = -V3.Dot(n1, v) * n2.Z + V3.Dot(n2, v) * n1.Z;
                if (k1 != 0) Add(-k0 / k1, ts);
            }
    }
    public override V3 Snap(V3 p, int onSurface)
    {
        double radius = (onSurface & Outer) != 0 ? _r : (onSurface & Inner) != 0 ? _ri : 0;
        if (radius <= 0) return p;
        var w = p - _o; double wu = V3.Dot(w, _u); var foot = _o + _u * wu; var radial = (p - foot).Unit();
        return foot + radial * radius;
    }
}

/// <summary>An open piece of a cylinder bounded by two rulings and two arcs (an extruded arc): a sheet, not a solid.</summary>
public sealed class CylinderPatch : Solid
{
    private readonly V3 _o, _u, _x, _y; private readonly double _r, _phi, _sweep, _h0, _h1; private readonly (V3 N, double D)[] _rims;
    public CylinderPatch(V3 origin, V3 axis, V3 x, V3 y, double radius, double phi, double sweep, double h0, double h1, V3 min, V3 max)
    {
        _o = origin; _u = axis.Unit(); _x = x; _y = y; _r = radius; _phi = phi; _sweep = sweep; _h0 = h0; _h1 = h1;
        _rims = new[] { (_u, V3.Dot(_u, _o) + h0), (_u, V3.Dot(_u, _o) + h1) };
        X0 = min.X; Y0 = min.Y; X1 = max.X; Y1 = max.Y; ZMax = max.Z;
    }
    private bool On(V3 q)
    {
        var w = q - _o; double h = V3.Dot(w, _u); if (h < _h0 - 1e-9 || h > _h1 + 1e-9) return false;
        double d = (Math.Atan2(V3.Dot(w, _y), V3.Dot(w, _x)) - _phi) % (2 * Math.PI); if (d < 0) d += 2 * Math.PI;
        return d <= _sweep + 1e-9 || d >= 2 * Math.PI - 1e-9;
    }
    public override bool Hides(V3 p, double tol, int skip)
    {
        if ((skip & Outer) != 0) return false; // the ray touches the cylinder: it meets it nowhere else
        var w = p - _o; double eu = _u.Z, wu = V3.Dot(w, _u), a = 1 - eu * eu, b = w.Z - wu * eu, c = V3.Dot(w, w) - wu * wu - _r * _r;
        if (a < 1e-14) return false;            // seen along the axis the sheet has no area
        double d = b * b - a * c; if (d <= 0) return false;
        double root = Math.Sqrt(d), s0 = (-b - root) / a, s1 = (-b + root) / a;
        return s0 > tol && On(p + new V3(0, 0, s0)) || s1 > tol && On(p + new V3(0, 0, s1));
    }
    public override void Events(V3 a, V3 v, double tol, List<double> ts)
    {
        Tube.CylinderEvents(_o, _u, _r, _rims, a, v, tol, ts);
        foreach (double phi in new[] { _phi, _phi + _sweep })
        {   // the ray through a ruling
            var foot = _o + _x * (_r * Math.Cos(phi)) + _y * (_r * Math.Sin(phi)); double den = v.X * _u.Y - v.Y * _u.X;
            if (den != 0) Add(((foot.X - a.X) * _u.Y - (foot.Y - a.Y) * _u.X) / den, ts);
        }
    }
    public override V3 Snap(V3 p, int onSurface)
    {
        if ((onSurface & Outer) == 0) return p;
        var w = p - _o; var foot = _o + _u * V3.Dot(w, _u); return foot + (p - foot).Unit() * _r;
    }
}

public sealed class SolidIndex
{
    private readonly Solid[] _solids; private readonly double _x0, _y0, _cell; private readonly int _nx, _ny; private readonly int[] _start, _items;
    public int Count => _solids.Length;
    public Solid this[int i] => _solids[i];
    public SolidIndex(Solid[] solids)
    {
        _solids = solids;
        if (solids.Length == 0) { _start = new int[2]; _items = Array.Empty<int>(); _nx = _ny = 1; _cell = 1; return; }
        _x0 = solids.Min(s => s.X0); _y0 = solids.Min(s => s.Y0); double x1 = solids.Max(s => s.X1), y1 = solids.Max(s => s.Y1);
        _cell = Math.Max(1e-6, 2 * solids.Average(s => Math.Max(s.X1 - s.X0, s.Y1 - s.Y0)));
        _nx = Math.Clamp((int)((x1 - _x0) / _cell) + 1, 1, 1024); _ny = Math.Clamp((int)((y1 - _y0) / _cell) + 1, 1, 1024);
        var count = new int[_nx * _ny + 1];
        for (int pass = 0; pass < 2; pass++)
        {
            for (int i = 0; i < solids.Length; i++)
            {
                var s = solids[i]; Cells(s.X0, s.Y0, s.X1, s.Y1, out int i0, out int j0, out int i1, out int j1);
                for (int j = j0; j <= j1; j++) for (int k = i0; k <= i1; k++) { if (pass == 0) count[j * _nx + k + 1]++; else _items![--count[j * _nx + k + 1]] = i; }
            }
            if (pass == 0) { for (int c = 0; c < _nx * _ny; c++) count[c + 1] += count[c]; _items = new int[count[_nx * _ny]]; _start = (int[])count.Clone(); for (int c = _nx * _ny; c > 0; c--) count[c] = _start[c]; }
        }
        _start ??= count; _items ??= Array.Empty<int>();
    }
    private void Cells(double x0, double y0, double x1, double y1, out int i0, out int j0, out int i1, out int j1)
    {
        i0 = Math.Clamp((int)Math.Floor((x0 - _x0) / _cell), 0, _nx - 1); i1 = Math.Clamp((int)Math.Floor((x1 - _x0) / _cell), 0, _nx - 1);
        j0 = Math.Clamp((int)Math.Floor((y0 - _y0) / _cell), 0, _ny - 1); j1 = Math.Clamp((int)Math.Floor((y1 - _y0) / _cell), 0, _ny - 1);
    }
    public void Query(double x0, double y0, double x1, double y1, double zMin, List<int> result)
    {
        result.Clear(); if (_solids.Length == 0) return;
        Cells(x0, y0, x1, y1, out int i0, out int j0, out int i1, out int j1);
        for (int j = j0; j <= j1; j++)
            for (int k = i0; k <= i1; k++)
                for (int e = _start[j * _nx + k]; e < _start[j * _nx + k + 1]; e++)
                {
                    int id = _items[e]; var s = _solids[id];
                    if (s.X1 < x0 || s.X0 > x1 || s.Y1 < y0 || s.Y0 > y1 || s.ZMax <= zMin) continue;
                    if (!result.Contains(id)) result.Add(id);
                }
    }
}

// ---------------------------------------------------------------- Tier 2: meshes with a chordal bound

public sealed class SheetMesh
{
    public int Sheet; public double Eps; public V3[] Vertices = Array.Empty<V3>(); public int[] Triangles = Array.Empty<int>(); public int[] Faces = Array.Empty<int>();
}

public sealed class MeshIndex
{
    public readonly int Count;
    private readonly double[] _x, _y, _z;           // 3 per triangle
    private readonly int[] _sheet, _face; private readonly byte[] _naked; private readonly double[] _eps;
    private readonly double[] _pa, _pb, _pc, _slope, _zmin, _zmax; private readonly bool[] _steep;
    private readonly double _gx, _gy;

    public MeshIndex(IReadOnlyList<SheetMesh> sheets)
    {
        Count = sheets.Sum(s => s.Triangles.Length / 3);
        _x = new double[Count * 3]; _y = new double[Count * 3]; _z = new double[Count * 3]; _sheet = new int[Count]; _face = new int[Count]; _naked = new byte[Count]; _eps = new double[Count];
        _pa = new double[Count]; _pb = new double[Count]; _pc = new double[Count]; _slope = new double[Count]; _zmin = new double[Count]; _zmax = new double[Count]; _steep = new bool[Count];
        int t = 0; double sum = 0;
        double gx0 = double.MaxValue, gy0 = double.MaxValue, gx1 = double.MinValue, gy1 = double.MinValue;
        foreach (var s in sheets)
        {
            int first = t; var edges = new Dictionary<(int, int), int>(s.Triangles.Length * 2);
            for (int i = 0; i < s.Triangles.Length; i += 3, t++)
            {
                for (int k = 0; k < 3; k++)
                {
                    var p = s.Vertices[s.Triangles[i + k]]; _x[t * 3 + k] = p.X; _y[t * 3 + k] = p.Y; _z[t * 3 + k] = p.Z;
                    int a = s.Triangles[i + k], b = s.Triangles[i + (k + 1) % 3]; var key = a < b ? (a, b) : (b, a);
                    edges[key] = edges.GetValueOrDefault(key) + 1;
                }
                _sheet[t] = s.Sheet; _face[t] = s.Faces[i / 3]; _eps[t] = s.Eps;
                double ax = _x[t * 3], ay = _y[t * 3], az = _z[t * 3], ux = _x[t * 3 + 1] - ax, uy = _y[t * 3 + 1] - ay, uz = _z[t * 3 + 1] - az, vx = _x[t * 3 + 2] - ax, vy = _y[t * 3 + 2] - ay, vz = _z[t * 3 + 2] - az;
                double nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx, planar = Math.Sqrt(nx * nx + ny * ny);
                _zmin[t] = Math.Min(az, Math.Min(az + uz, az + vz)); _zmax[t] = Math.Max(az, Math.Max(az + uz, az + vz));
                if (Math.Abs(nz) <= 1e-9 * planar || nz == 0) _steep[t] = true;
                else { _pa[t] = -nx / nz; _pb[t] = -ny / nz; _pc[t] = az - _pa[t] * ax - _pb[t] * ay; _slope[t] = planar / Math.Abs(nz); }
                double x0 = Math.Min(ax, Math.Min(ax + ux, ax + vx)), x1 = Math.Max(ax, Math.Max(ax + ux, ax + vx)), y0 = Math.Min(ay, Math.Min(ay + uy, ay + vy)), y1 = Math.Max(ay, Math.Max(ay + uy, ay + vy));
                sum += Math.Max(x1 - x0, y1 - y0) + 2 * s.Eps; gx0 = Math.Min(gx0, x0 - s.Eps); gy0 = Math.Min(gy0, y0 - s.Eps); gx1 = Math.Max(gx1, x1 + s.Eps); gy1 = Math.Max(gy1, y1 + s.Eps);
            }
            t = first;
            for (int i = 0; i < s.Triangles.Length; i += 3, t++)
                for (int k = 0; k < 3; k++)
                {
                    int a = s.Triangles[i + k], b = s.Triangles[i + (k + 1) % 3]; var key = a < b ? (a, b) : (b, a);
                    if (edges[key] != 2) _naked[t] |= (byte)(1 << k); // boundary or non-manifold: no parity argument across it
                }
        }
        if (Count == 0) return;
        // Multi-level grid: a triangle goes to the finest level where its inflated box spans at most three cells a side.
        _gx = gx0; _gy = gy0; double baseCell = Math.Max(1e-9, sum / Count), extent = Math.Max(gx1 - gx0, gy1 - gy0);
        while (extent / baseCell > 3000) baseCell *= 1.25;
        var level = new int[Count]; int levels = 1;
        for (int i = 0; i < Count; i++)
        {
            Box(i, out var x0, out var y0, out var x1, out var y1); double size = Math.Max(x1 - x0, y1 - y0), cell = baseCell; int l = 0;
            while (size > 2 * cell && l < 15) { cell *= 4; l++; }
            level[i] = l; levels = Math.Max(levels, l + 1);
        }
        _levels = new Level[levels];
        for (int l = 0; l < levels; l++)
        {
            double cell = baseCell * Math.Pow(4, l); int nx = (int)((gx1 - gx0) / cell) + 1, ny = (int)((gy1 - gy0) / cell) + 1;
            var count = new int[nx * ny + 1]; int[] items = Array.Empty<int>(); int[] start = count;
            for (int pass = 0; pass < 2; pass++)
            {
                for (int i = 0; i < Count; i++)
                {
                    if (level[i] != l) continue;
                    Box(i, out var x0, out var y0, out var x1, out var y1);
                    int i0 = Clamp((x0 - _gx) / cell, nx), i1 = Clamp((x1 - _gx) / cell, nx), j0 = Clamp((y0 - _gy) / cell, ny), j1 = Clamp((y1 - _gy) / cell, ny);
                    for (int j = j0; j <= j1; j++) for (int k = i0; k <= i1; k++) { if (pass == 0) count[j * nx + k + 1]++; else items[--count[j * nx + k + 1]] = i; }
                }
                if (pass == 0)
                {
                    for (int c = 0; c < nx * ny; c++) count[c + 1] += count[c];
                    items = new int[count[nx * ny]]; start = (int[])count.Clone();
                    for (int c = nx * ny; c > 0; c--) count[c] = start[c];
                }
            }
            _levels[l] = new(cell, nx, ny, start, items);
        }
    }
    private sealed record Level(double Cell, int Nx, int Ny, int[] Start, int[] Items);
    private readonly Level[] _levels = Array.Empty<Level>();
    private static int Clamp(double v, int n) => Math.Clamp((int)Math.Floor(v), 0, n - 1);
    private void Box(int i, out double x0, out double y0, out double x1, out double y1)
    {
        double e = _eps[i];
        x0 = Math.Min(_x[i * 3], Math.Min(_x[i * 3 + 1], _x[i * 3 + 2])) - e; x1 = Math.Max(_x[i * 3], Math.Max(_x[i * 3 + 1], _x[i * 3 + 2])) + e;
        y0 = Math.Min(_y[i * 3], Math.Min(_y[i * 3 + 1], _y[i * 3 + 2])) - e; y1 = Math.Max(_y[i * 3], Math.Max(_y[i * 3 + 1], _y[i * 3 + 2])) + e;
    }

    /// <summary>Which own-face triangles the local rule may exempt.
    /// 1 strict: only triangles whose depth range reaches the candidate itself.
    /// 2 loose: also triangles depth-connected to those, unless they cover part of the piece in the drawing.
    /// 0 default: strict for edges, loose for tangent silhouettes (whose own surface folds away in a ring of steep triangles).</summary>
    public static int StrictChain;
    public sealed class Scratch
    {
        internal int[] Stamp; internal int Id; internal readonly List<int>[] Lists = new List<int>[48];
        internal readonly List<(int Sheet, double Lo, double Hi, int Flags)> Entries = new(); internal readonly List<(double Lo, double Hi, int Tri)> Local = new(); // flags: 1 crossing at the middle, 2 naked edge near, 4 poison (may reach the start)
        public long Tests, Nodes; public double TraceT = double.NaN; public List<string>? Trace;
        public Scratch(MeshIndex index) { Stamp = new int[index.Count]; for (int i = 0; i < Lists.Length; i++) Lists[i] = new(); }
    }

    /// <summary>Classify the straight chord piece [t0, t1]; pieces are appended in order.</summary>
    public void Certify(in Chord chord, double t0, double t1, double tol, double hMin, Scratch s, List<Piece> output)
    {
        if (Count == 0) { output.Add(new(t0, t1, Cls.Visible, 2)); return; }
        var a = chord.Straight(t0); var b = chord.Straight(t1);
        var list = s.Lists[0]; list.Clear(); int id = ++s.Id;
        double qx0 = Math.Min(a.X, b.X) - _gx, qx1 = Math.Max(a.X, b.X) - _gx, qy0 = Math.Min(a.Y, b.Y) - _gy, qy1 = Math.Max(a.Y, b.Y) - _gy; double zFloor = Math.Min(a.Z, b.Z);
        foreach (var level in _levels)
        {
            if (level.Items.Length == 0) continue;
            int i0 = Clamp(qx0 / level.Cell, level.Nx), i1 = Clamp(qx1 / level.Cell, level.Nx), j0 = Clamp(qy0 / level.Cell, level.Ny), j1 = Clamp(qy1 / level.Cell, level.Ny);
            for (int j = j0; j <= j1; j++)
                for (int k = i0; k <= i1; k++)
                    for (int e = level.Start[j * level.Nx + k]; e < level.Start[j * level.Nx + k + 1]; e++)
                    {
                        int tri = level.Items[e]; if (s.Stamp[tri] == id) continue; s.Stamp[tri] = id;
                        if (_zmax[tri] + _eps[tri] - zFloor <= tol) continue;   // wholly behind the piece: can never be in front
                        list.Add(tri);
                    }
        }
        Classify(chord, t0, t1, tol, hMin, s, 0, output);
    }

    private void Classify(in Chord chord, double t0, double t1, double tol, double hMin, Scratch s, int depth, List<Piece> output)
    {
        s.Nodes++;
        var a = chord.Straight(t0); var b = chord.Straight(t1); var m = chord.Straight((t0 + t1) / 2);
        var list = s.Lists[depth]; var sub = s.Lists[depth + 1]; sub.Clear(); var entries = s.Entries; entries.Clear();
        double zcMin = Math.Min(a.Z, b.Z), zcMax = Math.Max(a.Z, b.Z), own = double.NegativeInfinity; bool ownNear = false, otherNear = false;
        foreach (int t in list)
        {
            double e = _eps[t];
            if (!Near(t, a, b, e)) continue;
            sub.Add(t); s.Tests++;
            double lo = _zmin[t] - zcMax, hi = _zmax[t] - zcMin;
            if (!_steep[t])
            {
                double ga = _pa[t] * a.X + _pb[t] * a.Y + _pc[t] - a.Z, gb = _pa[t] * b.X + _pb[t] * b.Y + _pc[t] - b.Z, w = e * _slope[t];
                double lo2 = Math.Min(ga, gb) - w, hi2 = Math.Max(ga, gb) + w;
                if (lo2 > hi || hi2 < lo) continue;
                lo = Math.Max(lo, lo2); hi = Math.Min(hi, hi2);
            }
            lo -= e; hi += e;
            bool trace = s.Trace is not null && t0 <= s.TraceT && s.TraceT <= t1;
            if (trace) s.Trace!.Add($"  d{depth} [{t0:F4},{t1:F4}] tri {t} sheet {_sheet[t]} face {_face[t]} gap [{lo:G5},{hi:G5}] steep {_steep[t]} slope {_slope[t]:G4} in {PointIn(t, m)} naked {_naked[t]}");
            if (hi <= tol) continue;                                   // cannot hold a surface point in front
            bool mine = _sheet[t] == chord.Sheet;
            if (mine)
            {
                bool f0 = _face[t] == chord.F0, f1 = !f0 && _face[t] == chord.F1;
                if (f0 || f1)
                {
                    double limit = f0 ? chord.S0 : chord.S1;
                    if (hi <= limit)
                    {
                        if (f0 ? chord.X0 : chord.X1) { own = Math.Max(own, hi); continue; }   // exact: this face is not crossed again before limit
                        s.Local.Add((lo, hi, t)); continue;                                    // decided after the loop
                    }
                }
            }
            if (lo > tol)
            {
                int flags = PointIn(t, m) ? 1 : 0;
                if (_naked[t] != 0 && NakedNear(t, a, b, e)) flags |= 2;
                entries.Add((_sheet[t], lo, hi, flags));
            }
            else { entries.Add((_sheet[t], lo, hi, 4)); }
            if (mine) ownNear = true; else otherNear = true;
        }
        if (s.Local.Count > 0)
        {
            // Local rule: only the part of the own face that is depth-connected to the candidate is known not to be crossed.
            // A triangle that does not reach the candidate's own depth and yet covers part of the piece in the drawing is a
            // separate sheet of the face passing in front (a wall beyond a corner): the chain stops there.
            s.Local.Sort(static (p, q) => p.Lo.CompareTo(q.Lo)); double reach = tol; int k = 0;
            for (; k < s.Local.Count && s.Local[k].Lo <= reach; k++)
            {
                if (s.Local[k].Lo > tol && (StrictChain == 1 || StrictChain == 0 && !chord.Tangent || Near(s.Local[k].Tri, a, b, 0))) break;
                reach = Math.Max(reach, s.Local[k].Hi);
            }
            if (k > 0) own = Math.Max(own, reach);
            for (; k < s.Local.Count; k++)
            {
                var (lo, hi, t) = s.Local[k]; int flags = PointIn(t, m) ? 1 : 0;
                if (_naked[t] != 0 && NakedNear(t, a, b, _eps[t])) flags |= 2;
                entries.Add((_sheet[t], lo, hi, flags)); ownNear = true;
            }
            s.Local.Clear();
        }
        if (entries.Count == 0) { output.Add(new(t0, t1, Cls.Visible, 2)); return; }
        // Front depth components per sheet: a component with no poison, no boundary and an odd number of crossings holds a
        // real surface crossing in front of every point of the piece.
        entries.Sort(static (p, q) => p.Sheet != q.Sheet ? p.Sheet.CompareTo(q.Sheet) : p.Lo.CompareTo(q.Lo));
        bool hidden = false;
        for (int i = 0; i < entries.Count && !hidden;)
        {
            int sheet = entries[i].Sheet; double end = entries[i].Hi; int crossings = 0, flags = 0; int j = i;
            double floor = sheet == chord.Sheet ? own : double.NegativeInfinity;
            for (; j < entries.Count && entries[j].Sheet == sheet && (j == i || entries[j].Lo <= end); j++)
            {
                end = Math.Max(end, entries[j].Hi); flags |= entries[j].Flags; crossings += entries[j].Flags & 1;
            }
            if ((flags & 6) == 0 && entries[i].Lo > floor && (crossings & 1) == 1) hidden = true;
            i = j;
        }
        if (hidden) { output.Add(new(t0, t1, Cls.Hidden, 1)); return; }
        double dx = b.X - a.X, dy = b.Y - a.Y, dz = b.Z - a.Z;
        if (dx * dx + dy * dy + dz * dz <= hMin * hMin || depth + 2 >= s.Lists.Length) { output.Add(new(t0, t1, Cls.Unresolved, (byte)(otherNear ? 4 : ownNear ? 3 : 4))); return; }
        double tm = (t0 + t1) / 2;
        Classify(chord, t0, tm, tol, hMin, s, depth + 1, output);
        // The first half reused the deeper lists; rebuild this level's sub list for the second half.
        sub = s.Lists[depth + 1]; sub.Clear();
        foreach (int t in list) if (Near(t, a, b, _eps[t])) sub.Add(t);
        Classify(chord, tm, t1, tol, hMin, s, depth + 1, output);
    }

    private static double Orient(double ax, double ay, double bx, double by, double px, double py) => (bx - ax) * (py - ay) - (by - ay) * (px - ax);

    /// <summary>Point in projected triangle with a symbolic perturbation p + (d, d²), evaluated on canonically ordered
    /// edges so that two triangles sharing an edge always disagree: a point is in exactly one of them.</summary>
    private bool PointIn(int t, V3 p)
    {
        int o = t * 3;
        for (int k = 0; k < 3; k++)
        {
            double ax = _x[o + k], ay = _y[o + k], bx = _x[o + (k + 1) % 3], by = _y[o + (k + 1) % 3], cx = _x[o + (k + 2) % 3], cy = _y[o + (k + 2) % 3];
            if (ax > bx || ax == bx && ay > by) { (ax, bx) = (bx, ax); (ay, by) = (by, ay); }
            double side = Orient(ax, ay, bx, by, cx, cy); if (side == 0) return false;
            double at = Orient(ax, ay, bx, by, p.X, p.Y);
            if (at == 0) at = by != ay ? -(by - ay) : bx - ax;
            if (at > 0 != side > 0) return false;
        }
        return true;
    }

    private static double PointSegment2(double px, double py, double ax, double ay, double bx, double by)
    {
        double dx = bx - ax, dy = by - ay, den = dx * dx + dy * dy, t = den == 0 ? 0 : Math.Clamp(((px - ax) * dx + (py - ay) * dy) / den, 0, 1);
        double ex = px - ax - dx * t, ey = py - ay - dy * t; return ex * ex + ey * ey;
    }
    private static bool SegmentsWithin(double ax, double ay, double bx, double by, double cx, double cy, double dx, double dy, double e2)
    {
        double o1 = Orient(ax, ay, bx, by, cx, cy), o2 = Orient(ax, ay, bx, by, dx, dy), o3 = Orient(cx, cy, dx, dy, ax, ay), o4 = Orient(cx, cy, dx, dy, bx, by);
        if ((o1 > 0) != (o2 > 0) && (o3 > 0) != (o4 > 0)) return true;
        return PointSegment2(ax, ay, cx, cy, dx, dy) <= e2 || PointSegment2(bx, by, cx, cy, dx, dy) <= e2 || PointSegment2(cx, cy, ax, ay, bx, by) <= e2 || PointSegment2(dx, dy, ax, ay, bx, by) <= e2;
    }
    private bool Inside(int t, double px, double py)
    {
        int o = t * 3;
        double s0 = Orient(_x[o], _y[o], _x[o + 1], _y[o + 1], px, py), s1 = Orient(_x[o + 1], _y[o + 1], _x[o + 2], _y[o + 2], px, py), s2 = Orient(_x[o + 2], _y[o + 2], _x[o], _y[o], px, py);
        return s0 >= 0 && s1 >= 0 && s2 >= 0 || s0 <= 0 && s1 <= 0 && s2 <= 0;
    }
    /// <summary>Is the projected triangle within e of the segment a-b in the drawing?</summary>
    private bool Near(int t, V3 a, V3 b, double e)
    {
        int o = t * 3;
        double x0 = Math.Min(a.X, b.X) - e, x1 = Math.Max(a.X, b.X) + e, y0 = Math.Min(a.Y, b.Y) - e, y1 = Math.Max(a.Y, b.Y) + e;
        if (Math.Max(_x[o], Math.Max(_x[o + 1], _x[o + 2])) < x0 || Math.Min(_x[o], Math.Min(_x[o + 1], _x[o + 2])) > x1 ||
            Math.Max(_y[o], Math.Max(_y[o + 1], _y[o + 2])) < y0 || Math.Min(_y[o], Math.Min(_y[o + 1], _y[o + 2])) > y1) return false;
        if (Inside(t, a.X, a.Y) || Inside(t, b.X, b.Y)) return true;
        double e2 = e * e;
        for (int k = 0; k < 3; k++)
            if (SegmentsWithin(a.X, a.Y, b.X, b.Y, _x[o + k], _y[o + k], _x[o + (k + 1) % 3], _y[o + (k + 1) % 3], e2)) return true;
        return false;
    }
    private bool NakedNear(int t, V3 a, V3 b, double e)
    {
        int o = t * 3; double e2 = e * e;
        for (int k = 0; k < 3; k++)
            if ((_naked[t] & (1 << k)) != 0 && SegmentsWithin(a.X, a.Y, b.X, b.Y, _x[o + k], _y[o + k], _x[o + (k + 1) % 3], _y[o + (k + 1) % 3], e2)) return true;
        return false;
    }
}

/// <summary>Union of the two tiers for one chord.</summary>
public static class ChordSolver
{
    public sealed class Scratch
    {
        public readonly MeshIndex.Scratch Mesh; public readonly List<int> Solids = new(); public readonly List<double> Events = new(); public long SolidTests;
        public Scratch(MeshIndex index) { Mesh = new(index); }
    }

    public static void Solve(in Chord chord, SolidIndex solids, MeshIndex meshes, double tol, double hMin, Scratch s, List<Piece> output)
    {
        int first = output.Count;
        var a = chord.A; var b = chord.B; var v = b - a;
        solids.Query(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y), Math.Min(a.Z, b.Z), s.Solids);
        if (s.Solids.Count == 0) meshes.Certify(chord, 0, 1, tol, hMin, s.Mesh, output);
        else
        {
            var ts = s.Events; ts.Clear(); ts.Add(0); ts.Add(1);
            foreach (int id in s.Solids) solids[id].Events(a, v, tol, ts);
            ts.Sort();
            double open = double.NaN;
            for (int i = 0; i + 1 < ts.Count; i++)
            {
                double t0 = ts[i], t1 = ts[i + 1]; if (t1 - t0 <= 1e-12) continue;
                var p = chord.At((t0 + t1) / 2); bool hidden = false;
                foreach (int id in s.Solids)
                {
                    s.SolidTests++;
                    bool mine = id == chord.Solid; var q = mine ? solids[id].Snap(p, chord.OnSurface) : p;
                    if (solids[id].Hides(q, tol, mine && chord.Tangent ? chord.OnSurface : 0)) { hidden = true; break; }
                }
                if (hidden)
                {
                    if (!double.IsNaN(open)) { meshes.Certify(chord, open, t0, tol, hMin, s.Mesh, output); open = double.NaN; }
                    output.Add(new(t0, t1, Cls.Hidden, 0));
                }
                else if (double.IsNaN(open)) open = t0;
            }
            if (!double.IsNaN(open)) meshes.Certify(chord, open, 1, tol, hMin, s.Mesh, output);
        }
        // Merge neighbours of one class.
        int w = first;
        for (int i = first; i < output.Count; i++)
        {
            if (w > first && output[w - 1].Class == output[i].Class && (output[i].Class != Cls.Unresolved || output[w - 1].Why == output[i].Why))
                output[w - 1] = output[w - 1] with { T1 = output[i].T1 };
            else output[w++] = output[i];
        }
        output.RemoveRange(w, output.Count - w);
    }
}

/// <summary>Classed 2D segments in a hash grid: which classes are drawn within a distance of a point?</summary>
public sealed class Locus
{
    private readonly List<(double Ax, double Ay, double Bx, double By, byte Class)> _segments = new();
    private readonly Dictionary<(int, int), List<int>> _grid = new(); private readonly double _cell;
    public Locus(double cell) { _cell = cell; }
    public void Add(double ax, double ay, double bx, double by, byte cls)
    {
        int id = _segments.Count; _segments.Add((ax, ay, bx, by, cls));
        int i0 = (int)Math.Floor(Math.Min(ax, bx) / _cell), i1 = (int)Math.Floor(Math.Max(ax, bx) / _cell), j0 = (int)Math.Floor(Math.Min(ay, by) / _cell), j1 = (int)Math.Floor(Math.Max(ay, by) / _cell);
        if ((long)(i1 - i0 + 1) * (j1 - j0 + 1) > 4096)
        {   // a long diagonal segment: walk it instead of filling its box
            int steps = (int)(Math.Max(Math.Abs(bx - ax), Math.Abs(by - ay)) / _cell * 2) + 1; var seen = new HashSet<(int, int)>();
            for (int k = 0; k <= steps; k++) { double x = ax + (bx - ax) * k / steps, y = ay + (by - ay) * k / steps; for (int di = -1; di <= 1; di++) for (int dj = -1; dj <= 1; dj++) seen.Add(((int)Math.Floor(x / _cell) + di, (int)Math.Floor(y / _cell) + dj)); }
            foreach (var c in seen) Put(c, id); return;
        }
        for (int i = i0; i <= i1; i++) for (int j = j0; j <= j1; j++) Put((i, j), id);
    }
    private void Put((int, int) cell, int id) { if (!_grid.TryGetValue(cell, out var list)) _grid.Add(cell, list = new()); list.Add(id); }
    /// <summary>Bit mask of classes with linework within reach of (x, y). reach must not exceed the cell size.</summary>
    public int Classes(double x, double y, double reach)
    {
        int mask = 0; double r2 = reach * reach;
        int i0 = (int)Math.Floor((x - reach) / _cell), i1 = (int)Math.Floor((x + reach) / _cell), j0 = (int)Math.Floor((y - reach) / _cell), j1 = (int)Math.Floor((y + reach) / _cell);
        for (int i = i0; i <= i1; i++)
            for (int j = j0; j <= j1; j++)
                if (_grid.TryGetValue((i, j), out var list))
                    foreach (int id in list)
                    {
                        var g = _segments[id]; if ((mask & (1 << g.Class)) != 0) continue;
                        double dx = g.Bx - g.Ax, dy = g.By - g.Ay, den = dx * dx + dy * dy, t = den == 0 ? 0 : Math.Clamp(((x - g.Ax) * dx + (y - g.Ay) * dy) / den, 0, 1);
                        double ex = x - g.Ax - dx * t, ey = y - g.Ay - dy * t;
                        if (ex * ex + ey * ey <= r2) mask |= 1 << g.Class;
                    }
        return mask;
    }
}
