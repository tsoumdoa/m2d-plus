using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using M2DPlus.Core;
using M2DPlus.Rhino.Services;
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
    private readonly List<string> _settingsWarnings = new();
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
        _views = inputViews.Select(v => ViewRequest.Parse(Value(v), document)).ToArray();
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
        try
        {
            var assignments = Settings.Parse(settings?.AllData(true).Select(v => ((GH_String)v).Value) ?? Array.Empty<string>());
            foreach (var assignment in assignments)
            {
                if (assignment.View != "*") { _settingsWarnings.Add("Per-view settings are not in this build yet."); continue; }
                if (assignment.Key == "multiplethreads" && bool.TryParse(assignment.Value, out var enabled)) _settings = new(enabled);
                else if (assignment.Key == "multiplethreads" && assignment.Value is "on" or "off") _settings = new(assignment.Value == "on");
                else _settingsWarnings.Add($"Setting '{assignment.Key} = {assignment.Value}' is not supported in M1b; default retained.");
            }
        }
        catch (FormatException ex) { _settingsWarnings.Add(ex.Message); }
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
                _capture = new(document, _watch, _inputs);
                _timer!.Interval = 10;
            }
            if (!_capture.Step(TimeSpan.FromMilliseconds(12))) { Message = $"Reading {_capture.CapturedCount} objects"; OnDisplayExpired(true); return; }
            var scene = _capture.Accept();
            _captureInfo = $"Capture: initial enumeration {_capture.InitialEnumerationMilliseconds:F2} ms; maximum slice {_capture.MaximumSliceMilliseconds:F2} ms (12 ms target; individual copies may overrun).";
            _capture.Dispose(); _capture = null; _inputs = null;
            var request = _requests.Begin(scene.Value.Revision, string.Join("|", _views.Select(v => v.Revision)), _settings.ToString());
            var epoch = _epoch; var documentGeneration = _watch.Generation;
            lock (_progressSync) _progress = new(JobPhase.Queued, 0, _views.Length);
            _timer!.Interval = 100;
            _ = RunDrawing(scene, request.Identity, request.Token, epoch, documentGeneration, _views, _settings);
        }
        catch (OperationCanceledException) { Invalidate(); Refresh(false); }
        catch (Exception ex) { _timer?.Stop(); _capture?.Dispose(); _capture = null; _error = ex.Message; Refresh(true); }
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
        var unresolved = new GH_Structure<GH_Curve>(); var frames = new GH_Structure<GH_Rectangle>(); var info = new GH_Structure<GH_String>();
        for (var index = 0; index < batch.Views.Count; index++)
        {
            var view = batch.Views[index]; var viewPath = new GH_Path(index);
            info.Append(new(view.Info + "\n" + _captureInfo + "\n" + string.Join("\n", _settingsWarnings)), viewPath);
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
                    else unresolved.Append(new(curve), path);
                }
                else if (copy is Point point)
                {
                    if (item.Class == LineClass.Visible) visible.Append(new GH_Point(point.Location), path);
                    point.Dispose();
                }
                else copy.Dispose();
            }
            if (view.Report.Quarantined || view.Report.UncertainCount != 0)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, $"{view.Name}: uncertain/quarantined view; inspect Unresolved and Info. Uncertain points are suppressed.");
            if (view.Report.UnsupportedByType.Count > 0)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, $"{view.Name}: unsupported sources: {string.Join(", ", view.Report.UnsupportedByType.Select(p => $"{p.Key}={p.Value}"))}. See Info.");
        }
        foreach (var warning in _settingsWarnings) AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, warning);
        var good = batch.Views.Count(v => v.Error is null);
        Message = good == batch.Views.Count ? $"{good} drawings · {batch.Views.Sum(v => v.Seconds):F1} s" : $"Partial · {good}/{batch.Views.Count} drawings";
        da.SetDataTree(0, visible); da.SetDataTree(1, hidden); da.SetDataTree(6, unresolved); da.SetDataTree(7, frames); da.SetDataTree(8, info);
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
