using System.Diagnostics;
using M2DPlus.Core;
using M2DPlus.Rhino.Sections;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace M2DPlus.Rhino.Services;

public sealed record NativeSettings(bool MultipleThreads = false);
public sealed class DrawingPiece : IDisposable
{
    private readonly GeometryBase _geometry;
    internal GeometryBase? DepthGeometry { get; }
    internal SourceSection Source { get; }
    public bool Profile { get; internal set; }
    public double Intensity { get; internal set; } = 1;
    public DepthInterval Depths { get; internal set; }
    public string SourceLayer => Source.LayerPath;
    public double SourceWidth => Source.LayerWidth;
    public uint SourceColour => Source.LayerColour;
    private readonly int[] _path;
    public string SourceIdentity { get; }
    public int SourceItem { get; }
    public int[] SourcePath => (int[])_path.Clone();
    internal ReadOnlySpan<int> Path => _path;
    public LineClass Class { get; internal set; }
    internal string GeometricKey { get; }
    internal DrawingPiece(GeometryBase geometry, PreparedLeaf source, LineClass classification, GeometryBase? depthGeometry = null)
    {
        DepthGeometry = depthGeometry; Source = source.Section; SourceItem = source.SourceItem;
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
    public void Dispose() { _geometry.Dispose(); DepthGeometry?.Dispose(); }
}
public sealed class ViewDrawing : IDisposable
{
    internal List<DrawingPiece> Pieces { get; } = new();
    public IReadOnlyList<DrawingPiece> Items => Pieces.AsReadOnly();
    public string Name { get; internal init; } = "";
    public EffectiveSettings? Settings { get; internal set; }
    public M2DPlus.Core.Styling.LineWeightPreset Weights => new(Settings?.Scale ?? 100, Settings?.Weights ?? WeightMode.Auto);
    public string? Error { get; internal set; }
    public Rectangle3d Frame { get; internal set; }
    public QualityReport Report { get; internal set; } = new("Native HLD", 0, 0, new Dictionary<string, int>(), Array.Empty<string>(), false);
    public IReadOnlyList<string> Diagnostics => _diagnostics.AsReadOnly();
    public List<DrawingRegion> Regions { get; } = new();
    internal readonly List<string> _diagnostics = new();
    public int HiddenPoints { get; internal set; }
    public int DuplicateSegments { get; internal set; }
    public int ProjectingSegments { get; internal set; }
    public int EmptyNativeSlots { get; internal set; }
    public int ZeroLengthNativeSegments { get; internal set; }
    public int ToleranceDroppedPieces { get; internal set; }
    public int SubToleranceSourceEdges { get; internal set; }
    public int SubToleranceBoundsGaps { get; internal set; }
    public int UncertainSectionComponents { get; internal set; }
    public string? ToleranceWarning => ToleranceDroppedPieces + SubToleranceSourceEdges + SubToleranceBoundsGaps + UncertainSectionComponents == 0 ? null :
        $"Detail may be lost or uncertain at tolerance {Tolerance:G6} document units: {ToleranceDroppedPieces} adapter tolerance drops; {SubToleranceSourceEdges} source edges at or below 2 x tolerance; {SubToleranceBoundsGaps} close source-bound gaps; {UncertainSectionComponents} uncertain section components. See Info; use a finer document tolerance or tolerance = override when needed.";
    public double Seconds { get; internal set; }
    public string Info => $"{Name}: {(Error ?? "finished")} · {Seconds:F3} s\n" +
        $"{Report.Route}; deterministic={!MultipleThreads}; tolerance={Tolerance:R}; validation: {Report.ValidationDomain}\n" +
        $"Visible={Pieces.Count(p => p.Class == LineClass.Visible)}, hidden={Pieces.Count(p => p.Class == LineClass.Hidden)}, unresolved={Report.UncertainCount}, hidden points={HiddenPoints}; " +
        $"invalid extraction={Report.InvalidExtractionCount}; duplicate={DuplicateSegments}; projecting={ProjectingSegments}; empty slots={EmptyNativeSlots}; zero length={ZeroLengthNativeSegments}; quarantined={Report.Quarantined}\n" +
        $"Tolerance counts: adapter tolerance drops={ToleranceDroppedPieces}; source edges at or below 2 x tolerance={SubToleranceSourceEdges}; close source-bound gaps={SubToleranceBoundsGaps}; uncertain section components={UncertainSectionComponents}. Native internal drops=unknown; risk counts are not measured native loss counts.\n" +
        $"Unsupported: {string.Join(", ", Report.UnsupportedByType.Select(p => $"{p.Key}={p.Value}"))}\n" +
        string.Join("; ", Report.Normalisation.Concat(Diagnostics)) +
        $"\nCut regions={Regions.Count}, Hatch={Regions.Count(r => r.Region.Hatch is not null)}, Beyond={Pieces.Count(p => p.Class == LineClass.Beyond)}, Far clip={Pieces.Count(p => p.Class == LineClass.FarClip)}; weights={Weights.BandName}.";
    internal bool MultipleThreads;
    internal double Tolerance;
    public void Dispose() { foreach (var item in Pieces) item.Dispose(); Pieces.Clear(); foreach (var region in Regions) region.Region.Dispose(); Regions.Clear(); }
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
    private static long _hldCalls;
    public static long HldCalls => Interlocked.Read(ref _hldCalls);
    internal static void CountHld() => Interlocked.Increment(ref _hldCalls);
    public static async Task<DrawingBatch> SolveAsync(LeaseEntry<PreparedScene>.Lease sceneLease, RequestIdentity identity,
        IReadOnlyList<ViewRequest> views, NativeSettings settings, CancellationToken token, IProgress<JobProgress>? progress = null)
    {
        using (sceneLease)
        {
            return await SolveBorrowedAsync(sceneLease.Value, identity, views, settings, token, progress).ConfigureAwait(false);
        }
    }
    internal static async Task<DrawingBatch> SolveBorrowedAsync(PreparedScene scene, RequestIdentity identity,
        IReadOnlyList<ViewRequest> views, NativeSettings settings, CancellationToken token, IProgress<JobProgress>? progress)
    {
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
                    var edgeCensus = new Dictionary<double, int>(); // per distinct drawing tolerance (overrides may differ per view)
                    for (var index = 0; index < views.Count; index++)
                    {
                        ct.ThrowIfCancellationRequested(); progress?.Report(new(JobPhase.Drawing, index, views.Count));
                        var drawing = SolveView(scene, prepared, views[index], settings, ct);
                        results.Add(drawing);
                        if (!edgeCensus.TryGetValue(drawing.Tolerance, out var census))
                            edgeCensus[drawing.Tolerance] = census = ToleranceDiagnostics.SourceEdgeCensus(prepared, drawing.Tolerance, ct);
                        ToleranceDiagnostics.Apply(drawing, prepared, census, ct);
                    }
                    BeforeCompletion?.Invoke();
                    ct.ThrowIfCancellationRequested(); progress?.Report(new(JobPhase.Complete, views.Count, views.Count));
                    return new DrawingBatch(identity, results);
                }
                catch { foreach (var result in results) result.Dispose(); throw; }
            }, token).ConfigureAwait(false);
    }
    private static ViewDrawing SolveView(PreparedScene scene, IReadOnlyList<PreparedLeaf> sources, ViewRequest view, NativeSettings settings, CancellationToken token)
    {
        var tolerance = view.Settings?.Tolerance ?? scene.Tolerance;
        if (view.Settings is { } effective)
        {
            if (view.Named is { } named) view = view with { Named = named with { Crop = effective.Crop } };
            if (view.Cut is null && view.Error is null)
            {
                var frame = view.Resolve(scene.Bounds, effective.Tolerance);
                if (effective.FarClip != FarClipMode.None && scene.Bounds.IsValid &&
                    -scene.Bounds.GetCorners().Min(frame.Plane.DistanceTo) >= effective.Depth - effective.Tolerance)
                    view = view with { Cut = new CutFrame(frame.Plane, effective.Crop && view.Named is not null ? frame : null,
                        effective.Depth, false, false, Array.Empty<Guid>(), Array.Empty<Guid>(), null) };
            }
            if (view.Cut is { } cut) view = view with { Cut = cut with { SourceDepth = effective.Depth, FarClip = effective.FarClip } };
        }
        if (view.Cut is not null && view.Error is null) return SectionDrawing.Solve(scene, sources, view, settings, token);
        var result = new ViewDrawing { Name = view.Name, MultipleThreads = settings.MultipleThreads, Tolerance = tolerance };
        var timer = Stopwatch.StartNew();
        using var glass = new GlassGeometry(sources, view);
        sources = glass.Sources;
        var invalid = 0;
        var quarantine = scene.HasPotentialOccluderUnsupported;
        var uncertainSources = new HashSet<string>(StringComparer.Ordinal);
        var uncertainRegions = new List<BoundingBox>();
        try
        {
            if (view.Error is not null) { result.Error = view.Error; return result; }
            result.Frame = view.Resolve(scene.Bounds, tolerance);
            result._diagnostics.Add($"Admission: {scene.Cost.GeometryBytes} geometry bytes; {scene.Cost.BrepFaces} faces; {scene.Cost.MeshTriangles} triangles; {scene.SourceCount} sources. Native multipleThreads={settings.MultipleThreads} (serial global scheduler).");
            if (sources.Count == 0) return result;
            using var viewport = new ViewportInfo();
            var frame = result.Frame; var radius = scene.Bounds.IsValid ? Math.Max(scene.Bounds.Diagonal.Length, 1) : 1;
            viewport.IsParallelProjection = true;
            viewport.SetCameraLocation(frame.Plane.Origin + frame.Plane.ZAxis * (radius * 4 + 10));
            viewport.SetCameraDirection(view.Direction); viewport.SetCameraUp(frame.Plane.YAxis);
            viewport.SetFrustum(frame.X.Min, frame.X.Max, frame.Y.Min, frame.Y.Max, .001, radius * 10 + 100);
            var parameters = new HiddenLineDrawingParameters { AbsoluteTolerance = tolerance, Flatten = false,
                IncludeHiddenCurves = view.Settings?.Hidden ?? Settings.DefaultHidden, IncludeTangentEdges = view.Settings?.Tangents ?? false, IncludeTangentSeams = view.Settings?.Seams ?? false };
            parameters.SetViewport(viewport);
            foreach (var source in sources)
            {
                token.ThrowIfCancellationRequested();
                if (!parameters.AddGeometry(source.Geometry, source)) { invalid++; quarantine = true; result._diagnostics.Add($"Native rejected {source.Identity}"); }
            }
            CountHld();
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
                {
                    var evidence = NativeUncertainty.Inspect(segment, tag => tag as PreparedLeaf, toPlane);
                    if (evidence.Kind == UnsetKind.EmptySlot) result.EmptyNativeSlots++;
                    else if (evidence.Kind == UnsetKind.ZeroLength) result.ZeroLengthNativeSegments++;
                    else
                    {
                        invalid++;
                        if (evidence.Source is { } affected) uncertainSources.Add(affected.Identity);
                        else if (evidence.Bounds is { } bounds) uncertainRegions.Add(bounds);
                        else quarantine = true;
                        result._diagnostics.Add($"Unset segment {segment.Index}: {evidence.Kind}; source={evidence.Source?.Identity}; bounds={evidence.Bounds}; {evidence.Detail}");
                    }
                    continue;
                }
                if (visibility == HiddenLineDrawingSegment.Visibility.Hidden && view.Settings?.Hidden == false) continue;
                var parent = segment.ParentCurve;
                if (parent?.SourceObject?.Tag is not PreparedLeaf source)
                { invalid++; quarantine = true; result._diagnostics.Add($"Parentless/source-less segment {segment.Index}"); continue; }
                if (visibility == HiddenLineDrawingSegment.Visibility.Duplicate) { result.DuplicateSegments++; continue; }
                if (visibility == HiddenLineDrawingSegment.Visibility.Projecting) { result.ProjectingSegments++; continue; }
                var proxy = segment.CurveGeometry;
                if (proxy is null) { invalid++; uncertainSources.Add(source.Identity); result._diagnostics.Add($"Null geometry {segment.Index}: {source.Identity}"); continue; }
                var owned = proxy.DuplicateCurve();
                try
                {
                    owned.EnsurePrivateCopy();
                    using var depthCurve = proxy.DuplicateCurve(); depthCurve.Transform(inverse);
                    if (!owned.Transform(toPlane) || !owned.IsValid) throw new InvalidOperationException("Native curve extraction failed.");
                    var classification = visibility switch
                    {
                        HiddenLineDrawingSegment.Visibility.Visible => LineClass.Visible,
                        HiddenLineDrawingSegment.Visibility.Hidden => LineClass.Hidden, _ => LineClass.Unresolved
                    };
                    if (view.Named is { Crop: true })
                    {
                        foreach (var clipped in Crop(owned, frame, tolerance)) result.Pieces.Add(new(clipped, source, classification, depthCurve.Trim(clipped.Domain)) { Profile = segment.IsSceneSilhouette });
                        owned.Dispose();
                    }
                    else result.Pieces.Add(new(owned, source, classification, depthCurve.DuplicateCurve()) { Profile = segment.IsSceneSilhouette });
                }
                catch { owned.Dispose(); throw; }
            }
            foreach (var point in drawing.Points)
            {
                token.ThrowIfCancellationRequested();
                if (point.PointVisibility == HiddenLineDrawingPoint.Visibility.Unset || point.SourceObject?.Tag is not PreparedLeaf source || !point.Location.IsValid)
                { invalid++; quarantine = true; result._diagnostics.Add("Invalid native point extraction"); continue; }
                if (point.PointVisibility == HiddenLineDrawingPoint.Visibility.Hidden) { result.HiddenPoints++; continue; }
                if (point.PointVisibility != HiddenLineDrawingPoint.Visibility.Visible)
                { invalid++; quarantine = true; result._diagnostics.Add("Uncertain native point"); continue; }
                var world = point.Location; world.Transform(inverse);
                var location = point.Location; location.Transform(toPlane);
                if (view.Named is { Crop: true } && !Inside(location, frame, tolerance)) continue;
                result.Pieces.Add(new(new Point(location), source, LineClass.Visible, new Point(world)));
            }
        }
        catch (OperationCanceledException) { result.Dispose(); throw; }
        catch (Exception ex) { result.Dispose(); result.Error = ex.Message; quarantine = true; }
        finally
        {
            if (quarantine || uncertainSources.Count != 0 || uncertainRegions.Count != 0)
            {
                foreach (var piece in result.Pieces.ToArray())
                {
                    using var geometry = piece.CopyGeometry();
                    if (!quarantine && !uncertainSources.Contains(piece.SourceIdentity) && !uncertainRegions.Any(b => NativeUncertainty.Affects(b, geometry.GetBoundingBox(true), tolerance))) continue;
                    if (geometry is Point) { result.Pieces.Remove(piece); piece.Dispose(); result._diagnostics.Add($"Uncertain point suppressed: {piece.SourceIdentity}"); }
                    else piece.Class = LineClass.Unresolved;
                }
            }
            if (result.EmptyNativeSlots + result.ZeroLengthNativeSegments != 0) result._diagnostics.Add($"Benign native records dropped: {result.EmptyNativeSlots} empty slots (index -1, no parent); {result.ZeroLengthNativeSegments} exactly zero-length point curves. No nonzero Unset was promoted.");
            result.Pieces.Sort(ComparePieces);
            result.Report = new("Native HLD (in process)", result.Pieces.Count(p => p.Class == LineClass.Unresolved), invalid,
                scene.Unsupported, scene.Conversions, quarantine);
            result.Seconds = timer.Elapsed.TotalSeconds;
        }
        return result;
    }

    internal static int ComparePieces(DrawingPiece a, DrawingPiece b)
    {
        var x = a.Path; var y = b.Path;
        for (var i = 0; i < Math.Min(x.Length, y.Length); i++) { var c = x[i].CompareTo(y[i]); if (c != 0) return c; }
        var result = x.Length.CompareTo(y.Length);
        if (result == 0) result = StringComparer.Ordinal.Compare(a.SourceIdentity, b.SourceIdentity);
        if (result == 0) result = StringComparer.Ordinal.Compare(a.GeometricKey, b.GeometricKey);
        if (result == 0) result = a.Class.CompareTo(b.Class);
        return result;
    }
    internal static bool Inside(Point3d p, Rectangle3d frame, double tolerance)
    {
        frame.Plane.ClosestParameter(p, out var x, out var y);
        return x >= frame.X.Min - tolerance && x <= frame.X.Max + tolerance && y >= frame.Y.Min - tolerance && y <= frame.Y.Max + tolerance;
    }
    internal static IEnumerable<Curve> Crop(Curve curve, Rectangle3d frame, double tolerance)
    {
        var parameters = new List<double> { curve.Domain.Min, curve.Domain.Max };
        var sides = new[] { new Plane(frame.Corner(0), frame.Plane.XAxis), new Plane(frame.Corner(2), frame.Plane.XAxis),
            new Plane(frame.Corner(0), frame.Plane.YAxis), new Plane(frame.Corner(2), frame.Plane.YAxis) };
        foreach (var side in sides)
        {
            using var intersections = global::Rhino.Geometry.Intersect.Intersection.CurvePlane(curve, side, tolerance);
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
