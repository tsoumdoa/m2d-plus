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
    public const string Editor = "editor", Bake = "bake", BakeOne = "bake-one:", AutoRun = "autorun", Run = "run", Cancel = "cancel",
        Set = "set:", Preview = "preview:", CopyInfo = "copy-info", CopySettings = "copy-settings", ClearCache = "clear-cache";
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

public sealed record MenuContext(SettingsPresentation Settings, DrawingRuntime Runtime, bool HasInfo, string? CacheSize = null);

/// <summary>The component's right-click items (UX §7). Every setting toggle edits the editor's All views row;
/// an item whose key the Settings input sets for every view is greyed and says "(set by input)".</summary>
public static class ComponentMenu
{
    private const string All = ComponentUiState.AllViewsRow;

    public static ImmutableArray<MenuNode> Build(MenuContext context)
    {
        var settings = context.Settings;
        var state = settings.State;
        var done = context.Runtime.Phase == DrawingPhase.Done;
        var views = settings.Context.Views;
        var items = ImmutableArray.CreateBuilder<MenuNode>();
        items.Add(new("View range & settings…", MenuCommands.Editor, Hint: "Double-click"));
        items.Add(MenuNode.Separator);
        items.Add(new("Bake drawing", MenuCommands.Bake, done, Hint: "Shift-click: add as a copy",
            Tooltip: done ? null : "Bake becomes available when the drawing is complete."));
        items.Add(new("Bake one", Enabled: done && views.Count > 0,
            Children: views.Select((v, i) => new MenuNode(v.Name, MenuCommands.BakeOne + i)).ToImmutableArray()));
        items.Add(MenuNode.Separator);
        items.Add(new("Run automatically", MenuCommands.AutoRun, Checked: state.AutoRun));
        items.Add(new("Run now", MenuCommands.Run));
        if (context.Runtime.Phase == DrawingPhase.Running) items.Add(new("Cancel", MenuCommands.Cancel, Hint: "Esc"));
        items.Add(MenuNode.Separator);
        items.Add(Toggle(settings, "Hidden lines", "hidden"));
        items.Add(Toggle(settings, "Tangent edges", "tangents"));
        items.Add(Toggle(settings, "Seams", "seams"));
        items.Add(Choice(settings, "Beyond", "beyond"));
        items.Add(Choice(settings, "Far clip", "farclip"));
        items.Add(Toggle(settings, "Depth cueing", "fade"));
        items.Add(Choice(settings, "Cuts", "cuts"));
        items.Add(Choice(settings, "Hatch", "hatch"));
        items.Add(Choice(settings, "Scale", "scale", new MenuNode("Other… (editor)", MenuCommands.Editor)));
        items.Add(Choice(settings, "Line weights", "weights"));
        items.Add(MenuNode.Separator);
        items.Add(new("Preview", Children: PreviewLayers.All
            .Select(l => new MenuNode(l.Label, MenuCommands.Preview + l.Name, Checked: state.Preview.Contains(l.Name))).ToImmutableArray()));
        items.Add(Choice(settings, "Output tree", "tree"));
        items.Add(MenuNode.Separator);
        items.Add(new("Copy info", MenuCommands.CopyInfo, context.HasInfo));
        items.Add(new("Copy settings as text", MenuCommands.CopySettings));
        items.Add(new(context.CacheSize is null ? "Clear drawing cache" : $"Clear drawing cache ({context.CacheSize})", MenuCommands.ClearCache));
        return items.ToImmutable();
    }

    private static string Suffix(FieldState field) => field.Locked ? " (set by input)"
        : field.InputViews > 0 ? $" (set by input for {field.InputViews} of {field.ViewCount} views)" : "";

    public static MenuNode Toggle(SettingsPresentation settings, string label, string key)
    {
        var field = settings.Field(All, key);
        var on = SettingsCatalog.IsOn(key, field.Text);
        return new(label + Suffix(field), MenuCommands.SetValue(key, on ? "off" : "on"), !field.Locked, on,
            Hint: $"{key} = {(on ? "on" : "off")}");
    }

    public static MenuNode Choice(SettingsPresentation settings, string label, string key, MenuNode? extra = null)
    {
        var field = settings.Field(All, key);
        var current = SettingsCatalog.Canonical(key, field.Text);
        var options = SettingsCatalog.Spec(key).Options;
        var children = options.Select(o => new MenuNode(o.Label, MenuCommands.SetValue(key, o.Value), !field.Locked,
            SettingsCatalog.Canonical(key, o.Value) == current)).ToList();
        // A typed value that is not one of the presets (e.g. beyond = 2 m) still shows as checked.
        if (!options.Any(o => SettingsCatalog.Canonical(key, o.Value) == current))
            children.Insert(0, new MenuNode(SettingsCatalog.Display(key, field.Text), null, false, true));
        if (extra is not null) children.Add(extra with { Enabled = !field.Locked });
        return new(label + Suffix(field), Enabled: !field.Locked, Hint: $"{key} = {current}", Children: children.ToImmutableArray());
    }
}

/// <summary>The View input's right-click items (UX §2.1). Choosing items sets the param's persistent text values.</summary>
public static class ViewMenu
{
    public const string Toggle = "view:", AllNamed = "all-named", AllClipping = "all-clip", PickClipping = "pick-clip", Capture = "capture", Clear = "clear";
    public static ImmutableArray<string> Directions { get; } = ["Top", "Front", "Right", "Back", "Left", "Bottom", "SW", "SE", "NE", "NW"];

    public static ImmutableArray<MenuNode> Build(IReadOnlyList<string> namedViews, IReadOnlyList<string> clippingPlanes,
        IReadOnlyList<string> current, bool wired)
    {
        var free = !wired;
        MenuNode Item(string value) => new(value, Toggle + value, free, Contains(current, value));
        ImmutableArray<MenuNode> List(IReadOnlyList<string> values, string empty) => values.Count == 0
            ? [new MenuNode(empty, null, false)] : values.Select(Item).ToImmutableArray();
        var directions = Directions.Take(6).Select(Item).Append(MenuNode.Separator).Concat(Directions.Skip(6).Select(Item)).ToImmutableArray();
        const string why = "Disconnect the wire to choose views here.";
        return
        [
            new("Named views", Enabled: free, Children: List(namedViews, "No named views in this document"), Tooltip: wired ? why : null),
            new("Clipping planes", Enabled: free, Children: List(clippingPlanes, "No named clipping planes"), Tooltip: wired ? why : null),
            new("Directions", Enabled: free, Children: directions, Tooltip: wired ? why : null),
            MenuNode.Separator,
            new("All named views", AllNamed, free && namedViews.Count > 0),
            new("All clipping planes", AllClipping, free && clippingPlanes.Count > 0),
            new("Pick clipping planes in Rhino…", PickClipping, free),
            new("Capture active view as named view…", Capture, free),
            MenuNode.Separator,
            new("Clear", Clear, free && current.Count > 0),
        ];
    }

    public static bool Contains(IReadOnlyList<string> current, string value) => current.Any(c => string.Equals(c, value, StringComparison.OrdinalIgnoreCase));

    /// <summary>Multi-check: adds the value at the end, or removes every case-insensitive match. Order is kept.</summary>
    public static ImmutableArray<string> ToggleValue(IReadOnlyList<string> current, string value) => Contains(current, value)
        ? current.Where(c => !string.Equals(c, value, StringComparison.OrdinalIgnoreCase)).ToImmutableArray()
        : [.. current, value];

    public static ImmutableArray<string> AddAll(IReadOnlyList<string> current, IEnumerable<string> values) =>
        values.Aggregate(current.ToImmutableArray(), (list, v) => Contains(list, v) ? list : list.Add(v));

    /// <summary>The next free "M2D 01" style name for Capture active view.</summary>
    public static string NextCaptureName(IEnumerable<string> existing)
    {
        var names = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
        for (var i = 1; ; i++) if (names.Add($"M2D {i:00}")) return $"M2D {i:00}";
    }
}
