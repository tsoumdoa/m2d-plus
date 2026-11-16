using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Text;
using M2DPlus.Rhino.Services;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using Xunit;
using Xunit.Abstractions;
using Environment = System.Environment;

namespace M2DPlus.Rhino.Tests;

/// <summary>Opt-in only (M2D_PROFILE_CAPTURE=1): opens the perf models from M2D_MODELS.</summary>
public sealed class ProfileFactAttribute : FactAttribute
{
    public ProfileFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("M2D_RHINO_TESTS") != "1" || Environment.GetEnvironmentVariable("M2D_PROFILE_CAPTURE") != "1")
            Skip = "Capture profile: set M2D_RHINO_TESTS=1 and M2D_PROFILE_CAPTURE=1 (opens the ~6k-object perf models).";
    }
}

/// <summary>
/// Host capture profile on ~6k-object scenes (owner feedback on beta.1: "loading up objects seems slow"). Runs the
/// capture exactly as the component does (12 ms slices on the host thread) and reports where the time goes: document
/// snapshot, enumeration, per-object reads, section/colour capture, block expansion, fingerprinting, normalisation,
/// signature and scene construction, plus scene preparation (off the UI thread in the component). The first capture
/// is cold; the second repeats it with warm caches, which is what a re-capture costs. Writes a Markdown table to
/// M2D_PROFILE_OUT (default artifacts/profile/capture.md).
/// </summary>
[Collection("Rhino")]
[Trait("Category", "RhinoIntegration")]
public sealed class CaptureProfileTests(RhinoHost host, ITestOutputHelper output)
{
    private static string Models => Environment.GetEnvironmentVariable("M2D_MODELS")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".t3", "m2d-plus-models");

    private sealed record Run(string Scene, string Mode, string Pass, int Objects, int Leaves, double Snapshot, double Enumerate,
        int Slices, double MaxSlice, double Active, double Accept, double Prepare, IReadOnlyList<(SceneCapture.CapturePhase Phase, double Milliseconds)> Profile)
    {
        /// <summary>Waiting for the background part of the capture after the last slice.</summary>
        public double Join { get; init; }
        /// <summary>Capture start to scene ready, back to back (the component adds timer gaps between slices).</summary>
        public double Wall { get; init; }
    }

    [ProfileFact] public void ProfileHostCapture() => host.Run(() =>
    {
        var runs = new List<Run>();
        foreach (var (name, file, mixed) in new[]
        {
            ("perf-M-blocks", "buildings/perf-M-blocks.3dm", false), ("perf-M-exploded", "buildings/perf-M-exploded.3dm", false),
            ("mixed-6k", "buildings/perf-M-blocks.3dm", true),
        })
        {
            var path = Path.Combine(Models, file);
            Assert.True(File.Exists(path), path);
            using var document = RhinoDoc.OpenHeadless(path);
            if (mixed) AddMixed(document, 1000);
            var top = document.Objects.GetObjectList(new ObjectEnumeratorSettings { NormalObjects = true, LockedObjects = true, ReferenceObjects = true })
                .Where(o => SceneCapture.IsDrawable(document, o)).Select(o => o.Id).OrderBy(i => i).ToArray();
            foreach (var mode in new[] { "document", "referenced", "document-interactive" })
            {
                DocumentWatch.SimulateInteractive = mode.EndsWith("interactive", StringComparison.Ordinal);
                using var cache = new SceneCache(4L * 1024 * 1024 * 1024);
                var inputs = mode.StartsWith("document", StringComparison.Ordinal) ? null : top.Select((id, i) => new CaptureInput(id, [0], $"input/{{0}}/{i}/{id}") { SourceItem = i }).ToArray();
                foreach (var pass in new[] { "cold", "warm" })
                {
                    var timer = Stopwatch.StartNew();
                    using var watch = new DocumentWatch(document);
                    var snapshot = watch.SnapshotMilliseconds;
                    timer.Restart();
                    var capture = new SceneCapture(document, watch, inputs, cache);
                    var slices = 0; var maxSlice = 0d; var active = 0d;
                    while (true)
                    {
                        var slice = Stopwatch.StartNew();
                        var done = capture.Step(TimeSpan.FromMilliseconds(12));
                        slices++; maxSlice = Math.Max(maxSlice, slice.Elapsed.TotalMilliseconds); active += slice.Elapsed.TotalMilliseconds;
                        if (done) break;
                    }
                    // AcceptAsync: the coherence check on this (host) thread, then background join and scene build.
                    var accept = Stopwatch.StartNew();
                    var pending = capture.AcceptAsync();
                    var acceptMs = accept.Elapsed.TotalMilliseconds;
                    var join = Stopwatch.StartNew();
                    using var lease = pending.GetAwaiter().GetResult();
                    var joinMs = join.Elapsed.TotalMilliseconds;
                    var prepare = Stopwatch.StartNew();
                    lease.Value.Prepare(default);
                    var prepareMs = prepare.Elapsed.TotalMilliseconds;
                    var run = new Run(name, mode, pass, top.Length, lease.Value.SourceCount, snapshot, capture.InitialEnumerationMilliseconds, slices, maxSlice,
                        active + capture.InitialEnumerationMilliseconds + acceptMs, acceptMs, prepareMs, capture.Profile) { Join = joinMs, Wall = timer.Elapsed.TotalMilliseconds };
                    capture.Dispose();
                    runs.Add(run);
                    output.WriteLine($"{name} {mode} {pass}: {run.Leaves} leaves; UI {run.Active:F0} ms in {run.Slices} slices (max {run.MaxSlice:F1}); accept(UI) {run.Accept:F0}; join(bg) {run.Join:F0}; wall {run.Wall:F0}; prepare {run.Prepare:F0}; snapshot {run.Snapshot:F0}; " +
                        string.Join(", ", run.Profile.Select(p => $"{p.Phase}={p.Milliseconds:F0}")));
                }
            }
        }
        DocumentWatch.SimulateInteractive = false;
        var md = new StringBuilder();
        md.AppendLine("| Scene | Mode | Pass | Objects | Leaves | Watch snapshot | UI thread (capture) | Slices | Max slice | Accept (UI) | Join (bg) | Wall | Prepare (off UI) | " +
            string.Join(" | ", Enum.GetValues<SceneCapture.CapturePhase>().Select(p => p + (SceneCapture.OnHostThread(p) ? "" : " (bg)"))) + " |");
        md.AppendLine("|" + string.Concat(Enumerable.Repeat("---|", 13 + Enum.GetNames<SceneCapture.CapturePhase>().Length)));
        foreach (var r in runs)
            md.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"| {r.Scene} | {r.Mode} | {r.Pass} | {r.Objects} | {r.Leaves} | {r.Snapshot:F0} | {r.Active:F0} | {r.Slices} | {r.MaxSlice:F1} | {r.Accept:F0} | {r.Join:F0} | {r.Wall:F0} | {r.Prepare:F0} | ") +
                string.Join(" | ", r.Profile.Select(p => p.Milliseconds.ToString("F0", CultureInfo.InvariantCulture))) + " |");
        var target = Environment.GetEnvironmentVariable("M2D_PROFILE_OUT") ?? Path.Combine(AppContext.BaseDirectory, "artifacts", "profile", "capture.md");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(target, md.ToString());
        output.WriteLine(md.ToString());
    });

    /// <summary>~1,000 more objects of every kind the capture handles: extrusions, curves, meshes, SubDs, points, and
    /// nested block instances with By Parent colour and section style, on several layers with styles and colours.</summary>
    internal static void AddMixed(RhinoDoc document, int count)
    {
        var random = new Random(7);
        var layers = Enumerable.Range(0, 6).Select(i =>
        {
            var parent = document.Layers.Add($"Mixed {i}", Color.FromArgb(40 * i, 120, 200 - 30 * i));
            return document.Layers.Add(new Layer { Name = "Sub", ParentLayerId = document.Layers[parent].Id, Color = Color.FromArgb(200, 30 * i, 90) });
        }).ToArray();
        using (var style = new SectionStyle { BackgroundFillMode = SectionBackgroundFillMode.SolidColor, BackgroundFillColor = Color.Peru })
            document.Layers[layers[0]].SetCustomSectionStyle(style);
        using var part = Brep.CreateFromBox(new BoundingBox(Point3d.Origin, new Point3d(.4, .4, 2)));
        using var leg = new Cylinder(new Circle(Plane.WorldXY, .05), 1).ToBrep(true, true);
        var byParent = new ObjectAttributes { ColorSource = ObjectColorSource.ColorFromParent, SectionAttributesSource = ObjectSectionAttributesSource.FromParent, LayerIndex = layers[1] };
        var inner = document.InstanceDefinitions.Add("mixed-inner", "", Point3d.Origin, [part, leg], [byParent, byParent]);
        using var innerRef = new InstanceReferenceGeometry(document.InstanceDefinitions[inner].Id, Transform.Translation(1, 0, 0));
        using var outerPart = Brep.CreateFromBox(new BoundingBox(Point3d.Origin, new Point3d(.2, 1, .2)));
        var outer = document.InstanceDefinitions.Add("mixed-outer", "", Point3d.Origin, new GeometryBase[] { innerRef, outerPart }, [byParent, new ObjectAttributes { LayerIndex = layers[2] }]);
        for (var i = 0; i < count; i++)
        {
            var at = new Point3d(-60 + random.NextDouble() * 20, -60 + random.NextDouble() * 20, random.NextDouble() * 20);
            var attributes = new ObjectAttributes { LayerIndex = layers[i % layers.Length] };
            if (i % 3 == 0) { attributes.ColorSource = ObjectColorSource.ColorFromObject; attributes.ObjectColor = Color.FromArgb(i % 255, 80, 160); }
            switch (i % 6)
            {
                case 0:
                    using (var e = Extrusion.Create(new Circle(new Plane(at, Vector3d.ZAxis), .3).ToNurbsCurve(), 2, true)) document.Objects.AddExtrusion(e, attributes);
                    break;
                case 1: document.Objects.AddCurve(new Arc(new Circle(new Plane(at, Vector3d.XAxis), .5), Math.PI).ToNurbsCurve(), attributes); break;
                case 2: using (var m = Mesh.CreateFromSphere(new Sphere(at, .4), 8, 8)) document.Objects.AddMesh(m, attributes); break;
                case 3:
                    using (var box = Mesh.CreateFromBox(new BoundingBox(at, at + new Vector3d(.5, .5, .5)), 1, 1, 1))
                    using (var subd = SubD.CreateFromMesh(box)) document.Objects.AddSubD(subd, attributes);
                    break;
                case 4: document.Objects.AddInstanceObject(outer, Transform.Translation(new Vector3d(at)) * Transform.Rotation(i, Vector3d.ZAxis, Point3d.Origin), attributes); break;
                default: document.Objects.AddPoint(at, attributes); break;
            }
        }
    }
}
