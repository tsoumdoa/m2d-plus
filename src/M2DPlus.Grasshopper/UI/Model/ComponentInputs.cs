using System.Collections.Immutable;

namespace M2DPlus.Grasshopper.UI.Model;

public sealed record ComponentInput(string Name, string NickName, string Type, string Access, bool Optional, bool PlacementOnly, string Description);

/// <summary>Stable input order and registration; placement inputs never form part of a visibility request.</summary>
public static class ComponentInputs
{
    public const int Source = 5;
    public const string SourceDescription = "Optional plane to orient from, like Orient's A: the drawing is moved from Source to Target (required). Leave empty to use the view's own plane (a surface view uses its centre and U/V).";
    public static ImmutableArray<ComponentInput> All { get; } = [
        new("Geometry", "G", "Generic", "tree", true, false, "Scene to draw, shared by every view. Leave empty to draw the visible Rhino model. Also accepts layer names such as Walls or Structure::*, and block instances (repeats are drawn once and reused)."),
        new("View", "V", "View", "list", false, false, "What to draw. A referenced planar surface, rectangle or M2D+ view box (right-click Convert to view boxes to make one), a named view, clipping plane name or direction (Top, Front, SW…), a referenced clipping plane, a Plane, a Rectangle (its plane is the view plane and it crops; if it cuts the model you get a section), a Box (cut, crop and depth) or a Vector (look along it). A list gives one drawing per view."),
        new("Target", "T", "Plane", "list", true, true, "Optional plane per view. Source (or the view's own plane) is moved onto it 1:1. One plane for several views lays them out in a row. Leave empty to show each drawing in place."),
        new("Settings", "S", "Text", "tree", true, false, "Optional key = value lines, for example hidden = off or farclip = line. They override view-object user text (m2d.*) and the right-click menu. One branch applies to all views; one branch per view, or [View name] headers, set views separately."),
        new("Depth", "D", "Text", "list", true, false, "Optional view depth for Plane, Rectangle and clipping-plane views: a number in document units or text such as 8 m. One value for all views, or one per view. Beats depth in Settings. Boxes and view boxes take their depth from their geometry."),
        new("Source", "Src", "Plane", "list", true, true, SourceDescription)];
}
