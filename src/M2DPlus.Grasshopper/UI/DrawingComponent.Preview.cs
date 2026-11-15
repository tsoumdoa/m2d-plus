using System.Drawing;
using M2DPlus.Core;
using M2DPlus.Rhino.Services;
using M2DPlus.Rhino.Sections;
using M2DPlus.Core.Sections;
using Rhino.Display;
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
            var box = BoundingBox.Empty;
            if (_drawing is not null) foreach (var view in _drawing.Views) box.Union(view.Frame.BoundingBox);
            if (ShowViewPreviews) foreach (var preview in _previews) box.Union(preview.Bounds);
            return box;
        }
    }

    public override void DrawViewportWires(IGH_PreviewArgs args)
    {
        if (Hidden) return;
        DrawDrawingWires(args);
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
    private void DrawDrawingWires(IGH_PreviewArgs args)
    {
        if (_drawing is null) return;
        var background = global::Rhino.ApplicationSettings.AppearanceSettings.ViewportBackgroundColor;
        var ink = background.GetBrightness() < .5 ? Color.FromArgb(245, 245, 245) : Color.Black;
        foreach (var view in _drawing.Views)
        {
            if (view.Error is not null) continue;
            foreach (var piece in view.Items.OrderBy(p => p.Class is LineClass.Hidden or LineClass.Ignored ? 0 : 1))
            {
                var toggle = piece.Class switch { LineClass.Hidden => PreviewLayers.Hidden, LineClass.Beyond => PreviewLayers.Beyond,
                    LineClass.FarClip => PreviewLayers.FarClip, LineClass.Ignored => PreviewLayers.Ignored, _ => null };
                if (toggle is not null && !Ui.State.Preview.Contains(toggle)) continue;
                var kind = piece.Profile && piece.Class == LineClass.Visible ? LineClass.Profile : piece.Class;
                var style = view.Weights[kind];
                var colour = kind == LineClass.Unresolved ? Color.FromArgb(224, 122, 0) :
                    DrawingBake.Blend(view.Weights.UseSourceLayers ? Color.FromArgb(unchecked((int)piece.SourceColour)) : ink,
                        background, piece.Intensity * style.InkIntensity);
                var pixels = view.Weights.UseSourceLayers ? Math.Max(1, (int)Math.Round(piece.SourceWidth * 96 / 25.4)) : style.PreviewPixels;
                using var geometry = piece.CopyGeometry();
                if (geometry is Curve curve) DrawCurve(curve, colour, pixels, style.Dashed);
                else if (geometry is global::Rhino.Geometry.Point point) args.Display.DrawPoint(point.Location, global::Rhino.Display.PointStyle.Simple, 3, colour);
            }
            foreach (var entry in view.Regions)
            {
                var boundary = entry.Region.Style.Boundary;
                if (!boundary.Visible) continue;
                foreach (var loop in entry.Region.Loops)
                    DrawCurve(loop.Curve, boundary.Colour == 0xff000000 ? ink : Color.FromArgb(unchecked((int)boundary.Colour)), view.Weights[LineClass.Cut].PreviewPixels,
                        boundary.LinetypeSegments.Count != 0);
            }
            if (Ui.State.Preview.Contains(PreviewLayers.Frame)) { using var frameCurve = view.Frame.ToNurbsCurve(); args.Display.DrawCurve(frameCurve, ViewBoxes.Cobalt, 1); }
        }
        void DrawCurve(Curve curve, Color colour, int pixels, bool dashed)
        {
            if (!dashed) { args.Display.DrawCurve(curve, colour, pixels); return; }
            using var poly = curve.ToPolyline(.001, .02, 0, 0);
            if (poly?.TryGetPolyline(out var points) == true) args.Display.DrawPatternedPolyline(points, colour, unchecked((int)0x00000fff), pixels, false);
        }
    }

    public override void DrawViewportMeshes(IGH_PreviewArgs args)
    {
        if (Hidden || _drawing is null || !Ui.State.Preview.Contains(PreviewLayers.Hatch)) return;
        var backgroundColour = global::Rhino.ApplicationSettings.AppearanceSettings.ViewportBackgroundColor;
        var ink = backgroundColour.GetBrightness() < .5 ? Color.FromArgb(245, 245, 245) : Color.Black;
        foreach (var view in _drawing.Views)
        foreach (var entry in view.Regions)
        {
            var region = entry.Region; if (region.Hatch is not { } hatch) continue;
            var style = region.Style;
            if (style.Background != BackgroundMode.None)
            {
                var backgrounds = Brep.CreatePlanarBreps(region.Loops.Select(l => l.Curve), .001) ?? Array.Empty<Brep>();
                using var material = new DisplayMaterial(Color.FromArgb(unchecked((int)style.BackgroundColour)));
                foreach (var background in backgrounds) using (background) args.Display.DrawBrepShaded(background, material);
            }
            using var pattern = SectionHatches.CreatePattern(style.Pattern ?? new PatternDefinition("Solid", true, Array.Empty<PatternLine>()));
            hatch.CreateDisplayGeometry(pattern, 1, out var bounds, out var lines, out var solid);
            try
            {
                var colour = view.Settings?.Hatch == HatchMode.Black ? ink : Color.FromArgb(unchecked((int)(style.Pattern is null ? style.BackgroundColour : style.PatternColour)));
                foreach (var line in lines) args.Display.DrawLine(line, colour, 1);
                if (solid is not null) { using var material = new DisplayMaterial(colour); args.Display.DrawBrepShaded(solid, material); }
            }
            finally { foreach (var curve in bounds) curve.Dispose(); solid?.Dispose(); }
        }
    }

}
