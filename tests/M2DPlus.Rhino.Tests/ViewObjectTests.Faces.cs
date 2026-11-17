using System.Diagnostics;
using Grasshopper.Kernel.Types;
using M2DPlus.Core.Placement;
using M2DPlus.Core.Views;
using M2DPlus.Rhino.Views;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using Xunit;

namespace M2DPlus.Rhino.Tests;

/// <summary>Owner decisions 2026-11-15: the <c>$M2D Views</c> layer and the front-face fill of view boxes. The fill
/// colour is checked through <see cref="ViewBoxFaceIndex"/> (the conduit's non-drawing computation); the drawing
/// itself needs an interactive Rhino viewport and is not verified here.</summary>
public sealed partial class ViewObjectTests
{
    // A 10 × 10 × 3 building at the origin.
    private static Brep Building() => Brep.CreateFromBox(new BoundingBox(new Point3d(0, 0, 0), new Point3d(10, 10, 3)));

    /// <summary>A horizontal frame at height z, x and y from −1 to 11, looking down (a plan).</summary>
    private static Rectangle3d PlanFrame(double z, double x0 = -1, double y0 = -1, double size = 12) =>
        new(new Plane(new Point3d(x0, y0, z), Vector3d.XAxis, Vector3d.YAxis), new Interval(0, size), new Interval(0, size));

    private static Guid AddViewBox(RhinoDoc doc, string name, Rectangle3d frame, double depth, string? colour = null, int layer = -1)
    {
        using var box = ViewBoxes.Build(frame, depth);
        var attributes = new ObjectAttributes { Name = name };
        if (layer >= 0) attributes.LayerIndex = layer;
        ViewBoxes.WriteDefaults(attributes);
        if (colour is not null) attributes.SetUserString("m2d.color", colour);
        var id = doc.Objects.AddExtrusion(box, attributes);
        Assert.NotEqual(Guid.Empty, id);
        return id;
    }

    private static ObjectAttributes Attributes(RhinoDoc doc, Guid id) => doc.Objects.FindId(id).Attributes;

    [RhinoFact] public void TheViewsLayerIsCreatedCobaltAndNoPrintAndAnExistingOneIsNeverOverwritten() => host.Run(() =>
    {
        Assert.True(ModelComponent.IsValidComponentName(ViewBoxes.LayerName));
        using (var doc = RhinoDoc.CreateHeadless(null))
        {
            var index = ViewBoxes.ViewsLayer(doc);
            var layer = doc.Layers[index];
            Assert.Equal("$M2D Views", layer.FullPath);
            Assert.Equal(Guid.Empty, layer.ParentLayerId);
            Assert.Equal(Argb(0x1F, 0x6F, 0xD1), layer.Color.ToArgb());
            Assert.Equal(-1, layer.PlotWeight); // No Print
            Assert.Equal(index, ViewBoxes.ViewsLayer(doc));
            Assert.Single(doc.Layers, l => !l.IsDeleted && l.Name == "$M2D Views");
        }
        // A file that already has the layer, restyled by the user: Validate and Create use it as it is.
        using (var doc = RhinoDoc.CreateHeadless(null))
        {
            RhinoDoc.ActiveDoc = doc;
            doc.Objects.AddBrep(ModelBox());
            var mine = doc.Layers.Add(new Layer { Name = "$M2D Views", Color = System.Drawing.Color.FromArgb(200, 10, 10), PlotWeight = .5 });
            var id = doc.Objects.AddSurface(FrontSurface());
            Assert.True(ViewBoxes.Validate(doc, [id]).Single().Ok);
            Assert.Equal(mine, Attributes(doc, id).LayerIndex);
            using var viewport = new ViewportInfo { IsParallelProjection = true };
            viewport.SetCameraLocation(new Point3d(0, -20, 1)); viewport.SetCameraDirection(Vector3d.YAxis); viewport.SetCameraUp(Vector3d.ZAxis);
            viewport.SetFrustum(-5, 5, -3, 3, 1, 100);
            var created = ViewBoxes.Create(doc, viewport);
            Assert.True(created.Ok, created.Message);
            Assert.Contains("$M2D Views", created.Message);
            Assert.Equal(mine, Attributes(doc, created.Id).LayerIndex);
            Assert.Equal(System.Drawing.Color.FromArgb(200, 10, 10).ToArgb(), doc.Layers[mine].Color.ToArgb());
            Assert.Equal(.5, doc.Layers[mine].PlotWeight);
            Assert.Single(doc.Layers, l => !l.IsDeleted && l.Name == "$M2D Views");
        }
    });

    private static int Argb(int r, int g, int b) => System.Drawing.Color.FromArgb(r, g, b).ToArgb();

    [RhinoFact] public void ValidateMovesConvertedObjectsToTheViewsLayerInOneUndoKeepingIdNameAndUserText() => host.Run(() =>
    {
        using var doc = RhinoDoc.CreateHeadless(null); RhinoDoc.ActiveDoc = doc;
        doc.Objects.AddBrep(ModelBox());
        var model = doc.Layers.Add(new Layer { Name = "Model" });
        var attributes = new ObjectAttributes { Name = "Section A", LayerIndex = model };
        attributes.SetUserString("m2d.hidden", "off");
        attributes.SetUserString("m2d.color", "#112233");
        attributes.SetUserString("note", "keep me");
        var id = doc.Objects.AddSurface(FrontSurface(), attributes);
        var plain = doc.Objects.AddSurface(new PlaneSurface(new Plane(new Point3d(-2, 5, 0), Vector3d.ZAxis, Vector3d.XAxis), new Interval(0, 4), new Interval(0, 4)),
            new ObjectAttributes { LayerIndex = model });

        var results = ViewBoxes.Validate(doc, [id, plain]);
        Assert.All(results, r => Assert.True(r.Ok && r.Changed, r.Message));
        Assert.All(results, r => Assert.Contains("on layer $M2D Views", r.Message));
        var obj = doc.Objects.FindId(id);
        Assert.IsType<ExtrusionObject>(obj);                       // same id, now a view box
        Assert.Equal("$M2D Views", doc.Layers[obj.Attributes.LayerIndex].FullPath);
        Assert.Equal("Section A", obj.Attributes.Name);
        Assert.Equal("off", obj.Attributes.GetUserString("m2d.hidden"));
        Assert.Equal("#112233", obj.Attributes.GetUserString("m2d.color")); // never overwritten
        Assert.Equal("keep me", obj.Attributes.GetUserString("note"));
        Assert.Equal("default", Attributes(doc, plain).GetUserString("m2d.color"));
        Assert.Equal("$M2D Views", doc.Layers[Attributes(doc, plain).LayerIndex].FullPath);
        // m2d.color is display only: it is not a drawing setting row.
        Assert.DoesNotContain(ViewBoxes.ReadRow(obj.Attributes).Values, p => p.Key == "color");

        // One undo restores both objects: surfaces again, on their layer, without the view-box user text.
        Assert.True(doc.Undo());
        foreach (var restoredId in new[] { id, plain })
        {
            var restored = doc.Objects.FindId(restoredId);
            Assert.IsType<BrepObject>(restored);
            Assert.Equal(model, restored.Attributes.LayerIndex);
            Assert.Null(restored.Attributes.GetUserString("m2d.view"));
        }
        Assert.Equal("#112233", Attributes(doc, id).GetUserString("m2d.color"));
        Assert.Null(Attributes(doc, plain).GetUserString("m2d.color"));
    });

    [RhinoFact] public void RevalidatingAViewBoxOnAnotherLayerMovesItBack() => host.Run(() =>
    {
        using var doc = RhinoDoc.CreateHeadless(null); RhinoDoc.ActiveDoc = doc;
        doc.Objects.AddBrep(ModelBox());
        var id = doc.Objects.AddSurface(FrontSurface());
        Assert.True(ViewBoxes.Validate(doc, [id]).Single().Ok);
        var views = Attributes(doc, id).LayerIndex;
        var elsewhere = doc.Layers.Add(new Layer { Name = "Elsewhere" });
        var moved = Attributes(doc, id).Duplicate();
        moved.LayerIndex = elsewhere; moved.SetUserString("m2d.color", "#00FF00");
        Assert.True(doc.Objects.ModifyAttributes(id, moved, true));

        var again = Assert.Single(ViewBoxes.Validate(doc, [id]));
        Assert.True(again.Ok && again.Changed, again.Message);
        Assert.Contains("moved to layer $M2D Views", again.Message);
        Assert.DoesNotContain("added", again.Message);
        Assert.Equal(views, Attributes(doc, id).LayerIndex);
        Assert.Equal("#00FF00", Attributes(doc, id).GetUserString("m2d.color"));
        var unchanged = Assert.Single(ViewBoxes.Validate(doc, [id]));
        Assert.False(unchanged.Changed, unchanged.Message);
        Assert.True(doc.Undo());
        Assert.Equal(elsewhere, Attributes(doc, id).LayerIndex);
    });

    [RhinoFact] public void FrontFaceColourIsRedWhenItCutsCobaltWhenItDoesntAndTheOverrideWins() => host.Run(() =>
    {
        using var doc = RhinoDoc.CreateHeadless(null); RhinoDoc.ActiveDoc = doc;
        doc.ModelAbsoluteTolerance = Tol;
        doc.Objects.AddBrep(Building());
        var off = doc.Layers.Add(new Layer { Name = "Off", IsVisible = false });
        // Things that never make a face red: a curve, an object on a layer that is off, a hidden object and a baked
        // M2D+ drawing (m2d.* user text), all crossing the plan at z = 20 inside its frame.
        doc.Objects.AddLine(new Line(new Point3d(5, 5, 0), new Point3d(5, 5, 30)));
        doc.Objects.AddBrep(Brep.CreateFromBox(new BoundingBox(new Point3d(1, 1, 19), new Point3d(2, 2, 21))), new ObjectAttributes { LayerIndex = off });
        var hidden = doc.Objects.AddBrep(Brep.CreateFromBox(new BoundingBox(new Point3d(3, 3, 19), new Point3d(4, 4, 21))));
        doc.Objects.Hide(hidden, true);
        var baked = new ObjectAttributes(); baked.SetUserString("m2d.bake", "x/y");
        doc.Objects.AddBrep(Brep.CreateFromBox(new BoundingBox(new Point3d(6, 6, 19), new Point3d(7, 7, 21))), baked);
        // A block instance crossing the plan at z = 40.
        var definition = doc.InstanceDefinitions.Add("Column", "", Point3d.Origin, [Brep.CreateFromBox(new BoundingBox(new Point3d(0, 0, 0), new Point3d(1, 1, 2)))]);
        doc.Objects.AddInstanceObject(definition, Transform.Translation(4, 4, 39));

        var plan = AddViewBox(doc, "Plan", PlanFrame(1.5), 5);
        var above = AddViewBox(doc, "Above", PlanFrame(20), 5);
        var block = AddViewBox(doc, "Block", PlanFrame(40), 5);
        // Elevation: frame at y = −5 looking +Y (Z toward −Y). Section: frame at x = 5 looking +X.
        var elevationFrame = new Rectangle3d(new Plane(new Point3d(-1, -5, -1), Vector3d.XAxis, Vector3d.ZAxis), new Interval(0, 12), new Interval(0, 5));
        var sectionFrame = new Rectangle3d(new Plane(new Point3d(5, 11, -1), -Vector3d.YAxis, Vector3d.ZAxis), new Interval(0, 12), new Interval(0, 5));
        var besideFrame = new Rectangle3d(new Plane(new Point3d(5, 30, -1), -Vector3d.YAxis, Vector3d.ZAxis), new Interval(0, 10), new Interval(0, 5));
        var elevation = AddViewBox(doc, "Elevation", elevationFrame, 20);
        var section = AddViewBox(doc, "Section", sectionFrame, 8);
        var beside = AddViewBox(doc, "Beside", besideFrame, 8); // on the x = 5 plane, but past the building's end
        var green = AddViewBox(doc, "Green", elevationFrame, 20, "#00FF00");
        var rgba = AddViewBox(doc, "Rgba", sectionFrame, 8, "#11223380");
        var bad = AddViewBox(doc, "Bad", sectionFrame, 8, "purple");
        var hiddenBox = AddViewBox(doc, "Hidden", sectionFrame, 8);
        doc.Objects.Hide(hiddenBox, true);
        var offBox = AddViewBox(doc, "Off layer", sectionFrame, 8, layer: off);

        using var index = new ViewBoxFaceIndex(doc);
        Assert.True(index.IsDirty);
        index.Refresh();
        Assert.False(index.IsDirty);
        FillChoice Fill(Guid id) => index.Find(id)!.Fill;
        Assert.Equal(new FillChoice(ViewBoxFill.Cut, true, FillSource.ByType, null), Fill(plan));
        Assert.Equal(new FillChoice(ViewBoxFill.NoCut, false, FillSource.ByType, null), Fill(above));
        Assert.True(Fill(block).Cuts);
        Assert.Equal(new FillChoice(ViewBoxFill.NoCut, false, FillSource.ByType, null), Fill(elevation));
        Assert.Equal(ViewBoxFill.Cut, Fill(section).Colour);
        Assert.False(Fill(beside).Cuts);
        Assert.Equal(new FillChoice(new Rgba(0, 255, 0, ViewBoxFill.NoCut.A), false, FillSource.Override, null), Fill(green));
        Assert.Equal(new Rgba(0x11, 0x22, 0x33, 0x80), Fill(rgba).Colour);
        Assert.True(Fill(rgba).Cuts);
        Assert.Equal(FillSource.Invalid, Fill(bad).Source);
        Assert.Equal(ViewBoxFill.Cut, Fill(bad).Colour);
        Assert.Contains("Bad: m2d.color = 'purple'", Fill(bad).Problem);
        Assert.Equal(System.Drawing.Color.FromArgb(0x40, 0xD9, 0x41, 0x2B), index.Find(section)!.Colour);
        // Only visible view boxes are drawn.
        Assert.True(index.Find(section)!.Visible);
        Assert.False(index.Find(hiddenBox)!.Visible);
        Assert.False(index.Find(offBox)!.Visible);
        Assert.Equal(1, index.Rebuilds);
        // The front-face quad lies on the frame.
        var mesh = index.MeshOf(section)!;
        Assert.Equal(4, mesh.Vertices.Count);
        Assert.All(mesh.Vertices, v => Assert.Equal(5, v.X, 9));
        // The scene excludes the view boxes, the curve, the off-layer, hidden and baked objects (building + block).
        Assert.Equal(2, index.SceneCount);
        // The conduit only exists with a display; headless it is not installed.
        output.WriteLine($"headless = {RhinoApp.IsRunningHeadless}, conduit installed = {ViewBoxFaceConduit.Install()}");
    });

    [RhinoFact] public void TheCutHintCacheIsInvalidatedWhenAnObjectMovesAcrossThePlane() => host.Run(() =>
    {
        using var doc = RhinoDoc.CreateHeadless(null); RhinoDoc.ActiveDoc = doc;
        doc.ModelAbsoluteTolerance = Tol;
        doc.Objects.AddBrep(Building());
        var mover = doc.Objects.AddBrep(Brep.CreateFromBox(new BoundingBox(new Point3d(20, 20, 0), new Point3d(21, 21, 1))));
        var above = AddViewBox(doc, "Above", PlanFrame(20), 5);
        AddViewBox(doc, "Plan", PlanFrame(1.5), 5);
        AddViewBox(doc, "Section", new Rectangle3d(new Plane(new Point3d(5, 11, -1), -Vector3d.YAxis, Vector3d.ZAxis), new Interval(0, 12), new Interval(0, 5)), 8);
        using var index = new ViewBoxFaceIndex(doc);
        index.Refresh();
        Assert.False(index.Find(above)!.Fill.Cuts);
        var (rebuilds, evaluations) = (index.Rebuilds, index.HintEvaluations);
        index.Refresh(); // nothing changed: nothing is recomputed
        Assert.Equal(evaluations, index.HintEvaluations);

        void Edit(Action edit, Guid id)
        {
            var events = index.EventsSeen;
            edit();
            // Interactive Rhino raises Replace/ModifyAttributes events; headless documents may raise none, so the edit
            // is reported the way the event would report it.
            if (index.EventsSeen == events) index.ObjectChanged(id);
            output.WriteLine($"document events seen for the edit: {index.EventsSeen - events}");
            Assert.True(index.IsDirty);
        }
        // Move the mover up into the "Above" frame across its plane (z 19.5–20.5).
        Edit(() => Assert.True(doc.Objects.Transform(mover, Transform.Translation(-15, -15, 19.5), true) != Guid.Empty), mover);
        Assert.False(index.Find(above)!.Fill.Cuts); // stale until the debounced refresh
        index.Refresh();
        Assert.True(index.Find(above)!.Fill.Cuts);
        Assert.Equal(ViewBoxFill.Cut, index.Find(above)!.Fill.Colour);
        Assert.Equal(rebuilds, index.Rebuilds);                 // incremental, no rebuild
        Assert.Equal(evaluations + 1, index.HintEvaluations);   // only the view box near the old and new bounds

        // Lift it clear of the plane (z 22–23): cobalt again.
        Edit(() => doc.Objects.Transform(mover, Transform.Translation(0, 0, 2.5), true), mover);
        index.Refresh();
        Assert.False(index.Find(above)!.Fill.Cuts);
        // Back across, then hidden: a hidden object never cuts.
        Edit(() => doc.Objects.Transform(mover, Transform.Translation(0, 0, -2.5), true), mover);
        index.Refresh();
        Assert.True(index.Find(above)!.Fill.Cuts);
        Edit(() => doc.Objects.Hide(mover, true), mover);
        index.Refresh();
        Assert.False(index.Find(above)!.Fill.Cuts);
        Edit(() => doc.Objects.Show(mover, true), mover);
        index.Refresh();
        Assert.True(index.Find(above)!.Fill.Cuts);
        // Deleted: cobalt. A layer change rebuilds everything.
        Edit(() => doc.Objects.Delete(mover, true), mover);
        index.Refresh();
        Assert.False(index.Find(above)!.Fill.Cuts);
        index.Invalidate();
        index.Refresh();
        Assert.Equal(rebuilds + 1, index.Rebuilds);
        // An m2d.color edit on the view box itself recolours it without a rebuild.
        var attributes = Attributes(doc, above).Duplicate(); attributes.SetUserString("m2d.color", "#123456");
        Edit(() => doc.Objects.ModifyAttributes(above, attributes, true), above);
        index.Refresh();
        Assert.Equal(new Rgba(0x12, 0x34, 0x56, ViewBoxFill.NoCut.A), index.Find(above)!.Fill.Colour);
        Assert.Equal(rebuilds + 1, index.Rebuilds);
    });

    [RhinoFact] public void TheCutHintMatchesTheBruteForceRuleOnFiftyThousandObjects() => host.Run(() =>
    {
        using var doc = RhinoDoc.CreateHeadless(null); RhinoDoc.ActiveDoc = doc;
        doc.ModelAbsoluteTolerance = Tol;
        // 224 × 224 = 50 176 columns 1 × 1 at spacing 2, heights 1–6 m; 300 view boxes: plans and sections.
        var random = new Random(20261115);
        var timer = Stopwatch.StartNew();
        var boxes = new List<BoundingBox>();
        for (var i = 0; i < 224; i++)
            for (var j = 0; j < 224; j++)
            {
                var b = new BoundingBox(new Point3d(2 * i, 2 * j, 0), new Point3d(2 * i + 1, 2 * j + 1, 1 + random.Next(6)));
                using var column = Extrusion.CreateBoxExtrusion(new Box(b), true);
                doc.Objects.AddExtrusion(column);
                boxes.Add(b);
            }
        var views = new List<Guid>();
        for (var k = 0; k < 300; k++)
        {
            var (x, y, size) = (random.NextDouble() * 440, random.NextDouble() * 440, 2 + random.NextDouble() * 30);
            views.Add(k % 2 == 0
                ? AddViewBox(doc, $"P{k}", PlanFrame(random.NextDouble() * 8, x, y, size), 5)
                : AddViewBox(doc, $"S{k}", new Rectangle3d(new Plane(new Point3d(x + .5, y, -1), Vector3d.YAxis, Vector3d.ZAxis), new Interval(0, size), new Interval(0, 8)), 5));
        }
        output.WriteLine($"setup {timer.ElapsedMilliseconds} ms");
        using var index = new ViewBoxFaceIndex(doc);
        timer.Restart(); index.Refresh(); var rebuild = timer.Elapsed.TotalMilliseconds;
        Assert.Equal(boxes.Count, index.SceneCount);
        var cutting = 0;
        foreach (var id in views)
        {
            var face = index.Find(id)!;
            var p = face.Frame.Plane; var o = face.Frame.Corner(0);
            var front = new FrontFace(new Plane3(new(o.X, o.Y, o.Z), new(p.XAxis.X, p.XAxis.Y, p.XAxis.Z), new(p.YAxis.X, p.YAxis.Y, p.YAxis.Z)), face.Frame.Width, face.Frame.Height);
            var expected = CutHint.AnyCuts(front, boxes.Select(b => new Bounds3(new(b.Min.X, b.Min.Y, b.Min.Z), new(b.Max.X, b.Max.Y, b.Max.Z))), Tol);
            Assert.True(expected == face.Fill.Cuts, $"{face.Name}: index {face.Fill.Cuts}, brute force {expected}");
            if (expected) cutting++;
        }
        Assert.InRange(cutting, 30, 290); // both kinds present
        // One moved object: incremental.
        var first = doc.Objects.First(o => o.Geometry is Extrusion && !ViewBoxes.IsViewBox(o)).Id;
        doc.Objects.Transform(first, Transform.Translation(0, 0, 50), true);
        index.ObjectChanged(first);
        timer.Restart(); index.Refresh(); var incremental = timer.Elapsed.TotalMilliseconds;
        index.Invalidate();
        timer.Restart(); index.Refresh(); var full = timer.Elapsed.TotalMilliseconds;
        output.WriteLine($"{boxes.Count} objects, {views.Count} view boxes ({cutting} cut): first build {rebuild:0} ms, " +
            $"one moved object {incremental:0.0} ms, full rebuild {full:0} ms");
    });

    [RhinoFact] public void AnInvalidColourIsARemarkOnTheComponentAndAValidOneIsNotASetting() => host.Run(() =>
    {
        Assert.True(global::Rhino.PlugIns.PlugIn.LoadPlugIn(GrasshopperId));
        using var doc = TwoBoxes(); RhinoDoc.ActiveDoc = doc;
        var id = doc.Objects.AddSurface(new PlaneSurface(new Plane(new Point3d(-1, -.5, -1), Vector3d.XAxis, Vector3d.ZAxis), new Interval(0, 6), new Interval(0, 3)));
        Assert.True(ViewBoxes.Validate(doc, [id]).Single().Ok);
        void SetColour(string value)
        {
            var attributes = Attributes(doc, id).Duplicate();
            attributes.SetUserString("m2d.color", value);
            doc.Objects.ModifyAttributes(id, attributes, true);
        }
        var byDefault = Draw(new GH_Brep(id));
        Assert.DoesNotContain(byDefault.Messages, m => m.Contains("color", StringComparison.OrdinalIgnoreCase));
        SetColour("#112233");
        var valid = Draw(new GH_Brep(id));
        Assert.DoesNotContain(valid.Messages, m => m.Contains("color", StringComparison.OrdinalIgnoreCase) || m.Contains("Unknown setting"));
        Assert.Contains("Visible=4", valid.Info); // the drawing is unaffected
        SetColour("blueish");
        var invalid = Draw(new GH_Brep(id));
        output.WriteLine(string.Join("\n", invalid.Messages));
        Assert.Contains(invalid.Messages, m => m.Contains("m2d.color = 'blueish' is not a colour") && m.Contains("by type"));
        Assert.DoesNotContain(invalid.Messages, m => m.Contains("Unknown setting"));
        Assert.Contains("Visible=4", invalid.Info);
    });
}
