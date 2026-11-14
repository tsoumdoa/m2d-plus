using System.Windows.Forms;
using GH_IO.Serialization;
using M2DPlus.Core;
using M2DPlus.Grasshopper.UI;
using M2DPlus.Grasshopper.UI.Model;

namespace M2DPlus.Grasshopper;

/// <summary>UI hook-up for the Drawing component, kept apart from the job files (see docs/design/gh-ui.md).
/// Everything UI-specific lives in <see cref="DrawingUi"/>; this file only connects it.</summary>
public sealed partial class DrawingComponent
{
    private DrawingUi? _ui;
    public DrawingUi Ui => _ui ??= CreateUi();

    private DrawingUi CreateUi()
    {
        var ui = new DrawingUi(this) { Runtime = UiRuntime };
        // Cancel rejects the running job at once; nothing is published and the button then reads Run.
        ui.CancelRequested += (_, _) => { Invalidate(); OnDisplayExpired(true); };
        ui.BakeRequested += (_, request) => BakeCurrent(global::Rhino.RhinoDoc.ActiveDoc, request.AsCopy, request.ViewIndex);
        ui.ClearCacheRequested += (_, _) => { _drawingCache.Clear(); M2DPlus.Rhino.Services.SceneCache.Shared.Clear(); Invalidate(); ExpireSolution(true); };
        return ui;
    }

    public override void CreateAttributes() => m_attributes = new DrawingAttributes(this, () => Ui);

    protected override void AppendAdditionalComponentMenuItems(ToolStripDropDown menu)
    {
        base.AppendAdditionalComponentMenuItems(menu);
        Ui.AppendMenuItems(menu);
    }

    public override bool Write(GH_IWriter writer)
    {
        writer.SetString("M3BakeNames", System.Text.Json.JsonSerializer.Serialize(_allocatedBakeNames));
        return Ui.Write(writer) && base.Write(writer);
    }
    public override bool Read(GH_IReader reader)
    {
        if (reader.ItemExists("M3BakeNames"))
        {
            try { _allocatedBakeNames = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString("M3BakeNames")) ?? new(); }
            catch (System.Text.Json.JsonException) { _allocatedBakeNames = new(); }
        }
        return Ui.Read(reader) && base.Read(reader);
    }

    /// <summary>Job state for the canvas. Reads the job fields owned by DrawingComponent.Async.cs.</summary>
    private DrawingRuntime UiRuntime()
    {
        if (_drawing is not null) return new(DrawingPhase.Done);
        if (_error is not null) return new(DrawingPhase.Error);
        if (_started)
        {
            JobProgress? progress;
            lock (_progressSync) progress = _progress;
            return new(DrawingPhase.Running, progress is { } p ? RuntimeModel.Fraction(p) : null);
        }
        if (Ui.Paused) return new(DrawingPhase.Paused);
        return Message == "Cancelled" ? new(DrawingPhase.Cancelled) : DrawingRuntime.Idle;
    }
}
