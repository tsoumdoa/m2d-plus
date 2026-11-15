using M2DPlus.Core;

namespace M2DPlus.Grasshopper.UI.Model;

/// <summary>The View Range &amp; Settings editor without any UI toolkit: a working copy of the component's
/// state, the selected row, validation, Reset, Copy as text and Apply/OK/Cancel semantics (UX §6.1).</summary>
public sealed class EditorSession
{
    private static readonly ViewSettingsContext Probe = new("probe", "probe", ViewSourceKind.Plane, true, 1, .001);
    public ComponentUiState Original { get; private set; }
    public ComponentUiState Working { get; private set; }
    public UiContext Context { get; private set; }
    public SettingsPresentation View { get; private set; }
    public string Selected { get; private set; } = ComponentUiState.AllViewsRow;

    public EditorSession(ComponentUiState original, UiContext context)
    {
        Original = Working = original; Context = context;
        View = new(Working, Context);
    }

    public bool IsDirty => UiStateSerializer.Write(Working) != UiStateSerializer.Write(Original);
    public EditorRow SelectedRow => View.Rows.First(r => r.Identity == Selected);
    public bool SelectedIsAllViews => Selected == ComponentUiState.AllViewsRow;
    public FieldState Field(string key) => View.Field(Selected, key);

    public void Select(string identity)
    {
        if (View.Rows.Any(r => r.Identity == identity)) Selected = identity;
    }

    /// <summary>The views or inputs changed while the editor is open: keep edits, refresh rows and locks.</summary>
    public void UpdateContext(UiContext context)
    {
        Context = context; Refresh();
        if (!View.Rows.Any(r => r.Identity == Selected)) Selected = ComponentUiState.AllViewsRow;
    }

    /// <summary>Null when the text is a valid value for the key; otherwise Core's message (with a suggestion).</summary>
    public string? Validate(string key, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "Enter a value, or reset the field.";
        var row = new SettingsRow([KeyValuePair.Create(key, text)]);
        var probe = Settings.Resolve([Probe], [], Context.Units, new EditorSettings(row));
        return probe.Diagnostics.FirstOrDefault(d => d.Key == key && d.Severity != DiagnosticSeverity.Remark && d.Message.StartsWith("Invalid", StringComparison.Ordinal))?.Message;
    }

    /// <summary>Sets the key in the selected row (null = inherit / back to default). Locked or invalid edits are refused.</summary>
    public bool Set(string key, string? text)
    {
        if (Field(key).Locked) return false;
        if (text is not null && Validate(key, text) is not null) return false;
        var current = Working.Row(Selected).Values.TryGetValue(key, out var existing) ? existing : null;
        if (current == text?.Trim()) return true;
        Working = Working.With(Selected, key, text);
        Refresh();
        return true;
    }

    /// <summary>"Reset to All views" for a view row; "Reset to defaults" for the All views row.</summary>
    public void ResetSelected() { Working = Working.ClearRow(Selected); Refresh(); }
    public string ResetLabel => SelectedIsAllViews ? "Reset to defaults" : "Reset to All views";

    public void RemoveUnused()
    {
        Working = Working.RemoveRows(View.UnusedRows.Select(r => r.Identity).ToArray());
        if (!Working.Views.ContainsKey(Selected)) Selected = ComponentUiState.AllViewsRow;
        Refresh();
    }

    /// <summary>Editor values as Settings text with [view] headers, ready for a Panel (Core serializer).</summary>
    public string CopyAsText() => Settings.CopyEditorAsText(Working.ToEditorSettings(), Context.Views);

    /// <summary>Editor-only problems for the status line, e.g. a contradictory fade start/end.</summary>
    public IReadOnlyList<string> Problems => View.EditorDiagnostics.Where(d => d.Severity != DiagnosticSeverity.Remark)
        .Select(d => d.Message).Distinct().ToArray();

    /// <summary>Apply/OK: the working copy becomes the new original and is returned for the component to store.</summary>
    public ComponentUiState Apply() { Original = Working; return Working; }
    public void Revert() { Working = Original; Refresh(); }

    /// <summary>The component's state changed elsewhere (menu toggle, undo). Unedited sessions follow it;
    /// a session with pending edits keeps them and only moves its baseline.</summary>
    public void Rebase(ComponentUiState state)
    {
        var dirty = IsDirty;
        Original = state;
        if (!dirty) Working = state;
        Refresh();
        if (!View.Rows.Any(r => r.Identity == Selected)) Selected = ComponentUiState.AllViewsRow;
    }

    private void Refresh() => View = new(Working, Context);
}
