using System.Reflection;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Parameters;
using Grasshopper.Kernel.Types;
using M2DPlus.Core.Bake;
using M2DPlus.Rhino.Services;
using Rhino;
using Rhino.Geometry;
using Xunit;

namespace M2DPlus.Rhino.Tests;

[Collection("Rhino")]
[Trait("Category", "RhinoIntegration")]
public sealed class M3ComponentTests(RhinoHost host)
{
    [RhinoFact] public void ActualComponentReusesVisibilityPlacesOutputsAndWiresBakeCopyAndClear() => host.Run(() =>
    {
        Assert.True(global::Rhino.PlugIns.PlugIn.LoadPlugIn(new Guid("b45a29b1-4343-4035-989e-044e8580d9cf")));
        using var doc = RhinoDoc.CreateHeadless(null); doc.ModelAbsoluteTolerance = .00001; RhinoDoc.ActiveDoc = doc;
        doc.Objects.AddLine(new Point3d(0, 0, -1), new Point3d(4, 0, -1));
        using var gh = new GH_Document { Enabled = true };
        var assembly = Assembly.LoadFrom(Path.Combine(AppContext.BaseDirectory, "M2DPlus.Grasshopper.gha"));
        var component = (GH_Component)Activator.CreateInstance(assembly.GetType("M2DPlus.Grasshopper.DrawingComponent", true)!)!;
        gh.AddObject(component, false);
        dynamic ui = component.GetType().GetProperty("Ui")!.GetValue(component)!;
        ((Param_GenericObject)component.Params.Input[1]).PersistentData.Append(new GH_Plane(Plane.WorldXY));
        ((Param_String)component.Params.Input[3]).PersistentData.Append(new GH_String("beyond=off\nname=Test drawing"));
        try
        {
            Solve(); var calls = NativeDrawing.VisibilitySolves;
            var target = (Param_Plane)component.Params.Input[2];
            target.PersistentData.Append(new GH_Plane(new Plane(new Point3d(30, 40, 50), Vector3d.YAxis, Vector3d.ZAxis)));
            target.ExpireSolution(false); component.ExpireSolution(false); Solve(); Assert.Equal(calls, NativeDrawing.VisibilitySolves);
            var curve = ((GH_Curve)Assert.Single(component.Params.Output[0].VolatileData.AllData(true))).Value;
            Assert.Equal(new Point3d(30, 40, 50), curve.PointAtStart); Assert.Equal(new Point3d(30, 44, 50), curve.PointAtEnd);
            ui.Execute("set:scale=1:50", false); Solve(); Assert.Equal(calls, NativeDrawing.VisibilitySolves);
            ui.Execute("set:tree=object", false); Solve(); Assert.Equal(calls, NativeDrawing.VisibilitySolves);
            Assert.All(component.Params.Output[0].VolatileData.Paths, path => { Assert.Equal(3, path.Length); Assert.Equal(0, path.Indices[2]); });
            ui.Execute("preview:hidden", false); Assert.Equal(calls, NativeDrawing.VisibilitySolves);
            ui.Execute("bake", false);
            Assert.Single(Owned("Test drawing"));
            ui.Execute("bake-one:0", false); Assert.Single(Owned("Test drawing"));
            ui.Execute("bake", true); Assert.Single(Owned("Test drawing (2)"));
            ui.Execute("bake", false); Assert.Single(Owned("Test drawing")); Assert.Single(Owned("Test drawing (2)"));
            ui.Execute("clear-cache", false); Solve(); Assert.True(NativeDrawing.VisibilitySolves > calls);
        }
        finally { component.RemovedFromDocument(gh); SceneCache.Shared.Clear(); }

        Guid[] Owned(string name) => doc.Objects.Where(o => o.Attributes.GetUserString(BakeTags.Bake) == BakeTags.Owner(component.InstanceGuid, name)).Select(o => o.Id).ToArray();
        void Solve()
        {
            gh.NewSolution(false, GH_SolutionMode.Silent);
            GrasshopperTests.PumpUntil(() => component.Params.Output[8].VolatileDataCount == 1,
                () => component.Message + ": " + string.Join(";", component.RuntimeMessages(GH_RuntimeMessageLevel.Error)),
                () => { if (gh.ScheduleDelay >= 0) gh.NewSolution(false, GH_SolutionMode.Silent); });
        }
    });
}
