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

/// <summary>
/// The component's look, layout B (owner choice 2026-11-16, UX §1), on the real <c>DrawingAttributes</c> with GH's own
/// canvas (headless): one 20-unit row grid, one 4-unit margin, inputs on GH's even spacing snapped to the grid (labels
/// share output rows), a slim name strip by default, and a full-width footer with the status left, a fixed-width button
/// flush right (inset by the margin on every side) and the progress bar along the footer's top edge. Renders the done,
/// running, remark and warning states at 100 % and 200 %; set M2D_UI_SCREENSHOTS to write the PNGs.
/// </summary>
[Collection("Rhino")]
[Trait("Category", "RhinoIntegration")]
public sealed class ComponentLookTests(RhinoHost host, Xunit.Abstractions.ITestOutputHelper output)
{
    private static readonly Guid GrasshopperId = new("b45a29b1-4343-4035-989e-044e8580d9cf");
    private static Assembly Gha => Assembly.LoadFrom(Path.Combine(AppContext.BaseDirectory, "M2DPlus.Grasshopper.gha"));
    private static Type UiType(string name) => Gha.GetType("M2DPlus.Grasshopper.UI." + name, true)!;
    private static string? ScreenshotFolder => Environment.GetEnvironmentVariable("M2D_UI_SCREENSHOTS");
    private const float Row = 20, Margin = 4, Footer = 24, Strip = 14, NameColumn = 24;

    private sealed class Restore(Action action) : IDisposable { public void Dispose() => action(); }

    /// <summary>Level: 0 none, 1 remark (a normal run with unresolved pieces: stays calm), 2 warning (the user must act).</summary>
    private static readonly (string File, string Message, string Phase, double? Fraction, int Level, string Button)[] States =
    [
        ("done", "5 drawings · 9.9 s", "Done", null, 0, "Bake"),
        ("running", "Drawing 3/5", "Running", .6, 0, "Cancel"),
        ("remark", "5 drawings · 9.9 s", "Done", null, 1, "Bake"),
        ("warning", "5 drawings · 9.9 s", "Done", null, 2, "Bake"),
        ("paused", "Paused · out of date", "Paused", null, 0, "Run"),
    ];

    private static (GH_Document Document, GH_Component Component) Build(string phase, double? fraction)
    {
        var document = new GH_Document();
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
        geometry.CreateAttributes(); geometry.Attributes.Pivot = new PointF(110, 140);
        document.AddObject(geometry, false);
        component.Params.Input[0].AddSource(geometry);
        var views = new GH_Panel { UserText = "Section A" };
        views.CreateAttributes(); views.Attributes.Bounds = new RectangleF(50, 175, 90, 20); views.Attributes.Pivot = new PointF(50, 175);
        document.AddObject(views, false);
        component.Params.Input[1].AddSource(views);
        var curves = new Param_Curve { NickName = "Lines" };
        curves.CreateAttributes();
        document.AddObject(curves, false);
        curves.AddSource(component.Params.Output[0]);
        foreach (var o in document.Objects) { o.Attributes.ExpireLayout(); o.Attributes.PerformLayout(); }
        curves.Attributes.Pivot = new PointF(component.Attributes.Bounds.Right + 90, 110);
        curves.Attributes.ExpireLayout(); curves.Attributes.PerformLayout();
        return (document, component);
    }

    private static RectangleF Get(GH_Component component, string property) =>
        (RectangleF)component.Attributes.GetType().GetProperty(property)!.GetValue(component.Attributes)!;

    /// <summary>The grid, margin, footer and button invariants of layout B, for any display mode.</summary>
    private static void AssertLayout(GH_Component component, float centreWidth, bool running = false)
    {
        var body = component.Attributes.Bounds;
        var footer = Get(component, "FooterBounds"); var button = Get(component, "ButtonBounds"); var centre = Get(component, "CentreBounds");
        var outputs = component.Params.Output; var inputs = component.Params.Input;
        // Rows: eleven outputs on a 20-unit grid; the body is the rows plus the footer, which has a second 20-unit row
        // while a job runs.
        var footerHeight = running ? Footer + Row : Footer;
        Assert.Equal(11 * Row + footerHeight, body.Height, 3);
        for (var i = 0; i < outputs.Count; i++) Assert.Equal(body.Top + (i + .5f) * Row, outputs[i].Attributes.OutputGrip.Y, 1);
        // Inputs: GH's even spacing snapped to the grid, every label on an output row.
        Assert.Equal([1, 3, 5, 7, 9], inputs.Select(p => (int)Math.Round((p.Attributes.InputGrip.Y - body.Top - Row / 2) / Row)));
        foreach (var p in inputs) Assert.Contains(outputs, o => Math.Abs(o.Attributes.OutputGrip.Y - p.Attributes.InputGrip.Y) < .51f);
        // One margin: labels end / start two margins from the centre column, which is inset one margin top and bottom.
        Assert.Equal(centreWidth, centre.Width, 3);
        Assert.Equal(body.Top + Margin, centre.Top, 3); Assert.Equal(footer.Top - Margin, centre.Bottom, 3);
        foreach (var p in inputs) Assert.InRange(centre.Left - p.Attributes.Bounds.Right, 2 * Margin - 1, 2 * Margin + 1);
        foreach (var p in outputs) Assert.InRange(p.Attributes.Bounds.Left - centre.Right, 2 * Margin - 1, 2 * Margin + 1);
        Assert.Equal(body.Left, inputs.Min(p => p.Attributes.Bounds.Left), 1);
        Assert.Equal(body.Right, outputs.Max(p => p.Attributes.Bounds.Right), 1);
        // Footer: full width at the bottom. The button sits in the footer's last 24-unit band, inset one margin on every
        // side of that band (so it is the same size, in the same place relative to the bottom, in every state).
        Assert.Equal((body.Left, body.Width, body.Bottom, footerHeight), (footer.Left, footer.Width, footer.Bottom, footer.Height));
        Assert.Equal(Footer - 2 * Margin, button.Height, 3);
        Assert.Equal(footer.Bottom - Footer + Margin, button.Top, 3); Assert.Equal(footer.Bottom - Margin, button.Bottom, 3); Assert.Equal(footer.Right - Margin, button.Right, 3);
    }

    [RhinoFact] public void LayoutBHoldsItsGridInEveryStateAndDisplayMode() => host.Run(() =>
    {
        Assert.True(global::Rhino.PlugIns.PlugIn.LoadPlugIn(GrasshopperId));
        using var rhino = RhinoDoc.CreateHeadless(null);
        RhinoDoc.ActiveDoc = rhino;
        GH_Document.EnableSolutions = false;
        using var restore = new Restore(() => GH_Document.EnableSolutions = true);
        var buttons = new List<RectangleF>();
        var grips = new Dictionary<string, float[]>();
        foreach (var (file, message, phase, fraction, level, label) in States)
        {
            var (document, component) = Build(phase, fraction);
            using var owned = document;
            Assert.Equal(("Params", "Util", GH_Exposure.quarternary), (component.Category, component.SubCategory, component.Exposure));
            // Default: GH's "application" display, drawn as layout B's slim name strip (no icon).
            Assert.Equal(GH_IconDisplayMode.application, component.IconDisplayMode);
            AssertLayout(component, Strip, running: phase == "Running");
            grips[phase] = component.Params.Input.Select(p => p.Attributes.InputGrip.Y).Concat(component.Params.Output.Select(p => p.Attributes.OutputGrip.Y)).ToArray();
            void Settle()
            {
                component.ClearRuntimeMessages();
                component.Message = message;
                if (level == 1) component.AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "Section A: 12 unresolved pieces in the Unresolved output (orange in the preview, own layer when baked). Check them before issuing.");
                if (level == 2) component.AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Section A: 2 objects not drawn (2 Mesh). They could hide other objects. See Info.");
            }
            foreach (var zoom in new[] { 1f, 2f })
            {
                using var image = Render(document, $"component-{file}-{(int)(zoom * 100)}.png", zoom, Settle, out var offset);
                var last = component.Attributes.GetType().GetProperty("LastButton")!.GetValue(component.Attributes)!;
                var shown = last.GetType().GetField("Item1")!.GetValue(last)!; var bounds = (RectangleF)last.GetType().GetField("Item2")!.GetValue(last)!;
                Assert.True(label == shown.ToString(), $"{file} at {zoom}: button {shown}, expected {label}; rows {component.Attributes.GetType().GetProperty("LastRows")!.GetValue(component.Attributes)}; bounds {component.Attributes.Bounds}; message {component.Message}; runtime {((dynamic)component).Ui.CurrentRuntime}");
                Assert.Equal(Get(component, "ButtonBounds"), bounds);
                buttons.Add(bounds);
                // The progress bar runs along the footer's top edge: cobalt on its first canvas unit while running.
                var footer = Get(component, "FooterBounds");
                var pixel = image.GetPixel((int)((footer.Left + 12) * zoom + offset.X), (int)((footer.Top + .5f) * zoom + offset.Y));
                if (phase == "Running") Assert.True(Bluish(pixel), $"progress pixel {pixel}");
                else Assert.False(Bluish(pixel), $"no progress expected, pixel {pixel}");
            }
        }
        // One button width in every state.
        Assert.Single(buttons.Select(b => b.Width).Distinct());
        // The footer grows downwards while running: every grip, and so every wire, stays exactly where it was.
        Assert.Equal(grips["Done"], grips["Running"]);
        // An explicit Display ▸ Name or Icon works the GH way, on the same grid.
        foreach (var mode in new[] { GH_IconDisplayMode.name, GH_IconDisplayMode.icon })
        {
            var (document, component) = Build("Done", null);
            using var owned = document;
            component.IconDisplayMode = mode;
            component.Attributes.ExpireLayout(); component.Attributes.PerformLayout();
            AssertLayout(component, NameColumn);
            using var image = Render(document, $"component-display-{mode}-200.png", 2, () => component.Message = "5 drawings · 9.9 s", out _);
        }
    });

    /// <summary>
    /// The footer while a job runs (owner feedback on beta.1: "Queued" was vague, and one row was too short): row 1 says
    /// what is being processed with its full counts, row 2 how far along (the step and percentage, the estimate before
    /// Cancel), one font size, and the bar along the top edge. Renders every stage to footer-*.png and checks exactly
    /// what each row shows: counts are never shortened, the view name goes first.
    /// </summary>
    [RhinoFact] public void FooterShowsEveryStage() => host.Run(() =>
    {
        Assert.True(global::Rhino.PlugIns.PlugIn.LoadPlugIn(GrasshopperId));
        using var rhino = RhinoDoc.CreateHeadless(null);
        RhinoDoc.ActiveDoc = rhino;
        GH_Document.EnableSolutions = false;
        using var restore = new Restore(() => GH_Document.EnableSolutions = true);
        var statusText = Gha.GetType("M2DPlus.Grasshopper.UI.Model.StatusText", true)!;
        var stages = new (string File, M2DPlus.Core.JobProgress Progress, double Fraction, bool Indeterminate, double SpanEnd, double? Eta, string Row1, string? Row2, string? EtaText)[]
        {
            ("loading", new(M2DPlus.Core.JobPhase.Reading, 2400, 6000), .06, false, .15, null, "Loading objects 2,400 / 6,000", null, null),
            ("waiting", new(M2DPlus.Core.JobPhase.Queued, 0, 8) { Ahead = 1 }, .15, true, .15, null, "Waiting for another drawing (1 ahead)", null, null),
            ("preparing", new(M2DPlus.Core.JobPhase.Preparing, 3100, 6000), .2, false, .25, 22, "Preparing geometry 3,100 / 6,000", null, "~20 s left"),
            ("drawing", new(M2DPlus.Core.JobPhase.Drawing, 2, 8) { View = "Section A", Step = M2DPlus.Core.ViewSteps.Native, StepFraction = .45 }, .47, false, .51, 41,
                "Drawing 3 / 8 · Section A", "native 45 %", "~40 s left"),
            ("drawing-fast", new(M2DPlus.Core.JobPhase.Drawing, 0, 1) { View = "North", Step = M2DPlus.Core.ViewSteps.Native, StepFraction = .45 }, .47, false, .75, null,
                "Drawing 1 / 1 · North", "native 45 %", null),
            ("drawing-long-name", new(M2DPlus.Core.JobPhase.Drawing, 6, 12) { View = "North elevation, courtyard side, level 3", Step = M2DPlus.Core.ViewSteps.Sections }, .64, true, .66, 95,
                "Drawing 7 / 12 · North…", "sections", "~2 min left"),
            ("workers", new(M2DPlus.Core.JobPhase.Drawing, 5, 8) { Workers = 3 }, .69, true, .95, 12, "Drawing 5 / 8 · 3 workers", null, "~12 s left"),
            // A worker's inner step crosses the pipe with its view: the view in row 1, the step's percentage in row 2.
            ("workers-step", new(M2DPlus.Core.JobPhase.Drawing, 5, 8) { Workers = 3, View = "Section A", Step = M2DPlus.Core.ViewSteps.Sections, StepFraction = .37 }, .69, true, .95, 12,
                "Drawing 5 / 8 · 3 workers · Section A", "sections 37 %", "~12 s left"),
            ("workers-sending", new(M2DPlus.Core.JobPhase.Drawing, 0, 8) { Workers = 3, Step = "sending scene" }, .3, true, .5, null, "Drawing 0 / 8 · 3 workers", "sending scene", null),
            ("output", new(M2DPlus.Core.JobPhase.Output, 0, 0), .95, true, 1, null, "Building output", null, null),
        };
        foreach (var (file, progress, fraction, indeterminate, spanEnd, eta, row1, row2, etaText) in stages)
        {
            var (document, component) = Build("Running", fraction);
            using var owned = document;
            var status = statusText.GetMethod("Of")!.Invoke(null, [progress, eta])!;
            var runtimeType = UiType("Model.DrawingRuntime");
            object runtime = Activator.CreateInstance(runtimeType, Enum.Parse(UiType("Model.DrawingPhase"), "Running"), (double?)fraction)!;
            runtimeType.GetProperty("Status")!.SetValue(runtime, status);
            runtimeType.GetProperty("Indeterminate")!.SetValue(runtime, indeterminate);
            runtimeType.GetProperty("SpanEnd")!.SetValue(runtime, spanEnd);
            dynamic ui = ((dynamic)component).Ui;
            ui.GetType().GetProperty("Runtime")!.SetValue(ui,
                Expression.Lambda(typeof(Func<>).MakeGenericType(runtimeType), Expression.Constant(runtime, runtimeType)).Compile());
            var full = (string)status.GetType().GetProperty("Full")!.GetValue(status)!;
            using var image = Render(document, $"footer-{file}-200.png", 2, () => component.Message = full, out var offset);
            // The bar fills to the fraction: cobalt just left of it, track (lighter) well right of it.
            var footer = Get(component, "FooterBounds");
            var y = (int)((footer.Top + .5f) * 2 + offset.Y);
            Assert.True(Bluish(image.GetPixel((int)((footer.Left + footer.Width * fraction - 3) * 2 + offset.X), y)), file + ": filled part");
            AssertLayout(component, Strip, running: true);
            var rows = component.Attributes.GetType().GetProperty("LastRows")!.GetValue(component.Attributes)!;
            string? Row(string item) => (string?)rows.GetType().GetField(item)!.GetValue(rows);
            output.WriteLine($"{file}: [{Row("Item1")}] [{Row("Item2")}] [{Row("Item3")}]");
            // A long view name is shortened (never the counts): the row keeps the counts and ends in an ellipsis.
            if (row1.EndsWith('…')) { Assert.StartsWith(row1[..^1], Row("Item1")); Assert.EndsWith("…", Row("Item1")); }
            else Assert.Equal(row1, Row("Item1"));
            Assert.Equal(row2, Row("Item2"));
            Assert.Equal(etaText, Row("Item3"));
        }
    });

    /// <summary>Cobalt, or cobalt anti-aliased into its neighbours at 100 %: clearly blue, unlike the grey body and dark footer.</summary>
    private static bool Bluish(Color c) => c.B - c.R > 60 && c.B > c.G + 25;

    /// <param name="offset">The viewport translation: canvas point p is at image pixel p × zoom + offset.</param>
    private static Bitmap Render(GH_Document document, string file, float zoom, Action settle, out PointF offset)
    {
        using var canvas = new GH_Canvas();
        _ = canvas.Handle;
        canvas.Document = document;
        var bounds = document.Objects.Select(o => o.Attributes.Bounds).Aggregate(RectangleF.Union);
        bounds = RectangleF.FromLTRB(bounds.Left - 24, bounds.Top - 34, bounds.Right + 24, bounds.Bottom + 24);
        var viewport = new GH_Viewport(canvas.Viewport)
        {
            Width = (int)(bounds.Width * zoom), Height = (int)(bounds.Height * zoom), Zoom = zoom,
            Tx = (int)(-bounds.X * zoom), Ty = (int)(-bounds.Y * zoom)
        };
        offset = new PointF(viewport.Tx, viewport.Ty);
        canvas.GenerateHiResImageTile(viewport, GH_Skin.canvas_back).Dispose();
        settle();
        var image = canvas.GenerateHiResImageTile(viewport, GH_Skin.canvas_back);
        Assert.True(image.Width > 50 && image.Height > 50);
        if (ScreenshotFolder is { } folder) { Directory.CreateDirectory(folder); image.Save(Path.Combine(folder, file), ImageFormat.Png); }
        canvas.Document = null;
        return image;
    }
}
