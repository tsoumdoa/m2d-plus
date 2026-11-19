using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Parameters;
using Grasshopper.Kernel.Types;
using M2DPlus.Core;
using M2DPlus.Rhino.Experimental.PipeVisibility;
using M2DPlus.Rhino.Services;
using Rhino;
using Rhino.FileIO;
using Rhino.Geometry;
using Xunit;
using Xunit.Abstractions;

namespace M2DPlus.Rhino.Tests;

[Collection("Rhino")]
public sealed class PipeTrialTests(RhinoHost host, ITestOutputHelper output)
{
    private static string Models => Environment.GetEnvironmentVariable("M2D_MODELS") ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".t3", "m2d-plus-models");
    private static string Model => Path.Combine(Models, "owner", "pipes-node-gdas.3dm");
    private static ViewRequest View(PreparedScene scene, string name, string engine = "pipes", object? source = null,
        string extra = "")
    {
        var view = ViewRequest.Parse(source ?? name);
        return view with { Name = name, Settings = Settings.Resolve([new(name, name, view.Kind, view.Cut is not null,
            scene.Bounds.Diagonal.Length, scene.Tolerance, view.Cut?.SourceDepth)],
            [[$"visibility={engine}\nhidden=on\ntangents=on\nfarclip=none\n{extra}"]], DocumentUnits.Millimetres).Views[0] };
    }
    private static RequestIdentity Identity(PreparedScene scene) => new(scene.Revision, "pipe-trial", "trial", 1);
    private static object Summary(ViewDrawing drawing) => new
    {
        drawing.Name, drawing.Seconds, drawing.Report.Route, drawing.NativeCalls, drawing.Error,
        Visible = drawing.Items.Count(p => p.Class == LineClass.Visible),
        Hidden = drawing.Items.Count(p => p.Class == LineClass.Hidden),
        Unresolved = drawing.Items.Count(p => p.Class == LineClass.Unresolved),
        VisibleLength = Length(drawing, LineClass.Visible), HiddenLength = Length(drawing, LineClass.Hidden),
        UnresolvedLength = Length(drawing, LineClass.Unresolved),
        Curves = drawing.Items.Count(p => { using var g = p.CopyGeometry(); return g is Curve; }),
        drawing.Info
    };
    private static double Length(ViewDrawing drawing, LineClass kind) => drawing.Items.Where(p => p.Class == kind).Sum(p =>
    { using var geometry = p.CopyGeometry(); return geometry is Curve curve ? curve.GetLength() : 0; });

    private static void Export(string name, ViewDrawing drawing)
    {
        var folder = Environment.GetEnvironmentVariable("M2D_PIPE_TRIAL_EVIDENCE");
        if (folder is null) return;
        var path = Path.Combine(folder, name + $"-net{Environment.Version.Major}.3dm");
        if (File.Exists(path)) throw new IOException("Preserve existing evidence: " + path);
        using var model = new File3dm(); model.Settings.ModelUnitSystem = UnitSystem.Millimeters;
        foreach (var group in drawing.Items.GroupBy(p => p.Class))
        {
            var index = model.AllLayers.Count;
            model.AllLayers.Add(new global::Rhino.DocObjects.Layer { Name = group.Key.ToString() });
            foreach (var piece in group)
            {
                using var geometry = piece.CopyGeometry();
                model.Objects.Add(geometry, new global::Rhino.DocObjects.ObjectAttributes
                    { LayerIndex = index, Name = group.Key + "|" + piece.SourceIdentity });
            }
        }
        var frameLayer = model.AllLayers.Count;
        model.AllLayers.Add(new global::Rhino.DocObjects.Layer { Name = "Frame" });
        using var frame = drawing.Frame.ToNurbsCurve();
        model.Objects.AddCurve(frame, new global::Rhino.DocObjects.ObjectAttributes { LayerIndex = frameLayer });
        if (!model.Write(path, 8)) throw new IOException("Could not write drawing evidence: " + path);
    }
    private void Evidence(string name, object value)
    {
        output.WriteLine(JsonSerializer.Serialize(value, WorkerCodec.Json));
        var folder = Environment.GetEnvironmentVariable("M2D_PIPE_TRIAL_EVIDENCE");
        if (folder is null) return;
        Directory.CreateDirectory(folder);
        using var file = new FileStream(Path.Combine(folder, name + ".json"), FileMode.CreateNew);
        JsonSerializer.Serialize(file, value, WorkerCodec.Json);
    }
    private static string[] Classes(IEnumerable<(string Source, string Class)> values) => values
        .GroupBy(v => v).Select(g => $"{g.Key.Source}|{g.Key.Class}|{g.Count()}").Order(StringComparer.Ordinal).ToArray();

    private static Box OwnerBox(double front = 17669, double halfWidth = 1500) => new(
        new Plane(new Point3d(65986, 40014, front - 3000), Vector3d.ZAxis),
        new Interval(-halfWidth, halfWidth), new Interval(-halfWidth, halfWidth), new Interval(-3000, 3000));

    private sealed record Agreement(string Direction, LineClass Class, double Length, int Samples,
        double Same, double OppositeOnly, double UnresolvedOnly, double Absent)
    { public double Percent => Length == 0 ? 100 : Same / Length * 100; }

    // Bidirectional classed locus sampling, matching the independent P2 probe:
    // 0.05 mm distance, 0.25 mm stations, at least 33 stations per source curve.
    private static Agreement Compare(ViewDrawing a, ViewDrawing b, string direction, LineClass kind, Rectangle3d? mask = null)
    {
        var targets = b.Items.Select(p => (p.Class, Geometry: p.CopyGeometry())).ToArray();
        var length = 0d; var same = 0d; var opposite = 0d; var unresolved = 0d; var absent = 0d; var samples = 0;
        try
        {
            var curves = targets.Where(t => t.Geometry is Curve).Select(t =>
            {
                var curve = (Curve)t.Geometry; var bounds = curve.GetBoundingBox(true); bounds.Inflate(.05);
                return (t.Class, Curve: curve, Bounds: bounds);
            }).ToArray();
            foreach (var piece in a.Items.Where(p => p.Class == kind))
            {
                using var geometry = piece.CopyGeometry(); if (geometry is not Curve c) continue;
                var len = c.GetLength(); var count = Math.Max(33, (int)Math.Ceiling(len / .25));
                for (var station = 0; station < count; station++)
                {
                    var point = c.PointAtNormalizedLength((station + .5) / count);
                    if (mask is { } frame)
                    {
                        frame.Plane.ClosestParameter(point, out var x, out var y);
                        if (x < frame.X.Min || x > frame.X.Max || y < frame.Y.Min || y > frame.Y.Max) continue;
                    }
                    var hitSame = false; var hitOpposite = false; var hitUnresolved = false;
                    foreach (var target in curves)
                    {
                        if (!target.Bounds.Contains(point) || !target.Curve.ClosestPoint(point, out var parameter, .050001) ||
                            target.Curve.PointAt(parameter).DistanceTo(point) > .05) continue;
                        if (target.Class == kind) { hitSame = true; break; }
                        if (target.Class == LineClass.Unresolved) hitUnresolved = true;
                        else if (target.Class is LineClass.Visible or LineClass.Hidden) hitOpposite = true;
                    }
                    var weight = len / count; length += weight; samples++;
                    if (hitSame) same += weight; else if (hitOpposite) opposite += weight;
                    else if (hitUnresolved) unresolved += weight; else absent += weight;
                }
            }
            return new(direction, kind, length, samples, same, opposite, unresolved, absent);
        }
        finally { foreach (var target in targets) target.Geometry.Dispose(); }
    }

    [RhinoFact] public void OwnerBoxesUsePipesForCropAndHldForDepthCuts() => host.Run(() =>
    {
        using var doc = RhinoDoc.OpenHeadless(Model); Assert.NotNull(doc);
        using var cache = new SceneCache(); using var scene = BaselineTests.Capture(doc, cache);
        var rows = new List<object>();
        foreach (var (name, box) in new[] { ("non-cutting-box", OwnerBox()), ("side-crop-box", OwnerBox(halfWidth: 500)) })
        {
            var view = View(scene.Value, name, source: box, extra: "farclip=noline\nbeyond=off");
            using var pipes = NativeDrawing.SolveBorrowedAsync(scene.Value, Identity(scene.Value), [view], new(), default, null).GetAwaiter().GetResult();
            var hldView = View(scene.Value, name, "hld", box, "farclip=noline\nbeyond=off");
            using var hld = NativeDrawing.SolveBorrowedAsync(scene.Value, Identity(scene.Value), [hldView], new(), default, null).GetAwaiter().GetResult();
            var drawing = pipes.Views[0]; Assert.Null(drawing.Error);
            Export(name + "-pipes", drawing); Export(name + "-hld", hld.Views[0]);
            Assert.Equal("pipes (experimental)", drawing.Report.Route); Assert.Equal(0, drawing.NativeCalls);
            Assert.Equal(0, drawing.Report.UncertainCount); Assert.DoesNotContain(drawing.Items, p => p.Class == LineClass.Unresolved);
            Assert.Empty(drawing.Regions); Assert.Equal(hld.Views[0].Frame, drawing.Frame);
            Assert.All(drawing.Items, piece =>
            {
                using var geometry = piece.CopyGeometry();
                Assert.All(geometry.GetBoundingBox(true).GetCorners(), p => Assert.True(NativeDrawing.Inside(p, drawing.Frame, .002)));
                Assert.NotNull(piece.DepthGeometry);
            });
            var timer = Stopwatch.StartNew();
            var agreements = new[] { Compare(drawing, hld.Views[0], "pipes-to-hld", LineClass.Visible),
                Compare(drawing, hld.Views[0], "pipes-to-hld", LineClass.Hidden),
                Compare(hld.Views[0], drawing, "hld-to-pipes", LineClass.Visible),
                Compare(hld.Views[0], drawing, "hld-to-pipes", LineClass.Hidden) };
            rows.Add(new { name, Pipes = Summary(drawing), Hld = Summary(hld.Views[0]), agreements, ComparisonSeconds = timer.Elapsed.TotalSeconds });
            // Retain all evidence before asserting the experiment's measured agreement.
            Evidence(name + "-net" + Environment.Version.Major, rows[^1]);
            if (name == "non-cutting-box")
                Assert.All(agreements, row => Assert.True(row.Percent >= 97.5, JsonSerializer.Serialize(row)));
            else
            {
                // A smaller crop concentrates the PoC's known junction errors;
                // verify crop correctness independently of HLD classification.
                var fullView = View(scene.Value, "Top");
                using var full = NativeDrawing.SolveBorrowedAsync(scene.Value, Identity(scene.Value), [fullView], new(), default, null).GetAwaiter().GetResult();
                using var aligned = new ViewDrawing();
                var projection = Transform.PlanarProjection(drawing.Frame.Plane);
                foreach (var piece in full.Views[0].Items)
                {
                    var geometry = piece.DepthGeometry!.Duplicate(); geometry.Transform(projection);
                    aligned.Pieces.Add(new(geometry, scene.Value.Prepare(default).First(s => s.Identity == piece.SourceIdentity), piece.Class));
                }
                var cropAgreements = new[] { Compare(drawing, aligned, "cropped-to-full", LineClass.Visible),
                    Compare(drawing, aligned, "cropped-to-full", LineClass.Hidden),
                    Compare(aligned, drawing, "full-inside-crop", LineClass.Visible, drawing.Frame),
                    Compare(aligned, drawing, "full-inside-crop", LineClass.Hidden, drawing.Frame) };
                Evidence("side-crop-oracle-net" + Environment.Version.Major, cropAgreements);
                Assert.All(cropAgreements, row =>
                { Assert.True(row.Samples > 0); Assert.True(row.Percent >= 99.99, JsonSerializer.Serialize(row)); });
            }
            using var restored = WorkerCodec.RestoreDrawing(WorkerCodec.CaptureDrawing(drawing));
            Assert.Equal(WorkerCodec.CaptureDrawing(drawing), WorkerCodec.CaptureDrawing(restored));
            var decorated = View(scene.Value, name, source: box,
                extra: "farclip=line\nbeyond=3200\nfade=on\nmin visible=100000\ntree=object");
            using var presented = DrawingPresentation.Apply(pipes, Identity(scene.Value), [decorated], [Plane.WorldYZ], [drawing.Frame.Plane]);
            Assert.True(presented.Views[0].IgnoredPieces > 0);
            Assert.Contains(presented.Views[0].Items, p => p.Intensity < 1);
            Assert.DoesNotContain(presented.Views[0].Items, p => p.Class == LineClass.FarClip);
            var baked = DrawingBake.Bake(doc, Guid.NewGuid(), [new BakeDrawing(presented.Views[0], name)]);
            Assert.True(baked.Added.Count > 0);
        }
        var junction = View(scene.Value, "junction", source: OwnerBox(14800), extra: "farclip=noline\nbeyond=off");
        using var cut = NativeDrawing.SolveBorrowedAsync(scene.Value, Identity(scene.Value), [junction], new(), default, null).GetAwaiter().GetResult();
        Export("junction-hld-fallback", cut.Views[0]);
        Evidence("junction-net" + Environment.Version.Major, Summary(cut.Views[0]));
        Assert.True(cut.Views[0].NativeCalls > 0);
        Assert.Contains(cut.Views[0].Diagnostics, d => d == "pipes engine: box front plane cuts 5 object(s); used Rhino HLD");
    });

    [RhinoFact] public void BoxAdmissionConservativelyHandlesFrontBackContactsExclusionsAndCurveClips() => host.Run(() =>
    {
        using var doc = RhinoDoc.CreateHeadless(null); doc.ModelUnitSystem = UnitSystem.Millimeters;
        doc.ModelAbsoluteTolerance = .001;
        using var solid = new BoundingBox(new Point3d(-1, -1, -2), new Point3d(1, 1, -1)).ToBrep();
        var bodyId = doc.Objects.AddBrep(solid);
        // Wholly outside solids must not remain as occluders or feature sources.
        using var above = new BoundingBox(new Point3d(-2, -2, 1), new Point3d(2, 2, 2)).ToBrep();
        using var below = new BoundingBox(new Point3d(-2, -2, -12), new Point3d(2, 2, -11)).ToBrep();
        var aboveId = doc.Objects.AddBrep(above); var belowId = doc.Objects.AddBrep(below);
        doc.Objects.AddLine(new Point3d(3, 0, 2), new Point3d(3, 0, -12));
        doc.Objects.AddPoint(new Point3d(3, 1, -3)); doc.Objects.AddPoint(new Point3d(3, 1, 3));
        using var cache = new SceneCache(); using var scene = BaselineTests.Capture(doc, cache);
        ViewRequest BoxAt(double front, double depth, string extra = "") => View(scene.Value, "box", source:
            new Box(Plane.WorldXY, new Interval(-5, 5), new Interval(-5, 5), new Interval(front - depth, front)),
            extra: "farclip=noline\nbeyond=off\n" + extra);
        ViewDrawing Draw(ViewRequest view)
        {
            using var batch = NativeDrawing.SolveBorrowedAsync(scene.Value, Identity(scene.Value), [view], new() { CullSources = false }, default, null).GetAwaiter().GetResult();
            return WorkerCodec.RestoreDrawing(WorkerCodec.CaptureDrawing(batch.Views[0]));
        }
        using var admitted = Draw(BoxAt(0, 10));
        Assert.Equal("pipes (experimental)", admitted.Report.Route); Assert.Equal(0, admitted.NativeCalls);
        Assert.DoesNotContain(admitted.Items, p => p.SourceIdentity == aboveId.ToString() || p.SourceIdentity == belowId.ToString());
        Assert.Contains(admitted.Items, p => p.SourceIdentity == bodyId.ToString());
        Assert.All(admitted.Items, p =>
        {
            Assert.NotNull(p.DepthGeometry); var bounds = p.DepthGeometry!.GetBoundingBox(true);
            Assert.True(bounds.Min.Z >= -10.001 && bounds.Max.Z <= .001);
        });
        using var front = Draw(BoxAt(-1.5, 8));
        Assert.Contains(front.Diagnostics, d => d.Contains("box front plane cuts 1 object(s); used Rhino HLD", StringComparison.Ordinal));
        using var back = Draw(BoxAt(0, 1.5));
        Assert.Contains(back.Diagnostics, d => d.Contains("box back plane cuts 1 object(s); used Rhino HLD", StringComparison.Ordinal));
        using var contact = Draw(BoxAt(-1, 9));
        Assert.NotEqual("pipes (experimental)", contact.Report.Route);
        using var noFar = Draw(BoxAt(0, 1.5, "farclip=none"));
        Assert.Equal("pipes (experimental)", noFar.Report.Route);
        Assert.Contains(noFar.Items, p => p.SourceIdentity == belowId.ToString());
        Evidence("analytic-depth-net" + Environment.Version.Major, new { Admitted = Summary(admitted), Front = Summary(front),
            Back = Summary(back), Contact = Summary(contact), NoFar = Summary(noFar) });
    });

    [RhinoFact] public void BoxViewSetUsesNewAdmissionInsideProductionWorkers() => host.Run(() =>
    {
        using var doc = RhinoDoc.OpenHeadless(Model); Assert.NotNull(doc);
        using var cache = new SceneCache(); using var scene = BaselineTests.Capture(doc, cache);
        ViewRequest[] views = [View(scene.Value, "worker-box", source: OwnerBox(), extra: "farclip=noline\nbeyond=off"),
            View(scene.Value, "worker-cut", source: OwnerBox(14800), extra: "farclip=noline\nbeyond=off")];
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        using var pool = new DrawingWorkerPool(new() { Count = 1, Directory = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "../../../../../src/M2DPlus.Worker/bin", configuration, $"net{Environment.Version.Major}.0-windows")) });
        var timer = Stopwatch.StartNew();
        using var workers = DrawingExecution.SolveBorrowedAsync(scene.Value, Identity(scene.Value), views, new(), default, null, pool).GetAwaiter().GetResult();
        Evidence("box-workers-net" + Environment.Version.Major, new { WallSeconds = timer.Elapsed.TotalSeconds,
            OwnedProcessIds = pool.ProcessIds, Views = workers.Views.Select(Summary).ToArray(), pool.LastSizing });
        Assert.Single(pool.ProcessIds);
        Assert.Equal("pipes (experimental)", workers.Views[0].Report.Route);
        Assert.Equal(0, workers.Views[0].Report.UncertainCount); Assert.Equal(0, workers.Views[0].NativeCalls);
        Assert.Contains(workers.Views[1].Diagnostics, d => d == "pipes engine: box front plane cuts 5 object(s); used Rhino HLD");
        Assert.True(workers.Views[1].NativeCalls > 0);
        Assert.All(workers.Views, d => Assert.Contains(d.Diagnostics, n => n.StartsWith("Worker: isolated whole-view", StringComparison.Ordinal)));
    });

    [RhinoFact] public void BoxCacheKeysAdmissionKindAndReusesPresentationChanges() => host.Run(() =>
    {
        using var doc = RhinoDoc.OpenHeadless(Model); Assert.NotNull(doc);
        using var scenes = new SceneCache(); using var cache = new DrawingCache();
        using var scene = BaselineTests.Capture(doc, scenes);
        var view = View(scene.Value, "same-frame", source: OwnerBox(), extra: "farclip=noline\nbeyond=off");
        DrawingBatch Draw(ViewRequest request) => cache.SolveAsync(BaselineTests.Capture(doc, scenes), Identity(scene.Value),
            [request], new(), [], default).GetAwaiter().GetResult();
        using var box = Draw(view);
        Assert.Equal("pipes (experimental)", box.Views[0].Report.Route);
        var solves = NativeDrawing.VisibilitySolves;
        var decorated = View(scene.Value, "same-frame", source: OwnerBox(),
            extra: "farclip=noline\nbeyond=3200\nfade=on\nmin visible=100000\ntree=object");
        using var presentation = Draw(decorated);
        Assert.Equal(solves, NativeDrawing.VisibilitySolves);
        Assert.True(presentation.Views[0].IgnoredPieces > 0);
        Assert.Contains(presentation.Views[0].Items, p => p.Class == LineClass.Beyond || p.Class == LineClass.Ignored);
        // Equal geometry/revision/settings, different source kind: must miss cache.
        var rectangle = view with { Kind = ViewSourceKind.Rectangle };
        Assert.Equal(view.Revision, rectangle.Revision);
        Assert.NotEqual(WorkerCodec.TimingKey(view, new(), WorkerCodec.Fingerprint()),
            WorkerCodec.TimingKey(rectangle, new(), WorkerCodec.Fingerprint()));
        using var fallback = Draw(rectangle);
        Assert.Equal(solves + 1, NativeDrawing.VisibilitySolves);
        Assert.Contains(fallback.Views[0].Diagnostics, d => d == "pipes engine: view has a section; used Rhino HLD");
        Evidence("box-cache-net" + Environment.Version.Major, new { Box = Summary(box.Views[0]),
            Presentation = Summary(presentation.Views[0]), Rectangle = Summary(fallback.Views[0]) });
    });

    [RhinoFact] public void CacheSwitchesEngineAndRetainsMinVisiblePlacementBakeAndSettingsFlags() => host.Run(() =>
    {
        using var doc = RhinoDoc.OpenHeadless(Model); Assert.NotNull(doc);
        using var scenes = new SceneCache(); using var cache = new DrawingCache();
        DrawingBatch Draw(string engine, string extra = "", Plane[]? targets = null)
        {
            var scene = BaselineTests.Capture(doc, scenes);
            return cache.SolveAsync(scene, Identity(scene.Value), [View(scene.Value, "Top", engine, extra: extra)],
                new(), targets ?? [], default).GetAwaiter().GetResult();
        }
        using var hld = Draw("hld");
        var solves = NativeDrawing.VisibilitySolves; var native = NativeDrawing.HldCalls;
        using var pipes = Draw("pipes");
        Assert.Equal(solves + 1, NativeDrawing.VisibilitySolves); Assert.Equal(native, NativeDrawing.HldCalls);
        var fingerprint = WorkerCodec.Fingerprint();
        using var sceneLease = BaselineTests.Capture(doc, scenes);
        Assert.NotEqual(WorkerCodec.TimingKey(View(sceneLease.Value, "Top", "hld"), new(), fingerprint),
            WorkerCodec.TimingKey(View(sceneLease.Value, "Top"), new(), fingerprint));
        using var ignored = Draw("pipes", "min visible=100000", [Plane.WorldYZ]);
        Assert.Equal(solves + 1, NativeDrawing.VisibilitySolves);
        Assert.True(ignored.Views[0].IgnoredPieces > 0);
        var baked = DrawingBake.Bake(doc, Guid.NewGuid(), [new BakeDrawing(ignored.Views[0], "Pipe trial")]);
        Assert.True(baked.Added.Count > 0);
        Assert.Contains(baked.Added, id => doc.Objects.FindId(id).Attributes.GetUserString(M2DPlus.Core.Bake.BakeTags.Class) == "ignored");
        // Bake markers exclude these new objects from the next capture.
        using var noTangents = Draw("pipes", "tangents=off");
        Assert.Equal(solves + 2, NativeDrawing.VisibilitySolves);
        // Apparent contours are silhouettes, which HLD always draws; tangents only names smooth face joins.
        Assert.Equal(Classes(pipes.Views[0].Items.Select(p => (p.SourceIdentity, p.Class.ToString()))),
            Classes(noTangents.Views[0].Items.Select(p => (p.SourceIdentity, p.Class.ToString()))));
        using var seams = Draw("pipes", "seams=on");
        Assert.Equal(Classes(pipes.Views[0].Items.Select(p => (p.SourceIdentity, p.Class.ToString()))),
            Classes(seams.Views[0].Items.Select(p => (p.SourceIdentity, p.Class.ToString()))));
        using var noHidden = Draw("pipes", "hidden=off\nmin visible=100000");
        Assert.DoesNotContain(noHidden.Views[0].Items, p => p.Class is LineClass.Hidden or LineClass.Ignored);
        using var switchedBack = Draw("hld");
        Assert.Equal(native + 1, NativeDrawing.HldCalls);
        Evidence("cache-flags-bake-net" + Environment.Version.Major, new { Pipes = Summary(pipes.Views[0]),
            Ignored = Summary(ignored.Views[0]), BakeObjects = baked.Added.Count, NoTangents = Summary(noTangents.Views[0]),
            Seams = Summary(seams.Views[0]), NoHidden = Summary(noHidden.Views[0]), Hld = Summary(switchedBack.Views[0]) });
    });

    [RhinoFact] public void AdmissionAndTransportKeepUnitsAngleAndOriginalSourceTypes() => host.Run(() =>
    {
        using var doc = RhinoDoc.CreateHeadless(null); doc.ModelUnitSystem = UnitSystem.Millimeters;
        doc.ModelAngleToleranceRadians = .0123;
        using var box = new BoundingBox(Point3d.Origin, new Point3d(20, 20, 20)).ToBrep(); doc.Objects.AddBrep(box);
        using var cache = new SceneCache();
        using (var scene = BaselineTests.Capture(doc, cache))
        using (var restored = WorkerCodec.RestoreScene(WorkerCodec.Capture(scene.Value, default)))
        {
            Assert.True(restored.Millimetres); Assert.Equal(.0123, restored.AngleTolerance);
            Assert.Null(PipeProjection.Ineligible(restored, restored.Prepare(default), View(restored, "Top")));
        }
        doc.ModelUnitSystem = UnitSystem.Meters;
        using (var metres = BaselineTests.Capture(doc, cache))
            Assert.Equal("model units are not millimetres", PipeProjection.Ineligible(metres.Value, metres.Value.Prepare(default), View(metres.Value, "Top")));
        doc.ModelUnitSystem = UnitSystem.Millimeters;
        var definition = doc.InstanceDefinitions.Add("pipe trial block", "", Point3d.Origin, [box], [new global::Rhino.DocObjects.ObjectAttributes()]);
        doc.Objects.AddInstanceObject(definition, Transform.Translation(40, 0, 0));
        using var block = BaselineTests.Capture(doc, cache);
        Assert.Equal("scene has block instances", PipeProjection.Ineligible(block.Value, block.Value.Prepare(default), View(block.Value, "Top")));
    });

    [RhinoFact] public void OwnerModelMatchesStandaloneAndArchivedPocThenKeepsPlacementAndCodec() => host.Run(() =>
    {
        using var doc = RhinoDoc.OpenHeadless(Model); Assert.NotNull(doc);
        using var cache = new SceneCache(); using var scene = BaselineTests.Capture(doc, cache);
        var rows = new List<object>();
        foreach (var name in new[] { "Top", "SW" })
        {
            var view = View(scene.Value, name);
            var inputs = doc.Objects.GetObjectList(global::Rhino.DocObjects.ObjectType.AnyObject)
                .Select(o => new PipeGeometry.Input(o.Id.ToString(), o.Geometry)).OrderBy(i => i.Name, StringComparer.Ordinal).ToList();
            var strokes = new List<PipeGeometry.Stroke>(); var dots = new List<PipeGeometry.Dot>();
            var clock = Stopwatch.StartNew();
            try
            {
                SurfaceEvents.Draw(inputs, view.Resolve(scene.Value.Bounds, scene.Value.Tolerance).Plane, view.Direction,
                    doc.ModelAbsoluteTolerance, doc.ModelAngleToleranceRadians, Math.Min(8, Environment.ProcessorCount), strokes, dots);
                var standaloneSeconds = clock.Elapsed.TotalSeconds;
                using var raw = NativeDrawing.SolveBorrowedAsync(scene.Value, Identity(scene.Value), [view], new(), default, null).GetAwaiter().GetResult();
                var drawing = raw.Views[0]; Assert.Null(drawing.Error); Assert.Equal(0, drawing.NativeCalls);
                Assert.Equal("pipes (experimental)", drawing.Report.Route);
                Assert.Equal(Classes(strokes.Select(s => (s.Source, s.Class))), Classes(drawing.Items.Where(p =>
                { using var g = p.CopyGeometry(); return g is Curve; }).Select(p => (p.SourceIdentity, p.Class.ToString()))));
                // Original commit's standalone archive is an external independent porting oracle.
                using var archived = File3dm.Read(Path.Combine(Models, "evidence", "pipe-poc-scaling-20261118", name + ".3dm"));
                var archivedCurves = archived.Objects.Where(o => o.Geometry is Curve).ToArray();
                Assert.Equal(Classes(archivedCurves.Select(o =>
                { var pieces = o.Attributes.Name.Split('|'); return (pieces[1], pieces[0]); })), Classes(strokes.Select(s => (s.Source, s.Class))));
                using var restored = WorkerCodec.RestoreDrawing(WorkerCodec.CaptureDrawing(drawing));
                Assert.Equal(WorkerCodec.CaptureDrawing(drawing), WorkerCodec.CaptureDrawing(restored));
                var target = new Plane(new Point3d(12000, 34000, 56000), Vector3d.YAxis, Vector3d.ZAxis);
                using var placed = DrawingPresentation.Apply(raw, Identity(scene.Value), [view], [target], [Plane.WorldXY]);
                Assert.Equal(drawing.Items.Count, placed.Views[0].Items.Count);
                Assert.Equal(Classes(drawing.Items.Select(p => (p.SourceIdentity, p.Class.ToString()))),
                    Classes(placed.Views[0].Items.Select(p => (p.SourceIdentity, p.Class.ToString()))));
                Assert.Contains("pipes (experimental)", placed.Views[0].Info);
                Assert.All(placed.Views[0].Items, p => { using var g = p.CopyGeometry(); Assert.True(g.IsValid); });
                rows.Add(new { Name = name, standaloneSeconds, Pipeline = Summary(drawing), Placed = Summary(placed.Views[0]),
                    ArchivedVisible = archivedCurves.Count(o => o.Attributes.Name.StartsWith("Visible|", StringComparison.Ordinal)),
                    ArchivedHidden = archivedCurves.Count(o => o.Attributes.Name.StartsWith("Hidden|", StringComparison.Ordinal)) });
            }
            finally { foreach (var stroke in strokes) stroke.Curve.Dispose(); }
        }
        Evidence("a-parity-placement-codec-net" + Environment.Version.Major, rows);
    });

    [RhinoFact] public void TwoViewsUseProductionWorkerRouteAndIneligibleViewsExplainHld() => host.Run(() =>
    {
        using var doc = RhinoDoc.OpenHeadless(Model); Assert.NotNull(doc);
        using var cache = new SceneCache(); using var scene = BaselineTests.Capture(doc, cache);
        ViewRequest[] views = [View(scene.Value, "Top"), View(scene.Value, "SW")];
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        // One worker plus this licensed host: at most two owned Rhino processes.
        using var pool = new DrawingWorkerPool(new() { Count = 1, Directory = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "../../../../../src/M2DPlus.Worker/bin", configuration, $"net{Environment.Version.Major}.0-windows")) });
        using var raw = NativeDrawing.SolveBorrowedAsync(scene.Value, Identity(scene.Value), views, new(), default, null).GetAwaiter().GetResult();
        var clock = Stopwatch.StartNew();
        using var workers = DrawingExecution.SolveBorrowedAsync(scene.Value, Identity(scene.Value), views, new(), default, null, pool).GetAwaiter().GetResult();
        var wallSeconds = clock.Elapsed.TotalSeconds;
        Assert.InRange(pool.ProcessIds.Length, 1, 2);
        for (var i = 0; i < views.Length; i++)
        {
            var drawing = workers.Views[i]; Assert.Null(drawing.Error);
            Assert.Equal("pipes (experimental)", drawing.Report.Route); Assert.Equal(0, drawing.NativeCalls);
            Assert.Contains(drawing.Diagnostics, d => d.StartsWith("Worker: isolated whole-view", StringComparison.Ordinal));
            Assert.Equal(Classes(raw.Views[i].Items.Select(p => (p.SourceIdentity, p.Class.ToString()))),
                Classes(drawing.Items.Select(p => (p.SourceIdentity, p.Class.ToString()))));
        }
        Evidence("c-workers-net" + Environment.Version.Major, new { wallSeconds, OwnedProcessIds = pool.ProcessIds,
            Views = workers.Views.Select(Summary).ToArray(), pool.LastSizing });
        var section = View(scene.Value, "Section", source: new Plane(scene.Value.Bounds.Center, Vector3d.ZAxis));
        using var cut = NativeDrawing.SolveBorrowedAsync(scene.Value, Identity(scene.Value), [section], new(), default, null).GetAwaiter().GetResult();
        Assert.Contains(ViewNotices.For(cut.Views[0]), n => n.Level == NoticeLevel.Remark && n.Text.Contains("pipes engine: view has a section; used Rhino HLD", StringComparison.Ordinal));
        Assert.True(cut.Views[0].NativeCalls > 0);
        using var mesh = Mesh.CreateFromBox(new Box(new BoundingBox(new Point3d(10000, 10000, 10000), new Point3d(10001, 10001, 10001))), 1, 1, 1);
        doc.Objects.AddMesh(mesh);
        using var meshScene = BaselineTests.Capture(doc, cache);
        using var meshBatch = NativeDrawing.SolveBorrowedAsync(meshScene.Value, Identity(meshScene.Value), [View(meshScene.Value, "Top")], new(), default, null).GetAwaiter().GetResult();
        Assert.Contains(ViewNotices.For(meshBatch.Views[0]), n => n.Level == NoticeLevel.Remark && n.Text.Contains("scene has Mesh; used Rhino HLD", StringComparison.Ordinal));
        Assert.Equal(1, meshBatch.Views[0].NativeCalls);
        Evidence("d-fallback-net" + Environment.Version.Major, new { Section = Summary(cut.Views[0]), Mesh = Summary(meshBatch.Views[0]) });
    });

    [RhinoFact] public void ActualComponentPublishesPipesForOwnerModelAndSwitchesBackToHld() => host.Run(() =>
    {
        Assert.True(global::Rhino.PlugIns.PlugIn.LoadPlugIn(new Guid("b45a29b1-4343-4035-989e-044e8580d9cf")));
        using var doc = RhinoDoc.OpenHeadless(Model); RhinoDoc.ActiveDoc = doc;
        using var document = new GH_Document { Enabled = true };
        var assembly = Assembly.LoadFrom(Path.Combine(AppContext.BaseDirectory, "M2DPlus.Grasshopper.gha"));
        var component = (GH_Component)Activator.CreateInstance(assembly.GetType("M2DPlus.Grasshopper.DrawingComponent", true)!)!;
        document.AddObject(component, false);
        var view = (Param_GenericObject)component.Params.Input[1];
        var settings = (Param_String)component.Params.Input[3];
        var rows = new List<object>();
        try
        {
            foreach (var name in new[] { "Top", "SW", "Box" })
            foreach (var engine in new[] { "pipes", "hld" })
            {
                view.PersistentData.Clear();
                view.PersistentData.Append(name == "Box" ? new GH_Box(OwnerBox()) : new GH_String(name)); view.ExpireSolution(false);
                var extra = name == "Box" ? "farclip=noline\nbeyond=off\nmin visible=100000\ntree=object" : "farclip=none";
                settings.PersistentData.Clear(); settings.PersistentData.Append(new GH_String($"visibility={engine}\ntangents=on\n{extra}")); settings.ExpireSolution(false);
                if (name == "Box")
                {
                    var source = (Param_Plane)component.Params.Input[5];
                    source.PersistentData.Clear(); source.PersistentData.Append(new GH_Plane(ViewRequest.Parse(OwnerBox()).Cut!.Plane)); source.ExpireSolution(false);
                    var target = (Param_Plane)component.Params.Input[2];
                    target.PersistentData.Clear(); target.PersistentData.Append(new GH_Plane(Plane.WorldYZ)); target.ExpireSolution(false);
                }
                document.NewSolution(false, GH_SolutionMode.Silent);
                GrasshopperTests.PumpUntil(() => component.Params.Output[8].VolatileDataCount == 1,
                    () => component.Message + string.Join(';', component.RuntimeMessages(GH_RuntimeMessageLevel.Error)),
                    () => { if (document.ScheduleDelay >= 0) document.NewSolution(false, GH_SolutionMode.Silent); });
                var info = ((GH_String)component.Params.Output[8].VolatileData.AllData(true).Single()).Value;
                Assert.Contains(engine == "pipes" ? "Engine: pipes (experimental)" : "Engine: Rhino HLD", info);
                if (engine == "pipes" && name != "Box")
                {
                    using var archived = File3dm.Read(Path.Combine(Models, "evidence", "pipe-poc-scaling-20261118", name + ".3dm"));
                    Assert.Equal(archived.Objects.Count(o => o.Attributes.LayerIndex == 0), component.Params.Output[0].VolatileDataCount);
                    Assert.Equal(archived.Objects.Count(o => o.Attributes.LayerIndex == 1), component.Params.Output[1].VolatileDataCount);
                }
                if (name == "Box" && engine == "pipes")
                {
                    Assert.Equal(0, component.Params.Output[6].VolatileDataCount);
                    Assert.True(component.Params.Output[9].VolatileDataCount > 0);
                    Assert.All(component.Params.Output[10].VolatileData.Paths, path => Assert.Equal(3, path.Length));
                    foreach (var index in new[] { 0, 1, 9 })
                        Assert.All(component.Params.Output[index].VolatileData.Paths, path =>
                            Assert.Contains(path, component.Params.Output[10].VolatileData.Paths));
                    var frame = ((GH_Rectangle)component.Params.Output[7].VolatileData.AllData(true).Single()).Value;
                    Assert.True(frame.Plane.Origin.DistanceTo(Point3d.Origin) < .001);
                    Assert.True(Math.Abs(frame.Plane.Normal * Vector3d.XAxis) > .9999);
                    Assert.Contains("Placement: Source", info);
                }
                Assert.True(component.Params.Output[10].VolatileDataCount > 0);
                rows.Add(new { name, engine, Visible = component.Params.Output[0].VolatileDataCount,
                    Hidden = component.Params.Output[1].VolatileDataCount, info });
            }
            Evidence("component-net" + Environment.Version.Major, rows);
        }
        finally { component.RemovedFromDocument(document); SceneCache.Shared.Clear(); }
    });
}

