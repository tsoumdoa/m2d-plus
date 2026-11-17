namespace M2DPlus.Core.Bake;

/// <summary>Creation-time layer shades (owner decision 2026-11-17, UX §8.1): each line-type layer takes its source layer's
/// colour shaded per type, so types differ while staying recognisably the source colour. Display and print colours use the
/// same rule; Unresolved is always orange.</summary>
public static class BakeColours
{
    /// <summary>User string on a layer M2D+ created with a class shade. Objects on such a layer take the class's ink rule from
    /// the layer, so only depth-cue fading is applied per object; on any other layer (earlier betas, classic type parents)
    /// the object still gets the class's ink intensity, as before.</summary>
    public const string ShadeTag = "m2d.shade";
    public const string ShadeVersion = "v1";

    public static uint Shade(uint source, LineClass kind)
    {
        // (desaturation, share of the colour kept, what the rest blends toward)
        var (desaturate, keep, toward) = kind switch
        {
            LineClass.Cut => (0d, .6, 0d),
            LineClass.Visible or LineClass.Profile or LineClass.FarClip => (0d, 1d, 0d),
            LineClass.Hidden => (.5, .6, 255d),
            LineClass.Beyond => (0d, .55, 255d),
            LineClass.Hatch => (0d, .4, 255d),
            LineClass.Ignored => (0d, .25, 255d),
            LineClass.Unresolved => (0d, 1d, 0d),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        if (kind == LineClass.Unresolved) return 0xffe07a00;
        double r = (source >> 16) & 255, g = (source >> 8) & 255, b = source & 255;
        var grey = .299 * r + .587 * g + .114 * b;
        uint Channel(double c) => (uint)Math.Clamp(Math.Round((c + (grey - c) * desaturate) * keep + toward * (1 - keep)), 0, 255);
        return (source & 0xff000000) | (Channel(r) << 16) | (Channel(g) << 8) | Channel(b);
    }
}
