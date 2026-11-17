using M2DPlus.Core;
using M2DPlus.Rhino.Services;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using Xunit;

namespace M2DPlus.Rhino.Tests;

[Collection("Rhino")]
public sealed class WorkerMemoryRecoveryTests(RhinoHost host)
{
    [RhinoFact] public void CurvedTopologyCountsInstancesAndSurvivesSceneTransport() => host.Run(() =>
    {
        using var doc = RhinoDoc.CreateHeadless(null); using var cache = new SceneCache();
        using var cylinder = new Cylinder(new Circle(Plane.WorldXY, 1), 2).ToBrep(true, true);
        using var box = Brep.CreateFromBox(new BoundingBox(Point3d.Origin, new Point3d(1, 1, 1)));
        int definition = doc.InstanceDefinitions.Add("curved", "", Point3d.Origin, [cylinder], [new ObjectAttributes()]);
        doc.Objects.AddInstanceObject(definition, Transform.Identity);
        doc.Objects.AddInstanceObject(definition, Transform.Translation(3, 0, 0)); doc.Objects.AddBrep(box);
        using var scene = BaselineTests.Capture(doc, cache);
        Assert.Equal(2, scene.Value.Cost.CurvedBrepFaces);
        Assert.Equal(2 * cylinder.Edges.Count, scene.Value.Cost.CurvedBrepEdges);
        using var restored = WorkerCodec.RestoreScene(WorkerCodec.Capture(scene.Value, default));
        // Rhino's MemoryEstimate can change slightly after archive restoration; topology must not.
        Assert.Equal(scene.Value.Cost.CurvedBrepFaces, restored.Cost.CurvedBrepFaces);
        Assert.Equal(scene.Value.Cost.CurvedBrepEdges, restored.Cost.CurvedBrepEdges);
        Assert.Equal(WorkerMemoryPolicy.Required(scene.Value.Cost), WorkerMemoryPolicy.Required(restored.Cost));
    });

    [RhinoFact] public void IdleWorkerIsReapedBeforeRefusingLowFreeCommit() => host.Run(() =>
    {
        using var doc = RhinoDoc.CreateHeadless(null); using var cache = new SceneCache();
        using var box = Brep.CreateFromBox(new BoundingBox(Point3d.Origin, new Point3d(2, 2, 2))); doc.Objects.AddBrep(box);
        using var scene = BaselineTests.Capture(doc, cache);
        DrawingWorkerPool? pool = null; int reads = 0, originalPid = 0;
        var options = new WorkerOptions { Count = 1, MemoryBytes = 6 * WorkerMemoryPolicy.GiB,
            Directory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/M2DPlus.Worker/bin/Debug/net" + System.Environment.Version.Major + ".0-windows")),
            ReadMemory = () => {
                int read = ++reads;
                if (read == 2) return new(64 * WorkerMemoryPolicy.GiB, WorkerMemoryPolicy.GiB);
                if (read == 3) {
                    Assert.Empty(pool!.ProcessIds);
                    Assert.Throws<ArgumentException>(() => System.Diagnostics.Process.GetProcessById(originalPid));
                }
                return new(64 * WorkerMemoryPolicy.GiB, 40 * WorkerMemoryPolicy.GiB);
            } };
        using (pool = new(options))
        {
            var view = ViewRequest.Parse("Top"); var identity = new RequestIdentity(scene.Value.Revision, view.Revision, "memory", 1);
            using var first = pool.SolveAsync(scene.Value, identity, [view], new(), default).GetAwaiter().GetResult();
            originalPid = Assert.Single(pool.ProcessIds);
            using var second = pool.SolveAsync(scene.Value, identity, [view], new(), default).GetAwaiter().GetResult();
            Assert.Equal(3, reads); Assert.NotEqual(originalPid, Assert.Single(pool.ProcessIds));
            Assert.Equal(WorkerCodec.CaptureDrawing(first.Views[0], true), WorkerCodec.CaptureDrawing(second.Views[0], true));
        }
    });
}
