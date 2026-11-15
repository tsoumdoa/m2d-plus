using System.Diagnostics;
using M2DPlus.Core;
using M2DPlus.Rhino.Services;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using Xunit;
using Xunit.Abstractions;
using Point = Rhino.Geometry.Point;
using Environment = System.Environment;

namespace M2DPlus.Rhino.Tests;

[Collection("Rhino")]
[Trait("Category", "RhinoIntegration")]
public sealed class BaselineTests(RhinoHost host, ITestOutputHelper output)
{
    internal static LeaseEntry<PreparedScene>.Lease Capture(RhinoDoc document, SceneCache cache, IEnumerable<CaptureInput>? inputs = null)
    {
        using var watch = new DocumentWatch(document);
        using var capture = new SceneCapture(document, watch, inputs, cache);
        while (!capture.Step(TimeSpan.FromMilliseconds(12))) { }
        return capture.Accept();
    }
    internal static DrawingBatch Solve(LeaseEntry<PreparedScene>.Lease scene, params ViewRequest[] views) =>
        NativeDrawing.SolveAsync(scene, new(scene.Value.Revision, string.Join("|", views.Select(v => v.Revision)), "default", 1), views, new(), default).GetAwaiter().GetResult();

    [RhinoFact] public void AllDeclaredPrimitiveValuesBecomeOwnedAndNonuniformCurvesDoNotDistortSilently() => host.Run(() =>
    {
        using var document = RhinoDoc.CreateHeadless(null); using var cache = new SceneCache();
        var circle = new Circle(Plane.WorldXY, 1);
        var values = new object[] { new Box(new BoundingBox(Point3d.Origin, new Point3d(1, 2, 3))),
            new BoundingBox(Point3d.Origin, new Point3d(1, 2, 3)), new Sphere(Point3d.Origin, 1),
            new Cylinder(circle, 2), new Cone(Plane.WorldXY, 2, 1), new Torus(Plane.WorldXY, 2, .5),
            new Rectangle3d(Plane.WorldXY, 2, 3), new Line(Point3d.Origin, new Point3d(1, 2, 3)),
            new Polyline(new[] { Point3d.Origin, new Point3d(1, 0, 0), new Point3d(1, 1, 0) }),
            new Arc(circle, Math.PI), circle, new Point3d(1, 2, 3) };
        using var lease = Capture(document, cache, values.Select((v, i) => new CaptureInput(v, new[] { i }, "primitive/" + i)));
        Assert.Equal(values.Length, lease.Value.SourceCount); Assert.Empty(lease.Value.Unsupported);
        var prepared = lease.Value.Prepare(default); Assert.All(prepared, p => Assert.True(p.Geometry.IsValid));
        using var volume = VolumeMassProperties.Compute((Brep)prepared[0].Geometry); Assert.Equal(6, volume.Volume, 7);
    });

    [RhinoFact] public void DeepBlocksComposeNonuniformScaleAndMirrorWithAnalyticEllipseAndBox() => host.Run(() =>
    {
        using var document = RhinoDoc.CreateHeadless(null); using var cache = new SceneCache();
        using var circle = new ArcCurve(new Circle(Plane.WorldXY, 1));
        using var box = Brep.CreateFromBox(new BoundingBox(Point3d.Origin, new Point3d(1, 2, 3)));
        var index = document.InstanceDefinitions.Add("leaf", "", Point3d.Origin, new GeometryBase[] { circle, box }, new[] { new ObjectAttributes(), new ObjectAttributes() });
        for (var depth = 0; depth < 80; depth++)
        {
            using var nested = new InstanceReferenceGeometry(document.InstanceDefinitions[index].Id, Transform.Translation(1, 0, 0));
            index = document.InstanceDefinitions.Add("nested" + depth, "", Point3d.Origin, new[] { nested }, new[] { new ObjectAttributes() });
        }
        document.Objects.AddInstanceObject(index, Transform.Scale(Plane.WorldXY, -2, 3, .5));
        using var lease = Capture(document, cache); Assert.Equal(2, lease.Value.SourceCount); Assert.Empty(lease.Value.Unsupported);
        var prepared = lease.Value.Prepare(default);
        var curve = Assert.IsAssignableFrom<Curve>(prepared.Single(p => p.Geometry is Curve).Geometry);
        var bounds = curve.GetBoundingBox(true);
        // Eighty translations, then x'=-2x, y'=3y; the circle is an ellipse centred at (-160,0).
        Assert.Equal(-162, bounds.Min.X, 6); Assert.Equal(-158, bounds.Max.X, 6);
        Assert.Equal(-3, bounds.Min.Y, 6); Assert.Equal(3, bounds.Max.Y, 6);
        for (var i = 0; i < 97; i++)
        { var p = curve.PointAtNormalizedLength(i / 96d); Assert.InRange(Math.Abs(Math.Pow((p.X + 160) / 2, 2) + Math.Pow(p.Y / 3, 2) - 1), 0, 1e-7); }
        var solid = Assert.IsType<Brep>(prepared.Single(p => p.Geometry is Brep).Geometry);
        using var mass = VolumeMassProperties.Compute(solid); Assert.Equal(18, mass.Volume, 6); Assert.Equal(BrepSolidOrientation.Outward, solid.SolidOrientation);
        Assert.Equal(-162, solid.GetBoundingBox(true).Min.X, 6); Assert.Equal(-160, solid.GetBoundingBox(true).Max.X, 6);
    });

    /// <summary>Owner decision 2026-11-15: a layer name selects every object on matching layers whatever its visibility
    /// (hidden objects, layers that are off, a parent that is off), like referencing them. Bakes stay excluded.</summary>
    [RhinoFact] public void LayerStringsRespectSourcePathsAndIgnoreVisibility() => host.Run(() =>
    {
        using var document = RhinoDoc.CreateHeadless(null); using var cache = new SceneCache();
        var walls = document.Layers.Add("Walls", System.Drawing.Color.Black);
        var structure = document.Layers.Add("Structure", System.Drawing.Color.Black);
        var child = new Layer { Name = "Columns", ParentLayerId = document.Layers[structure].Id }; var columns = document.Layers.Add(child);
        var other = document.Layers.Add("Other", System.Drawing.Color.Black);
        var wallId = document.Objects.AddPoint(new Point3d(1, 0, 0), new ObjectAttributes { LayerIndex = walls });
        var columnId = document.Objects.AddPoint(new Point3d(2, 0, 0), new ObjectAttributes { LayerIndex = columns });
        document.Objects.AddPoint(new Point3d(3, 0, 0), new ObjectAttributes { LayerIndex = other });
        var hidden = document.Objects.AddPoint(new Point3d(4, 0, 0), new ObjectAttributes { LayerIndex = walls }); Assert.True(document.Objects.Hide(hidden, true));
        var doors = document.Layers.Add("Doors", System.Drawing.Color.Black);
        var doorId = document.Objects.AddPoint(new Point3d(5, 0, 0), new ObjectAttributes { LayerIndex = doors });
        var baked = new ObjectAttributes { LayerIndex = walls }; baked.SetUserString("m2d.class", "visible");
        document.Objects.AddPoint(new Point3d(6, 0, 0), baked);
        // The Doors layer and the Structure parent (so Structure::Columns) are switched off.
        document.Layers[doors].IsVisible = false; document.Layers[structure].IsVisible = false;
        Assert.False(document.Layers[doors].IsVisible); Assert.False(document.Layers[structure].IsVisible);
        using var batch = Solve(Capture(document, cache, new[] { new CaptureInput("Walls", new[] { 2, 1 }, "walls"), new CaptureInput("Structure::*", new[] { 7 }, "structure"),
            new CaptureInput("Doors", new[] { 8 }, "doors") }), ViewRequest.Parse("Top"));
        var items = batch.Views[0].Items; Assert.Equal(4, items.Count);
        Assert.Contains(items, p => p.SourceIdentity.EndsWith(wallId.ToString()) && p.SourcePath.SequenceEqual(new[] { 2, 1 }));
        Assert.Contains(items, p => p.SourceIdentity.EndsWith(hidden.ToString()) && p.SourcePath.SequenceEqual(new[] { 2, 1 }));
        Assert.Contains(items, p => p.SourceIdentity.EndsWith(columnId.ToString()) && p.SourcePath.SequenceEqual(new[] { 7 }));
        Assert.Contains(items, p => p.SourceIdentity.EndsWith(doorId.ToString()) && p.SourcePath.SequenceEqual(new[] { 8 }));
    });

    [RhinoFact] public void LinkedArchiveWithDeepDefinitionsIsExpandedOnTheHost() => host.Run(() =>
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "artifacts", "linked"); Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, Guid.NewGuid() + ".3dm");
        using var source = RhinoDoc.CreateHeadless(null);
        source.ModelUnitSystem = UnitSystem.Meters;
        using var box = Brep.CreateFromBox(new BoundingBox(Point3d.Origin, new Point3d(1, 2, 3)));
        var leaf = source.InstanceDefinitions.Add("leaf", "", Point3d.Origin, new[] { box }, new[] { new ObjectAttributes() });
        var index = leaf;
        for (var depth = 0; depth < 24; depth++)
        {
            using var nested = new InstanceReferenceGeometry(source.InstanceDefinitions[index].Id, Transform.Translation(1, 0, 0));
            index = source.InstanceDefinitions.Add("deep" + depth, "", Point3d.Origin, new[] { nested }, new[] { new ObjectAttributes() });
        }
        source.Objects.AddInstanceObject(index, Transform.Identity);
        Assert.True(source.Write3dmFile(file, new global::Rhino.FileIO.FileWriteOptions()));
        using var document = RhinoDoc.CreateHeadless(null); document.ModelUnitSystem = UnitSystem.Meters;
        using var cache = new SceneCache();
        var linked = document.InstanceDefinitions.Add("linked", "", Point3d.Origin, new[] { box }, new[] { new ObjectAttributes() });
        using var fileReference = global::Rhino.FileIO.FileReference.CreateFromFullPath(file);
        Assert.True(document.InstanceDefinitions.ModifySourceArchive(linked, fileReference, InstanceDefinitionUpdateType.Linked, true));
        Assert.True(document.InstanceDefinitions.UpdateLinkedInstanceDefinition(linked, file, true, true));
        Assert.Equal(InstanceDefinitionUpdateType.Linked, document.InstanceDefinitions[linked].UpdateType);
        document.Objects.AddInstanceObject(linked, Transform.Translation(100, 0, 0));
        using var scene = Capture(document, cache); Assert.Equal(1, scene.Value.SourceCount); Assert.Empty(scene.Value.Unsupported);
        var prepared = Assert.Single(scene.Value.Prepare(default));
        var bounds = prepared.Geometry.GetBoundingBox(true);
        Assert.Equal(124, bounds.Min.X, 6); Assert.Equal(125, bounds.Max.X, 6); Assert.Equal(2, bounds.Max.Y, 6);
        using var volume = VolumeMassProperties.Compute((Brep)prepared.Geometry); Assert.Equal(6, volume.Volume, 6);
    });

    [RhinoFact] public void NamedViewWinsOverKeywordKeepsRolledAxesAndCropsCurvesPointsAndOcclusion() => host.Run(() =>
    {
        using var document = RhinoDoc.CreateHeadless(null); using var cache = new SceneCache();
        document.ModelAbsoluteTolerance = 1e-7;
        var lineId = document.Objects.AddLine(new Point3d(-5, 0, -1), new Point3d(5, 0, -1));
        document.Objects.AddPoint(new Point3d(.7, 0, -1)); document.Objects.AddPoint(new Point3d(5, 0, -1));
        using var occluder = Brep.CreateFromBox(new BoundingBox(new Point3d(-.3, -.3, 0), new Point3d(.3, .3, 1))); document.Objects.AddBrep(occluder);
        using var offscreen = Brep.CreateFromBox(new BoundingBox(new Point3d(10, 10, 0), new Point3d(11, 11, 1))); document.Objects.AddBrep(offscreen);
        using var viewport = new ViewportInfo(); viewport.IsParallelProjection = true;
        viewport.SetCameraLocation(new Point3d(0, 0, 10)); viewport.SetCameraDirection(-Vector3d.ZAxis);
        viewport.SetCameraUp(Vector3d.XAxis); viewport.SetFrustum(-2, 2, -1, 1, .1, 100);
        viewport.SetScreenPort(new System.Drawing.Rectangle(0, 0, 800, 400), 0, 1);
        using var sourceViewport = new global::Rhino.Display.RhinoViewport();
        Assert.True(sourceViewport.SetViewProjection(viewport, false));
        using var view = new ViewInfo(sourceViewport) { Name = "Top" };
        var added = document.NamedViews.Add(view); Assert.Equal(0, added);
        output.WriteLine($"Named add={added}, count={document.NamedViews.Count}, original={ViewRequest.Parse(view).Revision}; table={string.Join(';', document.NamedViews.Select(v => ViewRequest.Parse(v).Revision))}");
        var namedRequest = ViewRequest.Parse("top", document); Assert.True(namedRequest.Named is not null, namedRequest.Error);
        Assert.Equal("Top", namedRequest.Name); Assert.Equal(Vector3d.XAxis, namedRequest.Named.Camera.YAxis);
        var request = namedRequest;
        Assert.True(request.Named.Camera.YAxis.IsParallelTo(Vector3d.XAxis) == 1);
        using var batch = Solve(Capture(document, cache), request, ViewRequest.Parse("Front", document));
        Assert.All(batch.Views, v => Assert.Null(v.Error));
        var cropped = batch.Views[0]; Assert.False(cropped.Report.Quarantined, cropped.Info); Assert.Equal(8, cropped.Items.Count);
        var lines = cropped.Items.Where(p => p.SourceIdentity == lineId.ToString()).ToArray(); Assert.Equal(3, lines.Length);
        var lineBounds = BoundingBox.Empty;
        foreach (var piece in lines) { using var g = piece.CopyGeometry(); lineBounds.Union(g.GetBoundingBox(true)); }
        Assert.Equal(-1, lineBounds.Min.X, 6); Assert.Equal(1, lineBounds.Max.X, 6);
        using var hiddenLine = Assert.Single(lines, p => p.Class == LineClass.Hidden).CopyGeometry();
        Assert.Equal(-.3, hiddenLine.GetBoundingBox(true).Min.X, 5); Assert.Equal(.3, hiddenLine.GetBoundingBox(true).Max.X, 5);
        Assert.Equal(4, cropped.Frame.Width, 6); Assert.Equal(2, cropped.Frame.Height, 6);
        var uncropped = request with { Named = request.Named with { Crop = false } };
        using var full = Solve(Capture(document, cache), uncropped); Assert.Equal(13, full.Views[0].Items.Count);
        using var perspective = new ViewportInfo(viewport); perspective.IsParallelProjection = false;
        using var partial = Solve(Capture(document, cache), ViewRequest.Parse(perspective), ViewRequest.Parse("Front"));
        Assert.Contains("Set the named view to Parallel", partial.Views[0].Error); Assert.Empty(partial.Views[0].Items); Assert.Null(partial.Views[1].Error);
        using var watch = new DocumentWatch(document); var generation = watch.Generation;
        document.NamedViews.Rename(0, "Changed"); watch.VerifyHostState(); Assert.True(watch.Generation > generation);
    });

    [RhinoFact] public void StableOutputOrderSurvivesPermutationCacheRetirementAndRecompute() => host.Run(() =>
    {
        using var document = RhinoDoc.CreateHeadless(null); using var cache = new SceneCache(100);
        using var box = Brep.CreateFromBox(new BoundingBox(new Point3d(-1, -1, 0), new Point3d(1, 1, 1)));
        var values = new[] { new CaptureInput(new Line(new Point3d(-3, 0, -1), new Point3d(3, 0, -1)), new[] { 10 }, "line"),
            new CaptureInput(new Line(new Point3d(4, 0, 0), new Point3d(5, 0, 0)), new[] { 2 }, "other"),
            new CaptureInput(box, new[] { 10 }, "box") };
        var firstScene = Capture(document, cache, values); var revision = firstScene.Value.Revision;
        using var first = Solve(firstScene, ViewRequest.Parse("Top")); cache.Clear();
        var secondScene = Capture(document, cache, values.AsEnumerable().Reverse()); Assert.Equal(revision, secondScene.Value.Revision);
        using var second = Solve(secondScene, ViewRequest.Parse("Top"));
        Assert.Equal(first.Views[0].Items.Select(p => p.SourceIdentity + p.GeometricKey + p.Class), second.Views[0].Items.Select(p => p.SourceIdentity + p.GeometricKey + p.Class));
        output.WriteLine(first.Views[0].Info + string.Join(';', first.Views[0].Items.Select(p => $"{p.SourceIdentity}[{string.Join(',', p.SourcePath)}]")));
        Assert.Equal("other", first.Views[0].Items[0].SourceIdentity); Assert.InRange(cache.RetainedBytes, 0, 100);
        Assert.Equal(new[] { "other", "box", "line" }, first.Views[0].Items.Select(p => p.SourceIdentity).Distinct());
        var keys = first.Views[0].Items.Where(p => p.SourceIdentity == "line").Select(p => p.GeometricKey).ToArray();
        Assert.Equal(3, keys.Length); Assert.Equal(keys.Order(StringComparer.Ordinal), keys);
    });

    [RhinoFact] public void BinaryFingerprintDetectsInteriorGeometryAndUserDataEdits() => host.Run(() =>
    {
        using var curve = new PolylineCurve(new[] { new Point3d(0, 0, 0), new Point3d(1, .2, 0), new Point3d(2, 0, 0) });
        var hash = GeometryFingerprint.Compute(curve); using var copy = curve.DuplicateCurve();
        Assert.Equal(hash, GeometryFingerprint.Compute(copy)); copy.SetUserString("test", "value"); Assert.NotEqual(hash, GeometryFingerprint.Compute(copy));
        using var a = new PolylineCurve(new[] { new Point3d(0, 0, 0), new Point3d(1, .2, 0), new Point3d(2, 2, 0) });
        using var b = new PolylineCurve(new[] { new Point3d(0, 0, 0), new Point3d(1, .8, 0), new Point3d(2, 2, 0) });
        Assert.Equal(a.GetBoundingBox(true), b.GetBoundingBox(true)); Assert.NotEqual(GeometryFingerprint.Compute(a), GeometryFingerprint.Compute(b));
    });

    [RhinoFact] public void ExpectedFailureNativeMixedLinePointUnsetIsReportedAndQuarantines() => host.Run(() =>
    {
        using var document = RhinoDoc.CreateHeadless(null); using var cache = new SceneCache();
        var inputs = new[] { new CaptureInput(new Line(new Point3d(-3, 0, 0), new Point3d(3, 0, 0)), new[] { 0 }, "line"),
            new CaptureInput(new Point3d(4, 0, 0), new[] { 0 }, "point") };
        var lease = Capture(document, cache, inputs);
        using var batch = NativeDrawing.SolveAsync(lease, new(lease.Value.Revision, "Top", "native", 1), new[] { ViewRequest.Parse("Top") }, new(NativeOnly: true), default).GetAwaiter().GetResult(); var view = batch.Views[0];
        // F property, not a passing visibility claim: expected point (4,0) is absent in this native build.
        // An improvement deliberately fails this assertion so reviewers can upgrade the gate to P.
        Assert.True(view.Report.Quarantined, view.Info); Assert.Equal(1, view.Report.InvalidExtractionCount);
        Assert.Contains("Invalid native point extraction", view.Diagnostics);
        Assert.All(view.Items, p => Assert.Equal(LineClass.Unresolved, p.Class)); Assert.Single(view.Items);
    });

    [RhinoFact] public void BuildingSixViewsIsGatedForCompletionAndQualityButNotElapsedTime() => host.Run(() =>
    {
        var path = Environment.GetEnvironmentVariable("M2D_BUILDING_FIXTURE") ?? "C:/Users/tomosandego/.t3/scratch/m2d-plus-research-20261114/fixtures/repeated_building_305.3dm";
        Assert.True(File.Exists(path), "Set M2D_BUILDING_FIXTURE to the research 305-object fixture.");
        using var document = RhinoDoc.OpenHeadless(path); using var cache = new SceneCache();
        var timer = Stopwatch.StartNew(); var scene = Capture(document, cache);
        var cost = scene.Value.Cost; Assert.Equal(305, scene.Value.SourceCount); var captureMs = timer.Elapsed.TotalMilliseconds;
        using var batch = Solve(scene, new[] { "Top", "Front", "Right", "Back", "Left", "SW" }.Select(v => ViewRequest.Parse(v)).ToArray());
        Assert.Equal(6, batch.Views.Count); Assert.All(batch.Views, v => { Assert.Null(v.Error); Assert.False(v.Report.Quarantined, v.Info); Assert.NotEmpty(v.Items); Assert.Equal(0, v.Report.InvalidExtractionCount); });
        output.WriteLine($"305 objects / six views: capture={captureMs:F1} ms; total={timer.Elapsed.TotalSeconds:F3}s; estimate={cost.Estimate(6)}; peak process={Process.GetCurrentProcess().PeakWorkingSet64}; per-view={string.Join(',', batch.Views.Select(v => v.Seconds.ToString("F3")))}");
    });

    [RhinoFact] public void EstimateRefusalReleasesLeaseWithoutPreparingOrEnteringNative() => host.Run(() =>
    {
        using var document = RhinoDoc.CreateHeadless(null); using var cache = new SceneCache();
        using var box = Brep.CreateFromBox(new BoundingBox(Point3d.Origin, new Point3d(1, 1, 1))); document.Objects.AddBrep(box);
        var lease = Capture(document, cache); var scene = lease.Value;
        var views = Enumerable.Repeat(ViewRequest.Parse("Top"), 8192).ToArray();
        var error = Assert.Throws<InvalidOperationException>(() => Solve(lease, views));
        Assert.Contains(SceneCost.Refusal, error.Message); Assert.Equal(0, scene.PreparationCount);
        Assert.Throws<ObjectDisposedException>(() => lease.Value);
    });

    [RhinoFact] public void FiveThousandSimpleBoxesAreAdmittedAndActuallyDrawn() => host.Run(() =>
    {
        using var document = RhinoDoc.CreateHeadless(null); using var cache = new SceneCache();
        for (var i = 0; i < 5000; i++)
        { var x = i % 100 * 3; var y = i / 100 * 3; using var b = Brep.CreateFromBox(new BoundingBox(new Point3d(x, y, 0), new Point3d(x + 1, y + 1, 1))); document.Objects.AddBrep(b); }
        var timer = Stopwatch.StartNew(); var scene = Capture(document, cache); var captureMs = timer.Elapsed.TotalMilliseconds; var cost = scene.Value.Cost;
        Assert.True(cost.Admitted(6, NativeScheduler.Global.BudgetBytes));
        using var batch = Solve(scene, ViewRequest.Parse("Top")); var view = Assert.Single(batch.Views);
        Assert.Null(view.Error); Assert.False(view.Report.Quarantined, view.Info); Assert.Equal(20000, view.Items.Count);
        output.WriteLine($"5000 boxes: capture={captureMs:F1} ms; HLD/extraction={view.Seconds:F3}s; total={timer.Elapsed.TotalSeconds:F3}s; bytes={cost.GeometryBytes}; faces={cost.BrepFaces}; estimate6={cost.Estimate(6)}; peak process={Process.GetCurrentProcess().PeakWorkingSet64}");
    });
}
