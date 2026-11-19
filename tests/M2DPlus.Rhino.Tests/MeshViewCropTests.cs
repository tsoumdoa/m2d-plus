using System.Diagnostics;
using System.Text.Json;
using M2DPlus.Core;
using M2DPlus.Rhino.Services;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using Xunit;
using Environment = System.Environment;

namespace M2DPlus.Rhino.Tests;

[Collection("Rhino")]
[Trait("Category", "RhinoIntegration")]
public sealed class MeshViewCropTests(RhinoHost host)
{
    private const double Tolerance = .001;
    private static string Evidence => Environment.GetEnvironmentVariable("M2D_MESH_CROP_EVIDENCE") ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".t3", "m2d-plus-models", "evidence", "mesh-face-crop");

    internal static Mesh Terrain(int cells)
    {
        var mesh = new Mesh();
        mesh.Vertices.UseDoublePrecisionVertices = true;
        for (int y = 0; y <= cells; y++)
            for (int x = 0; x <= cells; x++)
            {
                double u = 100d * x / cells - 50, v = 100d * y / cells - 50;
                mesh.Vertices.Add(u, v, -8 + 2 * Math.Sin(u / 12) * Math.Cos(v / 15));
            }
        for (int y = 0; y < cells; y++)
            for (int x = 0; x < cells; x++)
            {
                int a = y * (cells + 1) + x, b = a + 1, d = a + cells + 1, c = d + 1;
                mesh.Faces.AddFace(a, b, c); mesh.Faces.AddFace(a, c, d);
            }
        mesh.Normals.ComputeNormals();
        return mesh;
    }

    private static ViewRequest View(double fraction = .05, Plane? camera = null)
    {
        double half = 50 * Math.Sqrt(fraction);
        var plane = camera ?? Plane.WorldXY;
        return ViewRequest.Parse("Top") with { Direction = -plane.Normal, Named = new(plane, new(-half, half), new(-half, half)) };
    }

    private static DrawingBatch Solve(PreparedScene scene, ViewRequest view, bool enabled,
        Action<IReadOnlyList<VisibilityInput>>? observe = null) => NativeDrawing.SolveBorrowedAsync(scene,
        new(scene.Revision, view.Revision, "mesh-crop", 1), [view], new() { CullSources = enabled, ObserveNativeInputs = observe },
        default, null).GetAwaiter().GetResult();

    [RhinoFact] public void TerrainSmallCropPreservesClassedLociAndSharedGeometry() => host.Run(() =>
    {
        using var document = RhinoDoc.CreateHeadless(null);
        using var terrain = Terrain(80);
        using var building = Brep.CreateFromBox(new BoundingBox(new Point3d(-3, -2, -10), new Point3d(3, 2, -2)));
        using var probe = new LineCurve(new Point3d(-10, 0, -10), new Point3d(10, 0, -10));
        using var cache = new SceneCache();
        using var scene = BaselineTests.Capture(document, cache, new GeometryBase[] { terrain, building, probe }
            .Select((g, i) => new CaptureInput(g, [i], "terrain/" + i)));
        var prepared = scene.Value.Prepare(default);
        string before = WorkerCodec.Archive(prepared[0].Geometry);
        var view = View();
        using (var crop = new MeshViewCrop(prepared, view, scene.Value.Bounds, Tolerance, default))
        {
            Assert.True(crop.DroppedFaces > terrain.Faces.Count * .8);
            Assert.NotSame(prepared[0].Geometry, crop.Sources[0].Geometry);
            // Every artificial naked boundary stays well outside the crop, including tolerance.
            var reduced = Assert.IsType<Mesh>(crop.Sources[0].Geometry);
            foreach (var boundary in reduced.GetNakedEdges())
                foreach (var point in boundary)
                    Assert.True(Math.Abs(point.X) > 50 * Math.Sqrt(.05) + .03 || Math.Abs(point.Y) > 50 * Math.Sqrt(.05) + .03);
        }
        Compare(scene.Value, view, "terrain-small");
        Assert.Equal(before, WorkerCodec.Archive(prepared[0].Geometry));
    });

    [RhinoTheory]
    [InlineData(0d, false)]
    [InlineData(.002, false)]
    [InlineData(.1, false)]
    [InlineData(0d, true)]
    [InlineData(.002, true)]
    [InlineData(.1, true)]
    public void BrepEdgesOnOrAboveTerrainAcrossCropKeepProfileAndSolvedClass(double clearance, bool oblique) => host.Run(() =>
    {
        using var document = RhinoDoc.CreateHeadless(null);
        using var terrain = Terrain(80);
        for (int i = 0; i < terrain.Vertices.Count; i++)
        {
            var p = terrain.Vertices.Point3dAt(i);
            terrain.Vertices.SetVertex(i, p.X, p.Y, -8);
        }
        terrain.Normals.ComputeNormals();
        // Both long bottom Brep edges cross both crop edges, touching or just above terrain.
        using var building = Brep.CreateFromBox(new BoundingBox(new Point3d(-45, -2, -8 + clearance), new Point3d(45, 2, -2)));
        using var cache = new SceneCache();
        using var scene = BaselineTests.Capture(document, cache, [new CaptureInput(terrain, [0], "terrain"),
            new CaptureInput(building, [1], "building")]);
        var camera = oblique ? new Plane(Point3d.Origin, Vector3d.XAxis, new Vector3d(0, 1, 1)) : Plane.WorldXY;
        Compare(scene.Value, View(camera: camera), "terrain-edge-" + clearance.ToString(System.Globalization.CultureInfo.InvariantCulture) + "-" + oblique,
            verify: drawing =>
            {
                if (!oblique) return;
                var point = drawing.Frame.Plane.ClosestPoint(new Point3d(0, -2, -8 + clearance));
                Assert.Contains(drawing.Items, p =>
                {
                    using var geometry = p.CopyGeometry();
                    return p.Class == LineClass.Visible && geometry is Curve c && c.ClosestPoint(point, out var t) &&
                        c.PointAt(t).DistanceTo(point) <= Tolerance * 1.25;
                });
            });
    });

    [RhinoTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void EffectiveNamedViewSettingsDriveCropAndFarClip(bool farClip) => host.Run(() =>
    {
        using var document = RhinoDoc.CreateHeadless(null);
        using var terrain = Terrain(80);
        using var front = Brep.CreateFromBox(new BoundingBox(new Point3d(-4, -3, -4), new Point3d(4, 3, -2)));
        // This body crosses depth=12, forcing the named-view-to-Cut rewrite.
        using var crossing = Brep.CreateFromBox(new BoundingBox(new Point3d(-2, -2, -15), new Point3d(2, 2, -10)));
        using var cache = new SceneCache();
        using var scene = BaselineTests.Capture(document, cache, [new CaptureInput(terrain, [0], "terrain"),
            new CaptureInput(front, [1], "front"), new CaptureInput(crossing, [2], "crossing")]);
        var view = View() with { Kind = ViewSourceKind.NamedView, Named = View().Named! with { Crop = false } };
        var context = new ViewSettingsContext("named", view.Name, view.Kind, false, 15, Tolerance);
        var resolved = Settings.Resolve([context], [new[] { "crop=auto", "beyond=off",
            farClip ? "depth=12" : "depth=auto", farClip ? "farclip=line" : "farclip=none" }], DocumentUnits.Metres);
        Assert.False(resolved.HasErrors, string.Join(";", resolved.Diagnostics));
        view = view with { Settings = resolved.Views[0] };
        Assert.True(view.Settings.Crop); Assert.False(view.Named!.Crop); Assert.Null(view.Cut);
        var nativeBodyMinZ = new List<double>();
        using var on = Solve(scene.Value, view, true, inputs => nativeBodyMinZ.AddRange(inputs
            .Where(p => p.Geometry is Brep).Select(p => p.Geometry.GetBoundingBox(true).Min.Z)));
        Assert.Contains("Mesh face crop: dropped", on.Views[0].Info);
        if (farClip)
        {
            Assert.Equal(12, view.Settings.Depth);
            double farZ = (view with { Named = view.Named with { Crop = true } }).Resolve(scene.Value.Bounds, Tolerance).Plane.OriginZ - 12;
            Assert.Contains(nativeBodyMinZ, z => Math.Abs(z - farZ) < Tolerance);
            Assert.All(nativeBodyMinZ, z => Assert.True(z >= farZ - Tolerance));
        }
        Compare(scene.Value, view, "settings-named-" + farClip);

        if (farClip)
        {
            // Also cover effective SourceDepth/FarClip replacing a pre-existing Cut's values.
            var box = ViewRequest.Parse(new Box(Plane.WorldXY, new(-10, 10), new(-10, 10), new(-30, 0)));
            box = box with { Settings = view.Settings, Cut = box.Cut! with { FarClip = FarClipMode.None } };
            nativeBodyMinZ.Clear();
            using var boxOn = Solve(scene.Value, box, true, inputs => nativeBodyMinZ.AddRange(inputs
                .Where(p => p.Geometry is Brep).Select(p => p.Geometry.GetBoundingBox(true).Min.Z)));
            Assert.Null(boxOn.Views[0].Error);
            Assert.Contains(nativeBodyMinZ, z => Math.Abs(z + 12) < Tolerance);
            Assert.All(nativeBodyMinZ, z => Assert.True(z >= -12 - Tolerance));
            Compare(scene.Value, box, "settings-box-depth");
        }
    });

    [RhinoTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void ClosedMeshOpenedThroughItsMiddlePreservesSilhouettesAndOcclusion(bool reverse) => host.Run(() =>
    {
        using var document = RhinoDoc.CreateHeadless(null);
        using var mesh = Mesh.CreateFromBox(new BoundingBox(new Point3d(-50, -50, -8), new Point3d(50, 50, -4)), 80, 80, 2);
        Assert.True(mesh.IsClosed);
        if (reverse) mesh.Flip(true, true, true);
        using var behind = new LineCurve(new Point3d(-2, -55, -10), new Point3d(2, 55, -10));
        using var front = new LineCurve(new Point3d(2, -55, -2), new Point3d(-2, 55, -2));
        using var cache = new SceneCache();
        using var scene = BaselineTests.Capture(document, cache, new GeometryBase[] { mesh, behind, front }
            .Select((g, i) => new CaptureInput(g, [i], "closed/" + i)));
        var view = View() with { Named = new(Plane.WorldXY, new(-5, 5), new(-55, 55)) };
        using var crop = new MeshViewCrop(scene.Value.Prepare(default), view, scene.Value.Bounds, Tolerance, default);
        Assert.True(crop.DroppedFaces > 0);
        Assert.False(Assert.IsType<Mesh>(crop.Sources[0].Geometry).IsClosed);
        Compare(scene.Value, view, "closed-middle-" + reverse);
    });

    [RhinoFact] public void InstancesReceiveSeparateWorldCopiesAndKeepCanonicalSections() => host.Run(() =>
    {
        using var document = RhinoDoc.CreateHeadless(null);
        using var terrain = Terrain(80);
        int definition = document.InstanceDefinitions.Add("terrain", "", Point3d.Origin, [terrain], [new ObjectAttributes()]);
        document.Objects.AddInstanceObject(definition, Transform.Translation(-20, 0, 0));
        document.Objects.AddInstanceObject(definition, Transform.Translation(20, 0, 0));
        document.Objects.AddLine(new Point3d(-10, 0, -20), new Point3d(10, 0, -20));
        document.Objects.AddLine(new Point3d(-10, 3, -2), new Point3d(10, 3, -2));
        using var cache = new SceneCache(); using var scene = BaselineTests.Capture(document, cache);
        var prepared = scene.Value.Prepare(default);
        var meshes = prepared.Where(p => p.Geometry is Mesh).ToArray();
        Assert.Equal(2, meshes.Length); Assert.All(meshes, p => Assert.NotNull(p.Canonical));
        using var crop = new MeshViewCrop(prepared, View(), scene.Value.Bounds, Tolerance, default);
        Assert.Equal(2, crop.ReducedMeshes);
        var reduced = crop.Sources.Where(p => p.Geometry is Mesh).ToArray();
        Assert.NotSame(reduced[0].Geometry, reduced[1].Geometry);
        Assert.All(meshes, p => Assert.NotNull(p.Canonical));
        Compare(scene.Value, View(), "instances");
        var cutPlane = new Plane(new Point3d(0, 0, -8), Vector3d.ZAxis);
        var section = ViewRequest.Parse(new Rectangle3d(cutPlane, new Interval(-10, 10), new Interval(-10, 10)));
        using var untouched = new MeshViewCrop(prepared, section, scene.Value.Bounds, Tolerance, default);
        Assert.Same(prepared, untouched.Sources);
        Assert.All(untouched.Sources.Where(p => p.Geometry is Mesh), p => Assert.NotNull(p.Canonical));
    });

    [RhinoFact] public void ClosedCurvedMeshPreservesLimbSilhouettesAndBackFaces() => host.Run(() =>
    {
        using var document = RhinoDoc.CreateHeadless(null);
        using var mesh = Mesh.CreateFromSphere(new Sphere(new Point3d(0, 0, -60), 50), 120, 100);
        using var probe = new LineCurve(new Point3d(25, 0, -120), new Point3d(55, 0, -120));
        using var cache = new SceneCache();
        using var scene = BaselineTests.Capture(document, cache, [new CaptureInput(mesh, [0], "sphere"), new CaptureInput(probe, [1], "probe")]);
        var view = View() with { Named = new(Plane.WorldXY, new(25, 55), new(-30, 30)) };
        using var crop = new MeshViewCrop(scene.Value.Prepare(default), view, scene.Value.Bounds, Tolerance, default);
        Assert.True(mesh.IsClosed); Assert.True(crop.DroppedFaces > 0);
        Assert.False(Assert.IsType<Mesh>(crop.Sources[0].Geometry).IsClosed);
        Compare(scene.Value, view, "closed-curved");
    });

    [RhinoFact] public void SectionViewProjectionBypassReducesButCrossingBodyIsUntouched() => host.Run(() =>
    {
        using var document = RhinoDoc.CreateHeadless(null); using var terrain = Terrain(80);
        using var cache = new SceneCache();
        using var back = new LineCurve(new Point3d(-8, 0, -15), new Point3d(8, 0, -15));
        using var front = new LineCurve(new Point3d(-8, 3, -2), new Point3d(8, 3, -2));
        using var scene = BaselineTests.Capture(document, cache, [new CaptureInput(terrain, [0], "terrain"),
            new CaptureInput(back, [1], "back"), new CaptureInput(front, [2], "front")]);
        var projection = ViewRequest.Parse(new Box(Plane.WorldXY, new(-10, 10), new(-10, 10), new(-20, 0)));
        using var crop = new MeshViewCrop(scene.Value.Prepare(default), projection, scene.Value.Bounds, Tolerance, default);
        Assert.True(crop.DroppedFaces > 0);
        Compare(scene.Value, projection, "section-projection");
        var automatic = ViewRequest.Parse(new Rectangle3d(Plane.WorldXY, new Interval(-10, 10), new Interval(-10, 10)));
        Assert.Null(automatic.Cut!.SourceDepth);
        Compare(scene.Value, automatic, "section-auto-depth");
        using var closed = Mesh.CreateFromBox(new BoundingBox(new Point3d(-50, -50, -8), new Point3d(50, 50, -4)), 80, 80, 2);
        using var closedScene = BaselineTests.Capture(document, cache, [new CaptureInput(closed, [0], "closed-section")]);
        var section = ViewRequest.Parse(new Rectangle3d(new Plane(new Point3d(0, 0, -6), Vector3d.ZAxis), new Interval(-5, 5), new Interval(-55, 55)));
        var original = closedScene.Value.Prepare(default);
        using var untouched = new MeshViewCrop(original, section, closedScene.Value.Bounds, Tolerance, default);
        Assert.Same(original, untouched.Sources);
        Compare(closedScene.Value, section, "section-crossing", expectReduction: false);
    });

    [RhinoFact] public void SectionAndFarCrossingsBelowThresholdCropOffAndDisabledAreUntouched() => host.Run(() =>
    {
        using var terrain = Terrain(80); using var small = Terrain(20);
        var source = new PreparedLeaf("mesh", [0], terrain);
        PreparedLeaf[] sources = [source];
        var bounds = terrain.GetBoundingBox(true);
        var section = ViewRequest.Parse(new Rectangle3d(new Plane(new Point3d(0, 0, -8), Vector3d.ZAxis), new Interval(-10, 10), new Interval(-10, 10)));
        using var crossing = new MeshViewCrop(sources, section, bounds, Tolerance, default);
        Assert.Same(sources, crossing.Sources);
        var farView = ViewRequest.Parse(new Box(Plane.WorldXY, new(-10, 10), new(-10, 10), new(-8, 0)));
        using var far = new MeshViewCrop(sources, farView, bounds, Tolerance, default);
        Assert.Same(sources, far.Sources);
        var scoped = ViewRequest.Parse(new Box(Plane.WorldXY, new(-10, 10), new(-10, 10), new(-20, 0)));
        scoped = scoped with { Cut = scoped.Cut! with { ScopeEnabled = true } };
        using var nonparticipant = new MeshViewCrop(sources, scoped, bounds, Tolerance, default);
        Assert.Same(sources, nonparticipant.Sources); // Its original bounds also gate Cut/Hatch foreground admission.
        using var disabled = new MeshViewCrop(sources, View(), bounds, Tolerance, default, false);
        Assert.Same(sources, disabled.Sources);
        using var noCrop = new MeshViewCrop(sources, View() with { Named = View().Named! with { Crop = false } }, bounds, Tolerance, default);
        Assert.Same(sources, noCrop.Sources);
        using var full = new MeshViewCrop(sources, View(1), bounds, Tolerance, default);
        Assert.Same(sources, full.Sources);
        PreparedLeaf[] smallSources = [new("small", [0], small)];
        using var below = new MeshViewCrop(smallSources, View(), small.GetBoundingBox(true), Tolerance, default);
        Assert.Same(smallSources, below.Sources);
        using var document = RhinoDoc.CreateHeadless(null); using var cache = new SceneCache();
        using var probe = new LineCurve(new Point3d(-8, 0, -15), new Point3d(8, 0, -15));
        using var scene = BaselineTests.Capture(document, cache, [new CaptureInput(small, [0], "small"), new CaptureInput(probe, [1], "probe")]);
        using var off = Solve(scene.Value, View(), false); using var on = Solve(scene.Value, View(), true);
        Assert.NotEmpty(on.Views[0].Items);
        Assert.Equal(WorkerCodec.CaptureDrawing(off.Views[0], true), WorkerCodec.CaptureDrawing(on.Views[0], true));
        var current = WorkerCodec.Decode<WorkerCodec.Drawing>(WorkerCodec.CaptureDrawing(on.Views[0], true));
        var main = Environment.GetEnvironmentVariable("M2D_MESH_CROP_MAIN");
        if (main is not null)
        {
            var prior = WorkerCodec.Decode<WorkerCodec.Drawing>(File.ReadAllBytes(Path.Combine(main, "below-threshold.archive.json")));
            Assert.Equal(WorkerCodec.Encode(prior), WorkerCodec.Encode(current with { Fingerprint = prior.Fingerprint }));
        }
        Directory.CreateDirectory(Evidence);
        File.WriteAllBytes(Path.Combine(Evidence, "below-threshold.archive.json"), WorkerCodec.Encode(current));
        Save("below-threshold", new { byteIdentical = true, faces = small.Faces.Count });
    });

    [RhinoFact] public void RotatedFrameUsesViewXYAndKeepsOneFaceRing() => host.Run(() =>
    {
        using var terrain = Terrain(80);
        var camera = new Plane(new Point3d(120, -70, 90), new Vector3d(1, 1, 0), new Vector3d(-1, 1, 2));
        terrain.Transform(Transform.PlaneToPlane(Plane.WorldXY, camera));
        var sources = new[] { new PreparedLeaf("rotated", [0], terrain) };
        using var crop = new MeshViewCrop(sources, View(camera: camera), terrain.GetBoundingBox(true), Tolerance, default);
        Assert.True(crop.DroppedFaces > .8 * terrain.Faces.Count);
        using var document = RhinoDoc.CreateHeadless(null); using var cache = new SceneCache();
        using var back = new LineCurve(new Point3d(-8, 0, -15), new Point3d(8, 0, -15));
        using var front = new LineCurve(new Point3d(-8, 3, -2), new Point3d(8, 3, -2));
        back.Transform(Transform.PlaneToPlane(Plane.WorldXY, camera));
        front.Transform(Transform.PlaneToPlane(Plane.WorldXY, camera));
        using var scene = BaselineTests.Capture(document, cache, [new CaptureInput(terrain, [0], "rotated"),
            new CaptureInput(back, [1], "back"), new CaptureInput(front, [2], "front")]);
        Compare(scene.Value, View(camera: camera), "rotated");
    });

    private static void Compare(PreparedScene scene, ViewRequest view, string name, bool expectReduction = true,
        Action<ViewDrawing>? verify = null)
    {
        var main = Environment.GetEnvironmentVariable("M2D_MESH_CROP_MAIN");
        using var originalScene = main is null ? null : WorkerCodec.RestoreScene(File.ReadAllBytes(Path.Combine(main, name + ".scene.json")));
        scene = originalScene ?? scene; // Freeze instance/source identities as well as geometry across builds.
        long offFaces = 0, onFaces = 0;
        using var baseline = Solve(scene, view, false, inputs => offFaces = inputs.Sum(p => p.Geometry is Mesh m ? (long)m.Faces.Count : 0));
        using var candidate = Solve(scene, view, true, inputs => onFaces = inputs.Sum(p => p.Geometry is Mesh m ? (long)m.Faces.Count : 0));
        using var archived = main is null ? null : RestoreBaseline(Path.Combine(main, name + ".archive.json"));
        var a = archived ?? baseline.Views[0]; var b = candidate.Views[0];
        bool? disabledByteIdentical = null;
        if (archived is not null)
        {
            var prior = WorkerCodec.Decode<WorkerCodec.Drawing>(WorkerCodec.CaptureDrawing(archived, true));
            var off = WorkerCodec.Decode<WorkerCodec.Drawing>(WorkerCodec.CaptureDrawing(baseline.Views[0], true));
            Assert.Equal(WorkerCodec.Encode(prior), WorkerCodec.Encode(off));
            disabledByteIdentical = true;
        }
        Assert.Null(a.Error); Assert.Null(b.Error);
        verify?.Invoke(a); verify?.Invoke(b);
        if (expectReduction)
        {
            Assert.Contains("Mesh face crop: dropped", b.Info);
            Assert.True(onFaces < offFaces, $"Native mesh faces did not decrease: {offFaces} -> {onFaces}.");
        }
        else Assert.DoesNotContain("Mesh face crop: dropped", b.Info);
        var missing = Sample(a, b); var extra = Sample(b, a);
        Assert.True(missing.Samples > 0 && extra.Samples > 0, "Locus gate requires nonempty linework.");
        Save(name, new { fingerprint = WorkerCodec.Fingerprint(), spacing = .01, tolerance = Tolerance * 1.25,
            missing, extra, offFaces, onFaces, expectReduction, disabledByteIdentical, baseline = a.Info, candidate = b.Info });
        File.WriteAllBytes(Path.Combine(Evidence, name + ".archive.json"), WorkerCodec.CaptureDrawing(b));
        File.WriteAllBytes(Path.Combine(Evidence, name + ".scene.json"), WorkerCodec.Capture(scene, default));
        Assert.Equal(0, missing.Misses); Assert.Equal(0, extra.Misses);
        Assert.Equal(0, missing.WrongVisible); Assert.Equal(0, extra.WrongVisible);
        Assert.Equal(0, missing.WrongProfile); Assert.Equal(0, extra.WrongProfile);
        Assert.Equal(0, missing.WrongSolvedClass); Assert.Equal(0, extra.WrongSolvedClass);
        HldFixtureComparison.Equal(a, b);
    }

    private static ViewDrawing RestoreBaseline(string path)
    {
        var drawing = WorkerCodec.Decode<WorkerCodec.Drawing>(File.ReadAllBytes(path));
        var current = WorkerCodec.Fingerprint();
        // Cross-build research comparison only: engine assembly hashes necessarily differ.
        // Production cache/transport still rejects this archive. Retain its original manifest on disk.
        Assert.Equal(current.RhinoBuild, drawing.Fingerprint.RhinoBuild);
        Assert.Equal(current.RhinoRevision, drawing.Fingerprint.RhinoRevision);
        Assert.Equal(current.RhinoCommonSha256, drawing.Fingerprint.RhinoCommonSha256);
        Assert.Equal(current.RuntimeVersion, drawing.Fingerprint.RuntimeVersion);
        Assert.Equal(current.Architecture, drawing.Fingerprint.Architecture);
        return WorkerCodec.RestoreDrawing(WorkerCodec.Encode(drawing with { Fingerprint = current }));
    }

    private sealed record Coverage(long Samples, long ProfileSamples, long Misses, long WrongVisible, long WrongProfile, long WrongSolvedClass);
    private static Coverage Sample(ViewDrawing from, ViewDrawing to)
    {
        var targets = to.Items.Select(p => (p.Class, p.Profile, p.SolvedClass, Geometry: p.CopyGeometry())).ToArray();
        using var index = new RTree();
        for (int i = 0; i < targets.Length; i++)
        { var box = targets[i].Geometry.GetBoundingBox(true); box.Inflate(Tolerance * 1.25); index.Insert(box, i); }
        long samples = 0, profileSamples = 0, misses = 0, wrongVisible = 0, wrongProfile = 0, wrongSolvedClass = 0;
        try
        {
            foreach (var item in from.Items)
            {
                using var geometry = item.CopyGeometry();
                if (geometry is not Curve curve) continue;
                int count = Math.Max(1, (int)Math.Ceiling(curve.GetLength() / .01));
                for (int i = 0; i <= count; i++)
                {
                    var point = curve.PointAtNormalizedLength((double)i / count); samples++;
                    if (item.Profile) profileSamples++;
                    bool same = false, other = false, sameProfile = false, sameSolvedClass = false, sameMetadata = false;
                    index.Search(new Sphere(point, Tolerance * 1.25), (_, e) =>
                    {
                        var target = targets[e.Id];
                        if (target.Geometry is Curve c && c.ClosestPoint(point, out var t) && c.PointAt(t).DistanceTo(point) <= Tolerance * 1.25)
                        {
                            if (target.Class == item.Class)
                            {
                                same = true;
                                if (target.Profile == item.Profile) sameProfile = true;
                                if (target.SolvedClass == item.SolvedClass) sameSolvedClass = true;
                                if (target.Profile == item.Profile && target.SolvedClass == item.SolvedClass) sameMetadata = true;
                            }
                            else other = true;
                        }
                    });
                    if (same && !sameProfile) wrongProfile++;
                    if (same && !sameSolvedClass) wrongSolvedClass++;
                    if (sameMetadata) continue;
                    misses++;
                    if (item.Class == LineClass.Visible && !same && other) wrongVisible++;
                }
            }
            return new(samples, profileSamples, misses, wrongVisible, wrongProfile, wrongSolvedClass);
        }
        finally { foreach (var target in targets) target.Geometry.Dispose(); }
    }

    private static void Save(string name, object data)
    { Directory.CreateDirectory(Evidence); File.WriteAllText(Path.Combine(Evidence, name + ".json"), JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true })); }

    [MeshCropBenchmarkTheory]
    [InlineData(.05)]
    [InlineData(.25)]
    [InlineData(1d)]
    public void TerrainTimingEvidence(double fraction)
    {
        host.Run(() =>
        {
            using var document = RhinoDoc.CreateHeadless(null);
            using var terrain = Terrain(240); // 115,200 triangles, generated only outside git.
            document.ModelAbsoluteTolerance = Tolerance;
            document.Objects.AddMesh(terrain);
            for (int i = 0; i < 3; i++)
            {
                using var box = Brep.CreateFromBox(new BoundingBox(new Point3d(-5 + i * 4, -2, -10), new Point3d(-3 + i * 4, 2, -1)));
                document.Objects.AddBrep(box);
            }
            Directory.CreateDirectory(Evidence);
            string model = Path.Combine(Evidence, "synthetic-terrain.3dm");
            if (!File.Exists(model)) document.WriteFile(model, new global::Rhino.FileIO.FileWriteOptions());
            using var cache = new SceneCache(); using var scene = BaselineTests.Capture(document, cache);
            var view = View(fraction);
            using (Solve(scene.Value, view, false)) { }
            using (Solve(scene.Value, view, true)) { }
            var rows = new List<object>();
            foreach (bool enabled in new[] { false, true, true, false })
            {
                var watch = Stopwatch.StartNew();
                using var batch = Solve(scene.Value, view, enabled);
                var drawing = batch.Views[0];
                Assert.Null(drawing.Error);
                rows.Add(new { enabled, wall = watch.Elapsed.TotalSeconds, phases = drawing.Phases.Seconds, drawing.Info });
            }
            Save("timing-" + fraction.ToString(System.Globalization.CultureInfo.InvariantCulture), new {
                fingerprint = WorkerCodec.Fingerprint(), model, hash = WorkerCodec.Hash(File.ReadAllBytes(model)),
                fraction, faces = terrain.Faces.Count, tolerance = Tolerance, warmups = 2, order = "ABBA", rows });
        });
    }
}

public sealed class MeshCropBenchmarkTheoryAttribute : TheoryAttribute
{
    public MeshCropBenchmarkTheoryAttribute()
    {
        if (Environment.GetEnvironmentVariable("M2D_RHINO_TESTS") != "1" || Environment.GetEnvironmentVariable("M2D_MESH_CROP_BENCH") != "1")
            Skip = "Opt-in terrain timings: M2D_RHINO_TESTS=1 and M2D_MESH_CROP_BENCH=1.";
    }
}
