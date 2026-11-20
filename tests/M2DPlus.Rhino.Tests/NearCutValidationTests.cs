using M2DPlus.Core.Sections;
using M2DPlus.Rhino.Sections;
using M2DPlus.Rhino.Services;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using Xunit;

namespace M2DPlus.Rhino.Tests;

[Collection("Rhino")]
[Trait("Category", "RhinoIntegration")]
public sealed class NearCutValidationTests(RhinoHost host)
{
    private const double Tol = .0001;
    private static Plane Cut(double z) => new(new Point3d(0, 0, z), Vector3d.ZAxis);
    private static SectionValidation Slab(double z) => SectionValidation.For(new(Cut(z), Tol, .001));

    // A closed, manifold polyhedron. The lower prism is a square; only the
    // upper ring is crossed. Splitting at z=2 keeps the remote defect's face
    // bounds off the z=1 section. No private model geometry is used.
    private static Brep CrossedSolid()
    {
        using var mesh = new Mesh();
        foreach (var z in new[] { 0d, 2d })
        foreach (var xy in new[] { (-1d, -1d), (1d, -1d), (1d, 1d), (-1d, 1d) })
            mesh.Vertices.Add(xy.Item1, xy.Item2, z);
        foreach (var xy in new[] { (-1d, -1d), (1d, 1d), (1d, -1d), (-1d, 1d) })
            mesh.Vertices.Add(xy.Item1, xy.Item2, 4);
        for (var ring = 0; ring < 2; ring++)
        for (var i = 0; i < 4; i++)
        {
            var a = ring * 4 + i; var b = ring * 4 + (i + 1) % 4;
            mesh.Faces.AddFace(a, b, b + 4, a + 4);
        }
        mesh.Faces.AddFace(0, 3, 2, 1);
        mesh.Faces.AddFace(8, 9, 10); mesh.Faces.AddFace(8, 10, 11);
        mesh.Faces.ConvertQuadsToTriangles();
        mesh.Normals.ComputeNormals();
        var solid = Brep.CreateFromMesh(mesh, true);
        Assert.True(solid.IsValid && solid.IsManifold && solid.IsSolid);
        return solid;
    }

    [RhinoFact]
    public void RemoteDefectAllowsSquareSectionButCutDefectStillRefuses() => host.Run(() =>
    {
        using var solid = CrossedSolid(); using var cache = new SectionVerdictCache();
        var source = new SectionSource("crossed", solid, SectionStyleSnapshot.Default());
        Assert.StartsWith("Self-intersecting Brep faces", SectionGeometry.Verdict(source, Tol, cache));
        Assert.Equal("", SectionGeometry.Verdict(source, Tol, cache, Slab(1)));
        Assert.StartsWith("Self-intersecting Brep faces", SectionGeometry.Verdict(source, Tol, cache, Slab(3)));
        using var near = SectionRegionEngine.Compute([source], new(Cut(1), Tol, .001));
        var region = Assert.Single(near.Regions);
        Assert.NotNull(region.Hatch); Assert.InRange(Math.Abs(region.Area - 4), 0, 1e-8);
        using var bad = SectionRegionEngine.Compute([source], new(Cut(3), Tol, .001));
        Assert.Empty(bad.Regions); Assert.NotEmpty(bad.Refusals);
    });

    [RhinoFact]
    public void FarSlabAndPerturbationMarginParticipateInVerdictIdentity() => host.Run(() =>
    {
        using var solid = CrossedSolid(); using var cache = new SectionVerdictCache();
        var source = new SectionSource("crossed", solid, SectionStyleSnapshot.Default());
        var near = Slab(5);
        Assert.Equal("", SectionGeometry.Verdict(source, Tol, cache, near));
        Assert.StartsWith("Self-intersecting Brep faces", SectionGeometry.Verdict(source, Tol, cache, near with { Far = Cut(3) }));
        Assert.NotEqual(near.Key, (near with { Far = Cut(3) }).Key);
        Assert.NotEqual(near.Key, (near with { Margin = near.Margin * 2 }).Key);
        Assert.NotEqual(near.Key, Slab(3).Key);
        var box = new BoundingBox(new Point3d(-1, -1, 1 + 4 * Tol), new Point3d(1, 1, 2));
        Assert.Equal(1, Slab(1).Touches(box));
        Assert.Equal(0, Slab(0).Touches(box));
        Assert.Equal(2, (Slab(0) with { Far = Cut(1) }).Touches(box));
        var shifted = Slab(1) with { Near = new Plane(new Point3d(1000000, -2000000, 1), Vector3d.ZAxis) };
        Assert.Equal(Slab(1).Key, shifted.Key);
        Assert.Equal(Slab(1).Touches(box), shifted.Touches(box));
    });

    [RhinoFact]
    public void SectionPolicyChangesTimingIdentityWithoutChangingUncutHldIdentity() => host.Run(() =>
    {
        var settings = new NativeSettings(); var fingerprint = WorkerCodec.Fingerprint();
        string PreviousKey(ViewRequest view) => WorkerCodec.Hash(WorkerCodec.Encode(new
        {
            fingerprint, Policy = HldProjection.Policy, settings.CullSources,
            View = view with { Settings = null }, Settings = view.Settings?.VisibilityKey()
        }));
        var uncut = ViewRequest.Parse("Top"); var cut = ViewRequest.Parse(Cut(1));
        Assert.Equal(PreviousKey(uncut), WorkerCodec.TimingKey(uncut, settings, fingerprint));
        Assert.NotEqual(PreviousKey(cut), WorkerCodec.TimingKey(cut, settings, fingerprint));
    });

    [RhinoFact]
    public void DefinitionLocalCutsSeparateHeightsScalesMirrorsAndSurveyCoordinates() => host.Run(() =>
    {
        using var shape = CrossedSolid(); using var doc = RhinoDoc.CreateHeadless(null); using var sceneCache = new SceneCache();
        doc.ModelAbsoluteTolerance = Tol;
        var definition = doc.InstanceDefinitions.Add("synthetic crossed solid", "", Point3d.Origin, [shape], [new ObjectAttributes()]);
        // The same world cut crosses the sound prism in one placement and the
        // defective upper half in another, including scaled/mirrored instances.
        var cases = new[]
        {
            (Scale: 1d, LocalZ: 1d, Mirror: false), (Scale: 1d, LocalZ: 3d, Mirror: false),
            (Scale: 2d, LocalZ: 1d, Mirror: true), (Scale: 2d, LocalZ: 3d, Mirror: true)
        };
        foreach (var c in cases)
        {
            var transform = Transform.Translation(1000000, 2000000, 100 - c.Scale * c.LocalZ) * Transform.Scale(Point3d.Origin, c.Scale);
            if (c.Mirror) transform *= Transform.Mirror(Plane.WorldYZ);
            doc.Objects.AddInstanceObject(definition, transform);
        }
        using var lease = BaselineTests.Capture(doc, sceneCache);
        var sources = lease.Value.Prepare(default).Select(l => new SectionSource(l.Identity, l.Geometry, SectionStyleSnapshot.Default()) { Canonical = l.Canonical }).ToArray();
        using var verdicts = new SectionVerdictCache();
        var validation = Slab(100);
        var results = sources.Select(s => SectionGeometry.Verdict(s, Tol, verdicts, validation)).ToArray();
        Assert.Equal(2, results.Count(s => s.Length == 0));
        Assert.Equal(2, results.Count(s => s.StartsWith("Self-intersecting Brep faces", StringComparison.Ordinal)));
        Assert.Equal(4, verdicts.Computations);
        // Exercise the engine's independent centering of source geometry and
        // its canonical placement, not just the direct verdict entry point.
        using var sections = SectionRegionEngine.Compute(sources, new(Cut(100), Tol, .001), verdicts, default);
        Assert.Equal(2, sections.Regions.Count);
        Assert.InRange(Math.Abs(sections.Regions.Sum(r => r.Area) - 20), 0, 1e-6);
    });
}
