using Eto.Drawing;
using Eto.Forms;
using M2DPlus.Grasshopper.UI.Model;
// The GHA also imports WinForms/System.Drawing implicitly; this file is Eto only.
using Bitmap = Eto.Drawing.Bitmap;
using Button = Eto.Forms.Button;
using CheckBox = Eto.Forms.CheckBox;
using Color = Eto.Drawing.Color;
using ComboBox = Eto.Forms.ComboBox;
using Control = Eto.Forms.Control;
using Form = Eto.Forms.Form;
using GroupBox = Eto.Forms.GroupBox;
using Icon = Eto.Drawing.Icon;
using Keys = Eto.Forms.Keys;
using Label = Eto.Forms.Label;
using Orientation = Eto.Forms.Orientation;
using Padding = Eto.Drawing.Padding;
using Panel = Eto.Forms.Panel;
using Size = Eto.Drawing.Size;
using SystemColors = Eto.Drawing.SystemColors;
using SystemFonts = Eto.Drawing.SystemFonts;
using TextBox = Eto.Forms.TextBox;
using Clipboard = Eto.Forms.Clipboard;
using HorizontalAlignment = Eto.Forms.HorizontalAlignment;
using TextAlignment = Eto.Forms.TextAlignment;
using VerticalAlignment = Eto.Forms.VerticalAlignment;
using ListItem = Eto.Forms.ListItem;
using Binding = Eto.Forms.Binding;

namespace M2DPlus.Grasshopper.UI;

/// <summary>The View Range &amp; Settings editor (UX §6.1): an "All views" row plus one row per view on top,
/// the selected row's fields below. Modeless Eto form; all state lives in an <see cref="EditorSession"/>.</summary>
public sealed class SettingsEditorForm : Form
{
    internal static readonly Color Cobalt = Color.FromArgb(0x1F, 0x6F, 0xD1);
    private static readonly Color Problem = Color.FromArgb(0xC8, 0x3A, 0x32);
    private readonly EditorSession _session;
    private readonly Action<ComponentUiState> _apply;
    private readonly string? _readProblem;
    private readonly GridView _table = new() { AllowMultipleSelection = false, ShowHeader = true, Height = 150, GridLines = GridLines.Horizontal };
    private readonly Label _heading = new() { Font = SystemFonts.Bold() };
    private readonly Label _source = new() { TextColor = SystemColors.DisabledText };
    private readonly Label _strip = new() { TextColor = SystemColors.DisabledText };
    private readonly Label _status = new() { Wrap = WrapMode.Word };
    private readonly Button _reset = new();
    private readonly Button _removeUnused = new() { Visible = false };
    private readonly Button _applyButton = new() { Text = "Apply" };
    private readonly List<FieldEditor> _fields = new();
    private bool _updating;

    public SettingsEditorForm(EditorSession session, Action<ComponentUiState> apply, string? readProblem = null)
    {
        _session = session; _apply = apply; _readProblem = readProblem;
        Title = "M2D+ · View Range & Settings";
        Resizable = true; Minimizable = false; Maximizable = false; ShowInTaskbar = false;
        MinimumSize = new Size(760, 620);
        ClientSize = new Size(840, 720);
        try { Icon = new Icon(1f, M2DIconsEto.Drawing()); } catch (Exception e) when (e is NotSupportedException or ArgumentException) { }
        Content = Build();
        KeyDown += (_, e) => { if (e.Key == Keys.Escape) { e.Handled = true; Close(); } };
        Reload();
        TryRhinoStyle();
    }

    public EditorSession Session => _session;

    // Follows Rhino's light/dark theme inside Rhino. Looked up at run time so this file depends on Eto and the
    // UI models only, and also runs where Rhino.UI is absent (the standalone preview in tools/EditorPreview).
    private void TryRhinoStyle()
    {
        try { Type.GetType("Rhino.UI.EtoExtensions, Rhino.UI")?.GetMethod("UseRhinoStyle", [typeof(Control)])?.Invoke(null, [this]); }
        catch (Exception e) when (e is System.Reflection.TargetInvocationException or FileNotFoundException or FileLoadException or TypeLoadException) { }
    }

    private Control Build()
    {
        _table.Columns.Add(Column("View", nameof(TableItem.Name), 150));
        _table.Columns.Add(Column("Source", nameof(TableItem.Source), 130));
        _table.Columns.Add(Column("Depth", nameof(TableItem.Depth), 80));
        _table.Columns.Add(Column("Beyond", nameof(TableItem.Beyond), 80));
        _table.Columns.Add(Column("Far clip", nameof(TableItem.FarClip), 110));
        _table.Columns.Add(Column("Fade", nameof(TableItem.Fade), 60));
        _table.Columns.Add(Column("Scale", nameof(TableItem.Scale), 70));
        _table.CellFormatting += (_, e) =>
        {
            if (e.Item is not TableItem item) return;
            if (item.Kind == RowKind.Unused) e.ForegroundColor = SystemColors.DisabledText;
            else if (item.Kind == RowKind.View && item.Cell(e.Column.DisplayIndex) == "·") e.ForegroundColor = SystemColors.DisabledText;
            if (item.Kind == RowKind.AllViews) e.Font = SystemFonts.Bold();
        };
        _table.SelectedRowsChanged += (_, _) =>
        {
            if (_updating || _table.SelectedItem is not TableItem item) return;
            _session.Select(item.Identity); Reload();
        };

        var viewRange = Group(SettingsCatalog.ViewRange,
            Row("View plane", _source),
            Field("depth"), Field("beyond"), Field("farclip"), Field("crop"),
            Spacer(),
            Field("fade"), Field("fade.start"), Field("fade.end"), Field("fade.limit"));
        var lines = Group(SettingsCatalog.Lines, Field("hidden"), Field("tangents"), Field("seams"), Field("glass"), Field("weights"));
        var cuts = Group(SettingsCatalog.Cuts, Field("hatch"), Field("cuts"));
        var placement = Group(SettingsCatalog.Placement, Field("scale"), Row("", _strip), Field("gap"), Field("name"));

        var body = new TableLayout
        {
            Spacing = new Size(12, 0),
            Rows =
            {
                new TableRow(
                    new TableCell(new StackLayout { Spacing = 10, HorizontalContentAlignment = HorizontalAlignment.Stretch, Items = { viewRange } }, true),
                    new TableCell(new StackLayout { Spacing = 10, HorizontalContentAlignment = HorizontalAlignment.Stretch, Items = { lines, cuts, placement } }, true)),
                new TableRow { ScaleHeight = true }
            }
        };

        var copy = new Button { Text = "Copy as text", ToolTip = "Copy the editor's values as Settings text with [view] headers, ready to paste into a Panel." };
        copy.Click += (_, _) => { Clipboard.Instance.Text = _session.CopyAsText(); ShowStatus("Copied the editor's values as Settings text.", null); };
        _reset.Click += (_, _) => { _session.ResetSelected(); Reload(); };
        _removeUnused.Click += (_, _) => { _session.RemoveUnused(); Reload(); };
        var ok = new Button { Text = "OK" };
        ok.Click += (_, _) => { Commit(); Close(); };
        var cancel = new Button { Text = "Cancel" };
        cancel.Click += (_, _) => Close();
        _applyButton.Click += (_, _) => { Commit(); Reload(); };

        var footer = new TableLayout
        {
            Spacing = new Size(8, 0),
            Rows = { new TableRow(copy, _reset, _removeUnused, new TableCell(null, true), ok, cancel, _applyButton) }
        };

        return new TableLayout
        {
            Padding = new Padding(14, 12),
            Spacing = new Size(0, 10),
            Rows =
            {
                _table,
                new StackLayout { Orientation = Orientation.Horizontal, Spacing = 8, VerticalContentAlignment = VerticalAlignment.Center,
                    Items = { _heading, new Label { Text = "· “·” in the table means “as All views”", TextColor = SystemColors.DisabledText } } },
                new TableRow(new Scrollable { Border = BorderType.None, Content = body, ExpandContentWidth = true }) { ScaleHeight = true },
                _status,
                footer
            }
        };
    }

    private void Commit()
    {
        if (!_session.IsDirty) return;
        _apply(_session.Apply());
    }

    private static GridColumn Column(string header, string property, int width) => new()
    {
        HeaderText = header, Width = width, Editable = false, Sortable = false, Expand = property == nameof(TableItem.Name),
        DataCell = new TextBoxCell { Binding = Binding.Property<string>(property) }
    };

    private static GroupBox Group(string title, params TableRow[] rows)
    {
        var layout = new TableLayout { Spacing = new Size(8, 4), Padding = new Padding(8, 6, 8, 8) };
        foreach (var row in rows) layout.Rows.Add(row);
        return new GroupBox { Text = title, Content = layout };
    }

    private static TableRow Row(string label, Control control) =>
        new(new Label { Text = label, VerticalAlignment = VerticalAlignment.Center, Width = 76, TextColor = SystemColors.DisabledText }, new TableCell(control, true));

    private static TableRow Spacer() => new(new Panel { Height = 4 });

    private TableRow Field(string key)
    {
        var spec = SettingsCatalog.Spec(key);
        var editor = FieldEditor.Create(spec, this);
        _fields.Add(editor);
        var text = spec.Kind != FieldKind.Toggle ? spec.Label : spec.Key == "fade" ? "Depth cueing" : "";
        var label = new Label { Text = text,
            VerticalAlignment = VerticalAlignment.Center, Width = 76, TextColor = SystemColors.DisabledText, ToolTip = spec.Tooltip };
        var tail = new StackLayout { Orientation = Orientation.Horizontal, Spacing = 6, VerticalContentAlignment = VerticalAlignment.Center, Items = { editor.Note, editor.Reset } };
        return new TableRow(label, new TableCell(new StackLayout
        {
            Orientation = Orientation.Horizontal, Spacing = 8, VerticalContentAlignment = VerticalAlignment.Center,
            Items = { new StackLayoutItem(editor.Control, false), tail }
        }, true));
    }

    internal void Edit(string key, string? text)
    {
        if (_updating) return;
        var error = text is null ? null : _session.Validate(key, text);
        if (error is not null) { _fields.First(f => f.Key == key).ShowError(error); return; }
        _session.Set(key, text);
        Reload(keepFocus: true);
    }

    /// <summary>Re-render everything from the session: cheap, and keeps every lock and "·" honest.</summary>
    private void Reload(bool keepFocus = false)
    {
        _updating = true;
        try
        {
            var view = _session.View;
            var items = view.Rows.Select(r => new TableItem(r, view)).ToList();
            _table.DataStore = items;
            var index = items.FindIndex(i => i.Identity == _session.Selected);
            if (index >= 0) _table.SelectRow(index);
            var row = _session.SelectedRow;
            _heading.Text = row.Kind == RowKind.Unused ? $"Unused row “{row.Name}”" : row.Name;
            _source.Text = row.Kind switch { RowKind.AllViews => "Every view, unless its row says otherwise", RowKind.Unused => "No current view has this identity", _ => row.Source };
            foreach (var field in _fields) field.Show(_session.Field(field.Key), keepFocus);
            var scale = _session.Field("scale"); var weights = _session.Field("weights");
            _strip.Text = SettingsCatalog.WeightStrip(scale.Text, weights.Text, twoLines: true);
            _fields.First(f => f.Key == "scale").Control.Enabled = !scale.Locked && SettingsCatalog.Canonical("weights", weights.Text) != "layers";
            _reset.Text = _session.ResetLabel;
            _reset.Enabled = _session.Working.Row(_session.Selected).Values.Count > 0;
            var unused = view.UnusedRows.Count();
            _removeUnused.Visible = unused > 0;
            _removeUnused.Text = $"Remove unused ({unused})";
            _applyButton.Enabled = _session.IsDirty;
            var problems = _session.Problems;
            if (problems.Count > 0) ShowStatus(string.Join("  ", problems), Problem);
            else if (_readProblem is not null) ShowStatus(_readProblem, Problem);
            else if (view.Context.InputBranches.Count > 0) ShowStatus("The Settings input is wired: fields it sets are locked and show its value. Unwire it and these values come back.", Cobalt);
            else ShowStatus("", null);
        }
        finally { _updating = false; }
    }

    private void ShowStatus(string text, Color? colour)
    {
        _status.Text = text;
        _status.TextColor = colour ?? SystemColors.ControlText;
    }

    public void Rebase(ComponentUiState state) { _session.Rebase(state); Reload(); }
    public void UpdateContext(UiContext context) { _session.UpdateContext(context); Reload(); }
    public void SelectRow(string identity) { _session.Select(identity); Reload(); }

    private sealed class TableItem(EditorRow row, SettingsPresentation view)
    {
        public string Identity { get; } = row.Identity;
        public RowKind Kind { get; } = row.Kind;
        public string Name { get; } = row.Kind == RowKind.Unused ? "Unused: " + row.Name : row.Name;
        public string Source { get; } = row.Kind == RowKind.AllViews ? "defaults" : row.Source;
        public string Depth { get; } = view.Cell(row.Identity, "depth");
        public string Beyond { get; } = view.Cell(row.Identity, "beyond");
        public string FarClip { get; } = view.Cell(row.Identity, "farclip");
        public string Fade { get; } = view.Cell(row.Identity, "fade");
        public string Scale { get; } = view.Cell(row.Identity, "scale");
        public string? Cell(int column) => column switch { 2 => Depth, 3 => Beyond, 4 => FarClip, 5 => Fade, 6 => Scale, _ => null };
    }

    /// <summary>One key's control, its note ("set by input", "as All views", an error) and a Reset link.</summary>
    private abstract class FieldEditor(string key, SettingsEditorForm form)
    {
        public string Key { get; } = key;
        protected SettingsEditorForm Form { get; } = form;
        public abstract Control Control { get; }
        public Label Note { get; } = new() { TextColor = SystemColors.DisabledText };
        public LinkButton Reset { get; } = new() { Text = "Reset" };
        protected bool Locked { get; private set; }

        public static FieldEditor Create(FieldSpec spec, SettingsEditorForm form)
        {
            FieldEditor editor = spec.Kind switch
            {
                FieldKind.Toggle => new ToggleEditor(spec, form),
                FieldKind.Percent => new PercentEditor(spec, form),
                FieldKind.Choice when spec.Key == "farclip" => new RadioEditor(spec, form),
                FieldKind.Choice => new DropDownEditor(spec, form),
                FieldKind.Text => new TextEditor(spec, form),
                _ => new ComboEditor(spec, form)
            };
            editor.Control.ToolTip = spec.Tooltip;
            editor.Reset.Click += (_, _) => form.Edit(spec.Key, null);
            return editor;
        }

        public void Show(FieldState field, bool keepFocus)
        {
            Locked = field.Locked;
            Control.Enabled = !field.Locked;
            Note.Text = field.Note;
            Note.TextColor = field.Locked || field.InputViews > 0 ? Cobalt : SystemColors.DisabledText;
            Note.ToolTip = field.Locked ? "The Settings input sets this key; its value is shown. Unwire the input to edit it here." : null;
            Reset.Visible = field.RowText is not null && !field.Locked;
            Reset.ToolTip = Form._session.SelectedIsAllViews ? "Back to the default" : "Use the All views value";
            if (!(keepFocus && Control.HasFocus)) Display(field);
        }

        public void ShowError(string message)
        {
            Note.Text = "not valid";
            Note.TextColor = Problem;
            Note.ToolTip = message;
        }

        protected abstract void Display(FieldState field);
    }

    private sealed class ToggleEditor : FieldEditor
    {
        private readonly CheckBox _box;
        public ToggleEditor(FieldSpec spec, SettingsEditorForm form) : base(spec.Key, form)
        {
            _box = new CheckBox { Text = spec.Label, ThreeState = false };
            _box.CheckedChanged += (_, _) => Form.Edit(Key, _box.Checked == true ? "on" : "off");
        }
        public override Control Control => _box;
        protected override void Display(FieldState field) => _box.Checked = SettingsCatalog.IsOn(Key, field.Text);
    }

    private sealed class PercentEditor : FieldEditor
    {
        private readonly Slider _slider = new() { MinValue = 0, MaxValue = 100, TickFrequency = 25, Width = 140 };
        private readonly Label _value = new() { Width = 44, TextAlignment = TextAlignment.Right };
        private readonly StackLayout _layout;
        public PercentEditor(FieldSpec spec, SettingsEditorForm form) : base(spec.Key, form)
        {
            _layout = new StackLayout { Orientation = Orientation.Horizontal, Spacing = 6, VerticalContentAlignment = VerticalAlignment.Center, Items = { _slider, _value } };
            _slider.ValueChanged += (_, _) => { _value.Text = _slider.Value + " %"; Form.Edit(Key, _slider.Value + " %"); };
        }
        public override Control Control => _layout;
        protected override void Display(FieldState field)
        {
            var percent = SettingsCatalog.Percent(field.Text) ?? 0;
            _slider.Value = (int)Math.Round(percent);
            _value.Text = SettingsCatalog.Number(percent) + " %";
        }
    }

    private sealed class DropDownEditor : FieldEditor
    {
        private readonly DropDown _list = new() { Width = 160 };
        private readonly FieldSpec _spec;
        public DropDownEditor(FieldSpec spec, SettingsEditorForm form) : base(spec.Key, form)
        {
            _spec = spec;
            foreach (var option in spec.Options) _list.Items.Add(new ListItem { Text = option.Label, Key = option.Value });
            _list.SelectedIndexChanged += (_, _) => { if (_list.SelectedKey is { } value) Form.Edit(Key, value); };
        }
        public override Control Control => _list;
        protected override void Display(FieldState field)
        {
            var canonical = SettingsCatalog.Canonical(Key, field.Text);
            _list.SelectedKey = _spec.Options.FirstOrDefault(o => SettingsCatalog.Canonical(Key, o.Value) == canonical)?.Value;
        }
    }

    private sealed class RadioEditor : FieldEditor
    {
        private readonly RadioButtonList _list = new() { Orientation = Orientation.Vertical, Spacing = new Size(0, 2) };
        private readonly FieldSpec _spec;
        public RadioEditor(FieldSpec spec, SettingsEditorForm form) : base(spec.Key, form)
        {
            _spec = spec;
            foreach (var option in spec.Options) _list.Items.Add(new ListItem { Text = option.Label, Key = option.Value });
            _list.SelectedIndexChanged += (_, _) => { if (_list.SelectedKey is { } value) Form.Edit(Key, value); };
        }
        public override Control Control => _list;
        protected override void Display(FieldState field)
        {
            var canonical = SettingsCatalog.Canonical(Key, field.Text);
            _list.SelectedKey = _spec.Options.FirstOrDefault(o => SettingsCatalog.Canonical(Key, o.Value) == canonical)?.Value;
        }
    }

    /// <summary>Editable list for distances and scale: preset choices plus typed text such as "40 m" or "1:75".</summary>
    private sealed class ComboEditor : FieldEditor
    {
        private readonly ComboBox _box = new() { AutoComplete = true, Width = 120 };
        public ComboEditor(FieldSpec spec, SettingsEditorForm form) : base(spec.Key, form)
        {
            foreach (var option in spec.Options) _box.Items.Add(new ListItem { Text = option.Label, Key = option.Value });
            _box.TextChanged += (_, _) => Commit();
            _box.SelectedIndexChanged += (_, _) => { if (_box.SelectedKey is { } value) Form.Edit(Key, value); };
        }
        private void Commit()
        {
            var text = _box.Text?.Trim() ?? "";
            if (text.Length == 0) return;
            var option = SettingsCatalog.Spec(Key).Options.FirstOrDefault(o => string.Equals(o.Label, text, StringComparison.OrdinalIgnoreCase));
            Form.Edit(Key, option?.Value ?? text);
        }
        public override Control Control => _box;
        protected override void Display(FieldState field) => _box.Text = SettingsCatalog.Display(Key, field.Text);
    }

    private sealed class TextEditor : FieldEditor
    {
        private readonly TextBox _box = new() { Width = 160, PlaceholderText = "From the view" };
        public TextEditor(FieldSpec spec, SettingsEditorForm form) : base(spec.Key, form)
        {
            _box.TextChanged += (_, _) =>
            {
                var text = _box.Text?.Trim() ?? "";
                Form.Edit(Key, text.Length == 0 ? null : text);
            };
        }
        public override Control Control => _box;
        protected override void Display(FieldState field)
        {
            var text = SettingsCatalog.Canonical(Key, field.Text);
            _box.Text = text == "auto" ? "" : text;
        }
    }
}

/// <summary>The component icon for the editor's title bar, without referencing Rhino's Eto helpers.</summary>
internal static class M2DIconsEto
{
    public static Bitmap Drawing()
    {
        using var source = M2DIcons.Drawing(32);
        using var stream = new MemoryStream();
        source.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
        stream.Position = 0;
        return new Bitmap(stream);
    }
}
