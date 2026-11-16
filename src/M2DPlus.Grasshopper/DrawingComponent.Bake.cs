using Grasshopper.Kernel;
using M2DPlus.Core;
using M2DPlus.Core.Bake;
using M2DPlus.Rhino.Services;
using Rhino;
using Rhino.DocObjects;

namespace M2DPlus.Grasshopper;

public sealed partial class DrawingComponent
{
    private Dictionary<string, string> _allocatedBakeNames = new(StringComparer.Ordinal);
    public override bool IsBakeCapable => _drawing is not null;
    public override void BakeGeometry(RhinoDoc doc, List<Guid> ids) => ids.AddRange(BakeCurrent(doc, false, null));
    public override void BakeGeometry(RhinoDoc doc, ObjectAttributes attributes, List<Guid> ids)
    {
        AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "M2D+ Bake uses its drawing layers; the Grasshopper layer choice is ignored.");
        ids.AddRange(BakeCurrent(doc, false, null));
    }

    private IReadOnlyList<Guid> BakeCurrent(RhinoDoc? doc, bool copy, int? viewIndex)
    {
        if (doc is null || _drawing is null) return Array.Empty<Guid>();
        // Document events during bake must not dispose the very drawing being enumerated. Nothing the bake changes is read
        // by the drawing, so the watch ignores it and keeps the scene: a later bake-only change can republish (UX §8.1).
        using var mute = _watch is { IsClosed: false } watch && watch.DocumentSerial == doc.RuntimeSerialNumber ? watch.Mute(doc) : null;
        try
        {
            var reserved = DrawingBake.Names(doc).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var allocated = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var drawings = new List<BakeDrawing>();
            for (var i = 0; i < _drawing.Views.Count; i++)
            {
                if (viewIndex is int only && i != only) continue;
                var view = _drawing.Views[i]; if (view.Error is not null) continue;
                var request = _views[i];
                var name = BakeNames.Infer(new(request.Kind, DrawingPresentation.Plain(request.Cut?.Plane ?? view.Frame.Plane),
                    view.Regions.Count > 0, request.Name,
                    request.Settings?["name"].Source == SettingSource.Auto ? null : request.Settings?.Name, i + 1), _context.Units);
                var key = $"{i}/{_context.Views[i].Identity}/{name}";
                var chosen = !copy && _allocatedBakeNames.TryGetValue(key, out var existing) ? existing :
                    BakeNames.Unique(new[] { name }, reserved.Concat(allocated))[0];
                if (!copy) _allocatedBakeNames[key] = chosen;
                // The layout comes from the latest resolution: a bake-only change republishes the drawing without redrawing it,
                // so the drawing's own settings may hold an older bake value (UX §8.1).
                allocated.Add(chosen); reserved.Add(chosen); drawings.Add(new(view, chosen, request.Settings?.Bake ?? view.Settings?.Bake));
            }
            var result = DrawingBake.Bake(doc, InstanceGuid, drawings);
            foreach (var message in result.Messages) { RhinoApp.WriteLine(message); AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, message); }
            OnDisplayExpired(true); return result.Added;
        }
        catch (Exception ex) { AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Bake failed: " + ex.Message); return Array.Empty<Guid>(); }
    }
}
