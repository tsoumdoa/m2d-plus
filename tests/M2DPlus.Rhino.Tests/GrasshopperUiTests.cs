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
/// Set M2D_UI_SCREENSHOTS to a folder to also write PNGs of the canvas and menus.</summary>
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
        Assert.Equal((5, 10), (component.Params.Input.Count, component.Params.Output.Count));
        Assert.Equal(new Guid("c8d0f9fe-5498-45dd-8caf-10201b80d211"), component.ComponentGuid);
        Assert.Equal(["V", "H", "B", "Fc", "C", "Ha", "?", "F", "i", "Ig"], component.Params.Output.Select(p => p.NickName));
        Assert.Equal(["G", "V", "T", "S", "D"], component.Params.Input.Select(p => p.NickName));
        Assert.Equal(GH_ParamAccess.list, component.Params.Input[4].Access);
        Assert.True(component.Params.Input[4].Optional);
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

        // Component menu toggles edit the menu defaults; unknown keys persist. There is no editor any more.
        ui.Execute("set:hidden=off", false);
        ui.Execute("set:farclip=line", false);
        ui.Commit(ui.State.With("future.key", "kept"), "test", false);
        Assert.Null(component.GetType().GetMethod("OpenEditor"));
        Assert.Null(((object)ui).GetType().GetMethod("OpenEditor"));
        using (var menu = new ContextMenuStrip())
        {
            ui.AppendMenuItems(menu);
            var items = menu.Items.OfType<ToolStripMenuItem>().ToArray();
            Assert.DoesNotContain(items, i => i.Text!.Contains("settings…", StringComparison.Ordinal));
            Assert.Contains(items, i => i.Text == "Validate views");
            var hidden = items.Single(i => i.Text == "Hidden lines");
            Assert.False(hidden.Checked);
            var far = items.Single(i => i.Text == "Far clip");
            Assert.True(far.DropDownItems.OfType<ToolStripMenuItem>().Single(i => i.Text == "Clip with line").Checked);
            SaveMenu(menu, "menu.png");
        }
        using (var viewMenu = new ContextMenuStrip())
        {
            view.AppendAdditionalMenuItems(viewMenu);
            Assert.Contains(viewMenu.Items.OfType<ToolStripMenuItem>(), i => i.Text == "Pick views in Rhino…");
            SaveMenu(viewMenu, "view-menu.png");
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

            // A definition saved before the Depth input existed (4 inputs) still reads, and Depth is added empty.
            var legacy = new GH_Archive();
            using (var old = new GH_Document())
            {
                var four = NewComponent(old, 0, 0);
                four.Params.UnregisterInputParameter(four.Params.Input[4]);
                ((dynamic)four.Params.Input[1]).SetTexts(new[] { "Front" }, "test");
                Assert.True(legacy.AppendObject(old, "Definition"));
            }
            var legacyObjects = legacy.GetRootNode.FindChunk("Definition").FindChunk("DefinitionObjects");
            var legacySaved = Enumerable.Range(0, legacyObjects.GetInt32("ObjectCount")).Select(i => legacyObjects.FindChunk("Object", i))
                .Single(o => o.GetGuid("GUID") == component.ComponentGuid);
            using var upgraded = new GH_Document();
            var five = NewComponent(upgraded, 0, 0);
            Assert.True(five.Read(legacySaved.FindChunk("Container")));
            Assert.Equal(5, five.Params.Input.Count);
            Assert.Equal(["Front"], ViewTexts(five));
            Assert.Equal("Depth", five.Params.Input[4].Name);
            Assert.Equal(0, five.Params.Input[4].SourceCount);

            // A definition saved before the Ignored output existed (9 outputs) still reads: the nine keep their
            // indices and instance ids (so downstream wires still find them), and Ignored is added empty at index 9.
            var nineOutputs = new GH_Archive();
            Guid[] oldOutputIds;
            using (var old = new GH_Document())
            {
                var nine = NewComponent(old, 0, 0);
                nine.Params.UnregisterOutputParameter(nine.Params.Output[9]);
                Assert.Equal(9, nine.Params.Output.Count);
                ((dynamic)nine.Params.Input[1]).SetTexts(new[] { "Front" }, "test");
                oldOutputIds = nine.Params.Output.Select(p => p.InstanceGuid).ToArray();
                Assert.True(nineOutputs.AppendObject(old, "Definition"));
            }
            var nineObjects = nineOutputs.GetRootNode.FindChunk("Definition").FindChunk("DefinitionObjects");
            var nineSaved = Enumerable.Range(0, nineObjects.GetInt32("ObjectCount")).Select(i => nineObjects.FindChunk("Object", i))
                .Single(o => o.GetGuid("GUID") == component.ComponentGuid);
            using var upgradedOutputs = new GH_Document();
            var ten = NewComponent(upgradedOutputs, 0, 0);
            Assert.True(ten.Read(nineSaved.FindChunk("Container")));
            Assert.Equal((5, 10), (ten.Params.Input.Count, ten.Params.Output.Count));
            Assert.Equal(oldOutputIds, ten.Params.Output.Take(9).Select(p => p.InstanceGuid));
            Assert.Equal(["Visible", "Hidden", "Beyond", "Far clip", "Cut", "Hatch", "Unresolved", "Frame", "Info", "Ignored"], ten.Params.Output.Select(p => p.Name));
            Assert.Empty(ten.Params.Output[9].Recipients);
            Assert.Equal(["Front"], ViewTexts(ten));
        }
        finally { File.Delete(path); }
    });

    [RhinoFact] public void CanvasRender() => host.Run(() =>
    {
        Assert.True(global::Rhino.PlugIns.PlugIn.LoadPlugIn(GrasshopperId));
        using var rhino = RhinoDoc.CreateHeadless(null);
        RhinoDoc.ActiveDoc = rhino;
        using var document = new GH_Document();
        // Only the UI is rendered: keep wiring from starting solutions (and their collection warnings).
        GH_Document.EnableSolutions = false;
        using var restore = new Restore(() => GH_Document.EnableSolutions = true);
        var states = new (string Caption, string Message, string Phase, double? Fraction)[]
        {
            ("Done · Bake", "Section A · 2.1 s", "Done", null),
            ("Drawing · Cancel", "Drawing 3/6 · 72 %", "Running", .72),
            ("Reading (indeterminate)", "Reading 50,210 objects", "Running", null),
            ("Paused · Run", "Paused · out of date", "Paused", null),
            ("Cancelled · Run", "Cancelled", "Cancelled", null),
            ("No view", "Pick a view", "Idle", null),
        };
        var components = new List<GH_Component>();
        for (var i = 0; i < states.Length; i++)
        {
            var (caption, message, phase, fraction) = states[i];
            var x = 140 + i % 3 * 230f; var y = 90 + i / 3 * 270f;
            var component = NewComponent(document, x, y);
            Assert.Equal(5, component.Params.Input.Count);
            dynamic ui = ((dynamic)component).Ui;
            SetProperty(ui, "Runtime", Constant(UiType("Model.DrawingRuntime"),
                Activator.CreateInstance(UiType("Model.DrawingRuntime"), Enum.Parse(UiType("Model.DrawingPhase"), phase), fraction)!));
            if (phase != "Idle")
            {
                // A real view, with Run automatically off so the render never starts a job.
                ui.Execute("autorun", false);
                ((dynamic)component.Params.Input[1]).SetTexts(new[] { "Section A" }, "test");
            }
            var scribble = new GH_Scribble { Text = caption, Font = new Font("Segoe UI", 11, FontStyle.Bold) };
            scribble.CreateAttributes();
            scribble.Attributes.Pivot = new PointF(x - 70, y - 115);
            document.AddObject(scribble, false);
            components.Add(component);
        }
        GH_Panel Panel(string text, float y)
        {
            var panel = new GH_Panel { UserText = text };
            panel.CreateAttributes();
            panel.Attributes.Bounds = new RectangleF(-40, y, 110, 22);
            panel.Attributes.Pivot = new PointF(-40, y);
            document.AddObject(panel, false);
            return panel;
        }
        components[0].Params.Input[3].AddSource(Panel("hidden = off", 155));
        components[0].Params.Input[4].AddSource(Panel("8 m", 195));
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
    });

    private sealed class Restore(Action action) : IDisposable { public void Dispose() => action(); }

    private static Delegate Constant(Type type, object value) =>
        Expression.Lambda(typeof(Func<>).MakeGenericType(type), Expression.Constant(value, type)).Compile();
    private static void SetProperty(object target, string name, object value) => target.GetType().GetProperty(name)!.SetValue(target, value);

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
}
