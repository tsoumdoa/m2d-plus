using System.Drawing;
using System.Reflection;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Parameters;
using Grasshopper.Kernel.Types;
using M2DPlus.Core;
using M2DPlus.Core.Bake;
using M2DPlus.Core.Styling;
using M2DPlus.Rhino.Services;
using M2DPlus.Rhino.Views;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using Xunit;
using Xunit.Abstractions;

namespace M2DPlus.Rhino.Tests;

/// <summary>
/// Configurable bake layer layout (owner decision 2026-11-16, UX §8.1): the exact layer tree and object counts per layer for
/// each layout, replace-bake within and across layouts (old objects found by their markers, M2D+'s empty unchanged layers
/// removed, user-created or user-changed layers kept), the merged <c>layer</c> layout, exclusion of baked geometry, and the
/// component republishing without a visibility solve or presentation when only <c>bake</c> changes.
/// </summary>
[Collection("Rhino")]
[Trait("Category", "RhinoIntegration")]
public sealed class BakeLayoutTests(RhinoHost host, ITestOutputHelper output)
{
    private const string Grasshopper = "b45a29b1-4343-4035-989e-044e8580d9cf";

    private static ViewRequest SettingsFor(ViewRequest view, params string[] lines)
    {
        var context = new ViewSettingsContext("view", view.Name, view.Kind, view.Cut is not null, 10, .00001,
            view.Kind == ViewSourceKind.Box ? view.Cut?.SourceDepth : null);
        var settings = Settings.Resolve([context], [new[] { "depth=10", "beyond=off", "farclip=none" }.Concat(lines).ToArray()], DocumentUnits.Metres);
        Assert.False(settings.HasErrors, string.Join(";", settings.Diagnostics));
        return view with { Settings = settings.Views[0] };
    }

    /// <summary>Arch::Walls holds a cut box, a line hidden under it and a visible line; Structure a visible line. Grasshopper
    /// geometry (no source layer) is a fifth, visible line.</summary>
    private sealed record Model(RhinoDoc Doc, Guid[] Ids)
    {
        public IEnumerable<CaptureInput> Inputs => Ids.Select((id, i) => new CaptureInput(id, [0], $"input/{{0}}/{i}/{id}") { SourceItem = i })
            .Append(new CaptureInput(new LineCurve(new Point3d(10, 0, -1), new Point3d(12, 0, -1)), [0], $"input/{{0}}/{Ids.Length}/local") { SourceItem = Ids.Length });
    }
    private static Model Build(RhinoDoc doc)
    {
        doc.ModelAbsoluteTolerance = .00001;
        var arch = doc.Layers.Add("Arch", Color.Red);
        var walls = doc.Layers.Add(new Layer { Name = "Walls", ParentLayerId = doc.Layers[arch].Id, Color = Color.Red });
        var structure = doc.Layers.Add("Structure", Color.Blue);
        var ids = new List<Guid>();
        using (var box = Brep.CreateFromBox(new BoundingBox(new Point3d(0, 0, -1), new Point3d(2, 2, 1))))
            ids.Add(doc.Objects.AddBrep(box, new ObjectAttributes { LayerIndex = walls }));
        ids.Add(doc.Objects.AddLine(new Point3d(.5, 1, -2), new Point3d(1.5, 1, -2), new ObjectAttributes { LayerIndex = walls }));
        ids.Add(doc.Objects.AddLine(new Point3d(3, 0, -1), new Point3d(5, 0, -1), new ObjectAttributes { LayerIndex = walls }));
        ids.Add(doc.Objects.AddLine(new Point3d(6, 0, -1), new Point3d(8, 0, -1), new ObjectAttributes { LayerIndex = structure }));
        return new(doc, ids.ToArray());
    }
    private DrawingBatch Draw(Model model, SceneCache scene, DrawingCache cache, params ViewRequest[] views)
    {
        var lease = BaselineTests.Capture(model.Doc, scene, model.Inputs);
        var batch = cache.SolveAsync(lease, new(lease.Value.Revision, "bake", "bake", 1), views, new(), [], default).GetAwaiter().GetResult();
        foreach (var view in batch.Views) { output.WriteLine(view.Info); Assert.Null(view.Error); Assert.False(view.Report.Quarantined, view.Info); }
        return batch;
    }

    /// <summary>Objects per bake layer, worked out independently from the drawing with the Core rule.</summary>
    private static SortedDictionary<string, int> Expected(BakeLayout layout, params (ViewDrawing View, string Name)[] drawings)
    {
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        void Add(string name, LineClass lineClass, string? source)
        { var path = BakeNames.Layer(layout, name, lineClass, source).FullPath; counts[path] = counts.GetValueOrDefault(path) + 1; }
        foreach (var (view, name) in drawings)
        {
            foreach (var piece in view.Items) Add(name, piece.Class, piece.Source.LayerPath);
            foreach (var region in view.Regions)
            {
                if (region.Region.Style.Boundary.Visible) foreach (var _ in region.Region.Loops) Add(name, LineClass.Cut, region.Source.LayerPath);
                if (region.Region.Hatch is not null) Add(name, LineClass.Hatch, region.Source.LayerPath);
            }
        }
        return counts;
    }
    private static SortedDictionary<string, int> Actual(RhinoDoc doc) => new(doc.Objects
        .Where(o => BakeTags.TryParseOwner(o.Attributes.GetUserString(BakeTags.Bake), out _, out _))
        .GroupBy(o => doc.Layers[o.Attributes.LayerIndex].FullPath).ToDictionary(g => g.Key, g => g.Count()), StringComparer.Ordinal);
    private static string[] Tree(RhinoDoc doc) => doc.Layers.Where(l => !l.IsDeleted && (l.FullPath == "M2D+" || l.FullPath.StartsWith("M2D+::", StringComparison.Ordinal)))
        .Select(l => l.FullPath).Order(StringComparer.Ordinal).ToArray();
    private static string[] Prefixes(IEnumerable<string> paths, params string[] more) => paths.Concat(more).SelectMany(p =>
    { var parts = p.Split("::"); return Enumerable.Range(1, parts.Length).Select(n => string.Join("::", parts.Take(n))); })
        .Distinct().Order(StringComparer.Ordinal).ToArray();
    private void Print(string title, RhinoDoc doc)
    {
        var counts = Actual(doc);
        output.WriteLine(title);
        foreach (var path in Tree(doc))
        {
            var parts = path.Split("::");
            output.WriteLine(new string(' ', 2 * (parts.Length - 1)) + parts[^1] + (counts.TryGetValue(path, out var n) ? $"  ({n})" : ""));
        }
    }
    private static int DocumentSources(RhinoDoc doc) { using var cache = new SceneCache(); using var lease = BaselineTests.Capture(doc, cache); return lease.Value.SourceCount; }
    private static int DroppedAsBaked(RhinoDoc doc)
    {
        using var cache = new SceneCache(); using var watch = new DocumentWatch(doc);
        using var capture = new SceneCapture(doc, watch, doc.Objects.Select((o, i) => new CaptureInput(o.Id, [0], $"all/{i}/{o.Id}") { SourceItem = i }).ToArray(), cache);
        while (!capture.Step(TimeSpan.FromMilliseconds(12))) { }
        using var lease = capture.Accept();
        return capture.Dropped.Count(d => d.Why == SceneCapture.Exclusion.Baked);
    }

    [RhinoFact] public void EachLayoutBakesItsExactLayerTreeAndReBakeReplacesWithoutDuplicates() => host.Run(() =>
    {
        // Rhino refuses layer names starting with ( or [, so the GH group cannot be "(Grasshopper)".
        Assert.True(ModelComponent.IsValidComponentName(BakeNames.Grasshopper));
        Assert.False(ModelComponent.IsValidComponentName("(Grasshopper)"));
        foreach (var layout in BakeNames.Layouts)
        {
            using var doc = RhinoDoc.CreateHeadless(null); var model = Build(doc);
            using var scene = new SceneCache(); using var cache = new DrawingCache();
            using var batch = Draw(model, scene, cache, SettingsFor(ViewRequest.Parse(Plane.WorldXY), "bake=" + BakeNames.LayoutText(layout)));
            var view = batch.Views[0];
            Assert.Equal(layout, view.Settings!.Bake);
            var sources = DocumentSources(doc);
            var owner = Guid.NewGuid();
            // No explicit layout: the drawing's own bake setting decides.
            var first = DrawingBake.Bake(doc, owner, [new BakeDrawing(view, "Section A")]);
            output.WriteLine(string.Join("\n", first.Messages));
            Print($"bake = {BakeNames.LayoutText(layout)}", doc);
            var expected = Expected(layout, (view, "Section A"));
            Assert.Equal(expected, Actual(doc));
            Assert.Equal(Prefixes(expected.Keys), Tree(doc));
            Assert.Equal(expected.Values.Sum(), first.Added.Count);
            string[] must = layout switch
            {
                BakeLayout.ViewLayer => ["M2D+::Section A::Arch::Walls::Cut", "M2D+::Section A::Arch::Walls::Hatch", "M2D+::Section A::Arch::Walls::Hidden",
                    "M2D+::Section A::Arch::Walls::Visible", "M2D+::Section A::Structure::Visible", "M2D+::Section A::Grasshopper (no layer)::Visible"],
                BakeLayout.LayerView => ["M2D+::Arch::Walls::Section A::Cut", "M2D+::Arch::Walls::Section A::Hatch", "M2D+::Arch::Walls::Section A::Hidden",
                    "M2D+::Arch::Walls::Section A::Visible", "M2D+::Structure::Section A::Visible", "M2D+::Grasshopper (no layer)::Section A::Visible"],
                BakeLayout.Layer => ["M2D+::Arch::Walls::Cut", "M2D+::Arch::Walls::Hatch", "M2D+::Arch::Walls::Hidden", "M2D+::Arch::Walls::Visible",
                    "M2D+::Structure::Visible", "M2D+::Grasshopper (no layer)::Visible"],
                _ => ["M2D+::Section A::Cut::Arch::Walls", "M2D+::Section A::Hatch::Arch::Walls", "M2D+::Section A::Hidden::Arch::Walls",
                    "M2D+::Section A::Visible::Arch::Walls", "M2D+::Section A::Visible::Structure", "M2D+::Section A::Visible"],
            };
            Assert.All(must, path => Assert.Contains(path, expected.Keys));

            // Category styling on creation: the line-type level (classic: it and its source sublayers); grouping levels plain.
            var preset = new LineWeightPreset(100);
            foreach (var path in Tree(doc))
            {
                var layer = doc.Layers[doc.Layers.FindByFullPath(path, -1)];
                Assert.Equal(owner.ToString(), layer.GetUserString(BakeLayerStyle.Created));
                Assert.Equal(DrawingBake.Style(layer).Signature, layer.GetUserString(BakeLayerStyle.Tag));
                var parts = path.Split("::");
                var type = layout == BakeLayout.ViewType ? parts.Length > 2 ? parts[2] : null : parts[^1];
                var styled = layout == BakeLayout.ViewType ? parts.Length > 2 : must.Contains(path) || expected.ContainsKey(path);
                var kind = type is null ? (LineClass?)null : BakeNames.ClassicCategory("M2D+::x::" + type);
                if (styled && kind is { } k)
                {
                    Assert.Equal("1:100", layer.GetUserString(BakeTags.Weights));
                    Assert.Equal(preset[k].PrintWidthMillimetres, layer.PlotWeight, 8);
                    var linetype = layer.LinetypeIndex >= 0 ? doc.Linetypes.FindIndex(layer.LinetypeIndex)?.Name : null;
                    Assert.True(k == LineClass.Hidden == (linetype == "Hidden"), $"{path}: linetype {layer.LinetypeIndex} {linetype}");
                }
                else
                {
                    Assert.Null(layer.GetUserString(BakeTags.Weights));
                    Assert.Equal(Color.Black.ToArgb(), layer.Color.ToArgb());
                }
            }
            Assert.All(first.Added, id =>
            {
                var a = doc.Objects.FindId(id).Attributes;
                Assert.Equal(BakeTags.Owner(owner, "Section A"), a.GetUserString(BakeTags.Bake));
                Assert.NotNull(a.GetUserString(BakeTags.Class)); Assert.Equal(a.GetUserString(BakeTags.Layer), doc.Layers[a.LayerIndex].Id.ToString());
            });

            // Re-bake, same layout: every object replaced, no duplicates, the same tree.
            var second = DrawingBake.Bake(doc, owner, [new BakeDrawing(view, "Section A", layout)]);
            Assert.All(first.Added, id => Assert.Null(doc.Objects.FindId(id)));
            Assert.Equal(first.Added.Count, second.Added.Count);
            Assert.Contains(second.Messages, m => m.Contains($"replaced {first.Added.Count} · kept 0", StringComparison.Ordinal));
            Assert.Equal(expected, Actual(doc));
            Assert.Equal(Prefixes(expected.Keys), Tree(doc));
            // Baked objects are never scene geometry: the next run reads the same sources, and explicit references drop them.
            Assert.Equal(sources, DocumentSources(doc));
            Assert.Equal(second.Added.Count, DroppedAsBaked(doc));
        }
    });

    [RhinoFact] public void SwitchingLayoutReplacesTheOldBakeAndKeepsUserLayers() => host.Run(() =>
    {
        using var doc = RhinoDoc.CreateHeadless(null); var model = Build(doc);
        using var scene = new SceneCache(); using var cache = new DrawingCache();
        using var batch = Draw(model, scene, cache, SettingsFor(ViewRequest.Parse(Plane.WorldXY)));
        var view = batch.Views[0]; var owner = Guid.NewGuid(); var sources = DocumentSources(doc);
        BakeResult Bake(BakeLayout layout) { var r = DrawingBake.Bake(doc, owner, [new BakeDrawing(view, "Section A", layout)]); output.WriteLine(string.Join("\n", r.Messages)); return r; }
        int Index(string path) { var i = doc.Layers.FindByFullPath(path, -1); Assert.True(i >= 0, path); return i; }

        // 1. A classic bake as beta.3 left it: no m2d.style on its layers.
        var classic = Bake(BakeLayout.ViewType);
        foreach (var path in Tree(doc)) doc.Layers[Index(path)].SetUserString(BakeLayerStyle.Tag, null);
        Assert.All(Tree(doc), path => Assert.Null(doc.Layers[Index(path)].GetUserString(BakeLayerStyle.Tag)));
        // The user recolours one classic layer, adds a layer of their own and edits one baked hatch.
        const string recoloured = "M2D+::Section A::Visible::Structure";
        doc.Layers[Index(recoloured)].Color = Color.Purple;
        Assert.True(doc.Layers.Add(new Layer { Name = "Notes", ParentLayerId = doc.Layers[Index("M2D+::Section A")].Id }) >= 0);
        var hatch = classic.Added.First(id => doc.Objects.FindId(id).Geometry is Hatch);
        var edited = doc.Objects.Transform(hatch, Transform.Translation(0, 20, 0), true);
        Assert.NotEqual(Guid.Empty, edited);
        var editedLayer = doc.Layers[doc.Objects.FindId(edited).Attributes.LayerIndex].FullPath;
        Assert.Equal("M2D+::Section A::Hatch::Arch::Walls", editedLayer);
        Print("bake = view-type (classic, beta.3 layers)", doc);

        // 2. view-layer: every unedited classic object goes, its emptied unchanged layers go; user layers stay.
        var viewLayer = Bake(BakeLayout.ViewLayer);
        Print("then bake = view-layer", doc);
        Assert.All(classic.Added.Where(id => id != hatch), id => Assert.Null(doc.Objects.FindId(id)));
        Assert.NotNull(doc.Objects.FindId(edited));
        Assert.Contains(viewLayer.Messages, m => m.Contains("kept 1 you edited", StringComparison.Ordinal));
        Assert.Contains(viewLayer.Messages, m => m.Contains("removed", StringComparison.Ordinal) && m.Contains("empty M2D+ layer", StringComparison.Ordinal));
        var expected = Expected(BakeLayout.ViewLayer, (view, "Section A"));
        var counts = Actual(doc); counts.Remove(editedLayer, out var keptHatch); Assert.Equal(1, keptHatch);
        Assert.Equal(expected, counts);
        string[] kept = [recoloured, "M2D+::Section A::Notes", editedLayer];
        Assert.Equal(Prefixes(expected.Keys, kept), Tree(doc));
        Assert.Equal(Color.Purple.ToArgb(), doc.Layers[Index(recoloured)].Color.ToArgb());

        // One undo record: Undo brings the classic bake back and removes the view-layer one, layers included.
        Assert.True(doc.Undo());
        Assert.All(classic.Added.Where(id => id != hatch), id => Assert.NotNull(doc.Objects.FindId(id)));
        Assert.All(viewLayer.Added, id => Assert.Null(doc.Objects.FindId(id)));
        Assert.True(doc.Layers.FindByFullPath("M2D+::Section A::Visible::Arch::Walls", -1) >= 0);
        // Bake view-layer again from the restored classic state: the same result.
        viewLayer = Bake(BakeLayout.ViewLayer);
        Assert.All(classic.Added.Where(id => id != hatch), id => Assert.Null(doc.Objects.FindId(id)));
        counts = Actual(doc); counts.Remove(editedLayer);
        Assert.Equal(expected, counts);
        Assert.Equal(Prefixes(expected.Keys, kept), Tree(doc));

        // 3. layer-view, after the user widened one view-layer layer: it and its parents stay; the rest of view-layer goes.
        const string widened = "M2D+::Section A::Arch::Walls::Cut";
        doc.Layers[Index(widened)].PlotWeight = .9;
        var layerView = Bake(BakeLayout.LayerView);
        Print("then bake = layer-view", doc);
        Assert.All(viewLayer.Added, id => Assert.Null(doc.Objects.FindId(id)));
        expected = Expected(BakeLayout.LayerView, (view, "Section A"));
        counts = Actual(doc); counts.Remove(editedLayer);
        Assert.Equal(expected, counts);
        kept = [.. kept, widened];
        Assert.Equal(Prefixes(expected.Keys, kept), Tree(doc));
        Assert.Equal(.9, doc.Layers[Index(widened)].PlotWeight, 8);

        // 4. layer: drawing levels of layer-view go; the shared source levels are reused.
        var layer = Bake(BakeLayout.Layer);
        Print("then bake = layer", doc);
        Assert.All(layerView.Added, id => Assert.Null(doc.Objects.FindId(id)));
        expected = Expected(BakeLayout.Layer, (view, "Section A"));
        counts = Actual(doc); counts.Remove(editedLayer);
        Assert.Equal(expected, counts);
        Assert.Equal(Prefixes(expected.Keys, kept), Tree(doc));
        Assert.Equal(layer.Added.Count + 1, doc.Objects.Count(o => o.Attributes.GetUserString(BakeTags.Bake) == BakeTags.Owner(owner, "Section A")));
        Assert.Equal(sources, DocumentSources(doc));
        Assert.Equal(layer.Added.Count + 1, DroppedAsBaked(doc));
    });

    [RhinoFact] public void TheLayerLayoutMergesTwoDrawingsAndReplacesEachOnItsOwn() => host.Run(() =>
    {
        using var doc = RhinoDoc.CreateHeadless(null); var model = Build(doc);
        using var scene = new SceneCache(); using var cache = new DrawingCache();
        using var batch = Draw(model, scene, cache, SettingsFor(ViewRequest.Parse(Plane.WorldXY), "bake=layer"),
            SettingsFor(ViewRequest.Parse(new Plane(new Point3d(0, 0, -.5), Vector3d.ZAxis)), "bake=layer"));
        var (a, b) = (batch.Views[0], batch.Views[1]); var owner = Guid.NewGuid(); var sources = DocumentSources(doc);
        var both = DrawingBake.Bake(doc, owner, [new BakeDrawing(a, "Plan A"), new BakeDrawing(b, "Plan B")]);
        output.WriteLine(string.Join("\n", both.Messages));
        Print("bake = layer, two drawings", doc);
        var expected = Expected(BakeLayout.Layer, (a, "Plan A"), (b, "Plan B"));
        Assert.Equal(expected, Actual(doc));
        Assert.Equal(Prefixes(expected.Keys), Tree(doc));
        Assert.DoesNotContain(Tree(doc), p => p.Contains("Plan", StringComparison.Ordinal));
        Guid[] Owned(string name) => doc.Objects.Where(o => o.Attributes.GetUserString(BakeTags.Bake) == BakeTags.Owner(owner, name)).Select(o => o.Id).ToArray();
        var ownedA = Owned("Plan A"); var ownedB = Owned("Plan B");
        Assert.Equal(Expected(BakeLayout.Layer, (a, "Plan A")).Values.Sum(), ownedA.Length);
        Assert.Equal(Expected(BakeLayout.Layer, (b, "Plan B")).Values.Sum(), ownedB.Length);
        // Shared leaves hold objects of both drawings.
        Assert.Contains(expected.Keys, path => doc.Objects.FindByLayer(doc.Layers[doc.Layers.FindByFullPath(path, -1)])
            .Select(o => o.Attributes.GetUserString(BakeTags.Bake)).Distinct().Count() == 2);
        // Re-baking Plan A replaces only Plan A; Plan B is untouched and nothing is duplicated.
        var again = DrawingBake.Bake(doc, owner, [new BakeDrawing(a, "Plan A")]);
        Assert.All(ownedA, id => Assert.Null(doc.Objects.FindId(id)));
        Assert.All(ownedB, id => Assert.NotNull(doc.Objects.FindId(id)));
        Assert.Equal(ownedA.Length, again.Added.Count); Assert.Equal(ownedA.Length, Owned("Plan A").Length);
        Assert.Equal(expected, Actual(doc));
        Assert.Equal(Prefixes(expected.Keys), Tree(doc));
        Assert.Equal(sources, DocumentSources(doc));
        Assert.Equal(ownedA.Length + ownedB.Length, DroppedAsBaked(doc));
    });

    /// <summary>The actual component: changing bake from the menu, view-object text or the Settings input republishes the same
    /// drawing (no visibility solve, no HLD call, no presentation), Info shows the new value and Bake uses it.</summary>
    [RhinoFact] public void ChangingBakeRepublishesWithoutRedrawingAndBakesWithTheNewLayout() => host.Run(() =>
    {
        Assert.True(global::Rhino.PlugIns.PlugIn.LoadPlugIn(new Guid(Grasshopper)));
        using var doc = RhinoDoc.CreateHeadless(null); RhinoDoc.ActiveDoc = doc; doc.ModelAbsoluteTolerance = .00001;
        GH_Document.EnableSolutions = true;
        var walls = doc.Layers.Add("Walls", Color.Black);
        using (var sphere = new Sphere(new Point3d(0, 0, 1), 1).ToBrep()) doc.Objects.AddBrep(sphere, new ObjectAttributes { LayerIndex = walls });
        var frame = doc.Objects.AddSurface(new PlaneSurface(new Plane(new Point3d(-5, -5, 5), Vector3d.XAxis, Vector3d.YAxis), new Interval(0, 15), new Interval(0, 10)));
        Assert.True(Assert.Single(ViewBoxes.Validate(doc, [frame])).Ok);
        Assert.Equal("default", doc.Objects.FindId(frame).Attributes.GetUserString("m2d.bake"));
        using var gh = new GH_Document { Enabled = true };
        var assembly = Assembly.LoadFrom(Path.Combine(AppContext.BaseDirectory, "M2DPlus.Grasshopper.gha"));
        var component = (GH_Component)Activator.CreateInstance(assembly.GetType("M2DPlus.Grasshopper.DrawingComponent", true)!)!;
        gh.AddObject(component, false);
        dynamic ui = component.GetType().GetProperty("Ui")!.GetValue(component)!;
        var reference = new GH_Brep(); reference.ReferenceID = frame;
        ((Param_GenericObject)component.Params.Input[1]).PersistentData.Append(reference);
        var settings = (Param_String)component.Params.Input[3];
        settings.PersistentData.Append(new GH_String("name=Plan"));
        object? Field(string name) => component.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(component);
        string Info() => ((GH_String)Assert.Single(component.Params.Output[8].VolatileData.AllData(true))).Value;
        void Pump(Func<bool> until) => GrasshopperTests.PumpUntil(() => until() && component.Params.Output[8].VolatileDataCount == 1,
            () => component.Message + ": " + string.Join(";", component.RuntimeMessages(GH_RuntimeMessageLevel.Error)),
            () => { if (gh.ScheduleDelay >= 0) gh.NewSolution(false, GH_SolutionMode.Silent); });
        Guid[] Owned() => doc.Objects.Where(o => o.Attributes.GetUserString(BakeTags.Bake) == BakeTags.Owner(component.InstanceGuid, "Plan")).Select(o => o.Id).ToArray();
        string[] Layers(Guid[] ids) => ids.Select(id => doc.Layers[doc.Objects.FindId(id).Attributes.LayerIndex].FullPath).Distinct().Order(StringComparer.Ordinal).ToArray();
        try
        {
            gh.NewSolution(true, GH_SolutionMode.Silent);
            Pump(() => Field("_drawing") is not null);
            var drawing = Field("_drawing"); var hld = NativeDrawing.HldCalls; var solves = NativeDrawing.VisibilitySolves;
            Assert.True(hld > 0, "a sphere needs native HLD");
            Assert.Contains("bake = view-layer (default)", Info());
            ui.Execute("bake", false);
            var baked = Owned(); Assert.NotEmpty(baked);
            output.WriteLine("default: " + string.Join(", ", Layers(baked)));
            Assert.All(Layers(baked), p => Assert.StartsWith("M2D+::Plan::Walls::", p));

            void Republished(string what, Action change, string info, string prefix)
            {
                var restores = (int)Field("_restoreCount")!; var captures = (int)Field("_captureCount")!;
                change();
                Pump(() => (int)Field("_restoreCount")! > restores && Field("_drawing") is not null);
                // The same drawing object: no visibility solve, no presentation, and the scene was not read again (even after a bake).
                Assert.Same(drawing, Field("_drawing"));
                Assert.Equal(captures, (int)Field("_captureCount")!);
                Assert.Equal(hld, NativeDrawing.HldCalls); Assert.Equal(solves, NativeDrawing.VisibilitySolves);
                Assert.Contains(info, Info());
                var before = Owned();
                ui.Execute("bake", false);
                var after = Owned();
                Assert.All(before, id => Assert.Null(doc.Objects.FindId(id)));
                Assert.Equal(baked.Length, after.Length);
                output.WriteLine($"{what}: {string.Join(", ", Layers(after))}");
                Assert.All(Layers(after), p => Assert.StartsWith(prefix, p));
                Assert.DoesNotContain(Tree(doc), p => p.StartsWith("M2D+::Plan::Walls", StringComparison.Ordinal) && !prefix.StartsWith("M2D+::Plan::Walls", StringComparison.Ordinal));
            }
            // Menu → layer; view object → view-type (beats the menu); Settings input → layer-view (beats both).
            Republished("menu bake = layer", () => ui.Execute("set:bake=layer", false), "bake = layer (component menu)", "M2D+::Walls::");
            Republished("m2d.bake = view-type", () =>
            {
                var attributes = doc.Objects.FindId(frame).Attributes.Duplicate(); attributes.SetUserString("m2d.bake", "view-type");
                Assert.True(doc.Objects.ModifyAttributes(frame, attributes, true));
                ((DocumentWatch)Field("_watch")!).VerifyHostState();
            }, "bake = view-type (view object)", "M2D+::Plan::");
            Assert.All(Layers(Owned()), p => Assert.Matches(@"^M2D\+::Plan::(Visible|Hidden)::Walls$", p));
            Republished("Settings bake = layer-view", () =>
            {
                settings.PersistentData.Clear(); settings.PersistentData.Append(new GH_String("name=Plan\nbake = layer-view"));
                settings.ExpireSolution(true);
            }, "bake = layer-view (set by input)", "M2D+::Walls::Plan::");

            // A change that is not bake-only still redraws (presentation; exact mode reuses visibility).
            var restored = (int)Field("_restoreCount")!;
            ui.Execute("set:hidden=off", false);
            Pump(() => Field("_drawing") is { } next && !ReferenceEquals(next, drawing));
            Assert.Equal(restored, (int)Field("_restoreCount")!);
            Assert.Equal(hld, NativeDrawing.HldCalls);
        }
        finally { component.RemovedFromDocument(gh); SceneCache.Shared.Clear(); }
    });
}
