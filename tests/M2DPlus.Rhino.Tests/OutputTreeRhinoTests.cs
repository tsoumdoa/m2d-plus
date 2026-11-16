using System.Reflection;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Parameters;
using Grasshopper.Kernel.Types;
using M2DPlus.Core.Bake;
using M2DPlus.Rhino.Services;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using Xunit;
using Xunit.Abstractions;

namespace M2DPlus.Rhino.Tests;

/// <summary>tree = auto | layer | object on the actual component (owner decision 2026-11-15, UX §1.3): branches per
/// layer with block parts on their own layers, branches per GH input item, the Key output, Cut/Hatch region alignment,
/// zero HLD calls for a tree change, and bake on its existing layer scheme.</summary>
[Collection("Rhino")]
[Trait("Category", "RhinoIntegration")]
public sealed class OutputTreeRhinoTests(RhinoHost host, ITestOutputHelper output)
{
    private const int Visible = 0, Cut = 4, Hatch = 5, Info = 8, Key = 10;

    private static GH_Component NewComponent(GH_Document gh)
    {
        var assembly = Assembly.LoadFrom(Path.Combine(AppContext.BaseDirectory, "M2DPlus.Grasshopper.gha"));
        var component = (GH_Component)Activator.CreateInstance(assembly.GetType("M2DPlus.Grasshopper.DrawingComponent", true)!)!;
        gh.AddObject(component, false);
        return component;
    }

    private static void Solve(GH_Document gh, GH_Component component, int views)
    {
        gh.NewSolution(false, GH_SolutionMode.Silent);
        GrasshopperTests.PumpUntil(() => component.Params.Output[Info].VolatileDataCount == views,
            () => component.Message + ": " + string.Join(";", component.RuntimeMessages(GH_RuntimeMessageLevel.Error)),
            () => { if (gh.ScheduleDelay >= 0) gh.NewSolution(false, GH_SolutionMode.Silent); });
        Assert.Empty(component.RuntimeMessages(GH_RuntimeMessageLevel.Error));
    }

    private static string[] Paths(GH_Component component, int index, int view) =>
        component.Params.Output[index].VolatileData.Paths.Where(p => p.Indices[0] == view && component.Params.Output[index].VolatileData.get_Branch(p).Count > 0)
            .Select(p => p.ToString()).Distinct().Order(StringComparer.Ordinal).ToArray();

    private static Dictionary<string, string> Keys(GH_Component component, int view)
    {
        var data = component.Params.Output[Key].VolatileData;
        return data.Paths.Where(p => p.Indices[0] == view).ToDictionary(p => p.ToString(),
            p => ((GH_String)Assert.Single(data.get_Branch(p).Cast<IGH_Goo>())).Value);
    }

    private static string InfoText(GH_Component component, int view) =>
        ((GH_String)component.Params.Output[Info].VolatileData.get_Branch(new GH_Path(view))[0]!).Value;

    private static Guid AddBox(RhinoDoc doc, double x, double y, int layer, string? name = null)
    {
        using var attributes = new ObjectAttributes { LayerIndex = layer, Name = name };
        return doc.Objects.AddBrep(Brep.CreateFromBox(new BoundingBox(new Point3d(x, y, 0), new Point3d(x + 1, y + 1, 1))), attributes);
    }

    [RhinoFact] public void DocumentModeBranchesPerLayerSplitsBlockPartsAndAnObjectSwitchMakesNoSolve() => host.Run(() =>
    {
        Assert.True(global::Rhino.PlugIns.PlugIn.LoadPlugIn(new Guid("b45a29b1-4343-4035-989e-044e8580d9cf")));
        using var doc = RhinoDoc.CreateHeadless(null); doc.ModelAbsoluteTolerance = .001; RhinoDoc.ActiveDoc = doc;
        var walls = doc.Layers.Add("Walls", System.Drawing.Color.Black);
        var exterior = doc.Layers.Add(new Layer { Name = "Exterior", ParentLayerId = doc.Layers[walls].Id });
        var columns = doc.Layers.Add("Columns", System.Drawing.Color.Black);
        var slabs = doc.Layers.Add("Slabs", System.Drawing.Color.Black);
        var frames = doc.Layers.Add("Frames", System.Drawing.Color.Black);
        var glass = doc.Layers.Add("Panes", System.Drawing.Color.Black);
        var wall = AddBox(doc, 0, 0, exterior, "Wall W");
        var column = AddBox(doc, 3, 0, columns);
        var slab = AddBox(doc, 6, 0, slabs);
        // A block whose parts sit on Frames and on Panes; the instance itself is on the default layer.
        using var frame = Brep.CreateFromBox(new BoundingBox(new Point3d(0, 0, 0), new Point3d(1, .2, 1)));
        using var pane = Brep.CreateFromBox(new BoundingBox(new Point3d(0, .5, 0), new Point3d(1, .7, 1)));
        using var onFrames = new ObjectAttributes { LayerIndex = frames }; using var onGlass = new ObjectAttributes { LayerIndex = glass };
        var definition = doc.InstanceDefinitions.Add("Window", "", Point3d.Origin, new GeometryBase[] { frame, pane }, new[] { onFrames, onGlass });
        using var instanceAttributes = new ObjectAttributes { Name = "W1", LayerIndex = doc.Layers.CurrentLayerIndex };
        var instance = doc.Objects.AddInstanceObject(definition, Transform.Translation(9, 0, 0), instanceAttributes);
        Assert.NotEqual(Guid.Empty, instance);

        using var gh = new GH_Document { Enabled = true };
        var component = NewComponent(gh);
        dynamic ui = component.GetType().GetProperty("Ui")!.GetValue(component)!;
        var view = (Param_GenericObject)component.Params.Input[1];
        view.PersistentData.Append(new GH_String("Top"));
        view.PersistentData.Append(new GH_Plane(new Plane(new Point3d(0, 0, .5), Vector3d.ZAxis))); // a plan section
        ((Param_String)component.Params.Input[3]).PersistentData.Append(new GH_String("beyond = off"));
        try
        {
            Assert.Equal(11, component.Params.Output.Count);
            Solve(gh, component, 2); var calls = NativeDrawing.VisibilitySolves;
            output.WriteLine(InfoText(component, 0));
            Assert.Contains("Output tree: by layer (auto: document mode)", InfoText(component, 0));

            // By layer: {view; layer index}, block parts on their own layers, nothing on the instance's layer.
            var layers = new[] { exterior, columns, slabs, frames, glass }.Order().Select(i => $"{{0;{i}}}").ToArray();
            Assert.Equal(layers, Paths(component, Visible, 0));
            var keys = Keys(component, 0);
            Assert.Equal(layers, keys.Keys.Order(StringComparer.Ordinal));
            Assert.Equal("Walls::Exterior", keys[$"{{0;{exterior}}}"]); Assert.Equal("Columns", keys[$"{{0;{columns}}}"]);
            Assert.Equal("Slabs", keys[$"{{0;{slabs}}}"]); Assert.Equal("Frames", keys[$"{{0;{frames}}}"]);
            Assert.Equal("Panes", keys[$"{{0;{glass}}}"]);
            Assert.DoesNotContain($"{{0;{doc.Layers.CurrentLayerIndex}}}", keys.Keys);

            // The section: one region per object at {view; layer; region}; each Hatch branch has exactly one item.
            var cut = component.Params.Output[Cut].VolatileData; var hatch = component.Params.Output[Hatch].VolatileData;
            var regions = new[] { exterior, columns, slabs, frames, glass }.Order().Select(i => $"{{1;{i};0}}").ToArray();
            Assert.Equal(regions, Paths(component, Cut, 1));
            Assert.Equal(cut.Paths.Select(p => p.ToString()), hatch.Paths.Select(p => p.ToString()));
            Assert.All(hatch.Paths, p => Assert.Single(hatch.get_Branch(p).Cast<object?>()));
            Assert.Equal(new[] { exterior, columns, slabs, frames, glass }.Order().Select(i => $"{{1;{i}}}"), Keys(component, 1).Keys.Order(StringComparer.Ordinal));

            // By object: one branch per top-level object, in object-id order; the instance is one object, labelled
            // with its id and name. Grouping is presentation: no HLD call.
            ui.Execute("set:tree=object", false); Solve(gh, component, 2);
            Assert.Equal(calls, NativeDrawing.VisibilitySolves);
            var ordered = new[] { wall, column, slab, instance }.Order().ToArray();
            var objectKeys = Keys(component, 0);
            Assert.Equal(Enumerable.Range(0, 4).Select(i => $"{{0;0;{i}}}"), objectKeys.Keys.Order(StringComparer.Ordinal));
            Assert.Equal(ordered.Select(id => id == instance ? $"{id} W1" : id == wall ? $"{id} Wall W" : id.ToString()),
                Enumerable.Range(0, 4).Select(i => objectKeys[$"{{0;0;{i}}}"]));
            // Cuts by object: {view; 0; object; region}; the instance's frame and pane are two regions of one object.
            var item = Array.IndexOf(ordered, instance);
            Assert.Equal(Enumerable.Range(0, 4).Select(i => $"{{1;0;{i};0}}").Append($"{{1;0;{item};1}}").Order(StringComparer.Ordinal), Paths(component, Cut, 1));
            Assert.Equal(cut.Paths.Select(p => p.ToString()), hatch.Paths.Select(p => p.ToString()));
            Assert.All(hatch.Paths, p => Assert.Single(hatch.get_Branch(p).Cast<object?>()));

            ui.Execute("set:tree=layer", false); Solve(gh, component, 2);
            ui.Execute("set:tree=auto", false); Solve(gh, component, 2);
            Assert.Equal(calls, NativeDrawing.VisibilitySolves);
            Assert.Equal(layers, Paths(component, Visible, 0));

            // Bake (default bake = view-layer): source layer, then line type, with block parts on their own layers.
            ui.Execute("bake", false);
            var baked = doc.Objects.Where(o => o.Attributes.GetUserString(BakeTags.Bake)?.StartsWith(component.InstanceGuid.ToString(), StringComparison.Ordinal) == true)
                .Select(o => doc.Layers[o.Attributes.LayerIndex].FullPath).ToHashSet(StringComparer.Ordinal);
            output.WriteLine(string.Join("\n", baked.Order(StringComparer.Ordinal)));
            Assert.Contains(baked, p => p.EndsWith("::Frames::Visible", StringComparison.Ordinal));
            Assert.Contains(baked, p => p.EndsWith("::Panes::Visible", StringComparison.Ordinal));
            Assert.Contains(baked, p => p.EndsWith("::Walls::Exterior::Visible", StringComparison.Ordinal));
            Assert.Contains(baked, p => p.EndsWith("::Columns::Cut", StringComparison.Ordinal));
            Assert.Contains(baked, p => p.EndsWith("::Frames::Hatch", StringComparison.Ordinal));
        }
        finally { component.RemovedFromDocument(gh); SceneCache.Shared.Clear(); }
    });

    [RhinoFact] public void ReferencedInputIsByLayerAndGrasshopperBoxesAreOneBranchPerInputItem() => host.Run(() =>
    {
        Assert.True(global::Rhino.PlugIns.PlugIn.LoadPlugIn(new Guid("b45a29b1-4343-4035-989e-044e8580d9cf")));
        using var doc = RhinoDoc.CreateHeadless(null); doc.ModelAbsoluteTolerance = .001; RhinoDoc.ActiveDoc = doc;
        var columns = doc.Layers.Add("Columns", System.Drawing.Color.Black);
        var referenced = AddBox(doc, 20, 0, columns, "C1");

        using var gh = new GH_Document { Enabled = true };
        var component = NewComponent(gh);
        dynamic ui = component.GetType().GetProperty("Ui")!.GetValue(component)!;
        ((Param_GenericObject)component.Params.Input[1]).PersistentData.Append(new GH_String("Top"));
        var geometry = (Param_GenericObject)component.Params.Input[0];
        try
        {
            // Every item a referenced Rhino object: auto is by layer.
            geometry.PersistentData.Append(new GH_Guid(referenced), new GH_Path(0));
            Solve(gh, component, 1);
            Assert.Contains("Output tree: by layer (auto: every Geometry item is a referenced Rhino object)", InfoText(component, 0));
            Assert.Equal([$"{{0;{columns}}}"], Paths(component, Visible, 0));
            Assert.Equal("Columns", Keys(component, 0)[$"{{0;{columns}}}"]);

            // Grasshopper boxes in a two-branch tree: one branch per box at {view; input path; item}.
            geometry.PersistentData.Clear();
            geometry.PersistentData.Append(new GH_Brep(Brep.CreateFromBox(new BoundingBox(0, 0, 0, 1, 1, 1))), new GH_Path(0));
            geometry.PersistentData.Append(new GH_Brep(Brep.CreateFromBox(new BoundingBox(3, 0, 0, 4, 1, 1))), new GH_Path(0));
            geometry.PersistentData.Append(new GH_Brep(Brep.CreateFromBox(new BoundingBox(6, 0, 0, 7, 1, 1))), new GH_Path(1));
            geometry.ExpireSolution(false); component.ExpireSolution(false); Solve(gh, component, 1);
            Assert.Contains("Output tree: by object (auto: Geometry holds Grasshopper geometry)", InfoText(component, 0));
            string[] boxes = ["{0;0;0}", "{0;0;1}", "{0;1;0}"];
            Assert.Equal(boxes, Paths(component, Visible, 0));
            Assert.Equal(new Dictionary<string, string> { ["{0;0;0}"] = "{0} 0", ["{0;0;1}"] = "{0} 1", ["{0;1;0}"] = "{1} 0" }, Keys(component, 0));
            var calls = NativeDrawing.VisibilitySolves;

            // Explicit by layer: Grasshopper geometry has no Rhino layer, so it shares the reserved "(no layer)" branch.
            ui.Execute("set:tree=layer", false); Solve(gh, component, 1);
            Assert.Equal(calls, NativeDrawing.VisibilitySolves);
            Assert.Equal([$"{{0;{int.MaxValue}}}"], Paths(component, Visible, 0));
            Assert.Equal("(no layer)", Assert.Single(Keys(component, 0)).Value);
            ui.Execute("set:tree=auto", false);

            // Mixed input (a referenced object beside the boxes) falls back to by object; the object is labelled by id and name.
            geometry.PersistentData.Append(new GH_Guid(referenced), new GH_Path(1));
            geometry.ExpireSolution(false); component.ExpireSolution(false); Solve(gh, component, 1);
            Assert.Equal([.. boxes, "{0;1;1}"], Paths(component, Visible, 0));
            Assert.Equal($"{referenced} C1", Keys(component, 0)["{0;1;1}"]);
        }
        finally { component.RemovedFromDocument(gh); SceneCache.Shared.Clear(); }
    });
}
