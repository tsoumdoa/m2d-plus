using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using M2DPlus.Core;
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
    private JobProgress? _progress;
    private readonly object _progressSync = new();
    private readonly object _completionSync = new();
    private sealed record Completion(RequestIdentity Identity, long Epoch, long DocumentGeneration, DrawingBatch? Batch, string? Error);
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
        foreach (var (level, text) in Ui.Notes) AddRuntimeMessage(level, text);
        if (_drawing is not null) { Emit(da, _drawing); return; }
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
                        $"input/{geometry.Paths[branch]}/{index}/{(value is Guid objectId ? objectId.ToString() : "local")}"));
                }
        }
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
            if (view.ObjectId != Guid.Empty && document.Objects.FindId(view.ObjectId) is { } obj) rows[identity] = ViewBoxes.ReadRow(obj.Attributes);
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
                if (completed.Batch is { } batch && _requests.TryPublish(completed.Identity, batch.Complete, () => _drawing = batch))
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
                _capture = new(document, _watch, _inputs, excluded: _excluded);
                _timer!.Interval = 10;
            }
            if (!_capture.Step(TimeSpan.FromMilliseconds(12))) { Message = $"Reading {_capture.CapturedCount} objects"; OnDisplayExpired(true); return; }
            var scene = _capture.Accept();
            _captureInfo = $"Capture: initial enumeration {_capture.InitialEnumerationMilliseconds:F2} ms; maximum slice {_capture.MaximumSliceMilliseconds:F2} ms (12 ms target; individual copies may overrun).";
            _capture.Dispose(); _capture = null; _inputs = null;
            ResolveSettings(scene.Value.Bounds);
            var request = _requests.Begin(scene.Value.Revision, string.Join("|", _views.Select(v => v.Revision)), _settings.ToString());
            var epoch = _epoch; var documentGeneration = _watch.Generation;
            lock (_progressSync) _progress = new(JobPhase.Queued, 0, _views.Length);
            _timer!.Interval = 100;
            _ = RunDrawing(scene, request.Identity, request.Token, epoch, documentGeneration, _views, _settings);
        }
        catch (OperationCanceledException) { Invalidate(); Refresh(false); }
        catch (Exception ex) { _timer?.Stop(); _capture?.Dispose(); _capture = null; _error = ex.Message; Refresh(true); }
    }
    /// <summary>UX rev. 4 §6 precedence, once the scene depth is known: Depth input &gt; Settings &gt; view object
    /// (m2d.* user text) &gt; component menu &gt; auto. Box views keep their geometric depth. In this build only depth
    /// reaches the engine; the rest is resolved, reported in Info and flagged as not applied yet.</summary>
    private void ResolveSettings(BoundingBox bounds)
    {
        var contexts = _context.Views.Select((c, i) => c with { SceneDepth = SceneDepth(_views[i], bounds) }).ToArray();
        SettingsResolution resolution;
        try { resolution = Settings.Resolve(contexts, _context.InputBranches, _context.Units, Ui.State.ToEditorSettings(_context.ViewRows), _context.Depth); }
        catch (ArgumentException ex) { _settingsWarnings.Add((GH_RuntimeMessageLevel.Error, ex.Message)); return; }
        foreach (var diagnostic in resolution.Diagnostics)
        {
            var where = diagnostic.ViewIndex is int v && v < _views.Length ? $"{_views[v].Name}: " : "";
            var level = diagnostic.Severity switch { DiagnosticSeverity.Error => GH_RuntimeMessageLevel.Error,
                DiagnosticSeverity.Warning => GH_RuntimeMessageLevel.Warning, _ => GH_RuntimeMessageLevel.Remark };
            _settingsWarnings.Add((level, where + diagnostic.Message));
        }
        var pending = new SortedSet<string>(StringComparer.Ordinal);
        _effective = new string[_views.Length];
        for (var i = 0; i < _views.Length && i < resolution.Views.Length; i++)
        {
            var effective = resolution.Views[i]; var view = _views[i];
            var entry = effective["depth"];
            if (view.Cut is { } cut && view.Kind != ViewSourceKind.Box && entry.Source != SettingSource.Auto)
                _views[i] = view with { Cut = cut with { SourceDepth = effective.Depth } };
            else if (view.Cut is null && view.Error is null && entry.Source != SettingSource.Auto)
                _settingsWarnings.Add((GH_RuntimeMessageLevel.Remark, $"{view.Name}: depth applies to plane, rectangle, box and clipping-plane views; this view has no far limit in this build."));
            var set = Settings.Keys.Where(k => effective[k].AssignedBy != SettingSource.Default && !(k == "depth" && entry.Source == SettingSource.Auto && entry.AssignedBy == SettingSource.Auto)).ToArray();
            foreach (var key in set.Where(k => k != "depth")) pending.Add(key);
            _effective[i] = $"Settings: depth = {(entry.Value is double d ? d.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) : "auto")} ({entry.SourceDescription})" +
                string.Concat(set.Where(k => k != "depth").Select(k => $" · {k} = {effective[k].Text} ({effective[k].SourceDescription})"));
        }
        if (pending.Count > 0)
            _settingsWarnings.Add((GH_RuntimeMessageLevel.Remark, $"Resolved but not applied to the drawing in this build yet: {string.Join(", ", pending)}. Only depth is applied."));
        Previews(bounds);
    }

    private double SceneDepth(ViewRequest view, BoundingBox bounds)
    {
        if (!bounds.IsValid) return 0;
        if (view.Cut is { } cut) return Math.Max(0, -bounds.GetCorners().Min(cut.Plane.DistanceTo)) + _tolerance * 10;
        return ViewPreview.For(view, bounds, _tolerance)?.Depth ?? 0;
    }

    private sealed class ProgressSink(Action<JobProgress> report) : IProgress<JobProgress>
    { public void Report(JobProgress value) => report(value); }
    private async Task RunDrawing(LeaseEntry<PreparedScene>.Lease scene, RequestIdentity identity, CancellationToken token,
        long epoch, long documentGeneration, ViewRequest[] views, NativeSettings settings)
    {
        DrawingBatch? batch = null;
        string? error = null;
        try
        {
            batch = await NativeDrawing.SolveAsync(scene, identity, views, settings, token,
                new ProgressSink(p => _requests.TryUpdate(identity, () => { lock (_progressSync) _progress = p; }))).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { error = ex.Message; }
        // A host timer drains this mailbox; the task never calls Rhino/GH/UI dispatch APIs.
        lock (_completionSync)
        {
            if (epoch != Interlocked.Read(ref _epoch)) { batch?.Dispose(); return; }
            _completion?.Batch?.Dispose();
            _completion = new(identity, epoch, documentGeneration, batch, error);
        }
    }
    private void Emit(IGH_DataAccess da, DrawingBatch batch)
    {
        var visible = new GH_Structure<IGH_GeometricGoo>(); var hidden = new GH_Structure<GH_Curve>();
        var farClip = new GH_Structure<GH_Curve>(); var cut = new GH_Structure<GH_Curve>(); var hatch = new GH_Structure<GH_Hatch>();
        var unresolved = new GH_Structure<GH_Curve>(); var frames = new GH_Structure<GH_Rectangle>(); var info = new GH_Structure<GH_String>();
        for (var index = 0; index < batch.Views.Count; index++)
        {
            var view = batch.Views[index]; var viewPath = new GH_Path(index);
            var effective = index < _effective.Length ? _effective[index] + "\n" : "";
            info.Append(new(view.Info + "\n" + effective + _captureInfo + "\n" + string.Join("\n", _settingsWarnings.Select(w => w.Text))), viewPath);
            if (view.Error is not null) { AddRuntimeMessage(GH_RuntimeMessageLevel.Error, view.Error); continue; }
            frames.Append(new(view.Frame), viewPath);
            foreach (var item in view.Items)
            {
                var path = new GH_Path(new[] { index }.Concat(item.SourcePath).ToArray());
                var copy = item.CopyGeometry();
                if (copy is Curve curve)
                {
                    if (item.Class == LineClass.Visible) visible.Append(new GH_Curve(curve), path);
                    else if (item.Class == LineClass.Hidden) hidden.Append(new(curve), path);
                    else if (item.Class == LineClass.FarClip) farClip.Append(new(curve), path);
                    else unresolved.Append(new(curve), path);
                }
                else if (copy is Point point)
                {
                    if (item.Class == LineClass.Visible) visible.Append(new GH_Point(point.Location), path);
                    point.Dispose();
                }
                else copy.Dispose();
            }
            var regionOrdinals = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var entry in view.Regions)
            {
                var key = string.Join(",", entry.SourcePath);
                var ordinal = regionOrdinals.GetValueOrDefault(key); regionOrdinals[key] = ordinal + 1;
                var path = new GH_Path(new[] { index }.Concat(entry.SourcePath).Append(ordinal).ToArray());
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
            if (view.Report.Quarantined || view.Report.UncertainCount != 0)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, $"{view.Name}: uncertain/quarantined view; inspect Unresolved and Info. Uncertain points are suppressed.");
            if (view.Report.UnsupportedByType.Count > 0)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, $"{view.Name}: unsupported sources: {string.Join(", ", view.Report.UnsupportedByType.Select(p => $"{p.Key}={p.Value}"))}. See Info.");
        }
        foreach (var (level, text) in _settingsWarnings) AddRuntimeMessage(level, text);
        var good = batch.Views.Count(v => v.Error is null);
        Message = good == batch.Views.Count ? $"{good} drawings · {batch.Views.Sum(v => v.Seconds):F1} s" : $"Partial · {good}/{batch.Views.Count} drawings";
        da.SetDataTree(0, visible); da.SetDataTree(1, hidden); da.SetDataTree(3, farClip); da.SetDataTree(4, cut); da.SetDataTree(5, hatch); da.SetDataTree(6, unresolved); da.SetDataTree(7, frames); da.SetDataTree(8, info);
    }
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
        _drawing?.Dispose(); _drawing = null; _inputs = null; _started = false; _error = null;
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
    public override void AddedToDocument(GH_Document document) { _removed = false; base.AddedToDocument(document); }
    public override void RemovedFromDocument(GH_Document document)
    {
        _removed = true; Invalidate(); DetachWatch(); _timer?.Dispose(); _timer = null;
        base.RemovedFromDocument(document);
    }
    public override void DocumentContextChanged(GH_Document document, GH_DocumentContext context)
    {
        if (context == GH_DocumentContext.Close) { Invalidate(); DetachWatch(); }
        base.DocumentContextChanged(document, context);
    }
}
