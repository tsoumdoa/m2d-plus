using M2DPlus.Core;
using M2DPlus.Rhino.Services;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using Xunit;
using Point = Rhino.Geometry.Point;

namespace M2DPlus.Rhino.Tests;

[Collection("Rhino")]
[Trait("Category", "RhinoIntegration")]
public sealed class WalkingSkeletonTests(RhinoHost host)
{
    private static RequestIdentity Request(PreparedScene scene, long generation = 1) => new(scene.Revision, "Top|Front", "deterministic", generation);
    private static LeaseEntry<PreparedScene>.Lease Capture(RhinoDoc document, DocumentWatch watch, SceneCache cache, IEnumerable<CaptureInput>? inputs = null)
    {
        using var capture = new SceneCapture(document, watch, inputs, cache);
        while (!capture.Step(TimeSpan.FromMilliseconds(12))) { }
        return capture.Accept();
    }
    private static DrawingBatch Solve(LeaseEntry<PreparedScene>.Lease lease, params string[] views) =>
        NativeDrawing.SolveAsync(lease, Request(lease.Value), views.Select(v => ViewRequest.Parse(v)).ToArray(), new(), default).GetAwaiter().GetResult();
    private static Guid Box(RhinoDoc document, Point3d min, Point3d max)
    { using var box = Brep.CreateFromBox(new BoundingBox(min, max)); return document.Objects.AddBrep(box); }
    private static int AddBlocks(RhinoDoc document)
    {
        using var a = Brep.CreateFromBox(new BoundingBox(Point3d.Origin, new Point3d(1, 2, 3)));
        using var b = Brep.CreateFromBox(new BoundingBox(new Point3d(2, 0, 0), new Point3d(3, 1, 1)));
        var leaf = document.InstanceDefinitions.Add("asymmetric", "", Point3d.Origin, new GeometryBase[] { a, b }, new[] { new ObjectAttributes(), new ObjectAttributes() });
        using var nested = new InstanceReferenceGeometry(document.InstanceDefinitions[leaf].Id, Transform.Translation(2, 4, 0));
        var parent = document.InstanceDefinitions.Add("nested", "", Point3d.Origin, new[] { nested }, new[] { new ObjectAttributes() });
        document.Objects.AddInstanceObject(parent, Transform.Translation(12, 0, 0) * Transform.Mirror(Plane.WorldYZ));
        return leaf;
    }
    [RhinoFact] public void AnalyticBoxCrossingCurvePointsAndNestedMirrorFromTwoDirections() => host.Run(() =>
    {
        using var document = RhinoDoc.CreateHeadless(null); using var cache = new SceneCache();
        document.ModelAbsoluteTolerance = 1e-7;
        var box = Box(document, Point3d.Origin, new Point3d(2, 2, 2));
        var crossing = document.Objects.AddLine(new Point3d(-1, 1, 1), new Point3d(3, 1, 1));
        var topHidden = document.Objects.AddPoint(new Point3d(1, 1, -1));
        var topFront = document.Objects.AddPoint(new Point3d(1, 1, 3));
        var frontHidden = document.Objects.AddPoint(new Point3d(1, 3, 1));
        var frontFront = document.Objects.AddPoint(new Point3d(1, -1, 1));
        AddBlocks(document);
        using var watch = new DocumentWatch(document);
        using var batch = Solve(Capture(document, watch, cache), "Top", "Front");
        Assert.Equal(2, batch.Views.Count);
        for (var index = 0; index < 2; index++)
        {
            var view = batch.Views[index];
            Assert.Null(view.Error); Assert.False(view.Report.Quarantined, view.Info); Assert.Equal(0, view.Report.InvalidExtractionCount);
            var lines = view.Items.Where(p => p.SourceIdentity == crossing.ToString()).ToArray();
            // The solid occupies 0 <= x <= 2 on both rays: visible [-1,0] U [2,3], hidden [0,2].
            AssertIntervals(lines, LineClass.Visible, new[] { (-1d, 0d), (2d, 3d) });
            AssertIntervals(lines, LineClass.Hidden, new[] { (0d, 2d) });
            AssertRectangle(view.Items.Where(p => p.SourceIdentity == box.ToString()), index, 0, 2, 0, 2);
            var block = view.Items.Where(p => p.SourceIdentity.Contains('/')).GroupBy(p => p.SourceIdentity).ToArray();
            Assert.Equal(2, block.Length);
            // Hand-composed mirror x'=12-(x+2), y'=y+4; no production transform helper used in the oracle.
            var blockBounds = block.Select(group => Bounds2(group, index)).OrderBy(b => b.Min.X).ToArray();
            Assert.Equal(7, blockBounds[0].Min.X, 7); Assert.Equal(8, blockBounds[0].Max.X, 7);
            Assert.Equal(9, blockBounds[1].Min.X, 7); Assert.Equal(10, blockBounds[1].Max.X, 7);
            foreach (var group in block)
            {
                var bounds = Bounds2(group, index); var left = bounds.Min.X < 8;
                AssertRectangle(group, index, left ? 7 : 9, left ? 8 : 10, index == 0 ? 4 : 0, index == 0 ? (left ? 5 : 6) : (left ? 1 : 3));
            }
            var points = view.Items.Where(p => { using var g = p.CopyGeometry(); return g is Point; }).Select(p => p.SourceIdentity).ToArray();
            Assert.DoesNotContain((index == 0 ? topHidden : frontHidden).ToString(), points);
            Assert.Contains((index == 0 ? topFront : frontFront).ToString(), points);
            Assert.Equal(1, view.HiddenPoints); Assert.Equal(3, points.Length);
        }
    });
    private static void AssertIntervals(IEnumerable<DrawingPiece> pieces, LineClass classification, (double Min, double Max)[] expected)
    {
        var actual = pieces.Where(p => p.Class == classification).Select(p =>
        { using var g = p.CopyGeometry(); var b = g.GetBoundingBox(true); return (Min: b.Min.X, Max: b.Max.X); }).OrderBy(p => p.Min).ToArray();
        Assert.Equal(expected.Length, actual.Length);
        for (var i = 0; i < expected.Length; i++) { Assert.Equal(expected[i].Min, actual[i].Min, 6); Assert.Equal(expected[i].Max, actual[i].Max, 6); }
    }
    private static BoundingBox Bounds2(IEnumerable<DrawingPiece> pieces, int view)
    {
        var bounds = BoundingBox.Empty;
        foreach (var piece in pieces)
        { using var g = piece.CopyGeometry(); foreach (var corner in g.GetBoundingBox(true).GetCorners()) bounds.Union(new Point3d(corner.X, view == 0 ? corner.Y : corner.Z, 0)); }
        return bounds;
    }
    private static void AssertRectangle(IEnumerable<DrawingPiece> pieces, int view, double x0, double x1, double y0, double y1)
    {
        // Check all candidate loci and completeness via side lengths; no count/signature derived from HLD.
        var lengths = new double[4];
        var intervals = Enumerable.Range(0, 4).Select(_ => new List<(double Min, double Max)>()).ToArray();
        foreach (var piece in pieces)
        {
            Assert.Equal(LineClass.Visible, piece.Class);
            using var geometry = piece.CopyGeometry(); var curve = Assert.IsAssignableFrom<Curve>(geometry);
            var start = curve.PointAtStart; var end = curve.PointAtEnd;
            var ay = view == 0 ? start.Y : start.Z; var by = view == 0 ? end.Y : end.Z;
            var side = Math.Abs(start.X - x0) < 1e-6 && Math.Abs(end.X - x0) < 1e-6 ? 0 :
                Math.Abs(start.X - x1) < 1e-6 && Math.Abs(end.X - x1) < 1e-6 ? 1 :
                Math.Abs(ay - y0) < 1e-6 && Math.Abs(by - y0) < 1e-6 ? 2 :
                Math.Abs(ay - y1) < 1e-6 && Math.Abs(by - y1) < 1e-6 ? 3 : -1;
            Assert.True(side >= 0, "Extra locus outside analytic rectangle");
            Assert.InRange(start.X, x0 - 1e-6, x1 + 1e-6); Assert.InRange(end.X, x0 - 1e-6, x1 + 1e-6);
            Assert.InRange(ay, y0 - 1e-6, y1 + 1e-6); Assert.InRange(by, y0 - 1e-6, y1 + 1e-6);
            lengths[side] += curve.GetLength();
            var a = side < 2 ? ay : start.X; var b = side < 2 ? by : end.X;
            intervals[side].Add((Math.Min(a, b), Math.Max(a, b)));
        }
        Assert.Equal(y1 - y0, lengths[0], 6); Assert.Equal(y1 - y0, lengths[1], 6);
        Assert.Equal(x1 - x0, lengths[2], 6); Assert.Equal(x1 - x0, lengths[3], 6);
        for (var side = 0; side < 4; side++)
        {
            var cursor = side < 2 ? y0 : x0;
            foreach (var interval in intervals[side].OrderBy(i => i.Min))
            { Assert.Equal(cursor, interval.Min, 6); cursor = interval.Max; }
            Assert.Equal(side < 2 ? y1 : x1, cursor, 6);
        }
    }
    private sealed class NativeBarrier : IProgress<double>, IDisposable
    {
        internal readonly ManualResetEventSlim Entered = new();
        internal readonly ManualResetEventSlim Release = new();
        public void Report(double value) { Entered.Set(); if (!Release.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("Host did not release native callback"); }
        public void Dispose() { Release.Set(); Entered.Dispose(); Release.Dispose(); }
    }
    [RhinoFact] public void EditTransformDeleteWhileNativeCallIsAlive() => MidSolve((document, cache, ids, _) =>
    { document.Objects.Transform(ids[0], Transform.Translation(20, 0, 0), true); document.Objects.Delete(ids[1], true); });
    [RhinoFact] public void DefinitionEditWhileNativeCallIsAlive() => MidSolve((document, cache, ids, definition) =>
    {
        using var box = Brep.CreateFromBox(new BoundingBox(Point3d.Origin, new Point3d(9, 2, 3)));
        Assert.True(document.InstanceDefinitions.ModifyGeometry(definition, new[] { box }, new[] { new ObjectAttributes() }));
    });
    [RhinoFact] public void DocumentCloseWhileNativeCallIsAlive() => MidSolve((document, cache, ids, _) => document.Dispose());
    [RhinoFact] public void CacheClearWhileNativeCallIsAlive() => MidSolve((document, cache, ids, _) => cache.Clear());
    private void MidSolve(Action<RhinoDoc, SceneCache, Guid[], int> edit) => host.Run(() =>
    {
        using var document = RhinoDoc.CreateHeadless(null); using var cache = new SceneCache();
        var ids = new[] { Box(document, Point3d.Origin, new Point3d(2, 2, 2)), document.Objects.AddPoint(new Point3d(1, 1, 3)) };
        var definition = AddBlocks(document); using var watch = new DocumentWatch(document);
        var lease = Capture(document, watch, cache); var revision = lease.Value.Revision;
        using var barrier = new NativeBarrier(); NativeDrawing.NativeProgress = barrier;
        var task = NativeDrawing.SolveAsync(lease, Request(lease.Value), new[] { ViewRequest.Parse("Top"), ViewRequest.Parse("Front") }, new(NativeOnly: true), default);
        try
        {
            Assert.True(barrier.Entered.Wait(TimeSpan.FromSeconds(15)), "Native progress callback was not entered");
            Assert.False(task.IsCompleted); edit(document, cache, ids, definition); Assert.False(task.IsCompleted);
        }
        finally { barrier.Release.Set(); NativeDrawing.NativeProgress = null; }
        using var result = task.GetAwaiter().GetResult();
        Assert.Equal(revision, result.Identity.SceneRevision); Assert.Equal(2, result.Views.Count);
        Assert.All(result.Views, view => { Assert.Null(view.Error); Assert.False(view.Report.Quarantined, view.Info); Assert.NotEmpty(view.Items); });
        for (var index = 0; index < 2; index++)
            AssertRectangle(result.Views[index].Items.Where(p => p.SourceIdentity == ids[0].ToString()), index, 0, 2, 0, 2);
    });
    [RhinoFact] public void OutputMutationCannotAlterCachedResultOrPreparationAndSecondConsumerReusesScene() => host.Run(() =>
    {
        using var document = RhinoDoc.CreateHeadless(null); using var cache = new SceneCache();
        Box(document, Point3d.Origin, new Point3d(2, 2, 2)); using var watch = new DocumentWatch(document);
        var first = Capture(document, watch, cache); var scene = first.Value;
        using var a = Solve(first, "Top", "Front");
        var item = a.Views[0].Items[0]; using var before = item.CopyGeometry();
        using (var emitted = item.CopyGeometry()) emitted.Transform(Transform.Translation(100, 0, 0));
        using var unchanged = item.CopyGeometry(); Assert.True(GeometryBase.GeometryEquals(before, unchanged));
        var second = Capture(document, watch, cache); Assert.Same(scene, second.Value);
        using var b = Solve(second, "Top", "Front");
        Assert.Equal(1, scene.PreparationCount); Assert.Equal(1, cache.SceneHits); Assert.True(cache.GeometryHits > 0);
        using var next = b.Views[0].Items[0].CopyGeometry(); Assert.True(GeometryBase.GeometryEquals(before, next));
    });
    [RhinoFact] public void CancelImmediatelyBeforeCompletionRejectsWholeBatch() => host.Run(() =>
    {
        using var document = RhinoDoc.CreateHeadless(null); using var cache = new SceneCache();
        Box(document, Point3d.Origin, new Point3d(2, 2, 2)); using var watch = new DocumentWatch(document);
        var lease = Capture(document, watch, cache); using var cancel = new CancellationTokenSource();
        NativeDrawing.BeforeCompletion = cancel.Cancel;
        try { Assert.ThrowsAny<OperationCanceledException>(() => NativeDrawing.SolveAsync(lease, Request(lease.Value), new[] { ViewRequest.Parse("Top") }, new(), cancel.Token).GetAwaiter().GetResult()); }
        finally { NativeDrawing.BeforeCompletion = null; }
    });
    [RhinoFact] public void DocumentOrDefinitionChangeBetweenCaptureSlicesIsRejected() => host.Run(() =>
    {
        using var document = RhinoDoc.CreateHeadless(null); using var cache = new SceneCache();
        var definition = AddBlocks(document); using var watch = new DocumentWatch(document);
        using var capture = new SceneCapture(document, watch, cache: cache);
        capture.Step(TimeSpan.FromTicks(1));
        using var changed = new Point(Point3d.Origin);
        document.InstanceDefinitions.ModifyGeometry(definition, new[] { changed }, new[] { new ObjectAttributes() });
        // Headless documents raise no events; the snapshot check runs at most every 200 ms between slices and always
        // before the scene is accepted, so the change is rejected by then at the latest.
        Assert.ThrowsAny<OperationCanceledException>(() => { while (!capture.Step(TimeSpan.FromMilliseconds(12))) { } capture.Accept().Dispose(); });
    });
    [RhinoFact] public void UnknownTypeQuarantinesBothUncutAndSectionViews() => host.Run(() =>
    {
        using var document = RhinoDoc.CreateHeadless(null); using var cache = new SceneCache();
        using var watch = new DocumentWatch(document);
        using var brep = Brep.CreateFromBox(new BoundingBox(Point3d.Origin, new Point3d(2, 2, 2)))!;
        using var light = new Light();
        // Unknown geometry types are conservatively treated as potential occluders.
        var input = new[] { new CaptureInput(brep, new[] { 0 }, "box"), new CaptureInput(light, new[] { 1 }, "light") };
        var lease = Capture(document, watch, cache, input);
        using var batch = NativeDrawing.SolveAsync(lease, Request(lease.Value), new[] { ViewRequest.Parse("Top"), ViewRequest.Parse(Plane.WorldXY) }, new(), default).GetAwaiter().GetResult();
        Assert.Null(batch.Views[0].Error); Assert.True(batch.Views[0].Report.Quarantined);
        Assert.All(batch.Views[0].Items, item => Assert.Equal(LineClass.Unresolved, item.Class));
        Assert.Equal(1, batch.Views[0].Report.UnsupportedByType["Light"]);
        Assert.Null(batch.Views[1].Error); Assert.True(batch.Views[1].Report.Quarantined);
        Assert.Empty(batch.Views[1].Regions); Assert.All(batch.Views[1].Items, item => Assert.Equal(LineClass.Unresolved, item.Class));
    });
    [RhinoFact] public void AnnotationsAreReportedButDoNotQuarantine() => host.Run(() =>
    {
        using var document = RhinoDoc.CreateHeadless(null); using var cache = new SceneCache();
        Box(document, Point3d.Origin, new Point3d(2, 2, 2));
        document.Objects.AddTextDot("dot", new Point3d(1, 1, 3));
        document.Objects.AddText("label", new Plane(new Point3d(0, 0, 3), Vector3d.ZAxis), 0.5, "Arial", false, false);
        using var watch = new DocumentWatch(document);
        using var batch = Solve(Capture(document, watch, cache), "Top");
        var view = batch.Views[0];
        Assert.Null(view.Error); Assert.False(view.Report.Quarantined, view.Info);
        Assert.Contains(view.Items, item => item.Class == LineClass.Visible);
        Assert.Equal(1, view.Report.UnsupportedByType[PreparedScene.NonOccludingPrefix + "TextDot"]);
        Assert.Equal(1, view.Report.UnsupportedByType[PreparedScene.NonOccludingPrefix + "TextEntity"]);
    });
    [RhinoFact] public void DocumentFilteringIncludesLockedAndSkipsHiddenLayersAndBakedGeometry() => host.Run(() =>
    {
        using var document = RhinoDoc.CreateHeadless(null); using var cache = new SceneCache();
        var normal = Box(document, Point3d.Origin, new Point3d(1, 1, 1));
        var locked = document.Objects.AddPoint(new Point3d(2, 2, 2)); document.Objects.Lock(locked, true);
        var hidden = document.Objects.AddPoint(new Point3d(3, 3, 3)); document.Objects.Hide(hidden, true);
        var layer = document.Layers.Add(new Layer { Name = "Hidden", IsVisible = false });
        document.Objects.AddPoint(new Point3d(4, 4, 4), new ObjectAttributes { LayerIndex = layer });
        var baked = new ObjectAttributes(); baked.SetUserString("m2d.bake", "test");
        document.Objects.AddPoint(new Point3d(5, 5, 5), baked);
        using var watch = new DocumentWatch(document);
        var lease = Capture(document, watch, cache); Assert.Equal(2, lease.Value.SourceCount);
        using var batch = Solve(lease, "Top");
        Assert.Contains(batch.Views[0].Items, p => p.SourceIdentity == locked.ToString());
        Assert.Contains(batch.Views[0].Items, p => p.SourceIdentity == normal.ToString());
        Assert.False(batch.Views[0].Report.Quarantined);
    });
    [RhinoFact] public void GhTreePathsPointsAndExplicitExtrusionSubdConversionsArePreserved() => host.Run(() =>
    {
        using var document = RhinoDoc.CreateHeadless(null); using var cache = new SceneCache();
        using var circle = new Circle(Plane.WorldXY, 1).ToNurbsCurve();
        using var extrusion = Extrusion.Create(circle, 2, true);
        using var subd = SubD.CreateFromMesh(Mesh.CreateFromBox(new BoundingBox(new Point3d(5, 0, 0), new Point3d(6, 1, 1)), 1, 1, 1));
        using var watch = new DocumentWatch(document);
        var input = new[] { new CaptureInput(extrusion, new[] { 2, 4 }, "extrusion"),
            new CaptureInput(subd, new[] { 5 }, "subd"), new CaptureInput(new Point3d(10, 10, 10), new[] { 8, 9 }, "point") };
        using var batch = Solve(Capture(document, watch, cache, input), "Top");
        var view = batch.Views[0]; Assert.Null(view.Error); Assert.Equal(3, view.Report.Normalisation.Count);
        Assert.Contains(view.Report.Normalisation, note => note.Contains("Extrusion to Brep"));
        Assert.Contains(view.Report.Normalisation, note => note.Contains("SubD to Brep"));
        Assert.Contains(view.Report.Normalisation, note => note.Contains("Point3d to Point"));
        var point = Assert.Single(view.Items, p => p.SourceIdentity == "point");
        Assert.Equal(new[] { 8, 9 }, point.SourcePath);
        var path = point.SourcePath; path[0] = 99; Assert.Equal(8, point.SourcePath[0]);
    });
    [RhinoFact] public void DirectionAndVectorFramesAreOutsideSceneAndUseExpectedUp() => host.Run(() =>
    {
        var bounds = new BoundingBox(new Point3d(-2, -3, -4), new Point3d(5, 6, 7));
        foreach (var name in new[] { "Top", "Front", "Right", "Back", "Left", "Bottom", "SW", "SE", "NE", "NW" })
        {
            var view = ViewRequest.Parse(name); var frame = view.Resolve(bounds, .001);
            Assert.All(bounds.GetCorners(), p => Assert.True(frame.Plane.DistanceTo(p) < 0));
            Assert.True(frame.Plane.YAxis * (name is "Top" or "Bottom" ? Vector3d.YAxis : Vector3d.ZAxis) > 0);
            var vectorFrame = ViewRequest.Parse(view.Direction).Resolve(bounds, .001); Assert.Equal(frame, vectorFrame);
        }
        Assert.NotNull(ViewRequest.Parse(Vector3d.Zero).Error);
        Assert.Null(ViewRequest.Parse(Plane.WorldXY).Error);
        Assert.Equal(Plane.WorldXY, ViewRequest.Parse(Plane.WorldXY).Cut!.Plane);
    });
    [RhinoFact] public void StableObjectReplacementInvalidatesContentAndReusesUnchangedOwnedEntry() => host.Run(() =>
    {
        using var document = RhinoDoc.CreateHeadless(null); using var cache = new SceneCache();
        var id = Box(document, Point3d.Origin, new Point3d(2, 2, 2));
        document.Objects.AddPoint(new Point3d(-10, -10, 10)); using var watch = new DocumentWatch(document);
        var first = Capture(document, watch, cache); var revision = first.Value.Revision;
        using var original = Solve(first, "Top");
        using var replacement = Brep.CreateFromBox(new BoundingBox(new Point3d(100, 0, 0), new Point3d(102, 2, 2)));
        Assert.True(document.Objects.Replace(id, replacement));
        var hits = cache.GeometryHits; var second = Capture(document, watch, cache);
        Assert.NotEqual(revision, second.Value.Revision); Assert.True(cache.GeometryHits > hits);
        using var changed = Solve(second, "Top");
        AssertRectangle(original.Views[0].Items.Where(p => p.SourceIdentity == id.ToString()), 0, 0, 2, 0, 2);
        AssertRectangle(changed.Views[0].Items.Where(p => p.SourceIdentity == id.ToString()), 0, 100, 102, 0, 2);
    });
}
