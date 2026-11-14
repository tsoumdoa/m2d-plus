using System.Drawing;
using M2DPlus.Core;
using M2DPlus.Core.Bake;
using M2DPlus.Core.Sections;
using M2DPlus.Rhino.Services;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using Xunit;
using Xunit.Abstractions;

namespace M2DPlus.Rhino.Tests;

[Collection("Rhino")]
[Trait("Category", "RhinoIntegration")]
public sealed class M3WiringTests(RhinoHost host, ITestOutputHelper output)
{
    private static ViewRequest SettingsFor(ViewRequest view, params string[] lines)
    {
        var context = new ViewSettingsContext("view", view.Name, view.Kind, view.Cut is not null, 10, .00001,
            view.Kind == ViewSourceKind.Box ? view.Cut?.SourceDepth : null);
        var settings = Settings.Resolve(new[] { context }, new[] { new[] { "depth=10", "beyond=off", "farclip=none" }.Concat(lines).ToArray() }, DocumentUnits.Metres);
        Assert.False(settings.HasErrors, string.Join(";", settings.Diagnostics));
        return view with { Settings = settings.Views[0] };
    }
    private static DrawingBatch Draw(RhinoDoc doc, SceneCache scene, DrawingCache cache, ViewRequest[] views, params Plane[] targets)
    {
        var lease = BaselineTests.Capture(doc, scene);
        return cache.SolveAsync(lease, new(lease.Value.Revision, "m3", "m3", 1), views, new(), targets, default).GetAwaiter().GetResult();
    }
    private void Healthy(ViewDrawing view) { output.WriteLine(view.Info); Assert.Null(view.Error); Assert.False(view.Report.Quarantined, view.Info); }
    private static double Length(ViewDrawing view, LineClass kind, string? source = null) => view.Items.Where(p => p.Class == kind && (source is null || p.SourceIdentity == source))
        .Sum(p => { using var g = p.CopyGeometry(); return g is Curve c ? c.GetLength() : 0; });
    private static Guid Box(RhinoDoc doc, Point3d a, Point3d b, ObjectAttributes? attributes = null)
    { using var box = Brep.CreateFromBox(new BoundingBox(a, b)); return doc.Objects.AddBrep(box, attributes); }

    [RhinoFact] public void BeyondUsesExactQuadraticPlaneRootsAndFadeUsesPieceDepthExtrema() => host.Run(() =>
    {
        using var doc = RhinoDoc.CreateHeadless(null); doc.ModelAbsoluteTolerance = .00001;
        // x=2t, z=-1-4t^2: d=2 intersects at t=1/2, x=1, independently of native parameterisation.
        using var bezier = new BezierCurve(new[] { new Point3d(0, 0, -1), new Point3d(1, 0, -1), new Point3d(2, 0, -5) });
        using var curve = bezier.ToNurbsCurve(); doc.Objects.AddCurve(curve);
        using var scene = new SceneCache(); using var cache = new DrawingCache();
        using var result = Draw(doc, scene, cache, new[] { SettingsFor(ViewRequest.Parse(Plane.WorldXY), "beyond=2", "fade=on") });
        var view = result.Views[0]; Healthy(view);
        Assert.Equal(1, Length(view, LineClass.Visible), 5); Assert.Equal(1, Length(view, LineClass.Beyond), 5);
        var near = Assert.Single(view.Items, p => p.Class == LineClass.Visible); var far = Assert.Single(view.Items, p => p.Class == LineClass.Beyond);
        using var n = near.CopyGeometry(); using var f = far.CopyGeometry();
        Assert.Equal(1, n.GetBoundingBox(true).Max.X, 5); Assert.Equal(1, f.GetBoundingBox(true).Min.X, 5);
        Assert.Equal(.895, near.Intensity, 5); Assert.Equal(.755, far.Intensity, 5);
        Assert.Equal(1, near.Depths.Minimum, 5); Assert.Equal(2, near.Depths.Maximum, 5);
        Assert.Equal(5, far.Depths.Maximum, 5);
    });

    [RhinoFact] public void HiddenOffAndClearGlassPreserveTheProbeLoci() => host.Run(() =>
    {
        foreach (var materialGlass in new[] { false, true })
        {
            using var doc = RhinoDoc.CreateHeadless(null); doc.ModelAbsoluteTolerance = .00001;
            var layer = doc.Layers.Add(materialGlass ? "Panels" : "Facade Glazing", Color.SkyBlue);
            using var attributes = new ObjectAttributes { LayerIndex = layer };
            if (materialGlass)
            {
                var material = doc.Materials.Add(); var editedMaterial = doc.Materials[material]; editedMaterial.Transparency = .7; editedMaterial.CommitChanges();
                attributes.MaterialSource = ObjectMaterialSource.MaterialFromObject; attributes.MaterialIndex = material;
            }
            var glass = Box(doc, new(-1, -1, -2), new(1, 1, -1), attributes);
            var probe = doc.Objects.AddLine(new Point3d(-3, 0, -3), new Point3d(3, 0, -3)).ToString();
            using var scene = new SceneCache(); using var cache = new DrawingCache();
            using var opaque = Draw(doc, scene, cache, new[] { SettingsFor(ViewRequest.Parse(Plane.WorldXY)) }); Healthy(opaque.Views[0]);
            Assert.Equal(4, Length(opaque.Views[0], LineClass.Visible, probe), 5); Assert.Equal(2, Length(opaque.Views[0], LineClass.Hidden, probe), 5);
            using var off = Draw(doc, scene, cache, new[] { SettingsFor(ViewRequest.Parse(Plane.WorldXY), "hidden=off") }); Healthy(off.Views[0]);
            Assert.Equal(0, Length(off.Views[0], LineClass.Hidden)); Assert.Equal(4, Length(off.Views[0], LineClass.Visible, probe), 5);
            using var clear = Draw(doc, scene, cache, new[] { SettingsFor(ViewRequest.Parse(Plane.WorldXY), "glass=clear") }); Healthy(clear.Views[0]);
            Assert.Equal(6, Length(clear.Views[0], LineClass.Visible, probe), 5); Assert.Equal(0, Length(clear.Views[0], LineClass.Hidden, probe));
            Assert.Contains(clear.Views[0].Items, p => p.SourceIdentity == glass.ToString());
        }
    });

    [RhinoFact] public void FarClipSettingsEmitIndependentTaperedSquareAndFadeAtTheFarDepth() => host.Run(() =>
    {
        using var doc = RhinoDoc.CreateHeadless(null); doc.ModelAbsoluteTolerance = .00001;
        using var bottom = new Rectangle3d(new Plane(new Point3d(0, 0, -3), Vector3d.ZAxis), new Interval(-2, 2), new Interval(-2, 2)).ToNurbsCurve();
        using var top = new Rectangle3d(new Plane(new Point3d(0, 0, 1), Vector3d.ZAxis), new Interval(-.5, .5), new Interval(-.5, .5)).ToNurbsCurve();
        var lofts = Brep.CreateFromLoft(new[] { bottom, top }, Point3d.Unset, Point3d.Unset, LoftType.Straight, false);
        try { using var solid = lofts[0].CapPlanarHoles(.00001); doc.Objects.AddBrep(solid); }
        finally { foreach (var loft in lofts) loft.Dispose(); }
        using var scene = new SceneCache(); using var cache = new DrawingCache();
        foreach (var mode in new[] { "line", "noline", "none" })
        {
            using var result = Draw(doc, scene, cache, new[] { SettingsFor(ViewRequest.Parse(Plane.WorldXY), "depth=2", "farclip=" + mode, "fade=on") });
            var view = result.Views[0]; Healthy(view);
            Assert.Equal(mode == "line" ? 13 : 0, Length(view, LineClass.FarClip), 4);
            if (mode == "line") Assert.All(view.Items.Where(p => p.Class == LineClass.FarClip), p => Assert.Equal(.3, p.Intensity, 4));
            if (mode == "none") Assert.Contains(view.Items, p => p.Depths.Maximum > 2.9);
        }
    });

    [RhinoFact] public void TangentsOnFilletedBoxActuallyChangeTheNativeOutput() => host.Run(() =>
    {
        using var doc = RhinoDoc.CreateHeadless(null); doc.ModelAbsoluteTolerance = .00001;
        using var box = Brep.CreateFromBox(new BoundingBox(new Point3d(-2, -2, -4), new Point3d(2, 2, -1)));
        var radii = Enumerable.Repeat(.3, box.Edges.Count).ToArray();
        var fillets = Brep.CreateFilletEdges(box, Enumerable.Range(0, box.Edges.Count), radii, radii, BlendType.Fillet, RailType.RollingBall, .00001);
        try { Assert.NotEmpty(fillets); foreach (var fillet in fillets) doc.Objects.AddBrep(fillet); }
        finally { foreach (var fillet in fillets) fillet.Dispose(); }
        using var scene = new SceneCache(); using var cache = new DrawingCache();
        using var off = Draw(doc, scene, cache, new[] { SettingsFor(ViewRequest.Parse("SW")) }); Healthy(off.Views[0]);
        using var on = Draw(doc, scene, cache, new[] { SettingsFor(ViewRequest.Parse("SW"), "tangents=on", "seams=on") }); Healthy(on.Views[0]);
        Assert.True(on.Views[0].Items.Count > off.Views[0].Items.Count);
        Assert.Contains(on.Views[0].Items, p => p.Profile);
    });

    [RhinoFact] public void PlacementIsRigidRowGapAndMismatchAndStyleEditsNeverCallHld() => host.Run(() =>
    {
        using var doc = RhinoDoc.CreateHeadless(null); doc.ModelAbsoluteTolerance = .00001;
        doc.Objects.AddLine(new Point3d(2, 3, -1), new Point3d(6, 3, -1));
        using var scene = new SceneCache(); using var cache = new DrawingCache();
        var source = new Plane(new Point3d(1, 2, 0), Vector3d.XAxis, Vector3d.YAxis);
        var view = ViewRequest.Parse(new Rectangle3d(source, new Interval(0, 10), new Interval(0, 8)));
        var first = SettingsFor(view, "gap=3"); var views = new[] { first, first };
        using var baseline = Draw(doc, scene, cache, views); Healthy(baseline.Views[0]);
        var calls = NativeDrawing.HldCalls;
        var target = new Plane(new Point3d(100, 20, 30), Vector3d.YAxis, Vector3d.ZAxis);
        var styled = views.Select(v => SettingsFor(v, "scale=1:50", "fade=on", "gap=3", "hatch=black", "cuts=merge")).ToArray();
        using var placed = Draw(doc, scene, cache, styled, target);
        Assert.Equal(calls, NativeDrawing.HldCalls);
        Assert.Equal(target.Origin, placed.Views[0].Frame.Plane.Origin);
        Assert.Equal(target.PointAt(13, 0), placed.Views[1].Frame.Plane.Origin);
        foreach (var v in placed.Views) { Assert.Equal(10, v.Frame.Width, 8); Assert.Equal(4, Length(v, LineClass.Visible), 6); }
        using var perView = Draw(doc, scene, cache, styled, target, Plane.WorldXY);
        Assert.Equal(Point3d.Origin, perView.Views[1].Frame.Plane.Origin);
        Assert.Equal(calls, NativeDrawing.HldCalls);
        Assert.Throws<ArgumentException>(() => Draw(doc, scene, cache, styled, target, target, target));
        Assert.Equal(calls, NativeDrawing.HldCalls);
        cache.Clear(); using var cold = Draw(doc, scene, cache, styled, target); Assert.True(NativeDrawing.HldCalls > calls);
    });

    [RhinoFact] public void HatchPoliciesAndMergeHaveIndependentAreasAndNoVisibilityCost() => host.Run(() =>
    {
        using var doc = RhinoDoc.CreateHeadless(null); doc.ModelAbsoluteTolerance = .00001;
        var layer = doc.Layers.Add("Walls", Color.Red);
        Box(doc, new(0, 0, -1), new(2, 2, 1), new ObjectAttributes { LayerIndex = layer });
        Box(doc, new(2, 0, -1), new(4, 2, 1), new ObjectAttributes { LayerIndex = layer });
        using var scene = new SceneCache(); using var cache = new DrawingCache();
        long calls = -1;
        foreach (var cuts in new[] { "separate", "merge" })
        foreach (var mode in new[] { "styles", "colour", "black", "none" })
        {
            using var batch = Draw(doc, scene, cache, new[] { SettingsFor(ViewRequest.Parse(Plane.WorldXY), "hatch=" + mode, "cuts=" + cuts) });
            var v = batch.Views[0]; Healthy(v); Assert.Equal(cuts == "merge" ? 1 : 2, v.Regions.Count);
            Assert.Equal(8, v.Regions.Sum(r => r.Region.Area), 5);
            Assert.All(v.Regions, r => { if (mode == "none") Assert.Null(r.Region.Hatch); else { Assert.NotNull(r.Region.Hatch); Assert.Equal(mode == "black" ? 0xff000000 : 0xffff0000, r.Region.Style.PatternColour); } });
            if (calls == -1) calls = NativeDrawing.HldCalls; else Assert.Equal(calls, NativeDrawing.HldCalls);
        }
        doc.Layers[layer].Color = Color.Blue; doc.Layers[layer].PlotWeight = .7;
        using var recoloured = Draw(doc, scene, cache, new[] { SettingsFor(ViewRequest.Parse(Plane.WorldXY), "weights=layers") });
        Assert.Equal(calls, NativeDrawing.HldCalls);
        Assert.All(recoloured.Views[0].Regions, r => { Assert.Equal(0xff0000ff, r.Region.Style.PatternColour); Assert.Equal(.7, r.Source.LayerWidth, 8); });
    });

    [RhinoFact] public void BakeReplacesOnlyUneditedObjectsPreservesLayerEditsAndUndoesAsOneRecord() => host.Run(() =>
    {
        using var doc = RhinoDoc.CreateHeadless(null); doc.ModelAbsoluteTolerance = .00001;
        var layer = doc.Layers.Add("Arch", Color.Red); var child = doc.Layers.Add(new Layer { Name = "Walls", ParentLayerId = doc.Layers[layer].Id, Color = Color.Red });
        Box(doc, new(0, 0, -1), new(2, 2, 1), new ObjectAttributes { LayerIndex = child });
        doc.Objects.AddLine(new Point3d(3, 0, -1), new Point3d(5, 0, -1), new ObjectAttributes { LayerIndex = child });
        using var scene = new SceneCache(); using var cache = new DrawingCache();
        using var batch = Draw(doc, scene, cache, new[] { SettingsFor(ViewRequest.Parse(Plane.WorldXY)) }); Healthy(batch.Views[0]);
        var owner = Guid.NewGuid(); var drawings = new[] { new BakeDrawing(batch.Views[0], "Section A") };
        var first = DrawingBake.Bake(doc, owner, drawings); Assert.True(first.Added.Count >= 3);
        var hatchLayer = doc.Layers.FindByFullPath("M2D+::Section A::Hatch::Arch::Walls", -1); Assert.True(hatchLayer >= 0);
        var visibleLayer = doc.Layers.FindByFullPath("M2D+::Section A::Visible::Arch::Walls", -1); Assert.True(visibleLayer >= 0);
        Assert.Equal(.18, doc.Layers[visibleLayer].PlotWeight, 8);
        var edited = first.Added.First(id => doc.Objects.FindId(id).Geometry is Curve);
        var layerMoved = first.Added.First(id => id != edited && doc.Objects.FindId(id).Geometry is Curve);
        var unchanged = first.Added.First(id => id != edited && id != layerMoved);
        using var movedAttributes = doc.Objects.FindId(layerMoved).Attributes.Duplicate(); movedAttributes.LayerIndex = child;
        Assert.True(doc.Objects.ModifyAttributes(layerMoved, movedAttributes, true));
        var moved = doc.Objects.Transform(edited, Transform.Translation(0, 10, 0), true); Assert.NotEqual(Guid.Empty, moved);
        doc.Layers[visibleLayer].PlotWeight = .42; doc.Layers[visibleLayer].Color = Color.Purple;
        doc.Layers[visibleLayer].IsLocked = true; doc.Layers[visibleLayer].IsVisible = false;
        var second = DrawingBake.Bake(doc, owner, drawings); output.WriteLine(string.Join("\n", second.Messages));
        Assert.NotNull(doc.Objects.FindId(moved)); Assert.NotNull(doc.Objects.FindId(layerMoved)); Assert.Null(doc.Objects.FindId(unchanged));
        Assert.Equal(first.Added.Count, second.Added.Count);
        Assert.Contains(second.Messages, m => m.Contains("kept 2 you edited")); Assert.Contains(second.Messages, m => m.Contains("Hidden layers kept hidden"));
        Assert.Equal(.42, doc.Layers[visibleLayer].PlotWeight, 8); Assert.Equal(Color.Purple.ToArgb(), doc.Layers[visibleLayer].Color.ToArgb());
        Assert.True(doc.Layers[visibleLayer].IsLocked); Assert.False(doc.Layers[visibleLayer].IsVisible);
        Assert.All(second.Added, id => { var a = doc.Objects.FindId(id).Attributes; Assert.NotNull(a.GetUserString(BakeTags.Hash)); Assert.Equal(owner.ToString("D") + "/Section A", a.GetUserString(BakeTags.Bake)); });
        Assert.True(doc.Undo()); Assert.All(second.Added, id => Assert.Null(doc.Objects.FindId(id))); Assert.NotNull(doc.Objects.FindId(unchanged));
        Assert.NotNull(doc.Objects.FindId(moved));
    });

    [RhinoFact] public void BakeAppliesFadeToDisplayAndPrintAndUpdatesOnlyManagedPresetWidths() => host.Run(() =>
    {
        using var doc = RhinoDoc.CreateHeadless(null); doc.ModelAbsoluteTolerance = .00001;
        var layer = doc.Layers.Add("Steel", Color.Navy); doc.Layers[layer].PlotWeight = .6;
        doc.Objects.AddLine(new Point3d(0, 0, -2), new Point3d(4, 0, -2), new ObjectAttributes { LayerIndex = layer });
        using var scene = new SceneCache(); using var cache = new DrawingCache();
        using var faded = Draw(doc, scene, cache, new[] { SettingsFor(ViewRequest.Parse(Plane.WorldXY), "fade=on") });
        var owner = Guid.NewGuid();
        var first = DrawingBake.Bake(doc, owner, new[] { new BakeDrawing(faded.Views[0], "Line") });
        var obj = doc.Objects.FindId(Assert.Single(first.Added));
        Assert.Equal(Color.FromArgb(38, 38, 38).ToArgb(), obj.Attributes.ObjectColor.ToArgb());
        Assert.Equal(obj.Attributes.ObjectColor.ToArgb(), obj.Attributes.PlotColor.ToArgb());
        var category = doc.Layers.FindByFullPath("M2D+::Line::Visible", -1); Assert.Equal(.18, doc.Layers[category].PlotWeight, 8);
        using var detail = Draw(doc, scene, cache, new[] { SettingsFor(ViewRequest.Parse(Plane.WorldXY), "scale=1:50") });
        DrawingBake.Bake(doc, owner, new[] { new BakeDrawing(detail.Views[0], "Line") }); Assert.Equal(.25, doc.Layers[category].PlotWeight, 8);
        doc.Layers[category].PlotWeight = .42; doc.Layers[category].Color = Color.Purple;
        using var site = Draw(doc, scene, cache, new[] { SettingsFor(ViewRequest.Parse(Plane.WorldXY), "scale=1:500") });
        var preserved = DrawingBake.Bake(doc, owner, new[] { new BakeDrawing(site.Views[0], "Line") });
        Assert.Equal(.42, doc.Layers[category].PlotWeight, 8); Assert.Equal(Color.Purple.ToArgb(), doc.Layers[category].Color.ToArgb());
        Assert.Contains(preserved.Messages, m => m.Contains("Kept layer width you edited"));
        using var layers = Draw(doc, scene, cache, new[] { SettingsFor(ViewRequest.Parse(Plane.WorldXY), "weights=layers") });
        var fromLayer = DrawingBake.Bake(doc, owner, new[] { new BakeDrawing(layers.Views[0], "Source widths") });
        var sourceObj = doc.Objects.FindId(Assert.Single(fromLayer.Added)); Assert.Equal(.6, sourceObj.Attributes.PlotWeight, 8);
        Assert.Equal(Color.Navy.ToArgb(), sourceObj.Attributes.ObjectColor.ToArgb());
        // An unresolved piece is never faded and is assigned to its own orange category.
        layers.Views[0].Pieces[0].Class = LineClass.Unresolved;
        var unresolved = DrawingBake.Bake(doc, owner, new[] { new BakeDrawing(layers.Views[0], "Uncertain") });
        var uncertain = doc.Objects.FindId(Assert.Single(unresolved.Added));
        Assert.Equal("M2D+::Uncertain::Unresolved::Steel", doc.Layers[uncertain.Attributes.LayerIndex].FullPath);
        Assert.Equal(Color.FromArgb(224, 122, 0).ToArgb(), uncertain.Attributes.ObjectColor.ToArgb());
    });

    [RhinoFact] public void BakedHatchPatternAndColoursSurviveNativeDocumentSaveReopen() => host.Run(() =>
    {
        using var doc = RhinoDoc.OpenHeadless(Path.Combine(AppContext.BaseDirectory, "fixtures", "sections", "section_styles.3dm"));
        using var scene = new SceneCache(); using var cache = new DrawingCache();
        using var batch = Draw(doc, scene, cache, new[] { SettingsFor(ViewRequest.Parse(Plane.WorldXY)) }); Healthy(batch.Views[0]);
        var owner = Guid.NewGuid();
        var result = DrawingBake.Bake(doc, owner, new[] { new BakeDrawing(batch.Views[0], "Patterns") });
        var hatches = result.Added.Select(id => doc.Objects.FindId(id)).Where(o => o.Geometry is Hatch).ToArray(); Assert.NotEmpty(hatches);
        var expected = hatches.Select(o => (o.Id, ((Hatch)o.Geometry).PatternScale, ((Hatch)o.Geometry).PatternRotation,
            doc.HatchPatterns[((Hatch)o.Geometry).PatternIndex].HatchLineCount, o.Attributes.ObjectColor.ToArgb(), o.Attributes.HatchBackgroundFillColor.ToArgb())).ToArray();
        var file = Path.Combine(Path.GetTempPath(), "m2d-m3-" + Guid.NewGuid().ToString("N") + ".3dm");
        try
        {
            Assert.True(doc.Write3dmFile(file, new global::Rhino.FileIO.FileWriteOptions()));
            using var reopened = RhinoDoc.OpenHeadless(file);
            foreach (var e in expected)
            {
                var obj = reopened.Objects.FindId(e.Id); var hatch = Assert.IsType<Hatch>(obj.Geometry);
                Assert.Equal(e.PatternScale, hatch.PatternScale, 8); Assert.Equal(e.PatternRotation, hatch.PatternRotation, 8);
                Assert.Equal(e.HatchLineCount, reopened.HatchPatterns[hatch.PatternIndex].HatchLineCount);
                Assert.Equal(e.Item5, obj.Attributes.ObjectColor.ToArgb()); Assert.Equal(e.Item6, obj.Attributes.HatchBackgroundFillColor.ToArgb());
            }
            var replacement = DrawingBake.Bake(reopened, owner, new[] { new BakeDrawing(batch.Views[0], "Patterns") });
            Assert.Contains(replacement.Messages, m => m.Contains($"replaced {result.Added.Count}"));
            Assert.DoesNotContain(replacement.Messages, m => m.Contains("kept 1"));
        }
        finally { if (File.Exists(file)) File.Delete(file); }
    });
}
