using System.Runtime.CompilerServices;
using M2DPlus.Core.Visibility;
using Rhino;
using Rhino.Geometry;

namespace M2DPlus.Rhino.Services;

/// <summary>
/// A welded mesh of one body in the body's own coordinates, with a band for every triangle: no point of the body's
/// surface is further from the mesh than the band of the triangle it maps to.
///
/// Rhino's mesher is asked for a fraction of the wanted band, but its answer is not trusted (it does not hold its
/// tolerance, and its texture coordinates are not surface parameters). Every vertex is pulled back onto its face to
/// learn its parameters, and each triangle's band is then a bound from the face's control net
/// (<see cref="SurfaceCertificate.Deviation"/>) plus how far its corners are from the surface. A triangle with no
/// such bound (a face whose NURBS form has other parameters, a triangle across a seam or a crease) is measured at
/// four points instead and takes a quarter more than the worst found on its face; <see cref="Uncertified"/> counts
/// them. Flat faces take the largest band of the curved triangles they share a corner with: a flat face's mesh is
/// exact except where its rim is curved, and there it is as far out as its neighbour's chord.
///
/// One mesh is made per definition and kept; instances place it with <see cref="Place"/>.
/// </summary>
internal sealed class CertifiedMesh
{
    internal Point3d[] Vertices { get; private init; } = [];
    internal int[] Triangles { get; private init; } = [];
    internal double[] Bands { get; private init; } = [];
    /// <summary>Face index per triangle.</summary>
    internal int[] Faces { get; private init; } = [];
    /// <summary>Per triangle: its parameter box (u0, u1, v0, v1) in the parameters of its face's certificate, NaN where it has none.</summary>
    internal double[] Parameters { get; private init; } = [];
    /// <summary>Per face: its control-net certificate in the body's own coordinates, or null.</summary>
    internal SurfaceCertificate?[] Certificates { get; private init; } = [];
    internal int Uncertified { get; private init; }
    /// <summary>The vertices' box, and the largest band.</summary>
    internal BoundingBox Box { get; private init; }
    internal double Widest { get; private init; }
    internal double Band { get; private init; }
    internal int Count => Bands.Length;

    /// <summary>Fraction of the wanted band asked of Rhino's mesher: the control-net bound is about twice what is measured.</summary>
    internal const double MeshFraction = .2;
    /// <summary>Certificate cells are halved down to this many bands.</summary>
    internal const double CellBands = 12.5;

    private static readonly ConditionalWeakTable<GeometryBase, Dictionary<(double, double), CertifiedMesh?>> ByObject = new();
    private static readonly Dictionary<string, (CertifiedMesh? Mesh, long Used)> ByDefinition = new(StringComparer.Ordinal);
    private static long _clock, _builds;
    private const int Capacity = 4096;
    /// <summary>Meshes built since the process started (cache misses).</summary>
    internal static long Builds => Interlocked.Read(ref _builds);

    /// <summary>
    /// The certified mesh of a source's body and its placement in the world, or null when there is none (not a
    /// Brep-like body, or meshing failed). <paramref name="geometry"/> is the owned world geometry; when it is the
    /// leaf's own and the leaf has a definition, the definition's mesh is shared between its instances.
    /// </summary>
    internal static (CertifiedMesh Mesh, Transform Placement, double Scale)? For(GeometryBase geometry, PreparedLeaf source, double tolerance, double band, CancellationToken token)
    {
        if (geometry is not (Brep or Extrusion or Surface)) return null;
        if (ReferenceEquals(geometry, source.Geometry) && source.Canonical is { } canonical && source.Placement is { } placement)
        {
            double local = band / canonical.Scale, localTolerance = tolerance / canonical.Scale;
            string key = FormattableString.Invariant($"{canonical.Key.Split("|scale=")[0]}@{localTolerance:R}@{local:R}");
            CertifiedMesh? mesh; bool held;
            lock (ByDefinition) { held = ByDefinition.TryGetValue(key, out var entry); mesh = entry.Mesh; if (held) ByDefinition[key] = (mesh, ++_clock); }
            if (!held)
            {
                using var definition = canonical.CopyLocal();
                mesh = Build(definition, localTolerance, local, token);
                lock (ByDefinition)
                {
                    if (ByDefinition.Count >= Capacity && !ByDefinition.ContainsKey(key))
                        foreach (var old in ByDefinition.OrderBy(k => k.Value.Used).Take(Capacity / 4).Select(k => k.Key).ToArray()) ByDefinition.Remove(old);
                    ByDefinition[key] = (mesh, ++_clock);
                }
            }
            return mesh is null ? null : (mesh, placement, canonical.Scale);
        }
        var table = ByObject.GetOrCreateValue(geometry);
        lock (table)
        {
            if (!table.TryGetValue((tolerance, band), out var mesh)) table[(tolerance, band)] = mesh = Build(geometry, tolerance, band, token);
            return mesh is null ? null : (mesh, Transform.Identity, 1);
        }
    }

    /// <summary>The mesh as one sheet in view coordinates; bands are at least <paramref name="floor"/>.</summary>
    internal BandedSheet Place(Transform placement, double scale, Plane view, int id, double floor)
    {
        var vertices = new Vertex[Vertices.Length];
        for (int i = 0; i < vertices.Length; i++)
        {
            var p = Vertices[i]; p.Transform(placement); var d = p - view.Origin;
            vertices[i] = new(d * view.XAxis, d * view.YAxis, d * view.ZAxis);
        }
        var bands = new double[Bands.Length];
        for (int i = 0; i < bands.Length; i++) bands[i] = Math.Max(floor, Bands[i] * scale);
        return new() { Id = id, Vertices = vertices, Triangles = Triangles, Bands = bands };
    }

    internal static CertifiedMesh? Build(GeometryBase geometry, double tolerance, double band, CancellationToken token)
    {
        Brep? temporary = null;
        try
        {
            var brep = geometry as Brep ?? (temporary = geometry is Extrusion e ? e.ToBrep() : (geometry as Surface)?.ToBrep());
            if (brep is null || brep.Faces.Count == 0) return null;
            Interlocked.Increment(ref _builds);
            var parameters = new MeshingParameters
            {
                Tolerance = band * MeshFraction, RelativeTolerance = 0, MinimumEdgeLength = 0, MaximumEdgeLength = 0, GridAngle = RhinoMath.ToRadians(20), GridAspectRatio = 6,
                GridAmplification = 1, GridMinCount = 16, GridMaxCount = 0, RefineAngle = 0, RefineGrid = true, SimplePlanes = true, JaggedSeams = false, DoublePrecision = true
            };
            var meshes = Mesh.CreateFromBrep(brep, parameters);
            if (meshes is null) return null;
            try
            {
                if (meshes.Length != brep.Faces.Count) return null;
                var lookup = new Dictionary<Point3d, int>(); var vertices = new List<Point3d>(); var triangles = new List<int>(); var faces = new List<int>();
                var bands = new List<double>(); var boxes = new List<double>(); var flat = new List<bool>(); var certificates = new SurfaceCertificate?[brep.Faces.Count]; int uncertified = 0;
                for (int f = 0; f < meshes.Length; f++)
                {
                    token.ThrowIfCancellationRequested();
                    var mesh = meshes[f]; var face = brep.Faces[f]; mesh.Faces.ConvertQuadsToTriangles();
                    bool planar = face.IsPlanar(tolerance);
                    // Parameters are the face's own, or those of its NURBS form where the two differ (a revolved surface).
                    Surface? form = null; var certificate = planar ? null : certificates[f] = Certificate(face, tolerance, band * CellBands, out form);
                    using var keep = form; Surface pull = form ?? face;
                    int count = mesh.Vertices.Count; var map = new int[count]; var uv = new Point2d[count]; var off = new double[count]; var pole = new byte[count];
                    for (int v = 0; v < count; v++)
                    {
                        var p = mesh.Vertices.Point3dAt(v);
                        if (!lookup.TryGetValue(p, out int w)) { w = vertices.Count; vertices.Add(p); lookup.Add(p, w); }
                        map[v] = w; uv[v] = new(double.NaN, double.NaN); off[v] = double.PositiveInfinity;
                        if (!planar && pull.ClosestPoint(p, out double pu, out double pv) && pull.Evaluate(pu, pv, 1, out var on, out var ders))
                        {
                            uv[v] = new(pu, pv); off[v] = on.DistanceTo(p);
                            // At a pole one parameter does not move the point: any value of it is this vertex.
                            double lu = ders[0].Length * pull.Domain(0).Length, lv = ders[1].Length * pull.Domain(1).Length;
                            if (lu <= 1e-9 * lv) pole[v] = 1; else if (lv <= 1e-9 * lu) pole[v] = 2;
                        }
                    }
                    int first = bands.Count; double measured = 0; var pending = new List<int>();
                    for (int t = 0; t < mesh.Faces.Count; t++)
                    {
                        var mf = mesh.Faces[t]; int a = map[mf.A], b = map[mf.B], c = map[mf.C];
                        if (a == b || b == c || a == c) continue;
                        triangles.Add(a); triangles.Add(b); triangles.Add(c); faces.Add(f); flat.Add(planar);
                        double bound = planar ? 0 : double.NaN, u0 = double.NaN, u1 = double.NaN, v0 = double.NaN, v1 = double.NaN;
                        if (!planar)
                        {
                            Point2d qa = uv[mf.A], qb = uv[mf.B], qc = uv[mf.C];
                            if (pole[mf.A] != 0) qa = AtPole(qa, qb, qc, pole[mf.A]); if (pole[mf.B] != 0) qb = AtPole(qb, qa, qc, pole[mf.B]); if (pole[mf.C] != 0) qc = AtPole(qc, qa, qb, pole[mf.C]);
                            u0 = Math.Min(qa.X, Math.Min(qb.X, qc.X)); u1 = Math.Max(qa.X, Math.Max(qb.X, qc.X)); v0 = Math.Min(qa.Y, Math.Min(qb.Y, qc.Y)); v1 = Math.Max(qa.Y, Math.Max(qb.Y, qc.Y));
                            // Corners pulled to either side of a seam: the triangle has no parameter box.
                            if (double.IsNaN(u0 + v0) || u1 - u0 > .5 * pull.Domain(0).Length || v1 - v0 > .5 * pull.Domain(1).Length) u0 = u1 = v0 = v1 = double.NaN;
                            else if (certificate is not null)
                            {
                                bound = certificate.Deviation(qa.X, qa.Y, qb.X, qb.Y, qc.X, qc.Y) + Math.Max(off[mf.A], Math.Max(off[mf.B], off[mf.C]));
                                if (!double.IsFinite(bound)) bound = double.NaN;
                            }
                            if (double.IsNaN(bound))
                            {   // measured, not proved
                                Point3d pa = mesh.Vertices.Point3dAt(mf.A), pb = mesh.Vertices.Point3dAt(mf.B), pc = mesh.Vertices.Point3dAt(mf.C);
                                foreach (var q in new[] { (pa + pb + pc) / 3, (pa + pb) / 2, (pb + pc) / 2, (pa + pc) / 2 })
                                    measured = Math.Max(measured, face.ClosestPoint(q, out double mu, out double mv) ? face.PointAt(mu, mv).DistanceTo(q) : band);
                                pending.Add(bands.Count); uncertified++;
                            }
                        }
                        bands.Add(bound); boxes.Add(u0); boxes.Add(u1); boxes.Add(v0); boxes.Add(v1);
                    }
                    foreach (int t in pending) bands[t] = Math.Max(band, 1.25 * measured);
                }
                // Flat faces: as loose as the curved triangles they touch.
                var reach = new double[vertices.Count];
                for (int t = 0; t < bands.Count; t++) if (!flat[t]) for (int k = 0; k < 3; k++) reach[triangles[t * 3 + k]] = Math.Max(reach[triangles[t * 3 + k]], bands[t]);
                for (int t = 0; t < bands.Count; t++) if (flat[t]) bands[t] = Math.Max(reach[triangles[t * 3]], Math.Max(reach[triangles[t * 3 + 1]], reach[triangles[t * 3 + 2]]));
                return new()
                {
                    Vertices = vertices.ToArray(), Triangles = triangles.ToArray(), Bands = bands.ToArray(), Faces = faces.ToArray(), Parameters = boxes.ToArray(),
                    Certificates = certificates, Uncertified = uncertified, Band = band, Box = new BoundingBox(vertices), Widest = bands.Count == 0 ? 0 : bands.Max()
                };
            }
            finally { foreach (var mesh in meshes) mesh.Dispose(); }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return null; }
        finally { temporary?.Dispose(); }
    }

    private static Point2d AtPole(Point2d p, Point2d a, Point2d b, byte which) => which == 1 ? new((a.X + b.X) / 2, p.Y) : new(p.X, (a.Y + b.Y) / 2);

    /// <summary>The parameter box of the face's outer loop, inside the surface's domain.</summary>
    internal static (Interval U, Interval V) ParameterBox(BrepFace face)
    {
        var box = BoundingBox.Empty; foreach (var trim in face.OuterLoop.Trims) box.Union(trim.GetBoundingBox(true));
        Interval du = face.Domain(0), dv = face.Domain(1); if (!box.IsValid) return (du, dv);
        double pu = du.Length * 1e-6, pv = dv.Length * 1e-6;
        var u = new Interval(Math.Max(du.Min, box.Min.X - pu), Math.Min(du.Max, box.Max.X + pu)); var v = new Interval(Math.Max(dv.Min, box.Min.Y - pv), Math.Min(dv.Max, box.Max.Y + pv));
        return (u.Length > 0 ? u : du, v.Length > 0 ? v : dv);
    }

    /// <summary>
    /// The control-net certificate of a face's surface over the face's parameter box, in the body's coordinates, or
    /// null: no NURBS form, or a form that is not piecewise Bezier after knot insertion. Where the NURBS form is the
    /// same surface with other parameters (a revolved surface), <paramref name="form"/> is that form and the
    /// certificate's parameters are its parameters; the caller owns it.
    /// </summary>
    internal static SurfaceCertificate? Certificate(BrepFace face, double tolerance, double cellSize, out Surface? form)
    {
        form = null;
        try
        {
            using var surface = face.UnderlyingSurface(); var (du, dv) = ParameterBox(face);
            using var trimmed = surface.Trim(du, dv) ?? surface.Duplicate() as Surface;
            using var nurbs = trimmed?.ToNurbsSurface();
            if (nurbs is null) return null;
            bool same = true, congruent = true;
            for (int i = 0; i <= 4; i++)
                for (int j = 0; j <= 4; j++)
                {
                    double u = du.ParameterAt(.02 + .24 * i), v = dv.ParameterAt(.02 + .24 * j); var point = surface.PointAt(u, v);
                    if (!nurbs.Domain(0).IncludesParameter(u) || !nurbs.Domain(1).IncludesParameter(v) || nurbs.PointAt(u, v).DistanceTo(point) > tolerance / 100) same = false;
                    if (!nurbs.ClosestPoint(point, out double nu, out double nv) || nurbs.PointAt(nu, nv).DistanceTo(point) > tolerance / 100) congruent = false;
                }
            if (!congruent) return null;
            if (!same) form = (Surface)nurbs.Duplicate();
            int p = nurbs.Degree(0), q = nurbs.Degree(1); var breaks = new double[2][]; var kinks = new double[2][];
            for (int direction = 0; direction < 2; direction++)
            {
                var domain = nurbs.Domain(direction); int degree = direction == 0 ? p : q;
                var interior = (direction == 0 ? nurbs.KnotsU.ToArray() : nurbs.KnotsV.ToArray()).Where(k => k > domain.Min && k < domain.Max).ToArray();
                var values = interior.Distinct().ToArray();
                // A knot of full multiplicity may be a crease inside the face.
                kinks[direction] = values.Where(k => interior.Count(x => x == k) >= degree).ToArray();
                foreach (double k in values) { if (direction == 0) nurbs.KnotsU.InsertKnot(k, degree); else nurbs.KnotsV.InsertKnot(k, degree); }
                breaks[direction] = [domain.Min, .. values, domain.Max];
            }
            int countU = (breaks[0].Length - 1) * p + 1, countV = (breaks[1].Length - 1) * q + 1;
            if (nurbs.Points.CountU != countU || nurbs.Points.CountV != countV) { form?.Dispose(); form = null; return null; }
            bool rational = nurbs.IsRational; var h = new double[countU * countV * 4];
            for (int i = 0; i < countU; i++)
                for (int j = 0; j < countV; j++)
                {
                    var c = nurbs.Points.GetControlPoint(i, j); double w = rational ? c.Weight : 1; if (!(w > 0)) { form?.Dispose(); form = null; return null; }
                    int o = (i * countV + j) * 4; h[o] = c.Location.X * w; h[o + 1] = c.Location.Y * w; h[o + 2] = c.Location.Z * w; h[o + 3] = w;
                }
            return SurfaceCertificate.Create(p, q, breaks[0], breaks[1], h, rational, kinks[0], kinks[1], cellSize);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { form?.Dispose(); form = null; return null; }
    }
}
