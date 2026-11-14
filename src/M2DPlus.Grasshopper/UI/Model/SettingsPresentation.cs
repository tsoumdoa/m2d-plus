using System.Collections.Immutable;
using M2DPlus.Core;

namespace M2DPlus.Grasshopper.UI.Model;

/// <summary>What the UI knows about the current request: views in View-list order, the Settings input
/// branches in GH path order (empty = not wired) and the document units. Plain values only.</summary>
public sealed record UiContext(IReadOnlyList<ViewSettingsContext> Views, IReadOnlyList<IReadOnlyList<string>> InputBranches, DocumentUnits Units)
{
    public static UiContext Empty { get; } = new([], [], DocumentUnits.Metres);
}

public enum RowKind { AllViews, View, Unused }
public sealed record EditorRow(string Identity, string Name, string Source, RowKind Kind, int ViewIndex);

public enum FieldOrigin { Row, AllViews, Default, Input }

/// <summary>One key as one editor row shows it. Text is Settings syntax (the control's value); Display is its label.</summary>
public sealed record FieldState(string Key, string Text, string Display, string? RowText, FieldOrigin Origin,
    bool Locked, int InputViews, int ViewCount)
{
    public bool IsInherited => RowText is null && !Locked;
    /// <summary>The small tag beside a field (UX §6: "set by input"; blank means set in this row).</summary>
    public string Note => Locked ? "set by input"
        : InputViews > 0 ? $"set by input for {InputViews} of {ViewCount} views"
        : Origin == FieldOrigin.AllViews ? "as All views" : "";
}

/// <summary>The UX §6 precedence (input → view row → All views row → default) presented per editor row.
/// Core does the resolving; this only decides what each row and menu item shows and whether it is locked.</summary>
public sealed class SettingsPresentation
{
    private static readonly ViewSettingsContext Probe = new("probe", "probe", ViewSourceKind.Plane, true, 1, .001);
    private static readonly ImmutableDictionary<string, string> Defaults = Settings.Resolve([Probe], [], DocumentUnits.Metres)
        .Views[0].Values.ToImmutableDictionary(p => p.Key, p => p.Key == "name" ? "auto" : p.Value.Text);

    public ComponentUiState State { get; }
    public UiContext Context { get; }
    /// <summary>Input + editor, as the component will resolve it. Null if the host context is invalid.</summary>
    public SettingsResolution? Resolution { get; }
    /// <summary>Diagnostics of the editor values alone (bad text, contradictory fade), for the editor's status line.</summary>
    public ImmutableArray<SettingsDiagnostic> EditorDiagnostics { get; }
    public ImmutableArray<EditorRow> Rows { get; }

    public SettingsPresentation(ComponentUiState state, UiContext context)
    {
        State = state; Context = context;
        var editor = state.ToEditorSettings();
        try
        {
            Resolution = Settings.Resolve(context.Views, context.InputBranches, context.Units, editor);
            var probes = context.Views.Count > 0 ? context.Views : [Probe];
            EditorDiagnostics = Settings.Resolve(probes, [], context.Units, editor).Diagnostics;
        }
        catch (ArgumentException) { Resolution = null; EditorDiagnostics = []; }
        var rows = ImmutableArray.CreateBuilder<EditorRow>();
        rows.Add(new(ComponentUiState.AllViewsRow, "All views", "", RowKind.AllViews, -1));
        var used = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < context.Views.Count; i++)
            if (used.Add(context.Views[i].Identity))
                rows.Add(new(context.Views[i].Identity, context.Views[i].Name, SettingsCatalog.SourceLabel(context.Views[i]), RowKind.View, i));
        foreach (var identity in state.Views.Keys.Where(k => !used.Contains(k)))
            rows.Add(new(identity, identity, "unused", RowKind.Unused, -1));
        Rows = rows.ToImmutable();
    }

    public static string DefaultText(string key) => Defaults[key];
    public IEnumerable<EditorRow> UnusedRows => Rows.Where(r => r.Kind == RowKind.Unused);

    public FieldState Field(string row, string key)
    {
        var all = State.AllViews.Values.TryGetValue(key, out var a) ? a : null;
        var viewCount = Context.Views.Count;
        EffectiveSettings[] byInput = Resolution is null ? [] : Resolution.Views.Where(v => v[key].IsSetByInput).ToArray();
        if (row == ComponentUiState.AllViewsRow)
        {
            if (viewCount > 0 && byInput.Length == viewCount)
            {
                var texts = byInput.Select(v => InputText(key, v[key])).Distinct().ToArray();
                var text = texts.Length == 1 ? texts[0] : "varies";
                return new(key, text, texts.Length == 1 ? SettingsCatalog.Display(key, text) : "Varies by view", all, FieldOrigin.Input, true, byInput.Length, viewCount);
            }
            var value = all ?? DefaultText(key);
            return new(key, value, SettingsCatalog.Display(key, value), all, all is null ? FieldOrigin.Default : FieldOrigin.Row, false, byInput.Length, viewCount);
        }
        var index = Rows.FirstOrDefault(r => r.Identity == row)?.ViewIndex ?? -1;
        var own = State.Row(row).Values.TryGetValue(key, out var o) ? o : null;
        if (index >= 0 && Resolution is not null && Resolution.Views[index][key] is { IsSetByInput: true } entry)
        {
            var text = InputText(key, entry);
            return new(key, text, SettingsCatalog.Display(key, text), own, FieldOrigin.Input, true, 1, 1);
        }
        var inherited = own ?? all ?? DefaultText(key);
        var origin = own is not null ? FieldOrigin.Row : all is not null ? FieldOrigin.AllViews : FieldOrigin.Default;
        return new(key, inherited, SettingsCatalog.Display(key, inherited), own, origin, false, 0, 1);
    }

    private static string InputText(string key, SettingEntry entry) => key == "name" ? SettingsCatalog.UnquoteName(entry.Text) : entry.Text;

    /// <summary>A cell in the editor's upper table. "·" means "as All views" (UX §6.1).</summary>
    public string Cell(string row, string key)
    {
        var field = Field(row, key);
        if (field.Locked) return field.Display + " (input)";
        if (row != ComponentUiState.AllViewsRow && field.RowText is null) return "·";
        return field.Display;
    }

    public static ImmutableArray<string> TableKeys { get; } = ["depth", "beyond", "farclip", "fade", "scale"];
}
