using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Grasshopper.GUI;
using Grasshopper.GUI.Canvas;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Attributes;
using M2DPlus.Grasshopper.UI.Model;

namespace M2DPlus.Grasshopper.UI;

/// <summary>
/// The M2D+ Drawing component's look, layout B (owner choice 2026-11-16, UX §1). One row grid (<see cref="Row"/>) and one
/// margin (<see cref="Margin"/>) for everything:
/// <list type="bullet">
/// <item>Outputs on the rows; the five inputs at GH's even spacing snapped to the grid (rows 1, 3, 5, 7, 9 beside the
/// eleven outputs), so every input label shares a row with an output. Labels and state tags are GH's own.</item>
/// <item>A slim name strip in the centre by default. An explicit right-click <i>Display ▸ Name</i> or <i>Icon</i> works
/// the GH way (the full nickname capsule, or the icon).</item>
/// <item>A full-width footer inside the body: the status left-aligned, the Bake / Cancel / Run button flush right with
/// equal inset on all sides and a fixed width across states, and the progress bar along the footer's top edge.</item>
/// </list>
/// Double-click selects the referenced view objects in Rhino. Text is measured with GH's zoom-independent metrics, so
/// sizes match at every canvas zoom. Colours come from GH's palette except cobalt.
/// </summary>
public sealed class DrawingAttributes : GH_ComponentAttributes
{
    public static readonly Color Cobalt = Color.FromArgb(0x1F, 0x6F, 0xD1);
    /// <summary>Canvas units (the canvas zoom scales them): the row pitch, the one margin, the footer height (the button is
    /// <c>Footer − 2 × Margin</c> tall) and the slim strip's width. The capsule's corner radius matches GH's.</summary>
    public const float Row = 20, Margin = 4, Footer = 24, Strip = 14, NameColumn = 24, Radius = 6, Progress = 2;
    private const float RepaintMilliseconds = 100; // ≤ 10 Hz
    /// <summary>The footer is laid out for this status, so the width never jumps while a job reports progress; a longer
    /// message shrinks to 85 %, then ends in an ellipsis.</summary>
    public const string ReferenceStatus = "88 drawings · 88.8 s";
    private readonly Func<DrawingUi> _ui;
    private RectangleF _footer, _button, _centre;
    private FooterButton _shown;
    private bool _pressed;
    private GH_Canvas? _canvas;
    private System.Windows.Forms.Timer? _timer;
    private DrawingRuntime _painted;

    public DrawingAttributes(GH_Component owner, Func<DrawingUi> ui) : base(owner) => _ui = ui;

    private DrawingUi Ui => _ui();
    /// <summary>For tests: the last painted button and its canvas rectangle.</summary>
    public (FooterButton Button, RectangleF Bounds) LastButton => (_shown, _button);
    /// <summary>For tests: the footer (full body width, at the bottom), the button slot and the centre strip / name / icon box.</summary>
    public RectangleF FooterBounds => _footer;
    public RectangleF ButtonBounds => _button;
    public RectangleF CentreBounds => _centre;

    private static readonly Font LabelFont = GH_FontServer.StandardAdjusted;
    private static readonly Font BoldFont = new(LabelFont.FontFamily, LabelFont.Size, FontStyle.Bold, LabelFont.Unit);
    private static readonly Font StripFont = new(LabelFont.FontFamily, LabelFont.Size * .85f, FontStyle.Bold, LabelFont.Unit);
    /// <summary>GH's zoom-independent metrics plus GDI+'s own text padding (a sixth of the em each side), so DrawString
    /// never trims what the layout measured.</summary>
    private static float TextWidth(string text, Font font) => GH_FontServer.MeasureString(text, font).Width + font.GetHeight() / 3;
    /// <summary>One width for Bake, Cancel and Run, so the button never changes size between states.</summary>
    public static float ButtonWidth => new[] { FooterButton.Bake, FooterButton.Cancel, FooterButton.Run }
        .Max(b => TextWidth(RuntimeModel.Label(b), BoldFont)) + 4 * Margin;

    /// <summary>The centre column: the slim strip by default (GH's "application" display), the nickname capsule or the
    /// icon when the user chose Display ▸ Name or Icon.</summary>
    private float CentreWidth => Owner.IconDisplayMode == GH_IconDisplayMode.application ? Strip : NameColumn;

    /// <summary>Input rows: GH's even spacing snapped to the row grid, as a whole-row pitch centred on the outputs.</summary>
    public static int[] InputRows(int inputs, int outputs)
    {
        if (inputs <= 0) return [];
        var rows = Math.Max(inputs, outputs);
        var pitch = Math.Max(1, (int)Math.Round((double)rows / inputs));
        while (pitch > 1 && (inputs - 1) * pitch >= rows) pitch--;
        var first = (rows - 1 - (inputs - 1) * pitch) / 2;
        return Enumerable.Range(0, inputs).Select(i => first + i * pitch).ToArray();
    }

    protected override void Layout()
    {
        Pivot = GH_Convert.ToPoint(Pivot);
        var inputs = Owner.Params.Input; var outputs = Owner.Params.Output;
        var rows = Math.Max(1, Math.Max(inputs.Count, outputs.Count));
        var height = rows * Row;
        var inColumn = (inputs.Count == 0 ? 0 : inputs.Max(p => GH_FontServer.StringWidth(p.NickName, LabelFont))) + 2 * Margin;
        var outColumn = (outputs.Count == 0 ? 0 : outputs.Max(p => GH_FontServer.StringWidth(p.NickName, LabelFont))) + 2 * Margin;
        var centre = CentreWidth + 2 * Margin;
        var content = inColumn + centre + outColumn;
        var footerWidth = 2 * Margin + TextWidth(ReferenceStatus, LabelFont) + Margin + ButtonWidth + Margin;
        var width = (float)Math.Ceiling(Math.Max(content, footerWidth));
        var extra = (width - content) / 2;
        inColumn += extra; outColumn += extra;
        // GH's convention: the pivot is the centre of the name/icon box, so saved definitions keep their place.
        var left = Pivot.X - inColumn - centre / 2;
        var top = Pivot.Y - height / 2;
        _centre = new RectangleF(left + inColumn + Margin, top + Margin, CentreWidth, height - 2 * Margin);
        m_innerBounds = new RectangleF(left + inColumn, top, centre, height);
        // GH's own parameter layout (it also lays out graft / flatten state tags), on boxes chosen so that the labels
        // land on the grid: inputs end at their column's inner margin, outputs start at theirs.
        var inputRows = InputRows(inputs.Count, outputs.Count);
        var pitch = inputRows.Length > 1 ? (inputRows[1] - inputRows[0]) * Row : Row;
        if (inputs.Count > 0)
            LayoutInputParams(Owner, new RectangleF(left + inColumn - Margin + 3, top + inputRows[0] * Row + Row / 2 - pitch / 2, centre, inputs.Count * pitch));
        if (outputs.Count > 0)
            LayoutOutputParams(Owner, new RectangleF(left + inColumn + centre + Margin - 3 - centre, top, centre, outputs.Count * Row));
        // Grips sit on the body's edges: stretch each parameter to its side.
        foreach (var input in inputs)
        {
            var b = input.Attributes.Bounds;
            input.Attributes.Bounds = RectangleF.FromLTRB(left, b.Top, b.Right, b.Bottom);
        }
        foreach (var output in outputs)
        {
            var b = output.Attributes.Bounds;
            output.Attributes.Bounds = RectangleF.FromLTRB(b.Left, b.Top, left + width, b.Bottom);
        }
        _footer = new RectangleF(left, top + height, width, Footer);
        _button = new RectangleF(_footer.Right - Margin - ButtonWidth, _footer.Top + Margin, ButtonWidth, Footer - 2 * Margin);
        Bounds = new RectangleF(left, top, width, height + Footer);
    }

    protected override void Render(GH_Canvas canvas, Graphics graphics, GH_CanvasChannel channel)
    {
        if (channel != GH_CanvasChannel.Objects) { base.Render(canvas, graphics, channel); return; }
        if (canvas.DrawingMode == GH_CanvasMode.Control) _canvas = canvas;
        var visible = Bounds;
        // Off-screen: nothing to draw or click; the last painted button stays as it was.
        if (!canvas.Viewport.IsVisible(ref visible, 10)) return;
        var owner = (GH_Component)Owner;
        var palette = GH_CapsuleRenderEngine.GetImpliedPalette(owner);
        if (palette == GH_Palette.Normal && !owner.IsPreviewCapable) palette = GH_Palette.Hidden;
        var style = GH_CapsuleRenderEngine.GetImpliedStyle(palette, Selected, owner.Locked, owner.Hidden);
        var runtime = Ui.CurrentRuntime;
        var smoothing = graphics.SmoothingMode;
        graphics.SmoothingMode = SmoothingMode.HighQuality;
        canvas.SetSmartTextRenderingHint();
        try
        {
            using (var capsule = GH_Capsule.CreateCapsule(Bounds, palette))
            {
                foreach (var p in owner.Params.Input) capsule.AddInputGrip(p.Attributes.InputGrip.Y);
                foreach (var p in owner.Params.Output) capsule.AddOutputGrip(p.Attributes.OutputGrip.Y);
                capsule.Render(graphics, style);
            }
            RenderCentre(graphics, owner);
            RenderComponentParameters(canvas, graphics, owner, style);
            RenderFooter(graphics, owner.Message, runtime, style);
        }
        finally { graphics.SmoothingMode = smoothing; }
        _painted = runtime;
        if (runtime.Phase == DrawingPhase.Running && canvas.DrawingMode == GH_CanvasMode.Control) StartTimer();
    }

    private void RenderCentre(Graphics graphics, GH_Component owner)
    {
        switch (owner.IconDisplayMode)
        {
            case GH_IconDisplayMode.icon:
            {
                var icon = owner.Locked ? owner.Icon_24x24_Locked : owner.Icon_24x24;
                if (icon is null) return;
                var box = new RectangleF(_centre.X + (_centre.Width - 24) / 2, _centre.Y + (_centre.Height - 24) / 2, 24, 24);
                graphics.DrawImage(icon, box);
                return;
            }
            case GH_IconDisplayMode.name:
            {
                using var capsule = GH_Capsule.CreateTextCapsule(Rectangle.Round(_centre), Rectangle.Round(_centre), GH_Palette.Black, owner.NickName,
                    GH_FontServer.LargeAdjusted, GH_Orientation.vertical_center, 3, 6);
                capsule.Render(graphics, Selected, owner.Locked, false);
                return;
            }
            default:
            {
                using var capsule = GH_Capsule.CreateTextCapsule(Rectangle.Round(_centre), Rectangle.Round(_centre), GH_Palette.Black, owner.NickName,
                    StripFont, GH_Orientation.vertical_center, 3, 0);
                capsule.Render(graphics, Selected, owner.Locked, false);
                return;
            }
        }
    }

    private void RenderFooter(Graphics graphics, string? message, DrawingRuntime runtime, GH_PaletteStyle style)
    {
        _shown = FooterButton.None;
        // The band is the capsule's own edge colour, clipped to the capsule's rounded bottom corners.
        using (var shape = Rounded(Bounds, Radius))
        {
            var clip = graphics.Clip;
            graphics.SetClip(shape, CombineMode.Intersect);
            using (var band = new SolidBrush(style.Edge)) graphics.FillRectangle(band, _footer);
            if (runtime.Phase == DrawingPhase.Running) RenderProgress(graphics, runtime);
            graphics.Clip = clip;
            using var edge = new Pen(style.Edge);
            graphics.DrawPath(edge, shape);
        }
        var alpha = GH_Canvas.ZoomFadeMedium;
        if (alpha < 5) return;
        var ink = GH_GraphicsUtil.ForegroundColour(style.Edge, 230);
        var button = RuntimeModel.Button(runtime);
        var statusRight = _footer.Right - Margin;
        if (button != FooterButton.None)
        {
            _shown = button;
            statusRight = _button.Left - Margin;
            // Bake and Run are the primary action (cobalt). Cancel is a quiet outline in the footer's own ink.
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
                    using var brush = new SolidBrush(Fade(Color.FromArgb(_pressed ? 80 : 36, ink), alpha));
                    using var pen = new Pen(Fade(Color.FromArgb(170, ink), alpha), 1);
                    graphics.FillPath(brush, path);
                    graphics.DrawPath(pen, path);
                }
            }
            using var label = new SolidBrush(Fade(primary ? Color.White : ink, alpha));
            graphics.DrawString(RuntimeModel.Label(button), BoldFont, label, _button, GH_TextRenderingConstants.CenterCenter);
        }
        var area = RectangleF.FromLTRB(_footer.Left + 2 * Margin, _footer.Top, statusRight, _footer.Bottom);
        if (area.Width <= 4) return;
        // While a job runs: the longest form of the status that fits (detail, then view, then estimate go first).
        if (runtime.Status is { } line) message = line.Fit(t => TextWidth(t, LabelFont), area.Width);
        if (string.IsNullOrWhiteSpace(message)) return;
        var ratio = area.Width / Math.Max(1, TextWidth(message, LabelFont));
        using var shrunk = ratio < 1 ? new Font(LabelFont.FontFamily, LabelFont.Size * Math.Max(.85f, ratio), LabelFont.Style, LabelFont.Unit) : null;
        using var format = new StringFormat(GH_TextRenderingConstants.NearCenter) { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
        using var status = new SolidBrush(Fade(Color.FromArgb(215, ink), alpha));
        graphics.DrawString(message, shrunk ?? LabelFont, status, area, format);
    }

    /// <summary>Along the footer's top edge, full width (clipped to the capsule by the caller). The bar fills to the
    /// job's fraction, which never moves back; a stage without sub-progress shimmers over the part it will fill, so the
    /// bar is never frozen. With nothing known yet, one segment sweeps the whole width.</summary>
    private void RenderProgress(Graphics graphics, DrawingRuntime runtime)
    {
        var track = new RectangleF(_footer.Left, _footer.Top, _footer.Width, Progress);
        using (var faint = new SolidBrush(Color.FromArgb(70, Cobalt))) graphics.FillRectangle(faint, track);
        using var ink = new SolidBrush(Cobalt);
        var phase = Environment.TickCount64 % 1200 / 1200f;
        if (runtime.Fraction is not double f)
        {
            var width = track.Width * .25f;
            var x = track.Left - width + (track.Width + width) * phase;
            var sweep = RectangleF.FromLTRB(Math.Max(track.Left, x), track.Top, Math.Min(track.Right, x + width), track.Bottom);
            if (sweep.Width > 0) graphics.FillRectangle(ink, sweep);
            return;
        }
        var filled = (float)(track.Width * Math.Clamp(f, 0, 1));
        if (filled > 0) graphics.FillRectangle(ink, track with { Width = filled });
        if (!runtime.Indeterminate) return;
        // The shimmer runs over the current unit of work (at least 24 units wide, so it is visible).
        var left = track.Left + filled;
        var right = Math.Min(track.Right, Math.Max(track.Left + (float)(track.Width * Math.Clamp(runtime.SpanEnd, 0, 1)), left + 24));
        if (right - left < 1) return;
        var segment = Math.Max(8, (right - left) * .4f);
        var at = left - segment + (right - left + segment) * phase;
        var glint = RectangleF.FromLTRB(Math.Max(left, at), track.Top, Math.Min(right, at + segment), track.Bottom);
        using var light = new SolidBrush(Color.FromArgb(150, Cobalt));
        if (glint.Width > 0) graphics.FillRectangle(light, glint);
    }

    private static Color Fade(Color colour, int alpha) => Color.FromArgb(colour.A * alpha / 255, colour);

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
        // Double-click selects the referenced view objects in Rhino (their m2d.* user text is then in Properties).
        // With none, GH's default double-click behaviour is kept.
        if (e.Button == MouseButtons.Left && !_button.Contains(e.CanvasLocation) && Bounds.Contains(e.CanvasLocation) && Ui.SelectViewObjects())
            return GH_ObjectResponse.Handled;
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
        // Over the footer: the full status (what is being processed, how far along) and the stage timings.
        if (_footer.Contains(point) && Ui.Detail?.Invoke() is { } detail)
        {
            e.Title = Owner.Name;
            e.Text = detail;
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
        // Repaint for an indeterminate sweep, a moved fraction, or once more to clear the bar when the job ends.
        if (runtime.Phase != DrawingPhase.Running) StopTimer();
        // Repaint the canvas only when the footer changes, or to animate a shimmer: never Rhino's viewports.
        if (runtime == _painted && runtime.Fraction is not null && !runtime.Indeterminate) return;
        var visible = Bounds;
        if (_canvas.Document == Owner.OnPingDocument() && _canvas.Viewport.IsVisible(ref visible, 10)) _canvas.Invalidate();
    }

    private void StopTimer()
    {
        if (_timer is null) return;
        _timer.Stop(); _timer.Tick -= Tick; _timer.Dispose(); _timer = null;
    }
}
