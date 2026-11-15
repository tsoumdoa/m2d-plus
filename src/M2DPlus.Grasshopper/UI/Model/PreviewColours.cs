using System.Drawing;

namespace M2DPlus.Grasshopper.UI.Model;

/// <summary>
/// The base colour of a drawn line in the canvas preview (owner decision 2026-11-15, UX §3.6 "Preview colours"). With
/// <i>Preview ▸ Object colours</i> (the default) a line takes its source object's display colour, read on the UI thread
/// with the scene (object colour, else layer colour, By Parent through the block instance path). Grasshopper geometry has
/// no Rhino colour and uses ink. Class styling (Beyond lighter, depth fade, Hidden dashed) is applied on top by the
/// caller; Unresolved stays orange and Ignored stays faint ink.
/// </summary>
public static class PreviewColours
{
    /// <summary>Below this WCAG contrast ratio against the viewport background an object colour would vanish (white
    /// objects on a white viewport, black ones on a dark viewport), so ink is used instead.</summary>
    public const double MinimumContrast = 1.6;

    public static readonly Color Unresolved = Color.FromArgb(224, 122, 0);

    /// <param name="objectColour">The source's display colour (ARGB), or null for Grasshopper geometry / Ink mode.</param>
    public static Color Line(uint? objectColour, Color ink, Color background)
    {
        if (objectColour is not uint argb) return ink;
        var colour = Color.FromArgb(255, Color.FromArgb(unchecked((int)argb)));
        return Contrast(colour, background) < MinimumContrast ? ink : colour;
    }

    /// <summary>WCAG 2 contrast ratio, 1 (same) to 21 (black on white).</summary>
    public static double Contrast(Color a, Color b)
    {
        double l1 = Luminance(a), l2 = Luminance(b);
        return (Math.Max(l1, l2) + .05) / (Math.Min(l1, l2) + .05);
    }

    private static double Luminance(Color c)
    {
        static double Channel(int v) { var s = v / 255d; return s <= .03928 ? s / 12.92 : Math.Pow((s + .055) / 1.055, 2.4); }
        return .2126 * Channel(c.R) + .7152 * Channel(c.G) + .0722 * Channel(c.B);
    }
}
