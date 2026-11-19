using System.Collections.Immutable;

namespace M2DPlus.Grasshopper.UI.Model;

/// <summary>A toolkit-free menu item. Command is executed by the GH adapter; Hint is the right-aligned key text.</summary>
public sealed record MenuNode(string Label, string? Command = null, bool Enabled = true, bool Checked = false,
    string? Hint = null, ImmutableArray<MenuNode> Children = default, string? Tooltip = null)
{
    public static MenuNode Separator { get; } = new("-");
    public bool IsSeparator => Label == "-" && Command is null;
    public bool IsSubmenu => !Children.IsDefault;
}

public static class MenuCommands
{
    public const string Bake = "bake", BakeOne = "bake-one:", AutoRun = "autorun", Run = "run", Cancel = "cancel",
        Set = "set:", Reset = "reset-menu", Preview = "preview:", PreviewObjectColours = "preview-colours:object", PreviewInk = "preview-colours:ink", CopyInfo = "copy-info", CopySettings = "copy-settings", ClearCache = "clear-cache";
    public static string SetValue(string key, string value) => $"{Set}{key}={value}";
    public static bool TryParseSet(string command, out string key, out string value)
    {
        key = value = "";
        if (!command.StartsWith(Set, StringComparison.Ordinal)) return false;
        var body = command[Set.Length..]; var equals = body.IndexOf('=');
        if (equals <= 0) return false;
        key = body[..equals]; value = body[(equals + 1)..];
        return true;
    }
}

/// <summary>The view-object commands shared by the component and the View input menus (UX rev. 4 §2.2).</summary>
public static class ViewCommands
{
    public const string Validate = "views-validate", Flip = "views-flip", Create = "views-create", Select = "views-select";

    /// <summary>What the view group needs to know: referenced Rhino objects, how many are view boxes, whether V is wired.</summary>
    public sealed record State(int Objects, int ViewBoxes, bool Wired);

    /// <summary>The one-line result of Convert to view boxes, e.g. "Converted 3 to view boxes; refused 1: not planar."
    /// Already-converted view boxes are counted separately; up to two refusal reasons are quoted.</summary>
    public static string ConvertSummary(int converted, int already, IReadOnlyList<string> refusals)
    {
        var parts = new List<string> { $"Converted {converted} to view box{(converted == 1 ? "" : "es")}" };
        if (already > 0) parts.Add($"{already} already {(already == 1 ? "was one" : "were")}");
        if (refusals.Count > 0)
            parts.Add($"refused {refusals.Count}: " + string.Join("; ", refusals.Take(2)) + (refusals.Count > 2 ? "…" : ""));
        return string.Join("; ", parts) + ".";
    }

    public static ImmutableArray<MenuNode> Build(State state, bool withSelect)
    {
        const string none = "Reference planar surfaces, rectangles or view boxes in View first (right-click V ▸ Pick views in Rhino…).";
        var items = ImmutableArray.CreateBuilder<MenuNode>();
        items.Add(new("Convert to view boxes", Validate, state.Objects > 0, Tooltip: state.Objects > 0
            ? "Check each referenced surface or rectangle and turn it into an editable M2D+ view box (keeps the object and its id; one undo)."
            : none));
        items.Add(new("Flip view direction", Flip, state.ViewBoxes > 0, Tooltip: state.ViewBoxes > 0
            ? "Look the other way: the front face stays, the box extends to the other side."
            : "Only view boxes can be flipped. Convert to view boxes first."));
        items.Add(new("Create view from current viewport", Create, Tooltip: state.Wired
            ? "Bakes a view box matching the active parallel viewport. V is wired, so wire the new box in yourself."
            : "Bakes a view box matching the active parallel viewport (on the $M2D Views layer) and adds it to View."));
        if (withSelect)
            items.Add(new("Select view objects", Select, state.Objects > 0, Hint: "Double-click",
                Tooltip: "Select the referenced view objects in Rhino. Their settings are in Properties ▸ Attribute User Text."));
        return items.ToImmutable();
    }
}

public sealed record MenuContext(SettingsPresentation Settings, DrawingRuntime Runtime, bool HasInfo, string? CacheSize = null,
    ViewCommands.State? Views = null);

/// <summary>The component's right-click items (UX rev. 4 §7). Setting items edit the menu defaults. An item whose key
/// the Settings input sets for every view is greyed "(set by input)"; view-object overrides are only reported.</summary>
public static class ComponentMenu
{
    public static ImmutableArray<MenuNode> Build(MenuContext context)
    {
        var settings = context.Settings;
        var state = settings.State;
        var done = context.Runtime.Phase == DrawingPhase.Done;
        var views = settings.Context.Views;
        var items = ImmutableArray.CreateBuilder<MenuNode>();
        items.AddRange(ViewCommands.Build(context.Views ?? new(0, 0, false), withSelect: true));
        items.Add(MenuNode.Separator);
        items.Add(new("Bake drawing", MenuCommands.Bake, done, Hint: "Shift-click: add as a copy",
            Tooltip: done ? null : "Bake becomes available when the drawing is complete."));
        items.Add(new("Bake one", Enabled: done && views.Count > 0,
            Children: views.Select((v, i) => new MenuNode(v.Name, MenuCommands.BakeOne + i)).ToImmutableArray()));
        // Bake time only (UX §8.1): choosing a layout never redraws.
        items.Add(Choice(settings, "Bake layers", "bake") with { Tooltip = SettingsCatalog.Spec("bake").Tooltip });
        items.Add(MenuNode.Separator);
        items.Add(new("Run automatically", MenuCommands.AutoRun, Checked: state.AutoRun));
        items.Add(new("Run now", MenuCommands.Run));
        if (context.Runtime.Phase == DrawingPhase.Running) items.Add(new("Cancel", MenuCommands.Cancel, Hint: "Esc"));
        items.Add(MenuNode.Separator);
        items.Add(Choice(settings, "Visibility engine", "visibility"));
        items.Add(Toggle(settings, "Hidden lines", "hidden"));
        items.Add(Toggle(settings, "Tangent edges", "tangents"));
        items.Add(Toggle(settings, "Seams", "seams"));
        items.Add(Choice(settings, "Min visible", "min_visible") with { Tooltip = SettingsCatalog.Spec("min_visible").Tooltip });
        items.Add(Choice(settings, "Beyond", "beyond"));
        items.Add(Choice(settings, "Far clip", "farclip"));
        items.Add(Toggle(settings, "Cut only", "cutonly"));
        items.Add(Toggle(settings, "Depth cueing", "fade"));
        items.Add(Choice(settings, "Cuts", "cuts"));
        items.Add(Choice(settings, "Hatch", "hatch"));
        items.Add(Choice(settings, "Scale", "scale") with
        { Tooltip = "Line weights only; the drawing stays 1:1. " + SettingsCatalog.WeightStrip(settings.Field("scale").Text, settings.Field("weights").Text) });
        items.Add(Choice(settings, "Line weights", "weights"));
        items.Add(new("Reset menu defaults", MenuCommands.Reset, state.HasMenuValues,
            Tooltip: "Every item above back to its built-in default. View objects and the Settings input are not changed."));
        items.Add(MenuNode.Separator);
        var ink = state.Preview.Contains(PreviewLayers.Ink);
        items.Add(new("Preview", Children: PreviewLayers.All
            .Select(l => new MenuNode(l.Label, MenuCommands.Preview + l.Name, Checked: state.Preview.Contains(l.Name)))
            .Append(MenuNode.Separator)
            .Append(new("Object colours", MenuCommands.PreviewObjectColours, Checked: !ink,
                Tooltip: "Lines in each source object's display colour (object, else layer). Grasshopper geometry in ink. Preview only."))
            .Append(new("Ink", MenuCommands.PreviewInk, Checked: ink, Tooltip: "Every line in ink (black, or white on a dark viewport). Preview only."))
            .ToImmutableArray()));
        items.Add(Choice(settings, "Output tree", "tree") with { Tooltip = SettingsCatalog.Spec("tree").Tooltip });
        items.Add(MenuNode.Separator);
        items.Add(new("Copy info", MenuCommands.CopyInfo, context.HasInfo));
        items.Add(new("Copy settings as text", MenuCommands.CopySettings,
            Tooltip: "Menu defaults and view-object values as Settings text with [view] headers, ready for a Panel."));
        items.Add(new(context.CacheSize is null ? "Clear drawing cache" : $"Clear drawing cache ({context.CacheSize})", MenuCommands.ClearCache));
        return items.ToImmutable();
    }

    private static string Suffix(FieldState field) => field.Note.Length == 0 ? "" : $" ({field.Note})";

    public static MenuNode Toggle(SettingsPresentation settings, string label, string key)
    {
        var field = settings.Field(key);
        var on = SettingsCatalog.IsOn(key, field.Text);
        return new(label + Suffix(field), MenuCommands.SetValue(key, on ? "off" : "on"), !field.Locked, on,
            Hint: $"{key} = {(on ? "on" : "off")}");
    }

    public static MenuNode Choice(SettingsPresentation settings, string label, string key)
    {
        var field = settings.Field(key);
        var current = SettingsCatalog.Canonical(key, field.Text);
        var options = SettingsCatalog.Spec(key).Options;
        var children = options.Select(o => new MenuNode(o.Label, MenuCommands.SetValue(key, o.Value), !field.Locked,
            SettingsCatalog.Canonical(key, o.Value) == current)).ToList();
        // A typed value that is not one of the presets (e.g. beyond = 2 m from text) still shows as checked.
        if (!options.Any(o => SettingsCatalog.Canonical(key, o.Value) == current))
            children.Insert(0, new MenuNode(SettingsCatalog.Display(key, field.Text), null, false, true));
        return new(label + Suffix(field), Enabled: !field.Locked, Hint: $"{key} = {current}", Children: children.ToImmutableArray());
    }
}

/// <summary>The View input's right-click items (UX rev. 4 §2.1). Text choices set the param's persistent text values;
/// picked surfaces, rectangles and view boxes are stored as object references.</summary>
public static class ViewMenu
{
    public const string Toggle = "view:", AllNamed = "all-named", AllClipping = "all-clip", Pick = "pick", Clear = "clear";
    public static ImmutableArray<string> Directions { get; } = ["Top", "Front", "Right", "Back", "Left", "Bottom", "SW", "SE", "NE", "NW"];

    public static ImmutableArray<MenuNode> Build(IReadOnlyList<string> namedViews, IReadOnlyList<string> clippingPlanes,
        IReadOnlyList<string> current, bool wired, ViewCommands.State? views = null, int storedObjects = 0)
    {
        var free = !wired;
        MenuNode Item(string value) => new(value, Toggle + value, free, Contains(current, value));
        ImmutableArray<MenuNode> List(IReadOnlyList<string> values, string empty) => values.Count == 0
            ? [new MenuNode(empty, null, false)] : values.Select(Item).ToImmutableArray();
        var directions = Directions.Take(6).Select(Item).Append(MenuNode.Separator).Concat(Directions.Skip(6).Select(Item)).ToImmutableArray();
        const string why = "Disconnect the wire to choose views here.";
        return
        [
            new("Pick views in Rhino…", Pick, free, Tooltip: wired ? why
                : "Select planar surfaces, closed rectangles, view boxes or clipping planes in Rhino. Then Convert to view boxes."),
            .. ViewCommands.Build(views ?? new(0, 0, wired), withSelect: false),
            MenuNode.Separator,
            new("Named views", Enabled: free, Children: List(namedViews, "No named views in this document"), Tooltip: wired ? why : null),
            new("Clipping planes", Enabled: free, Children: List(clippingPlanes, "No named clipping planes"), Tooltip: wired ? why : null),
            new("Directions", Enabled: free, Children: directions, Tooltip: wired ? why : null),
            new("All named views", AllNamed, free && namedViews.Count > 0),
            new("All clipping planes", AllClipping, free && clippingPlanes.Count > 0),
            MenuNode.Separator,
            new("Clear", Clear, free && current.Count + storedObjects > 0),
        ];
    }

    public static bool Contains(IReadOnlyList<string> current, string value) => current.Any(c => string.Equals(c, value, StringComparison.OrdinalIgnoreCase));

    /// <summary>Multi-check: adds the value at the end, or removes every case-insensitive match. Order is kept.</summary>
    public static ImmutableArray<string> ToggleValue(IReadOnlyList<string> current, string value) => Contains(current, value)
        ? current.Where(c => !string.Equals(c, value, StringComparison.OrdinalIgnoreCase)).ToImmutableArray()
        : [.. current, value];

    public static ImmutableArray<string> AddAll(IReadOnlyList<string> current, IEnumerable<string> values) =>
        values.Aggregate(current.ToImmutableArray(), (list, v) => Contains(list, v) ? list : list.Add(v));
}
