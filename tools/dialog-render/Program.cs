using System.IO;
using System.Runtime.InteropServices;
using Eto.Forms;
using M2DPlus.Core.Views;
using M2DPlus.Grasshopper.UI;
using Application = Eto.Forms.Application;
using Window = Eto.Forms.Window;
using UITimer = Eto.Forms.UITimer;

namespace M2DPlus.DialogRender;

/// <summary>
/// Shows the real "View box depth" dialog (src/M2DPlus.Grasshopper/UI/DepthDialog.cs) with Eto's WinForms platform (Rhino 8
/// uses Eto.Wpf; WinForms is captured reliably anywhere) for a few selections, and writes each state to a PNG. The dialogs close themselves. Rhino's
/// own theme colours (UseRhinoStyle) need a running Rhino and are not applied here.
/// The dialogs open off-screen and close themselves. Child controls are drawn by Windows' themed renderers at the bounds
/// Eto gave them (some sessions cannot rasterise WPF, read the screen or print windows), so native chrome is approximate.
/// Usage: dotnet run --project tools/dialog-render -- [output folder, default artifacts/ui]
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var folder = Path.GetFullPath(args.Length > 0 ? args[0] : Path.Combine("artifacts", "ui"));
        Directory.CreateDirectory(folder);
        using var app = new Application(new Eto.WinForms.Platform());
        var cases = new (string Name, ViewDepthPrompt Model, string? Typed)[]
        {
            ("depth-dialog-plan-mm", new([new AutoDepth(3000, ViewBoxKind.Plan, AutoDepthRule.Storey, 120)], .001, .001), null),
            ("depth-dialog-mixed", new([new AutoDepth(3000, ViewBoxKind.Plan, AutoDepthRule.Storey, 120), new AutoDepth(3000, ViewBoxKind.Plan, AutoDepthRule.Storey, 98),
                new AutoDepth(24_350, ViewBoxKind.Section, AutoDepthRule.Back, 310), new AutoDepth(31_200, ViewBoxKind.Elevation, AutoDepthRule.Back, 412)], .001, .001), null),
            ("depth-dialog-section-m", new([new AutoDepth(27.37, ViewBoxKind.Section, AutoDepthRule.Back, 2)], 1, .001), "8 m"),
            ("depth-dialog-invalid", new([new AutoDepth(3000, ViewBoxKind.Plan, AutoDepthRule.Storey, 120)], .001, .001), "8 parsecs"),
        };
        foreach (var (name, model, typed) in cases)
        {
            var dialog = new DepthDialog(model);
            OffScreen(dialog);
            var step = 0;
            var timer = new UITimer { Interval = .5 };
            timer.Elapsed += (_, _) =>
            {
                if (step++ == 0)
                {
                    if (typed is not null) { dialog.DepthText = typed; if (typed.Contains("parsecs")) dialog.Accept(); }
                    return; // let WPF lay out and render the change
                }
                timer.Stop();
                Capture(dialog, Path.Combine(folder, name + ".png"));
                dialog.Close(null);
            };
            dialog.Shown += (_, _) => timer.Start();
            dialog.ShowModal();
            timer.Dispose();
        }
        return 0;
    }

    /// <summary>The dialog as laid out by Eto: the frame and background from the form's DrawToBitmap, then every control
    /// drawn at its real bounds with its real text, font and colour by Windows' themed renderers (child controls do not
    /// paint through WM_PRINT here).</summary>
    private static void Capture(Window window, string file)
    {
        var form = (System.Windows.Forms.Form)window.ControlObject;
        using var image = new System.Drawing.Bitmap(form.Width, form.Height, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        form.DrawToBitmap(image, new System.Drawing.Rectangle(0, 0, form.Width, form.Height));
        using (var g = System.Drawing.Graphics.FromImage(image))
        {
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            Draw(form, form, g);
        }
        image.Save(file, System.Drawing.Imaging.ImageFormat.Png);
        Console.WriteLine($"{file}: {image.Width} x {image.Height} px");
    }

    private static void Draw(System.Windows.Forms.Form form, System.Windows.Forms.Control parent, System.Drawing.Graphics g)
    {
        foreach (System.Windows.Forms.Control control in parent.Controls)
        {
            if (!control.Visible || control.Width <= 0 || control.Height <= 0) continue;
            var at = control.PointToScreen(System.Drawing.Point.Empty);
            var bounds = new System.Drawing.Rectangle(at.X - form.Left, at.Y - form.Top, control.Width, control.Height);
            switch (control)
            {
                case System.Windows.Forms.TextBoxBase box:
                    System.Windows.Forms.TextBoxRenderer.DrawTextBox(g, bounds, " " + box.Text, box.Font,
                        System.Windows.Forms.TextFormatFlags.VerticalCenter | System.Windows.Forms.TextFormatFlags.Left,
                        System.Windows.Forms.VisualStyles.TextBoxState.Hot);
                    continue;
                case System.Windows.Forms.ButtonBase button:
                    System.Windows.Forms.ButtonRenderer.DrawButton(g, bounds, button.Text, button.Font, false,
                        form.AcceptButton == button ? System.Windows.Forms.VisualStyles.PushButtonState.Default : System.Windows.Forms.VisualStyles.PushButtonState.Normal);
                    continue;
                case System.Windows.Forms.Label or System.Windows.Forms.Control when control.GetType().Name.Contains("Label"):
                    System.Windows.Forms.TextRenderer.DrawText(g, control.Text, control.Font, bounds, control.ForeColor,
                        System.Windows.Forms.TextFormatFlags.VerticalCenter | System.Windows.Forms.TextFormatFlags.Left | System.Windows.Forms.TextFormatFlags.NoPadding);
                    continue;
            }
            Draw(form, control, g);
        }
    }

    /// <summary>Keeps the dialog off every screen: it opens far outside the desktop and closes itself.</summary>
    private static void OffScreen(DepthDialog dialog)
    {
        var form = (System.Windows.Forms.Form)dialog.ControlObject;
        form.StartPosition = System.Windows.Forms.FormStartPosition.Manual;
        form.Load += (_, _) => form.Location = new System.Drawing.Point(-20000, -20000);
    }

    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out Rect value, int size);
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
}
