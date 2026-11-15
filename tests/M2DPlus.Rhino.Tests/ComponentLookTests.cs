using System.Drawing;
using System.Drawing.Imaging;
using System.Linq.Expressions;
using System.Reflection;
using Grasshopper.GUI.Canvas;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Parameters;
using Grasshopper.Kernel.Special;
using Rhino;
using Xunit;

namespace M2DPlus.Rhino.Tests;

/// <summary>Renders the M2D+ Drawing component with GH's own canvas (headless), in the idle, running, remark and warning
/// states at 100 % and 200 % canvas zoom, beside stock GH parameters for scale. Set M2D_UI_SCREENSHOTS to a folder to
/// write the PNGs (the owner reviews them, UX §1). The assertions check the compact layout.</summary>
[Collection("Rhino")]
[Trait("Category", "RhinoIntegration")]
public sealed class ComponentLookTests(RhinoHost host)
{
    private static readonly Guid GrasshopperId = new("b45a29b1-4343-4035-989e-044e8580d9cf");
    private static Assembly Gha => Assembly.LoadFrom(Path.Combine(AppContext.BaseDirectory, "M2DPlus.Grasshopper.gha"));
    private static Type UiType(string name) => Gha.GetType("M2DPlus.Grasshopper.UI." + name, true)!;
    private static string? ScreenshotFolder => Environment.GetEnvironmentVariable("M2D_UI_SCREENSHOTS");

    private sealed class Restore(Action action) : IDisposable { public void Dispose() => action(); }

    /// <summary>Level: 0 none, 1 remark (a normal run with unresolved pieces: stays calm), 2 warning (the user must act).</summary>
    private static readonly (string File, string Message, string Phase, double? Fraction, int Level)[] States =
    [
        ("idle", "5 drawings · 9.9 s", "Done", null, 0),
        ("running", "Drawing 3/5", "Running", .72, 0),
        ("remark", "5 drawings · 9.9 s", "Done", null, 1),
        ("warning", "5 drawings · 9.9 s", "Done", null, 2),
    ];

    [RhinoFact] public void ComponentStatesRenderCompactly() => host.Run(() =>
    {
        Assert.True(global::Rhino.PlugIns.PlugIn.LoadPlugIn(GrasshopperId));
        using var rhino = RhinoDoc.CreateHeadless(null);
        RhinoDoc.ActiveDoc = rhino;
        GH_Document.EnableSolutions = false;
        using var restore = new Restore(() => GH_Document.EnableSolutions = true);
        foreach (var (file, message, phase, fraction, level) in States)
        {
            using var document = new GH_Document();
            var component = (GH_Component)Activator.CreateInstance(Gha.GetType("M2DPlus.Grasshopper.DrawingComponent", true)!)!;
            component.CreateAttributes();
            component.Attributes.Pivot = new PointF(300, 200);
            document.AddObject(component, false);
            dynamic ui = ((dynamic)component).Ui;
            var runtimeType = UiType("Model.DrawingRuntime");
            var runtime = Activator.CreateInstance(runtimeType, Enum.Parse(UiType("Model.DrawingPhase"), phase), fraction)!;
            ui.GetType().GetProperty("Runtime")!.SetValue(ui,
                Expression.Lambda(typeof(Func<>).MakeGenericType(runtimeType), Expression.Constant(runtime, runtimeType)).Compile());
            ui.Execute("autorun", false);
            // Context: a geometry param and a panel wired in, a curve param wired out, as on the owner's canvas.
            var geometry = new Param_Geometry { NickName = "Model" };
            geometry.CreateAttributes(); geometry.Attributes.Pivot = new PointF(170, 170);
            document.AddObject(geometry, false);
            component.Params.Input[0].AddSource(geometry);
            var views = new GH_Panel { UserText = "Section A" };
            views.CreateAttributes(); views.Attributes.Bounds = new RectangleF(110, 205, 90, 20); views.Attributes.Pivot = new PointF(110, 205);
            document.AddObject(views, false);
            component.Params.Input[1].AddSource(views);
            var curves = new Param_Curve { NickName = "Lines" };
            curves.CreateAttributes(); curves.Attributes.Pivot = new PointF(470, 175);
            document.AddObject(curves, false);
            curves.AddSource(component.Params.Output[0]);
            foreach (var o in document.Objects) { o.Attributes.ExpireLayout(); o.Attributes.PerformLayout(); }
            void Settle()
            {
                component.ClearRuntimeMessages();
                component.Message = message;
                if (level == 1) component.AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "Section A: 12 unresolved pieces in the Unresolved output (orange in the preview, own layer when baked). Check them before issuing.");
                if (level == 2) component.AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Section A: 2 objects not drawn (2 Mesh). They could hide other objects. See Info.");
            }
            foreach (var zoom in new[] { 1f, 2f })
                Render(document, $"component-{file}-{(int)(zoom * 100)}.png", zoom, Settle);
            // Compact: the body is no taller than GH needs for 11 outputs and no wider than a typical component; the
            // dark tile icon, not the nickname capsule; the five inputs on the middle output rows.
            Assert.Equal(GH_IconDisplayMode.icon, component.IconDisplayMode);
            Assert.Equal(component.Params.Output[3].Attributes.Bounds.Y, component.Params.Input[0].Attributes.Bounds.Y);
            Assert.Equal(component.Params.Output[7].Attributes.Bounds.Y, component.Params.Input[4].Attributes.Bounds.Y);
            Assert.Equal(("Params", "Util", GH_Exposure.quarternary), (component.Category, component.SubCategory, component.Exposure));
            var bounds = component.Attributes.Bounds;
            Assert.True(bounds.Height <= 11 * 20 + 6, $"body height {bounds.Height}");
            Assert.True(bounds.Width <= 150, $"body width {bounds.Width}");
        }
    });

    private static void Render(GH_Document document, string file, float zoom, Action settle)
    {
        using var canvas = new GH_Canvas();
        _ = canvas.Handle;
        canvas.Document = document;
        var bounds = document.Objects.Select(o => o.Attributes.Bounds).Aggregate(RectangleF.Union);
        bounds = RectangleF.FromLTRB(bounds.Left - 24, bounds.Top - 34, bounds.Right + 24, bounds.Bottom + 40);
        var viewport = new GH_Viewport(canvas.Viewport)
        {
            Width = (int)(bounds.Width * zoom), Height = (int)(bounds.Height * zoom), Zoom = zoom,
            Tx = (int)(-bounds.X * zoom), Ty = (int)(-bounds.Y * zoom)
        };
        canvas.GenerateHiResImageTile(viewport, GH_Skin.canvas_back).Dispose();
        settle();
        using var image = canvas.GenerateHiResImageTile(viewport, GH_Skin.canvas_back);
        Assert.True(image.Width > 50 && image.Height > 50);
        if (ScreenshotFolder is { } folder) { Directory.CreateDirectory(folder); image.Save(Path.Combine(folder, file), ImageFormat.Png); }
        canvas.Document = null;
    }
}
