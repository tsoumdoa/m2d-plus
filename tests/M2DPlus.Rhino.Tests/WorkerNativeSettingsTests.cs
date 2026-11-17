using System.Text.Json.Nodes;
using M2DPlus.Core;
using M2DPlus.Rhino.Services;
using Rhino;
using Rhino.Geometry;
using Xunit;

namespace M2DPlus.Rhino.Tests;

[Collection("Rhino")]
[Trait("Category", "RhinoIntegration")]
public sealed class WorkerNativeSettingsTests(RhinoHost host)
{
    [RhinoFact] public void EveryPublicComputeOptionRoundTripsAndUsesTheWorkerScheduler() => host.Run(() =>
    {
        // Fail when a new public compute option needs an explicit transport decision.
        Assert.Equal(new[] { "MultipleThreads", "NativeOnly", "Verification" },
            typeof(NativeSettings).GetProperties().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal(new[] { "MaxRayTests", "MaxSamples", "ViewSpacingFraction" },
            typeof(NativeVerificationOptions).GetProperties().Select(p => p.Name).Order(StringComparer.Ordinal));
        var settings = new NativeSettings(true, true)
        {
            Verification = new(37, 91, .125), CullSources = false, DenseAudit = true,
            DenseAuditOptions = new(25, 3, 123_456, 654_321)
        };
        var identity = new RequestIdentity("scene", "view", "settings", 7);
        var sent = WorkerCodec.Request.FromSettings(identity, 2, ViewRequest.Parse("Top"), settings);
        var received = WorkerCodec.Decode<WorkerCodec.Request>(WorkerCodec.Encode(sent));
        var scheduler = new NativeScheduler(123_456_789);
        var restored = received.ToSettings(scheduler);
        Assert.Equal(identity, received.Identity); Assert.Equal(2, received.ViewIndex);
        Assert.True(restored.MultipleThreads); Assert.True(restored.NativeOnly);
        Assert.Equal(settings.Verification, restored.Verification);
        Assert.True(restored.DenseAudit); Assert.Equal(settings.DenseAuditOptions, restored.DenseAuditOptions);
        Assert.False(restored.CullSources); Assert.True(restored.IsWorker); Assert.Same(scheduler, restored.Scheduler);
    });

    [RhinoFact] public void DefaultAndOlderRequestsRetainSerialAndDefaultVerification() => host.Run(() =>
    {
        var sent = WorkerCodec.Request.FromSettings(default, 0, ViewRequest.Parse("Top"), new() { DenseAudit = false });
        var current = WorkerCodec.Decode<WorkerCodec.Request>(WorkerCodec.Encode(sent));
        var older = JsonNode.Parse(WorkerCodec.Encode(sent))!.AsObject();
        older.Remove("MultipleThreads"); older.Remove("Verification");
        var legacy = WorkerCodec.Decode<WorkerCodec.Request>(System.Text.Encoding.UTF8.GetBytes(older.ToJsonString()));
        var omitted = WorkerCodec.Decode<WorkerCodec.Request>(WorkerCodec.Encode(new WorkerCodec.Request(default, 0, sent.View, false)));
        foreach (var request in new[] { current, legacy, omitted })
        {
            var settings = request.ToSettings(NativeScheduler.Global);
            Assert.False(settings.MultipleThreads); Assert.False(settings.NativeOnly);
            Assert.Equal(new NativeVerificationOptions(), settings.Verification);
            Assert.False(settings.DenseAudit); Assert.Equal(new DenseAuditOptions(), settings.DenseAuditOptions);
            Assert.True(settings.CullSources);
        }
        // Explicit JSON null has the same fallback as an omitted optional Verification field.
        Assert.Equal(new NativeVerificationOptions(), (current with { Verification = null }).ToSettings(NativeScheduler.Global).Verification);
    });

    [RhinoFact] public void TimingHistorySeparatesEveryPublicComputeOption() => host.Run(() =>
    {
        var view = ViewRequest.Parse("Top");
        var defaults = new NativeSettings() { DenseAudit = false };
        string Key(NativeSettings settings) => WorkerCodec.Hash(WorkerCodec.Encode(WorkerCodec.Request.FromSettings(default, 0, view, settings)));
        var changed = new[]
        {
            defaults with { MultipleThreads = true }, defaults with { NativeOnly = true },
            defaults with { Verification = new(MaxRayTests: 17) },
            defaults with { Verification = new(MaxSamples: 0) },
            defaults with { Verification = new(ViewSpacingFraction: .125) }
        };
        var keys = changed.Select(Key).Append(Key(defaults)).ToArray();
        Assert.Equal(keys.Length, keys.Distinct(StringComparer.Ordinal).Count());
        foreach (var settings in changed)
        {
            var restored = WorkerCodec.Decode<WorkerCodec.Request>(WorkerCodec.Encode(WorkerCodec.Request.FromSettings(default, 0, view, settings)))
                .ToSettings(NativeScheduler.Global);
            Assert.Equal(settings.MultipleThreads, restored.MultipleThreads);
            Assert.Equal(settings.NativeOnly, restored.NativeOnly);
            Assert.Equal(settings.Verification, restored.Verification);
        }
    });

    private static WorkerOptions Options() => new()
    {
        Count = 1,
        Directory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/M2DPlus.Worker/bin/Debug/net" + Environment.Version.Major + ".0-windows")),
        IdleTimeout = TimeSpan.FromMinutes(2)
    };
    private static LeaseEntry<PreparedScene>.Lease Capture(RhinoDoc doc, SceneCache cache)
    {
        using var watch = new DocumentWatch(doc); using var capture = new SceneCapture(doc, watch, null, cache);
        while (!capture.Step(TimeSpan.FromSeconds(30))) { }
        return capture.Accept();
    }
    private static Brep Freeform()
    {
        var points = Enumerable.Range(0, 4).SelectMany(i => Enumerable.Range(0, 4).Select(j =>
        { double x = -1 + 2 * i / 3d, y = -1 + 2 * j / 3d; return new Point3d(x, y, .3 * x * y + .1 * x * x * y * y); }));
        using var surface = NurbsSurface.CreateFromPoints(points, 4, 4, 3, 3); return surface.ToBrep();
    }
    private static ViewRequest View(bool native = false)
    {
        var view = ViewRequest.Parse("Back");
        return view with { Settings = Settings.Resolve([new("v", view.Name, view.Kind, false, 20, 1e-6)],
            [[$"engine={(native ? "native" : "exact")}", "hidden=off"]], DocumentUnits.Metres).Views[0] };
    }

    [RhinoTheory]
    [InlineData(false, false)] [InlineData(true, false)]
    [InlineData(false, true)] [InlineData(true, true)]
    public void WorkerAndLocalHonorThreadingAndVerificationInFallbackAndComposition(bool threads, bool composed) => host.Run(() =>
    {
        using var doc = RhinoDoc.CreateHeadless(null); doc.ModelAbsoluteTolerance = 1e-6;
        using var body = Freeform(); doc.Objects.AddBrep(body);
        if (composed) doc.Objects.AddLine(new Point3d(-2, -2, -.5), new Point3d(2, 2, -.5));
        using var cache = new SceneCache(); using var scene = Capture(doc, cache);
        using var pool = new DrawingWorkerPool(Options());
        var identity = new RequestIdentity(scene.Value.Revision, "view", "settings", 1);
        var view = View();
        // Same persistent worker/identity: changing only options must take effect on every solve.
        foreach (var verification in new[] { new NativeVerificationOptions(), new(MaxSamples: 0), new(5, 17, .125), new() })
        {
            var observed = new List<NativeHldOptions>();
            var settings = new NativeSettings(threads) { Verification = verification, DenseAudit = false, ObserveHldOptions = observed.Add };
            using var local = NativeDrawing.SolveBorrowedAsync(scene.Value, identity, [view], settings, default, null).GetAwaiter().GetResult();
            using var worker = pool.SolveAsync(scene.Value, identity, [view], settings, default).GetAwaiter().GetResult();
            Assert.NotEmpty(observed);
            Assert.All(observed, o => { Assert.Equal(threads, o.MultipleThreads); Assert.True(o.IncludeHiddenCurves); });
            foreach (var drawing in new[] { local.Views[0], worker.Views[0] })
            {
                Assert.Null(drawing.Error); Assert.True(drawing.NativeCalls > 0); Assert.NotEmpty(drawing.Items);
                Assert.Equal(composed, drawing.KernelEdges > 0);
                Assert.Equal(threads, drawing.MultipleThreads);
                Assert.Contains($"Native HLD options: multipleThreads={threads}; IncludeHiddenCurves=True.", drawing.Diagnostics);
                if (verification.MaxSamples == 0)
                {
                    Assert.Contains("Native ray verifier disabled by sample budget; native confidence unverified.", drawing.Diagnostics);
                    Assert.DoesNotContain(drawing.Diagnostics, d => d.StartsWith("Native ray verifier:", StringComparison.Ordinal));
                }
                else
                {
                    var audit = Assert.Single(drawing.Diagnostics, d => d.StartsWith("Native ray verifier:", StringComparison.Ordinal));
                    Assert.Matches($@"ray tests=\d+/{verification.MaxRayTests};", audit);
                    var counts = System.Text.RegularExpressions.Regex.Match(audit, @"; samples=(\d+); ray tests=(\d+)/");
                    Assert.True(counts.Success);
                    Assert.InRange(int.Parse(counts.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture), 0, verification.MaxSamples);
                    Assert.InRange(int.Parse(counts.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture), 0, verification.MaxRayTests);
                    var spacing = Math.Max(8 * drawing.Tolerance, drawing.Frame.BoundingBox.Diagonal.Length * verification.ViewSpacingFraction);
                    Assert.Contains(FormattableString.Invariant($"spacing<={spacing:R};"), audit);
                }
            }
            // Threaded HLD is explicitly nondeterministic: compare option semantics, not repeated geometry bytes.
            if (!threads) Assert.Equal(WorkerCodec.CaptureDrawing(local.Views[0], true), WorkerCodec.CaptureDrawing(worker.Views[0], true));
        }
    });

    [RhinoFact] public void NativeEngineStillForcesThreadingAndSkipsVerificationOnBothRoutes() => host.Run(() =>
    {
        using var doc = RhinoDoc.CreateHeadless(null); using var body = Freeform(); doc.Objects.AddBrep(body);
        using var cache = new SceneCache(); using var scene = Capture(doc, cache);
        using var pool = new DrawingWorkerPool(Options());
        var identity = new RequestIdentity(scene.Value.Revision, "view", "settings", 1);
        var settings = new NativeSettings(false) { Verification = new(0, 0, .125), DenseAudit = false };
        using var local = NativeDrawing.SolveBorrowedAsync(scene.Value, identity, [View(true)], settings, default, null).GetAwaiter().GetResult();
        using var worker = pool.SolveAsync(scene.Value, identity, [View(true)], settings, default).GetAwaiter().GetResult();
        foreach (var drawing in new[] { local.Views[0], worker.Views[0] })
        {
            Assert.Null(drawing.Error); Assert.True(drawing.NativeCalls > 0); Assert.True(drawing.MultipleThreads);
            Assert.Contains("Native HLD options: multipleThreads=True; IncludeHiddenCurves=False.", drawing.Diagnostics);
            Assert.DoesNotContain(drawing.Diagnostics, d => d.StartsWith("Native ray verifier", StringComparison.Ordinal));
        }
    });
}
