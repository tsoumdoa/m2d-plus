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
    /// <summary>SolveInstance calls, for the re-run tests.</summary>
    private int _solveCount;
    private GeometryInputKind _inputKind = GeometryInputKind.Document;
    private sealed record TreeSources(GeometryInputKind Kind, IReadOnlyDictionary<string, SourceGroupInfo> Groups, IReadOnlyDictionary<string, uint> Colours);
    private readonly DrawingCache _drawingCache = new();
    private Plane[] _targets = Array.Empty<Plane>();
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
    private string _captureInfo = "";
    private double _captureActiveSeconds;
    private JobProgress? _progress;
    private readonly object _progressSync = new();
    private readonly object _completionSync = new();
    private sealed record Completion(RequestIdentity Identity, long Epoch, long DocumentGeneration, DrawingBatch? Batch, string? Error, TreeSources Sources);
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
            DetachWatch(); _watch = new(document); _watch.Changed += DocumentChanged;
        }
        _tolerance = document.ModelAbsoluteTolerance;
        // Not-yet-validated frame surfaces face the model; their scene bounds exclude every referenced view object.
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
        // tree = auto (UX §1.3): ids and layer names select Rhino objects; anything else is Grasshopper geometry.
        _inputKind = OutputTree.Classify(_inputs is null, (_inputs ?? []).Select(i => i.Value is Guid or string));
        var targets = new List<Plane>(); da.GetDataList(2, targets); _targets = targets.ToArray();
        if (_targets.Length != 0 && _targets.Length != 1 && _targets.Length != _views.Length)
        { _error = "Give 1 target, or one per view"; AddRuntimeMessage(GH_RuntimeMessageLevel.Error, _error); return; }
        _settingsWarnings.Clear(); _settings = new();
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
                            _settings = new(parts[1].Trim().ToLowerInvariant() is "on" or "true" or "yes" or "1");
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
        _context = new UiContext(contexts, branches, DrawingUi.Units(document)) { ViewRows = rows, Depth = depth.Select(d => d?.Value).ToArray() };
        Ui.Context = () => _context;
        Previews(BoundingBox.Empty);
        _started = true; Message = "Reading";
        _timer ??= CreateTimer(); _timer.Interval = 250; _timer.Start();
    }
    private System.Windows.Forms.Timer CreateTimer()
    {
        var timer = new System.Windows.Forms.Timer(); timer.Tick += CaptureTick; return timer;
    }
    private void CaptureTick(object? sender, EventArgs args)
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
                if (completed.Batch is { } batch && _requests.TryPublish(completed.Identity, batch.Complete, () => { _drawing = batch; _drawingSources = completed.Sources; }))
                { _timer?.Stop(); Refresh(true); return; }
                completed.Batch?.Dispose();
                if (_requests.IsCurrent(completed.Identity)) { _timer?.Stop(); _error = completed.Error ?? "Cancelled"; Refresh(true); }
                return;
            }
            if (_capture is null)
            {
                lock (_progressSync)
                {
                    if (_progress is { } progress)
                    {
                        Message = progress.Phase switch
                        {
                            JobPhase.Queued => "Queued", JobPhase.Preparing => "Preparing",
                            JobPhase.Drawing => $"Drawing {progress.Done + 1}/{progress.Total}", _ => Message
                        };
                        OnDisplayExpired(true); return;
                    }
                }
                var document = RhinoDoc.FromRuntimeSerialNumber(_watch.DocumentSerial);
                if (document is null) { Invalidate(); return; }
                // What the drawing reads, so the watch re-runs only for changes to it (UX §5.1).
                var scope = new WatchScope(_inputs is null ? M2DPlus.Core.Watch.WatchMode.Document : M2DPlus.Core.Watch.WatchMode.Referenced,
                    _inputs?.Select(i => i.Value).OfType<string>(), _views.Select(v => v.ObjectId), _textViews);
                _capture = new(document, _watch, _inputs, excluded: _excluded, scope: scope);
                _timer!.Interval = 10;
            }
            if (!_capture.Step(TimeSpan.FromMilliseconds(12))) { Message = $"Reading {_capture.CapturedCount} objects"; OnDisplayExpired(true); return; }
            var scene = _capture.Accept();
            var sources = new TreeSources(_inputKind, new Dictionary<string, SourceGroupInfo>(_capture.Groups, StringComparer.Ordinal),
                new Dictionary<string, uint>(_capture.Colours, StringComparer.Ordinal));
            _captureActiveSeconds = _capture.ActiveSeconds;
            _captureInfo = $"Capture active {_captureActiveSeconds:F4} s; initial enumeration {_capture.InitialEnumerationMilliseconds:F2} ms; maximum slice {_capture.MaximumSliceMilliseconds:F2} ms (12 ms target; individual copies may overrun).";
            _capture.Dispose(); _capture = null; _inputs = null;
            ResolveSettings(scene.Value.Bounds);
            var request = _requests.Begin(scene.Value.Revision, string.Join("|", _views.Select(v => v.Revision)), _settings.ToString());
            var epoch = _epoch; var documentGeneration = _watch.Generation;
            lock (_progressSync) _progress = new(JobPhase.Queued, 0, _views.Length);
            _timer!.Interval = 100;
            _ = RunDrawing(scene, request.Identity, request.Token, epoch, documentGeneration, _views, _settings, sources);
        }
        catch (OperationCanceledException) { Invalidate(); Refresh(false); }
        catch (Exception ex) { _timer?.Stop(); _capture?.Dispose(); _capture = null; _error = ex.Message; Refresh(true); }
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

    private sealed class ProgressSink(Action<JobProgress> report) : IProgress<JobProgress>
    { public void Report(JobProgress value) => report(value); }
    private async Task RunDrawing(LeaseEntry<PreparedScene>.Lease scene, RequestIdentity identity, CancellationToken token,
        long epoch, long documentGeneration, ViewRequest[] views, NativeSettings settings, TreeSources sources)
    {
        DrawingBatch? batch = null;
        string? error = null;
        try
        {
            batch = await _drawingCache.SolveAsync(scene, identity, views, settings, _targets, token,
                new ProgressSink(p => _requests.TryUpdate(identity, () => { lock (_progressSync) _progress = p; }))).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { error = ex.Message; }
        // A host timer drains this mailbox; the task never calls Rhino/GH/UI dispatch APIs.
        lock (_completionSync)
        {
            if (epoch != Interlocked.Read(ref _epoch)) { batch?.Dispose(); return; }
            _completion?.Batch?.Dispose();
            _completion = new(identity, epoch, documentGeneration, batch, error, sources);
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
            string Info() => view.Info + "\n" + tree + effective + _captureInfo + "\n" + string.Join("\n", _settingsWarnings.Select(w => w.Text));
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
    private void DocumentChanged() { Invalidate(); Refresh(false); }
    private void Invalidate()
    {
        Interlocked.Increment(ref _epoch); _requests.Cancel(); _timer?.Stop(); _capture?.Dispose(); _capture = null;
        lock (_completionSync) { _completion?.Batch?.Dispose(); _completion = null; }
        _drawing?.Dispose(); _drawing = null; _drawingSources = null; _inputs = null; _started = false; _error = null;
        lock (_progressSync) _progress = null;
        Message = "Cancelled";
    }
    private void DetachWatch()
    { if (_watch is not null) { _watch.Changed -= DocumentChanged; _watch.Dispose(); _watch = null; } }
    public override void ClearData()
    {
        if (!_internalRefresh)
        {
            Invalidate();
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
