using System.Collections.Concurrent;
using System.Reflection;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Parameters;
using Grasshopper.Kernel.Types;
using M2DPlus.Core;
using M2DPlus.Rhino.Services;
using Rhino;
using Rhino.Geometry;
using Xunit;

namespace M2DPlus.Rhino.Tests;

[Collection("Rhino")]
[Trait("Category", "RhinoIntegration")]
public sealed class DrawingRetryTests(RhinoHost host)
{
    // Inject at the native boundary without crashing a process, exhausting memory or waiting for a deadline.
    private static Exception Failure(string kind) => kind switch
    {
        "crash" => new InvalidOperationException("Worker failed: injected crash"),
        "timeout" => new TimeoutException("Worker solve deadline exceeded"),
        "memory" => new OutOfMemoryException("Worker memory cap exceeded"),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static ViewRequest View(string settings = "engine=native")
    {
        var view = ViewRequest.Parse("Top");
        var resolved = Settings.Resolve([new("top", view.Name, view.Kind, false, 10, .001)], [[settings]], DocumentUnits.Metres);
        Assert.False(resolved.HasErrors);
        return view with { Settings = resolved.Views[0] };
    }

    private static void Populate(RhinoDoc doc)
    {
        using var box = Brep.CreateFromBox(new BoundingBox(Point3d.Origin, new Point3d(2, 3, 4)));
        doc.Objects.AddBrep(box);
    }

    [RhinoTheory]
    [InlineData("crash")]
    [InlineData("timeout")]
    [InlineData("memory")]
    public void CacheRetriesUnchangedFailedDrawingThenReusesSuccessfulVisibility(string kind) => host.Run(() =>
    {
        using var doc = RhinoDoc.CreateHeadless(null); Populate(doc);
        using var scenes = new SceneCache(); using var scene = BaselineTests.Capture(doc, scenes);
        using var drawings = new DrawingCache();
        var attempts = 0;
        var previous = NativeDrawing.NativeStarting;
        NativeDrawing.NativeStarting = () => { if (Interlocked.Increment(ref attempts) == 1) throw Failure(kind); };
        DrawingBatch Draw(ViewRequest view, long generation, params Plane[] targets) => drawings.SolveAsync(scene.Share(),
            new(scene.Value.Revision, "top", "settings", generation), [view], new(), targets, default).GetAwaiter().GetResult();
        try
        {
            using var failed = Draw(View(), 1);
            Assert.True(failed.Complete); Assert.Equal(Failure(kind).Message, Assert.Single(failed.Views).Error);
            Assert.Equal(1, attempts);

            using var recovered = Draw(View(), 2);
            Assert.True(recovered.Complete); Assert.Null(Assert.Single(recovered.Views).Error);
            Assert.NotEmpty(recovered.Views[0].Items); Assert.Equal(2, attempts);

            // Both ordinary repeats and presentation-only changes retain the successful raw solve.
            using var repeat = Draw(View(), 3);
            Assert.Equal(2, attempts); Assert.Null(repeat.Views[0].Error); Assert.Equal(3, repeat.Identity.Generation);
            using var styled = Draw(View("engine=native\nscale=1:50"), 4, new Plane(new Point3d(20, 30, 0), Vector3d.ZAxis));
            Assert.Equal(2, attempts); Assert.Null(styled.Views[0].Error); Assert.Equal(50, styled.Views[0].Settings!.Scale);
            Assert.NotEqual(recovered.Views[0].Frame, styled.Views[0].Frame);
            drawings.Clear();
            using var copy = styled.Views[0].Items[0].CopyGeometry();
            Assert.True(copy.IsValid);
        }
        finally { NativeDrawing.NativeStarting = previous; }
    });

    private sealed class Phases : IProgress<JobProgress>
    {
        public ConcurrentBag<JobPhase> Seen { get; } = new();
        public void Report(JobProgress value) => Seen.Add(value.Phase);
    }

    [RhinoFact] public void CachePublishesCompletePartialBatchesButRetriesThem() => host.Run(() =>
    {
        using var doc = RhinoDoc.CreateHeadless(null); Populate(doc);
        using var scenes = new SceneCache(); using var scene = BaselineTests.Capture(doc, scenes);
        using var drawings = new DrawingCache();
        ViewRequest[] views = [View(), new("invalid", default, "authored view error")];
        DrawingBatch Draw(Phases phases) => drawings.SolveAsync(scene.Share(), new(scene.Value.Revision, "views", "settings", 1),
            views, new(), [], default, phases).GetAwaiter().GetResult();
        using var first = Draw(new());
        Assert.True(first.Complete); Assert.Null(first.Views[0].Error); Assert.NotEmpty(first.Views[0].Items);
        Assert.Equal("authored view error", first.Views[1].Error);
        var hash = ExclusionTests.Hash(first.Views[0]);
        var phases = new Phases(); using var next = Draw(phases);
        Assert.Contains(JobPhase.Drawing, phases.Seen); // A poisoned cache hit would report only Output.
        Assert.True(next.Complete); Assert.Null(next.Views[0].Error); Assert.NotEmpty(next.Views[0].Items);
        Assert.Equal("authored view error", next.Views[1].Error);
        drawings.Clear();
        Assert.Equal(hash, ExclusionTests.Hash(first.Views[0])); // Failure eviction must not dispose published good views.
    });

    private static GH_Component Component(GH_Document document, bool partial = false)
    {
        var assembly = Assembly.LoadFrom(Path.Combine(AppContext.BaseDirectory, "M2DPlus.Grasshopper.gha"));
        var component = (GH_Component)Activator.CreateInstance(assembly.GetType("M2DPlus.Grasshopper.DrawingComponent", true)!)!;
        document.AddObject(component, false);
        var views = (Param_GenericObject)component.Params.Input[1];
        views.PersistentData.Append(new GH_String("Top"));
        if (partial) views.PersistentData.Append(new GH_String("missing retry-test view"));
        ((Param_String)component.Params.Input[3]).PersistentData.Append(new GH_String("engine=native"));
        return component;
    }

    private static object? Field(GH_Component component, string name) => component.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(component);
    private static void Pump(GH_Document document, GH_Component component, Func<bool> until, int views = 1) =>
        GrasshopperTests.PumpUntil(() => until() && component.Params.Output[8].VolatileDataCount == views,
            () => component.Message + ": " + string.Join(";", component.RuntimeMessages(GH_RuntimeMessageLevel.Error)),
            () => { if (document.ScheduleDelay >= 0) document.NewSolution(false, GH_SolutionMode.Silent); });

    [RhinoTheory]
    [InlineData("crash")]
    [InlineData("timeout")]
    [InlineData("memory")]
    public void RunNowRetriesUnchangedFailureAndThenRestoresSuccessfulDrawing(string kind) => host.Run(() =>
    {
        Assert.True(global::Rhino.PlugIns.PlugIn.LoadPlugIn(new Guid("b45a29b1-4343-4035-989e-044e8580d9cf")));
        using var doc = RhinoDoc.CreateHeadless(null); RhinoDoc.ActiveDoc = doc; Populate(doc);
        GH_Document.EnableSolutions = true;
        using var document = new GH_Document { Enabled = true };
        var component = Component(document);
        dynamic ui = component.GetType().GetProperty("Ui")!.GetValue(component)!;
        var attempts = 0;
        var previous = NativeDrawing.NativeStarting;
        NativeDrawing.NativeStarting = () => { if (Interlocked.Increment(ref attempts) == 1) throw Failure(kind); };
        try
        {
            document.NewSolution(true, GH_SolutionMode.Silent);
            Pump(document, component, () => Field(component, "_drawing") is not null);
            var failed = Assert.IsType<DrawingBatch>(Field(component, "_drawing"));
            Assert.True(failed.Complete); Assert.Equal(Failure(kind).Message, Assert.Single(failed.Views).Error);
            Assert.Contains(Failure(kind).Message, component.RuntimeMessages(GH_RuntimeMessageLevel.Error));
            var key = Field(component, "_drawingRunKey");
            var captures = (int)Field(component, "_captureCount")!;
            var restores = (int)Field(component, "_restoreCount")!;

            ui.Execute("run", false);
            Pump(document, component, () => Field(component, "_drawing") is { } next && !ReferenceEquals(failed, next));
            var recovered = Assert.IsType<DrawingBatch>(Field(component, "_drawing"));
            Assert.Null(Assert.Single(recovered.Views).Error); Assert.Equal(2, attempts);
            Assert.Equal(key, Field(component, "_drawingRunKey"));
            Assert.Equal(captures, (int)Field(component, "_captureCount")!);
            Assert.Equal(restores, (int)Field(component, "_restoreCount")!);
            Assert.True(component.Params.Output[0].VolatileDataCount > 0);
            Assert.Empty(component.RuntimeMessages(GH_RuntimeMessageLevel.Error));

            ui.Execute("run", false);
            Pump(document, component, () => (int)Field(component, "_restoreCount")! == restores + 1);
            Assert.Same(recovered, Field(component, "_drawing")); Assert.Equal(2, attempts);
            Assert.Equal(captures, (int)Field(component, "_captureCount")!);
        }
        finally
        {
            NativeDrawing.NativeStarting = previous;
            component.RemovedFromDocument(document); SceneCache.Shared.Clear();
        }
    });

    [RhinoFact] public void RunNowKeepsGoodViewsInPartialPublicationAndDoesNotRestoreFailedBatch() => host.Run(() =>
    {
        Assert.True(global::Rhino.PlugIns.PlugIn.LoadPlugIn(new Guid("b45a29b1-4343-4035-989e-044e8580d9cf")));
        using var doc = RhinoDoc.CreateHeadless(null); RhinoDoc.ActiveDoc = doc; Populate(doc);
        GH_Document.EnableSolutions = true;
        using var document = new GH_Document { Enabled = true };
        var component = Component(document, partial: true);
        dynamic ui = component.GetType().GetProperty("Ui")!.GetValue(component)!;
        try
        {
            document.NewSolution(true, GH_SolutionMode.Silent);
            Pump(document, component, () => Field(component, "_drawing") is not null, views: 2);
            var failed = Assert.IsType<DrawingBatch>(Field(component, "_drawing"));
            Assert.True(failed.Complete); Assert.Null(failed.Views[0].Error); Assert.NotNull(failed.Views[1].Error);
            Assert.Equal("Partial · 1/2 drawings", component.Message);
            Assert.True(component.Params.Output[0].VolatileDataCount > 0);
            var captures = (int)Field(component, "_captureCount")!;
            var restores = (int)Field(component, "_restoreCount")!;
            ui.Execute("run", false);
            Pump(document, component, () => Field(component, "_drawing") is { } next && !ReferenceEquals(next, failed), views: 2);
            Assert.Equal(restores, (int)Field(component, "_restoreCount")!);
            Assert.Equal(captures, (int)Field(component, "_captureCount")!);
            Assert.Equal("Partial · 1/2 drawings", component.Message);
            Assert.True(component.Params.Output[0].VolatileDataCount > 0);
        }
        finally { component.RemovedFromDocument(document); SceneCache.Shared.Clear(); }
    });
}
