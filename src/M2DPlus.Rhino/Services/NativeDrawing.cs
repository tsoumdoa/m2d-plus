using System.Diagnostics;
using M2DPlus.Rhino.Experimental.PipeVisibility;
using M2DPlus.Core;
using M2DPlus.Rhino.Sections;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace M2DPlus.Rhino.Services;

public sealed record NativeSettings
{
    internal NativeScheduler Scheduler { get; init; } = NativeScheduler.Global;
    internal bool IsWorker { get; init; }
    // Internal research controls only; production always uses threaded HLD and scene bounds.
    internal bool MultipleThreads { get; init; } = true;
    internal BoundingBox? CameraBounds { get; init; }
    internal bool CullSources { get; init; } = true;
    internal Action<IReadOnlyList<VisibilityInput>>? ObserveNativeInputs { get; init; }
    internal Action<NativeHldOptions>? ObserveHldOptions { get; init; }
}
public sealed class DrawingPiece : IDisposable
{
    private readonly GeometryBase _geometry;
    internal GeometryBase? DepthGeometry { get; }
    internal bool FarCandidate { get; init; }
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
    /// <summary>The class the solve gave this piece, before any later quarantine to Unresolved. Presentation drops
    /// solved-hidden pieces when <c>hidden = off</c>, exactly as the solve used to.</summary>
    internal LineClass SolvedClass { get; }
    internal string GeometricKey { get; }
    internal DrawingPiece(GeometryBase geometry, PreparedLeaf source, LineClass classification, GeometryBase? depthGeometry = null, LineClass? solvedClass = null)
    {
        DepthGeometry = depthGeometry; Source = source.Section; SourceItem = source.SourceItem; SolvedClass = solvedClass ?? classification;
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
    /// <summary>The min visible threshold applied (document units; 0 = off), and what it ignored.</summary>
    public double MinVisible { get; internal set; }
    public int IgnoredObjects { get; internal set; }
    public int IgnoredPieces { get; internal set; }
    public string IgnoredSummary => MinVisible <= 0 ? "Ignored: off (min visible = off)." :
        FormattableString.Invariant($"Ignored (min visible {MinVisible:G6}): {IgnoredObjects} objects, {IgnoredPieces} pieces. Their lines are in the Ignored output; nothing is deleted.");
    public PhaseTimings Phases { get; } = new();
    // Number of distinct sources registered with HLD.
    public int FastNativeSources { get; internal set; }
    public string EngineSummary => Report.Route == "pipes (experimental)" ? "Engine: pipes (experimental); surface visibility; known junction errors" : NativeCalls == 0 ? Settings?.CutOnly == true
        ? "Engine: sections only; HLD not run" : "Engine: HLD not run"
        : $"Engine: Rhino HLD; {(MultipleThreads ? "multithreaded" : "single-threaded")}; visibility not certified";
    private string JointsDisclosure => Settings is { } settings && settings["joints"].AssignedBy != SettingSource.Default
        ? "Joints suppression removed; joints is ignored; Rhino HLD controls contact edges.\n" : "";
    public string Info => $"{Name}: {(Error ?? "finished")} · {Seconds:F3} s\n" +
        EngineSummary + "\n" + JointsDisclosure + (NativeCalls > 0 ? "Fresh HLD solves may change duplicate ownership/source attribution and Ignored results.\n" : "") +
        (Settings?.CutOnly == true ? "Cut only: projection beyond the cut skipped\n" : "") +
        $"{Report.Route}; HLD threading={(NativeCalls == 0 ? "not run" : MultipleThreads ? "multithreaded" : "single-threaded")}; tolerance={Tolerance:R}; validation: {Report.ValidationDomain}\n" +
        $"Visible={Pieces.Count(p => p.Class == LineClass.Visible)}, hidden={Pieces.Count(p => p.Class == LineClass.Hidden)}, unresolved={Report.UncertainCount}, hidden points={HiddenPoints}; " +
        $"invalid extraction={Report.InvalidExtractionCount}; duplicate={DuplicateSegments}; projecting={ProjectingSegments}; empty slots={EmptyNativeSlots}; zero length={ZeroLengthNativeSegments}; quarantined={Report.Quarantined}\n" +
        $"Tolerance counts: adapter tolerance drops={ToleranceDroppedPieces}; source edges at or below 2 x tolerance={SubToleranceSourceEdges}; close source-bound gaps={SubToleranceBoundsGaps}; uncertain section components={UncertainSectionComponents}. Native internal drops=unknown; risk counts are not measured native loss counts.\n" +
        $"Unsupported: {string.Join(", ", Report.UnsupportedByType.Select(p => $"{p.Key}={p.Value}"))}\n" +
        string.Join("; ", Report.Normalisation.Concat(Diagnostics)) +
        $"\nHLD: native candidates={NativeEdges}; native calls={NativeCalls}.\nPhases: {string.Join(", ", Phases.Seconds.Select(p => $"{p.Key}={p.Value:F4}s"))}\nCut regions={Regions.Count}, Hatch={Regions.Count(r => r.Region.Hatch is not null)}, Beyond={Pieces.Count(p => p.Class == LineClass.Beyond)}, Far clip={Pieces.Count(p => p.Class == LineClass.FarClip)}; weights={Weights.BandName}.\n{IgnoredSummary}";
    public int NativeEdges { get; internal set; }
    public int NativeCalls { get; internal set; }
    public int CurvedCandidateEdges { get; internal set; }
    public int NativeFragments { get; internal set; }
    internal int RoutingInvalid;
    internal bool RoutingQuarantine;
    internal bool MultipleThreads = true;
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

    /// <summary>The view being drawn on this flow reports its user-facing step here (UX §5.2 status). Parallel managed
    /// work inherits it; there is no process-global view state.</summary>
    private static readonly AsyncLocal<StepStatus?> Steps = new();
    /// <summary>An engine phase started (PhaseTimings): report its step once when it changes.</summary>
    internal static void ReportPhase(string phase)
    {
        if (Steps.Value is { } steps && ViewSteps.Of(phase) is { } step) steps.Report(step, null);
    }
    /// <summary>Inner-phase progress of the current view (native HLD): <paramref name="done"/> of
    /// <paramref name="total"/> within <paramref name="step"/>. Safe from any thread; only whole-percent changes go out.</summary>
    internal static void ReportStep(string step, long done, long total)
    {
        if (Steps.Value is { } steps && total > 0) steps.Report(step, (double)done / total);
    }
    private sealed class StepProgress(StepStatus steps) : IProgress<double>
    {
        public void Report(double value) { NativeProgress?.Report(value); if (double.IsFinite(value)) steps.Report(ViewSteps.Native, value); }
    }
    /// <summary>Rhino's own HLD progress: the test hook, and the hidden-lines fraction of the current view.</summary>
    internal static IProgress<double>? HldProgress => Steps.Value is { } steps ? new StepProgress(steps) : NativeProgress;

    /// <summary>One view's step and that step's percentage. Reports go out only when the step or its whole percent
    /// changes, so native callbacks from many threads never flood the UI or a worker pipe.</summary>
    private sealed class StepStatus(Action<string, double?> sink)
    {
        private readonly object _gate = new();
        private volatile string? _step;
        private volatile int _percent = -1;
        internal void Report(string step, double? fraction)
        {
            var percent = fraction is double f ? (int)Math.Floor(Math.Clamp(f, 0, 1) * 100) : -1;
            if (ReferenceEquals(step, _step) && (fraction is null || percent == _percent)) return;
            lock (_gate)
            {
                if (step == _step && (fraction is null || percent == _percent)) return;
                // A new step starts without a percentage until its own work reports one.
                if (step != _step) _percent = -1;
                _step = step;
                if (fraction is null) { sink(step, null); return; }
                if (percent < _percent) return; // a step's percentage never goes back
                _percent = percent; sink(step, percent / 100d);
            }
        }
    }
    internal static Action? BeforeCompletion { get; set; }
    private static long _hldCalls;
    private static long _visibilitySolves;
    internal static long VisibilitySolves => Interlocked.Read(ref _visibilitySolves);
    public static long HldCalls => Interlocked.Read(ref _hldCalls);
    internal static Action? NativeStarting { get; set; }
    internal static void CountHld() { Interlocked.Increment(ref _hldCalls); NativeStarting?.Invoke(); }
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
            if (settings.IsWorker ? scene.Cost.Estimate(views.Count) > settings.Scheduler.BudgetBytes : !scene.Cost.Admitted(views.Count, settings.Scheduler.BudgetBytes))
                throw new InvalidOperationException($"{SceneCost.Refusal} (estimate {scene.Cost.Estimate(views.Count):N0} bytes; {scene.Cost.BrepFaces} Brep faces; {scene.Cost.MeshTriangles} triangles; {views.Count} views).");
            return await settings.Scheduler.RunAsync(scene.Cost.Estimate(views.Count), ct =>
            {
                var results = new List<ViewDrawing>();
                try
                {
                    progress?.Report(new(JobPhase.Preparing, 0, scene.SourceCount));
                    var prepareTimer = Stopwatch.StartNew();
                    var prepared = scene.Prepare(ct, (done, total) => progress?.Report(new(JobPhase.Preparing, done, total)));
                    progress?.Report(new(JobPhase.Preparing, scene.SourceCount, scene.SourceCount));
                    double preparationSeconds = prepareTimer.Elapsed.TotalSeconds;
                    var edgeCensus = new Dictionary<double, int>(); // per distinct drawing tolerance (overrides may differ per view)
                    for (var index = 0; index < views.Count; index++)
                    {
                        ct.ThrowIfCancellationRequested();
                        var name = views[index].Name; var at = index;
                        progress?.Report(new(JobPhase.Drawing, index, views.Count) { View = name });
                        Steps.Value = progress is null ? null : new StepStatus((step, fraction) => progress.Report(new(JobPhase.Drawing, at, views.Count) { View = name, Step = step, StepFraction = fraction }));
                        ViewDrawing drawing;
                        try { drawing = SolveView(scene, prepared, views[index], settings, ct); }
                        finally { Steps.Value = null; }
                        drawing.Phases.Add("normalisation", preparationSeconds / views.Count);
                        results.Add(drawing);
                        using var censusPhase = drawing.Phases.Start("tolerance census");
                        if (!edgeCensus.TryGetValue(drawing.Tolerance, out var census))
                            edgeCensus[drawing.Tolerance] = census = ToleranceDiagnostics.SourceEdgeCensus(prepared, drawing.Tolerance, ct);
                        ToleranceDiagnostics.Apply(drawing, prepared, census, ct);
                    }
                    BeforeCompletion?.Invoke();
                    ct.ThrowIfCancellationRequested(); progress?.Report(new(JobPhase.Complete, views.Count, views.Count));
                    return new DrawingBatch(identity, results);
                }
                catch { foreach (var result in results) result.Dispose(); throw; }
            }, token, ahead => progress?.Report(new(JobPhase.Queued, 0, views.Count) { Ahead = ahead })).ConfigureAwait(false);
    }
    private static ViewDrawing SolveView(PreparedScene scene, IReadOnlyList<PreparedLeaf> sources, ViewRequest view, NativeSettings settings, CancellationToken token)
    {
        Interlocked.Increment(ref _visibilitySolves);
        var tolerance = view.Settings?.Tolerance ?? scene.Tolerance;
        view = ViewSourceCull.ResolveView(view, scene.Bounds, scene.Tolerance);
        var original = sources;
        var cullTimer = Stopwatch.StartNew();
        if (settings.CullSources)
        {
            ReportPhase("source culling");
            sources = ViewSourceCull.Filter(sources, view, scene.Bounds, tolerance, token);
        }
        var cullSeconds = cullTimer.Elapsed.TotalSeconds;
        var meshTimer = Stopwatch.StartNew();
        using var meshCrop = new MeshViewCrop(sources, view, scene.Bounds, tolerance, token, settings.CullSources);
        var meshSeconds = meshTimer.Elapsed.TotalSeconds;
        sources = meshCrop.Sources;
        var admissionTimer = Stopwatch.StartNew();
        var pipeAdmission = view.Settings?.Visibility == VisibilityMode.Pipes
            ? PipeProjection.Admit(scene, sources, view, token) : null;
        var admissionSeconds = admissionTimer.Elapsed.TotalSeconds;
        var pipeReason = pipeAdmission?.Reason;
        var drawing = view.Settings?.Visibility == VisibilityMode.Pipes && pipeReason is null && view.Error is null && view.Settings.CutOnly == false
            ? PipeProjection.Solve(scene, pipeAdmission!.Sources, view, token, tolerance)
            : SolveFilteredView(scene, sources, original, view, settings, token, tolerance);
        if (drawing.Report.Route == "pipes (experimental)" && drawing.Error is { } pipeError)
        {
            // The experimental pipes engine failed on an admitted view: draw it with HLD and say so, never a blank view.
            drawing.Dispose();
            drawing = SolveFilteredView(scene, sources, original, view, settings, token, tolerance);
            drawing._diagnostics.Add(pipeError + "; used Rhino HLD");
        }
        if (view.Settings?.Visibility == VisibilityMode.Pipes && pipeReason is not null && view.Settings.CutOnly == false)
            drawing._diagnostics.Add("pipes engine: " + pipeReason + "; used Rhino HLD");
        if (pipeAdmission is not null)
        { drawing.Phases.Add("pipe admission", admissionSeconds); drawing.Seconds += admissionSeconds; }
        if (settings.CullSources)
        {
            drawing.Phases.Add("source culling", cullSeconds);
            drawing.Seconds += cullSeconds;
            drawing._diagnostics.Add($"culled {original.Count - sources.Count} sources outside the view");
            drawing.Phases.Add("mesh face crop", meshSeconds);
            drawing.Seconds += meshSeconds;
            if (meshCrop.ReducedMeshes > 0)
                drawing._diagnostics.Add($"Mesh face crop: dropped {meshCrop.DroppedFaces} faces across {meshCrop.ReducedMeshes} meshes (XY only).");
        }
        return drawing;
    }

    private static ViewDrawing SolveFilteredView(PreparedScene scene, IReadOnlyList<PreparedLeaf> sources,
        IReadOnlyList<PreparedLeaf> original, ViewRequest view, NativeSettings settings, CancellationToken token, double tolerance)
    {
        if (view.Cut is not null && view.Error is null) return SectionDrawing.Solve(scene, sources, view, settings, token,
            DrawingFrames.Resolve(view, original, scene.Bounds, tolerance));
        var result = new ViewDrawing { Name = view.Name, Settings = view.Settings, MultipleThreads = settings.MultipleThreads, Tolerance = tolerance };
        var timer = Stopwatch.StartNew();
        using var glass = new GlassGeometry(sources, view);
        var quarantine = false;
        try
        {
            if (view.Error is not null) { result.Error = view.Error; return result; }
            result.Frame = view.Resolve(scene.Bounds, tolerance);
            if (view.Settings?.CutOnly == true)
            { ReportPhase("sections"); result._diagnostics.Add(ViewNotices.CutOnlyEmpty); return result; }
            result._diagnostics.Add($"Admission: {scene.Cost.GeometryBytes} geometry bytes; {scene.Cost.BrepFaces} faces; {scene.Cost.MeshTriangles} triangles; {scene.SourceCount} sources.");
            HldProjection.Solve(glass.Sources.Select(s => new VisibilityInput(s.Geometry, s)).ToArray(), result, view, settings, scene.Bounds, token);
            quarantine |= result.RoutingQuarantine;
            if (view.Named is { Crop: true })
                foreach (var piece in result.Pieces.ToArray())
                {
                    using var geometry = piece.CopyGeometry();
                    result.Pieces.Remove(piece);
                    try
                    {
                        var source = sources.First(s => s.Identity == piece.SourceIdentity);
                        if (geometry is Curve curve)
                            foreach (var part in Crop(curve, result.Frame, tolerance))
                                result.Pieces.Add(new(part, source, piece.Class, (piece.DepthGeometry as Curve)?.Trim(part.Domain)) { Profile = piece.Profile });
                        else if (geometry is Point point && Inside(point.Location, result.Frame, tolerance))
                            result.Pieces.Add(new(point.Duplicate(), source, piece.Class, piece.DepthGeometry?.Duplicate()));
                    }
                    finally { piece.Dispose(); }
                }
            HldProjection.ApplyFailure(result, quarantine, [], tolerance, token);
        }
        catch (OperationCanceledException) { result.Dispose(); throw; }
        catch (Exception ex) { result.Dispose(); result.Error = ex.Message; }
        finally
        {
            result._diagnostics.Add($"Compute: {HldProjection.Policy}; fingerprint={WorkerCodec.Fingerprint()}; processors={System.Environment.ProcessorCount}.");
            result.Pieces.Sort(ComparePieces);
            result.Report = new("Rhino HLD", result.Items.Count(p => p.Class == LineClass.Unresolved), result.RoutingInvalid,
                scene.Unsupported, scene.Conversions, quarantine) { ValidationDomain = "Rhino HLD; visibility not certified" };
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
            var trimmed = CurveIntervals.Trim(curve, interval);
            if (trimmed is null && curve is LineCurve && curve.PointAt(interval.Min) == curve.PointAt(interval.Max)) continue;
            if (trimmed is null) throw new InvalidOperationException("Named-view crop failed; output suppressed.");
            yield return trimmed;
        }
    }
}
