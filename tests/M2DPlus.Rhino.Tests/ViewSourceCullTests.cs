using M2DPlus.Core;
using M2DPlus.Rhino.Services;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using Xunit;

namespace M2DPlus.Rhino.Tests;

[Collection("Rhino")]
public sealed class ViewSourceCullTests(RhinoHost host)
{
    private static Rectangle3d Crop => new(Plane.WorldXY, new Interval(-1, 1), new Interval(-1, 1));

    [RhinoFact] public void ScopeAndCropOffKeepSourcesAndTightRotatedBoundsCull() => host.Run(() =>
    {
        using var outside = new LineCurve(new Point3d(4, 0, -20), new Point3d(5, 0, -19));
        var sources = new[] { new PreparedLeaf("outside", [0], outside) };
        var scene = outside.GetBoundingBox(true);
        var named = ViewRequest.Parse("Top") with { Named = new(Plane.WorldXY, Crop.X, Crop.Y) };
        Assert.Empty(ViewSourceCull.Filter(sources, named, scene, .001, default));
        Assert.Same(sources, ViewSourceCull.Filter(sources, named with { Named = named.Named! with { Crop = false } }, scene, .001, default));
        var cut = ViewRequest.Parse(Plane.WorldXY);
        cut = cut with { Cut = cut.Cut! with { SourceDepth = 10 } };
        Assert.Empty(ViewSourceCull.Filter(sources, cut, scene, .001, default));
        Assert.Same(sources, ViewSourceCull.Filter(sources, cut with { Cut = cut.Cut! with { FarClip = FarClipMode.None } }, scene, .001, default));
        Assert.Same(sources, ViewSourceCull.Filter(sources, cut with { Cut = cut.Cut! with { ScopeEnabled = true } }, scene, .001, default));
        var tilted = new Plane(Point3d.Origin, new Vector3d(1, 1, 0), Vector3d.ZAxis);
        using var line = new LineCurve(tilted.PointAt(2, -10), tilted.PointAt(2, 10));
        var tiltedCrop = new Rectangle3d(tilted, Crop.X, Crop.Y);
        Assert.Empty(ViewSourceCull.Filter([new("tilted", [0], line)], ViewRequest.Parse(tiltedCrop), line.GetBoundingBox(true), .001, default));
    });

    [RhinoFact] public void TransformedNestedBlocksCullByTheirOwnedDefinitionLeaves() => host.Run(() =>
    {
        using var document = RhinoDoc.CreateHeadless(null);
        using var shape = Brep.CreateFromBox(new BoundingBox(new Point3d(-.2, -.2, -2), new Point3d(.2, .2, -1)));
        int leaf = document.InstanceDefinitions.Add("leaf", "", Point3d.Origin, [shape], [new ObjectAttributes()]);
        using var reference = new InstanceReferenceGeometry(document.InstanceDefinitions[leaf].Id, Transform.Translation(3, 0, 0));
        int nested = document.InstanceDefinitions.Add("nested", "", Point3d.Origin, [reference], [new ObjectAttributes()]);
        document.Objects.AddInstanceObject(nested, Transform.Translation(-3, 0, 0));
        document.Objects.AddInstanceObject(nested, Transform.Scale(Plane.WorldXY, -2, 3, 1));
        using var cache = new SceneCache();
        using var scene = BaselineTests.Capture(document, cache);
        var sources = scene.Value.Prepare(default);
        Assert.Equal(2, sources.Count);
        var kept = Assert.Single(ViewSourceCull.Filter(sources, ViewRequest.Parse(Crop), scene.Value.Bounds, .001, default));
        Assert.InRange(kept.Geometry.GetBoundingBox(true).Center.X, -.001, .001);
    });

    [RhinoTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void SharedRoutePreservesExactGeometryAndRecordsProgressAndWorkerState(bool native) => host.Run(() =>
    {
        using var document = RhinoDoc.CreateHeadless(null);
        using var inside = Brep.CreateFromBox(new BoundingBox(new Point3d(-.5, -.5, -2), new Point3d(.5, .5, -1)));
        using var outside = Brep.CreateFromBox(new BoundingBox(new Point3d(4, 0, -2), new Point3d(5, 1, -1)));
        using var far = Brep.CreateFromBox(new BoundingBox(new Point3d(0, 0, -20), new Point3d(1, 1, -19)));
        using var cache = new SceneCache();
        using var scene = BaselineTests.Capture(document, cache, new[] { inside, outside, far }.Select((g, i) => new CaptureInput(g, [i], "source/" + i)));
        var view = ViewRequest.Parse(new Box(Plane.WorldXY, Crop.X, Crop.Y, new Interval(-10, 0)));
        view = view with { Settings = Settings.Resolve([new("cull", "cull", ViewSourceKind.Box, true, 10, .001)],
            [[$"engine={(native ? "native" : "exact")}", "hidden=on", "beyond=off"]], DocumentUnits.Metres).Views[0] };
        var steps = new List<string?>();
        var progress = new InlineProgress(p => steps.Add(p.Step));
        DrawingBatch Solve(bool enabled) => NativeDrawing.SolveBorrowedAsync(scene.Value, new(scene.Value.Revision, view.Revision, "test", 1),
            [view], new() { CullSources = enabled }, default, progress).GetAwaiter().GetResult();
        using var off = Solve(false); using var on = Solve(true);
        HldFixtureComparison.Equal(off.Views[0],on.Views[0]);
        Assert.Contains("culled 2 sources outside the view", on.Views[0].Info);
        Assert.Contains(ViewSteps.Culling, steps);
        Assert.True(on.Views[0].Phases.Seconds.ContainsKey("source culling"));
        using var restored = WorkerCodec.RestoreDrawing(WorkerCodec.CaptureDrawing(on.Views[0]));
        Assert.Equal(on.Views[0].Info, restored.Info);
        Assert.False(WorkerCodec.Decode<WorkerCodec.Request>(WorkerCodec.Encode(new WorkerCodec.Request(default, 0, view, CullSources: false))).CullSources);
    });

    private sealed class InlineProgress(Action<JobProgress> report) : IProgress<JobProgress>
    { public void Report(JobProgress value) => report(value); }
}

