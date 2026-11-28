using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using M2DPlus.Core;
using M2DPlus.Core.Output;
using M2DPlus.Rhino.Services;
using M2DPlus.Rhino.Sections;
using M2DPlus.Rhino.Views;
using M2DPlus.Grasshopper.UI;
using M2DPlus.Grasshopper.UI.Model;
using Rhino;
using Rhino.Geometry;
using Point = Rhino.Geometry.Point;

namespace M2DPlus.Grasshopper;

public sealed partial class DrawingComponent
{
    private readonly RequestGate _requests = new();
    private System.Windows.Forms.Timer? _timer;
    private DocumentWatch? _watch;
    private SceneCapture? _capture;
    private DrawingBatch? _drawing;
    /// <summary>Output-tree values and display colours captured on the host with the scene that <see cref="_drawing"/>
    /// was drawn from (presentation only; changing them never re-runs visibility).</summary>
    private TreeSources? _drawingSources;
    /// <summary>A View item is text (a named view, clipping plane name or direction), so clipping planes may matter.</summary>
    private bool _textViews;
    /// <summary>SolveInstance calls, scene captures and republished drawings, for the re-run and capture-count tests.</summary>
    private int _solveCount, _captureCount, _restoreCount;
    /// <summary>What a run draws from, without bake-time keys (UX §8.1): inputs, views, Settings text, menu and view-object
    /// rows, Depth, Target, Source, units and tolerance. The published drawing keeps the key it was drawn with.</summary>
    private string? _runKey, _drawingRunKey;
    private long _drawingGeneration;
    /// <summary>The successful drawing taken down by the last expiry. If the next run's key and the document are unchanged (only
    /// <c>bake</c> changed), it is published again as it is: no visibility solve and no presentation.</summary>
    private sealed record Parked(DrawingBatch Batch, TreeSources Sources, string RunKey, long Generation);
    private Parked? _parked;
    /// <summary>The captured scene, kept across runs (owner feedback on beta.1): a Settings, Depth, Target, Source, menu or
    /// view-object change re-solves from it, and only a change to what it read (the capture key, or a scene change in the
    /// document) reads the objects again. The component holds one lease; each job gets its own share.</summary>
    private LeaseEntry<PreparedScene>.Lease? _scene;
    private string? _sceneKey, _captureKey;
    private long _sceneGeneration, _captureGeneration;
    private TreeSources? _sceneSources;
    private string _sceneInfo = "";
    private string? _sceneDropped;
    private Task<LeaseEntry<PreparedScene>.Lease>? _accepting;
    private TreeSources? _acceptingSources;
    // Progress (UX §5.2): the latest report, the job's stage model and clock, first-seen stage times and the result.
    private JobProgressModel? _model;
    private readonly System.Diagnostics.Stopwatch _jobClock = new();
    private ProgressState _state;
    private StatusLine? _status;
    private readonly double?[] _stageSeen = new double?[5]; // loading, waiting, preparing, drawing, output (s since start)
    private double _loadingEnd, _uiSeconds;
    private int _readCount;
    private bool _reused;
    private string? _historyKey;
    private readonly Dictionary<string, StageTimes> _history = new(StringComparer.Ordinal);
    private string _timing = "";
    private GeometryInputKind _inputKind = GeometryInputKind.Document;
    private sealed record TreeSources(GeometryInputKind Kind, IReadOnlyDictionary<string, SourceGroupInfo> Groups, IReadOnlyDictionary<string, uint> Colours);
    private readonly DrawingCache _drawingCache = new();
    private Plane[] _targets = Array.Empty<Plane>();
    private Plane[] _sources = Array.Empty<Plane>();
    private List<CaptureInput>? _inputs;
    private ViewRequest[] _views = Array.Empty<ViewRequest>();
    private NativeSettings _settings = new();
    private readonly List<(GH_RuntimeMessageLevel Level, string Text)> _settingsWarnings = new();
    // Settings resolution inputs captured on the host in SolveDrawing, resolved after capture (scene depth known).
    private UiContext _context = UiContext.Empty;
    private string[] _effective = Array.Empty<string>();
    private HashSet<Guid> _excluded = new();
    private double _tolerance = .001;
    private long _epoch;
    private bool _internalRefresh;
    private bool _removed;
    private bool _started;
    private string? _error;

    private JobProgress? _progress;
    private readonly object _progressSync = new();
    private readonly object _completionSync = new();
    private sealed record Completion(RequestIdentity Identity, long Epoch, long DocumentGeneration, DrawingBatch? Batch, string? Error, TreeSources Sources,
        string? RunKey);
    private Completion? _completion;

    // Model Object/View values are host-only GH adapters; compute never sees a GH wrapper.
    internal static object? Value(IGH_Goo? goo)
    {
        var value = goo?.ScriptVariable();
        if (value is global::Grasshopper.Rhinoceros.Model.ModelObject model)
        {
            GeometryBase geometry = null!;
            if (model.Id is { } id && RhinoDoc.ActiveDoc?.Objects.FindId(id) is not null) return id;
            if (model.CastTo(out geometry)) return geometry;
        }
        if (value is global::Grasshopper.Rhinoceros.Display.ModelView view) return view.ToViewportInfo();
        return value;
    }
    private void SolveDrawing(IGH_DataAccess da)
    {
        _solveCount++;
        var uiSince = System.Diagnostics.Stopwatch.GetTimestamp();
        try { SolveDrawingCore(da); }
        finally { _uiSeconds += System.Diagnostics.Stopwatch.GetElapsedTime(uiSince).TotalSeconds; }
    }
    private void SolveDrawingCore(IGH_DataAccess da)
    {
        foreach (var (level, text) in Ui.Notes) AddRuntimeMessage(level, text);
        if (_drawing is not null) { Emit(da, _drawing, _drawingSources!); return; }
        if (_error is not null) { Message = "Error"; AddRuntimeMessage(GH_RuntimeMessageLevel.Error, _error); return; }
        var inputViews = new List<IGH_Goo>();
        if (!da.GetDataList(1, inputViews) || inputViews.Count == 0) { Message = "Pick a view"; return; }
        if (_started) return;
        if (!Ui.ShouldStart()) { Message = "Paused · out of date"; return; }
        // Resolve once on the host thread. No worker ever reads ActiveDoc.
        var document = RhinoDoc.ActiveDoc;
        if (document is null) { Message = "Error"; AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "No Rhino document is open."); return; }
        if (_watch is null || _watch.DocumentSerial != document.RuntimeSerialNumber)
        {
            DetachWatch(); _watch = new(document); _watch.Changed += DocumentChanged; _watch.ViewsChanged += ViewsChanged;
        }
        _tolerance = document.ModelAbsoluteTolerance;
        // Not-yet-converted frame surfaces face the model; their scene bounds exclude every referenced view object.
        var referenced = inputViews.Select(ViewObjects.IdOf).OfType<Guid>().ToArray();
        BoundingBox? frameScene = referenced.Any(id => document.Objects.FindId(id) is { } o && !ViewBoxes.IsViewBox(o) && o is not global::Rhino.DocObjects.ClippingPlaneObject)
            ? ViewBoxes.SceneBounds(document, referenced) : null;
        _views = inputViews.Select(v => ViewRequest.Parse(v is IGH_GeometricGoo { IsReferencedGeometry: true } reference && reference.ReferenceID != Guid.Empty
            ? reference.ReferenceID : Value(v), document, frameScene)).ToArray();
        _excluded = _views.Where(v => v.ObjectId != Guid.Empty && v.Kind != ViewSourceKind.ClippingPlane).Select(v => v.ObjectId).ToHashSet();
        _textViews = inputViews.Any(v => v is GH_String);
        da.GetDataTree(0, out GH_Structure<IGH_Goo> geometry);
        _inputs = null;
        if (geometry is not null && geometry.DataCount > 0)
        {
            _inputs = new();
            for (var branch = 0; branch < geometry.PathCount; branch++)
                for (var index = 0; index < geometry.Branches[branch].Count; index++)
                {
                    var goo = geometry.Branches[branch][index];
                    var value = goo is IGH_GeometricGoo { IsReferencedGeometry: true } reference && reference.ReferenceID != Guid.Empty
                        ? reference.ReferenceID : Value(goo);
                    _inputs.Add(new(value ?? "Null input", geometry.Paths[branch].Indices,
                        $"input/{geometry.Paths[branch]}/{index}/{(value is Guid objectId ? objectId.ToString() : "local")}") { SourceItem = index });
                }
        }
        // What a capture would read (UX §5.1a): equal keys and no scene change reuse the captured scene.
        _captureKey = SceneCapture.InputKey(document, _inputs, _excluded);
        _historyKey = null;
        // tree = auto (UX §1.3): ids and layer names select Rhino objects; anything else is Grasshopper geometry.
        _inputKind = OutputTree.Classify(_inputs is null, (_inputs ?? []).Select(i => i.Value is Guid or string));
        var targets = new List<Plane>(); da.GetDataList(2, targets); _targets = targets.ToArray();
        if (_targets.Length != 0 && _targets.Length != 1 && _targets.Length != _views.Length)
        { _error = "Give 1 target, or one per view"; AddRuntimeMessage(GH_RuntimeMessageLevel.Error, _error); return; }
        var sources = new List<Plane>();
        if (Params.Input.Count > ComponentInputs.Source) da.GetDataList(ComponentInputs.Source, sources);
        _sources = sources.ToArray();
        if (_sources.Length != 0 && _sources.Length != 1 && _sources.Length != _views.Length)
        { _error = "Give 1 source, or one per view"; AddRuntimeMessage(GH_RuntimeMessageLevel.Error, _error); return; }
        _settingsWarnings.Clear(); _settings = new();
        if (_sources.Length > 0 && _targets.Length == 0)
            _settingsWarnings.Add((GH_RuntimeMessageLevel.Remark, "Source is given without Target; the drawing stays in place."));
        da.GetDataTree(3, out GH_Structure<GH_String> settings);
        var branches = new List<IReadOnlyList<string>>();
        if (settings is not null)
            foreach (var path in settings.Paths.OrderBy(p => p))
            {
                // multiplethreads is a host diagnostic switch, not a UX setting: read it here and keep it from Core.
                var lines = new List<string>();
                foreach (var item in settings[path])
                    foreach (var line in (item?.Value ?? "").Replace("\r\n", "\n").Split('\n'))
                    {
                        var parts = line.Split('=', 2);
                        if (parts.Length == 2 && parts[0].Trim().Equals("multiplethreads", StringComparison.OrdinalIgnoreCase))
                            _settings = new();
                        else lines.Add(line);
                    }
                branches.Add(lines);
            }
        var depth = new List<GH_String>();
        if (Params.Input.Count > 4) da.GetDataList(4, depth);
        var rows = new Dictionary<string, SettingsRow>(StringComparer.Ordinal);
        var contexts = new List<ViewSettingsContext>();
        for (var i = 0; i < _views.Length; i++)
        {
            var view = _views[i];
            var identity = view.ObjectId != Guid.Empty ? DrawingUi.Identity(view.ObjectId)
                : inputViews[i] is GH_String text ? "text:" + text.Value.Trim().ToLowerInvariant() : $"index:{i}";
            if (view.ObjectId != Guid.Empty && document.Objects.FindId(view.ObjectId) is { } obj)
            {
                rows[identity] = ViewBoxes.ReadRow(obj.Attributes);
                // m2d.color only colours the view box's front face in Rhino; a bad value falls back to by type.
                if (M2DPlus.Core.Views.ViewBoxFill.Validate(obj.Attributes.GetUserString(ViewBoxes.ColorKey), view.Name) is { } colour)
                    _settingsWarnings.Add((GH_RuntimeMessageLevel.Remark, colour));
            }
            contexts.Add(new(identity, view.Name, view.Kind, view.Cut is not null, 0, _tolerance,
                view.Kind is ViewSourceKind.Box or ViewSourceKind.ClippingPlane ? view.Cut?.SourceDepth : null));
            if (view.Note is { } note) _settingsWarnings.Add((GH_RuntimeMessageLevel.Remark, note));
        }
        _context = new UiContext(contexts, branches, DrawingUi.Units(document)) { ViewRows = rows, Depth = depth.Select(d => d?.Value).ToArray(), DocumentTolerance = _tolerance };
        Ui.Context = () => _context;
        // Only retained volumes key capture reuse: style, placement and hidden changes still reuse the scene.
        _captureKey += "|scope=" + SceneCapture.ScopeKey(ScopeViews(BoundingBox.Empty), _tolerance);
        Previews(BoundingBox.Empty);
        // The progress weights of the next run come from this one when the inputs, views and settings are the same.
        _historyKey = _captureKey + "|" + string.Join("|", _views.Select(v => v.Revision)) + "|" + string.Join("\n", branches.Select(Settings.RunText)) +
            "|" + string.Join(",", depth.Select(d => d?.Value)) + "|" + Settings.RunText(Ui.State.Menu) + "|" + string.Join(",", _targets) + "|" + string.Join(",", _sources);
        const char Unit = '\u001f';
        _runKey = string.Join(Unit, [_captureKey ?? "", string.Join(Unit, _views.Select(v => v.Revision)),
            string.Join(Unit, branches.Select(Settings.RunText)), string.Join(Unit, depth.Select(d => d?.Value)), Settings.RunText(Ui.State.Menu),
            string.Join(Unit, rows.OrderBy(r => r.Key, StringComparer.Ordinal).Select(r => r.Key + "=" + Settings.RunText(r.Value))),
            string.Join(Unit, _targets.Select(PlaneRevision)), string.Join(Unit, _sources.Select(PlaneRevision)),
            _settings.ToString(), _context.Units.ToString(), _tolerance.ToString("R", System.Globalization.CultureInfo.InvariantCulture)]);
        if (new SettingsPresentation(Ui.State, _context).Resolution?.Views.Any(v => v.Visibility == VisibilityMode.Raster) == true)
        {
            var rasterIdentity = M2DPlus.Rhino.Experimental.RasterVisibility.RasterEngineIdentity.Value;
            _runKey += Unit + rasterIdentity; _historyKey += "|" + rasterIdentity;
        }
        _started = true; _uiSeconds = 0; _jobClock.Restart(); Array.Clear(_stageSeen); _reused = false; _loadingEnd = 0;
        _model = null; _state = default; _status = new("Starting…"); Message = _status.Full;
        _timer ??= CreateTimer(); _timer.Interval = _parked is null ? 250 : 1; _timer.Start();
    }
    private System.Windows.Forms.Timer CreateTimer()
    {
        var timer = new System.Windows.Forms.Timer(); timer.Tick += CaptureTick; return timer;
    }
    // Keep placement edits smaller than Rhino's display precision distinct when deciding whether to restore a parked drawing.
    private static string PlaneRevision(Plane plane) => new NamedFrame(plane, new(0, 1), new(0, 1)).Revision;
    private void CaptureTick(object? sender, EventArgs args)
    {
        var uiSince = System.Diagnostics.Stopwatch.GetTimestamp();
        try { CaptureTickCore(); }
        finally { _uiSeconds += System.Diagnostics.Stopwatch.GetElapsedTime(uiSince).TotalSeconds; }
    }
    private void CaptureTickCore()
    {
        if (_removed || _watch is null || _watch.IsClosed) { Invalidate(); return; }
        try
        {
            Completion? completed;
            lock (_completionSync) { completed = _completion; _completion = null; }
            if (completed is not null)
            {
                _watch.VerifyHostState();
                if (completed.Epoch != _epoch || _watch.IsClosed || _watch.Generation != completed.DocumentGeneration)
                { completed.Batch?.Dispose(); return; }
                if (completed.Batch is { } batch && _requests.TryPublish(completed.Identity, batch.Complete, () =>
                    { _drawing = batch; _drawingSources = completed.Sources; _drawingRunKey = completed.RunKey; _drawingGeneration = completed.DocumentGeneration; }))
                { Finish(); _timer?.Stop(); Refresh(true); return; }
                completed.Batch?.Dispose();
                if (_requests.IsCurrent(completed.Identity)) { _timer?.Stop(); _error = completed.Error ?? "Cancelled"; Refresh(true); }
                return;
            }
            if (_accepting is { } accepting)
            {
                if (!accepting.IsCompleted) { Status(new(JobPhase.Reading, _readCount, _readCount)); return; }
                _accepting = null;
                var captured = accepting.GetAwaiter().GetResult(); // rethrows a background failure
                if (_watch.Generation != _captureGeneration) { captured.Dispose(); throw new OperationCanceledException("The document changed while reading it."); }
                DropScene();
                _scene = captured; _sceneKey = _captureKey; _sceneGeneration = _captureGeneration; _sceneSources = _acceptingSources;
                _loadingEnd = _jobClock.Elapsed.TotalSeconds;
                StartJob();
                return;
            }
            if (_capture is null && _model is not null)
            {
                lock (_progressSync) if (_progress is { } progress) Status(progress);
                return;
            }
            if (_capture is null)
            {
                var document = RhinoDoc.FromRuntimeSerialNumber(_watch.DocumentSerial);
                if (document is null) { Invalidate(); return; }
                // Headless documents raise no events: compare now, before deciding whether the scene can be reused.
                _watch.VerifyHostState();
                if (!_started) return;
                var reuse = _scene is not null && _sceneKey == _captureKey && _sceneGeneration == _watch.Generation;
                // One stage model per job, weighted by the previous run of the same inputs when there was one.
                _model = new JobProgressModel(_historyKey is null ? null : _history.GetValueOrDefault(_historyKey), loading: !reuse);
                if (reuse)
                {
                    _reused = true;
                    _watch.Scope?.SetViews(_views.Select(v => v.ObjectId), _textViews);
                    if (!Restore()) StartJob();
                    return;
                }
                DisposeParked();
                DropScene();
                // What the drawing reads, so the watch re-runs only for changes to it (UX §5.1).
                var scope = new WatchScope(_inputs is null ? M2DPlus.Core.Watch.WatchMode.Document : M2DPlus.Core.Watch.WatchMode.Referenced,
                    _inputs?.Select(i => i.Value).OfType<string>(), _views.Select(v => v.ObjectId), _textViews);
                _capture = new(document, _watch, _inputs, excluded: _excluded, scope: scope, scopeViews: ScopeViews);
                _captureGeneration = _watch.Generation; _captureCount++;
                _stageSeen[0] = _jobClock.Elapsed.TotalSeconds;
                // About 16 ms of work per tick (the UI stays responsive); the timer's own granularity sets the gaps.
                _timer!.Interval = 1;
            }
            var done = _capture.Step(TimeSpan.FromMilliseconds(16));
            Status(new(JobPhase.Reading, _capture.CapturedCount, _capture.CapturedCount + _capture.PendingCount) { Step = _capture.CaptureStep });
            if (!done) return;
            _acceptingSources = new TreeSources(_inputKind, new Dictionary<string, SourceGroupInfo>(_capture.Groups, StringComparer.Ordinal),
                new Dictionary<string, uint>(_capture.Colours, StringComparer.Ordinal));
            _sceneDropped = _capture.DroppedSummary; _readCount = _capture.CapturedCount;
            var profile = string.Join(", ", _capture.Profile.Where(p => p.Milliseconds >= .5)
                .Select(p => $"{p.Phase} {p.Milliseconds:F0} ms{(SceneCapture.OnHostThread(p.Phase) ? "" : " (background)")}"));
            var scoped = _capture.Scoped ? $"excluded {_capture.ScopedOutCount:N0} object{(_capture.ScopedOutCount == 1 ? "" : "s")} outside the view boxes before capture. " : "";
            _sceneInfo = $"{scoped}Objects read: {_capture.CapturedCount:N0} in {_capture.Slices} slices, longest {_capture.MaximumSliceMilliseconds:F0} ms; {profile}.";
            _accepting = _capture.AcceptAsync();
            _capture.Dispose(); _capture = null; _inputs = null;
            _timer!.Interval = 15;
        }
        catch (OperationCanceledException) { Invalidate(); Refresh(false); }
        catch (Exception ex) { _timer?.Stop(); _capture?.Dispose(); _capture = null; _error = ex.Message; Refresh(true); }
    }

    /// <summary>Starts the drawing job from the retained scene (a new share of its lease).</summary>
    private void StartJob()
    {
        var scene = _scene!.Share();
        ResolveSettings(scene.Value.Bounds);
        var request = _requests.Begin(scene.Value.Revision, string.Join("|", _views.Select(v => v.Revision)), _settings.ToString());
        var epoch = _epoch; var documentGeneration = _watch!.Generation;
        lock (_progressSync) _progress = new(JobPhase.Preparing, 0, 0);
        Status(new(JobPhase.Preparing, 0, 0));
        _timer!.Interval = 100;
        _ = RunDrawing(scene, request.Identity, request.Token, epoch, documentGeneration, _views, _settings, _sceneSources!, _runKey);
    }

    /// <summary>Only bake-time keys changed (UX §8.1): the parked drawing was drawn from the same scene, views, inputs and
    /// settings, so it is published again as it is. Settings are resolved again for Info and the next bake.</summary>
    private bool Restore()
    {
        if (_parked is not { } parked) return false;
        _parked = null;
        if (_runKey is null || parked.RunKey != _runKey || parked.Generation != _watch!.Generation || _scene is null)
        { parked.Batch.Dispose(); return false; }
        ResolveSettings(_scene.Value.Bounds);
        _drawing = parked.Batch; _drawingSources = parked.Sources; _drawingRunKey = parked.RunKey; _drawingGeneration = parked.Generation;
        _restoreCount++; _model = null; _status = null; _state = default; _timer?.Stop();
        Refresh(true);
        return true;
    }

    /// <summary>Folds a report into the stage model and the status line. UI thread only; never redraws Rhino's viewports
    /// (the canvas footer repaints itself at most every 100 ms while a job runs).</summary>
    private void Status(JobProgress progress)
    {
        var elapsed = _jobClock.Elapsed.TotalSeconds;
        var stage = progress.Phase switch { JobPhase.Reading => 0, JobPhase.Queued => 1, JobPhase.Preparing => 2, JobPhase.Drawing => 3, JobPhase.Output or JobPhase.Complete => 4, _ => -1 };
        if (stage >= 0) _stageSeen[stage] ??= elapsed;
        _model ??= new JobProgressModel(null);
        _state = _model.Update(progress, elapsed);
        _status = StatusText.Of(progress, _state.RemainingSeconds);
        if (Message != _status.Full) Message = _status.Full;
    }

    /// <summary>Stage times of the finished job: Info's timing line and the next run's progress weights.</summary>
    private void Finish()
    {
        var total = _jobClock.Elapsed.TotalSeconds;
        double At(int i, double fallback) => _stageSeen[i] ?? fallback;
        var output = At(4, total); var drawing = At(3, output); var preparing = At(2, drawing); var waiting = At(1, preparing);
        var loading = _reused ? 0 : Math.Max(0, _loadingEnd - At(0, 0));
        var times = new StageTimes(loading, Math.Max(0, drawing - preparing), Math.Max(0, output - drawing), Math.Max(0, total - output));
        if (_historyKey is not null)
        {
            if (_history.Count > 16) _history.Clear();
            _history[_historyKey] = times;
        }
        var wait = Math.Max(0, preparing - waiting);
        _timing = $"Timing: loading {(_reused ? "skipped (objects reused)" : $"{loading:F2} s")} · " +
            (wait >= .01 ? $"waiting {wait:F2} s · " : "") +
            $"preparing {times.Preparing:F2} s · drawing {times.Drawing:F2} s · output {times.Output:F2} s · total {total:F2} s; " +
            $"UI thread busy {_uiSeconds:F2} s.";
    }

    private IReadOnlyList<ViewRequest> ScopeViews(BoundingBox bounds)
    {
        var contexts = _context.Views.Select((c, i) => c with { SceneDepth = SceneDepth(_views[i], bounds) }).ToArray();
        var resolution = Settings.Resolve(contexts, _context.InputBranches, _context.Units, Ui.State.ToEditorSettings(_context.ViewRows), _context.Depth);
        return _views.Select((v, i) => v with { Settings = resolution.Views[i] }).ToArray();
    }

    /// <summary>Resolve every key on the host; the engine receives immutable values only.</summary>
    private void ResolveSettings(BoundingBox bounds)
    {
        var contexts = _context.Views.Select((c, i) => c with { SceneDepth = SceneDepth(_views[i], bounds) }).ToArray();
        var resolution = Settings.Resolve(contexts, _context.InputBranches, _context.Units, Ui.State.ToEditorSettings(_context.ViewRows), _context.Depth);
        foreach (var diagnostic in resolution.Diagnostics)
        {
            var where = diagnostic.ViewIndex is int v && v < _views.Length ? $"{_views[v].Name}: " : "";
            var level = diagnostic.Severity switch { DiagnosticSeverity.Error => GH_RuntimeMessageLevel.Error,
                DiagnosticSeverity.Warning => GH_RuntimeMessageLevel.Warning, _ => GH_RuntimeMessageLevel.Remark };
            _settingsWarnings.Add((level, where + diagnostic.Message));
        }
        _effective = resolution.Views.Select(s => s.Describe()).ToArray();
        for (var i = 0; i < _views.Length; i++) _views[i] = _views[i] with { Settings = resolution.Views[i] };
        Previews(bounds);
    }

    private double SceneDepth(ViewRequest view, BoundingBox bounds)
    {
        if (!bounds.IsValid) return 0;
        if (view.Cut is { } cut) return Math.Max(0, -bounds.GetCorners().Min(cut.Plane.DistanceTo)) + _tolerance * 10;
        var plane = view.Resolve(bounds, _tolerance).Plane;
        return Math.Max(0, -bounds.GetCorners().Min(plane.DistanceTo)) + _tolerance * 10;
    }

    /// <summary>Keeps the view's name across step reports that don't repeat it.</summary>
    private static JobProgress MergeProgress(JobProgress? previous, JobProgress next) =>
        next.View is null && previous is { } p && p.Phase == next.Phase && p.Done == next.Done && next.Phase == JobPhase.Drawing ? next with { View = p.View } : next;
    private sealed class ProgressSink(Action<JobProgress> report) : IProgress<JobProgress>
    { public void Report(JobProgress value) => report(value); }
    private async Task RunDrawing(LeaseEntry<PreparedScene>.Lease scene, RequestIdentity identity, CancellationToken token,
        long epoch, long documentGeneration, ViewRequest[] views, NativeSettings settings, TreeSources sources, string? runKey)
    {
        DrawingBatch? batch = null;
        string? error = null;
        try
        {
            batch = await _drawingCache.SolveAsync(scene, identity, views, settings, _targets, token,
                new ProgressSink(p => _requests.TryUpdate(identity, () => { lock (_progressSync) _progress = MergeProgress(_progress, p); })), _sources).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { error = ex.Message; }
        // A host timer drains this mailbox; the task never calls Rhino/GH/UI dispatch APIs.
        lock (_completionSync)
        {
            if (epoch != Interlocked.Read(ref _epoch)) { batch?.Dispose(); return; }
            _completion?.Batch?.Dispose();
            _completion = new(identity, epoch, documentGeneration, batch, error, sources, runKey);
        }
    }
    private void Emit(IGH_DataAccess da, DrawingBatch batch, TreeSources sources)
    {
        var visible = new GH_Structure<IGH_GeometricGoo>(); var hidden = new GH_Structure<GH_Curve>();
        var beyond = new GH_Structure<GH_Curve>(); var farClip = new GH_Structure<GH_Curve>(); var cut = new GH_Structure<GH_Curve>(); var hatch = new GH_Structure<GH_Hatch>();
        var unresolved = new GH_Structure<GH_Curve>(); var frames = new GH_Structure<GH_Rectangle>(); var info = new GH_Structure<GH_String>();
        var ignored = new GH_Structure<GH_Curve>(); var keys = new GH_Structure<GH_String>();
        for (var index = 0; index < batch.Views.Count; index++)
        {
            var view = batch.Views[index];
            using var publicationPhase = view.Phases.Start("GH publication");
            var viewPath = new GH_Path(index);
            var effective = index < _effective.Length ? _effective[index] + "\n" : "";
            // Grouping is presentation over host-captured values: changing tree never re-runs visibility (UX §1.3).
            var treeSetting = view.Settings?.Tree ?? OutputTreeMode.Auto;
            var layout = new OutputTreeLayout(OutputTree.Effective(treeSetting, sources.Kind), sources.Groups);
            var tree = "Output tree: " + OutputTree.Describe(treeSetting, sources.Kind) + "\n";
            string Info() => view.Info + "\n" + tree + effective + _timing + "\n" + _sceneInfo + "\n" +
                (_sceneDropped is { } dropped ? dropped + "\n" : "") + string.Join("\n", _settingsWarnings.Select(w => w.Text));
            GH_Path Group(IEnumerable<int> group) => new(new[] { index }.Concat(group).ToArray());
            if (view.Error is not null) { AddRuntimeMessage(GH_RuntimeMessageLevel.Error, view.Error); info.Append(new(Info()), viewPath); continue; }
            frames.Append(new(view.Frame), viewPath);
            foreach (var item in view.Items)
            {
                var copy = item.CopyGeometry();
                if (copy is Curve curve)
                {
                    var path = Group(layout.Line(item.SourceIdentity));
                    if (item.Class is LineClass.Visible or LineClass.Profile) visible.Append(new GH_Curve(curve), path);
                    else if (item.Class == LineClass.Beyond) beyond.Append(new(curve), path);
                    else if (item.Class == LineClass.Hidden) hidden.Append(new(curve), path);
                    else if (item.Class == LineClass.FarClip) farClip.Append(new(curve), path);
                    else if (item.Class == LineClass.Ignored) ignored.Append(new(curve), path);
                    else unresolved.Append(new(curve), path);
                }
                else if (copy is Point point)
                {
                    if (item.Class is LineClass.Visible or LineClass.Profile) visible.Append(new GH_Point(point.Location), Group(layout.Line(item.SourceIdentity)));
                    point.Dispose();
                }
                else copy.Dispose();
            }
            foreach (var entry in view.Regions)
            {
                // {view; group; region}: a merged region takes the lowest group that took part (UX §1.3).
                var path = Group(layout.Region(entry.Region.SourceIds));
                foreach (var loop in entry.Region.Loops) cut.Append(new(loop.Curve.DuplicateCurve()), path);
                if (entry.Region.Hatch is { } fill)
                {
                    using var pattern = SectionHatches.CreatePattern(entry.Region.Style.Pattern ?? new M2DPlus.Core.Sections.PatternDefinition("Solid", true, Array.Empty<M2DPlus.Core.Sections.PatternLine>()));
                    var copy = (Hatch)fill.Duplicate();
                    copy.SetUserString("M2D.SectionStyle", System.Text.Json.JsonSerializer.Serialize(entry.Region.Style));
                    hatch.Append(new GH_Hatch(copy, new global::Grasshopper.Rhinoceros.Drafting.ModelHatchPattern(pattern)), path);
                }
                else hatch.Append(null!, path);
            }
            foreach (var group in layout.Keys) keys.Append(new(group.Key), Group(group.Path));
            publicationPhase.Pause();
            info.Append(new(Info()), viewPath);
            // Severity policy (UX §5.2): a warning means the user has to act; a normal run's findings are remarks.
            foreach (var notice in ViewNotices.For(view)) AddRuntimeMessage(Level(notice.Level), notice.Text);
        }
        foreach (var (level, text) in _settingsWarnings) AddRuntimeMessage(level, text);
        if (_sceneDropped is { } droppedNote) AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, droppedNote);
        var good = batch.Views.Count(v => v.Error is null);
        Message = good == batch.Views.Count ? $"{good} drawings · {batch.Views.Sum(v => v.Seconds):F1} s" : $"Partial · {good}/{batch.Views.Count} drawings";
        da.SetDataTree(0, visible); da.SetDataTree(1, hidden); da.SetDataTree(2, beyond); da.SetDataTree(3, farClip); da.SetDataTree(4, cut); da.SetDataTree(5, hatch); da.SetDataTree(6, unresolved); da.SetDataTree(7, frames); da.SetDataTree(8, info);
        if (Params.Output.Count > 9) da.SetDataTree(9, ignored);
        if (Params.Output.Count > 10) da.SetDataTree(10, keys);
    }

    internal static GH_RuntimeMessageLevel Level(NoticeLevel level) => level switch
    {
        NoticeLevel.Error => GH_RuntimeMessageLevel.Error, NoticeLevel.Warning => GH_RuntimeMessageLevel.Warning, _ => GH_RuntimeMessageLevel.Remark,
    };

    private void Refresh(bool preserve)
    {
        if (_removed) return;
        OnPingDocument()?.ScheduleSolution(1, _ =>
        {
            if (_removed) return;
            _internalRefresh = preserve;
            try { ExpireSolution(false); } finally { _internalRefresh = false; }
        });
    }
    /// <summary>A change to what the scene read: read the objects again on the next run.</summary>
    private void DocumentChanged() { DropScene(); Invalidate(); Refresh(false); }
    /// <summary>A change to view objects only: re-run from the captured scene, or republish the drawing when only their
    /// bake-time text (<c>m2d.bake</c>) changed.</summary>
    private void ViewsChanged() { Park(); Refresh(false); }
    /// <summary>Takes the drawing down like <see cref="Invalidate"/>, but keeps successful results for <see cref="Restore"/>.
    /// Authored view errors are stable; Run now must retry execution failures even with unchanged inputs.</summary>
    private void Park()
    {
        if (_drawing is { Complete: true } drawing &&
            drawing.Views.Select((v, i) => v.Error is null || _views[i].Error is not null).All(reusable => reusable) &&
            _drawingRunKey is not null && _drawingSources is not null)
        {
            DisposeParked();
            _parked = new(drawing, _drawingSources, _drawingRunKey, _drawingGeneration);
            _drawing = null;
        }
        Invalidate(keepParked: true);
    }
    private void DisposeParked() { _parked?.Batch.Dispose(); _parked = null; }
    private void DropScene() { _scene?.Dispose(); _scene = null; _sceneKey = null; _sceneSources = null; }
    private void AbandonAccept()
    {
        if (_accepting is not { } accepting) return;
        _accepting = null;
        accepting.ContinueWith(t => { if (t.IsCompletedSuccessfully) t.Result.Dispose(); }, TaskScheduler.Default);
    }
    private void Invalidate(bool keepParked = false)
    {
        Interlocked.Increment(ref _epoch); _requests.Cancel(); _timer?.Stop(); _capture?.Dispose(); _capture = null; AbandonAccept();
        _model = null; _status = null; _state = default;
        lock (_completionSync) { _completion?.Batch?.Dispose(); _completion = null; }
        if (!keepParked) DisposeParked();
        _drawing?.Dispose(); _drawing = null; _drawingSources = null; _drawingRunKey = null; _inputs = null; _started = false; _error = null;
        lock (_progressSync) _progress = null;
        Message = "Cancelled";
    }
    private void DetachWatch()
    { DropScene(); if (_watch is not null) { _watch.Changed -= DocumentChanged; _watch.ViewsChanged -= ViewsChanged; _watch.Dispose(); _watch = null; } }
    public override void ClearData()
    {
        if (!_internalRefresh)
        {
            // Any expiry (an input, the menu, Run now) parks a successful drawing; unchanged failures must retry.
            Park();
            if (Params.Input.Count > 1 && Params.Input[1].Sources.Count == 0 &&
                Params.Input[1] is global::Grasshopper.Kernel.Parameters.Param_GenericObject { PersistentDataCount: 0 })
                Message = "Pick a view";
        }
        base.ClearData();
    }
    private void EscapePressed(object? sender, EventArgs args)
    { if (Attributes?.Selected == true && _started && _drawing is null) { Invalidate(); OnDisplayExpired(true); } }
    public override void AddedToDocument(GH_Document document)
    { _removed = false; RhinoApp.EscapeKeyPressed += EscapePressed; base.AddedToDocument(document); }
    public override void RemovedFromDocument(GH_Document document)
    {
        RhinoApp.EscapeKeyPressed -= EscapePressed; _removed = true; Invalidate(); _drawingCache.Clear(); DetachWatch(); _timer?.Dispose(); _timer = null;
        base.RemovedFromDocument(document);
    }
    public override void DocumentContextChanged(GH_Document document, GH_DocumentContext context)
    {
        if (context == GH_DocumentContext.Close) { Invalidate(); _drawingCache.Clear(); DetachWatch(); }
        base.DocumentContextChanged(document, context);
    }
}
