using Eto.Drawing;
using Eto.Forms;
using M2DPlus.Core.Views;
using Button = Eto.Forms.Button;
using Color = Eto.Drawing.Color;
using HorizontalAlignment = Eto.Forms.HorizontalAlignment;
using Label = Eto.Forms.Label;
using Orientation = Eto.Forms.Orientation;
using Padding = Eto.Drawing.Padding;
using SystemColors = Eto.Drawing.SystemColors;
using TextBox = Eto.Forms.TextBox;
using VerticalAlignment = Eto.Forms.VerticalAlignment;
using Window = Eto.Forms.Window;

namespace M2DPlus.Grasshopper.UI;

/// <summary>
/// The "View box depth" pop-up (owner decision 2026-11-16, UX §2.2): one depth for every box a Convert to view boxes or
/// Create view run makes. A depth text box (a number in document units or a distance with units, read by the settings
/// unit parser), an <b>Auto</b> button (the default) that fills in the Auto value, a one-line hint saying what Auto
/// means for this selection, an inline error for invalid input, OK and Cancel. Modal on the Rhino UI thread, owned by
/// the Rhino main window (<c>DepthDialog.Rhino.cs</c>; this file is pure Eto so tools/dialog-render can show it). The only
/// depth UI M2D+ has: an explicit owner exception to "no settings dialogs" (§6.1).
/// </summary>
public sealed partial class DepthDialog : Dialog<DepthChoice?>
{
    private readonly ViewDepthPrompt _model;
    private readonly TextBox _depth;
    private readonly Label _error;

    public DepthDialog(ViewDepthPrompt model)
    {
        _model = model;
        Title = model.Title;
        Resizable = false; Minimizable = false; Maximizable = false; ShowInTaskbar = false;
        Padding = new Padding(10);
        _depth = new TextBox { Text = model.AutoText, Width = 200 };
        var auto = new Button { Text = "Auto", Width = 80, ToolTip = "Fill in the Auto depth for this selection." };
        // One line each, never wrapped (the hint and errors are short by design), so every Eto platform lays it out alike.
        var hint = new Label { Text = model.Hint, Wrap = WrapMode.None, TextColor = SystemColors.DisabledText };
        _error = new Label { Text = " ", Wrap = WrapMode.None, TextColor = Color.FromArgb(0xC4, 0x2B, 0x1C) };
        var ok = new Button { Text = "OK", Width = 80 };
        var cancel = new Button { Text = "Cancel", Width = 80 };
        DefaultButton = ok; AbortButton = cancel;

        auto.Click += (_, _) => { _depth.Text = model.AutoText; ShowError(null); _depth.Focus(); _depth.SelectAll(); };
        _depth.TextChanged += (_, _) => ShowError(null);
        ok.Click += (_, _) => Accept();
        cancel.Click += (_, _) => Close(null);
        Shown += (_, _) => { _depth.Focus(); _depth.SelectAll(); };

        Content = new StackLayout
        {
            Spacing = 6,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Items =
            {
                new StackLayout
                {
                    Orientation = Orientation.Horizontal, Spacing = 6, VerticalContentAlignment = VerticalAlignment.Center,
                    Items = { new Label { Text = model.DepthLabel }, new StackLayoutItem(_depth, true), auto },
                },
                hint,
                _error,
                new StackLayout
                {
                    Orientation = Orientation.Horizontal, Spacing = 6,
                    Items = { new StackLayoutItem(null, true), ok, cancel },
                },
            },
        };
    }

    /// <summary>The text in the depth box (tests and the render harness set it).</summary>
    public string DepthText { get => _depth.Text; set => _depth.Text = value; }

    /// <summary>The inline error shown, or null.</summary>
    public string? Error => string.IsNullOrWhiteSpace(_error.Text) ? null : _error.Text;

    /// <summary>OK: closes with the choice, or shows the inline error and stays open. Returns whether it closed.</summary>
    public bool Accept()
    {
        if (!_model.TryAccept(_depth.Text, out var choice, out var error)) { ShowError(error); _depth.Focus(); _depth.SelectAll(); return false; }
        Close(choice);
        return true;
    }

    private void ShowError(string? message) => _error.Text = message ?? " ";
}
