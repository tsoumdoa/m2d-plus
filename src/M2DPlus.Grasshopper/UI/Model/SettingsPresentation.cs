using System.Collections.Immutable;
using M2DPlus.Core;

namespace M2DPlus.Grasshopper.UI.Model;

/// <summary>What the UI knows about the current request: views in View-list order, the Settings input branches in
/// GH path order (empty = not wired), the document units, the view objects' m2d.* rows keyed by view identity, and
/// the Depth input items (empty = not wired). Plain values only.</summary>
public sealed record UiContext(IReadOnlyList<ViewSettingsContext> Views, IReadOnlyList<IReadOnlyList<string>> InputBranches, DocumentUnits Units)
{
    public static UiContext Empty { get; } = new([], [], DocumentUnits.Metres);
    public IReadOnlyDictionary<string, SettingsRow> ViewRows { get; init; } = ImmutableDictionary<string, SettingsRow>.Empty;
    public IReadOnlyList<string?> Depth { get; init; } = [];
}

public enum FieldOrigin { Menu, Default, Input, ViewObjects }

/// <summary>One key as the component menu shows it. Text is Settings syntax; Display is its label.</summary>
public sealed record FieldState(string Key, string Text, string Display, string? MenuText, FieldOrigin Origin,
    bool Locked, int InputViews, int ObjectViews, int ViewCount)
{
    /// <summary>The menu label suffix (UX rev. 4 §7): input locks win; view-object overrides are only reported.</summary>
    public string Note => Locked ? "set by input"
        : InputViews > 0 ? $"set by input for {InputViews} of {ViewCount} views"
        : ObjectViews == ViewCount && ViewCount > 0 ? "set on every view object"
        : ObjectViews > 0 ? $"set on {ObjectViews} of {ViewCount} view objects" : "";
}

/// <summary>The UX rev. 4 §6 precedence (Settings input → view object → menu → default/auto) as the component
/// menu presents it. Core does the resolving; this only decides what each menu item shows and whether it is locked.</summary>
public sealed class SettingsPresentation
{
    private static readonly ViewSettingsContext Probe = new("probe", "probe", ViewSourceKind.Plane, true, 1, .001);
    private static readonly ImmutableDictionary<string, string> Defaults = Settings.Resolve([Probe], [], DocumentUnits.Metres)
        .Views[0].Values.ToImmutableDictionary(p => p.Key, p => p.Key == "name" ? "auto" : p.Value.Text);

    public ComponentUiState State { get; }
    public UiContext Context { get; }
    /// <summary>Input + view objects + menu, as the component will resolve it. Null if the host context is invalid.</summary>
    public SettingsResolution? Resolution { get; }

    public SettingsPresentation(ComponentUiState state, UiContext context)
    {
        State = state; Context = context;
        try { Resolution = Settings.Resolve(context.Views, context.InputBranches, context.Units, state.ToEditorSettings(context.ViewRows), context.Depth); }
        catch (ArgumentException) { Resolution = null; }
    }

    public static string DefaultText(string key) => Defaults[key];

    /// <summary>The menu's view of a key: the menu default (or built-in default), locked when the Settings input sets it
    /// for every view, with counts of views whose input or view object overrides it.</summary>
    public FieldState Field(string key)
    {
        var menu = State.Menu.Values.TryGetValue(key, out var m) ? m : null;
        var viewCount = Context.Views.Count;
        var views = Resolution?.Views ?? [];
        var byInput = views.Where(v => v[key].IsSetByInput).ToArray();
        var byObject = views.Count(v => !v[key].IsSetByInput && v[key].AssignedBy == SettingSource.EditorView);
        if (viewCount > 0 && byInput.Length == viewCount)
        {
            var texts = byInput.Select(v => InputText(key, v[key])).Distinct().ToArray();
            var text = texts.Length == 1 ? texts[0] : "varies";
            return new(key, text, texts.Length == 1 ? SettingsCatalog.Display(key, text) : "Varies by view", menu, FieldOrigin.Input, true, byInput.Length, 0, viewCount);
        }
        var value = menu ?? DefaultText(key);
        var origin = byObject == viewCount && viewCount > 0 ? FieldOrigin.ViewObjects : menu is null ? FieldOrigin.Default : FieldOrigin.Menu;
        return new(key, value, SettingsCatalog.Display(key, value), menu, origin, false, byInput.Length, byObject, viewCount);
    }

    private static string InputText(string key, SettingEntry entry) => key == "name" ? SettingsCatalog.UnquoteName(entry.Text) : entry.Text;
}
