using System.Diagnostics;
using System.Windows.Forms;
using Grasshopper.Kernel;
using M2DPlus.Grasshopper.UI.Model;
using M2DPlus.Rhino.Services;
using Rhino;

namespace M2DPlus.Grasshopper.UI;

/// <summary>Text on the system clipboard. Rhino for Mac's own toolkit is Eto, so the Mac build uses Eto's clipboard;
/// Windows keeps WinForms exactly as before.</summary>
internal static class PlatformClipboard
{
    public static void SetText(string text)
    {
        // Eto's documented use: a fresh Clipboard over the general pasteboard, not disposed (disposal is not needed).
        if (OperatingSystem.IsMacOS()) new Eto.Forms.Clipboard().Text = text;
        else Clipboard.SetText(text);
    }
    public static string? GetText()
    {
        if (OperatingSystem.IsMacOS()) return new Eto.Forms.Clipboard().Text;
        return Clipboard.ContainsText() ? Clipboard.GetText() : null;
    }
}

/// <summary>The "Platform self-test…" menu item (macOS only; docs/macos-test-checklist.md): runs
/// <see cref="PlatformSelfTest"/>, then the GH/UI checks that only make sense inside Grasshopper (icons, menu rendering
/// with text entry, Eto, WinForms and Eto timers, UI-thread marshalling), then shows and saves one report. The clipboard
/// is left alone (a write would replace whatever the user copied); the report dialog's Copy button is the manual check.</summary>
internal static class PlatformSelfTestUi
{
    private static bool _running;

    public static void Start(GH_Component owner, DrawingUi ui)
    {
        if (_running) return;
        // The fixture blocks the UI thread for a few seconds; never let it wait behind another component's drawing.
        if (HostPlatform.NativeBusy)
        {
            owner.AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "Another M2D+ drawing is running. Run the platform self-test when it finishes.");
            owner.OnDisplayExpired(true);
            return;
        }
        _running = true;
        try { StartCore(owner, ui); }
        catch (Exception e)
        {
            _running = false;
            RhinoApp.WriteLine($"M2D+ platform self-test could not start: {e.GetType().Name}: {e.Message}");
        }
    }

    private static void StartCore(GH_Component owner, DrawingUi ui)
    {
        var lines = new List<string>();
        var passed = true;
        void Add(string name, Func<(bool Ok, string Detail)> check)
        {
            try { var (ok, detail) = check(); passed &= ok; lines.Add($"[{(ok ? "pass" : "FAIL")}] {name}: {detail}"); }
            catch (Exception e) { passed = false; lines.Add($"[FAIL] {name}: {e.GetType().Name}: {e.Message}"); }
        }
        RhinoApp.WriteLine("M2D+ platform self-test: running (Rhino is busy for a few seconds)…");
        PlatformSelfTest.Report? report = null;
        try { report = PlatformSelfTest.Run(); passed &= report.Passed; }
        catch (Exception e) { passed = false; lines.Add($"[FAIL] self-test: {e.GetType().Name}: {e.Message}"); }

        Add("icons", () =>
        {
            using var drawing = M2DIcons.Drawing(); using var logo = M2DIcons.Logo(24);
            var pixel = drawing.GetPixel(12, 12);
            return (drawing.Width == 24 && logo.Height == 24 && pixel.A > 0, $"Drawing {drawing.Width}x{drawing.Height}, Logo {logo.Width}x{logo.Height}, centre alpha {pixel.A}");
        });
        Add("component menu", () =>
        {
            var nodes = ComponentMenu.Build(ui.MenuContext());
            using var menu = new ContextMenuStrip();
            MenuRenderer.Append(menu, nodes, (_, _) => { });
            var all = Flatten(menu.Items).ToList();
            var textBoxes = all.OfType<ToolStripTextBox>().Count();
            var parallel = all.Any(i => i.Text?.StartsWith("Parallel views", StringComparison.Ordinal) == true);
            var expectParallel = HostPlatform.IsolatedWorkers;
            return (all.Count > 20 && textBoxes > 0 && parallel == expectParallel,
                $"{all.Count} items, {textBoxes} text entries; Parallel views {(parallel ? "shown" : "hidden")} (expected {(expectParallel ? "shown" : "hidden")})");
        });
        Add("eto", () =>
        {
            using var dialog = new Eto.Forms.Dialog { Title = "M2D+" };
            return (true, $"Eto {typeof(Eto.Forms.Dialog).Assembly.GetName().Version} platform {Eto.Platform.Instance?.ID ?? "none"}");
        });

        // Timers tick only once this handler returns to the run loop; finish when both have ticked, or after a fallback.
        var clock = Stopwatch.StartNew();
        double? winformsAt = null, etoAt = null, marshalAt = null;
        var finished = false;
        System.Windows.Forms.Timer? winforms = null; Eto.Forms.UITimer? eto = null; System.Threading.Timer? fallback = null;
        void Finish(string reason)
        {
            if (finished) return;
            finished = true;
            try { FinishCore(reason); }
            catch (Exception e) { RhinoApp.WriteLine($"M2D+ platform self-test failed to report: {e.GetType().Name}: {e.Message}"); }
            finally { _running = false; }
        }
        void FinishCore(string reason)
        {
            try { winforms?.Stop(); winforms?.Dispose(); } catch (Exception) { }
            try { eto?.Stop(); eto?.Dispose(); } catch (Exception) { }
            fallback?.Dispose();
            lines.Add($"[{(winformsAt is null ? "FAIL" : "pass")}] WinForms timer (component status/progress repaint): {(winformsAt is double w ? $"ticked at {w:F2} s" : "never ticked")}");
            lines.Add($"[{(etoAt is null ? "FAIL" : "pass")}] Eto UITimer: {(etoAt is double et ? $"ticked at {et:F2} s" : "never ticked")}");
            lines.Add($"[{(marshalAt is null ? "info" : "pass")}] RhinoApp.InvokeOnUiThread: {(marshalAt is double m ? $"ran at {m:F2} s" : "not needed")}; finished: {reason}");
            passed &= winformsAt is not null && etoAt is not null;
            var text = (report?.Text ?? "M2D+ platform self-test") + Environment.NewLine + string.Join(Environment.NewLine, lines) + Environment.NewLine +
                $"Overall: {(passed ? "PASS" : "FAIL")}. Manual checks remain: docs/macos-test-checklist.md (canvas drawing, menus, preview, bake, cancel).";
            string where;
            try { where = PlatformSelfTest.Save(text); } catch (Exception e) { where = "not saved: " + e.Message; }
            var summary = $"M2D+ platform self-test: {(passed ? "PASS" : "FAIL")}. Report: {where}";
            RhinoApp.WriteLine(summary);
            owner.AddRuntimeMessage(passed ? GH_RuntimeMessageLevel.Remark : GH_RuntimeMessageLevel.Warning, summary);
            owner.OnDisplayExpired(true);
            _running = false;
            Show(text);
        }
        try
        {
            winforms = new System.Windows.Forms.Timer { Interval = 100 };
            winforms.Tick += (_, _) => { winformsAt ??= clock.Elapsed.TotalSeconds; winforms.Stop(); if (etoAt is not null) Finish("timers ticked"); };
            winforms.Start();
        }
        catch (Exception e) { lines.Add($"[FAIL] WinForms timer: {e.GetType().Name}: {e.Message}"); passed = false; }
        try
        {
            eto = new Eto.Forms.UITimer { Interval = 0.1 };
            eto.Elapsed += (_, _) => { etoAt ??= clock.Elapsed.TotalSeconds; if (winformsAt is not null) Finish("timers ticked"); };
            eto.Start();
        }
        catch (Exception e) { lines.Add($"[FAIL] Eto UITimer: {e.GetType().Name}: {e.Message}"); passed = false; }
        // If a UI timer never fires, finish from a thread-pool timer through Rhino's UI-thread marshalling.
        fallback = new System.Threading.Timer(_ => RhinoApp.InvokeOnUiThread(new Action(() =>
        { marshalAt ??= clock.Elapsed.TotalSeconds; Finish("5 s fallback"); })), null, 5000, Timeout.Infinite);
    }

    private static IEnumerable<ToolStripItem> Flatten(ToolStripItemCollection items)
    {
        foreach (ToolStripItem item in items)
        {
            yield return item;
            if (item is ToolStripDropDownItem dropDown && dropDown.HasDropDownItems)
                foreach (var child in Flatten(dropDown.DropDownItems)) yield return child;
        }
    }

    private static void Show(string text)
    {
        try
        {
            if (RhinoApp.IsRunningHeadless || global::Rhino.UI.RhinoEtoApp.MainWindow is not { Visible: true } main) return;
            using var dialog = new Eto.Forms.Dialog { Title = "M2D+ platform self-test", Resizable = true, ClientSize = new Eto.Drawing.Size(760, 520) };
            var area = new Eto.Forms.TextArea { Text = text, ReadOnly = true, Wrap = true };
            var copy = new Eto.Forms.Button { Text = "Copy report" };
            copy.Click += (_, _) => { try { PlatformClipboard.SetText(text); } catch (Exception) { } };
            var close = new Eto.Forms.Button { Text = "Close" };
            close.Click += (_, _) => dialog.Close();
            dialog.Content = new Eto.Forms.TableLayout
            {
                Padding = 10, Spacing = new Eto.Drawing.Size(6, 6),
                Rows = { new Eto.Forms.TableRow(area) { ScaleHeight = true }, new Eto.Forms.TableLayout(new Eto.Forms.TableRow(null, copy, close)) }
            };
            dialog.DefaultButton = close; dialog.AbortButton = close;
            dialog.UseRhinoStyle();
            dialog.ShowModal(main);
        }
        catch (Exception e) when (e is InvalidOperationException or NullReferenceException or NotSupportedException) { }
    }
}
