using M2DPlus.Core.Placement;

namespace M2DPlus.Core.Views;

/// <summary>Conservative separation in a parallel view's coordinates, with Z toward the viewer.</summary>
public static class ViewSourceBounds
{
    public static double Margin(double tolerance, double magnitude) =>
        tolerance + 128 * 2.220446049250313e-16 * Math.Max(1, magnitude);

    /// <summary>Only strict separation excludes a source. Near-side geometry is deliberately retained.</summary>
    public static bool Outside(Bounds3 box, Frame2? crop, double? depth, double margin)
    {
        if (!double.IsFinite(margin) || margin < 0 || box.Min.X > box.Max.X || box.Min.Y > box.Max.Y || box.Min.Z > box.Max.Z)
            return false;
        if (crop is { } c && (box.Max.X < c.MinX - margin || box.Min.X > c.MaxX + margin ||
            box.Max.Y < c.MinY - margin || box.Min.Y > c.MaxY + margin)) return true;
        return depth is >= 0 && box.Max.Z < -depth.Value - margin;
    }
}
