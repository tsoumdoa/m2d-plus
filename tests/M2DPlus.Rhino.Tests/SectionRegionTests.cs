using M2DPlus.Core.Sections;
using M2DPlus.Rhino.Sections;
using Rhino.FileIO;
using Rhino.Geometry;
using Xunit;

namespace M2DPlus.Rhino.Tests;

[Collection("Rhino")]
public sealed class SectionRegionTests(RhinoHost host)
{
    private const double Tol = .0001;
    private static SectionRequest Request => new(Plane.WorldXY, Tol, .001);
    private static SectionSource Source(GeometryBase geometry, string id = "source", uint colour = 0xff336699) => new(id, geometry, SectionStyleSnapshot.Default(colour));
    private static Brep Box(double x0 = -1, double x1 = 1, double y0 = -1, double y1 = 1, double z0 = -1, double z1 = 1) =>
        Brep.CreateFromBox(new BoundingBox(new Point3d(x0, y0, z0), new Point3d(x1, y1, z1)));
    private static Rectangle3d Rectangle(double x0, double x1, double y0, double y1) => new(Plane.WorldXY, new Interval(x0, x1), new Interval(y0, y1));
    private static void Healthy(SectionResult result)
    {
        Assert.False(result.RequiresQuarantine, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        Assert.All(result.Regions.Where(r => r.Status == SectionStatus.Crossing), r => {
            Assert.All(r.Loops, l => Assert.True(l.Curve.IsClosed && l.Curve.IsValid));
            Assert.Equal(1, r.Loops.Count(l => !l.IsHole));
        });
    }
    private static void Area(double expected, SectionResult result) => Assert.InRange(Math.Abs(result.Regions.Sum(r => r.Area) - expected), 0, 2e-6);
    // Independent point-in-material oracle: outer containment and exclusion by every hole.
    private static bool MaterialAt(SectionRegion region, double x, double y) => region.Loops.Any(l => !l.IsHole &&
        l.Curve.Contains(new Point3d(x, y, 0), Plane.WorldXY, Tol) == PointContainment.Inside) &&
        !region.Loops.Any(l => l.IsHole && l.Curve.Contains(new Point3d(x, y, 0), Plane.WorldXY, Tol) == PointContainment.Inside);

    [RhinoFact, Trait("Category", "RhinoIntegration")]
    public void RhsFixturePreservesVoidAndOneHatchBranch() => host.Run(() => {
        using var file = File3dm.Read(Path.Combine(AppContext.BaseDirectory, "fixtures", "rhs_hollow.3dm"));
        using var result = SectionRegionEngine.Compute(new[] { Source(file.Objects.First(o => o.Geometry is Brep).Geometry) }, Request);
        Healthy(result); Area(4 - 1.4 * 1.4, result);
        var region = Assert.Single(result.Regions); Assert.Equal(2, region.Loops.Count); Assert.NotNull(region.Hatch);
        Assert.False(MaterialAt(region, 0, 0)); Assert.True(MaterialAt(region, .85, 0));
        Assert.Single(region.Hatch!.Get3dCurves(false));
    });

    [RhinoFact, Trait("Category", "RhinoIntegration")]
    public void ChsFixtureHasAnalyticAnnulusArea() => host.Run(() => {
        using var file = File3dm.Read(Path.Combine(AppContext.BaseDirectory, "fixtures", "chs_hollow.3dm"));
        using var result = SectionRegionEngine.Compute(new[] { Source(file.Objects.First(o => o.Geometry is Brep).Geometry) }, Request);
        Healthy(result); Area(Math.PI * (1 - .7 * .7), result);
        var region = Assert.Single(result.Regions); Assert.Equal(2, region.Loops.Count);
        Assert.False(MaterialAt(region, 0, 0)); Assert.True(MaterialAt(region, .85, 0));
    });

    [RhinoFact, Trait("Category", "RhinoIntegration")]
    public void IBeamFixtureHasConcaveSingleLoop() => host.Run(() => {
        using var file = File3dm.Read(Path.Combine(AppContext.BaseDirectory, "fixtures", "i_beam.3dm"));
        using var result = SectionRegionEngine.Compute(new[] { Source(file.Objects.First(o => o.Geometry is Brep).Geometry) }, Request);
        Healthy(result); Area(1.76, result); Assert.Single(Assert.Single(result.Regions).Loops);
        Assert.True(MaterialAt(result.Regions[0], 0, 0)); Assert.False(MaterialAt(result.Regions[0], .8, 0));
    });

    [RhinoFact, Trait("Category", "RhinoIntegration")]
    public void HoleInHoleAndMultipleIslandsPreserveMaterialParity() => host.Run(() => {
        using var outer = Tube(4, 3); using var inner = Tube(2, 1); using var combined = outer.DuplicateBrep(); combined.Append(inner);
        using var result = SectionRegionEngine.Compute(new[] { Source(combined) }, Request);
        Healthy(result); Area(10 * Math.PI, result); Assert.Equal(2, result.Regions.Count);
        Assert.All(result.Regions, r => Assert.Equal(2, r.Loops.Count));
        Assert.Contains(result.Regions, r => MaterialAt(r, 3.5, 0)); Assert.DoesNotContain(result.Regions, r => MaterialAt(r, 2.5, 0));
        Assert.Contains(result.Regions, r => MaterialAt(r, 1.5, 0)); Assert.DoesNotContain(result.Regions, r => MaterialAt(r, 0, 0));
    });

    private static Brep Tube(double outer, double inner)
    {
        using var a = new Cylinder(new Circle(new Plane(new Point3d(0, 0, -1), Vector3d.ZAxis), outer), 2).ToBrep(true, true);
        using var b = new Cylinder(new Circle(new Plane(new Point3d(0, 0, -2), Vector3d.ZAxis), inner), 4).ToBrep(true, true);
        return Brep.CreateBooleanDifference(a, b, Tol).Single();
    }

    [RhinoFact, Trait("Category", "RhinoIntegration")]
    public void SeparateIslandsRemainIndependent() => host.Run(() => {
        using var a = Box(-3, -2); using var b = Box(2, 3); a.Append(b);
        using var result = SectionRegionEngine.Compute(new[] { Source(a) }, Request);
        Healthy(result); Area(4, result); Assert.Equal(2, result.Regions.Count);
    });

    [RhinoFact, Trait("Category", "RhinoIntegration")]
    public void CropSplitsLIntoTwoRegionsWithIndependentRectangleAreas() => host.Run(() => {
        // L: [0,4]x[0,1] union [0,1]x[0,4]. Rotated crop u=x+y, v=y-x
        // intersects two arms, removing the connecting corner.
        var points = new[] { new Point3d(0,0,-1), new(4,0,-1), new(4,1,-1), new(1,1,-1), new(1,4,-1), new(0,4,-1), new(0,0,-1) };
        using var profile = new PolylineCurve(points);
        using var extrusion = Extrusion.Create(profile, 2, true); using var solid = extrusion.ToBrep();
        var root = Math.Sqrt(.5);
        var plane = new Plane(Point3d.Origin, new Vector3d(root, root, 0), new Vector3d(-root, root, 0));
        // x+y in [3,4], |y-x| <= 5: each arm is a unit parallelogram, area 1.
        var crop = new Rectangle3d(plane, new Interval(3 * root, 4 * root), new Interval(-5 * root, 5 * root));
        using var result = SectionRegionEngine.Compute(new[] { Source(solid) }, Request with { Crop = crop });
        Healthy(result); Area(2, result); Assert.Equal(2, result.Regions.Count);
        Assert.All(result.Regions, r => Assert.InRange(Math.Abs(r.Area - 1), 0, 1e-7));
        Assert.Contains(result.Regions, r => MaterialAt(r, 3, .5)); Assert.Contains(result.Regions, r => MaterialAt(r, .5, 3));
        Assert.DoesNotContain(result.Regions, r => MaterialAt(r, .5, .5));
    });

    [RhinoFact, Trait("Category", "RhinoIntegration")]
    public void CroppedBoxIsClosedAreaTwoAndHasTwoCropEdges() => host.Run(() => {
        using var box = Box(); using var result = SectionRegionEngine.Compute(new[] { Source(box) }, Request with { Crop = Rectangle(-.5, .5, -2, 2) });
        Healthy(result); Area(2, result); var region = Assert.Single(result.Regions);
        Assert.Equal(2, region.Loops.SelectMany(l => l.Edges).Count(e => e.Origin == EdgeOrigin.Crop));
        Assert.Equal(2, region.Loops.SelectMany(l => l.Edges).Count(e => e.Origin == EdgeOrigin.PhysicalCut));
    });

    [RhinoFact, Trait("Category", "RhinoIntegration")]
    public void CroppedRhsSplitsAroundTheVoid() => host.Run(() => {
        using var file = File3dm.Read(Path.Combine(AppContext.BaseDirectory, "fixtures", "rhs_hollow.3dm"));
        using var result = SectionRegionEngine.Compute(new[] { Source(file.Objects.First(o => o.Geometry is Brep).Geometry) }, Request with { Crop = Rectangle(-.5, .5, -2, 2) });
        Healthy(result); Area(.6, result); Assert.Equal(2, result.Regions.Count);
        Assert.All(result.Regions, r => { Assert.Single(r.Loops); Assert.InRange(Math.Abs(r.Area - .3), 0, 1e-7); });
    });

    [RhinoFact, Trait("Category", "RhinoIntegration")]
    public void ColumnThroughSlabStaysTwoOverlappingRegions() => host.Run(() => {
        using var file = File3dm.Read(Path.Combine(AppContext.BaseDirectory, "fixtures", "column_through_slab.3dm"));
        var sources = file.Objects.Where(o => o.Geometry is Brep).Select((o, i) => Source(o.Geometry, $"{i}"));
        using var result = SectionRegionEngine.Compute(sources, Request);
        Healthy(result); Area(16.16, result); Assert.Equal(2, result.Regions.Count);
        Assert.Equal(new[] { .16, 16 }, result.Regions.Select(r => Math.Round(r.Area, 2)).Order());
        Assert.All(result.Regions, r => Assert.True(MaterialAt(r, 0, 0)));
    });

    [RhinoFact, Trait("Category", "RhinoIntegration")]
    public void TouchingSameStyleMergesAndDifferentStyleStaysSeparate() => host.Run(() => {
        using var a = Box(-2, 0); using var b = Box(0, 2);
        using var merged = SectionRegionEngine.Compute(new[] { Source(b, "b"), Source(a, "a") }, Request with { Cuts = CutMode.Merge });
        Healthy(merged); Area(8, merged); Assert.Equal(new[] { "a", "b" }, Assert.Single(merged.Regions).SourceIds);
        using var separate = SectionRegionEngine.Compute(new[] { Source(b, "b", 0xff112233), Source(a, "a") }, Request with { Cuts = CutMode.Merge });
        Healthy(separate); Area(8, separate); Assert.Equal(2, separate.Regions.Count);
        Assert.Equal("a", separate.Regions[0].SourceIds.Single());
        Assert.DoesNotContain(separate.Diagnostics, d => d.Message.Contains("overlapping incompatible"));
    });

    [RhinoFact, Trait("Category", "RhinoIntegration")]
    public void OverlapUnionPreservesVoidAndIncompatibleOverlapReportsInfo() => host.Run(() => {
        using var tube = Tube(1, .7); using var box = Box(.5, 1.5, -.2, .2);
        using var union = SectionRegionEngine.Compute(new[] { Source(tube, "a"), Source(box, "b") }, Request with { Cuts = CutMode.Merge });
        Healthy(union); Assert.Single(union.Regions); Assert.False(MaterialAt(union.Regions[0], 0, 0)); Assert.True(MaterialAt(union.Regions[0], .6, 0));
        static double ChordIntegral(double radius) => .2 * Math.Sqrt(radius * radius - .04) + radius * radius * Math.Asin(.2 / radius);
        Area(.51 * Math.PI + .4 - (ChordIntegral(1) - ChordIntegral(.7)), union);
        Assert.Equal(2, union.Regions[0].Loops.Count);
        using var a = Box(); using var b = Box(0, 2);
        using var incompatible = SectionRegionEngine.Compute(new[] { Source(b, "b", 0xff112233), Source(a, "a") }, Request with { Cuts = CutMode.Merge });
        Healthy(incompatible); Assert.Equal(2, incompatible.Regions.Count);
        Assert.Contains(incompatible.Diagnostics, d => d.Message.Contains("overlapping incompatible"));
    });

    [RhinoFact, Trait("Category", "RhinoIntegration")]
    public void CoplanarContactHasNoCutAndPreservesContactFace() => host.Run(() => {
        using var box = Box(z0: 0); using var result = SectionRegionEngine.Compute(new[] { Source(box) }, Request);
        Healthy(result); Assert.Empty(result.Regions); Assert.Equal(SectionStatus.Contact, Assert.Single(result.Components).Status);
        using var clipped = PhysicalSectionClipper.Clip(Source(box), Request);
        Assert.True(clipped.Succeeded); var face = Assert.Single(clipped.Pieces); Assert.False(face.Geometry.IsSolid);
        using var area = AreaMassProperties.Compute(face.Geometry); Assert.Equal(4, area.Area, 8);
        Assert.All(face.Edges, e => Assert.Equal(EdgeOrigin.Original, e.Origin));
    });

    [RhinoFact, Trait("Category", "RhinoIntegration")]
    public void NearContactsAtHalfAndTwoTolerancesAreExplicit() => host.Run(() => {
        foreach (var distance in new[] { -.5, .5, -2, 2 })
        {
            using var bottom = Box(z0: distance * Tol);
            using var result = SectionRegionEngine.Compute(new[] { Source(bottom) }, Request);
            var expected = Math.Abs(distance) < 1 ? SectionStatus.Uncertain : distance < 0 ? SectionStatus.Crossing : SectionStatus.InFront;
            Assert.Equal(expected, Assert.Single(result.Components).Status);
            if (expected == SectionStatus.Crossing) { Healthy(result); Area(4, result); } else Assert.Empty(result.Regions);
            using var top = Box(z1: distance * Tol);
            using var topResult = SectionRegionEngine.Compute(new[] { Source(top) }, Request);
            Assert.Equal(Math.Abs(distance) < 1 ? SectionStatus.Uncertain : distance > 0 ? SectionStatus.Crossing : SectionStatus.Behind,
                Assert.Single(topResult.Components).Status);
        }
    });

    [RhinoFact, Trait("Category", "RhinoIntegration")]
    public void OpenSurfaceAndOpenCylinderGiveOutlinesWithoutHatches() => host.Run(() => {
        using var surface = new PlaneSurface(Plane.WorldYZ, new Interval(-1, 1), new Interval(-1, 1)); using var brep = surface.ToBrep();
        using var cylinder = new Cylinder(new Circle(new Plane(new Point3d(0, 0, -1), Vector3d.ZAxis), 1), 2).ToBrep(false, false);
        foreach (var source in new[] { brep, cylinder })
        {
            using var result = SectionRegionEngine.Compute(new[] { Source(source) }, Request);
            Healthy(result); Assert.NotEmpty(result.Regions);
            Assert.All(result.Regions, r => { Assert.Null(r.Hatch); Assert.Equal(SectionStatus.OutlineOnly, r.Status); });
        }
    });

    [RhinoFact, Trait("Category", "RhinoIntegration")]
    public void DisconnectedBrepOnOppositeSidesIsNotACut() => host.Run(() => {
        using var a = Box(z0: -2, z1: -1); using var b = Box(z0: 1, z1: 2); a.Append(b);
        using var result = SectionRegionEngine.Compute(new[] { Source(a) }, Request);
        Healthy(result); Assert.Empty(result.Regions); Assert.Equal(2, result.Components.Count);
        Assert.Contains(result.Components, c => c.Status == SectionStatus.Behind); Assert.Contains(result.Components, c => c.Status == SectionStatus.InFront);
        using var clipped = PhysicalSectionClipper.Clip(Source(a), Request); Assert.True(clipped.Succeeded); Assert.Single(clipped.Pieces);
    });

    [RhinoFact, Trait("Category", "RhinoIntegration")]
    public void ClosedMeshCubeHasOneCutLoopAndClosedVisibilityCap() => host.Run(() => {
        using var mesh = Mesh.CreateFromBox(new BoundingBox(new Point3d(-1, -1, -1), new Point3d(1, 1, 1)), 1, 1, 1);
        using var result = SectionRegionEngine.Compute(new[] { Source(mesh) }, Request);
        Healthy(result); Area(4, result); Assert.Single(Assert.Single(result.Regions).Loops);
        Assert.Equal(8, result.Regions[0].Loops[0].Curve.GetLength(), 7);
        using var clipped = PhysicalSectionClipper.Clip(Source(mesh), Request); Assert.True(clipped.Succeeded, string.Join(";", clipped.Diagnostics));
        Assert.True(Assert.Single(clipped.Pieces).Geometry.IsSolid);
        Assert.Equal(4, clipped.Pieces[0].Geometry.GetVolume(), 7);
    });

    [RhinoFact, Trait("Category", "RhinoIntegration")]
    public void NearFarTrimsHaveArtificialEdgeProvenanceAndDepthGuards() => host.Run(() => {
        using var box = Box(z0: -3);
        var far = new Plane(new Point3d(0, 0, -2), -Vector3d.ZAxis);
        using var result = PhysicalSectionClipper.Clip(Source(box), Request, far);
        Assert.True(result.Succeeded); var piece = Assert.Single(result.Pieces);
        Assert.Equal(8, piece.Geometry.GetVolume(), 7);
        Assert.Contains(piece.Edges, e => e.Origin == EdgeOrigin.NearTrim); Assert.Contains(piece.Edges, e => e.Origin == EdgeOrigin.FarTrim);
        Assert.Contains(piece.Edges, e => e.Origin == EdgeOrigin.Original);
        using var zero = PhysicalSectionClipper.Clip(Source(box), Request, new Plane(Point3d.Origin, -Vector3d.ZAxis));
        Assert.True(zero.SectionOnly); Assert.Empty(zero.Pieces);
        using var thin = PhysicalSectionClipper.Clip(Source(box), Request, new Plane(new Point3d(0, 0, -.5 * Tol), -Vector3d.ZAxis));
        Assert.False(thin.Succeeded); Assert.Empty(thin.Pieces);
    });

    [RhinoFact, Trait("Category", "RhinoIntegration")]
    public void HatchesEmbedPatternDefinitionAndSurviveSaveReopen() => host.Run(() => {
        using var box = Box();
        var style = SectionStyleSnapshot.Default() with { HasStyle = true,
            Pattern = new("Embedded lines", false, new[] { new PatternLine(.2, default, new(0, .1), new[] { .2, -.1 }) }),
            Scale = 3, Rotation = .37, PatternColour = 0xffff0000, PatternPrintColour = 0xff800000 };
        using var result = SectionRegionEngine.Compute(new[] { new SectionSource("styled", box, style) }, Request);
        Healthy(result); var region = Assert.Single(result.Regions); Assert.NotNull(region.Hatch);
        var path = Path.Combine(Path.GetTempPath(), $"m2-sections-{Guid.NewGuid():N}.3dm");
        try
        {
            using (var file = new File3dm()) { Assert.NotEqual(Guid.Empty, SectionHatches.AddToFile(file, region)); Assert.True(file.Write(path, 8)); }
            using var reopened = File3dm.Read(path);
            var baked = Assert.Single(reopened.Objects);
            var hatch = Assert.IsType<Hatch>(baked.Geometry);
            var pattern = reopened.AllHatchPatterns.FindIndex(hatch.PatternIndex);
            Assert.NotNull(pattern); Assert.Equal(global::Rhino.DocObjects.HatchPatternFillType.Lines, pattern.FillType);
            Assert.Equal(1, pattern.HatchLineCount); using var line = pattern.HatchLineAt(0);
            Assert.Equal(.2, line.Angle, 10); Assert.Equal(.1, line.Offset.Y, 10); Assert.Equal(new[] { .2, -.1 }, line.GetDashes);
            Assert.Equal(3, hatch.PatternScale, 10); Assert.Equal(.37, hatch.PatternRotation, 10);
            Assert.Equal(unchecked((int)0xffff0000), baked.Attributes.ObjectColor.ToArgb());
        }
        finally { File.Delete(path); }
    });

    [RhinoFact, Trait("Category", "RhinoIntegration")]
    public void StyleOverridesRetainNullSlotsAndSourceGeometryIsUnmodified() => host.Run(() => {
        using var box = Box(); var original = box.GetVolume();
        foreach (var mode in new[] { HatchMode.Styles, HatchMode.Colour, HatchMode.Black, HatchMode.None })
        {
            using var result = SectionRegionEngine.Compute(new[] { Source(box) }, Request with { Hatch = mode });
            Healthy(result); var region = Assert.Single(result.Regions);
            Assert.Equal(mode == HatchMode.None, region.Hatch is null);
            if (mode == HatchMode.Black) Assert.Equal(0xff000000u, region.Style.PatternColour);
        }
        Assert.Equal(original, box.GetVolume(), 10); Assert.Equal(1, box.GetBoundingBox(true).Max.Z);
        using var noFill = SectionRegionEngine.Compute(new[] { Source(box) with { Style = SectionStyleSnapshot.Default() with { HasStyle = true } } }, Request);
        Assert.Null(Assert.Single(noFill.Regions).Hatch);
    });

    [RhinoFact, Trait("Category", "RhinoIntegration")]
    public void S2MeshAndRhsAdversarialFixturesAndNonmanifoldFailure() => host.Run(() => {
        using var rhs = File3dm.Read(Path.Combine(AppContext.BaseDirectory, "fixtures", "sections", "rhs_void.3dm"));
        using var rhsResult = SectionRegionEngine.Compute(new[] { Source(rhs.Objects.First(o => o.Geometry is Brep).Geometry) }, Request);
        Healthy(rhsResult); Area(2.04, rhsResult); Assert.Equal(2, Assert.Single(rhsResult.Regions).Loops.Count);
        using var mesh = File3dm.Read(Path.Combine(AppContext.BaseDirectory, "fixtures", "sections", "mesh_straddle.3dm"));
        using var meshResult = SectionRegionEngine.Compute(new[] { Source(mesh.Objects.First(o => o.Geometry is Mesh).Geometry) }, Request);
        Healthy(meshResult); Area(4, meshResult);
        using var invalid = File3dm.Read(Path.Combine(AppContext.BaseDirectory, "fixtures", "mesh_nonmanifold.3dm"));
        using var failure = SectionRegionEngine.Compute(new[] { Source(invalid.Objects.First(o => o.Geometry is Mesh).Geometry) }, Request);
        Assert.True(failure.RequiresQuarantine); Assert.Empty(failure.Regions);
        using var clipFailure = PhysicalSectionClipper.Clip(Source(invalid.Objects.First(o => o.Geometry is Mesh).Geometry), Request);
        Assert.False(clipFailure.Succeeded); Assert.Empty(clipFailure.Pieces);
    });

    [RhinoFact, Trait("Category", "RhinoIntegration")]
    public void NestedVoidShellsRemainVoidsDuringRegionAndVisibilityCapping() => host.Run(() => {
        using var outer = Box(-2, 2, -2, 2, -2, 2); using var cavity = Box(); cavity.Flip(); outer.Append(cavity);
        using var result = SectionRegionEngine.Compute(new[] { Source(outer) }, Request);
        Healthy(result); Area(12, result); var region = Assert.Single(result.Regions); Assert.Equal(2, region.Loops.Count);
        Assert.False(MaterialAt(region, 0, 0)); Assert.True(MaterialAt(region, 1.5, 0));
        using var clipped = PhysicalSectionClipper.Clip(Source(outer), Request);
        Assert.True(clipped.Succeeded, string.Join(";", clipped.Diagnostics));
        var piece = Assert.Single(clipped.Pieces); Assert.True(piece.Geometry.IsSolid);
        Assert.Equal(28, piece.Geometry.GetVolume(), 7);
        Assert.False(piece.Geometry.IsPointInside(new Point3d(0, 0, -.5), Tol, true));
        Assert.True(piece.Geometry.IsPointInside(new Point3d(1.5, 0, -.5), Tol, true));
    });

    [RhinoFact, Trait("Category", "RhinoIntegration")]
    public void IntersectingShellsAndOpenMeshOccludersAreRejected() => host.Run(() => {
        using var a = Box(); using var b = Box(0, 2, 0, 2, 0, 2); a.Append(b);
        using var result = SectionRegionEngine.Compute(new[] { Source(a) }, Request);
        Assert.True(result.RequiresQuarantine); Assert.Empty(result.Regions);
        using var file = File3dm.Read(Path.Combine(AppContext.BaseDirectory, "fixtures", "mesh_open_quad.3dm"));
        using var clipped = PhysicalSectionClipper.Clip(Source(file.Objects.First(o => o.Geometry is Mesh).Geometry), Request);
        Assert.False(clipped.Succeeded); Assert.Empty(clipped.Pieces);
    });

    [RhinoFact, Trait("Category", "RhinoIntegration")]
    public void RigidPlacementAndInputPermutationPreserveRegionIdentityAndArea() => host.Run(() => {
        using var a = Box(-3, -2); using var b = Box(2, 3);
        using var first = SectionRegionEngine.Compute(new[] { Source(a, "a"), Source(b, "b") }, Request);
        using var permuted = SectionRegionEngine.Compute(new[] { Source(b, "b"), Source(a, "a") }, Request);
        Healthy(first); Healthy(permuted); Assert.Equal(first.Regions.Select(r => r.Id), permuted.Regions.Select(r => r.Id));
        var transform = Transform.Translation(10, 20, 30) * Transform.Rotation(.7, Vector3d.XAxis, Point3d.Origin);
        a.Transform(transform); b.Transform(transform); var plane = Plane.WorldXY; plane.Transform(transform);
        using var placed = SectionRegionEngine.Compute(new[] { Source(b, "b"), Source(a, "a") }, Request with { Plane = plane });
        Healthy(placed); Area(4, placed); Assert.All(placed.Regions.SelectMany(r => r.Loops), l => Assert.True(l.Curve.IsInPlane(plane, Tol)));
    });

    [RhinoFact, Trait("Category", "RhinoIntegration")]
    public void CroppedChsAndNestedIslandsMatchCircleIntegrals() => host.Run(() => {
        using var outer = Tube(4, 3); using var inner = Tube(2, 1); using var nested = outer.DuplicateBrep(); nested.Append(inner);
        using var chs = Tube(1, .7);
        static double Integral(double r) => r * r * Math.Asin(.5 / r) + .5 * Math.Sqrt(r * r - .25);
        using var circles = SectionRegionEngine.Compute(new[] { Source(chs) }, Request with { Crop = Rectangle(-.5, .5, -5, 5) });
        Healthy(circles); Area(2 * (Integral(1) - Integral(.7)), circles); Assert.Equal(2, circles.Regions.Count);
        using var islands = SectionRegionEngine.Compute(new[] { Source(nested) }, Request with { Crop = Rectangle(-.5, .5, -5, 5) });
        Healthy(islands); Area(2 * (Integral(4) - Integral(3) + Integral(2) - Integral(1)), islands); Assert.Equal(4, islands.Regions.Count);
        Assert.All(islands.Regions, r => Assert.Single(r.Loops));
    });

    [RhinoFact, Trait("Category", "RhinoIntegration")]
    public void ExtrusionInputAndTriangulatedMeshHaveAnalyticCuts() => host.Run(() => {
        using var profile = new Rectangle3d(new Plane(new Point3d(0, 0, -1), Vector3d.ZAxis), 2, 1).ToNurbsCurve();
        using var extrusion = Extrusion.Create(profile, 2, true);
        using var result = SectionRegionEngine.Compute(new[] { Source(extrusion) }, Request);
        Healthy(result); Area(2, result); Assert.Single(Assert.Single(result.Regions).Loops);
        using var file = File3dm.Read(Path.Combine(AppContext.BaseDirectory, "fixtures", "mesh_closed_tri.3dm"));
        using var triangleResult = SectionRegionEngine.Compute(new[] { Source(file.Objects.First(o => o.Geometry is Mesh).Geometry) }, Request);
        Healthy(triangleResult); Area(4, triangleResult); Assert.Single(Assert.Single(triangleResult.Regions).Loops);
        Assert.Equal(8, triangleResult.Regions[0].Loops[0].Curve.GetLength(), 7);
    });

    [RhinoFact, Trait("Category", "RhinoIntegration")]
    public void SelfIntersectingMeshAndUnverifiedFreeformNeverGetFill() => host.Run(() => {
        using var a = Mesh.CreateFromBox(new BoundingBox(new Point3d(-1,-1,-1), new Point3d(1,1,1)), 1, 1, 1);
        using var b = Mesh.CreateFromBox(new BoundingBox(new Point3d(0,0,0), new Point3d(2,2,2)), 1, 1, 1); a.Append(b);
        using var result = SectionRegionEngine.Compute(new[] { Source(a) }, Request);
        Assert.True(result.RequiresQuarantine); Assert.Empty(result.Regions);
        using var file = File3dm.Read(Path.Combine(AppContext.BaseDirectory, "fixtures", "subd_converted_brep.3dm"));
        using var freeform = SectionRegionEngine.Compute(new[] { Source(file.Objects.First(o => o.Geometry is Brep).Geometry) }, Request);
        Assert.True(freeform.RequiresQuarantine); Assert.Empty(freeform.Regions);
    });

    [RhinoFact, Trait("Category", "RhinoIntegration")]
    public void PartialCoincidenceRetainsDualCutCropProvenance() => host.Run(() => {
        using var box = Box();
        using var result = SectionRegionEngine.Compute(new[] { Source(box) }, Request with { Crop = Rectangle(-1, .5, -2, 2) });
        Healthy(result); Area(3, result);
        var edges = Assert.Single(result.Regions).Loops.SelectMany(l => l.Edges).ToArray();
        Assert.Contains(edges, e => e.Origin == (EdgeOrigin.PhysicalCut | EdgeOrigin.Crop));
        Assert.Contains(edges, e => e.Origin == EdgeOrigin.Crop);
        Assert.InRange(edges.Sum(e => e.Curve.GetLength()), 7 - 1e-8, 7 + 1e-8);
    });

    [RhinoFact, Trait("Category", "RhinoIntegration")]
    public void ExactEdgeAndPointContactsRemainOrdinaryFeatures() => host.Run(() => {
        using var box = Box();
        var edgeRequest = Request with { Plane = new Plane(new Point3d(-1, -1, 0), new Vector3d(1, 1, 0)) };
        using var cut = SectionRegionEngine.Compute(new[] { Source(box) }, edgeRequest);
        Healthy(cut); Assert.Empty(cut.Regions); Assert.Equal(SectionStatus.Contact, Assert.Single(cut.Components).Status);
        using var edge = PhysicalSectionClipper.Clip(Source(box), edgeRequest);
        Assert.True(edge.Succeeded); Assert.Empty(edge.Pieces);
        Assert.Equal(2, Assert.IsAssignableFrom<Curve>(Assert.Single(edge.ContactFeatures).Geometry).GetLength(), 8);
        var pointRequest = Request with { Plane = new Plane(new Point3d(-1, -1, -1), new Vector3d(1, 1, 1)) };
        using var point = PhysicalSectionClipper.Clip(Source(box), pointRequest);
        Assert.True(point.Succeeded); Assert.Empty(point.Pieces);
        Assert.Equal(new Point3d(-1, -1, -1), Assert.IsType<global::Rhino.Geometry.Point>(Assert.Single(point.ContactFeatures).Geometry).Location);
    });
}



