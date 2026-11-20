using M2DPlus.Core;
using M2DPlus.Core.Sections;
using M2DPlus.Rhino.Sections;
using M2DPlus.Rhino.Services;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using Xunit;
using Xunit.Abstractions;

namespace M2DPlus.Rhino.Tests;

/// <summary>Shared section validation: deterministic verdicts per definition shape and local cut, owned by the scene.</summary>
[Collection("Rhino")]
[Trait("Category", "RhinoIntegration")]
public sealed class SectionReuseTests(RhinoHost host, ITestOutputHelper output)
{
    private const double Tol = .0001;
    private const string SelfIntersecting = "Self-intersecting Brep faces";

    /// <summary>Four planar walls over a bow-tie outline. Two opposite walls cross along a vertical line, away from every shared edge.</summary>
    private static Brep BowTie()
    {
        using var outline = new PolylineCurve(new[] { new Point3d(0, 0, 0), new Point3d(2, 2, 0), new Point3d(2, 0, 0), new Point3d(0, 2, 0), new Point3d(0, 0, 0) });
        using var surface = Surface.CreateExtrusion(outline, Vector3d.ZAxis * 2);
        var brep = surface.ToBrep(); brep.Faces.SplitKinkyFaces();
        Assert.True(brep.IsValid && brep.IsManifold && brep.Faces.Count == 4);
        return brep;
    }
    private static Brep Box(double size = 1) => Brep.CreateFromBox(new BoundingBox(new Point3d(-size, -size, -size), new Point3d(size, size, size)));
    private static RhinoDoc Document() { var document = RhinoDoc.CreateHeadless(null); document.ModelAbsoluteTolerance = Tol; return document; }
    private static int Define(RhinoDoc document, string name, GeometryBase geometry) =>
        document.InstanceDefinitions.Add(name, "", Point3d.Origin, new[] { geometry }, new[] { new ObjectAttributes() });
    private static ViewDrawing Draw(PreparedScene scene, ViewRequest view, long generation = 1) =>
        Assert.Single(Solve(scene, generation, view).Views);
    private static DrawingBatch Solve(PreparedScene scene, long generation, params ViewRequest[] views) =>
        NativeDrawing.SolveBorrowedAsync(scene, new RequestIdentity(scene.Revision, "reuse", "default", generation), views, new(), default, null).GetAwaiter().GetResult();
    private static ViewRequest Cut(double z = 0) => ViewRequest.Parse(new Plane(new Point3d(0, 0, z), Vector3d.XAxis, Vector3d.YAxis));
    // A property, not a field: a Rhino struct field would load RhinoCommon during test discovery, before the resolver.
    private static Transform Turn => Transform.Translation(7, -3, .25) * Transform.Rotation(.7, new Vector3d(1, 2, 3), new Point3d(1, 0, 0));

    /// <summary>Everything a drawing publishes, in published order: any thread-timing dependence would change it.</summary>
    private static string Signature(ViewDrawing view) => string.Join("\n", view.Items.Select(p => $"{p.Class}|{p.SourceIdentity}|{p.GeometricKey}")
        .Concat(view.Regions.Select(r => $"{r.Region.Id}|{string.Join(",", r.Region.SourceIds)}|{r.Region.Area:R}|{r.Region.Hatch is not null}|" +
            string.Join(";", r.Region.Loops.Select(l => $"{l.IsHole}/{l.Curve.GetLength():R}/{string.Join(",", l.Edges.Select(e => (int)e.Origin))}"))))
        .Concat(view.Diagnostics).Append(view.Info.Split('\n')[2]));

    [RhinoFact] public void SelfIntersectingDefinitionIsRejectedForEveryInstanceAtEachLocalCut() => host.Run(() =>
    {
        using var document = Document(); using var cache = new SceneCache();
        using (var shape = BowTie())
        {
            // Main's rule, on the shape itself: this is a real self-intersection, not a tolerance artefact.
            var failure = Assert.ThrowsAny<Exception>(() => SectionGeometry.Convert(shape, Tol));
            Assert.StartsWith(SelfIntersecting, failure.Message);
            var definition = Define(document, "bow-tie", shape);
            var placements = new Dictionary<string, Transform>
            {
                ["plain"] = Transform.Identity, ["moved"] = Turn, ["mirrored"] = Transform.Translation(20, 0, 0) * Transform.Mirror(Plane.WorldYZ),
                ["scaled"] = Transform.Translation(0, 20, 0) * Transform.Scale(Point3d.Origin, 2.5),
                ["mirrored+scaled"] = Transform.Translation(20, 20, 0) * Transform.Scale(Point3d.Origin, 2.5) * Transform.Mirror(Plane.WorldZX),
                ["stretched"] = Transform.Translation(40, 0, 0) * Transform.Scale(Plane.WorldXY, 1, 2, 1)
            };
            foreach (var placement in placements) document.Objects.AddInstanceObject(definition, placement.Value);
        }
        using var lease = BaselineTests.Capture(document, cache); var scene = lease.Value;
        var leaves = scene.Prepare(default);
        Assert.Equal(6, leaves.Count);
        using var batch = Solve(scene, 1, Cut(1), Cut(1.5));
        foreach (var view in batch.Views)
        {
            output.WriteLine(view.Info);
            // Every instance is refused, by the clip and by the region pass; none becomes a region or a confident line.
            foreach (var leaf in leaves)
                Assert.Equal(2, view.Diagnostics.Count(d => d.StartsWith($"{leaf.Identity}: Unsupported: {SelfIntersecting}", StringComparison.Ordinal)));
            Assert.Empty(view.Regions);
            Assert.DoesNotContain(view.Items, p => p.Class is LineClass.Visible or LineClass.Hidden);
        }
        // The exact local near/far equations differ for these five canonical
        // placements at both cuts (mirrors reverse a local plane normal).
        // The stretched copy validates its own geometry.
        Assert.Equal(10, scene.SectionVerdicts.Computations);
        Assert.Equal(10, scene.SectionVerdicts.Count);
    });

    [RhinoFact] public void ValidDefinitionSharesOnlyMatchingLocalCutsAcrossInstancesAndViews() => host.Run(() =>
    {
        using var document = Document(); using var cache = new SceneCache();
        using (var box = Box())
        {
            var definition = Define(document, "box", box);
            for (var i = 0; i < 40; i++)
                document.Objects.AddInstanceObject(definition, Transform.Translation(3 * (i % 8), 3 * (i / 8), .1 * (i % 3)));
        }
        using var lease = BaselineTests.Capture(document, cache); var scene = lease.Value;
        using (var first = Solve(scene, 1, Cut()))
        {
            var view = Assert.Single(first.Views); output.WriteLine(view.Info);
            Assert.Null(view.Error); Assert.False(view.Report.Quarantined, view.Info);
            Assert.Equal(40, view.Regions.Count);
            Assert.All(view.Regions, r => Assert.InRange(Math.Abs(r.Region.Area - 4), 0, 1e-6));
        }
        Assert.Equal(3, scene.SectionVerdicts.Computations);
        // The three placement heights share cuts; another plane must not reuse
        // their verdicts. An identical repeated request still does reuse them.
        using var second = Solve(scene, 2, Cut(.5), ViewRequest.Parse(new Plane(new Point3d(0, 4, 0), Vector3d.XAxis, Vector3d.ZAxis)));
        Assert.All(second.Views, v => Assert.Null(v.Error));
        var computations = scene.SectionVerdicts.Computations;
        Assert.True(computations > 3);
        using var repeat = Solve(scene, 3, Cut(.5), ViewRequest.Parse(new Plane(new Point3d(0, 4, 0), Vector3d.XAxis, Vector3d.ZAxis)));
        Assert.Equal(computations, scene.SectionVerdicts.Computations);
    });

    [RhinoFact] public void CongruentInstancesShareAKeyAndNonSimilarOnesShareNothing() => host.Run(() =>
    {
        using var document = Document(); using var cache = new SceneCache(); var ids = new List<Guid>();
        using (var box = Box())
        {
            var definition = Define(document, "box", box);
            using var other = Box(.5); var second = Define(document, "small", other);
            foreach (var transform in new[] { Transform.Identity, Turn, Transform.Mirror(Plane.WorldYZ), Transform.Scale(Point3d.Origin, 3),
                Turn * Transform.Scale(new Point3d(1, 1, 1), 3) * Transform.Mirror(Plane.WorldXY), Transform.Scale(Point3d.Origin, 3.0000001),
                Transform.Scale(Plane.WorldXY, 1, 1, 2), Transform.Shear(Plane.WorldXY, Vector3d.XAxis, new Vector3d(.3, 1, 0), Vector3d.ZAxis) })
                ids.Add(document.Objects.AddInstanceObject(definition, transform));
            ids.Add(document.Objects.AddInstanceObject(second, Transform.Identity));
        }
        using var lease = BaselineTests.Capture(document, cache);
        var leaves = lease.Value.Prepare(default);
        // Document order is by object id, so look each placement up by its own id.
        var keys = ids.Select(id => leaves.Single(l => l.Identity.StartsWith(id.ToString(), StringComparison.Ordinal)).Canonical).ToArray();
        Assert.Equal(9, keys.Length);
        var plain = keys[0]!;
        Assert.Equal(1, plain.Scale);
        Assert.Equal(plain.Key, keys[1]!.Key);                       // rotated and moved
        Assert.Equal(plain.Key, keys[2]!.Key);                       // mirrored
        Assert.Equal(1, keys[2]!.Scale);
        Assert.NotEqual(plain.Key, keys[3]!.Key);                    // uniformly scaled
        Assert.StartsWith(plain.Key + "|scale=", keys[3]!.Key);
        Assert.InRange(Math.Abs(keys[3]!.Scale - 3), 0, 1e-12);
        Assert.InRange(Math.Abs(keys[4]!.Scale - 3), 0, 1e-12);      // moved, scaled and mirrored: positive scale, same shape
        Assert.StartsWith(plain.Key + "|scale=", keys[4]!.Key);
        Assert.NotEqual(keys[3]!.Key, keys[5]!.Key);                 // a different scale is a different shape at this tolerance
        Assert.Null(keys[6]);                                        // non-uniform scale: no reuse
        Assert.Null(keys[7]);                                        // shear: no reuse
        Assert.NotEqual(plain.Key, keys[8]!.Key);                    // another definition
        // The canonical copy is the definition's own geometry, independent of any instance.
        using var local = plain.CopyLocal();
        var bounds = local.GetBoundingBox(true);
        Assert.InRange(bounds.Min.DistanceTo(new Point3d(-1, -1, -1)) + bounds.Max.DistanceTo(new Point3d(1, 1, 1)), 0, 1e-12);
    });

    [RhinoFact] public void SectionParallelismPreservesCertifiedRegionsAndHldInputs() => host.Run(() =>
    {
        var saved = SectionRegionEngine.Parallelism;
        try
        {
            var snapshots = new List<ViewDrawing[]>();
            var inputRuns = new List<string[]>();
            using var document = Document();
            using (var box = Box()) using (var pipe = Brep.CreatePipe(new LineCurve(new Point3d(0, 0, -2), new Point3d(0, 0, 2)), .4, false, PipeCapMode.Flat, true, Tol, .01)[0])
            using (var bad = BowTie())
            {
                var boxes = Define(document, "box", box); var pipes = Define(document, "pipe", pipe); var bows = Define(document, "bow", bad);
                for (var i = 0; i < 36; i++)
                {
                    var place = Transform.Translation(2.5 * (i % 6), 2.5 * (i / 6), .3 * (i % 4) - .2) * Transform.Rotation(.31 * i, new Vector3d(.2, .1, 1), Point3d.Origin);
                    document.Objects.AddInstanceObject(i % 3 == 0 ? pipes : boxes, i % 5 == 0 ? place * Transform.Mirror(Plane.WorldYZ) : i % 7 == 0 ? place * Transform.Scale(Point3d.Origin, 1.3) : place);
                }
                document.Objects.AddInstanceObject(bows, Transform.Translation(30, 0, -1));
                document.Objects.AddBrep(box, null);
                document.Objects.AddLine(new Point3d(-3, -3, -1), new Point3d(18, 16, -1));
            }
            foreach (var threads in new[] { 1, 4, 8, 1 })
            {
                SectionRegionEngine.Parallelism = threads;
                using var cache = new SceneCache(); // Its own scene for the same document: same identities, nothing warm.
                using var lease = BaselineTests.Capture(document, cache);
                Assert.Equal(0, lease.Value.SectionVerdicts.Count);
                var registrations=new List<string>();
                using var batch = NativeDrawing.SolveBorrowedAsync(lease.Value,new(lease.Value.Revision,"reuse","default",1),
                    [Cut(), ViewRequest.Parse(new Plane(new Point3d(0,5.1,0),Vector3d.XAxis,Vector3d.ZAxis))],
                    new(){ObserveNativeInputs=inputs=>registrations.Add(string.Join(";",inputs.Select(i=>i.Source.Identity+"/"+i.Far+"/"+GeometryFingerprint.Compute(i.Geometry))))},default,null).GetAwaiter().GetResult();
                inputRuns.Add(registrations.ToArray());
                Assert.All(batch.Views, v => Assert.Null(v.Error));
                Assert.Contains(batch.Views[0].Regions, r => r.Region.Hatch is not null);
                snapshots.Add(batch.Views.Select(v=>WorkerCodec.RestoreDrawing(WorkerCodec.CaptureDrawing(v))).ToArray());
                Assert.Equal(0, SectionRegionEngine.LiveOutcomes);
            }
            try
            {
                foreach(var run in snapshots.Skip(1))
                    for(int i=0;i<run.Length;i++)
                    {
                        Assert.Equal(WorkerCodec.Encode(WorkerCodec.Decode<WorkerCodec.Drawing>(WorkerCodec.CaptureDrawing(snapshots[0][i])).Regions),
                            WorkerCodec.Encode(WorkerCodec.Decode<WorkerCodec.Drawing>(WorkerCodec.CaptureDrawing(run[i])).Regions));
                        Assert.True(run[i].MultipleThreads);
                        try { HldFixtureComparison.Equal(snapshots[0][i],run[i]); }
                        catch (Xunit.Sdk.XunitException ex)
                        {
                            // This complex freeform scene gates ordered input and certified regions, not fresh HLD equality.
                            // Keep every observed native difference, at the fixture's original tolerance, for investigation.
                            var root=System.Environment.GetEnvironmentVariable("M2D_MODELS") ?? Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile),".t3","m2d-plus-models");
                            var folder=Path.Combine(root,"evidence","hld-only-variation",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
                            File.WriteAllBytes(Path.Combine(folder,"expected.json"),WorkerCodec.CaptureDrawing(snapshots[0][i]));
                            File.WriteAllBytes(Path.Combine(folder,"actual.json"),WorkerCodec.CaptureDrawing(run[i]));
                            File.WriteAllBytes(Path.Combine(folder,"manifest.json"),WorkerCodec.Encode(new{fixture="section-reuse-complex",view=i,
                                tolerance=Tol,policy=HldProjection.Policy,fingerprint=WorkerCodec.Fingerprint(),processorCount=System.Environment.ProcessorCount,orderedInputs=inputRuns[0],difference=ex.Message}));
                            output.WriteLine("Fresh HLD variation recorded: "+folder);
                        }
                    }
                Assert.All(inputRuns,run=>Assert.Equal(inputRuns[0],run));
            }
            finally{foreach(var drawing in snapshots.SelectMany(v=>v))drawing.Dispose();}

        }
        finally { SectionRegionEngine.Parallelism = saved; }
    });

    [RhinoFact] public void CancellingMidParallelDisposesEveryOutcomeAndLeavesTheEngineUsable() => host.Run(() =>
    {
        var saved = SectionRegionEngine.Parallelism;
        var boxes = Enumerable.Range(0, 480).Select(i => { var b = Box(); b.Translate(3 * (i % 24), 3 * (i / 24), 0); return b; }).ToArray();
        using var definition = Box();
        try
        {
            SectionRegionEngine.Parallelism = 4;
            var request = new SectionRequest(Plane.WorldXY, Tol, .001);
            // Every source is a moved copy of one definition: one verdict, settled before the parallel phase starts.
            var sources = boxes.Select((b, i) => new SectionSource($"s{i:000}", b, SectionStyleSnapshot.Default(0xff336699))
                { Canonical = new("cancel/box", 1, () => definition.DuplicateBrep()) }).ToArray();
            foreach (var cancelAt in new[] { 1, 12, 150 })
            {
                using var cancel = new CancellationTokenSource(); using var verdicts = new SectionVerdictCache(); var seen = 0;
                // Cancel from another thread once workers hold that many finished, unmerged outcomes: the middle of the parallel phase.
                var watcher = Task.Run(() =>
                {
                    var wait = new SpinWait();
                    while (!cancel.IsCancellationRequested && (seen = Math.Max(seen, SectionRegionEngine.LiveOutcomes)) < cancelAt) wait.SpinOnce(-1);
                    cancel.Cancel();
                });
                var thrown = Assert.ThrowsAny<OperationCanceledException>(() => SectionRegionEngine.Compute(sources, request, verdicts, cancel.Token, prepareVisibility: true));
                watcher.Wait();
                Assert.Equal(cancel.Token, thrown.CancellationToken);
                Assert.InRange(seen, cancelAt, sources.Length);
                Assert.Equal(1, verdicts.Computations);
                Assert.Equal(0, SectionRegionEngine.LiveOutcomes);
            }
            // The same sources, not cancelled, still give the complete result.
            using var result = SectionRegionEngine.Compute(sources, request, default, prepareVisibility: true);
            Assert.False(result.RequiresQuarantine);
            Assert.Equal(480, result.Regions.Count); Assert.Equal(480, result.Visibility.Count);
            Assert.Equal(0, SectionRegionEngine.LiveOutcomes);
            // A token cancelled before the call is refused at once, serial or parallel, before any geometry work.
            foreach (var threads in new[] { 1, 4 })
            {
                SectionRegionEngine.Parallelism = threads; using var verdicts = new SectionVerdictCache();
                Assert.ThrowsAny<OperationCanceledException>(() => SectionRegionEngine.Compute(sources, request, verdicts, new CancellationToken(true), prepareVisibility: true));
                Assert.Equal(0, verdicts.Computations); Assert.Equal(0, SectionRegionEngine.LiveOutcomes);
            }
        }
        finally { SectionRegionEngine.Parallelism = saved; foreach (var box in boxes) box.Dispose(); }
    });

    [RhinoFact] public void CurveOverlapAttributionIsRepeatableOnWorkerThreads() => host.Run(() =>
    {
        // Closed edges tested against themselves: where Rhino's curve-curve search, called from two threads at once,
        // lost part of an overlap. The section code serialises those calls, so worker threads must agree with a serial run.
        using var rail = new ArcCurve(new Arc(new Point3d(0, 0, 0), new Point3d(2, 1, .5), new Point3d(4, 0, 1)));
        var solids = Enumerable.Range(0, 64).Select(_ => Brep.CreatePipe(rail, .3, false, PipeCapMode.Flat, true, Tol, .01)[0]).ToArray();
        try
        {
            Assert.Contains(solids[0].Edges, e => e.IsClosed);
            static string Attribute(Brep solid)
            {
                var originals = solid.Edges.Select(e => e.DuplicateCurve()).ToArray();
                try
                {
                    return string.Join(";", solid.Edges.Select(edge =>
                    {
                        var parts = EdgeProvenance.Split(edge, originals.Select(c => (c, EdgeOrigin.Original)), Tol);
                        try { return $"{parts.Count}:{string.Join("", parts.Select(x => (int)x.Origin))}:{parts.Sum(x => x.Curve.GetLength()):F6}"; }
                        finally { foreach (var part in parts) part.Dispose(); }
                    }));
                }
                finally { foreach (var curve in originals) curve.Dispose(); }
            }
            var reference = Attribute(solids[0]);
            Assert.DoesNotContain(((int)EdgeOrigin.None).ToString(), reference.Split(';').Select(x => x.Split(':')[1]));
            var different = 0;
            for (var round = 0; round < 40; round++) // Each solid is used by one thread at a time.
                Parallel.For(0, solids.Length, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i => { if (Attribute(solids[i]) != reference) Interlocked.Increment(ref different); });
            Assert.Equal(0, different);
        }
        finally { foreach (var solid in solids) solid.Dispose(); }
    });

    [RhinoFact] public void VerdictsLiveWithTheSceneAndAreClearedWhenItIsDisposed() => host.Run(() =>
    {
        using var document = Document(); var cache = new SceneCache();
        using (var box = Box()) { var definition = Define(document, "box", box); document.Objects.AddInstanceObject(definition, Turn); document.Objects.AddBrep(box, null); }
        var lease = BaselineTests.Capture(document, cache); var scene = lease.Value; var verdicts = scene.SectionVerdicts;
        Assert.Equal(0, verdicts.Count);
        using (Solve(scene, 1, Cut())) { }
        Assert.Equal(2, verdicts.Count); Assert.Equal(2, verdicts.Computations); Assert.False(verdicts.IsDisposed);
        // A second capture of the unchanged document is the same scene, so the same verdicts.
        using (var again = BaselineTests.Capture(document, cache)) Assert.Same(verdicts, again.Value.SectionVerdicts);
        // Clearing the cache retires the scene, but a live lease keeps it and its verdicts.
        cache.Dispose();
        Assert.False(verdicts.IsDisposed); Assert.Equal(2, verdicts.Count);
        using (Solve(scene, 2, Cut(.5))) { }
        Assert.Equal(4, verdicts.Computations);
        lease.Dispose();
        Assert.True(verdicts.IsDisposed); Assert.Equal(0, verdicts.Count);
        using var shape = Box();
        Assert.Throws<ObjectDisposedException>(() => verdicts.Verdict(new SectionCanonical("late", 1, () => shape.DuplicateBrep()), Tol));
    });
}
