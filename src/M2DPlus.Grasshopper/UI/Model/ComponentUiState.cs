using System.Collections.Immutable;
using M2DPlus.Core;

namespace M2DPlus.Grasshopper.UI.Model;

/// <summary>Preview layers toggled from the component menu (UX §7 Preview ▸). Unknown names round-trip.</summary>
public static class PreviewLayers
{
    public const string Hidden = "hidden", Beyond = "beyond", FarClip = "farclip", Hatch = "hatch", Frame = "frame";
    public static ImmutableArray<(string Name, string Label)> All { get; } =
        [(Hidden, "Hidden"), (Beyond, "Beyond"), (FarClip, "Far clip"), (Hatch, "Hatch"), (Frame, "Frame")];
    public static ImmutableSortedSet<string> Default { get; } = ImmutableSortedSet.Create(StringComparer.Ordinal, Hidden, Beyond, FarClip, Hatch);
}

/// <summary>Everything the GH component persists for its UI: sparse editor rows (raw text, any key),
/// Run automatically, preview layers and unknown top-level properties from newer versions.
/// Results are never stored here (UX §5.1).</summary>
public sealed class ComponentUiState
{
    public const string AllViewsRow = "*";
    public SettingsRow AllViews { get; }
    public ImmutableSortedDictionary<string, SettingsRow> Views { get; }
    public bool AutoRun { get; }
    public ImmutableSortedSet<string> Preview { get; }
    /// <summary>Raw JSON values of top-level properties this version does not understand, kept verbatim.</summary>
    public ImmutableSortedDictionary<string, string> Extras { get; }

    public static ComponentUiState Default { get; } = new(SettingsRow.Empty, null, true, PreviewLayers.Default, null);

    public ComponentUiState(SettingsRow allViews, IEnumerable<KeyValuePair<string, SettingsRow>>? views, bool autoRun,
        IEnumerable<string>? preview, IEnumerable<KeyValuePair<string, string>>? extras)
    {
        AllViews = allViews ?? SettingsRow.Empty;
        Views = (views ?? []).Where(p => p.Value.Values.Count > 0)
            .ToImmutableSortedDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        AutoRun = autoRun;
        Preview = (preview ?? PreviewLayers.Default).ToImmutableSortedSet(StringComparer.Ordinal);
        Extras = (extras ?? []).ToImmutableSortedDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
    }

    /// <summary>True when the editor holds any value (the cobalt dot next to S, UX §1).</summary>
    public bool HasEditorValues => AllViews.Values.Count > 0 || Views.Count > 0;

    public SettingsRow Row(string identity) => identity == AllViewsRow ? AllViews
        : Views.TryGetValue(identity, out var row) ? row : SettingsRow.Empty;

    /// <summary>Core input for Settings.Resolve. Keys this version does not know stay stored but are not resolved,
    /// so a file from a newer M2D+ never produces unknown-key warnings here.</summary>
    public EditorSettings ToEditorSettings() => new(Known(AllViews), Views.Select(p => KeyValuePair.Create(p.Key, Known(p.Value))));

    private static SettingsRow Known(SettingsRow row) =>
        row.Values.Keys.All(Settings.Keys.Contains) ? row : new(row.Values.Where(p => Settings.Keys.Contains(p.Key)));

    /// <summary>Sets (text) or clears (null) one key in one row; other rows and unknown keys are untouched.</summary>
    public ComponentUiState With(string identity, string key, string? text)
    {
        ArgumentException.ThrowIfNullOrEmpty(identity);
        ArgumentException.ThrowIfNullOrEmpty(key);
        key = key.ToLowerInvariant();
        var values = Row(identity).Values;
        values = text is null ? values.Remove(key) : values.SetItem(key, text.Trim());
        return WithRow(identity, new SettingsRow(values));
    }

    public ComponentUiState WithRow(string identity, SettingsRow row) => identity == AllViewsRow
        ? new(row, Views, AutoRun, Preview, Extras)
        : new(AllViews, Views.SetItem(identity, row), AutoRun, Preview, Extras);

    /// <summary>Removes a view row entirely; for the All views row this restores every default.</summary>
    public ComponentUiState ClearRow(string identity) => identity == AllViewsRow
        ? new(SettingsRow.Empty, Views, AutoRun, Preview, Extras)
        : new(AllViews, Views.Remove(identity), AutoRun, Preview, Extras);

    public ComponentUiState RemoveRows(IEnumerable<string> identities) =>
        new(AllViews, Views.RemoveRange(identities), AutoRun, Preview, Extras);

    public ComponentUiState WithAutoRun(bool autoRun) => new(AllViews, Views, autoRun, Preview, Extras);

    public ComponentUiState WithPreview(string layer, bool on) =>
        new(AllViews, Views, AutoRun, on ? Preview.Add(layer) : Preview.Remove(layer), Extras);

    public override string ToString() => UiStateSerializer.Write(this);
}
