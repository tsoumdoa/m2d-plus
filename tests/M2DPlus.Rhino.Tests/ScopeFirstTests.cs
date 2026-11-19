using M2DPlus.Core;
using M2DPlus.Rhino.Services;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using Xunit;

namespace M2DPlus.Rhino.Tests;

[Collection("Rhino")]
public sealed class ScopeFirstTests(RhinoHost host)
{
    private static ViewRequest Crop(double x = 0) => ViewRequest.Parse(new Box(
        new Plane(new Point3d(x, 0, 0), Vector3d.ZAxis), new Interval(-1, 1), new Interval(-1, 1), new Interval(-10, 0)));
    private static Brep Shape => Brep.CreateFromBox(new BoundingBox(new Point3d(-.2, -.2, -2), new Point3d(.2, .2, -1)));
    private static LeaseEntry<PreparedScene>.Lease Capture(RhinoDoc doc, DocumentWatch watch, SceneCache cache,
        ViewRequest[] views, out int skipped, IEnumerable<CaptureInput>? inputs = null)
    {
        using var capture = new SceneCapture(doc, watch, inputs, cache, scopeViews: _ => views);
        while (!capture.Step(TimeSpan.FromMilliseconds(16))) { }
        skipped = capture.ScopedOutCount;
        return capture.Accept();
    }
    private static DrawingBatch Draw(PreparedScene scene, params ViewRequest[] views) => NativeDrawing.SolveBorrowedAsync(scene,
        new(scene.Revision, "scope", "test", 1), views, new(), default, null).GetAwaiter().GetResult();

    [RhinoTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void InternalSingleThreadAndPinnedCameraControlsPreserveDrawingState(bool section) => host.Run(() =>
    {
        using var doc = RhinoDoc.CreateHeadless(null); using var shape = Shape;
        doc.Objects.AddBrep(shape);
        using var cache = new SceneCache(); using var scene = BaselineTests.Capture(doc, cache);
        var view = section ? Crop() : ViewRequest.Parse("Top");
        var settings = new NativeSettings { MultipleThreads = false, CameraBounds = new BoundingBox(new Point3d(-20, -20, -20), new Point3d(20, 20, 20)),
            ObserveHldOptions = options => Assert.False(options.MultipleThreads) };
        using var batch = NativeDrawing.SolveBorrowedAsync(scene.Value, new(scene.Value.Revision, "controls", "test", 1),
            [view], settings, default, null).GetAwaiter().GetResult();
        var drawing = batch.Views[0];
        Assert.Null(drawing.Error); Assert.False(drawing.MultipleThreads);
        Assert.True(drawing.NativeCalls > 0); Assert.NotEmpty(drawing.Items);
        Assert.Contains("single-threaded", drawing.EngineSummary);
        Assert.Contains(drawing.Diagnostics, d => d.Contains("multithreaded=False", StringComparison.Ordinal));
        using var restored = WorkerCodec.RestoreDrawing(WorkerCodec.CaptureDrawing(drawing, true));
        Assert.False(restored.MultipleThreads);
        Assert.True(new NativeSettings().MultipleThreads);
        Assert.DoesNotContain(typeof(NativeSettings).GetProperties(), p => p.Name is "MultipleThreads" or "CameraBounds");
    });

    [RhinoFact] public void InternalSingleThreadFlagIsRetainedWhenSectionOnlySkipsHld() => host.Run(() =>
    {
        using var doc = RhinoDoc.CreateHeadless(null); using var shape = Shape;
        doc.Objects.AddBrep(shape);
        using var cache = new SceneCache(); using var scene = BaselineTests.Capture(doc, cache);
        var view = Crop(); view = view with { Cut = view.Cut! with { SourceDepth = 0 } };
        using var batch = NativeDrawing.SolveBorrowedAsync(scene.Value, new(scene.Value.Revision, "controls", "section-only", 1),
            [view], new() { MultipleThreads = false }, default, null).GetAwaiter().GetResult();
        Assert.Null(batch.Views[0].Error); Assert.Equal(0, batch.Views[0].NativeCalls);
        Assert.False(batch.Views[0].MultipleThreads);
    });

    [RhinoFact] public void OutsideInstanceAndNestedInstancesSkipCaptureWithSurvivorBounds() => host.Run(() =>
    {
        using var doc = RhinoDoc.CreateHeadless(null); using var shape = Shape;
        var leaf = doc.InstanceDefinitions.Add("scope-leaf", "", Point3d.Origin, [shape], [new ObjectAttributes()]);
        using var inside = new InstanceReferenceGeometry(doc.InstanceDefinitions[leaf].Id, Transform.Identity);
        using var outside = new InstanceReferenceGeometry(doc.InstanceDefinitions[leaf].Id, Transform.Translation(8, 0, 0));
        var nested = doc.InstanceDefinitions.Add("scope-nested", "", Point3d.Origin, [inside, outside], [new ObjectAttributes(), new ObjectAttributes()]);
        doc.Objects.AddInstanceObject(nested, Transform.Identity);
        doc.Objects.AddInstanceObject(nested, Transform.Translation(40, 0, 0));
        using var cache = new SceneCache(); using var watch = new DocumentWatch(doc);
        using var full = BaselineTests.Capture(doc, cache);
        using var scoped = Capture(doc, watch, cache, [Crop()], out var skipped);
        Assert.Equal(2, skipped); Assert.Equal(1, scoped.Value.SourceCount); Assert.Equal(4, full.Value.SourceCount);
        Assert.NotEqual(full.Value.Bounds, scoped.Value.Bounds);
        Assert.Single(scoped.Value.Prepare(default));
        using var a = Draw(full.Value, Crop()); using var b = Draw(scoped.Value, Crop());
        HldFixtureComparison.Equal(a.Views[0], b.Views[0]);
    });

    [RhinoTheory]
    [InlineData("brep_trimmed_curved")]
    [InlineData("brep_trimmed_planar_annulus")]
    [InlineData("nurbs_curve_3d")]
    [InlineData("mesh_terrain_brep_building")]
    [InlineData("extrusion_solid")]
    [InlineData("subd_rounded_cube")]
    [InlineData("surface_planar_open")]
    public void ExcludedSourcesAreNeverCopiedOrNormalised(string fixture) => host.Run(() =>
    {
        using var doc = RhinoDoc.OpenHeadless(Path.Combine(AppContext.BaseDirectory, "fixtures", fixture + ".3dm"));
        using var fullCache = new SceneCache(); using var full = BaselineTests.Capture(doc, fullCache);
        using var scopedCache = new SceneCache(); using var watch = new DocumentWatch(doc);
        var before = SceneCapture.Normalisations;
        using var scoped = Capture(doc, watch, scopedCache, [Crop(1e6)], out var skipped);
        Assert.True(skipped > 0); Assert.Equal(0, scoped.Value.SourceCount);
        Assert.False(scoped.Value.Bounds.IsValid);
        var normalized = SceneCapture.Normalisations;
        using var again = Capture(doc, watch, scopedCache, [Crop(1e6)], out _);
        Assert.Equal(normalized, SceneCapture.Normalisations);
        Assert.False(again.Value.Bounds.IsValid); Assert.Equal(before, normalized);
    });

    [RhinoFact] public void MovingAScopedOutReferencedObjectIntoTheVolumeExpiresAndRecaptures() => host.Run(() =>
    {
        using var doc = RhinoDoc.CreateHeadless(null); using var shape = Shape;
        shape.Transform(Transform.Translation(8, 0, 0)); var id = doc.Objects.AddBrep(shape);
        using var cache = new SceneCache(); using var watch = new DocumentWatch(doc);
        CaptureInput[] inputs = [new(id, [0], "reference/" + id)];
        using var before = Capture(doc, watch, cache, [Crop()], out var skipped, inputs);
        Assert.Equal(1, skipped); Assert.Equal(0, before.Value.SourceCount);
        var generation = watch.Generation;
        Assert.NotEqual(Guid.Empty, doc.Objects.Transform(id, Transform.Translation(-8, 0, 0), true));
        watch.VerifyHostState(); Assert.True(watch.Generation > generation);
        using var after = Capture(doc, watch, cache, [Crop()], out skipped, inputs);
        Assert.Equal(0, skipped); Assert.Equal(1, after.Value.SourceCount);
        using var drawing = Draw(after.Value, Crop()); Assert.NotEmpty(drawing.Views[0].Items);
    });

    [RhinoFact] public void UnionUnboundedAndInputDerivedFramesKeepTheirOriginalSources() => host.Run(() =>
    {
        using var doc = RhinoDoc.CreateHeadless(null); using var shape = Shape;
        doc.Objects.AddBrep(shape); shape.Transform(Transform.Translation(8, 0, 0)); doc.Objects.AddBrep(shape);
        using var cache = new SceneCache(); using var watch = new DocumentWatch(doc);
        using var union = Capture(doc, watch, cache, [Crop(), Crop(8)], out var skipped);
        Assert.Equal(0, skipped); Assert.Equal(2, union.Value.SourceCount);
        using var unbounded = Capture(doc, watch, cache, [Crop(), ViewRequest.Parse("Top")], out skipped);
        Assert.Equal(0, skipped); Assert.Equal(2, unbounded.Value.SourceCount);
        var plane = ViewRequest.Parse(Plane.WorldXY);
        plane = plane with { Settings = Settings.Resolve([new("v", "v", ViewSourceKind.Plane, true, 10, .001)],
            [["depth=1 m"]], DocumentUnits.Metres).Views[0] };
        using var derived = Capture(doc, watch, cache, [Crop(), plane], out skipped);
        Assert.Equal(0, skipped); Assert.Equal(2, derived.Value.SourceCount);
    });

    [RhinoFact] public void AlternatingScopesReusePreparedEntriesAndOutsideEditsKeepContentIdentity() => host.Run(() =>
    {
        using var doc = RhinoDoc.CreateHeadless(null); using var shape = Shape;
        doc.Objects.AddBrep(shape); shape.Transform(Transform.Translation(8, 0, 0)); var outside = doc.Objects.AddBrep(shape);
        using var cache = new SceneCache(); using var watch = new DocumentWatch(doc);
        using var first = Capture(doc, watch, cache, [Crop()], out _);
        using var other = Capture(doc, watch, cache, [Crop(8)], out _);
        Assert.NotEqual(first.Value.Revision, other.Value.Revision);
        using var again = Capture(doc, watch, cache, [Crop()], out _);
        Assert.Same(first.Value, again.Value);
        var attributes = doc.Objects.FindId(outside).Attributes.Duplicate(); attributes.Name = "outside renamed";
        doc.Objects.ModifyAttributes(outside, attributes, true); watch.VerifyHostState();
        using var edited = Capture(doc, watch, cache, [Crop()], out _);
        Assert.Equal(first.Value.Revision, edited.Value.Revision);
    });

    [RhinoTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void FarScopingUsesTheCompleteInstanceParticipationChain(bool blocks) => host.Run(() =>
    {
        using var doc = RhinoDoc.CreateHeadless(null); using var shape = Shape;
        shape.Transform(Transform.Translation(0, 0, -20));
        Guid Add()
        {
            if (!blocks) return doc.Objects.AddBrep(shape);
            var definition = doc.InstanceDefinitions.Add("far-" + Guid.NewGuid(), "", Point3d.Origin, [shape], [new ObjectAttributes()]);
            return doc.Objects.AddInstanceObject(definition, Transform.Identity);
        }
        var selected = Add(); var excluded = Add();
        var view = Crop(); view = view with { Cut = view.Cut! with { SourceDepth = 10, ScopeEnabled = true, Objects = [selected] } };
        using var cache = new SceneCache(); using var watch = new DocumentWatch(doc);
        using var scoped = Capture(doc, watch, cache, [view], out var skipped);
        Assert.Equal(1, skipped); Assert.Equal(1, scoped.Value.SourceCount);
        Assert.Contains(excluded, Assert.Single(scoped.Value.Prepare(default)).Section.ObjectIds);
    });

    [RhinoTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void StoreyAndElevationBoxesExcludeAboveBelowAndBehind(bool elevation) => host.Run(() =>
    {
        using var doc = RhinoDoc.CreateHeadless(null);
        for (var floor = 0; floor < 10; floor++)
        {
            using var shape = Brep.CreateFromBox(new BoundingBox(new Point3d(-.5, -.5, floor * 4), new Point3d(.5, .5, floor * 4 + 3)));
            doc.Objects.AddBrep(shape);
        }
        var plane = elevation ? new Plane(new Point3d(0, -1, 18), Vector3d.XAxis, Vector3d.ZAxis)
            : new Plane(new Point3d(0, 0, 18), Vector3d.ZAxis);
        var view = ViewRequest.Parse(new Box(plane, new Interval(-1, 1), new Interval(-1, 1), new Interval(-2, 0)));
        using var cache = new SceneCache(); using var watch = new DocumentWatch(doc);
        using var full = BaselineTests.Capture(doc, cache);
        using var scoped = Capture(doc, watch, cache, [view], out var skipped);
        Assert.Equal(9, skipped); Assert.Equal(1, scoped.Value.SourceCount);
        Assert.Equal(view.Resolve(full.Value.Bounds, .001), view.Resolve(scoped.Value.Bounds, .001));
        using var a = Draw(full.Value, view); using var b = Draw(scoped.Value, view);
        HldFixtureComparison.Equal(a.Views[0], b.Views[0]);
    });

    [RhinoTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void ForegroundNonparticipantsRemainForRegionAdmission(bool blocks) => host.Run(() =>
    {
        using var doc = RhinoDoc.CreateHeadless(null); using var shape = Shape;
        shape.Transform(Transform.Translation(0, 0, 5));
        Guid Add()
        {
            if (!blocks) return doc.Objects.AddBrep(shape);
            var definition = doc.InstanceDefinitions.Add("front-" + Guid.NewGuid(), "", Point3d.Origin, [shape], [new ObjectAttributes()]);
            return doc.Objects.AddInstanceObject(definition, Transform.Identity);
        }
        var selected = Add(); var other = Add();
        var view = Crop(); view = view with { Cut = view.Cut! with { ScopeEnabled = true, Objects = [selected] } };
        using var cache = new SceneCache(); using var watch = new DocumentWatch(doc);
        using var scene = Capture(doc, watch, cache, [view], out var skipped);
        Assert.Equal(1, skipped); Assert.Equal(1, scene.Value.SourceCount);
        Assert.Contains(other, Assert.Single(scene.Value.Prepare(default)).Section.ObjectIds);
    });

    [RhinoFact] public void MixedNamedViewAndSceneFacedRectangleKeepTheWholeScene() => host.Run(() =>
    {
        using var doc = RhinoDoc.CreateHeadless(null); using var shape = Shape;
        doc.Objects.AddBrep(shape); shape.Transform(Transform.Translation(8, 0, 0)); doc.Objects.AddBrep(shape);
        using var cache = new SceneCache(); using var watch = new DocumentWatch(doc);
        var named = new ViewRequest("Named", -Vector3d.ZAxis) { Kind = ViewSourceKind.NamedView,
            Named = new(Plane.WorldXY, new(-1, 1), new(-1, 1)) };
        using var mixed = Capture(doc, watch, cache, [Crop(), named], out var skipped);
        Assert.Equal(0, skipped); Assert.Equal(2, mixed.Value.SourceCount);
        var rectangle = Crop() with { Kind = ViewSourceKind.Rectangle, SceneDerivedFrame = true };
        using var derived = Capture(doc, watch, cache, [rectangle], out skipped);
        Assert.Equal(0, skipped); Assert.Equal(2, derived.Value.SourceCount);
        var auto = ViewRequest.Parse(new Rectangle3d(Plane.WorldXY, new Interval(-1, 1), new Interval(-1, 1)));
        using var unbounded = Capture(doc, watch, cache, [auto], out skipped);
        Assert.Equal(0, skipped); Assert.Equal(2, unbounded.Value.SourceCount);
    });

    [RhinoTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void SurvivorBoundsCannotChangeFrameDepthOrFade(bool rectangle) => host.Run(() =>
    {
        using var doc = RhinoDoc.CreateHeadless(null); using var shape = Shape;
        doc.Objects.AddBrep(shape); shape.Transform(Transform.Translation(100, 0, -100)); doc.Objects.AddBrep(shape);
        var view = rectangle ? ViewRequest.Parse(new Rectangle3d(Plane.WorldXY, new Interval(-1, 1), new Interval(-1, 1))) : Crop();
        ViewRequest Resolve(BoundingBox bounds)
        {
            var sceneDepth = bounds.IsValid ? Math.Max(0, -bounds.GetCorners().Min(view.Cut!.Plane.DistanceTo)) + .01 : 0;
            var settings = Settings.Resolve([new("v", "v", view.Kind, true, sceneDepth, .001, view.Cut!.SourceDepth)],
                [[rectangle ? "depth=10 m" : "depth=auto", "fade=on", "beyond=3 m"]], DocumentUnits.Metres).Views[0];
            return view with { Settings = settings };
        }
        using var cache = new SceneCache(); using var watch = new DocumentWatch(doc);
        using var full = BaselineTests.Capture(doc, cache);
        using var scoped = Capture(doc, watch, cache, [Resolve(BoundingBox.Empty)], out var skipped);
        Assert.Equal(1, skipped);
        var a = Resolve(full.Value.Bounds); var b = Resolve(scoped.Value.Bounds);
        Assert.Equal(a.Resolve(full.Value.Bounds, .001), b.Resolve(scoped.Value.Bounds, .001));
        Assert.Equal(a.Settings!.Depth, b.Settings!.Depth);
        Assert.Equal(a.Settings.Beyond, b.Settings.Beyond);
        foreach (var d in new[] { 0d, 1, 5, 10 }) Assert.Equal(new ViewRange(a.Settings).Intensity(d), new ViewRange(b.Settings).Intensity(d));
        using var first = Draw(full.Value, a); using var second = Draw(scoped.Value, b);
        HldFixtureComparison.Equal(first.Views[0], second.Views[0]);
    });

    [RhinoFact] public void PrimitiveValuesAreExcludedBeforeGeometryConversion() => host.Run(() =>
    {
        using var doc = RhinoDoc.CreateHeadless(null); using var watch = new DocumentWatch(doc); using var cache = new SceneCache();
        var before = SceneCapture.Normalisations;
        CaptureInput[] inputs = [new(new Box(Plane.WorldXY, new Interval(100, 101), new Interval(0, 1), new Interval(-2, -1)), [0], "outside-box"),
            new(new Sphere(new Point3d(100, 0, -2), 1), [0], "outside-sphere")];
        using var scene = Capture(doc, watch, cache, [Crop()], out var skipped, inputs);
        Assert.Equal(2, skipped); Assert.Equal(0, scene.Value.SourceCount); Assert.Equal(before, SceneCapture.Normalisations);
    });

    [RhinoFact] public void PrimitiveBoundsEncloseRotatedCurvedValuesWithoutConversions() => host.Run(() =>
    {
        var plane = new Plane(new Point3d(3, -5, 2), new Vector3d(1, 2, 3));
        var cone = new Cone(plane, 4, 2); var cylinder = new Cylinder(new Circle(plane, 2), 4);
        var torus = new Torus(plane, 4, 1); var arc = new Arc(new Circle(plane, 2), 4);
        void Check(object value, GeometryBase geometry)
        {
            using (geometry)
            {
                var box = SceneCapture.PrimitiveBounds(value); box.Inflate(.000001);
                Assert.True(box.IsValid);
                Assert.All(geometry.GetBoundingBox(true).GetCorners(), p => Assert.True(box.Contains(p), $"{value.GetType().Name}: {p}"));
            }
        }
        Check(cone, cone.ToBrep(true)); Check(cylinder, cylinder.ToBrep(true, true));
        Check(torus, torus.ToBrep()); Check(arc, new ArcCurve(arc));
    });

    [RhinoFact] public void SmallDeepSurvivorKeepsItsLocusWhenTheCameraRadiusShrinks() => host.Run(() =>
    {
        using var doc = RhinoDoc.CreateHeadless(null); using var shape = Shape;
        shape.Transform(Transform.Translation(0, 0, -500)); doc.Objects.AddBrep(shape);
        using var outside = Brep.CreateFromBox(new BoundingBox(new Point3d(1000, 1000, 1000), new Point3d(2000, 2000, 2000)));
        doc.Objects.AddBrep(outside);
        var view = Crop(); view = view with { Cut = view.Cut! with { SourceDepth = 1000 } };
        using var cache = new SceneCache(); using var watch = new DocumentWatch(doc);
        using var full = BaselineTests.Capture(doc, cache);
        using var scoped = Capture(doc, watch, cache, [view], out var skipped);
        Assert.Equal(1, skipped); Assert.True(scoped.Value.Bounds.Diagonal.Length < 2);
        Assert.True(full.Value.Bounds.Diagonal.Length > 1000);
        using var a = Draw(full.Value, view); using var b = Draw(scoped.Value, view);
        Assert.NotEmpty(a.Views[0].Items); Assert.NotEmpty(b.Views[0].Items);
        HldFixtureComparison.Equal(a.Views[0], b.Views[0]);
    });

    [RhinoFact] public void NamedViewsNeverScopeCapture() => host.Run(() =>
    {
        var named = ViewRequest.Parse("Top") with { Named = new(Plane.WorldXY, new Interval(-1, 1), new Interval(-1, 1)) };
        ViewRequest Depth(double depth) => named with { Settings = Settings.Resolve([new("v", "v", ViewSourceKind.NamedView, false, 0, .001)],
            [[$"depth={depth} m", "farclip=noline"]], DocumentUnits.Metres).Views[0] };
        Assert.Equal("unbounded", SceneCapture.ScopeKey([Depth(10)], .001));
        Assert.Equal("unbounded", SceneCapture.ScopeKey([Depth(20)], .001));
        ViewRequest NoFar(double depth) => named with { Settings = Settings.Resolve([new("v", "v", ViewSourceKind.NamedView, false, 0, .001)],
            [[$"depth={depth} m", "farclip=none"]], DocumentUnits.Metres).Views[0] };
        Assert.Equal(SceneCapture.ScopeKey([NoFar(10)], .001), SceneCapture.ScopeKey([NoFar(20)], .001));
    });

    [RhinoFact] public void WholeSceneCodecPreservesEmptyBoundsAndVisibilityIdentity() => host.Run(() =>
    {
        using var doc = RhinoDoc.CreateHeadless(null); using var cache = new SceneCache();
        using var scene = BaselineTests.Capture(doc, cache);
        using var restored = WorkerCodec.RestoreScene(WorkerCodec.Capture(scene.Value, default));
        Assert.False(restored.Bounds.IsValid); Assert.Equal(scene.Value.Bounds, restored.Bounds);
        Assert.Equal(scene.Value.VisibilityRevision, restored.VisibilityRevision);
    });

    [RhinoFact] public void WholeSceneTransportKeepsSharedRevisionAndSurvivorBounds() => host.Run(() =>
    {
        using var doc = RhinoDoc.CreateHeadless(null); using var shape = Shape;
        doc.Objects.AddBrep(shape); shape.Transform(Transform.Translation(8, 0, 0)); doc.Objects.AddBrep(shape);
        using var cache = new SceneCache(); using var watch = new DocumentWatch(doc);
        using var full = BaselineTests.Capture(doc, cache);
        using var scoped = Capture(doc, watch, cache, [Crop()], out _);
        using var restored = WorkerCodec.RestoreScene(WorkerCodec.Capture(scoped.Value, default));
        Assert.Equal(4, WorkerCodec.Schema);
        Assert.Equal(scoped.Value.Revision, restored.Revision);
        Assert.Equal(scoped.Value.Bounds, restored.Bounds);
        Assert.Equal(scoped.Value.VisibilityRevision, restored.VisibilityRevision);
        using var a = Draw(full.Value, Crop()); using var b = Draw(restored, Crop());
        HldFixtureComparison.Equal(a.Views[0], b.Views[0]);
    });

    private static void CompareInputs(PreparedScene full, PreparedScene scoped, ViewRequest view)
    {
        string Inputs(PreparedScene scene)
        {
            var hashes = new List<string>();
            using var batch = NativeDrawing.SolveBorrowedAsync(scene, new(scene.Revision, "edge", "test", 1), [view],
                new() { ObserveNativeInputs = inputs => hashes.Add(WorkerCodec.Hash(WorkerCodec.Encode(inputs.Select(i => new {
                    Geometry = WorkerCodec.Archive(i.Geometry), i.Source.Identity, i.Source.Path, i.Source.SourceItem, i.Source.Section, i.Far }).ToArray()))) }, default, null).GetAwaiter().GetResult();
            Assert.Null(batch.Views[0].Error);
            return string.Join("/", hashes);
        }
        Assert.Equal(Inputs(full), Inputs(scoped));
        using var a = Draw(full, view); using var b = Draw(scoped, view);
        HldFixtureComparison.Equal(a.Views[0], b.Views[0]);
    }

    [RhinoTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void TransformedInstancesAtCropEdgeMatchFullCapture(bool nonuniform, bool nested) => host.Run(() =>
    {
        using var doc = RhinoDoc.CreateHeadless(null); using var shape = Shape;
        var definition = doc.InstanceDefinitions.Add("edge", "", Point3d.Origin, [shape], [new ObjectAttributes()]);
        if (nested)
        {
            using var reference = new InstanceReferenceGeometry(doc.InstanceDefinitions[definition].Id, Transform.Rotation(.2, Vector3d.ZAxis, Point3d.Origin));
            definition = doc.InstanceDefinitions.Add("edge-outer", "", Point3d.Origin, [reference], [new ObjectAttributes()]);
        }
        var transform = Transform.Translation(.95, 0, 0) * Transform.Rotation(.63, Vector3d.ZAxis, Point3d.Origin);
        if (nonuniform) transform *= Transform.Scale(Plane.WorldXY, 2, .4, 1.3);
        doc.Objects.AddInstanceObject(definition, transform);
        using var cache = new SceneCache(); using var watch = new DocumentWatch(doc);
        using var full = BaselineTests.Capture(doc, cache);
        using var scoped = Capture(doc, watch, cache, [Crop()], out _);
        Assert.Equal(1, scoped.Value.SourceCount); CompareInputs(full.Value, scoped.Value, Crop());
    });

    [RhinoFact] public void EditedInnerDefinitionNearCropEdgeDoesNotUseStoredInstanceBounds() => host.Run(() =>
    {
        using var doc = RhinoDoc.CreateHeadless(null); using var shape = Shape;
        shape.Transform(Transform.Translation(8, 0, 0));
        var inner = doc.InstanceDefinitions.Add("inner", "", Point3d.Origin, [shape], [new ObjectAttributes()]);
        using var reference = new InstanceReferenceGeometry(doc.InstanceDefinitions[inner].Id, Transform.Identity);
        var outer = doc.InstanceDefinitions.Add("outer", "", Point3d.Origin, [reference], [new ObjectAttributes()]);
        doc.Objects.AddInstanceObject(outer, Transform.Identity);
        using var cache = new SceneCache(); using var watch = new DocumentWatch(doc);
        using var before = Capture(doc, watch, cache, [Crop()], out _);
        Assert.Equal(0, before.Value.SourceCount);
        shape.Transform(Transform.Translation(-7.05, 0, 0));
        Assert.True(doc.InstanceDefinitions.ModifyGeometry(inner, [shape], [new ObjectAttributes()]));
        watch.VerifyHostState();
        using var scoped = Capture(doc, watch, cache, [Crop()], out _);
        using var fresh = new SceneCache(); using var full = BaselineTests.Capture(doc, fresh);
        Assert.Equal(1, scoped.Value.SourceCount); CompareInputs(full.Value, scoped.Value, Crop());
    });

    [RhinoTheory]
    [InlineData("extrusion_solid")]
    [InlineData("subd_rounded_cube")]
    [InlineData("surface_trimmed")]
    public void ConversionTypesAtCropEdgeMatchFullCaptureAndNormalizeOnce(string fixture) => host.Run(() =>
    {
        using var doc = RhinoDoc.CreateHeadless(null);
        if (fixture == "surface_trimmed")
        {
            using var surface = new PlaneSurface(Plane.WorldXY, new Interval(-2, 2), new Interval(-2, 2));
            using var trimmed = surface.Trim(new Interval(-.2, .2), new Interval(-.3, .3));
            doc.Objects.AddSurface(trimmed);
        }
        else
        {
            using var source = RhinoDoc.OpenHeadless(Path.Combine(AppContext.BaseDirectory, "fixtures", fixture + ".3dm"));
            foreach (var obj in source.Objects.GetObjectList(ObjectType.AnyObject)) doc.Objects.Add(obj.Geometry);
        }
        var box = BoundingBox.Empty;
        foreach (var obj in doc.Objects.GetObjectList(ObjectType.AnyObject)) box.Union(obj.Geometry.GetBoundingBox(true));
        foreach (var obj in doc.Objects.GetObjectList(ObjectType.AnyObject).ToArray())
            doc.Objects.Transform(obj.Id, Transform.Translation(.95 - box.Center.X, -box.Center.Y, -2 - box.Max.Z), true);
        using var cache = new SceneCache(); using var watch = new DocumentWatch(doc);
        var before = SceneCapture.Normalisations;
        using var scoped = Capture(doc, watch, cache, [Crop()], out _);
        var after = SceneCapture.Normalisations;
        Assert.Equal(scoped.Value.SourceCount, after - before);
        using var again = Capture(doc, watch, cache, [Crop()], out _);
        Assert.Equal(after, SceneCapture.Normalisations);
        using var fresh = new SceneCache(); using var full = BaselineTests.Capture(doc, fresh);
        Assert.True(scoped.Value.SourceCount > 0); CompareInputs(full.Value, scoped.Value, Crop());
    });
}
