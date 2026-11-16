using System.Diagnostics;
using M2DPlus.Core;
using M2DPlus.Core.Visibility;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;

namespace M2DPlus.Rhino.Services;

/// <summary>A deterministic work cap, not a wall-clock cutoff (which would change output between runs).</summary>
public sealed record NativeVerificationOptions(int MaxRayTests = 4096, int MaxSamples = 16384, double ViewSpacingFraction = 1.0 / 128);

/// <summary>
/// Independent ray audit of native confidence. Samples can veto confidence, never prove a whole span.
/// Borrows only owned, post-glass/post-trim geometry for the duration of one view.
/// </summary>
internal sealed class NativeVisibilityVerifier : IDisposable
{
    internal readonly record struct Span(double A, double B, LineClass Class);
    private sealed record Body(GeometryBase Geometry, BoundingBox Bounds, string Source);
    private readonly List<Body> _bodies = [];
    private readonly List<GeometryBase> _owned = [];
    private readonly RTree _tree = new();
    private readonly EdgeFaceKernel.PointQuery? _planes;
    private readonly Plane _plane;
    private readonly double _tol, _spacing;
    private readonly NativeVerificationOptions _options;
    private readonly ViewDrawing _output;
    private readonly Stopwatch _clock = new();
    private int _tests, _samples, _skipped, _unknown, _visible, _hidden, _pieces, _spans;

    internal NativeVisibilityVerifier(IReadOnlyList<VisibilityInput> inputs, ViewDrawing output,
        NativeVerificationOptions options, CancellationToken token, PlanarExtraction? extraction = null)
    {
        if (options.MaxRayTests < 0 || options.MaxSamples < 0 || !double.IsFinite(options.ViewSpacingFraction) || options.ViewSpacingFraction <= 0)
            throw new ArgumentOutOfRangeException(nameof(options));
        _output = output; _plane = output.Frame.Plane; _tol = output.Tolerance; _options = options;
        _spacing = Math.Max(8 * _tol, output.Frame.BoundingBox.Diagonal.Length * options.ViewSpacingFraction);
        _clock.Start();
        try
        {
            var faces = extraction ?? new PlanarExtraction();
            var xy = Transform.PlaneToPlane(_plane, Plane.WorldXY);
            foreach (var input in VisibilityInputOrder.Canonical(inputs, i => i))
            {
                token.ThrowIfCancellationRequested();
                // A separate extraction deliberately does not reuse the native classification.
                var local = new PlanarExtraction();
                if (extraction is null && local.Add(input.Geometry, Transform.Identity, _plane, _tol, input.Source.Identity, token, nativeMeshCandidates: true))
                {
                    if (extraction is null) faces.Faces.AddRange(local.Faces);
                    continue;
                }
                GeometryBase geometry = input.Geometry;
                if (geometry is Extrusion extrusion) { geometry = extrusion.ToBrep(); _owned.Add(geometry); }
                else if (geometry is Surface surface) { geometry = surface.ToBrep(); _owned.Add(geometry); }
                if (geometry is not (Brep or Mesh)) continue;
                var bounds = geometry.GetBoundingBox(xy); bounds.Inflate(5 * _tol);
                int id = _bodies.Count; _bodies.Add(new(geometry, bounds, input.Source.Identity));
                _tree.Insert(new BoundingBox(new Point3d(bounds.Min.X, bounds.Min.Y, -1), new Point3d(bounds.Max.X, bounds.Max.Y, 1)), id);
            }
            _planes = new(faces.Faces, _tol, token);
        }
        catch { Dispose(); throw; }
        finally { _clock.Stop(); }
    }

    private Vertex Project(Point3d p)
    {
        var d = p - _plane.Origin; return new(d * _plane.XAxis, d * _plane.YAxis, d * _plane.ZAxis);
    }

    internal VisibilityClass Check(Point3d point, CancellationToken token, out string witness)
    {
        token.ThrowIfCancellationRequested(); witness = "clear";
        if (++_samples > _options.MaxSamples) { witness = "sample budget"; _skipped++; return VisibilityClass.Unresolved; }
        var p = Project(point);
        var planar = _planes?.Classify(p, token) ?? VisibilityClass.Unresolved;
        if (planar == VisibilityClass.Hidden) { witness = "exact planar hit; gap > tolerance"; return planar; }
        bool uncertain = planar == VisibilityClass.Unresolved;
        var ids = new List<int>();
        _tree.Search(new BoundingBox(new Point3d(p.X,p.Y,-1),new Point3d(p.X,p.Y,1)), (_, a) => ids.Add(a.Id));
        ids.Sort();
        foreach (int id in ids)
        {
            token.ThrowIfCancellationRequested(); var body = _bodies[id];
            if (body.Bounds.Max.Z <= p.Z + _tol) continue;
            if (_tests + 5 > _options.MaxRayTests) { _skipped++; witness = "ray budget"; return VisibilityClass.Unresolved; }
            double length = body.Bounds.Max.Z - p.Z + 8 * _tol;
            var central = Cast(body.Geometry,point,length,out double gap);
            // HLD curves and native intersections are tolerance approximations.
            // A grazing ray (including an endpoint on a silhouette) is not a
            // definite contradiction. Require the same result on a cross of
            // rays outside the fitting tolerance before vetoing confidence.
            bool stable = central != VisibilityClass.Unresolved;
            foreach (var offset in new[] {_plane.XAxis * (4*_tol), _plane.XAxis * (-4*_tol), _plane.YAxis * (4*_tol), _plane.YAxis * (-4*_tol)})
            {
                token.ThrowIfCancellationRequested();
                if (Cast(body.Geometry,point+offset,length,out _) != central) stable = false;
            }
            if (stable && central == VisibilityClass.Hidden)
            { witness = FormattableString.Invariant($"body={body.Source}; gap={gap:R}; tolerance-offset rays agree"); return VisibilityClass.Hidden; }
            if (!stable) uncertain = true;
        }
        if (uncertain) { _unknown++; witness = "intersection/contact margin uncertain"; return VisibilityClass.Unresolved; }
        return VisibilityClass.Visible;
    }

    private VisibilityClass Cast(GeometryBase geometry, Point3d point, double length, out double gap)
    {
        _tests++; gap = 0;
        if (geometry is Mesh mesh)
        {
            double distance = Intersection.MeshRay(mesh,new Ray3d(point+_plane.ZAxis*_tol,_plane.ZAxis));
            if (!double.IsFinite(distance)) return VisibilityClass.Unresolved;
            gap = distance + _tol;
            return distance < 0 ? VisibilityClass.Visible : gap > 4*_tol ? VisibilityClass.Hidden : VisibilityClass.Unresolved;
        }
        using var ray = new LineCurve(point,point+_plane.ZAxis*length);
        bool success = Intersection.CurveBrep(ray,(Brep)geometry,_tol,out var overlaps,out var hits);
        try
        {
            if (!success || hits.Any(h => !h.IsValid) || overlaps.Any(c => !c.IsValid)) return VisibilityClass.Unresolved;
            gap = hits.Select(h => (h-point)*_plane.ZAxis).DefaultIfEmpty(0).Max();
            if (gap > 4*_tol) return VisibilityClass.Hidden;
            if (gap > _tol || overlaps.Any(c => (c.PointAtStart-point)*_plane.ZAxis > _tol || (c.PointAtEnd-point)*_plane.ZAxis > _tol)) return VisibilityClass.Unresolved;
            return VisibilityClass.Visible;
        }
        finally { foreach (var c in overlaps) c.Dispose(); }
    }

    internal Span[] Verify(Curve world, LineClass native, string source, CancellationToken token)
    {
        if (native is not (LineClass.Visible or LineClass.Hidden)) return [new(world.Domain.Min, world.Domain.Max, native)];
        _clock.Start();
        try
        {
            double required = Math.Max(4, Math.Ceiling(world.GetLength() / _spacing));
            if (!double.IsFinite(required) || required + 1 > _options.MaxSamples - _samples)
            { _skipped++; return [new(world.Domain.Min, world.Domain.Max, native)]; }
            var ts = world.DivideByCount((int)required, true);
            if (ts is null || ts.Length < 2) { _unknown++; return [new(world.Domain.Min, world.Domain.Max, LineClass.Unresolved)]; }
            var veto = new bool[ts.Length];
            for (int i = 0; i < ts.Length; i++)
            {
                var point = world.PointAt(ts[i]); var truth = Check(point, token, out string witness);
                bool contradiction = native == LineClass.Visible && truth == VisibilityClass.Hidden || native == LineClass.Hidden && truth == VisibilityClass.Visible;
                if (!contradiction) continue;
                veto[i] = true;
                if (native == LineClass.Visible) _visible++; else _hidden++;
                _output._diagnostics.Add(FormattableString.Invariant($"Native ray contradiction: source={source}; native={native}; t={ts[i]:R}; world=({point.X:R},{point.Y:R},{point.Z:R}); {witness}. Sample neighbourhood -> Unresolved (not a span proof)."));
            }
            if (!veto.Any(v => v)) return [new(world.Domain.Min, world.Domain.Max, native)];
            _pieces++;
            var spans = new List<Span>();
            for (int i = 0; i < ts.Length; i++)
            {
                double a = i == 0 ? world.Domain.Min : (ts[i-1]+ts[i])/2;
                double b = i == ts.Length-1 ? world.Domain.Max : (ts[i]+ts[i+1])/2;
                var kind = veto[i] ? LineClass.Unresolved : native;
                if (spans.Count > 0 && spans[^1].Class == kind) spans[^1] = spans[^1] with { B = b };
                else spans.Add(new(a,b,kind));
            }
            _spans += spans.Count(s => s.Class == LineClass.Unresolved);
            return spans.ToArray();
        }
        finally { _clock.Stop(); }
    }

    internal void Report()
    {
        // Keep timing out of deterministic diagnostics / output signatures.
        _output.Phases.Add("native ray audit work",_clock.Elapsed.TotalSeconds);
        _output._diagnostics.Add(FormattableString.Invariant(
            $"Native ray verifier: wrong-visible samples={_visible}; false-hidden samples={_hidden}; contradicted pieces={_pieces}; unresolved spans={_spans}; samples={_samples}; ray tests={_tests}/{_options.MaxRayTests}; skipped={_skipped}; uncertain samples={_unknown}; spacing<={_spacing:R}. Sampling is an audit, not a visibility certificate; skipped/uncertain and unsampled interiors retain native confidence with this Info warning."));
    }

    internal static void Apply(IReadOnlyList<VisibilityInput> inputs, ViewDrawing output, NativeSettings settings, CancellationToken token)
    {
        if (settings.NativeOnly) return; // explicit raw comparator
        if (settings.Verification.MaxSamples == 0)
        { output._diagnostics.Add("Native ray verifier disabled by sample budget; native confidence unverified."); return; }
        using var verifier = new NativeVisibilityVerifier(inputs, output, settings.Verification, token);
        foreach (var piece in output.Pieces.ToArray())
        {
            token.ThrowIfCancellationRequested();
            if (piece.KernelOwned || piece.DepthGeometry is not Curve world || piece.Class is not (LineClass.Visible or LineClass.Hidden)) continue;
            var spans = verifier.Verify(world, piece.Class, piece.SourceIdentity, token);
            if (spans.Length == 1 && spans[0].Class == piece.Class) continue;
            using var geometry = piece.CopyGeometry();
            if (geometry is not Curve projected) continue;
            var source = inputs.First(i => i.Source.Identity == piece.SourceIdentity).Source;
            foreach (var span in spans)
            {
                var a = projected.Trim(span.A,span.B); var b = world.Trim(span.A,span.B);
                if (a is null || b is null) { a?.Dispose(); b?.Dispose(); throw new InvalidOperationException("Native verifier span trim failed"); }
                output.Pieces.Add(new(a,source,span.Class,b) { Profile = piece.Profile, FarCandidate = piece.FarCandidate });
            }
            output.Pieces.Remove(piece); piece.Dispose();
        }
        verifier.Report();
    }

    public void Dispose() { _tree.Dispose(); foreach (var g in _owned) g.Dispose(); }
}
