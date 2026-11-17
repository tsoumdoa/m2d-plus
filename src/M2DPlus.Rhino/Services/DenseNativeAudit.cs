using System.Diagnostics;
using M2DPlus.Core;
using M2DPlus.Core.Visibility;
using Rhino.Geometry;

namespace M2DPlus.Rhino.Services;

/// <summary>Deterministic limits of the dense audit. Internal: there is no user setting.</summary>
/// <param name="BandTolerances">The band asked of the certified meshes, in model tolerances.</param>
/// <param name="FloorTolerances">The least band used, in model tolerances.</param>
/// <param name="StripTriangles">Triangles aimed at per strip of the drawing; one strip is indexed at a time.</param>
/// <param name="MaxStripTriangles">A strip that needs more than this is not audited.</param>
internal sealed record DenseAuditOptions(double BandTolerances = 10, double FloorTolerances = 5, long StripTriangles = 4_000_000, long MaxStripTriangles = 24_000_000);

/// <summary>
/// Dense audit of native Visible lines (mesh tier, step C). Every curved body native HLD saw becomes a certified
/// mesh (<see cref="CertifiedMesh"/>); a stretch of a native Visible curve that the meshes prove Hidden
/// (<see cref="BandedMeshIndex"/>) is moved to Unresolved. It never makes a line Hidden or Visible: the bands of
/// triangles without a control-net bound are measured, and the trimmed rim of a face is taken to follow its mesh,
/// so a proof here is evidence against native's confidence, not yet a certificate for ours.
///
/// Unlike the sampled ray audit it looks at every chord of every Visible curve, and it sees cover that is thicker
/// than a tolerance, however thin. The body a curve lies on is a body like any other, so a line hidden by its own
/// body is found too. It says nothing where a curve is within a band of a surface or of an open boundary, and
/// nothing about native Hidden lines.
///
/// Memory is bounded by working through the drawing in vertical strips. A strip holds every body whose extent
/// reaches it, whole (the proof needs whole bodies), and the chords whose middle lies in it; strips are cut so that
/// each holds about <see cref="DenseAuditOptions.StripTriangles"/> triangles. A strip that would need more than
/// <see cref="DenseAuditOptions.MaxStripTriangles"/> is left unaudited and Info says how much of the drawing that
/// is. Both depend on the scene alone, never on time or memory at hand.
/// </summary>
internal sealed class DenseNativeAudit
{
    private sealed record Body(CertifiedMesh? Mesh, Transform Placement, double Scale, Mesh? Plain, double X0, double X1, long Triangles);
    private readonly record struct Chord(int Curve, double A, double B, Vertex Pa, Vertex Pb);

    private readonly List<Body> _bodies = [];
    private readonly Plane _plane; private readonly double _tol, _band, _floor, _chord;
    private readonly DenseAuditOptions _options;
    private readonly Dictionary<Curve, List<(double A, double B)>> _proven = new(ReferenceEqualityComparer.Instance);
    private readonly Stopwatch _clock = new(); private double _setup;
    private readonly long _triangles; private readonly int _uncertified;
    private int _curves, _spans, _uncut, _strips, _skippedStrips; private double _moved, _looked, _skippedLength; private long _largest;

    internal bool Active => _bodies.Count > 0;

    internal DenseNativeAudit(IReadOnlyList<VisibilityInput> inputs, ViewDrawing output, DenseAuditOptions options, CancellationToken token)
    {
        _plane = output.Frame.Plane; _tol = output.Tolerance; _options = options; _band = options.BandTolerances * _tol; _floor = options.FloorTolerances * _tol;
        _chord = Math.Max(64 * _tol, output.Frame.BoundingBox.Diagonal.Length / 256);
        _clock.Start();
        try
        {
            foreach (var input in VisibilityInputOrder.Canonical(inputs, i => i))
            {
                token.ThrowIfCancellationRequested();
                if (input.Geometry is Mesh plain)
                {   // A mesh is its own surface: its triangles are exact. A non-planar quad is not its two triangles: no say.
                    if (plain.Faces.Count == 0 || plain.Faces.QuadCount > 0 && !PlanarQuads(plain)) continue;
                    var (x0, x1) = Extent(plain.GetBoundingBox(false), Transform.Identity);
                    _bodies.Add(new(null, Transform.Identity, 1, plain, x0, x1, plain.Faces.Count + plain.Faces.QuadCount));
                }
                else if (CertifiedMesh.For(input.Geometry, input.Source, _tol, _band, token) is { } found && found.Mesh.Count > 0)
                {
                    var (x0, x1) = Extent(found.Mesh.Box, found.Placement);
                    _bodies.Add(new(found.Mesh, found.Placement, found.Scale, null, x0, x1, found.Mesh.Count)); _uncertified += found.Mesh.Uncertified;
                }
            }
            _triangles = _bodies.Sum(b => b.Triangles);
        }
        finally { _clock.Stop(); _setup = _clock.Elapsed.TotalSeconds; }
    }

    /// <summary>The drawing X range of a box under a placement.</summary>
    private (double, double) Extent(BoundingBox box, Transform placement)
    {
        double x0 = double.MaxValue, x1 = double.MinValue;
        foreach (var corner in box.GetCorners()) { var p = corner; p.Transform(placement); double x = (p - _plane.Origin) * _plane.XAxis; x0 = Math.Min(x0, x); x1 = Math.Max(x1, x); }
        return (x0, x1);
    }

    private bool PlanarQuads(Mesh mesh)
    {
        foreach (var face in mesh.Faces)
        {
            if (!face.IsQuad) continue;
            Point3d a = mesh.Vertices.Point3dAt(face.A), b = mesh.Vertices.Point3dAt(face.B), c = mesh.Vertices.Point3dAt(face.C), d = mesh.Vertices.Point3dAt(face.D);
            var normal = Vector3d.CrossProduct(b - a, c - a);
            if (!normal.Unitize() || Math.Abs((d - a) * normal) > _tol / 16) return false;
        }
        return true;
    }

    private Vertex Project(Point3d p) { var d = p - _plane.Origin; return new(d * _plane.XAxis, d * _plane.YAxis, d * _plane.ZAxis); }

    private BandedSheet Sheet(Body body, int id)
    {
        if (body.Mesh is not null) return body.Mesh.Place(body.Placement, body.Scale, _plane, id, _floor);
        var mesh = body.Plain!; var vertices = new Vertex[mesh.Vertices.Count];
        for (int i = 0; i < vertices.Length; i++) vertices[i] = Project(mesh.Vertices.Point3dAt(i));
        var triangles = new List<int>(mesh.Faces.Count * 3);
        foreach (var face in mesh.Faces)
        {
            triangles.Add(face.A); triangles.Add(face.B); triangles.Add(face.C);
            if (face.IsQuad) { triangles.Add(face.A); triangles.Add(face.C); triangles.Add(face.D); }
        }
        var bands = new double[triangles.Count / 3]; Array.Fill(bands, _floor);
        return new() { Id = id, Vertices = vertices, Triangles = triangles.ToArray(), Bands = bands };
    }

    /// <summary>
    /// Works out, for all the curves at once, which stretches the meshes prove Hidden. Each curve is cut into pieces
    /// whose points stay within half a tolerance of the straight piece at the same fraction; the candidate's error
    /// given to the index is that plus one tolerance for the curve itself.
    /// </summary>
    internal void Prepare(IReadOnlyList<Curve> curves, CancellationToken token)
    {
        if (!Active) return;
        _clock.Start();
        try
        {
            var chords = new List<Chord>(); var fresh = new List<Curve>();
            foreach (var world in curves)
            {
                if (_proven.ContainsKey(world)) continue;
                int index = fresh.Count; fresh.Add(world); _proven[world] = []; _curves++; _looked += world.GetLength();
                for (int span = 0; span < world.SpanCount; span++)
                {
                    token.ThrowIfCancellationRequested();
                    var domain = world.SpanDomain(span); var stack = new Stack<(double A, double B, int Depth)>(); stack.Push((domain.Min, domain.Max, 0));
                    while (stack.Count > 0)
                    {
                        var (a, b, depth) = stack.Pop(); if (!(b > a)) continue;
                        Point3d pa = world.PointAt(a), pb = world.PointAt(b); bool fine = pa.DistanceTo(pb) <= _chord || depth >= 40;
                        if (fine && depth < 40)
                            foreach (double k in new[] { .5, .25, .75 })
                                if (world.PointAt(a + (b - a) * k).DistanceTo(pa + (pb - pa) * k) > _tol / 2) { fine = false; break; }
                        if (fine) chords.Add(new(index, a, b, Project(pa), Project(pb)));
                        else { stack.Push(((a + b) / 2, b, depth + 1)); stack.Push((a, (a + b) / 2, depth + 1)); }
                    }
                }
            }
            if (chords.Count == 0) return;
            double error = 1.5 * _tol, least = Math.Max(_band / 2, 2 * _tol);
            // Strips by the chords' middles, cut where the triangles of the bodies whose centre has been passed add up.
            static double Middle(in Chord c) => (c.Pa.X + c.Pb.X) / 2;
            var order = Enumerable.Range(0, chords.Count).OrderBy(i => Middle(chords[i])).ThenBy(i => i).ToArray();
            var cuts = new List<double>(); long running = 0;
            foreach (var body in _bodies.OrderBy(b => b.X0 + b.X1)) { running += body.Triangles; if (running >= _options.StripTriangles) { cuts.Add((body.X0 + body.X1) / 2); running = 0; } }
            cuts.Add(double.PositiveInfinity);
            // A chord's triangles are within half its length, its error and a band of its middle. Bands are not bounded
            // from above, so every body's extent is widened by its own largest band when it is matched to a strip.
            double reach = _chord / 2 + error;
            var pieces = new List<BandedMeshIndex.Piece>?[chords.Count]; int at = 0;
            foreach (double to in cuts)
            {
                token.ThrowIfCancellationRequested();
                int first = at; while (at < order.Length && Middle(chords[order[at]]) <= to) at++;
                if (at == first) continue;
                double lo = Middle(chords[order[first]]) - reach, hi = Middle(chords[order[at - 1]]) + reach;
                var members = _bodies.Where(b => b.X1 + Widest(b) >= lo && b.X0 - Widest(b) <= hi).ToArray(); long count = members.Sum(b => b.Triangles);
                _strips++; _largest = Math.Max(_largest, count);
                if (count > _options.MaxStripTriangles)
                {
                    _skippedStrips++;
                    for (int k = first; k < at; k++) { var c = chords[order[k]]; _skippedLength += fresh[c.Curve].GetLength(new Interval(c.A, c.B)); }
                    continue;
                }
                if (members.Length == 0) continue;
                var index = new BandedMeshIndex(members.Select((b, id) => Sheet(b, id)).ToArray(), token); var slice = order.AsMemory(first, at - first);
                Parallel.For(0, slice.Length, new ParallelOptions { MaxDegreeOfParallelism = Math.Min(Environment.ProcessorCount, 8), CancellationToken = token },
                    () => new BandedMeshIndex.Scratch(index), (k, _, scratch) =>
                    {
                        int i = slice.Span[k]; var list = new List<BandedMeshIndex.Piece>(2);
                        index.Classify(chords[i].Pa, chords[i].Pb, _tol, error, least, scratch, list); pieces[i] = list; return scratch;
                    }, _ => { });
            }
            for (int i = 0; i < chords.Count; i++)
            {
                if (pieces[i] is not { } list) continue;
                var result = _proven[fresh[chords[i].Curve]];
                foreach (var piece in list)
                {
                    if (piece.Class != VisibilityClass.Hidden) continue;
                    double a = chords[i].A + (chords[i].B - chords[i].A) * piece.T0, b = chords[i].A + (chords[i].B - chords[i].A) * piece.T1;
                    if (result.Count > 0 && a <= result[^1].B) result[^1] = (result[^1].A, b); else result.Add((a, b));
                }
            }
            // A stretch too short to draw as a piece of its own is not worth a cut.
            foreach (var world in fresh) _proven[world].RemoveAll(r => world.GetLength(new Interval(r.A, r.B)) < 8 * _tol);
        }
        finally { _clock.Stop(); }
    }

    private double Widest(Body body) => body.Mesh is null ? _floor : Math.Max(_floor, body.Mesh.Widest * body.Scale);

    /// <summary>The whole domain as alternating spans: proven stretches Unresolved, the rest as native had it.</summary>
    internal List<(double A, double B, LineClass Class)> Spans(Curve world, LineClass native, CancellationToken token)
    {
        var spans = new List<(double, double, LineClass)>(); var domain = world.Domain;
        if (native != LineClass.Visible || !Active) { spans.Add((domain.Min, domain.Max, native)); return spans; }
        if (!_proven.ContainsKey(world)) Prepare([world], token);
        double at = domain.Min;
        foreach (var (a, b) in _proven[world])
        {
            double a0 = Math.Max(a, at), b0 = Math.Min(b, domain.Max); if (!(b0 > a0)) continue;
            if (a0 > at) spans.Add((at, a0, native));
            spans.Add((a0, b0, LineClass.Unresolved)); at = b0;
        }
        if (at < domain.Max || spans.Count == 0) spans.Add((at, domain.Max, native));
        return spans;
    }

    /// <summary>Counts a stretch that was cut out and moved to Unresolved.</summary>
    internal void Moved(Curve part) { _spans++; _moved += part.GetLength(); }

    internal void Report(ViewDrawing output)
    {
        output.Phases.Add("dense native audit", _clock.Elapsed.TotalSeconds); output.Phases.Add("dense native audit meshes", _setup);
        output._diagnostics.Add(!Active
            ? "Dense native audit: no curved body to audit against."
            : FormattableString.Invariant($"Dense native audit: native Visible proved Hidden by certified meshes and moved to Unresolved: spans={_spans}; world length={_moved:R}; Visible curves looked at={_curves} (length={_looked:R}); bodies={_bodies.Count}; triangles={_triangles}; band={_band:R}; triangles with a measured band={_uncertified}; strips={_strips} (largest {_largest} triangles); strips over the limit and not audited={_skippedStrips} (length={_skippedLength:R}); curves left whole because a stretch could not be cut out={_uncut}. It moves lines to Unresolved only and proves nothing about Hidden lines."));
    }

    /// <summary>The native fallback route: audits the drawing's native Visible pieces in place.</summary>
    internal static void Apply(IReadOnlyList<VisibilityInput> inputs, ViewDrawing output, NativeSettings settings, CancellationToken token)
    {
        if (!settings.DenseAudit || settings.NativeOnly) return;
        var audit = new DenseNativeAudit(inputs, output, settings.DenseAuditOptions, token);
        if (audit.Active)
        {
            var candidates = output.Pieces.Where(p => !p.KernelOwned && p.DepthGeometry is Curve && p.Class == LineClass.Visible).ToArray();
            audit.Prepare(candidates.Select(p => (Curve)p.DepthGeometry!).ToArray(), token);
            foreach (var piece in candidates)
            {
                token.ThrowIfCancellationRequested();
                var world = (Curve)piece.DepthGeometry!; var spans = audit.Spans(world, piece.Class, token);
                if (spans.Count == 1 && spans[0].Class == piece.Class) continue;
                using var geometry = piece.CopyGeometry();
                if (geometry is not Curve projected) continue;
                var source = inputs.First(i => i.Source.Identity == piece.SourceIdentity).Source; List<(DrawingPiece Piece, Curve Deep)>? parts = [];
                foreach (var (a, b, kind) in spans)
                {
                    var flat = projected.Trim(a, b); var deep = world.Trim(a, b);
                    // A span that cannot be cut out leaves the whole piece as it was: the audit only ever adds doubt.
                    if (flat is null || deep is null) { flat?.Dispose(); deep?.Dispose(); foreach (var p in parts) p.Piece.Dispose(); parts = null; audit._uncut++; break; }
                    parts.Add((new(flat, source, kind, deep) { Profile = piece.Profile, FarCandidate = piece.FarCandidate }, deep));
                }
                if (parts is null) continue;
                foreach (var (part, deep) in parts)
                {
                    output.Pieces.Add(part);
                    if (part.Class == LineClass.Unresolved) { audit.Moved(deep); settings.ObserveDenseAuditSpan?.Invoke(deep); }
                }
                output.Pieces.Remove(piece); piece.Dispose();
            }
        }
        audit.Report(output);
    }
}
