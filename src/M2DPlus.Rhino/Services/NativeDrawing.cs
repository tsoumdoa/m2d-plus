using System.Diagnostics;
using M2DPlus.Core;
using M2DPlus.Rhino.Sections;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace M2DPlus.Rhino.Services;

public sealed record NativeSettings(bool MultipleThreads = false)
{
    /// <summary>Opt-in certified raster prepass for uncut views; null keeps the plain native route.</summary>
    public M2DPlus.Rhino.Visibility.RasterOptions? Raster { get; init; }
}
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
    /// <summary>The same source and class with new geometry (used to trim a piece).</summary>
    internal DrawingPiece(DrawingPiece from, GeometryBase geometry, GeometryBase? depthGeometry)
    {
        DepthGeometry = depthGeometry; Source = from.Source; SourceItem = from.SourceItem; _geometry = geometry;
        SourceIdentity = from.SourceIdentity; _path = (int[])from._path.Clone(); Class = from.Class; Profile = from.Profile;
        Intensity = from.Intensity; Depths = from.Depths;
        var bounds = geometry.GetBoundingBox(true);
        GeometricKey = FormattableString.Invariant($"{bounds.Min.X:R},{bounds.Min.Y:R},{bounds.Min.Z:R},{bounds.Max.X:R},{bounds.Max.Y:R},{bounds.Max.Z:R}/") +
            GeometryFingerprint.Compute(geometry);
    }
    internal GeometryBase Geometry => _geometry;
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
    public double Seconds { get; internal set; }
    public M2DPlus.Rhino.Visibility.PrepassStats? Prepass { get; internal set; }
    /// <summary>Sources the prepass removed from the native solve (culled or outside the crop).</summary>
    internal IReadOnlyList<string> RemovedSources { get; set; } = Array.Empty<string>();
    /// <summary>Sources whose output the prepass emitted itself (bench attribution).</summary>
    internal IReadOnlySet<string> DirectSources { get; set; } = new HashSet<string>();
    public string Info => $"{Name}: {(Error ?? "finished")} · {Seconds:F3} s\n" +
        $"{Report.Route}; deterministic={!MultipleThreads}; tolerance={Tolerance:R}; validation: {Report.ValidationDomain}\n" +
        $"Visible={Pieces.Count(p => p.Class == LineClass.Visible)}, hidden={Pieces.Count(p => p.Class == LineClass.Hidden)}, unresolved={Report.UncertainCount}, hidden points={HiddenPoints}; " +
        $"invalid extraction={Report.InvalidExtractionCount}; duplicate={DuplicateSegments}; projecting={ProjectingSegments}; empty slots={EmptyNativeSlots}; zero length={ZeroLengthNativeSegments}; quarantined={Report.Quarantined}\n" +
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

/// <summary>Output of one native HLD call before it is merged into a view.</summary>
internal sealed class NativeSink
{
    internal List<DrawingPiece> Pieces { get; } = new();
    internal List<string> Diagnostics { get; } = new();
    internal HashSet<string> UncertainSources { get; } = new(StringComparer.Ordinal);
    internal List<BoundingBox> UncertainRegions { get; } = new();
    internal int EmptyNativeSlots, ZeroLengthNativeSegments, DuplicateSegments, ProjectingSegments, HiddenPoints, Invalid, Contradictions;
    internal bool Quarantine;
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
    internal static ViewDrawing SolveView(PreparedScene scene, IReadOnlyList<PreparedLeaf> sources, ViewRequest view, NativeSettings settings, CancellationToken token)
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
        var viewFrame = default(Rectangle3d);
        IReadOnlySet<string> certifiedHidden = new HashSet<string>();
        var contradictions = 0;
        try
        {
            if (view.Error is not null) { result.Error = view.Error; return result; }
            result.Frame = viewFrame = view.Resolve(scene.Bounds, tolerance);
            result._diagnostics.Add($"Admission: {scene.Cost.GeometryBytes} geometry bytes; {scene.Cost.BrepFaces} faces; {scene.Cost.MeshTriangles} triangles; {scene.SourceCount} sources. Native multipleThreads={settings.MultipleThreads} (serial global scheduler).");
            if (sources.Count == 0) return result;
            var nativeSources = sources;
            if (settings.Raster is { } raster && sources.Count != 0)
            {
                var prepass = M2DPlus.Rhino.Visibility.RasterPrepass.Classify(sources, view, viewFrame, scene.Bounds, tolerance, raster, token);
                result.Prepass = prepass.Stats; result.HiddenPoints += prepass.HiddenPoints; result.RemovedSources = prepass.Removed; result.DirectSources = prepass.Direct;
                nativeSources = prepass.Remaining; certifiedHidden = prepass.CulledNative;
                var hidden = view.Settings?.Hidden ?? Settings.DefaultHidden;
                void Direct(PreparedLeaf source, Curve projected, Curve world, LineClass cls, bool profile)
                {
                    try
                    {
                        if (cls == LineClass.Hidden && !hidden) { projected.Dispose(); world.Dispose(); return; }
                        if (view.Named is { Crop: true })
                        {
                            foreach (var clipped in Crop(projected, viewFrame, tolerance)) result.Pieces.Add(new(clipped, source, cls, world.DuplicateCurve()) { Profile = profile });
                            projected.Dispose(); world.Dispose();
                        }
                        else result.Pieces.Add(new(projected, source, cls, world) { Profile = profile });
                    }
                    catch { projected.Dispose(); world.Dispose(); throw; }
                }
                if (hidden)
                {
                    // Generate the certified-hidden candidates in parallel; add them in source order (deterministic output).
                    var culled = prepass.CulledDirect.Select(s => (Source: s, Curved: false)).Concat(prepass.CulledCurved.Select(s => (Source: s, Curved: true))).ToArray();
                    var curves = new List<(Curve, Curve)>[culled.Length];
                    Parallel.For(0, culled.Length, new ParallelOptions { CancellationToken = token }, k => curves[k] = (culled[k].Curved
                        ? M2DPlus.Rhino.Visibility.RasterPrepass.CurvedHiddenCurves(culled[k].Source, viewFrame, tolerance, raster)
                        : M2DPlus.Rhino.Visibility.RasterPrepass.HiddenCurves(culled[k].Source, viewFrame, tolerance, raster)).ToList());
                    for (var k = 0; k < culled.Length; k++)
                        foreach (var (projected, world) in curves[k]) Direct(culled[k].Source, projected, world, LineClass.Hidden, false);
                }
                foreach (var source in prepass.Isolated)
                {
                    if (source.Geometry is Point point)
                    {
                        var world = point.Location; var location = world; location.Transform(Transform.PlanarProjection(viewFrame.Plane));
                        if (view.Named is { Crop: true } && !Inside(location, viewFrame, tolerance)) continue;
                        result.Pieces.Add(new(new Point(location), source, LineClass.Visible, new Point(world)));
                        continue;
                    }
                    foreach (var (projected, world, cls, profile) in M2DPlus.Rhino.Visibility.RasterPrepass.IsolatedCurves(source, viewFrame, tolerance, raster))
                        Direct(source, projected, world, cls, profile);
                }
                result._diagnostics.Add($"Raster prepass: {prepass.Stats}");
            }
            if (nativeSources.Count != 0) { var sink = new NativeSink(); RunNative(nativeSources, sink); Merge(sink); }
            if (settings.Raster is not null) VisibleOverHidden(result, viewFrame, tolerance);
            if (contradictions != 0) result._diagnostics.Add($"Raster certificate contradicted by native on {contradictions} pieces (native marked certified-hidden pieces visible).");
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
        void Merge(NativeSink sink)
        {
            result.Pieces.AddRange(sink.Pieces); result._diagnostics.AddRange(sink.Diagnostics);
            result.EmptyNativeSlots += sink.EmptyNativeSlots; result.ZeroLengthNativeSegments += sink.ZeroLengthNativeSegments;
            result.DuplicateSegments += sink.DuplicateSegments; result.ProjectingSegments += sink.ProjectingSegments; result.HiddenPoints += sink.HiddenPoints;
            invalid += sink.Invalid; quarantine |= sink.Quarantine; contradictions += sink.Contradictions;
            uncertainSources.UnionWith(sink.UncertainSources); uncertainRegions.AddRange(sink.UncertainRegions);
        }
        void RunNative(IReadOnlyList<PreparedLeaf> subset, NativeSink sink)
        {
            using var viewport = new ViewportInfo();
            var radius = scene.Bounds.IsValid ? Math.Max(scene.Bounds.Diagonal.Length, 1) : 1;
            viewport.IsParallelProjection = true;
            viewport.SetCameraLocation(viewFrame.Plane.Origin + viewFrame.Plane.ZAxis * (radius * 4 + 10));
            viewport.SetCameraDirection(view.Direction); viewport.SetCameraUp(viewFrame.Plane.YAxis);
            var clip = viewFrame; var crop = view.Named is { Crop: true };
            viewport.SetFrustum(clip.X.Min, clip.X.Max, clip.Y.Min, clip.Y.Max, .001, radius * 10 + 100);
            var parameters = new HiddenLineDrawingParameters { AbsoluteTolerance = tolerance, Flatten = false,
                // Raster route: always solve with hidden curves on (native can drop visible geometry with them off) and filter below.
                IncludeHiddenCurves = settings.Raster is not null || (view.Settings?.Hidden ?? Settings.DefaultHidden), IncludeTangentEdges = view.Settings?.Tangents ?? false, IncludeTangentSeams = view.Settings?.Seams ?? false };
            parameters.SetViewport(viewport);
            foreach (var source in subset)
            {
                token.ThrowIfCancellationRequested();
                if (!parameters.AddGeometry(source.Geometry, source)) { sink.Invalid++; sink.Quarantine = true; sink.Diagnostics.Add($"Native rejected {source.Identity}"); }
            }
            CountHld();
            using var drawing = HiddenLineDrawing.Compute(parameters, settings.MultipleThreads, NativeProgress, token);
            token.ThrowIfCancellationRequested();
            if (drawing is null) throw new InvalidOperationException("Native HLD returned null without cancellation.");
            if (!drawing.WorldToHiddenLine.TryGetInverse(out var inverse)) throw new InvalidOperationException("Invalid native viewFrame.");
            var toPlane = Transform.PlanarProjection(viewFrame.Plane) * inverse;
            foreach (var segment in drawing.Segments)
            {
                token.ThrowIfCancellationRequested();
                var visibility = segment.SegmentVisibility;
                // Unset may be a non-dereferenceable proxy. Check it before ParentCurve or CurveGeometry.
                if (visibility == HiddenLineDrawingSegment.Visibility.Unset)
                {
                    var evidence = NativeUncertainty.Inspect(segment, tag => tag as PreparedLeaf, toPlane);
                    if (evidence.Kind == UnsetKind.EmptySlot) sink.EmptyNativeSlots++;
                    else if (evidence.Kind == UnsetKind.ZeroLength) sink.ZeroLengthNativeSegments++;
                    else
                    {
                        sink.Invalid++;
                        if (evidence.Source is { } affected) sink.UncertainSources.Add(affected.Identity);
                        else if (evidence.Bounds is { } bounds) sink.UncertainRegions.Add(bounds);
                        else sink.Quarantine = true;
                        sink.Diagnostics.Add($"Unset segment {segment.Index}: {evidence.Kind}; source={evidence.Source?.Identity}; bounds={evidence.Bounds}; {evidence.Detail}");
                    }
                    continue;
                }
                if (visibility == HiddenLineDrawingSegment.Visibility.Hidden && view.Settings?.Hidden == false) continue;
                var parent = segment.ParentCurve;
                if (parent?.SourceObject?.Tag is not PreparedLeaf source)
                { sink.Invalid++; sink.Quarantine = true; sink.Diagnostics.Add($"Parentless/source-less segment {segment.Index}"); continue; }
                if (visibility == HiddenLineDrawingSegment.Visibility.Duplicate) { sink.DuplicateSegments++; continue; }
                if (visibility == HiddenLineDrawingSegment.Visibility.Projecting) { sink.ProjectingSegments++; continue; }
                var proxy = segment.CurveGeometry;
                if (proxy is null) { sink.Invalid++; sink.UncertainSources.Add(source.Identity); sink.Diagnostics.Add($"Null geometry {segment.Index}: {source.Identity}"); continue; }
                var owned = proxy.DuplicateCurve();
                try
                {
                    owned.EnsurePrivateCopy();
                    using var depthCurve = proxy.DuplicateCurve(); depthCurve.Transform(inverse);
                    if (!owned.Transform(toPlane) || !owned.IsValid) throw new InvalidOperationException("Native curve extraction failed.");
                    if (visibility == HiddenLineDrawingSegment.Visibility.Visible && certifiedHidden.Contains(source.Identity)) sink.Contradictions++;
                    var classification = visibility switch
                    {
                        HiddenLineDrawingSegment.Visibility.Visible => LineClass.Visible,
                        HiddenLineDrawingSegment.Visibility.Hidden => LineClass.Hidden, _ => LineClass.Unresolved
                    };
                    if (crop)
                    {
                        foreach (var clipped in Crop(owned, clip, tolerance)) sink.Pieces.Add(new(clipped, source, classification, depthCurve.Trim(clipped.Domain)) { Profile = segment.IsSceneSilhouette });
                        owned.Dispose();
                    }
                    else sink.Pieces.Add(new(owned, source, classification, depthCurve.DuplicateCurve()) { Profile = segment.IsSceneSilhouette });
                }
                catch { owned.Dispose(); throw; }
            }
            foreach (var point in drawing.Points)
            {
                token.ThrowIfCancellationRequested();
                if (point.PointVisibility == HiddenLineDrawingPoint.Visibility.Unset || point.SourceObject?.Tag is not PreparedLeaf source || !point.Location.IsValid)
                { sink.Invalid++; sink.Quarantine = true; sink.Diagnostics.Add("Invalid native point extraction"); continue; }
                if (point.PointVisibility == HiddenLineDrawingPoint.Visibility.Hidden) { sink.HiddenPoints++; continue; }
                if (point.PointVisibility != HiddenLineDrawingPoint.Visibility.Visible)
                { sink.Invalid++; sink.Quarantine = true; sink.Diagnostics.Add("Uncertain native point"); continue; }
                var world = point.Location; world.Transform(inverse);
                var location = point.Location; location.Transform(toPlane);
                if (crop && !Inside(location, clip, tolerance)) continue;
                sink.Pieces.Add(new(new Point(location), source, LineClass.Visible, new Point(world)));
            }
        }
        return result;
    }

    /// <summary>
    /// Drawing-locus convention (as in the exact planar kernel): where a hidden locus coincides with a visible locus within
    /// tolerance, the visible wins and the hidden span is removed. Applied to linear hidden pieces (trimmed exactly by line
    /// parameter); other hidden pieces are kept whole.
    /// </summary>
    private static void VisibleOverHidden(ViewDrawing result, Rectangle3d frame, double tolerance)
    {
        var plane = frame.Plane; var visible = new List<(double, double, double, double)>();
        (double, double) Uv(Point3d p) { plane.ClosestParameter(p, out var u, out var v); return (u, v); }
        foreach (var piece in result.Pieces)
        {
            if (piece.Class != LineClass.Visible || piece.Geometry is not Curve curve) continue;
            Point3d[] points;
            if (curve.IsLinear(tolerance)) points = new[] { curve.PointAtStart, curve.PointAtEnd };
            else
            {
                // Chords within tolerance/4 of the curve: coincidence is decided within 3/4 tolerance of the true locus.
                using var polyline = curve.ToPolyline(tolerance / 4, 0.02, 0, 0);
                if (polyline is null || !polyline.TryGetPolyline(out var pl)) continue;
                points = pl.ToArray();
            }
            for (var k = 1; k < points.Length; k++) { var (ax, ay) = Uv(points[k - 1]); var (bx, by) = Uv(points[k]); visible.Add((ax, ay, bx, by)); }
        }
        if (visible.Count == 0) return;
        var index = new M2DPlus.Core.Visibility.Raster.VisibleOverHidden(visible, .75 * tolerance, Math.Max(frame.Width, frame.Height) / 512);
        var removed = 0; var trimmed = 0;
        for (var n = result.Pieces.Count - 1; n >= 0; n--)
        {
            var piece = result.Pieces[n];
            if (piece.Class != LineClass.Hidden || piece.Geometry is not Curve curve || !curve.IsLinear(tolerance)) continue;
            // Trim by parameter fraction only where the parameterisation is affine (midpoint maps to the midpoint).
            if (curve.PointAt(curve.Domain.Mid).DistanceTo((curve.PointAtStart + curve.PointAtEnd) / 2) > tolerance / 10) continue;
            var (ax, ay) = Uv(curve.PointAtStart); var (bx, by) = Uv(curve.PointAtEnd);
            var keep = index.Remaining(ax, ay, bx, by).Where(g => (g.T1 - g.T0) * Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay)) > tolerance).ToList();
            if (keep.Count == 1 && keep[0].T0 <= 0 && keep[0].T1 >= 1) continue;
            result.Pieces.RemoveAt(n);
            var domain = curve.Domain; var depth = piece.DepthGeometry as Curve;
            foreach (var (t0, t1) in keep)
            {
                var interval = new Interval(domain.ParameterAt(t0), domain.ParameterAt(t1));
                var part = curve.Trim(interval); var partDepth = depth?.Trim(new Interval(depth.Domain.ParameterAt(t0), depth.Domain.ParameterAt(t1)));
                if (part is null) { partDepth?.Dispose(); continue; }
                result.Pieces.Insert(n, new DrawingPiece(piece, part, partDepth)); trimmed++;
            }
            piece.Dispose(); removed++;
        }
        if (removed != 0) result._diagnostics.Add($"Visible over hidden: {removed} hidden pieces coincident with visible loci removed or trimmed ({trimmed} parts kept).");
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
