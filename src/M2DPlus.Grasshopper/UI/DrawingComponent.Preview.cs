using System.Drawing;
using Grasshopper.Kernel;
using M2DPlus.Grasshopper.UI.Model;
using M2DPlus.Rhino.Views;
using Rhino.Geometry;

namespace M2DPlus.Grasshopper;

/// <summary>View preview (UX rev. 4 §2.2): for each view, its frame, a look-direction arrow and the depth volume.
/// Subtle grey-blue normally, cobalt #1F6FD1 while the component is selected. Toggle: Preview ▸ View frames.</summary>
public sealed partial class DrawingComponent
{
    private IReadOnlyList<ViewPreview> _previews = Array.Empty<ViewPreview>();
    internal static readonly Color PreviewSubtle = Color.FromArgb(150, 96, 120, 156);

    /// <summary>The current view previews, for tests and the offscreen render.</summary>
    public IReadOnlyList<ViewPreview> ViewPreviews => _previews;

    /// <summary>Recomputed when views parse (boxes and cropped views, no scene needed) and again after capture.</summary>
    private void Previews(BoundingBox scene) =>
        _previews = _views.Select(v => ViewPreview.For(v, scene, _tolerance)).OfType<ViewPreview>().ToArray();

    private bool ShowViewPreviews => !Hidden && Ui.State.Preview.Contains(PreviewLayers.Views) && _previews.Count > 0;

    public override BoundingBox ClippingBox
    {
        get
        {
            var box = base.ClippingBox;
            if (ShowViewPreviews) foreach (var preview in _previews) box.Union(preview.Bounds);
            return box;
        }
    }

    public override void DrawViewportWires(IGH_PreviewArgs args)
    {
        base.DrawViewportWires(args);
        if (!ShowViewPreviews) return;
        var selected = Attributes?.Selected == true;
        var colour = selected ? ViewBoxes.Cobalt : PreviewSubtle;
        var faint = Color.FromArgb(selected ? 150 : 90, colour);
        foreach (var preview in _previews)
        {
            foreach (var edge in preview.FrontEdges) args.Display.DrawLine(edge, colour, selected ? 2 : 1);
            if (preview.Depth > 0)
            {
                foreach (var edge in preview.DepthEdges) args.Display.DrawDottedLine(edge, faint);
                foreach (var edge in preview.BackEdges) args.Display.DrawDottedLine(edge, faint);
            }
            args.Display.DrawArrow(preview.Arrow, colour);
            args.Display.Draw2dText(preview.Name, colour, preview.Frame.Corner(3), false, 11);
        }
    }
}
