using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Grasshopper.GUI;
using Grasshopper.GUI.Canvas;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Attributes;
using M2DPlus.Grasshopper.UI.Model;

namespace M2DPlus.Grasshopper.UI;

/// <summary>Standard GH body plus the UX §1 additions: a 2 px cobalt progress line along the bottom of the body
/// while a job runs, a Bake / Cancel / Run button at the right end of the message tab, double-click to open the
/// editor, and a cobalt dot beside S when the editor holds values. Colours come from GH's palette except cobalt.</summary>
public sealed class DrawingAttributes : GH_ComponentAttributes
{
    public static readonly Color Cobalt = Color.FromArgb(0x1F, 0x6F, 0xD1);
    private const float TabHeight = 17, ButtonHeight = 13, RepaintMilliseconds = 100; // ≤ 10 Hz
    private readonly Func<DrawingUi> _ui;
    private RectangleF _tab, _button;
    private FooterButton _shown;
    private bool _pressed;
    private GH_Canvas? _canvas;
    private System.Windows.Forms.Timer? _timer;
    private DrawingRuntime _painted;

    public DrawingAttributes(GH_Component owner, Func<DrawingUi> ui) : base(owner) => _ui = ui;

    private DrawingUi Ui => _ui();
    /// <summary>For tests: the last painted button and its canvas rectangle.</summary>
    public (FooterButton Button, RectangleF Bounds) LastButton => (_shown, _button);

    /// <summary>Wide enough for a readable message beside the tab button. The extra width goes to the
    /// parameter columns (as with longer nicknames); the icon/nickname box keeps GH's size.</summary>
    public const float MinimumWidth = 148;

    protected override void Layout()
    {
        base.Layout();
        var extra = MinimumWidth - Bounds.Width;
        if (extra <= 0) return;
        var half = (float)Math.Ceiling(extra / 2);
        foreach (var input in Owner.Params.Input)
        {
            var b = input.Attributes.Bounds;
            input.Attributes.Bounds = new RectangleF(b.X - half, b.Y, b.Width + half, b.Height);
            input.Attributes.Pivot = new PointF(input.Attributes.Pivot.X - half / 2, input.Attributes.Pivot.Y);
        }
        foreach (var output in Owner.Params.Output)
        {
            var b = output.Attributes.Bounds;
            output.Attributes.Bounds = b with { Width = b.Width + half };
            output.Attributes.Pivot = new PointF(output.Attributes.Pivot.X + half / 2, output.Attributes.Pivot.Y);
        }
        Bounds = LayoutBounds(Owner, m_innerBounds);
    }

    protected override void Render(GH_Canvas canvas, Graphics graphics, GH_CanvasChannel channel)
    {
        if (channel != GH_CanvasChannel.Objects) { base.Render(canvas, graphics, channel); return; }
        var owner = (GH_Component)Owner;
        var message = owner.Message;
        // GH's own tab is redrawn below with room for the button; hide it from the base pass only.
        owner.Message = null;
        try { base.Render(canvas, graphics, channel); }
        finally { owner.Message = message; }
        if (canvas.DrawingMode == GH_CanvasMode.Control) _canvas = canvas;
        var visible = Bounds; visible.Height += TabHeight + 2;
        if (!canvas.Viewport.IsVisible(ref visible, 10)) { _tab = _button = RectangleF.Empty; return; }

        var palette = GH_CapsuleRenderEngine.GetImpliedPalette(owner);
        if (palette == GH_Palette.Normal && !owner.IsPreviewCapable) palette = GH_Palette.Hidden;
        var style = GH_CapsuleRenderEngine.GetImpliedStyle(palette, Selected, owner.Locked, owner.Hidden);
        var runtime = Ui.CurrentRuntime;
        var smoothing = graphics.SmoothingMode;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        try
        {
            var body = (RectangleF)GH_Convert.ToRectangle(Bounds);
            if (runtime.Phase == DrawingPhase.Running) RenderProgress(graphics, body, runtime.Fraction);
            RenderTab(graphics, body, message, RuntimeModel.Button(runtime), style);
            if (Ui.State.HasEditorValues) RenderSettingsDot(graphics, owner);
        }
        finally { graphics.SmoothingMode = smoothing; }
        _painted = runtime;
        if (runtime.Phase == DrawingPhase.Running && canvas.DrawingMode == GH_CanvasMode.Control) StartTimer();
    }

    private static void RenderProgress(Graphics graphics, RectangleF body, double? fraction)
    {
        // Inside the capsule's bottom edge, clear of its rounded corners.
        var track = new RectangleF(body.Left + 5, body.Bottom - 4, body.Width - 10, 2);
        if (track.Width <= 0) return;
        using (var faint = new SolidBrush(Color.FromArgb(56, Cobalt))) graphics.FillRectangle(faint, track);
        RectangleF bar;
        if (fraction is double f)
            bar = track with { Width = (float)(track.Width * Math.Clamp(f, 0, 1)) };
        else
        {
            // Indeterminate: a quarter-width segment sweeping once every 1.2 s.
            var width = track.Width * .25f;
            var phase = Environment.TickCount64 % 1200 / 1200f;
            var x = track.Left - width + (track.Width + width) * phase;
            bar = RectangleF.FromLTRB(Math.Max(track.Left, x), track.Top, Math.Min(track.Right, x + width), track.Bottom);
        }
        if (bar.Width <= 0) return;
        using var ink = new SolidBrush(Cobalt);
        graphics.FillRectangle(ink, bar);
    }

    private void RenderTab(Graphics graphics, RectangleF body, string? message, FooterButton button, GH_PaletteStyle style)
    {
        _tab = _button = RectangleF.Empty; _shown = FooterButton.None;
        var alpha = GH_Canvas.ZoomFadeMedium;
        if (alpha < 5 || (string.IsNullOrWhiteSpace(message) && button == FooterButton.None)) return;
        // Same footprint and shape as GH's message tab, a little taller so the button fits.
        var tab = new RectangleF(body.Left + 3, body.Bottom, body.Width - 6, TabHeight);
        using (var path = TabPath(tab))
        using (var fill = new SolidBrush(Color.FromArgb(alpha, style.Edge)))
        using (var edge = new Pen(Color.FromArgb(alpha, style.Edge)) { LineJoin = LineJoin.Bevel })
        {
            graphics.FillPath(fill, path);
            graphics.DrawPath(edge, path);
        }
        _tab = tab;
        var textArea = tab;
        var foreground = GH_GraphicsUtil.ForegroundColour(style.Edge, 200);
        var standard = GH_FontServer.StandardAdjusted;
        graphics.TextRenderingHint = graphics.Transform.Elements[0] == 1 ? GH_TextRenderingConstants.GH_CrispText : GH_TextRenderingConstants.GH_SmoothText;
        if (button != FooterButton.None)
        {
            var label = RuntimeModel.Label(button);
            using var font = new Font(standard.FontFamily, standard.Size * .82f, FontStyle.Bold, standard.Unit);
            var width = Math.Max(28, graphics.MeasureString(label, font).Width + 6);
            if (width < tab.Width * .45f)
            {
                _button = new RectangleF(tab.Right - width - 2, tab.Top + (TabHeight - ButtonHeight) / 2, width, ButtonHeight);
                _shown = button;
                // Bake and Run are the primary action (cobalt). Cancel is a quiet outline in the tab's own ink.
                var primary = button != FooterButton.Cancel;
                using (var path = Rounded(_button, 3))
                {
                    if (primary)
                    {
                        using var brush = new SolidBrush(Fade(_pressed ? Darken(Cobalt) : Cobalt, alpha));
                        graphics.FillPath(brush, path);
                    }
                    else
                    {
                        using var brush = new SolidBrush(Fade(Color.FromArgb(_pressed ? 90 : 40, foreground), alpha));
                        using var pen = new Pen(Fade(Color.FromArgb(150, foreground), alpha), 1);
                        graphics.FillPath(brush, path);
                        graphics.DrawPath(pen, path);
                    }
                }
                using var text = new SolidBrush(Fade(primary ? Color.White : foreground, alpha));
                graphics.DrawString(label, font, text, _button, GH_TextRenderingConstants.CenterCenter);
                textArea = RectangleF.FromLTRB(tab.Left + 2, tab.Top, _button.Left - 2, tab.Bottom);
            }
        }
        if (string.IsNullOrWhiteSpace(message)) return;
        // Shrink long messages to fit, as GH does, rather than letting them run under the button.
        var measured = graphics.MeasureString(message, standard);
        var ratio = (textArea.Width - 4) / measured.Width;
        using var shrunk = ratio < 1 ? new Font(standard.FontFamily, Math.Max(standard.Size * .6f, standard.Size * ratio), standard.Style, standard.Unit) : null;
        using var ink = new SolidBrush(Fade(foreground, alpha));
        graphics.DrawString(message, shrunk ?? standard, ink, textArea, GH_TextRenderingConstants.CenterCenter);
    }

    private static Color Fade(Color colour, int alpha) => Color.FromArgb(colour.A * alpha / 255, colour);

    private void RenderSettingsDot(Graphics graphics, GH_Component owner)
    {
        if (owner.Params.Input.Count < 4 || GH_Canvas.ZoomFadeLow < 5) return;
        var param = owner.Params.Input[3].Attributes.Bounds;
        var label = GH_FontServer.MeasureString(owner.Params.Input[3].NickName, GH_FontServer.StandardAdjusted);
        var centre = new PointF(param.Right - label.Width - 4, param.Top + param.Height / 2);
        using var brush = new SolidBrush(Color.FromArgb(GH_Canvas.ZoomFadeLow, Cobalt));
        graphics.FillEllipse(brush, centre.X - 2.5f, centre.Y - 2.5f, 5, 5);
    }

    private static GraphicsPath TabPath(RectangleF box)
    {
        var path = new GraphicsPath();
        path.AddArc(box.Left - 3, box.Top, 6, 6, 270, 90);
        path.AddArc(box.Left + 3, box.Bottom - 6, 6, 6, 180, -90);
        path.AddArc(box.Right - 9, box.Bottom - 6, 6, 6, 90, -90);
        path.AddArc(box.Right - 3, box.Top, 6, 6, 180, 90);
        path.CloseAllFigures();
        return path;
    }

    private static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var d = radius * 2; var path = new GraphicsPath();
        path.AddArc(r.Left, r.Top, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static Color Darken(Color c) => Color.FromArgb(c.A, (int)(c.R * .8), (int)(c.G * .8), (int)(c.B * .8));

    // The tab sits outside GH's layout bounds; make it pickable so clicks reach this component.
    public override bool IsPickRegion(PointF point) => base.IsPickRegion(point) || _tab.Contains(point);

    public override GH_ObjectResponse RespondToMouseDown(GH_Canvas sender, GH_CanvasMouseEvent e)
    {
        if (e.Button == MouseButtons.Left && _shown != FooterButton.None && _button.Contains(e.CanvasLocation))
        {
            _pressed = true; sender.Invalidate();
            return GH_ObjectResponse.Capture;
        }
        return base.RespondToMouseDown(sender, e);
    }

    public override GH_ObjectResponse RespondToMouseUp(GH_Canvas sender, GH_CanvasMouseEvent e)
    {
        if (!_pressed) return base.RespondToMouseUp(sender, e);
        _pressed = false; sender.Invalidate();
        if (e.Button == MouseButtons.Left && _button.Contains(e.CanvasLocation))
            Ui.Press(_shown, (Control.ModifierKeys & Keys.Shift) != 0);
        return GH_ObjectResponse.Release;
    }

    public override GH_ObjectResponse RespondToMouseDoubleClick(GH_Canvas sender, GH_CanvasMouseEvent e)
    {
        if (e.Button == MouseButtons.Left && !_button.Contains(e.CanvasLocation) && (Bounds.Contains(e.CanvasLocation) || _tab.Contains(e.CanvasLocation)))
        {
            Ui.OpenEditor();
            return GH_ObjectResponse.Handled;
        }
        return base.RespondToMouseDoubleClick(sender, e);
    }

    public override void SetupTooltip(PointF point, GH_TooltipDisplayEventArgs e)
    {
        if (_shown != FooterButton.None && _button.Contains(point))
        {
            e.Title = "M2D+ " + RuntimeModel.Label(_shown);
            e.Text = RuntimeModel.Tooltip(_shown);
            e.Description = "";
            return;
        }
        base.SetupTooltip(point, e);
    }

    private void StartTimer()
    {
        if (_timer is null)
        {
            _timer = new System.Windows.Forms.Timer { Interval = (int)RepaintMilliseconds };
            _timer.Tick += Tick;
        }
        if (!_timer.Enabled) _timer.Start();
    }

    private void Tick(object? sender, EventArgs e)
    {
        if (Owner.OnPingDocument() is null || _canvas is null || _canvas.IsDisposed) { StopTimer(); return; }
        var runtime = Ui.CurrentRuntime;
        // Repaint for an indeterminate sweep, a moved fraction, or once more to clear the line when the job ends.
        if (runtime.Phase != DrawingPhase.Running) StopTimer();
        if (runtime == _painted && runtime.Fraction is not null) return;
        var visible = Bounds; visible.Height += TabHeight + 2;
        if (_canvas.Document == Owner.OnPingDocument() && _canvas.Viewport.IsVisible(ref visible, 10)) _canvas.Invalidate();
    }

    private void StopTimer()
    {
        if (_timer is null) return;
        _timer.Stop(); _timer.Tick -= Tick; _timer.Dispose(); _timer = null;
    }
}
