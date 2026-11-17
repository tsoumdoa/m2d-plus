using M2DPlus.Core;
using M2DPlus.Core.Views;
using M2DPlus.Rhino.Services;
using M2DPlus.Rhino.Views;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Parameters;
using Grasshopper.Kernel.Types;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using System.Reflection;
using Environment = System.Environment;
using Xunit;
using Xunit.Abstractions;

namespace M2DPlus.Rhino.Tests;

[Collection("Rhino")]
[Trait("Category", "RhinoIntegration")]
public sealed class SurfaceSourceTests(RhinoHost host, ITestOutputHelper output)
{
    private const double Tol = .00001;
    private static void Near(Point3d a, Point3d b) => Assert.True(a.DistanceTo(b) < Tol, $"{a} != {b}");
    private static void NearVector(Vector3d a, Vector3d b) => Assert.True((a - b).Length < Tol, $"{a} != {b}");
    private static PlaneSurface Surface() => new(new Plane(new Point3d(8, 3, 5), Vector3d.ZAxis, Vector3d.XAxis), new(-2, 2), new(-3, 3));

    [RhinoFact] public void LicensedChecksRunOnTheRequestedRuntime() => host.Run(() =>
    {
        Assert.Equal("1", Environment.GetEnvironmentVariable("M2D_RHINO_TESTS"));
        if (Environment.GetEnvironmentVariable("M2D_EXPECT_RUNTIME") is { } expected)
            Assert.Equal(int.Parse(expected), Environment.Version.Major);
        output.WriteLine($"M2D_RHINO_TESTS=1; runtime={Environment.Version}; Rhino={RhinoApp.Version}");
    });

    [RhinoTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void SlightlySkewedSurfaceUsesBoundaryAxesWithinTheDocumentAngleTolerance(bool flipped) => host.Run(() =>
    {
        using var doc = RhinoDoc.CreateHeadless(null); doc.ModelAbsoluteTolerance = Tol;
        doc.ModelAngleToleranceRadians = Math.PI / 180;
        var skew = 6 * Math.Tan(Math.PI / 18000); // 0.01 degrees, accepted by the 1 degree rectangle rule.
        using var surface = NurbsSurface.CreateFromCorners(new Point3d(0, 0, 3), new Point3d(4, 0, 3),
            new Point3d(4 + skew, 6, 3), new Point3d(skew, 6, 3));
        using var brep = surface.ToBrep(); Assert.True(brep.Faces[0].IsSurface);
        if (flipped) brep.Flip();
        var id = doc.Objects.AddBrep(brep);
        foreach (var value in new object[] { brep, id })
        {
            var view = ViewRequest.Parse(value, doc); Assert.Null(view.Error);
            var plane = view.Cut!.Plane;
            Near(new Point3d(2 + skew / 2, 3, 3), plane.Origin);
            NearVector(flipped ? -Vector3d.XAxis : Vector3d.XAxis, plane.XAxis);
            NearVector(Vector3d.YAxis, plane.YAxis);
            NearVector(flipped ? Vector3d.ZAxis : -Vector3d.ZAxis, view.Direction);
            Assert.Equal(4 + skew, view.Cut.Crop!.Value.Width, 8);
            Assert.Equal(6, view.Cut.Crop.Value.Height, 8);
        }
    });

    [RhinoFact] public void PointEditedPlanarNurbsUsesTheOutlineAxesRatherThanObliqueMidpointTangents() => host.Run(() =>
    {
        using var surface = NurbsSurface.Create(3, false, 4, 4, 4, 4);
        surface.KnotsU.CreateUniformKnots(1); surface.KnotsV.CreateUniformKnots(1);
        for (var u = 0; u < 4; u++) for (var v = 0; v < 4; v++)
            surface.Points.SetPoint(u, v, new Point3d(4d * u / 3, 2d * v, 3));
        surface.Points.SetPoint(1, 1, new Point3d(4d / 3 + .5, 2.3, 3));
        Assert.True(surface.Evaluate(surface.Domain(0).Mid, surface.Domain(1).Mid, 1, out _, out var derivatives));
        Assert.True(Math.Abs(derivatives[0] * derivatives[1]) > .01);
        var view = ViewRequest.Parse(surface); Assert.Null(view.Error);
        NearVector(Vector3d.XAxis, view.Cut!.Plane.XAxis); NearVector(Vector3d.YAxis, view.Cut.Plane.YAxis);
        Near(new Point3d(2, 3, 3), view.Cut.Plane.Origin);
        Assert.Equal(4, view.Cut.Crop!.Value.Width, 8); Assert.Equal(6, view.Cut.Crop.Value.Height, 8);
    });

    [RhinoTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void RotatedUntrimmedSurfaceKeepsItsAxesAndNormalForReferencesAndGeometry(bool flipped) => host.Run(() =>
    {
        using var doc = RhinoDoc.CreateHeadless(null); using var surface = Surface(); using var brep = surface.ToBrep();
        var rotation = Transform.Rotation(.63, new Vector3d(1, 2, 3), new Point3d(-4, 7, 2));
        Assert.True(brep.Transform(rotation));
        if (flipped) brep.Flip();
        var centre = new Point3d(8, 3, 5); centre.Transform(rotation);
        var x = flipped ? -Vector3d.ZAxis : Vector3d.ZAxis; x.Transform(rotation);
        var y = Vector3d.XAxis; y.Transform(rotation);
        var direction = flipped ? Vector3d.YAxis : -Vector3d.YAxis; direction.Transform(rotation);
        var id = doc.Objects.AddBrep(brep);
        foreach (var value in new object[] { brep, id })
        {
            var view = ViewRequest.Parse(value, doc); Assert.Null(view.Error);
            Near(centre, view.Cut!.Plane.Origin); NearVector(x, view.Cut.Plane.XAxis);
            NearVector(y, view.Cut.Plane.YAxis); NearVector(direction, view.Direction);
        }
    });

    [RhinoTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void TransposedFaceDerivesNormalAndAxesFromTheSameParameterSpace(bool flipped) => host.Run(() =>
    {
        using var surface = Surface(); using var brep = surface.ToBrep();
        Assert.NotNull(brep.Faces[0].Transpose(true));
        if (flipped) brep.Flip();
        var face = brep.Faces[0];
        Assert.True(face.Evaluate(face.Domain(0).Mid, face.Domain(1).Mid, 1, out _, out var derivatives));
        var expectedNormal = Vector3d.CrossProduct(derivatives[0], derivatives[1]); expectedNormal.Unitize();
        if (face.OrientationIsReversed) expectedNormal.Reverse();
        var view = ViewRequest.Parse(face); Assert.Null(view.Error);
        NearVector(-expectedNormal, view.Direction);
        Assert.True(view.Cut!.Plane.YAxis * derivatives[1] > 0);
        NearVector(view.Cut.Plane.ZAxis, Vector3d.CrossProduct(view.Cut.Plane.XAxis, view.Cut.Plane.YAxis));
    });

    [RhinoTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void RectanglePlanarSrfChecksIsSurfaceAndWorksReferencedOrWiredAsGeometry(bool wired) => host.Run(() =>
    {
        Assert.True(global::Rhino.PlugIns.PlugIn.LoadPlugIn(new Guid("b45a29b1-4343-4035-989e-044e8580d9cf")));
        using var doc = RhinoDoc.CreateHeadless(null); RhinoDoc.ActiveDoc = doc; doc.ModelAbsoluteTolerance = Tol;
        var plane = new Plane(new Point3d(0, 0, 3), Vector3d.XAxis, -Vector3d.YAxis);
        using var rectangle = new Rectangle3d(plane, new Interval(-3, 3), new Interval(-3, 3)).ToNurbsCurve();
        using var brep = Brep.CreatePlanarBreps(rectangle, Tol).Single();
        Assert.True(brep.Faces[0].IsSurface); // Rhino optimizes Rectangle + PlanarSrf to an untrimmed surface.
        var check = ViewBoxes.Establish(brep, Tol, Math.PI / 180);
        Assert.True(check.IsValid, check.Reason); Assert.True(check.UsesSurfaceAxes);
        var face = brep.Faces[0];
        Assert.True(face.Evaluate(face.Domain(0).Mid, face.Domain(1).Mid, 1, out _, out var derivatives));
        var normal = Vector3d.CrossProduct(derivatives[0], derivatives[1]); normal.Unitize();
        if (face.OrientationIsReversed) normal.Reverse();
        var id = doc.Objects.AddBrep(brep);
        using var gh = new GH_Document { Enabled = true };
        var assembly = Assembly.LoadFrom(Path.Combine(AppContext.BaseDirectory, "M2DPlus.Grasshopper.gha"));
        var component = (GH_Component)Activator.CreateInstance(assembly.GetType("M2DPlus.Grasshopper.DrawingComponent", true)!)!;
        gh.AddObject(component, false);
        using var box = Brep.CreateFromBox(new BoundingBox(new Point3d(-1, -1, -1), new Point3d(1, 1, 1)));
        ((Param_GenericObject)component.Params.Input[0]).PersistentData.Append(new GH_ObjectWrapper(box));
        try
        {
            if (wired)
            {
                var parameter = new Param_Surface(); parameter.PersistentData.Append(new GH_Surface(brep));
                gh.AddObject(parameter, false); component.Params.Input[1].AddSource(parameter);
            }
            else ((Param_GenericObject)component.Params.Input[1]).PersistentData.Append(new GH_Surface(id));
            gh.NewSolution(true, GH_SolutionMode.Silent);
            GrasshopperTests.PumpUntil(() => component.Params.Output[8].VolatileDataCount == 1,
                () => component.Message + string.Join(";", component.RuntimeMessages(GH_RuntimeMessageLevel.Error)),
                () => { if (gh.ScheduleDelay >= 0) gh.NewSolution(false, GH_SolutionMode.Silent); });
            var frame = ((GH_Rectangle)component.Params.Output[7].VolatileData.AllData(true).Single()).Value;
            NearVector(normal, frame.Plane.ZAxis); Assert.Equal(36, frame.Area, 8);
            Near(new Point3d(0, 0, 3), frame.Plane.Origin);
            Assert.True(frame.Plane.YAxis * derivatives[1] > 0);
            Assert.DoesNotContain(component.RuntimeMessages(GH_RuntimeMessageLevel.Remark), m => m.Contains("Use an untrimmed rectangular surface"));
            if (!wired) Assert.Contains(component.RuntimeMessages(GH_RuntimeMessageLevel.Remark), m => m.Contains("Right-click View > Convert to view boxes"));
        }
        finally { component.RemovedFromDocument(gh); SceneCache.Shared.Clear(); }
    });

    [RhinoFact] public void AttributeSchemaSurvivesMoveCopyAndExtrusionRebuildWithoutGeometryUserStrings() => host.Run(() =>
    {
        using var doc = RhinoDoc.CreateHeadless(null); doc.ModelAbsoluteTolerance = Tol;
        using var surface = Surface();
        var id = doc.Objects.AddSurface(surface);
        Assert.True(Assert.Single(ViewBoxes.Validate(doc, [id], DepthChoice.Of(4))).Ok);
        var before = ViewRequest.Parse(id, doc); var move = Transform.Translation(12, -4, 7);
        var movedId = doc.Objects.Transform(id, move, true); Assert.NotEqual(Guid.Empty, movedId);
        var copyId = doc.Objects.Transform(movedId, Transform.Translation(-8, 3, 1), false); Assert.NotEqual(Guid.Empty, copyId);
        foreach (var objectId in new[] { movedId, copyId })
        {
            var obj = doc.Objects.FindId(objectId);
            Assert.Equal("2", obj.Attributes.GetUserString(ViewBoxes.Marker));
            Assert.DoesNotContain(obj.Geometry.GetUserStrings().AllKeys, key => key?.StartsWith("m2d.", StringComparison.OrdinalIgnoreCase) == true);
            var view = ViewRequest.Parse(objectId, doc);
            Near(view.Cut!.Crop!.Value.Center, view.Cut.Plane.Origin);
            NearVector(before.Direction, view.Direction);
            using var rebuilt = ViewBoxes.Build(view.Cut.Crop.Value, 4);
            Assert.True(doc.Objects.Replace(objectId, rebuilt));
            var after = ViewRequest.Parse(objectId, doc);
            Near(view.Cut.Plane.Origin, after.Cut!.Plane.Origin);
            Assert.Equal("2", doc.Objects.FindId(objectId).Attributes.GetUserString(ViewBoxes.Marker));
        }
        // The geometry itself carries no m2d.* exclusion tag when it is used as an ordinary scene object.
        using var geometry = doc.Objects.FindId(copyId).Geometry.Duplicate();
        using var attributes = new ObjectAttributes();
        var plainId = doc.Objects.Add(geometry, attributes);
        Assert.True(SceneCapture.IsSelectable(doc, doc.Objects.FindId(plainId), attributes));
    });

    [RhinoFact] public void FlipOfAVersionOneBoxRetainsTheLegacyLowerLeftAnchor() => host.Run(() =>
    {
        using var doc = RhinoDoc.CreateHeadless(null);
        var frame = new Rectangle3d(Plane.WorldXY, new Interval(-3, 5), new Interval(2, 8));
        using var box = ViewBoxes.Build(frame, 4); using var attributes = new ObjectAttributes();
        attributes.SetUserString(ViewBoxes.Marker, "1");
        var id = doc.Objects.AddExtrusion(box, attributes);
        var before = ViewRequest.Parse(id, doc); var expected = ViewBoxes.Flip(before.Cut!.Crop!.Value);
        Assert.True(Assert.Single(ViewBoxes.Flip(doc, [id])).Ok);
        var after = ViewRequest.Parse(id, doc);
        Near(expected.Plane.Origin, after.Cut!.Plane.Origin);
        Near(after.Cut.Crop!.Value.Corner(0), after.Cut.Plane.Origin);
        Assert.Equal("1", doc.Objects.FindId(id).Attributes.GetUserString(ViewBoxes.Marker));
        Assert.Null(doc.Objects.FindId(id).Geometry.GetUserString("m2d.sourceorigin"));
        Assert.True(Assert.Single(ViewBoxes.Validate(doc, [id])).Ok);
        Assert.Equal("1", doc.Objects.FindId(id).Attributes.GetUserString(ViewBoxes.Marker));
    });

    [RhinoFact] public void UntrimmedSurfaceUsesCentreUvAndActualFaceNormalForGeometryAndReferences() => host.Run(() =>
    {
        using var doc = RhinoDoc.CreateHeadless(null);
        using var surface = Surface(); using var brep = surface.ToBrep();
        var centre = new Point3d(8, 3, 5);
        foreach (var flipped in new[] { false, true })
        {
            if (flipped) brep.Flip();
            Assert.Equal(flipped, brep.Faces[0].OrientationIsReversed);
            var id = doc.Objects.AddBrep(brep);
            foreach (var value in new object[] { brep, brep.Faces[0], id })
            {
                var view = ViewRequest.Parse(value, doc, new BoundingBox(new Point3d(-100, -100, -100), Point3d.Origin));
                Assert.Null(view.Error);
                if (value is Guid) Assert.Contains("Right-click View > Convert to view boxes", view.Note);
                else Assert.Null(view.Note);
                Near(centre, view.Cut!.Plane.Origin);
                NearVector(Vector3d.XAxis, view.Cut.Plane.YAxis); // V stays up, even when V is not world-up.
                NearVector(flipped ? -Vector3d.ZAxis : Vector3d.ZAxis, view.Cut.Plane.XAxis);
                NearVector(flipped ? Vector3d.YAxis : -Vector3d.YAxis, view.Direction);
                NearVector(view.Cut.Plane.ZAxis, Vector3d.CrossProduct(view.Cut.Plane.XAxis, view.Cut.Plane.YAxis));
                Assert.Equal(24, view.Cut.Crop!.Value.Area, 8);
            }
            doc.Objects.Delete(id, true);
        }
        var direct = ViewRequest.Parse(surface);
        Near(centre, direct.Cut!.Plane.Origin); NearVector(-Vector3d.YAxis, direct.Direction);
    });

    [RhinoFact] public void TrimmedRectanglesAndRectangleCurvesStillFaceTheModelAndGiveAGreyRemark() => host.Run(() =>
    {
        using var doc = RhinoDoc.CreateHeadless(null);
        using var big = new PlaneSurface(Plane.WorldXY, new(-20, 20), new(-20, 20)).ToBrep();
        using var outline = new Rectangle3d(Plane.WorldXY, new Interval(-2, 2), new Interval(-3, 3)).ToNurbsCurve();
        using var split = big.Faces[0].Split([outline], Tol);
        using var trimmed = split.Faces.Select(f => f.DuplicateFace(false)).Single(b => b.GetArea() < 30);
        Assert.False(trimmed.Faces[0].IsSurface);
        var scene = new BoundingBox(new Point3d(-1, -1, 1), new Point3d(1, 1, 5));
        foreach (var geometry in new GeometryBase[] { trimmed, outline })
        {
            var id = doc.Objects.Add(geometry);
            var direct = ViewRequest.Parse(geometry, doc, scene);
            var referenced = ViewRequest.Parse(id, doc, scene);
            foreach (var view in new[] { direct, referenced })
            {
                Assert.Null(view.Error); Assert.Contains("untrimmed rectangular surface", view.Note);
                NearVector(-Vector3d.ZAxis, view.Direction); // Existing horizontal-rectangle plan convention.
                Assert.Equal(24, view.Cut!.Crop!.Value.Area, 8);
            }
            Assert.Contains("Right-click View > Convert to view boxes", referenced.Note);
        }
    });

    [RhinoFact] public void ConversionKeepsSurfaceDirectionAxesCentreAndTargetPlacementForEitherNormal() => host.Run(() =>
    {
        using var doc = RhinoDoc.CreateHeadless(null); doc.ModelAbsoluteTolerance = Tol;
        using var surface = Surface(); using var brep = surface.ToBrep();
        var target = new Plane(new Point3d(100, 20, 0), Vector3d.YAxis, Vector3d.ZAxis);
        foreach (var flipped in new[] { false, true })
        {
            if (flipped) brep.Flip();
            var id = doc.Objects.AddBrep(brep);
            var before = ViewRequest.Parse(id, doc);
            Assert.True(Assert.Single(ViewBoxes.Validate(doc, [id], DepthChoice.Of(4))).Ok);
            var after = ViewRequest.Parse(id, doc);
            NearVector(before.Direction, after.Direction);
            Near(before.Cut!.Plane.Origin, after.Cut!.Plane.Origin);
            NearVector(before.Cut.Plane.XAxis, after.Cut.Plane.XAxis);
            NearVector(before.Cut.Plane.YAxis, after.Cut.Plane.YAxis);
            for (var j = 0; j < 4; j++) Near(before.Cut.Crop!.Value.Corner(j), after.Cut.Crop!.Value.Corner(j));
            using var rawBefore = FrameOnly(before); using var rawAfter = FrameOnly(after);
            using var placedBefore = DrawingPresentation.Apply(rawBefore, rawBefore.Identity, [before], [target]);
            using var placedAfter = DrawingPresentation.Apply(rawAfter, rawAfter.Identity, [after], [target]);
            Near(target.Origin, placedBefore.Views[0].Frame.Center);
            for (var j = 0; j < 4; j++) Near(placedBefore.Views[0].Frame.Corner(j), placedAfter.Views[0].Frame.Corner(j));
            Assert.True(Assert.Single(ViewBoxes.Flip(doc, [id])).Ok);
            var reversed = ViewRequest.Parse(id, doc);
            Near(after.Cut.Plane.Origin, reversed.Cut!.Plane.Origin);
            NearVector(-after.Direction, reversed.Direction);
            NearVector(after.Cut.Plane.YAxis, reversed.Cut.Plane.YAxis);
        }
    });

    [RhinoFact] public void ExistingViewBoxesKeepTheirLowerLeftAnchorEvenWithAnOffsetProfile() => host.Run(() =>
    {
        var frame = new Rectangle3d(Plane.WorldXY, new Interval(-3, 5), new Interval(2, 8));
        using var box = ViewBoxes.Build(frame, 4);
        var read = ViewBoxes.ReadBox(box, Tol, Math.PI / 180);
        Assert.True(read.IsValid);
        Near(new Point3d(-3, 2, 0), read.Frame!.Value.Plane.Origin);
        NearVector(Vector3d.XAxis, read.Frame.Value.Plane.XAxis);
        NearVector(Vector3d.YAxis, read.Frame.Value.Plane.YAxis);
        for (var i = 0; i < 4; i++) Near(frame.Corner(i), read.Frame.Value.Corner(i));
    });

    private static DrawingBatch FrameOnly(ViewRequest request) => new(new("surface", "view", "frame", 1),
        [new ViewDrawing { Frame = request.Cut!.Crop!.Value, Tolerance = Tol }]);

    [RhinoFact] public void FlippedSurfaceCutsTheBoxFromTheOtherSideAndRetainsOnlyPointsBehindIt() => host.Run(() =>
    {
        using var doc = RhinoDoc.CreateHeadless(null); doc.ModelAbsoluteTolerance = Tol;
        using var box = Brep.CreateFromBox(new BoundingBox(new Point3d(-1, -1, -2), new Point3d(1, 1, 3)));
        doc.Objects.AddBrep(box);
        var below = doc.Objects.AddPoint(new Point3d(2, 0, -1)).ToString();
        var above = doc.Objects.AddPoint(new Point3d(2, 0, 1)).ToString();
        using var surface = new PlaneSurface(Plane.WorldXY, new(-4, 4), new(-4, 4));
        using var brep = surface.ToBrep(); using var scene = new SceneCache();
        foreach (var flipped in new[] { false, true })
        {
            if (flipped) brep.Flip();
            var view = ViewRequest.Parse(brep);
            using var batch = BaselineTests.Solve(BaselineTests.Capture(doc, scene), view);
            var drawing = Assert.Single(batch.Views);
            Assert.Null(drawing.Error); Assert.False(drawing.Report.Quarantined, drawing.Info);
            Assert.NotEmpty(drawing.Regions);
            Assert.Contains(drawing.Items, p => p.SourceIdentity == (flipped ? above : below));
            Assert.DoesNotContain(drawing.Items, p => p.SourceIdentity == (flipped ? below : above));
            foreach (var region in drawing.Regions)
                foreach (var loop in region.Region.Loops)
                    Assert.True(loop.Curve.GetBoundingBox(true).Min.Z > -Tol && loop.Curve.GetBoundingBox(true).Max.Z < Tol);
        }
    });

    [RhinoFact] public void ExplicitSourceMovesPiecesRegionsHatchesFrameAndBakeByTheSameUnprojectedTransform() => host.Run(() =>
    {
        using var doc = RhinoDoc.CreateHeadless(null); doc.ModelAbsoluteTolerance = Tol;
        using var box = Brep.CreateFromBox(new BoundingBox(new Point3d(-1, -1, -1), new Point3d(1, 1, 2)));
        doc.Objects.AddBrep(box);
        using var scene = new SceneCache();
        var request = ViewRequest.Parse(Plane.WorldXY);
        using var raw = BaselineTests.Solve(BaselineTests.Capture(doc, scene), request);
        using var inPlace = DrawingPresentation.Apply(raw, raw.Identity, [request], []);
        var source = new Plane(new Point3d(3.123456, 4.456789, 5.98765), Vector3d.YAxis, Vector3d.ZAxis);
        var target = new Plane(new Point3d(100, 20, 7), Vector3d.XAxis, Vector3d.YAxis);
        var orient = Transform.PlaneToPlane(source, target);
        using var placed = DrawingPresentation.Apply(raw, raw.Identity, [request], [target], [source]);
        var a = inPlace.Views[0]; var b = placed.Views[0];
        Assert.Equal(a.Items.Count, b.Items.Count); Assert.Equal(a.Regions.Count, b.Regions.Count);
        for (var i = 0; i < 4; i++) { var p = a.Frame.Corner(i); p.Transform(orient); Near(p, b.Frame.Corner(i)); }
        for (var i = 0; i < a.Items.Count; i++)
        {
            Assert.Equal(a.Items[i].SourceIdentity, b.Items[i].SourceIdentity);
            Assert.Equal(a.Items[i].Class, b.Items[i].Class);
            using var original = a.Items[i].CopyGeometry(); using var moved = b.Items[i].CopyGeometry();
            original.Transform(orient); BoundsEqual(original, moved);
        }
        Assert.NotEmpty(a.Regions);
        for (var i = 0; i < a.Regions.Count; i++)
        {
            var ar = a.Regions[i].Region; var br = b.Regions[i].Region;
            for (var j = 0; j < ar.Loops.Count; j++)
            { using var loop = ar.Loops[j].Curve.DuplicateCurve(); loop.Transform(orient); BoundsEqual(loop, br.Loops[j].Curve); }
            Assert.NotNull(ar.Hatch); Assert.NotNull(br.Hatch);
            using var hatch = ar.Hatch.Duplicate(); hatch.Transform(orient); BoundsEqual(hatch, br.Hatch);
        }
        Assert.Contains("Placement: Source", b.Info);
        var placementLine = Assert.Single(b.Diagnostics, line => line.StartsWith("Placement: Source", StringComparison.Ordinal));
        Assert.Equal("Placement: Source → Target (1:1); origins (3.123, 4.457, 5.988) → (100, 20, 7).", placementLine.TrimEnd('\r'));
        var baked = DrawingBake.Bake(doc, Guid.NewGuid(), [new(b, "Source placement")]);
        Assert.NotEmpty(baked.Added);
        var expected = BoundingBox.Empty;
        foreach (var item in b.Items) { using var g = item.CopyGeometry(); expected.Union(g.GetBoundingBox(true)); }
        foreach (var region in b.Regions) foreach (var loop in region.Region.Loops) expected.Union(loop.Curve.GetBoundingBox(true));
        var actual = BoundingBox.Empty;
        foreach (var id in baked.Added) actual.Union(doc.Objects.FindId(id).Geometry.GetBoundingBox(true));
        Near(expected.Min, actual.Min); Near(expected.Max, actual.Max);
    });

    private static void BoundsEqual(GeometryBase a, GeometryBase b)
    { Near(a.GetBoundingBox(true).Min, b.GetBoundingBox(true).Min); Near(a.GetBoundingBox(true).Max, b.GetBoundingBox(true).Max); }

    [RhinoFact] public void SourceChangesReuseVisibilityForEveryViewKindAndDoNotAlterTheAuthoredView() => host.Run(() =>
    {
        using var doc = RhinoDoc.CreateHeadless(null); doc.ModelAbsoluteTolerance = Tol;
        // A small ellipsoid requires native HLD in the current engine too, so the counter proves cache reuse.
        using var surfaceGeometry = NurbsSurface.CreateFromSphere(new Sphere(Point3d.Origin, 1));
        surfaceGeometry.Transform(Transform.Scale(Plane.WorldXY, 1, 1, 1.5));
        using var ellipsoid = surfaceGeometry.ToBrep(); doc.Objects.AddBrep(ellipsoid);
        using var sceneCache = new SceneCache(); using var scene = BaselineTests.Capture(doc, sceneCache);
        using var surface = new PlaneSurface(new Plane(new Point3d(0, 0, 3), Vector3d.ZAxis), new(-3, 3), new(-3, 3));
        var plane = new Plane(new Point3d(0, 0, 3), Vector3d.ZAxis);
        using var viewport = new global::Rhino.DocObjects.ViewportInfo { IsParallelProjection = true };
        using var clipping = new ClippingPlaneSurface(plane);
        viewport.SetCameraLocation(new Point3d(0, 0, 10)); viewport.SetCameraDirection(-Vector3d.ZAxis); viewport.SetCameraUp(Vector3d.YAxis);
        viewport.SetFrustum(-3, 3, -3, 3, 1, 100);
        var requests = new object[] { "Top", -Vector3d.ZAxis, plane, new Rectangle3d(plane, 6, 6),
            new Box(plane, new(-3, 3), new(-3, 3), new(-7, 0)), surface, viewport, clipping };
        foreach (var value in requests)
        {
            var view = ViewRequest.Parse(value); Assert.Null(view.Error);
            var revision = view.Revision; using var cache = new DrawingCache();
            var identity = new RequestIdentity(scene.Value.Revision, revision, "source", 1);
            DrawingBatch Draw(Plane[] sources, Plane[] targets) => cache.SolveAsync(scene.Share(), identity, [view],
                new(), targets, default, sources: sources).GetAwaiter().GetResult();
            var before = NativeDrawing.HldCalls;
            using var first = Draw([], [Plane.WorldXY]);
            Assert.Null(first.Views[0].Error);
            Assert.True(NativeDrawing.HldCalls > before);
            var calls = NativeDrawing.HldCalls;
            using var changed = Draw([new Plane(new Point3d(4, 5, 6), Vector3d.YAxis, Vector3d.ZAxis)], [Plane.WorldXY]);
            using var ignored = Draw([plane], []);
            Assert.Equal(calls, NativeDrawing.HldCalls); Assert.Equal(revision, view.Revision);
            Assert.Equal(first.Views[0].Items.Count, changed.Views[0].Items.Count);
            Assert.Contains("without Target", ignored.Views[0].Info);
        }
    });
}
