using System.Diagnostics;
using M2DPlus.Core;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace M2DPlus.Rhino.Services;

public sealed record NativeSettings(bool MultipleThreads = false);
public sealed class DrawingPiece : IDisposable
{
    private readonly GeometryBase _geometry;
    private readonly int[] _path;
    public string SourceIdentity { get; }
    public int[] SourcePath => (int[])_path.Clone();
    internal ReadOnlySpan<int> Path => _path;
    public LineClass Class { get; internal set; }
    internal string GeometricKey { get; }
    internal DrawingPiece(GeometryBase geometry, PreparedLeaf source, LineClass classification)
    {
        _geometry = geometry; SourceIdentity = source.Identity; _path = (int[])source.Path.Clone(); Class = classification;
        var bounds = geometry.GetBoundingBox(true);
        GeometricKey = FormattableString.Invariant($"{bounds.Min.X:R},{bounds.Min.Y:R},{bounds.Min.Z:R},{bounds.Max.X:R},{bounds.Max.Y:R},{bounds.Max.Z:R}/") +
            GeometryFingerprint.Compute(geometry);
    }
    public GeometryBase CopyGeometry()
    {
        var copy = _geometry.Duplicate();
        try { copy.EnsurePrivateCopy(); return copy; }
        catch { copy.Dispose(); throw; }
    }
    public void Dispose() => _geometry.Dispose();
}
public sealed class ViewDrawing : IDisposable
{
    internal List<DrawingPiece> Pieces { get; } = new();
    public IReadOnlyList<DrawingPiece> Items => Pieces.AsReadOnly();
    public string Name { get; internal init; } = "";
    public string? Error { get; internal set; }
    public Rectangle3d Frame { get; internal set; }
    public QualityReport Report { get; internal set; } = new("Native HLD", 0, 0, new Dictionary<string, int>(), Array.Empty<string>(), false);
    public IReadOnlyList<string> Diagnostics => _diagnostics.AsReadOnly();
    internal readonly List<string> _diagnostics = new();
    public int HiddenPoints { get; internal set; }
    public int DuplicateSegments { get; internal set; }
    public int ProjectingSegments { get; internal set; }
    public double Seconds { get; internal set; }
    public string Info => $"{Name}: {(Error ?? "finished")} · {Seconds:F3} s\n" +
        $"{Report.Route}; deterministic={!MultipleThreads}; tolerance={Tolerance:R}; validation: {Report.ValidationDomain}\n" +
        $"Visible={Pieces.Count(p => p.Class == LineClass.Visible)}, hidden={Pieces.Count(p => p.Class == LineClass.Hidden)}, unresolved={Report.UncertainCount}, hidden points={HiddenPoints}; " +
        $"invalid extraction={Report.InvalidExtractionCount}; duplicate={DuplicateSegments}; projecting={ProjectingSegments}; quarantined={Report.Quarantined}\n" +
        $"Unsupported: {string.Join(", ", Report.UnsupportedByType.Select(p => $"{p.Key}={p.Value}"))}\n" +
        string.Join("; ", Report.Normalisation.Concat(Diagnostics)) +
        "\nM1b: Beyond, Far clip, Cut and Hatch are empty; Target placement is not in this build yet.";
    internal bool MultipleThreads;
    internal double Tolerance;
    public void Dispose() { foreach (var item in Pieces) item.Dispose(); Pieces.Clear(); }
}
public sealed class DrawingBatch : IDisposable
{
    public RequestIdentity Identity { get; }
    public IReadOnlyList<ViewDrawing> Views { get; }
    public bool Complete { get; }
    internal DrawingBatch(RequestIdentity identity, List<ViewDrawing> views) { Identity = identity; Views = views.AsReadOnly(); Complete = true; }
    public void Dispose() { foreach (var view in Views) view.Dispose(); }
}

/// <summary>Compute boundary: accepts only owned scenes and value requests; never resolves a document.</summary>
public static class NativeDrawing
{
    // Internal deterministic lifecycle instrumentation; never exposed through the product UI.
    internal static IProgress<double>? NativeProgress { get; set; }
    internal static Action? BeforeCompletion { get; set; }
    public static async Task<DrawingBatch> SolveAsync(LeaseEntry<PreparedScene>.Lease sceneLease, RequestIdentity identity,
        IReadOnlyList<ViewRequest> views, NativeSettings settings, CancellationToken token, IProgress<JobProgress>? progress = null)
    {
        using (sceneLease)
        {
            var scene = sceneLease.Value;
            views = views.ToArray();
            if (!scene.Cost.Admitted(views.Count, NativeScheduler.Global.BudgetBytes))
                throw new InvalidOperationException($"{SceneCost.Refusal} (estimate {scene.Cost.Estimate(views.Count):N0} bytes; {scene.Cost.BrepFaces} Brep faces; {scene.Cost.MeshTriangles} triangles; {views.Count} views).");
            progress?.Report(new(JobPhase.Queued, 0, views.Count));
            return await NativeScheduler.Global.RunAsync(scene.Cost.Estimate(views.Count), ct =>
            {
                var results = new List<ViewDrawing>();
                try
                {
                    progress?.Report(new(JobPhase.Preparing, 0, scene.SourceCount));
                    var prepared = scene.Prepare(ct);
                    for (var index = 0; index < views.Count; index++)
                    {
                        ct.ThrowIfCancellationRequested(); progress?.Report(new(JobPhase.Drawing, index, views.Count));
                        results.Add(SolveView(scene, prepared, views[index], settings, ct));
                    }
                    BeforeCompletion?.Invoke();
                    ct.ThrowIfCancellationRequested(); progress?.Report(new(JobPhase.Complete, views.Count, views.Count));
                    return new DrawingBatch(identity, results);
                }
                catch { foreach (var result in results) result.Dispose(); throw; }
            }, token).ConfigureAwait(false);
        }
    }
    private static ViewDrawing SolveView(PreparedScene scene, IReadOnlyList<PreparedLeaf> sources, ViewRequest view, NativeSettings settings, CancellationToken token)
    {
        var result = new ViewDrawing { Name = view.Name, MultipleThreads = settings.MultipleThreads, Tolerance = scene.Tolerance };
        var timer = Stopwatch.StartNew();
        var invalid = 0;
        var quarantine = scene.HasPotentialOccluderUnsupported;
        try
        {
            if (view.Error is not null) { result.Error = view.Error; return result; }
            result.Frame = view.Resolve(scene.Bounds, scene.Tolerance);
            result._diagnostics.Add($"Admission: {scene.Cost.GeometryBytes} geometry bytes; {scene.Cost.BrepFaces} faces; {scene.Cost.MeshTriangles} triangles; {scene.SourceCount} sources. Native multipleThreads={settings.MultipleThreads} (serial global scheduler).");
            if (sources.Count == 0) return result;
            using var viewport = new ViewportInfo();
            var frame = result.Frame; var radius = scene.Bounds.IsValid ? Math.Max(scene.Bounds.Diagonal.Length, 1) : 1;
            viewport.IsParallelProjection = true;
            viewport.SetCameraLocation(frame.Plane.Origin + frame.Plane.ZAxis * (radius * 4 + 10));
            viewport.SetCameraDirection(view.Direction); viewport.SetCameraUp(frame.Plane.YAxis);
            viewport.SetFrustum(frame.X.Min, frame.X.Max, frame.Y.Min, frame.Y.Max, .001, radius * 10 + 100);
            var parameters = new HiddenLineDrawingParameters { AbsoluteTolerance = scene.Tolerance, Flatten = false,
                IncludeHiddenCurves = true, IncludeTangentEdges = false, IncludeTangentSeams = false };
            parameters.SetViewport(viewport);
            foreach (var source in sources)
            {
                token.ThrowIfCancellationRequested();
                if (!parameters.AddGeometry(source.Geometry, source)) { invalid++; quarantine = true; result._diagnostics.Add($"Native rejected {source.Identity}"); }
            }
            using var drawing = HiddenLineDrawing.Compute(parameters, settings.MultipleThreads, NativeProgress, token);
            token.ThrowIfCancellationRequested();
            if (drawing is null) throw new InvalidOperationException("Native HLD returned null without cancellation.");
            if (!drawing.WorldToHiddenLine.TryGetInverse(out var inverse)) throw new InvalidOperationException("Invalid native frame.");
            var toPlane = Transform.PlanarProjection(frame.Plane) * inverse;
            foreach (var segment in drawing.Segments)
            {
                token.ThrowIfCancellationRequested();
                var visibility = segment.SegmentVisibility;
                // Unset may be a non-dereferenceable proxy. Check it before ParentCurve or CurveGeometry.
                if (visibility == HiddenLineDrawingSegment.Visibility.Unset)
                { invalid++; result._diagnostics.Add($"Unset segment {segment.Index}"); continue; }
                var parent = segment.ParentCurve;
                if (parent?.SourceObject?.Tag is not PreparedLeaf source)
                { invalid++; result._diagnostics.Add($"Parentless/source-less segment {segment.Index}"); continue; }
                if (visibility == HiddenLineDrawingSegment.Visibility.Duplicate) { result.DuplicateSegments++; continue; }
                if (visibility == HiddenLineDrawingSegment.Visibility.Projecting) { result.ProjectingSegments++; continue; }
                var proxy = segment.CurveGeometry;
                if (proxy is null) { invalid++; result._diagnostics.Add($"Null geometry {segment.Index}"); continue; }
                var owned = proxy.DuplicateCurve();
                try
                {
                    owned.EnsurePrivateCopy();
                    if (!owned.Transform(toPlane) || !owned.IsValid) throw new InvalidOperationException("Native curve extraction failed.");
                    var classification = visibility switch
                    {
                        HiddenLineDrawingSegment.Visibility.Visible => LineClass.Visible,
                        HiddenLineDrawingSegment.Visibility.Hidden => LineClass.Hidden, _ => LineClass.Unresolved
                    };
                    if (view.Named is { Crop: true })
                    {
                        foreach (var clipped in Crop(owned, frame, scene.Tolerance)) result.Pieces.Add(new(clipped, source, classification));
                        owned.Dispose();
                    }
                    else result.Pieces.Add(new(owned, source, classification));
                }
                catch { owned.Dispose(); throw; }
            }
            foreach (var point in drawing.Points)
            {
                token.ThrowIfCancellationRequested();
                if (point.PointVisibility == HiddenLineDrawingPoint.Visibility.Unset || point.SourceObject?.Tag is not PreparedLeaf source || !point.Location.IsValid)
                { invalid++; result._diagnostics.Add("Invalid native point extraction"); continue; }
                if (point.PointVisibility == HiddenLineDrawingPoint.Visibility.Hidden) { result.HiddenPoints++; continue; }
                if (point.PointVisibility != HiddenLineDrawingPoint.Visibility.Visible)
                { invalid++; result._diagnostics.Add("Uncertain native point"); continue; }
                var location = point.Location; location.Transform(toPlane);
                if (view.Named is { Crop: true } && !Inside(location, frame, scene.Tolerance)) continue;
                result.Pieces.Add(new(new Point(location), source, LineClass.Visible));
            }
        }
        catch (OperationCanceledException) { result.Dispose(); throw; }
        catch (Exception ex) { result.Dispose(); result.Error = ex.Message; quarantine = true; }
        finally
        {
            quarantine |= invalid != 0;
            if (quarantine)
            {
                foreach (var piece in result.Pieces.ToArray())
                {
                    using var geometry = piece.CopyGeometry();
                    if (geometry is Point) { result.Pieces.Remove(piece); piece.Dispose(); result._diagnostics.Add($"Uncertain point suppressed: {piece.SourceIdentity}"); }
                    else piece.Class = LineClass.Unresolved;
                }
            }
            result.Pieces.Sort(ComparePieces);
            result.Report = new("Native HLD (in process)", result.Pieces.Count(p => p.Class == LineClass.Unresolved), invalid,
                scene.Unsupported, scene.Conversions, quarantine);
            result.Seconds = timer.Elapsed.TotalSeconds;
        }
        return result;
    }

    private static int ComparePieces(DrawingPiece a, DrawingPiece b)
    {
        var x = a.Path; var y = b.Path;
        for (var i = 0; i < Math.Min(x.Length, y.Length); i++) { var c = x[i].CompareTo(y[i]); if (c != 0) return c; }
        var result = x.Length.CompareTo(y.Length);
        if (result == 0) result = StringComparer.Ordinal.Compare(a.SourceIdentity, b.SourceIdentity);
        if (result == 0) result = StringComparer.Ordinal.Compare(a.GeometricKey, b.GeometricKey);
        if (result == 0) result = a.Class.CompareTo(b.Class);
        return result;
    }
    private static bool Inside(Point3d p, Rectangle3d frame, double tolerance)
    {
        frame.Plane.ClosestParameter(p, out var x, out var y);
        return x >= frame.X.Min - tolerance && x <= frame.X.Max + tolerance && y >= frame.Y.Min - tolerance && y <= frame.Y.Max + tolerance;
    }
    private static IEnumerable<Curve> Crop(Curve curve, Rectangle3d frame, double tolerance)
    {
        var parameters = new List<double> { curve.Domain.Min, curve.Domain.Max };
        var sides = new[] { new Plane(frame.Corner(0), frame.Plane.XAxis), new Plane(frame.Corner(2), frame.Plane.XAxis),
            new Plane(frame.Corner(0), frame.Plane.YAxis), new Plane(frame.Corner(2), frame.Plane.YAxis) };
        foreach (var side in sides)
        {
            var intersections = global::Rhino.Geometry.Intersect.Intersection.CurvePlane(curve, side, tolerance);
            if (intersections is null) continue;
            foreach (var hit in intersections)
            { if (hit.IsOverlap) { parameters.Add(hit.OverlapA.Min); parameters.Add(hit.OverlapA.Max); } else parameters.Add(hit.ParameterA); }
        }
        var sorted = parameters.Distinct().Order().ToArray();
        for (var i = 1; i < sorted.Length; i++)
        {
            var interval = new Interval(sorted[i - 1], sorted[i]);
            if (!Inside(curve.PointAt(interval.Mid), frame, tolerance)) continue;
            var trimmed = curve.Trim(interval);
            if (trimmed is null) throw new InvalidOperationException("Named-view crop failed; output suppressed.");
            yield return trimmed;
        }
    }
}
