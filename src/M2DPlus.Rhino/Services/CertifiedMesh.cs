using System.Runtime.CompilerServices;
using M2DPlus.Core;
using M2DPlus.Core.Visibility;
using Rhino;
using Rhino.Geometry;

namespace M2DPlus.Rhino.Services;

/// <summary>
/// A welded mesh of one body in the body's own coordinates, with a band for every triangle: no point of the body's
/// surface is further from the mesh than the band of the triangle it maps to.
///
/// Rhino's mesher is asked for a fraction of the wanted band, but its answer is not trusted (it does not hold its
/// tolerance). It is asked for texture coordinates that are the surface's parameters; each is checked by evaluating
/// the surface there, and a vertex it does not fit is pulled back onto the face by closest point instead. Each
/// triangle's band is then a bound from the face's control net
/// (<see cref="SurfaceCertificate.Deviation"/>) plus how far its corners are from the surface. A triangle with no
/// such bound (a face whose NURBS form has other parameters, a triangle across a seam or a crease) is measured at
/// four points instead and takes a quarter more than the worst found on its face; <see cref="Uncertified"/> counts
/// them. Flat faces take the largest band of the curved triangles they share a corner with: a flat face's mesh is
/// exact except where its rim is curved, and there it is as far out as its neighbour's chord.
///
/// One mesh is made per definition and kept in the scene cache; instances place it with <see cref="Place"/>.
/// </summary>
internal sealed class CertifiedMesh : IDisposable
{
    /// <summary>Owned managed data only; disposal is what a cache lease needs, nothing is released.</summary>
    public void Dispose() { }
    // Vertices, three indices and a band per triangle. The certificates the bands came from are not kept.
    internal long Bytes => 24L * Vertices.Length + 20L * Bands.Length + 256;
    internal Point3d[] Vertices { get; private init; } = [];
    internal int[] Triangles { get; private init; } = [];
    internal double[] Bands { get; private init; } = [];
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
    private static long _builds, _pulled;
    /// <summary>Vertices whose parameters had to be found by closest point since the process started.</summary>
    internal static long PulledBack => Interlocked.Read(ref _pulled);
    /// <summary>Harness only: seconds spent meshing, on certificates, on vertices, on triangle bands and on measuring.</summary>
    internal static readonly double[] BuildSeconds = new double[5];
    /// <summary>Meshes built since the process started (cache misses).</summary>
    internal static long Builds => Interlocked.Read(ref _builds);

    /// <summary>A mesh in use: where it is in the world, and the cache lease that keeps it (none for a body meshed by itself).</summary>
    internal readonly record struct Placed(CertifiedMesh Mesh, Transform Placement, double Scale, LeaseEntry<CertifiedMesh>.Lease? Lease);

    /// <summary>
    /// The certified mesh of a source body and its placement in the world, or null when there is none (not a
    /// Brep-like body, or meshing failed). <paramref name="geometry"/> is the owned world geometry. When it is the
    /// leaf geometry itself and the leaf has a definition, the mesh of the definition is taken from the scene cache
    /// under the definition key and shared between its instances, views and solves; the caller disposes the lease.
    /// Any other body (a clipped piece, a glass substitute, a sheared instance) is meshed by itself and kept with
    /// that object.
    /// </summary>
    /// <param name="failed">True when a Brep-like body got no mesh: meshing threw (nothing is remembered, so a later
    /// solve tries again), or Rhino returned none for it (remembered).</param>
    internal static Placed? For(GeometryBase geometry, PreparedLeaf source, double tolerance, double band, CancellationToken token, out bool failed)
    {
        failed = false;
        if (geometry is not (Brep or Extrusion or Surface)) return null;
        try { var found = Find(geometry, source, tolerance, band, token); failed = found is null; return found; }
        // A failure to mesh one body costs the audit that body's proofs, never the view. Running out of memory and
        // cancellation are not failures of the body.
        catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException)) { failed = true; return null; }
    }

    private static Placed? Find(GeometryBase geometry, PreparedLeaf source, double tolerance, double band, CancellationToken token)
    {
        // The definition stands for this body only if the body is the leaf's own placed geometry. A copy of the leaf
        // with other geometry (a glass substitute made with `with`) keeps Canonical and Placement but not this.
        if (ReferenceEquals(geometry, source.Geometry) && ReferenceEquals(geometry, source.KernelClassifiedGeometry) && source.Canonical is { } canonical && source.Placement is { } placement)
        {
            double local = band / canonical.Scale, localTolerance = tolerance / canonical.Scale;
            string key = FormattableString.Invariant($"{source.DefinitionKey ?? canonical.Key.Split("|scale=")[0]}/certified-mesh-v1/{localTolerance:R}/{local:R}");
            var lease = (source.MeshCache ?? SceneCache.Shared).Mesh(key, () => { using var definition = canonical.CopyLocal(); return Build(definition, localTolerance, local, token); });
            return lease is null ? null : new(lease.Value, placement, canonical.Scale, lease);
        }
        var table = ByObject.GetOrCreateValue(geometry);
        lock (table)
        {
            // Build throws on failure, before anything is stored.
            if (!table.TryGetValue((tolerance, band), out var mesh)) table[(tolerance, band)] = mesh = Build(geometry, tolerance, band, token);
            return mesh is null ? null : new(mesh, Transform.Identity, 1, null);
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

    /// <summary>Null means the body has nothing to mesh (no faces, or Rhino returned no mesh for it). A failure throws.</summary>
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
                GridAmplification = 1, GridMinCount = 16, GridMaxCount = 0, RefineAngle = 0, RefineGrid = true, SimplePlanes = true, JaggedSeams = false, DoublePrecision = true,
                // Texture coordinates that are the surface's own parameters over its whole domain, not packed per face.
                TextureRange = MeshingParameterTextureRange.UnpackedUnscaledNormalized
            };
            var watch = System.Diagnostics.Stopwatch.StartNew(); void Lap(int k) { lock (BuildSeconds) BuildSeconds[k] += watch.Elapsed.TotalSeconds; watch.Restart(); }
            var meshes = Mesh.CreateFromBrep(brep, parameters); Lap(0);
            if (meshes is null) return null;
            try
            {
                if (meshes.Length != brep.Faces.Count) return null;
                var lookup = new Dictionary<Point3d, int>(); var vertices = new List<Point3d>(); var triangles = new List<int>();
                var bands = new List<double>(); var flat = new List<bool>(); int uncertified = 0;
                for (int f = 0; f < meshes.Length; f++)
                {
                    token.ThrowIfCancellationRequested();
                    var mesh = meshes[f]; var face = brep.Faces[f]; mesh.Faces.ConvertQuadsToTriangles();
                    bool planar = face.IsPlanar(tolerance);
                    // Parameters are the face's own, or those of its NURBS form where the two differ (a revolved surface).
                    Surface? form = null; var certificate = planar ? null : Certificate(face, tolerance, band * CellBands, out form);
                    using var keep = form; Surface pull = form ?? face; Lap(1);
                    int count = mesh.Vertices.Count; bool textured = mesh.TextureCoordinates.Count == count; Interval domainU = face.Domain(0), domainV = face.Domain(1); var map = new int[count]; var uv = new Point2d[count]; var off = new double[count]; var pole = new byte[count];
                    for (int v = 0; v < count; v++)
                    {
                        var p = mesh.Vertices.Point3dAt(v);
                        if (!lookup.TryGetValue(p, out int w)) { w = vertices.Count; vertices.Add(p); lookup.Add(p, w); }
                        map[v] = w; uv[v] = new(double.NaN, double.NaN); off[v] = double.PositiveInfinity;
                        if (planar) continue;
                        // The mesh's own parameters where they are the surface's (checked by evaluating there: single
                        // precision costs a few parts in ten million of the domain, which goes into the band). Only a
                        // vertex they do not fit, or a face meshed in its NURBS form's parameters, is pulled back.
                        double pu = double.NaN, pv = double.NaN; Point3d on = Point3d.Unset; Vector3d[]? ders = null;
                        if (form is null && textured)
                        {
                            var tc = mesh.TextureCoordinates[v]; pu = domainU.ParameterAt(tc.X); pv = domainV.ParameterAt(tc.Y);
                            if (!pull.Evaluate(pu, pv, 1, out on, out ders) || !(on.DistanceTo(p) <= Math.Min(band, tolerance))) ders = null;
                        }
                        if (ders is null) { Interlocked.Increment(ref _pulled); if (!pull.ClosestPoint(p, out pu, out pv) || !pull.Evaluate(pu, pv, 1, out on, out ders)) ders = null; }
                        if (ders is not null)
                        {
                            uv[v] = new(pu, pv); off[v] = on.DistanceTo(p);
                            // At a pole one parameter does not move the point: any value of it is this vertex.
                            double lu = ders[0].Length * pull.Domain(0).Length, lv = ders[1].Length * pull.Domain(1).Length;
                            if (lu <= 1e-9 * lv) pole[v] = 1; else if (lv <= 1e-9 * lu) pole[v] = 2;
                        }
                    }
                    Lap(2); int first = bands.Count; double measured = 0; var pending = new List<int>(); double measuring = 0;
                    for (int t = 0; t < mesh.Faces.Count; t++)
                    {
                        var mf = mesh.Faces[t]; int a = map[mf.A], b = map[mf.B], c = map[mf.C];
                        if (a == b || b == c || a == c) continue;
                        triangles.Add(a); triangles.Add(b); triangles.Add(c); flat.Add(planar);
                        Point2d qa = default, qb = default, qc = default;
                        double bound = planar ? 0 : double.NaN, u0 = double.NaN, u1 = double.NaN, v0 = double.NaN, v1 = double.NaN;
                        if (!planar)
                        {
                            qa = uv[mf.A]; qb = uv[mf.B]; qc = uv[mf.C];
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
                                var m0 = watch.Elapsed.TotalSeconds;
                                Point3d pa = mesh.Vertices.Point3dAt(mf.A), pb = mesh.Vertices.Point3dAt(mf.B), pc = mesh.Vertices.Point3dAt(mf.C);
                                foreach (var (x, y, z) in new[] { (1 / 3d, 1 / 3d, 1 / 3d), (.5, .5, 0d), (0d, .5, .5), (.5, 0d, .5) })
                                {   // with parameters: the surface point at the same parameters; without: the closest one
                                    var q = pa * x + pb * y + pc * z;
                                    measured = Math.Max(measured, !double.IsNaN(u0) ? pull.PointAt(qa.X * x + qb.X * y + qc.X * z, qa.Y * x + qb.Y * y + qc.Y * z).DistanceTo(q)
                                        : pull.ClosestPoint(q, out double mu, out double mv) ? pull.PointAt(mu, mv).DistanceTo(q) : band);
                                }
                                pending.Add(bands.Count); uncertified++; measuring += watch.Elapsed.TotalSeconds - m0;
                            }
                        }
                        bands.Add(bound);
                    }
                    foreach (int t in pending) bands[t] = Math.Max(band, 1.25 * measured);
                    lock (BuildSeconds) { BuildSeconds[3] += watch.Elapsed.TotalSeconds - measuring; BuildSeconds[4] += measuring; } watch.Restart();
                }
                // Flat faces: as loose as the curved triangles they touch.
                var reach = new double[vertices.Count];
                for (int t = 0; t < bands.Count; t++) if (!flat[t]) for (int k = 0; k < 3; k++) reach[triangles[t * 3 + k]] = Math.Max(reach[triangles[t * 3 + k]], bands[t]);
                for (int t = 0; t < bands.Count; t++) if (flat[t]) bands[t] = Math.Max(reach[triangles[t * 3]], Math.Max(reach[triangles[t * 3 + 1]], reach[triangles[t * 3 + 2]]));
                return new()
                {
                    Vertices = vertices.ToArray(), Triangles = triangles.ToArray(), Bands = bands.ToArray(), Uncertified = uncertified, Band = band, Box = new BoundingBox(vertices), Widest = bands.Count == 0 ? 0 : bands.Max()
                };
            }
            finally { foreach (var mesh in meshes) mesh.Dispose(); }
        }
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
