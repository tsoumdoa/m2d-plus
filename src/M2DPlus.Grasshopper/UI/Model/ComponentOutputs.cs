using System.Collections.Immutable;

namespace M2DPlus.Grasshopper.UI.Model;

public sealed record OutputSpec(int Index, string Name, string NickName, string Description);

/// <summary>The component's outputs in their fixed order (UX §1.2). Saved definitions match outputs by index, so an
/// output is only ever appended: Ignored was added as the 10th (index 9) and the first nine never move.</summary>
public static class ComponentOutputs
{
    public const int Visible = 0, Hidden = 1, Beyond = 2, FarClip = 3, Cut = 4, Hatch = 5, Unresolved = 6, Frame = 7, Info = 8, Ignored = 9;

    public static ImmutableArray<OutputSpec> All { get; } =
    [
        new(Visible, "Visible", "V", "Visible linework: profiles, edges, projected curves and points, nearer than the beyond distance."),
        new(Hidden, "Hidden", "H", "Lines hidden by other geometry. Empty when Hidden lines is off."),
        new(Beyond, "Beyond", "B", "Visible lines farther than the beyond distance. They are moved out of Visible, not copied."),
        new(FarClip, "Far clip", "Fc", "Where the far clip plane cuts geometry, drawn only with farclip = line. Otherwise empty."),
        new(Cut, "Cut", "C", "Section boundaries. One branch per cut region holding its outer loop and any holes (one region per object by default). Open outlines from open geometry get their own branches and no hatch."),
        new(Hatch, "Hatch", "Ha", "One hatch per Cut region branch, at the same path, so each Cut branch has exactly one matching Hatch item. The pattern comes from the object's Rhino 8 section style when it has one; otherwise it is a solid hatch in the object's display colour. Where there is no fill (a style without fill, an open outline, hatch = none), the item is null, so the branches stay aligned."),
        new(Unresolved, "Unresolved", "?", "Pieces whose visibility the engine could not decide. They never appear in Visible or Hidden. Usually empty."),
        new(Frame, "Frame", "F", "One placed drawing frame per view, at 1:1. It does not depend on the output's bounds. For a Rectangle view it is that rectangle, placed."),
        new(Info, "Info", "i", "One report per view: source, effective settings and where each came from, counts, styles, unresolved and unsupported objects, timing."),
        new(Ignored, "Ignored", "Ig", "Lines of objects seen only through small gaps: partly hidden objects whose longest continuous visible piece is shorter than min visible (Settings, or m2d.min_visible on a view object). They are moved out of Visible, Beyond and Far clip, never deleted. Empty while min visible is off (the default)."),
    ];
}
