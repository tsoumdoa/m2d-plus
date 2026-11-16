using M2DPlus.Core;
using M2DPlus.Rhino.Services;
using M2DPlus.Rhino.Views;
using Rhino;
using Rhino.Geometry;
using Xunit;

namespace M2DPlus.Rhino.Tests;

[Collection("Rhino")]
[Trait("Category", "RhinoIntegration")]
public sealed class CutOnlyTests(RhinoHost host)
{
    private static ViewRequest View(bool only, object? source = null, string extra = "")
    {
        var view = ViewRequest.Parse(source ?? Plane.WorldXY);
        var settings = Settings.Resolve([new("v", view.Name, view.Kind, view.Cut is not null, 20, .0001)],
            [[$"cutonly={(only ? "on" : "off")}\nhidden=on\nfarclip=line\ndepth=5\n" + extra]], DocumentUnits.Metres);
        Assert.False(settings.HasErrors);
        return view with { Settings = settings.Views[0] };
    }
    private static RequestIdentity Identity(PreparedScene scene) => new(scene.Revision, "views", "settings", 1);
    private static void Populate(RhinoDoc doc)
    {
        doc.ModelAbsoluteTolerance = .0001;
        using var box = new BoundingBox(new Point3d(-2, -2, -6), new Point3d(2, 2, 2)).ToBrep();
        doc.Objects.AddBrep(box);
        using var sphere = new Sphere(new Point3d(5, 0, -.25), 1).ToBrep(); doc.Objects.AddBrep(sphere);
        doc.Objects.AddLine(new Point3d(-4, 0, -1), new Point3d(8, 0, -1));
        doc.Objects.AddPoint(0, 0, -1);
    }
    private static DrawingBatch Solve(PreparedScene scene, ViewRequest view, NativeSettings? native = null, IProgress<JobProgress>? progress = null) =>
        NativeDrawing.SolveBorrowedAsync(scene, Identity(scene), [view], native ?? new(), default, progress).GetAwaiter().GetResult();
    private static byte[] Regions(ViewDrawing drawing) => WorkerCodec.Encode(WorkerCodec.Decode<WorkerCodec.Drawing>(WorkerCodec.CaptureDrawing(drawing, true)).Regions);
    private static void OnlySections(ViewDrawing drawing)
    {
        Assert.Null(drawing.Error); Assert.True(drawing.Frame.IsValid); Assert.Equal(0, drawing.NativeCalls); Assert.Equal(0, drawing.KernelEdges);
        Assert.All(drawing.Items, p => Assert.Equal(LineClass.Unresolved, p.Class));
        Assert.DoesNotContain(drawing.Phases.Seconds.Keys, p => p is "native solve" or "kernel solve" or "native ray verification");
        Assert.Contains("Cut only: projection beyond the cut skipped", drawing.Info);
    }

    [RhinoTheory] [InlineData(false)] [InlineData(true)]
    public void CutAndHatchBytesMatchNormalWithoutAnyProjection(bool nativeOnly) => host.Run(() =>
    {
        using var doc = RhinoDoc.CreateHeadless(null); Populate(doc); using var cache = new SceneCache();
        using var scene = BaselineTests.Capture(doc, cache);
        using var normal = Solve(scene.Value, View(false), new(NativeOnly: nativeOnly));
        var before = NativeDrawing.HldCalls; var reports = new List<JobProgress>();
        using var only = Solve(scene.Value, View(true), new(NativeOnly: nativeOnly)
        {
            ObserveNativeInputs = _ => throw new InvalidOperationException("Unexpected native input"),
            ObservePlanarScene = (_, _) => throw new InvalidOperationException("Unexpected planar solve")
        }, new Sink(reports));
        var drawing = only.Views[0]; OnlySections(drawing); Assert.Equal(before, NativeDrawing.HldCalls);
        Assert.Empty(drawing.Items);
        Assert.NotEmpty(drawing.Regions); Assert.All(drawing.Regions, r => Assert.NotNull(r.Region.Hatch));
        Assert.Equal(Regions(normal.Views[0]), Regions(drawing));
        Assert.Equal(ViewSteps.Sections, reports.Last(p => p.Step is not null).Step);
        Assert.DoesNotContain(reports, p => p.Step is ViewSteps.HiddenLines or ViewSteps.ExactLines or ViewSteps.Checking);
        using var displayedNormal = DrawingPresentation.Apply(normal, normal.Identity, [View(false, extra: "cuts=merge\nhatch=black")], []);
        using var displayedOnly = DrawingPresentation.Apply(only, only.Identity, [View(true, extra: "cuts=merge\nhatch=black")], []);
        Assert.Equal(Regions(displayedNormal.Views[0]), Regions(displayedOnly.Views[0]));
    });

    [RhinoTheory] [InlineData("Top")] [InlineData("SW")] [InlineData("plane")] [InlineData("outside-frame")]
    public void UncutViewsAreEmptyWithARemark(string kind) => host.Run(() =>
    {
        using var doc = RhinoDoc.CreateHeadless(null); Populate(doc); using var cache = new SceneCache();
        using var scene = BaselineTests.Capture(doc, cache);
        object source = kind switch
        {
            "plane" => new Plane(new Point3d(0, 0, 10), Vector3d.ZAxis),
            "outside-frame" => new Rectangle3d(Plane.WorldXY, new Interval(20, 30), new Interval(20, 30)),
            _ => kind
        };
        var view = View(true, source);
        using var result = Solve(scene.Value, view); var drawing = result.Views[0];
        OnlySections(drawing); Assert.Empty(drawing.Items); Assert.Empty(drawing.Regions);
        Assert.Contains(ViewNotices.For(drawing), n => n.Level == NoticeLevel.Remark && n.Text.Contains(ViewNotices.CutOnlyEmpty, StringComparison.Ordinal));
        using var restored = WorkerCodec.RestoreDrawing(WorkerCodec.CaptureDrawing(drawing));
        Assert.Contains(ViewNotices.For(restored), n => n.Text.Contains(ViewNotices.CutOnlyEmpty, StringComparison.Ordinal));
    });

    [RhinoFact] public void TogglingCachedDrawingBackToOffRestoresTheFreshNormalHash() => host.Run(() =>
    {
        using var doc = RhinoDoc.CreateHeadless(null); Populate(doc); using var cache = new SceneCache();
        using var scene = BaselineTests.Capture(doc, cache); using var drawings = new DrawingCache(); using var freshCache = new DrawingCache();
        DrawingBatch Cached(DrawingCache owner, bool only) => owner.SolveAsync(BaselineTests.Capture(doc, cache), Identity(scene.Value),
            [View(only)], new(), [], default).GetAwaiter().GetResult();
        using var off = Cached(drawings, false); var solves = NativeDrawing.VisibilitySolves;
        using var on = Cached(drawings, true); Assert.Equal(solves + 1, NativeDrawing.VisibilitySolves); OnlySections(on.Views[0]);
        using var back = Cached(drawings, false); Assert.Equal(solves + 2, NativeDrawing.VisibilitySolves);
        using var fresh = Cached(freshCache, false);
        Assert.Equal(WorkerCodec.CaptureDrawing(fresh.Views[0], true), WorkerCodec.CaptureDrawing(back.Views[0], true));
        Assert.Equal(WorkerCodec.CaptureDrawing(off.Views[0], true), WorkerCodec.CaptureDrawing(back.Views[0], true));
    });

    [RhinoFact] public void ViewObjectUserTextAndWorkerRoutePreserveCutOnlyWithoutCodecChanges() => host.Run(() =>
    {
        using var doc = RhinoDoc.CreateHeadless(null); Populate(doc);
        var id = doc.Objects.AddClippingPlane(Plane.WorldXY, 20, 20, Guid.Empty);
        var obj = doc.Objects.FindId(id); obj.Attributes.SetUserString("m2d.cutonly", "on"); obj.CommitChanges();
        var view = View(true); var context = view.Settings!.View;
        var settings = Settings.Resolve([context], [], DocumentUnits.Metres,
            new(SettingsRow.Empty, [KeyValuePair.Create("v", ViewBoxes.ReadRow(obj.Attributes))]));
        Assert.True(settings.Views[0].CutOnly); Assert.Equal(SettingSource.EditorView, settings.Views[0]["cutonly"].Source);
        using var cache = new SceneCache(); using var scene = BaselineTests.Capture(doc, cache);
        using var local = Solve(scene.Value, view);
        using var pool = new DrawingWorkerPool(new()
        {
            Count = 1, Directory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
                "../../../../../src/M2DPlus.Worker/bin/Debug/net" + Environment.Version.Major + ".0-windows"))
        });
        using var worker = pool.SolveAsync(scene.Value, Identity(scene.Value), [view, View(true, "Top")], new(), default).GetAwaiter().GetResult();
        OnlySections(worker.Views[0]);
        Assert.Equal(WorkerCodec.CaptureDrawing(local.Views[0], true), WorkerCodec.CaptureDrawing(worker.Views[0], true));
        Assert.Equal(Regions(local.Views[0]), Regions(worker.Views[0]));
        OnlySections(worker.Views[1]); Assert.Empty(worker.Views[1].Items); Assert.Empty(worker.Views[1].Regions);
        Assert.Contains(ViewNotices.For(worker.Views[1]), n => n.Text.Contains(ViewNotices.CutOnlyEmpty, StringComparison.Ordinal));
    });

    [RhinoFact] public void BackgroundUncertaintyDoesNotInventCutRefusalsAtTheProjectionBoundary() => host.Run(() =>
    {
        using var doc = RhinoDoc.CreateHeadless(null); Populate(doc); using var cache = new SceneCache();
        using var scene = BaselineTests.Capture(doc, cache);
        // The background slab is ambiguous. Both near cuts have their own
        // complete topology certificates and must not gain orange overlays.
        using var result = Solve(scene.Value, View(true, extra: "depth=0.00005"));
        var drawing = result.Views[0]; OnlySections(drawing);
        Assert.True(drawing.UncertainSectionComponents > 0 || drawing.Diagnostics.Any(d => d.Contains("within tolerance", StringComparison.Ordinal)));
        Assert.Equal(0,drawing.Report.UncertainCount); Assert.Empty(drawing.Items);
        Assert.Equal(2,drawing.Regions.Count); Assert.All(drawing.Regions,r=>Assert.NotNull(r.Region.Hatch));
        Assert.InRange(Math.Abs(drawing.Regions.Sum(r=>r.Region.Area)-(16+Math.PI*.9375)),0,1e-6);
        Assert.DoesNotContain(ViewNotices.For(drawing), n => n.Text.Contains(ViewNotices.CutOnlyEmpty, StringComparison.Ordinal));
    });

    private sealed class Sink(List<JobProgress> reports) : IProgress<JobProgress>
    { public void Report(JobProgress value) => reports.Add(value); }
}
