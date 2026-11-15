namespace M2DPlus.Core.Watch;

/// <summary>What the drawing reads from the Rhino document (UX §5.1, "What re-runs the component").</summary>
public enum WatchMode
{
    /// <summary>Geometry is empty: the whole visible document is drawn, so visibility decides what is in the scene.</summary>
    Document,
    /// <summary>Geometry is given (Grasshopper geometry, referenced objects, ids or layer names): only those inputs count.</summary>
    Referenced,
}

/// <summary>How one changed Rhino object relates to the current drawing. The host decides it from the captured scene.</summary>
public enum ObjectRole
{
    /// <summary>Not drawn and can't become drawn by this change (another object, a hidden object in document mode,
    /// an M2D+ bake, anything outside referenced Geometry).</summary>
    Unrelated,
    /// <summary>A drawn source: a Rhino object of the scene (a block instance counts as one).</summary>
    Scene,
    /// <summary>A drawn source whose own visibility decides whether it is drawn: a part inside a drawn block instance (as
    /// Rhino shows it), or an object a layer-name Geometry item selects (layer names select visible objects).</summary>
    Gated,
    /// <summary>Not in the drawing now but would be after this change: document mode, a visible drawable object; referenced
    /// mode, a visible object on a layer that a layer-name Geometry item selects.</summary>
    Candidate,
    /// <summary>A view object the View input uses: a view box, a referenced frame surface or a clipping plane.</summary>
    View,
}

/// <summary>Facets of one object that changed, compared on the host from the old and new state.</summary>
[Flags]
public enum ObjectChange
{
    None = 0,
    /// <summary>Geometry replaced: modified, moved, transformed.</summary>
    Geometry = 1 << 0,
    Added = 1 << 1,
    /// <summary>Deleted (undelete is <see cref="Added"/>).</summary>
    Deleted = 1 << 2,
    /// <summary>Hidden or shown in the model (Hide / Show, or a block part's own visibility).</summary>
    Visibility = 1 << 3,
    /// <summary>Moved to another layer.</summary>
    Layer = 1 << 4,
    /// <summary>Display or print colour and their sources.</summary>
    Colour = 1 << 5,
    /// <summary>Section style and its source.</summary>
    Section = 1 << 6,
    /// <summary>Render material assignment (glass is non-occluding with a transparent material).</summary>
    Material = 1 << 7,
    /// <summary>The object's name (Key labels, view names).</summary>
    Name = 1 << 8,
    /// <summary>Attribute User Text under <c>m2d.*</c> (per-view settings; bake tags exclude objects).</summary>
    M2DText = 1 << 9,
    /// <summary>Per-viewport or per-detail visibility. A drawing belongs to no viewport.</summary>
    ViewportVisibility = 1 << 10,
    /// <summary>Anything else: locking, selection, display-mode override, other user text, decorations…</summary>
    Other = 1 << 11,
}

/// <summary>How a changed layer relates to the current drawing.</summary>
[Flags]
public enum LayerRole
{
    None = 0,
    /// <summary>The layer, or one of its sublayers, holds drawn sources (its colour, name, style and widths are read).</summary>
    Used = 1 << 0,
    /// <summary>Its visibility decides what is drawn: in document mode a layer whose subtree holds drawable objects; in
    /// referenced mode a layer holding parts of drawn block instances, or one a layer-name Geometry item matches.</summary>
    Gate = 1 << 1,
    /// <summary>A layer-name Geometry item exists, so layer names and new layers can change what is selected.</summary>
    Pattern = 1 << 2,
}

[Flags]
public enum LayerChange
{
    None = 0,
    /// <summary>On / off in the model (the layer or a parent).</summary>
    Visibility = 1 << 0,
    /// <summary>On / off in one viewport or detail only.</summary>
    ViewportVisibility = 1 << 1,
    Colour = 1 << 2,
    Name = 1 << 3,
    Section = 1 << 4,
    /// <summary>Print width or print colour (bake, <c>weights = layers</c>).</summary>
    Print = 1 << 5,
    Material = 1 << 6,
    Added = 1 << 7,
    Deleted = 1 << 8,
    /// <summary>Locked, expanded in the panel, current layer, other properties.</summary>
    Other = 1 << 9,
}

/// <summary>
/// Which Rhino document changes re-run the drawing (owner request 2026-11-15, UX §5.1). A re-run re-reads the scene on
/// the UI thread; visibility is solved again only when its content changed (colour, name or tree changes reuse the
/// cached solve and make no HLD call). Everything here is plain values: the host computes roles and changes.
/// </summary>
public static class RerunPolicy
{
    private const ObjectChange Content = ObjectChange.Geometry | ObjectChange.Added | ObjectChange.Deleted |
        ObjectChange.Layer | ObjectChange.Colour | ObjectChange.Section | ObjectChange.Material | ObjectChange.Name | ObjectChange.M2DText;

    /// <summary>True when this object change must re-run the drawing.</summary>
    public static bool Object(WatchMode mode, ObjectRole role, ObjectChange change) => role switch
    {
        // Showing or hiding a view object, or any change to its colour or layer, changes nothing the drawing reads.
        ObjectRole.View => (change & (ObjectChange.Geometry | ObjectChange.Added | ObjectChange.Deleted | ObjectChange.Name | ObjectChange.M2DText)) != 0,
        ObjectRole.Scene => (change & Content) != 0 || mode == WatchMode.Document && (change & ObjectChange.Visibility) != 0,
        ObjectRole.Gated => (change & (Content | ObjectChange.Visibility)) != 0,
        // Something that should now be drawn is missing from the drawing.
        ObjectRole.Candidate => change != ObjectChange.None,
        _ => false,
    };

    /// <summary>True when this layer change must re-run the drawing.</summary>
    public static bool Layer(LayerRole role, LayerChange change)
    {
        if ((change & LayerChange.Visibility) != 0 && (role & LayerRole.Gate) != 0) return true;
        if ((change & (LayerChange.Colour | LayerChange.Name | LayerChange.Section | LayerChange.Print | LayerChange.Material | LayerChange.Deleted)) != 0
            && (role & LayerRole.Used) != 0) return true;
        return (change & (LayerChange.Name | LayerChange.Added | LayerChange.Deleted)) != 0 && (role & LayerRole.Pattern) != 0;
    }

    /// <summary>One row of the trigger table in UX §5.1, for documentation and tests.</summary>
    public sealed record Row(string Change, bool Document, bool Referenced, string Note);

    /// <summary>The trigger table for a drawn object / layer, in both modes. Viewport-only changes are never here: they
    /// raise no document change at all.</summary>
    public static IReadOnlyList<Row> Table { get; } =
    [
        Of("Geometry modified, moved, deleted or undeleted", ObjectChange.Geometry, "re-solves the changed content"),
        Of("Object hidden or shown (Hide / Show)", ObjectChange.Visibility, "document mode draws only what is visible"),
        Of("Object locked or unlocked", ObjectChange.Other, "locked objects are drawn"),
        Of("Object moved to another layer", ObjectChange.Layer, "layer style, colour, tree = layer"),
        Of("Object colour or print colour", ObjectChange.Colour, "no HLD call: preview, hatch fill and bake colours"),
        Of("Section style", ObjectChange.Section, "no HLD call: restyles cuts"),
        Of("Render material", ObjectChange.Material, "glass = clear uses transparency"),
        Of("Object name", ObjectChange.Name, "no HLD call: Key labels"),
        Of("m2d.* user text", ObjectChange.M2DText, "bake tags exclude objects"),
        Of("Other user text, display-mode override, selection", ObjectChange.Other, ""),
        Of("Per-viewport / per-detail visibility", ObjectChange.ViewportVisibility, "a drawing belongs to no viewport"),
        OfLayer("Layer on / off", LayerChange.Visibility, "document mode; with Geometry only layers holding parts of drawn blocks or named by a layer-name item"),
        OfLayer("Layer on / off in one viewport or detail", LayerChange.ViewportVisibility, ""),
        OfLayer("Layer colour, name, section style, print width / colour, material", LayerChange.Colour, "only layers that hold drawn objects"),
        OfLayer("Layer locked, expanded, made current", LayerChange.Other, ""),
    ];

    private static Row Of(string change, ObjectChange facet, string note) =>
        new(change, Object(WatchMode.Document, ObjectRole.Scene, facet), Object(WatchMode.Referenced, ObjectRole.Scene, facet), note);
    private static Row OfLayer(string change, LayerChange facet, string note) =>
        new(change, Layer(LayerRole.Used | LayerRole.Gate, facet), Layer(LayerRole.Used, facet), note);
}
