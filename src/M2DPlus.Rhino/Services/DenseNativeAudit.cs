using System.Diagnostics;
using M2DPlus.Core;
using M2DPlus.Core.Visibility;
using Rhino.Geometry;

namespace M2DPlus.Rhino.Services;

/// <summary>Deterministic limits of the dense audit. Internal: there is no user setting.</summary>
internal sealed record DenseAuditOptions(double BandTolerances = 10, double FloorTolerances = 5, long MaxTriangles = 8_000_000, int Coarsenings = 3);

/// <summary>
/// Dense audit of native Visible lines (mesh tier, step C). Every curved body native HLD saw becomes a certified
/// mesh (<see cref="CertifiedMesh"/>); a stretch of a native Visible curve that the meshes prove Hidden
/// (<see cref="BandedMeshIndex"/>) is moved to Unresolved. It never makes a line Hidden or Visible: the bands of
/// triangles without a control-net bound are measured, and the trimmed rim of a face is taken to follow its mesh,
/// so a proof here is evidence against native's confidence, not yet a certificate for ours.
///
/// Unlike the sampled ray audit it looks at every chord of every Visible curve, and it sees cover that is thicker
/// than a tolerance, however thin. It says nothing where a curve is within a band of a surface or of an open
/// boundary, and nothing about native Hidden lines.
///
/// The band starts at <see cref="DenseAuditOptions.BandTolerances"/> model tolerances and doubles, up to
/// <see cref="DenseAuditOptions.Coarsenings"/> times, while the view's triangles exceed
/// <see cref="DenseAuditOptions.MaxTriangles"/>; past that the view is not audited and Info says so. That depends on
/// the scene alone, never on time or memory at hand.
/// </summary>
internal sealed class DenseNativeAudit
{
    private readonly BandedMeshIndex? _index;
    private readonly Plane _plane; private readonly double _tol, _band, _chord;
    private readonly int _sheets, _uncertified; private readonly long _triangles;
    private readonly string? _skipped;
    private readonly Stopwatch _clock = new(); private double _setup;
    // Working storage is as large as the index: kept between curves, one per thread that has been used.
    private readonly System.Collections.Concurrent.ConcurrentBag<BandedMeshIndex.Scratch> _scratch = new();
    private int _curves, _spans, _uncut; private double _moved, _looked;

    internal bool Active => _index is not null;

    internal DenseNativeAudit(IReadOnlyList<VisibilityInput> inputs, ViewDrawing output, DenseAuditOptions options, CancellationToken token)
    {
        _plane = output.Frame.Plane; _tol = output.Tolerance; _chord = Math.Max(64 * _tol, output.Frame.BoundingBox.Diagonal.Length / 256);
        _clock.Start();
        try
        {
            var ordered = VisibilityInputOrder.Canonical(inputs, i => i);
            double band = options.BandTolerances * _tol; List<(CertifiedMesh Mesh, Transform Placement, double Scale)>? meshes = null; var plain = new List<Mesh>();
            foreach (var input in ordered) if (input.Geometry is Mesh mesh) plain.Add(mesh);
            long plainTriangles = plain.Sum(m => (long)m.Faces.Count + m.Faces.QuadCount);
            for (int attempt = 0; ; attempt++, band *= 2)
            {
                var placed = new List<(CertifiedMesh, Transform, double)>(); long count = plainTriangles;
                foreach (var input in ordered)
                {
                    token.ThrowIfCancellationRequested();
                    if (CertifiedMesh.For(input.Geometry, input.Source, _tol, band, token) is not { } found) continue;
                    placed.Add(found); count += found.Mesh.Count;
                }
                if (count <= options.MaxTriangles) { meshes = placed; _triangles = count; break; }
                if (attempt >= options.Coarsenings) { _skipped = FormattableString.Invariant($"{count} triangles at a band of {band:R} exceed the limit of {options.MaxTriangles}"); return; }
            }
            _band = band; double floor = options.FloorTolerances * _tol;
            var sheets = new List<BandedSheet>(meshes.Count + plain.Count);
            foreach (var (mesh, placement, scale) in meshes) { sheets.Add(mesh.Place(placement, scale, _plane, sheets.Count, floor)); _uncertified += mesh.Uncertified; }
            foreach (var mesh in plain)
            {   // A mesh is its own surface: its triangles are exact. Quads are split as Rhino splits them.
                token.ThrowIfCancellationRequested();
                var vertices = new Vertex[mesh.Vertices.Count];
                for (int i = 0; i < vertices.Length; i++) { var d = mesh.Vertices.Point3dAt(i) - _plane.Origin; vertices[i] = new(d * _plane.XAxis, d * _plane.YAxis, d * _plane.ZAxis); }
                var triangles = new List<int>(mesh.Faces.Count * 3);
                foreach (var face in mesh.Faces)
                {
                    triangles.Add(face.A); triangles.Add(face.B); triangles.Add(face.C);
                    if (face.IsQuad) { triangles.Add(face.A); triangles.Add(face.C); triangles.Add(face.D); }
                }
                // A non-planar quad is not its two triangles: such a mesh gets no say.
                if (mesh.Faces.QuadCount > 0 && !PlanarQuads(mesh)) continue;
                var bands = new double[triangles.Count / 3]; Array.Fill(bands, floor);
                sheets.Add(new() { Id = sheets.Count, Vertices = vertices, Triangles = triangles.ToArray(), Bands = bands });
            }
            _sheets = sheets.Count;
            if (sheets.Count > 0) _index = new(sheets, token);
        }
        finally { _clock.Stop(); _setup = _clock.Elapsed.TotalSeconds; }
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

    /// <summary>
    /// Parameter intervals of a native world curve that the meshes prove Hidden, in order and merged. The curve is
    /// cut into pieces whose points stay within half a tolerance of the straight piece at the same fraction; the
    /// candidate's error given to the index is that plus one tolerance for the curve itself.
    /// </summary>
    internal List<(double A, double B)> ProvenHidden(Curve world, CancellationToken token)
    {
        var result = new List<(double, double)>(); if (_index is null) return result;
        _clock.Start();
        try
        {
            var chords = new List<(double A, double B, Vertex Pa, Vertex Pb)>();
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
                    if (fine) chords.Add((a, b, Project(pa), Project(pb)));
                    else { stack.Push(((a + b) / 2, b, depth + 1)); stack.Push((a, (a + b) / 2, depth + 1)); }
                }
            }
            var pieces = new List<BandedMeshIndex.Piece>[chords.Count];
            double error = 1.5 * _tol, least = Math.Max(_band / 2, 2 * _tol);
            Parallel.For(0, chords.Count, new ParallelOptions { MaxDegreeOfParallelism = Math.Min(Environment.ProcessorCount, 8), CancellationToken = token },
                () => _scratch.TryTake(out var held) ? held : new BandedMeshIndex.Scratch(_index), (i, _, scratch) =>
                {
                    var list = new List<BandedMeshIndex.Piece>(2); _index.Classify(chords[i].Pa, chords[i].Pb, _tol, error, least, scratch, list); pieces[i] = list; return scratch;
                }, _scratch.Add);
            for (int i = 0; i < chords.Count; i++)
                foreach (var piece in pieces[i])
                {
                    if (piece.Class != VisibilityClass.Hidden) continue;
                    double a = chords[i].A + (chords[i].B - chords[i].A) * piece.T0, b = chords[i].A + (chords[i].B - chords[i].A) * piece.T1;
                    if (result.Count > 0 && a <= result[^1].Item2) result[^1] = (result[^1].Item1, b); else result.Add((a, b));
                }
            // A stretch too short to draw as a piece of its own is not worth a cut.
            result.RemoveAll(r => world.GetLength(new Interval(r.Item1, r.Item2)) < 8 * _tol);
            _curves++; _looked += world.GetLength();
            return result;
        }
        finally { _clock.Stop(); }
    }

    /// <summary>The whole domain as alternating spans: proven stretches Unresolved, the rest as native had it.</summary>
    internal List<(double A, double B, LineClass Class)> Spans(Curve world, LineClass native, CancellationToken token)
    {
        var spans = new List<(double, double, LineClass)>(); var domain = world.Domain;
        if (native != LineClass.Visible) { spans.Add((domain.Min, domain.Max, native)); return spans; }
        double at = domain.Min;
        foreach (var (a, b) in ProvenHidden(world, token))
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
        output._diagnostics.Add(_skipped is not null
            ? $"Dense native audit not run: {_skipped}. Native Visible lines keep the sampled audit only."
            : FormattableString.Invariant($"Dense native audit: native Visible proved Hidden by certified meshes and moved to Unresolved: spans={_spans}; world length={_moved:R}; Visible curves looked at={_curves} (length={_looked:R}); bodies={_sheets}; triangles={_triangles}; band={_band:R}; triangles with a measured band={_uncertified}; curves left whole because a stretch could not be cut out={_uncut}. It moves lines to Unresolved only and proves nothing about Hidden lines."));
    }

    /// <summary>The native fallback route: audits the drawing's native Visible pieces in place.</summary>
    internal static void Apply(IReadOnlyList<VisibilityInput> inputs, ViewDrawing output, NativeSettings settings, CancellationToken token)
    {
        if (!settings.DenseAudit || settings.NativeOnly) return;
        var audit = new DenseNativeAudit(inputs, output, settings.DenseAuditOptions, token);
        if (audit.Active)
            foreach (var piece in output.Pieces.ToArray())
            {
                token.ThrowIfCancellationRequested();
                if (piece.KernelOwned || piece.DepthGeometry is not Curve world || piece.Class != LineClass.Visible) continue;
                var spans = audit.Spans(world, piece.Class, token);
                if (spans.Count == 1 && spans[0].Class == piece.Class) continue;
                using var geometry = piece.CopyGeometry();
                if (geometry is not Curve projected) continue;
                var source = inputs.First(i => i.Source.Identity == piece.SourceIdentity).Source; var parts = new List<DrawingPiece>();
                foreach (var (a, b, kind) in spans)
                {
                    var flat = projected.Trim(a, b); var deep = world.Trim(a, b);
                    if (flat is null || deep is null) { flat?.Dispose(); deep?.Dispose(); foreach (var p in parts) p.Dispose(); parts = null; audit._uncut++; break; }
                    parts.Add(new(flat, source, kind, deep) { Profile = piece.Profile, FarCandidate = piece.FarCandidate });
                    if (kind == LineClass.Unresolved) { audit.Moved(deep); settings.ObserveDenseAuditSpan?.Invoke(deep); }
                }
                // A span that cannot be cut out leaves the whole piece as it was: the audit only ever adds doubt.
                if (parts is null) continue;
                output.Pieces.AddRange(parts); output.Pieces.Remove(piece); piece.Dispose();
            }
        audit.Report(output);
    }
}
