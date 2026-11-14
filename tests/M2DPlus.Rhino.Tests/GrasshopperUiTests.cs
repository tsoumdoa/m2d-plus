using System.Drawing;
using System.Drawing.Imaging;
using System.Linq.Expressions;
using System.Reflection;
using System.Windows.Forms;
using GH_IO.Serialization;
using Grasshopper.GUI.Canvas;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Parameters;
using Grasshopper.Kernel.Special;
using M2DPlus.Core;
using Rhino;
using Xunit;

namespace M2DPlus.Rhino.Tests;

/// <summary>GH UI layer in a real (headless) Grasshopper: attributes, menus, undo, .gh round trip and rendering.
/// Set M2D_UI_SCREENSHOTS to a folder to also write PNGs of the canvas, menu and editor.</summary>
[Collection("Rhino")]
[Trait("Category", "RhinoIntegration")]
public sealed class GrasshopperUiTests(RhinoHost host)
{
    private static readonly Guid GrasshopperId = new("b45a29b1-4343-4035-989e-044e8580d9cf");
    private static Assembly Gha => Assembly.LoadFrom(Path.Combine(AppContext.BaseDirectory, "M2DPlus.Grasshopper.gha"));
    private static Type UiType(string name) => Gha.GetType("M2DPlus.Grasshopper.UI." + name, true)!;
    private static string? ScreenshotFolder => Environment.GetEnvironmentVariable("M2D_UI_SCREENSHOTS");

    private static GH_Component NewComponent(GH_Document document, float x, float y)
    {
        var component = (GH_Component)Activator.CreateInstance(Gha.GetType("M2DPlus.Grasshopper.DrawingComponent", true)!)!;
        component.CreateAttributes();
        component.Attributes.Pivot = new PointF(x, y);
        document.AddObject(component, false);
        return component;
    }

    private static string StateText(GH_Component component) => (string)((dynamic)component).Ui.State.ToString();
    private static string[] ViewTexts(GH_Component component) => ((IEnumerable<string>)((dynamic)component.Params.Input[1]).CurrentTexts).ToArray();

    [RhinoFact] public void AttributesMenusUndoAndSavedGhRoundTrip() => host.Run(() =>
    {
        Assert.True(global::Rhino.PlugIns.PlugIn.LoadPlugIn(GrasshopperId));
        using var rhino = RhinoDoc.CreateHeadless(null);
        RhinoDoc.ActiveDoc = rhino;
        using var document = new GH_Document(); // never enabled: no job runs, only the UI layer is exercised
        var component = NewComponent(document, 200, 100);
        Assert.Equal("DrawingAttributes", component.Attributes.GetType().Name);
        Assert.Equal("ViewParam", component.Params.Input[1].GetType().Name);
        Assert.IsAssignableFrom<Param_GenericObject>(component.Params.Input[1]);
        Assert.Equal((4, 9), (component.Params.Input.Count, component.Params.Output.Count));
        dynamic ui = ((dynamic)component).Ui;

        // View input menu: multi-check text values, case-insensitive toggle.
        dynamic view = component.Params.Input[1];
        view.Execute("view:Top", rhino); view.Execute("view:SW", rhino); view.Execute("view:top", rhino);
        Assert.Equal(["SW"], ViewTexts(component));
        using (var viewMenu = new ContextMenuStrip())
        {
            view.AppendAdditionalMenuItems(viewMenu);
            var directions = viewMenu.Items.OfType<ToolStripMenuItem>().Single(i => i.Text == "Directions");
            Assert.True(directions.DropDownItems.OfType<ToolStripMenuItem>().Single(i => i.Text == "SW").Checked);
        }

        // Component menu toggles edit the All views row; the editor rows and unknown keys persist.
        ui.Execute("set:hidden=off", false);
        ui.Execute("set:farclip=line", false);
        ui.Commit(ui.State.With("text:sw", "depth", "40 m").With("*", "future.key", "kept"), "test", false);
        using (var menu = new ContextMenuStrip())
        {
            ui.AppendMenuItems(menu);
            var hidden = menu.Items.OfType<ToolStripMenuItem>().Single(i => i.Text == "Hidden lines");
            Assert.False(hidden.Checked);
            var far = menu.Items.OfType<ToolStripMenuItem>().Single(i => i.Text == "Far clip");
            Assert.True(far.DropDownItems.OfType<ToolStripMenuItem>().Single(i => i.Text == "Clip with line").Checked);
            SaveMenu(menu, "menu.png");
        }

        // One GH undo record per change, restored through Write/Read.
        var before = StateText(component);
        ui.Execute("set:seams=on", false);
        Assert.NotEqual(before, StateText(component));
        document.UndoServer.PerformUndo();
        Assert.Equal(before, StateText(component));

        // Run automatically off: the start gate refuses until Run now.
        ui.Execute("autorun", false);
        Assert.False((bool)ui.ShouldStart());
        Assert.True((bool)ui.Paused);
        ui.Execute("run", false);
        Assert.True((bool)ui.ShouldStart());

        // Save to a real .gh and reopen into a new document.
        var expected = StateText(component);
        Assert.Contains("future.key", expected);
        var path = Path.Combine(Path.GetTempPath(), $"m2d-ui-{Guid.NewGuid():N}.gh");
        try
        {
            var archive = new GH_Archive();
            Assert.True(archive.AppendObject(document, "Definition"));
            Assert.True(archive.WriteToFile(path, true, false));
            var read = new GH_Archive();
            Assert.True(read.ReadFromFile(path));
            // The test loads the GHA directly, so GH's component server can't instantiate it by GUID;
            // read the saved object chunk into a fresh component exactly as GH does after instantiation.
            var objects = read.GetRootNode.FindChunk("Definition").FindChunk("DefinitionObjects");
            var saved = Enumerable.Range(0, objects.GetInt32("ObjectCount")).Select(i => objects.FindChunk("Object", i))
                .Single(o => o.GetGuid("GUID") == component.ComponentGuid);
            using var reopened = new GH_Document();
            var again = NewComponent(reopened, 0, 0);
            Assert.True(again.Read(saved.FindChunk("Container")));
            Assert.Equal(expected, StateText(again));
            Assert.Equal(["SW"], ViewTexts(again));
            Assert.Equal("ViewParam", again.Params.Input[1].GetType().Name);
            Assert.Null(((dynamic)again).Ui.ReadProblem);
        }
        finally { File.Delete(path); }
    });

    [RhinoFact] public void CanvasAndEditorRender() => host.Run(() =>
    {
        Assert.True(global::Rhino.PlugIns.PlugIn.LoadPlugIn(GrasshopperId));
        using var rhino = RhinoDoc.CreateHeadless(null);
        RhinoDoc.ActiveDoc = rhino;
        using var document = new GH_Document();
        // Only the UI is rendered: keep wiring from starting solutions (and their collection warnings).
        GH_Document.EnableSolutions = false;
        using var restore = new Restore(() => GH_Document.EnableSolutions = true);
        var states = new (string Caption, string Message, string Phase, double? Fraction, bool Values)[]
        {
            ("Done · Bake", "Section A · 2.1 s", "Done", null, true),
            ("Drawing · Cancel", "Drawing 3/6 · 72 %", "Running", .72, true),
            ("Reading (indeterminate)", "Reading 50,210 objects", "Running", null, false),
            ("Paused · Run", "Paused · out of date", "Paused", null, false),
            ("Cancelled · Run", "Cancelled", "Cancelled", null, false),
            ("No view", "Pick a view", "Idle", null, false),
        };
        var components = new List<GH_Component>();
        for (var i = 0; i < states.Length; i++)
        {
            var (caption, message, phase, fraction, values) = states[i];
            var x = 140 + i % 3 * 230f; var y = 90 + i / 3 * 260f;
            var component = NewComponent(document, x, y);
            dynamic ui = ((dynamic)component).Ui;
            SetProperty(ui, "Runtime", Constant(UiType("Model.DrawingRuntime"),
                Activator.CreateInstance(UiType("Model.DrawingRuntime"), Enum.Parse(UiType("Model.DrawingPhase"), phase), fraction)!));
            if (values) ui.Commit(ui.State.With("*", "scale", "1:200"), "test", false);
            if (phase != "Idle")
            {
                // A real view, with Run automatically off so the render never starts a job.
                ui.Execute("autorun", false);
                ((dynamic)component.Params.Input[1]).SetTexts(new[] { "Section A" }, "test");
            }
            var scribble = new GH_Scribble { Text = caption, Font = new Font("Segoe UI", 11, FontStyle.Bold) };
            scribble.CreateAttributes();
            scribble.Attributes.Pivot = new PointF(x - 70, y - 110);
            document.AddObject(scribble, false);
            components.Add(component);
        }
        var panel = new GH_Panel { UserText = "hidden = off" };
        panel.CreateAttributes();
        panel.Attributes.Pivot = new PointF(-200, 110);
        document.AddObject(panel, false);
        components[0].Params.Input[3].AddSource(panel);
        // The canvas solves the definition once when attached. V is required, so GH warns "failed to collect data"
        // and the job sets its own message. Keep that warning on the last ("No view") example only and show each
        // example's state text.
        void Settle()
        {
            for (var i = 0; i < states.Length; i++)
            {
                if (i < states.Length - 1) components[i].ClearRuntimeMessages();
                components[i].Message = states[i].Message;
            }
        }
        RenderCanvas(document, "canvas.png", Settle);
        var saved = DarkSkin();
        try { RenderCanvas(document, "canvas-dark.png", Settle); }
        finally { saved(); }

        // The editor, with realistic contexts and a wired Settings input that locks Hidden lines.
        dynamic editorUi = ((dynamic)components[0]).Ui;
        var views = new[]
        {
            new ViewSettingsContext("text:section a", "Section A", ViewSourceKind.Rectangle, true, 52, .001),
            new ViewSettingsContext("text:section b", "Section B", ViewSourceKind.Rectangle, true, 52, .001),
            new ViewSettingsContext("text:south elevation", "South elevation", ViewSourceKind.Rectangle, false, 60, .001),
        };
        var context = Activator.CreateInstance(UiType("Model.UiContext"), views, new[] { new[] { "hidden = off" } }, DocumentUnits.Metres)!;
        SetProperty(editorUi, "Context", Constant(UiType("Model.UiContext"), context));
        editorUi.Commit(editorUi.State.ClearRow("*").With("*", "scale", "1:100")
            .With("text:section a", "depth", "40 m").With("text:section a", "farclip", "line").With("text:section a", "fade", "on")
            .With("text:section a", "fade.start", "10 %").With("text:section a", "fade.end", "90 %").With("text:section a", "fade.limit", "25 %")
            .With("text:south elevation", "depth", "60 m").With("text:south elevation", "beyond", "off")
            .With("text:south elevation", "fade", "on").With("text:south elevation", "scale", "1:200"), "test", false);
        dynamic form = editorUi.CreateEditor();
        try
        {
            form.Location = new Eto.Drawing.Point(-20000, -20000);
            form.Show();
            form.SelectRow("text:section a");
            Pump();
            Assert.Equal("text:section a", (string)form.Session.Selected);
            Assert.True((bool)form.Session.Field("hidden").Locked);
            AssertLaidOut((System.Windows.Window)form.ControlObject);
            form.SelectRow("*");
            Pump();
            Assert.Equal("Reset to defaults", (string)form.Session.ResetLabel);
        }
        finally { form.Close(); Pump(); }
        Assert.Null(editorUi.Editor);
    });

    private sealed class Restore(Action action) : IDisposable { public void Dispose() => action(); }

    private static Delegate Constant(Type type, object value) =>
        Expression.Lambda(typeof(Func<>).MakeGenericType(type), Expression.Constant(value, type)).Compile();
    private static void SetProperty(object target, string name, object value) => target.GetType().GetProperty(name)!.SetValue(target, value);

    private static void Pump()
    {
        for (var i = 0; i < 20; i++)
        {
            RhinoApp.Wait();
            System.Windows.Forms.Application.DoEvents();
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Thread.Sleep(10);
        }
    }

    private static void RenderCanvas(GH_Document document, string file, Action settle)
    {
        using var canvas = new GH_Canvas();
        _ = canvas.Handle;
        canvas.Document = document;
        foreach (var o in document.Objects) { o.Attributes.ExpireLayout(); o.Attributes.PerformLayout(); }
        var bounds = document.Objects.Select(o => o.Attributes.Bounds).Aggregate(RectangleF.Union);
        bounds = RectangleF.FromLTRB(bounds.Left - 30, bounds.Top - 30, bounds.Right + 30, bounds.Bottom + 50);
        const float zoom = 2;
        var viewport = new GH_Viewport(canvas.Viewport)
        {
            Width = (int)(bounds.Width * zoom), Height = (int)(bounds.Height * zoom), Zoom = zoom,
            Tx = (int)(-bounds.X * zoom), Ty = (int)(-bounds.Y * zoom)
        };
        canvas.GenerateHiResImageTile(viewport, GH_Skin.canvas_back).Dispose(); // lets the attach-time solve finish
        settle();
        using var image = canvas.GenerateHiResImageTile(viewport, GH_Skin.canvas_back);
        Assert.True(image.Width > 100 && image.Height > 100);
        if (ScreenshotFolder is { } folder) { Directory.CreateDirectory(folder); image.Save(Path.Combine(folder, file), ImageFormat.Png); }
        canvas.Document = null;
    }

    /// <summary>A dark GH skin (as many users configure); returns an action that restores the original.</summary>
    private static Action DarkSkin()
    {
        var back = GH_Skin.canvas_back; var grid = GH_Skin.canvas_grid; var edge = GH_Skin.canvas_edge; var shade = GH_Skin.canvas_shade;
        var normal = GH_Skin.palette_normal_standard; var normalSelected = GH_Skin.palette_normal_selected;
        var white = GH_Skin.palette_white_standard; var wire = GH_Skin.wire_default;
        GH_Skin.canvas_back = Color.FromArgb(38, 40, 43); GH_Skin.canvas_grid = Color.FromArgb(60, 64, 68, 72);
        GH_Skin.canvas_edge = Color.Black; GH_Skin.canvas_shade = Color.FromArgb(80, 0, 0, 0);
        GH_Skin.palette_normal_standard = new GH_PaletteStyle(Color.FromArgb(78, 81, 86), Color.FromArgb(18, 19, 21), Color.FromArgb(232, 232, 232));
        GH_Skin.palette_normal_selected = new GH_PaletteStyle(Color.FromArgb(70, 120, 70), Color.FromArgb(18, 19, 21), Color.FromArgb(240, 240, 240));
        GH_Skin.palette_white_standard = new GH_PaletteStyle(Color.FromArgb(70, 72, 76), Color.FromArgb(18, 19, 21), Color.FromArgb(232, 232, 232));
        GH_Skin.wire_default = Color.FromArgb(150, 200, 200, 200);
        return () =>
        {
            GH_Skin.canvas_back = back; GH_Skin.canvas_grid = grid; GH_Skin.canvas_edge = edge; GH_Skin.canvas_shade = shade;
            GH_Skin.palette_normal_standard = normal; GH_Skin.palette_normal_selected = normalSelected;
            GH_Skin.palette_white_standard = white; GH_Skin.wire_default = wire;
        };
    }

    private static void SaveMenu(ContextMenuStrip menu, string file)
    {
        if (ScreenshotFolder is not { } folder) return;
        menu.Show(new Point(-20000, -20000));
        try
        {
            System.Windows.Forms.Application.DoEvents();
            using var image = new Bitmap(menu.Width, menu.Height);
            menu.DrawToBitmap(image, new Rectangle(0, 0, menu.Width, menu.Height));
            Directory.CreateDirectory(folder);
            image.Save(Path.Combine(folder, file), ImageFormat.Png);
        }
        finally { menu.Close(); }
    }

    /// <summary>The editor opened inside Rhino on Rhino's Eto.Wpf platform and laid out. Pixels are captured by
    /// tools/EditorPreview instead: WPF composes nothing in a windowless or disconnected session.</summary>
    private static void AssertLaidOut(System.Windows.Window window)
    {
        window.UpdateLayout();
        Assert.True(window.IsLoaded && window.ActualWidth > 600 && window.ActualHeight > 500, $"Editor window measured {window.ActualWidth}×{window.ActualHeight}");
        Assert.NotNull(window.Background); // Rhino's own theme (UseRhinoStyle) was applied
    }
}
