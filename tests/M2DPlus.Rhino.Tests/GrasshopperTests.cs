using System.Diagnostics;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Parameters;
using Grasshopper.Kernel.Types;
using System.Reflection;
using M2DPlus.Rhino.Services;
using Rhino;
using Rhino.Geometry;
using Xunit;

namespace M2DPlus.Rhino.Tests;

[Collection("Rhino")]
[Trait("Category", "RhinoIntegration")]
public sealed class GrasshopperTests(RhinoHost host)
{
    private sealed class CallbackProgress(Action<double> callback) : IProgress<double>
    { public void Report(double value) => callback(value); }
    private sealed class Cleanup(Action action) : IDisposable { public void Dispose() => action(); }
    [RhinoFact] public void ModelObjectsReferencedGooPrimitivesAndModelViewsReachTheActualComponent() => host.Run(() =>
    {
        Assert.True(global::Rhino.PlugIns.PlugIn.LoadPlugIn(new Guid("b45a29b1-4343-4035-989e-044e8580d9cf")));
        using var rhinoDocument = RhinoDoc.CreateHeadless(null); RhinoDoc.ActiveDoc = rhinoDocument;
        using var document = new GH_Document { Enabled = true };
        using var box = Brep.CreateFromBox(new BoundingBox(Point3d.Origin, new Point3d(2, 2, 2)));
        var id = rhinoDocument.Objects.AddBrep(box);
        var component = Component(document, box);
        using var cleanup = new Cleanup(() => { component.RemovedFromDocument(document); SceneCache.Shared.Clear(); });
        var geometry = (Param_GenericObject)component.Params.Input[0]; geometry.PersistentData.Clear();
        geometry.PersistentData.Append(new global::Grasshopper.Rhinoceros.Model.ModelObject(rhinoDocument.Objects.FindId(id)));
        var referenced = new GH_Brep(); referenced.ReferenceID = id; geometry.PersistentData.Append(referenced);
        using var inlineBox = Brep.CreateFromBox(new BoundingBox(new Point3d(4, 0, 0), new Point3d(5, 1, 1)));
        geometry.PersistentData.Append(new global::Grasshopper.Rhinoceros.Model.ModelObject(rhinoDocument, new global::Rhino.DocObjects.ObjectAttributes(), inlineBox));
        geometry.PersistentData.Append(new GH_Box(new Box(new BoundingBox(new Point3d(6, 0, 0), new Point3d(7, 1, 1)))));
        geometry.PersistentData.Append(new GH_ObjectWrapper(new Sphere(new Point3d(9, 0, 0), .5)));
        using var viewport = new global::Rhino.DocObjects.ViewportInfo(); viewport.IsParallelProjection = true;
        viewport.SetCameraLocation(new Point3d(5, 0, 20)); viewport.SetCameraDirection(-Vector3d.ZAxis); viewport.SetCameraUp(Vector3d.YAxis);
        viewport.SetFrustum(-20, 20, -20, 20, .1, 100);
        var views = (Param_GenericObject)component.Params.Input[1]; views.PersistentData.Clear();
        views.PersistentData.Append(new global::Grasshopper.Rhinoceros.Display.ModelView(viewport));
        document.NewSolution(true, GH_SolutionMode.Silent);
        PumpUntil(() => component.Params.Output[8].VolatileDataCount == 1, () => component.Message,
            () => { if (document.ScheduleDelay >= 0) document.NewSolution(false, GH_SolutionMode.Silent); });
        var info = ((GH_String)Assert.Single(component.Params.Output[8].VolatileData.AllData(true))).Value;
        Assert.DoesNotContain("quarantined=True", info); Assert.DoesNotContain("ModelObject=", info);
        // Threaded HLD fragment counts vary between solves; assert the sources arrived and drew, not a fragment count.
        Assert.Contains("5 sources", info); Assert.True(component.Params.Output[0].VolatileDataCount > 0, info);
    });
    [RhinoFact] public void ChangedInputsCancelImmediatelyAndOnlyLatestViewPublishes() => host.Run(() =>
    {
        Assert.True(global::Rhino.PlugIns.PlugIn.LoadPlugIn(new Guid("b45a29b1-4343-4035-989e-044e8580d9cf")));
        using var rhinoDocument = RhinoDoc.CreateHeadless(null);
        RhinoDoc.ActiveDoc = rhinoDocument;
        GH_Document.EnableSolutions = true;
        using var document = new GH_Document { Enabled = true };
        // An ellipsoid, not a sphere: a sphere is an exact analytic solid and would never enter native HLD.
        using var ellipsoid = NurbsSurface.CreateFromSphere(new Sphere(new Point3d(1, 1, 1), 1));
        Assert.True(ellipsoid.Transform(Transform.Scale(new Plane(new Point3d(1, 1, 1), Vector3d.XAxis, Vector3d.YAxis), 1, 1, 1.5)));
        using var box = ellipsoid.ToBrep();
        var component = Component(document, box);
        using var cleanup = new Cleanup(() => { component.RemovedFromDocument(document); SceneCache.Shared.Clear(); });
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        NativeDrawing.NativeProgress = new CallbackProgress(_ =>
        { entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException(); });
        try
        {
            document.NewSolution(true, GH_SolutionMode.Silent);
            PumpUntil(() => entered.IsSet, () => component.Message);
            var views = (Param_GenericObject)component.Params.Input[1];
            views.PersistentData.Clear(); views.PersistentData.Append(new GH_String("Right"));
            views.ExpireSolution(false);
            Assert.Equal(0, component.Params.Output[0].VolatileDataCount);
            document.NewSolution(false, GH_SolutionMode.Silent);
        }
        finally { NativeDrawing.NativeProgress = null; release.Set(); }
        PumpUntil(() => component.Params.Output[8].VolatileDataCount == 1, () => component.Message,
            () => { if (document.ScheduleDelay >= 0) document.NewSolution(false, GH_SolutionMode.Silent); });
        var info = Assert.Single(component.Params.Output[8].VolatileData.AllData(true));
        Assert.StartsWith("Right:", ((GH_String)info).Value);
        Assert.True(component.Params.Output[0].VolatileDataCount > 0);
        component.DocumentContextChanged(document, GH_DocumentContext.Close);
    });
    [RhinoFact] public void NativeRuntimeAndGhaLoadAndTwoComponentsSharePreparation() => host.Run(() =>
    {
        var expectedRuntime = Environment.GetEnvironmentVariable("M2D_EXPECT_RUNTIME");
        if (expectedRuntime is not null) Assert.Equal(int.Parse(expectedRuntime), Environment.Version.Major);
        Assert.True(global::Rhino.PlugIns.PlugIn.LoadPlugIn(new Guid("b45a29b1-4343-4035-989e-044e8580d9cf")));
        using var rhinoDocument = RhinoDoc.CreateHeadless(null);
        RhinoDoc.ActiveDoc = rhinoDocument;
        GH_Document.EnableSolutions = true;
        using var document = new GH_Document();
        document.Enabled = true;
        global::Grasshopper.Instances.DocumentServer.AddDocument(document);
        using var box = Brep.CreateFromBox(new BoundingBox(Point3d.Origin, new Point3d(2, 2, 2)));
        SceneCache.Shared.Clear();
        var first = Component(document, box); var second = Component(document, box);
        using var cleanup = new Cleanup(() =>
        {
            first.RemovedFromDocument(document); second.RemovedFromDocument(document);
            global::Grasshopper.Instances.DocumentServer.RemoveDocument(document); SceneCache.Shared.Clear();
        });
        var before = SceneCache.Shared.SceneHits;
        var preparations = PreparedScene.TotalPreparations;
        document.NewSolution(true, GH_SolutionMode.Silent);
        PumpUntil(() => first.Params.Output[8].VolatileDataCount == 2 && second.Params.Output[8].VolatileDataCount == 2,
            () => $"first={first.Message}; second={second.Message}; watch={WatchDescription(first)}; active={RhinoDoc.ActiveDoc?.RuntimeSerialNumber}; bound={document.RhinoDocument?.RuntimeSerialNumber}; own={rhinoDocument.RuntimeSerialNumber}; enabled={document.Enabled}/{GH_Document.EnableSolutions}; errors={string.Join(';', first.RuntimeMessages(GH_RuntimeMessageLevel.Error))}",
            () => { if (document.ScheduleDelay >= 0) document.NewSolution(false, GH_SolutionMode.Silent); });
        Assert.True(SceneCache.Shared.SceneHits > before);
        Assert.Equal(1, PreparedScene.TotalPreparations - preparations);
        Assert.True(first.Params.Output[0].VolatileDataCount > 0); Assert.True(second.Params.Output[0].VolatileDataCount > 0);
        Assert.Equal(5, first.Params.Input.Count); Assert.Equal(11, first.Params.Output.Count);
        var visible = (GH_Curve)first.Params.Output[0].VolatileData.AllData(true).First(g => g is GH_Curve);
        var secondCurve = (GH_Curve)second.Params.Output[0].VolatileData.AllData(true).First(g => g is GH_Curve);
        var original = secondCurve.Value.GetBoundingBox(true);
        visible.Value.Transform(Transform.Translation(100, 0, 0));
        Assert.Equal(original, secondCurve.Value.GetBoundingBox(true));
        // One GH box: tree = auto is by object, {view; input path; item} = {view; 0; 0}.
        Assert.All(first.Params.Output[0].VolatileData.Paths, path => Assert.Equal(new[] { path.Indices[0], 0, 0 }, path.Indices));
        Assert.Equal(["{0} 0"], first.Params.Output[10].VolatileData.AllData(true).Cast<GH_String>().Select(s => s.Value).Distinct());
    });
    private static GH_Component Component(GH_Document document, Brep box)
    {
        var assembly = Assembly.LoadFrom(Path.Combine(AppContext.BaseDirectory, "M2DPlus.Grasshopper.gha"));
        var component = (GH_Component)Activator.CreateInstance(assembly.GetType("M2DPlus.Grasshopper.DrawingComponent", true)!)!;
        document.AddObject(component, false);
        ((Param_GenericObject)component.Params.Input[0]).PersistentData.Append(new GH_ObjectWrapper(box));
        ((Param_GenericObject)component.Params.Input[1]).PersistentData.Append(new GH_String("Top"));
        ((Param_GenericObject)component.Params.Input[1]).PersistentData.Append(new GH_String("Front"));
        return component;
    }
    private static string WatchDescription(GH_Component component)
    {
        var watch = (DocumentWatch?)component.GetType().GetField("_watch", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(component);
        return watch is null ? "null" : $"{watch.DocumentSerial}/{watch.Generation}/closed={watch.IsClosed}";
    }
    internal static void PumpUntil(Func<bool> condition, Func<string> diagnostic, Action? pump = null)
    {
        var timer = Stopwatch.StartNew();
        while (!condition() && timer.Elapsed < TimeSpan.FromSeconds(20))
        {
            RhinoApp.Wait(); System.Windows.Forms.Application.DoEvents(); pump?.Invoke(); Thread.Sleep(5);
        }
        Assert.True(condition(), diagnostic());
    }
}
