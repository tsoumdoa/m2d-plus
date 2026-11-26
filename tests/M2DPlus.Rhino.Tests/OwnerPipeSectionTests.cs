using M2DPlus.Core.Sections;
using M2DPlus.Rhino.Sections;
using Rhino.Geometry;
using Xunit;

namespace M2DPlus.Rhino.Tests;

public sealed class OwnerPipeTheoryAttribute : TheoryAttribute
{
    internal static string Model => Path.Combine(Environment.GetEnvironmentVariable("M2D_MODELS") ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".t3", "m2d-plus-models"),
        "owner", "pipes-node-gdas.3dm");

    public OwnerPipeTheoryAttribute()
    {
        if (Environment.GetEnvironmentVariable("M2D_RHINO_TESTS") != "1")
            Skip = "Licensed Rhino.Inside integration: set M2D_RHINO_TESTS=1 explicitly.";
        else if (!File.Exists(Model)) Skip = "Optional owner pipe model absent under M2D_MODELS/owner.";
    }
}

[Collection("Rhino")]
[Trait("Category", "RhinoIntegration")]
public sealed class OwnerPipeSectionTests(RhinoHost host)
{
    // Closed/valid topology alone does not make the approximate pipe
    // junctions trustworthy at .001 mm. Keep local geometric certification strict.
    [OwnerPipeTheory]
    [InlineData("3368e2f7-d2b7-41c8-ad54-3c6d62d5c04a", null, 0)]
    [InlineData("5dfd542b-34fd-45bf-8ac1-bb45346dad19", null, 0)]
    [InlineData("417ea64b-b422-423c-b001-88e33fb04f1b", "cannot associate loop direction", 0)]
    [InlineData("6aaf0a84-4883-44f7-b37f-7df6f7120a9b", null, 42080.415346)]
    [InlineData("7072d67a-02b9-4c39-aa8e-1181aaa9cee8", "cannot associate loop direction", 0)]
    public void JunctionCutRetainsOnlyGeometricallyCertifiedBodies(string id, string? refusal, double expectedArea) => host.Run(() =>
    {
        using var file = global::Rhino.FileIO.File3dm.Read(OwnerPipeTheoryAttribute.Model);
        var shape = Assert.IsType<Brep>(file.Objects.Single(o => o.Attributes.ObjectId == Guid.Parse(id)).Geometry);
        Assert.True(shape.IsValid && shape.IsSolid);
        var plane = new Plane(new Point3d(65986, 40014, 14800), Vector3d.ZAxis);
        using var sections = SectionRegionEngine.Compute([new(id, shape, SectionStyleSnapshot.Default())],
            new(plane, .001, .001), prepareVisibility: true);
        var clip = sections.Visibility[id];
        if (refusal is not null)
        {
            Assert.False(clip.Succeeded); Assert.Empty(clip.Pieces); Assert.Empty(sections.Regions);
            Assert.Contains(sections.Diagnostics, d => d.Message.Contains(refusal, StringComparison.Ordinal));
            Assert.NotEmpty(sections.Refusals);
            return;
        }
        Assert.True(clip.Succeeded, string.Join("; ", clip.Diagnostics.Select(d => d.Message)));
        Assert.NotEmpty(clip.Pieces); Assert.NotEmpty(sections.Regions);
        Assert.All(clip.Pieces, p => Assert.True(p.Geometry.IsValid && p.Geometry.IsSolid));
        if (expectedArea > 0)
        {
            Assert.Empty(sections.Refusals);
            var region = Assert.Single(sections.Regions);
            var loop = Assert.Single(region.Loops);
            Assert.False(loop.IsHole); Assert.True(loop.Curve.IsValid && loop.Curve.IsClosed);
            using var intersections = global::Rhino.Geometry.Intersect.Intersection.CurveSelf(loop.Curve, .001);
            Assert.Empty(intersections);
            Assert.InRange(Math.Abs(region.Area - expectedArea), 0, .000001);
        }
    });
}
