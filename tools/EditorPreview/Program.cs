using System.Drawing;
using System.Drawing.Imaging;
using M2DPlus.Core;
using M2DPlus.Grasshopper.UI;
using M2DPlus.Grasshopper.UI.Model;

/// <summary>dotnet run --project tools/EditorPreview -- &lt;output folder&gt;
/// Opens the editor off-screen with the UX mockup's views and a wired "hidden = off" input, saves PNGs of the
/// "Section A" and "All views" rows, then exits. Uses Eto's WinForms (GDI) backend so it also works in remote
/// or disconnected sessions where WPF composes nothing.</summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var output = Path.GetFullPath(args.Length > 0 ? args[0] : ".");
        Directory.CreateDirectory(output);
        var app = new Eto.Forms.Application(Eto.Platforms.WinForms);
        var views = new[]
        {
            new ViewSettingsContext("text:section a", "Section A", ViewSourceKind.Rectangle, true, 52, .001),
            new ViewSettingsContext("text:section b", "Section B", ViewSourceKind.Rectangle, true, 52, .001),
            new ViewSettingsContext("text:south elevation", "South elevation", ViewSourceKind.Rectangle, false, 60, .001),
        };
        var context = new UiContext(views, [["hidden = off"]], DocumentUnits.Metres);
        var state = ComponentUiState.Default.With("*", "scale", "1:100")
            .With("text:section a", "depth", "40 m").With("text:section a", "farclip", "line").With("text:section a", "fade", "on")
            .With("text:section a", "fade.start", "10 %").With("text:section a", "fade.end", "90 %").With("text:section a", "fade.limit", "25 %")
            .With("text:south elevation", "depth", "60 m").With("text:south elevation", "beyond", "off")
            .With("text:south elevation", "fade", "on").With("text:south elevation", "scale", "1:200")
            .With("text:old plan", "depth", "3 m");
        var form = new SettingsEditorForm(new EditorSession(state, context), _ => { }) { Location = new Eto.Drawing.Point(-20000, -20000) };
        var code = 1;
        var timer = new Eto.Forms.UITimer { Interval = .4 };
        form.Shown += (_, _) => timer.Start();
        timer.Elapsed += (_, _) =>
        {
            timer.Stop();
            try
            {
                form.SelectRow("text:section a"); Capture(form, Path.Combine(output, "editor.png"));
                form.SelectRow("*"); Capture(form, Path.Combine(output, "editor-all-views.png"));
                code = 0;
            }
            catch (Exception e) { Console.Error.WriteLine(e); }
            finally { form.Close(); app.Quit(); }
        };
        app.Run(form);
        return code;
    }

    private static void Capture(Eto.Forms.Form form, string file)
    {
        var native = (System.Windows.Forms.Form)form.ControlObject;
        System.Windows.Forms.Application.DoEvents();
        using var image = new Bitmap(native.Width, native.Height);
        native.DrawToBitmap(image, new Rectangle(0, 0, native.Width, native.Height));
        image.Save(file, ImageFormat.Png);
        Console.WriteLine("Saved " + file);
    }
}
