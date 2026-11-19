using System.Collections.Immutable;
using M2DPlus.Core;

namespace M2DPlus.Grasshopper.UI.Model;

/// <summary>Preview layers toggled from the component menu (UX §7 Preview ▸). Unknown names round-trip.</summary>
public static class PreviewLayers
{
    public const string Hidden = "hidden", Beyond = "beyond", FarClip = "farclip", Hatch = "hatch", Frame = "frame", Views = "views",
        Ignored = "ignored";
    /// <summary>Present = Preview ▸ Ink: every line in ink. Absent (the default) = Preview ▸ Object colours. Stored as the
    /// exception, so preview sets saved before the toggle existed read as object colours.</summary>
    public const string Ink = "ink";
    /// <summary>Ignored (min visible) is off by default: those lines are left out of the drawing, so the preview hides
    /// them unless asked, and then draws them very faint.</summary>
    public static ImmutableArray<(string Name, string Label)> All { get; } =
        [(Views, "View frames"), (Hidden, "Hidden"), (Beyond, "Beyond"), (FarClip, "Far clip"), (Hatch, "Hatch"), (Frame, "Frame"), (Ignored, "Ignored")];
    public static ImmutableSortedSet<string> Default { get; } = ImmutableSortedSet.Create(StringComparer.Ordinal, Views, Hidden, Beyond, FarClip, Hatch);
}

/// <summary>Everything the GH component persists for its UI (UX rev. 4): the right-click menu's global defaults
/// (sparse Settings text, any key), Run automatically, preview layers and unknown top-level properties from other
/// versions. Per-view values live on the view objects (m2d.* user text), never here. Results are never stored.</summary>
public sealed class ComponentUiState
{
    /// <summary>Global defaults set from the right-click menu; they apply to every view below view objects and input.</summary>
    public SettingsRow Menu { get; }
    public bool AutoRun { get; }
    public ImmutableSortedSet<string> Preview { get; }
    /// <summary>Raw JSON values of top-level properties this version does not understand, kept verbatim.</summary>
    public ImmutableSortedDictionary<string, string> Extras { get; }

    public static ComponentUiState Default { get; } = new(SettingsRow.Empty, true, PreviewLayers.Default, null);

    public ComponentUiState(SettingsRow menu, bool autoRun, IEnumerable<string>? preview, IEnumerable<KeyValuePair<string, string>>? extras)
    {
        Menu = menu ?? SettingsRow.Empty;
        AutoRun = autoRun;
        Preview = (preview ?? PreviewLayers.Default).ToImmutableSortedSet(StringComparer.Ordinal);
        Extras = (extras ?? []).ToImmutableSortedDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
    }

    /// <summary>True when a menu default differs from the built-in default.</summary>
    public bool HasMenuValues => Menu.Values.Count > 0;

    /// <summary>Core input for Settings.Resolve: the menu row plus the view objects' rows. Keys this version does not
    /// know stay stored but are not resolved, so a newer file never produces unknown-key warnings here.</summary>
    public EditorSettings ToEditorSettings(IReadOnlyDictionary<string, SettingsRow>? viewObjects = null) =>
        new(Known(Menu), viewObjects?.Select(p => KeyValuePair.Create(p.Key, p.Value)));

    private static SettingsRow Known(SettingsRow row) =>
        row.Values.Keys.All(Settings.Keys.Contains) ? row : new(row.Values.Where(p => Settings.Keys.Contains(p.Key)));

    /// <summary>Sets (text) or clears (null) one menu default; unknown keys are untouched.
    /// Tolerance = document/auto clears the override, so choosing the default also preserves the unset run key.</summary>
    public ComponentUiState With(string key, string? text)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        key = key.ToLowerInvariant();
        if (key == "tolerance" && text?.Trim().ToLowerInvariant() is "document" or "auto") text = null;
        var values = text is null ? Menu.Values.Remove(key) : Menu.Values.SetItem(key, text.Trim());
        return new(new SettingsRow(values), AutoRun, Preview, Extras);
    }

    /// <summary>Back to every built-in default (menu row only).</summary>
    public ComponentUiState ResetMenu() => new(SettingsRow.Empty, AutoRun, Preview, Extras);

    public ComponentUiState WithAutoRun(bool autoRun) => new(Menu, autoRun, Preview, Extras);

    public ComponentUiState WithPreview(string layer, bool on) =>
        new(Menu, AutoRun, on ? Preview.Add(layer) : Preview.Remove(layer), Extras);

    public override string ToString() => UiStateSerializer.Write(this);
}
