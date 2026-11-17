using System.Reflection;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Parameters;
using Grasshopper.Kernel.Types;
using M2DPlus.Core;
using M2DPlus.Core.Sections;
using M2DPlus.Rhino.Services;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using Xunit;
using Xunit.Abstractions;
using Point = Rhino.Geometry.Point;

namespace M2DPlus.Rhino.Tests;

[Collection("Rhino")]
[Trait("Category", "RhinoIntegration")]
public sealed class SectionWiringTests(RhinoHost host, ITestOutputHelper output)
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "fixtures", name + ".3dm");
    private static void Healthy(ViewDrawing view) { Assert.Null(view.Error); Assert.False(view.Report.Quarantined, view.Info); }
    private static double Length(ViewDrawing view, LineClass classification, string? identity = null) => view.Items
        .Where(p => p.Class == classification && (identity is null || p.SourceIdentity == identity))
        .Sum(p => { using var g = p.CopyGeometry(); return g is Curve c ? c.GetLength() : 0; });

    [RhinoTheory]
    [InlineData("crossing_curve", 2, 1)]
    [InlineData("rhs_void", 5.4, .6)]
    public void FrozenAdversarialProbeIntervals(string fixture, double visible, double hidden) => host.Run(() =>
    {
        using var document = RhinoDoc.OpenHeadless(Fixture("sections/" + fixture)); using var cache = new SceneCache();
        using var lease = BaselineTests.Capture(document, cache);
        var curve = Assert.Single(lease.Value.Prepare(default), s => s.Geometry is Curve);
        using var batch = BaselineTests.Solve(lease, ViewRequest.Parse(Plane.WorldXY));
        var view = Assert.Single(batch.Views); output.WriteLine(view.Info); Healthy(view);
        Assert.InRange(Math.Abs(Length(view, LineClass.Visible, curve.Identity) - visible), 0, .001);
        Assert.InRange(Math.Abs(Length(view, LineClass.Hidden, curve.Identity) - hidden), 0, .001);
        Assert.All(view.Regions, r => Assert.NotNull(r.Region.Hatch));
    });

    [RhinoFact] public void FrozenPointsUseTheClippedCapAndRhsVoid() => host.Run(() =>
    {
        foreach (var fixture in new[] { "points_cut_solid", "rhs_void" })
        {
            using var document = RhinoDoc.OpenHeadless(Fixture("sections/" + fixture)); using var cache = new SceneCache();
            using var batch = BaselineTests.Solve(BaselineTests.Capture(document, cache), ViewRequest.Parse(Plane.WorldXY));
            var view = Assert.Single(batch.Views); output.WriteLine(view.Info); Healthy(view);
            if (fixture == "points_cut_solid") Assert.True(view.HiddenPoints >= 2, view.Info);
            var points = view.Items.Select(p => p.CopyGeometry()).ToArray();
            try { Assert.DoesNotContain(points.OfType<Point>(), p => Math.Abs(p.Location.X) < .01 && fixture == "points_cut_solid"); }
            finally { foreach (var geometry in points) geometry.Dispose(); }
        }
        using var own = RhinoDoc.CreateHeadless(null); own.ModelAbsoluteTolerance = .0001;
        using var box = Brep.CreateFromBox(new BoundingBox(new Point3d(-1, -1, -1), new Point3d(1, 1, 1)));
        var values = new[] { new CaptureInput(box, new[] { 0 }, "solid"), new CaptureInput(new Point3d(0, 0, -.0002), new[] { 1 }, "near"),
            new CaptureInput(new Point3d(3, 0, -2), new[] { 1 }, "outside") };
        using var ownCache = new SceneCache();
        using var drawing = BaselineTests.Solve(BaselineTests.Capture(own, ownCache, values), ViewRequest.Parse(Plane.WorldXY));
        Healthy(drawing.Views[0]); Assert.Equal(1, drawing.Views[0].HiddenPoints);
        Assert.Contains(drawing.Views[0].Items, p => p.SourceIdentity == "outside");
        Assert.DoesNotContain(drawing.Views[0].Items, p => p.SourceIdentity == "near");
    });

    [RhinoFact] public void ScopeIncludesAndExcludesObjectsLayersAndNestedInstanceIds() => host.Run(() =>
    {
        foreach (var exclusion in new[] { false, true })
        foreach (var layerScope in new[] { false, true })
        {
            using var document = RhinoDoc.CreateHeadless(null); document.ModelAbsoluteTolerance = .0001;
            var layer = document.Layers.Add("occluder", System.Drawing.Color.DarkRed);
            using var box = Brep.CreateFromBox(new BoundingBox(new Point3d(-1, -1, .1), new Point3d(1, 1, 1)));
            var boxId = document.Objects.AddBrep(box, new ObjectAttributes { LayerIndex = layer });
            var lineId = document.Objects.AddLine(new Point3d(-3, 0, -2), new Point3d(3, 0, -2));
            document.Objects.AddPoint(new Point3d(0, 0, -2));
            var cpIndex = document.Objects.AddClippingPlane(Plane.WorldXY, 10, 10, Guid.Empty);
            var cp = (ClippingPlaneObject)document.Objects.FindId(cpIndex);
            var plane = cp.ClippingPlaneGeometry;
            plane.SetClipParticipation(layerScope ? Array.Empty<Guid>() : new[] { boxId }, layerScope ? new[] { layer } : Array.Empty<int>(), exclusion);
            plane.ParticipationListsEnabled = true;
            using var cache = new SceneCache();
            using var batch = BaselineTests.Solve(BaselineTests.Capture(document, cache), ViewRequest.Parse(cp, document));
            var view = Assert.Single(batch.Views); Healthy(view); output.WriteLine(view.Info);
            var id = lineId.ToString();
            // Excluded box stays whole in front and occludes. Included box is completely removed.
            Assert.InRange(Math.Abs(Length(view, LineClass.Visible, id) - (exclusion ? 4 : 6)), 0, .001);
            Assert.InRange(Math.Abs(Length(view, LineClass.Hidden, id) - (exclusion ? 2 : 0)), 0, .001);
            Assert.Equal(exclusion ? 1 : 0, view.HiddenPoints);
        }
    });

    [RhinoFact] public void FlippedPlaneKeepsTheOppositeWorldSideAndNamesResolve() => host.Run(() =>
    {
        using var document = RhinoDoc.CreateHeadless(null); document.ModelAbsoluteTolerance = .0001;
        var id = document.Objects.AddClippingPlane(new Plane(Point3d.Origin, -Vector3d.XAxis, Vector3d.YAxis), 10, 10, Guid.Empty);
        var cp = (ClippingPlaneObject)document.Objects.FindId(id); cp.Attributes.Name = "flipped"; cp.CommitChanges();
        document.Objects.AddPoint(new Point3d(1, 0, 1)); document.Objects.AddPoint(new Point3d(-1, 0, -1));
        using var cache = new SceneCache();
        using var batch = BaselineTests.Solve(BaselineTests.Capture(document, cache), ViewRequest.Parse("flipped", document));
        Healthy(batch.Views[0]); var item = Assert.Single(batch.Views[0].Items);
        using var geometry = item.CopyGeometry(); Assert.Equal(1, Assert.IsType<Point>(geometry).Location.X);
        Assert.True(batch.Views[0].Frame.Plane.Normal.Z < 0);
        // Independent native control verifies the retained side of Rhino's stored flipped plane.
        using var viewport = new ViewportInfo(); viewport.IsParallelProjection = true;
        viewport.SetCameraLocation(new Point3d(0, 0, -10)); viewport.SetCameraDirection(Vector3d.ZAxis); viewport.SetCameraUp(Vector3d.YAxis);
        viewport.SetFrustum(-5, 5, -5, 5, .001, 100);
        var parameters = new HiddenLineDrawingParameters { AbsoluteTolerance = .0001, Flatten = false, IncludeHiddenCurves = true };
        parameters.SetViewport(viewport); parameters.AddClippingPlane(cp.ClippingPlaneGeometry.Plane);
        using var kept = new LineCurve(new Point3d(1, 0, 1), new Point3d(2, 0, 1));
        using var removed = new LineCurve(new Point3d(-2, 0, -1), new Point3d(-1, 0, -1));
        parameters.AddGeometry(kept, "kept"); parameters.AddGeometry(removed, "removed");
        using var native = HiddenLineDrawing.Compute(parameters, false);
        var surviving = native.Segments.Where(s => s.SegmentVisibility == HiddenLineDrawingSegment.Visibility.Visible).ToArray();
        Assert.NotEmpty(surviving); Assert.All(surviving, s => Assert.Equal("kept", s.ParentCurve.SourceObject.Tag));
    });

    [RhinoFact] public void BoxUsesItsPositiveZFaceCropAndNegativeZDepthWhileNoLineKeepsOriginalFarCurves() => host.Run(() =>
    {
        using var document = RhinoDoc.CreateHeadless(null); document.ModelAbsoluteTolerance = .0001;
        using var solid = Brep.CreateFromBox(new BoundingBox(new Point3d(-1, -1, -1), new Point3d(1, 1, 1))); document.Objects.AddBrep(solid);
        var lineId = document.Objects.AddLine(new Point3d(3, 0, -.5), new Point3d(4, 0, -.5));
        var removed = document.Objects.AddPoint(new Point3d(3, 0, -.6));
        var box = new Box(new Plane(new Point3d(0, 0, -2), Vector3d.XAxis, Vector3d.YAxis),
            new Interval(-2, 5), new Interval(-2, 2), new Interval(1.5, 2));
        var request = ViewRequest.Parse(box); Assert.Equal(Point3d.Origin, request.Cut!.Plane.Origin); Assert.Equal(.5, request.Cut.SourceDepth);
        using var cache = new SceneCache(); using var batch = BaselineTests.Solve(BaselineTests.Capture(document, cache), request);
        var view = batch.Views[0]; Healthy(view); Assert.Equal(7, view.Frame.Width); Assert.Equal(4, view.Frame.Height);
        Assert.Single(view.Regions); Assert.Equal(4, view.Regions[0].Region.Area, 6);
        Assert.Equal(1, Length(view, LineClass.Visible, lineId.ToString()), 6);
        Assert.DoesNotContain(view.Items, p => p.SourceIdentity == removed.ToString() || p.Class == LineClass.FarClip);
        view.Regions[0].SourcePath[0] = 91; // Result metadata must not alias the cached source path.
        using var second = BaselineTests.Solve(BaselineTests.Capture(document, cache), request);
        Assert.Equal(new[] { 0 }, Assert.Single(second.Views[0].Regions).SourcePath);
    });

    [RhinoFact] public void ExcludedForegroundOverlappingSectionIsExplicitlyUnresolved() => host.Run(() =>
    {
        using var document = RhinoDoc.CreateHeadless(null); document.ModelAbsoluteTolerance = .0001;
        using var cutSolid = Brep.CreateFromBox(new BoundingBox(new Point3d(-1, -1, -1), new Point3d(1, 1, 1)));
        using var foreground = Brep.CreateFromBox(new BoundingBox(new Point3d(-.5, -.5, .2), new Point3d(.5, .5, .4)));
        var cutId = document.Objects.AddBrep(cutSolid); var wholeId = document.Objects.AddBrep(foreground);
        var request = ViewRequest.Parse(Plane.WorldXY);
        request = request with { Cut = request.Cut! with { ScopeEnabled = true, Objects = new[] { cutId } } };
        using var cache = new SceneCache(); using var batch = BaselineTests.Solve(BaselineTests.Capture(document, cache), request);
        var view = batch.Views[0]; Healthy(view); Assert.Empty(view.Regions);
        Assert.Contains(view.Items, p => p.SourceIdentity == cutId.ToString() && p.Class == LineClass.Unresolved);
        Assert.Contains(view.Items, p => p.SourceIdentity == wholeId.ToString() && p.Class == LineClass.Visible);
        Assert.Contains("region/Hatch withheld", view.Info);
    });

    [RhinoFact] public void FarLineUsesIndependentIntersectionAndCutWinsCoincidentIntervals() => host.Run(() =>
    {
        using var document = RhinoDoc.CreateHeadless(null); document.ModelAbsoluteTolerance = .0001;
        // Tapered square prism: far square is larger, so no projected overlap with the near square.
        using var bottom = new Rectangle3d(new Plane(new Point3d(0, 0, -3), Vector3d.ZAxis), new Interval(-2, 2), new Interval(-2, 2)).ToNurbsCurve();
        using var top = new Rectangle3d(new Plane(new Point3d(0, 0, 1), Vector3d.ZAxis), new Interval(-.5, .5), new Interval(-.5, .5)).ToNurbsCurve();
        var lofts = Brep.CreateFromLoft(new[] { bottom, top }, Point3d.Unset, Point3d.Unset, LoftType.Straight, false);
        try
        {
            using var solid = lofts[0].CapPlanarHoles(.0001); document.Objects.AddBrep(solid);
            using var cache = new SceneCache(); var request = ViewRequest.Parse(Plane.WorldXY);
            request = request with { Cut = request.Cut! with { SourceDepth = 2, FarClip = FarClipMode.Line } };
            using var batch = BaselineTests.Solve(BaselineTests.Capture(document, cache), request);
            var view = batch.Views[0]; Healthy(view); output.WriteLine(view.Info);
            Assert.InRange(Length(view, LineClass.FarClip), 12.99, 13.01);
            Assert.Single(view.Regions); Assert.NotNull(view.Regions[0].Region.Hatch);
        }
        finally { foreach (var loft in lofts) loft.Dispose(); }
        using var flat = RhinoDoc.CreateHeadless(null); flat.ModelAbsoluteTolerance = .0001;
        using var box = Brep.CreateFromBox(new BoundingBox(new Point3d(-1, -1, -3), new Point3d(1, 1, 1))); flat.Objects.AddBrep(box);
        using var otherCache = new SceneCache(); var boxView = ViewRequest.Parse(Plane.WorldXY);
        boxView = boxView with { Cut = boxView.Cut! with { SourceDepth = 2, FarClip = FarClipMode.Line } };
        using var coincident = BaselineTests.Solve(BaselineTests.Capture(flat, otherCache), boxView);
        Healthy(coincident.Views[0]); Assert.Equal(0, Length(coincident.Views[0], LineClass.FarClip));
    });

    [RhinoFact] public void ZeroAndThinDepthKeepSectionsWithoutConfidentBackground() => host.Run(() =>
    {
        using var document = RhinoDoc.CreateHeadless(null); document.ModelAbsoluteTolerance = .0001;
        using var box = Brep.CreateFromBox(new BoundingBox(new Point3d(-1, -1, -1), new Point3d(1, 1, 1))); document.Objects.AddBrep(box);
        foreach (var depth in new[] { 0d, .00005 })
        {
            using var cache = new SceneCache(); var request = ViewRequest.Parse(Plane.WorldXY);
            request = request with { Cut = request.Cut! with { SourceDepth = depth } };
            using var batch = BaselineTests.Solve(BaselineTests.Capture(document, cache), request); var view = batch.Views[0];
            Assert.Null(view.Error); Assert.DoesNotContain(view.Items, p => p.Class is LineClass.Visible or LineClass.Hidden or LineClass.FarClip);
            if (depth == 0) { Assert.Single(view.Regions); Assert.False(view.Report.Quarantined); }
            else
            {
                // Thin background depth is ambiguous, but the independently
                // certified square at the near plane is still exact material.
                var region = Assert.Single(view.Regions).Region;
                Assert.Equal(4,region.Area,8);Assert.Single(region.Loops);Assert.NotNull(region.Hatch);
                Assert.Equal(0, view.Report.UncertainCount);
                Assert.Contains("within tolerance", view.Info);
            }
        }
    });

    [RhinoFact] public void PartialOverlapSubtractionKeepsContinuationsAndHoleBoundaries() => host.Run(() =>
    {
        using var longLine = new LineCurve(new Point3d(-3, 0, 0), new Point3d(3, 0, 0));
        using var shortLine = new LineCurve(new Point3d(-1, 0, 0), new Point3d(1, 0, 0));
        var pieces = SectionDrawing.Subtract(longLine, new[] { shortLine }, .0001).ToArray();
        try { Assert.Equal(2, pieces.Length); Assert.Equal(4, pieces.Sum(c => c.GetLength()), 6); }
        finally { foreach (var piece in pieces) piece.Dispose(); }
    });

    [RhinoFact] public void ExtractedThreeSourceNativeClippingCaseHasOnlyBenignEmptySlots() => host.Run(() =>
    {
        using var file = global::Rhino.FileIO.File3dm.Read(Fixture("sections/unset-native-slot"));
        using var viewport = new ViewportInfo(); viewport.IsParallelProjection = true;
        viewport.SetCameraLocation(new Point3d(9.053553390593274, -46.32466994868157, 3.225));
        viewport.SetCameraDirection(Vector3d.YAxis); viewport.SetCameraUp(Vector3d.ZAxis);
        const double distance = 50.349669948681566;
        viewport.SetFrustum(-distance, distance, -distance, distance, .001, distance * 4);
        var parameters = new HiddenLineDrawingParameters { AbsoluteTolerance = .0001, Flatten = false, IncludeHiddenCurves = true };
        parameters.SetViewport(viewport); parameters.AddClippingPlane(new Plane(new Point3d(9, 6, 0), -Vector3d.YAxis));
        Assert.Equal(3, file.Objects.Count);
        foreach (var obj in file.Objects) Assert.True(parameters.AddGeometry(obj.Geometry, obj.Attributes.Name));
        using var drawing = HiddenLineDrawing.Compute(parameters, false);
        var unset = drawing.Segments.Where(s => s.SegmentVisibility == HiddenLineDrawingSegment.Visibility.Unset).ToArray();
        Assert.Equal(4, unset.Length);
        Assert.All(unset, s => Assert.Equal(ExtractionKind.EmptySlot, NativeExtractionDiagnostics.Inspect(s, _ => null, Transform.Identity).Kind));
        // Route C over the same three extracted sources must retain its independent world-space extents.
        using var doc = RhinoDoc.CreateHeadless(null); doc.ModelAbsoluteTolerance = .0001;
        var inputs = file.Objects.Select((o, i) => new CaptureInput(o.Geometry, new[] { i }, "extracted/" + i)).ToArray();
        using var cache = new SceneCache();
        using var batch = BaselineTests.Solve(BaselineTests.Capture(doc, cache, inputs), ViewRequest.Parse(new Plane(new Point3d(9, 6, 0), Vector3d.XAxis, Vector3d.ZAxis)));
        var view = batch.Views[0]; Healthy(view); Assert.DoesNotContain(view.Items, p => p.Class == LineClass.Unresolved);
        var bounds = BoundingBox.Empty;
        foreach (var item in view.Items) { using var geometry = item.CopyGeometry(); bounds.Union(geometry.GetBoundingBox(true)); }
        Assert.Equal(6, bounds.Min.X, 4); Assert.Equal(6.38, bounds.Max.X, 4);
        Assert.Equal(3, bounds.Min.Z, 4); Assert.Equal(6, bounds.Max.Z, 4);
    });

    [RhinoFact] public void HostStyleModesDefinitionsSentinelsAndNestedParentScopeAreSnapshots() => host.Run(() =>
    {
        using var document = RhinoDoc.CreateHeadless(null); document.ModelAbsoluteTolerance = .0001;
        using var pattern = new HatchPattern { Name = "Wiring-lines", FillType = HatchPatternFillType.Lines };
        using var hatchLine = new HatchLine { Angle = .2, BasePoint = new Point2d(.1, .2), Offset = new Vector2d(0, .1) };
        hatchLine.SetDashes(new[] { .2, -.1 }); pattern.AddHatchLine(hatchLine);
        var index = document.HatchPatterns.Add(pattern);
        using var style = new SectionStyle { HatchIndex = index, HatchScale = 2, HatchRotationRadians = .37,
            HatchPatternColor = System.Drawing.Color.FromArgb(0, 255, 255, 255) };
        var layerIndex = document.Layers.Add("styled", System.Drawing.Color.CornflowerBlue);
        document.Layers[layerIndex].SetCustomSectionStyle(style);
        using var box = Brep.CreateFromBox(new BoundingBox(new Point3d(-1, -1, -1), new Point3d(1, 1, 1)));
        var layerId = document.Objects.AddBrep(box, new ObjectAttributes { LayerIndex = layerIndex });
        var layerSnapshot = SectionCapture.Capture(document, document.Objects.FindId(layerId), null);
        Assert.Equal(2, layerSnapshot.Style.Scale); Assert.Equal(0x00ffffffu, layerSnapshot.Style.PatternColour);
        Assert.Equal(.37, layerSnapshot.Style.Rotation);
        var definition = layerSnapshot.Style.Pattern!; Assert.False(definition.Solid);
        Assert.Equal(new[] { .2, -.1 }, Assert.Single(definition.Lines).Dashes);
        Assert.Equal(unchecked((uint)System.Drawing.Color.CornflowerBlue.ToArgb()), layerSnapshot.Style.ObjectColour);
        using var custom = new ObjectAttributes { SectionAttributesSource = ObjectSectionAttributesSource.FromObject };
        style.HatchScale = 3; custom.SetCustomSectionStyle(style);
        var customId = document.Objects.AddBrep(box, custom);
        Assert.Equal(3, SectionCapture.Capture(document, document.Objects.FindId(customId), null).Style.Scale);
        using var leaf = new ObjectAttributes { SectionAttributesSource = ObjectSectionAttributesSource.FromParent, ColorSource = ObjectColorSource.ColorFromParent };
        var def = document.InstanceDefinitions.Add("parent-leaf", "", Point3d.Origin, new[] { box }, new[] { leaf });
        using var nested = new InstanceReferenceGeometry(document.InstanceDefinitions[def].Id, Transform.Identity);
        var middle = document.InstanceDefinitions.Add("parent-middle", "", Point3d.Origin, new[] { nested }, new[] { leaf });
        using var rootAttributes = new ObjectAttributes { SectionAttributesSource = ObjectSectionAttributesSource.FromObject, ColorSource = ObjectColorSource.ColorFromObject, ObjectColor = System.Drawing.Color.Goldenrod };
        style.HatchScale = 5; rootAttributes.SetCustomSectionStyle(style);
        var root = document.Objects.AddInstanceObject(middle, Transform.Translation(4, 0, 0), rootAttributes);
        using var cache = new SceneCache();
        using var scene = BaselineTests.Capture(document, cache, new[] { new CaptureInput(root, new[] { 7 }, "nested") });
        var source = Assert.Single(scene.Value.Prepare(default)); Assert.Equal(5, source.Section.Style.Scale);
        Assert.Equal(unchecked((uint)System.Drawing.Color.Goldenrod.ToArgb()), source.Section.Style.ObjectColour);
        Assert.Contains(root, source.Section.ObjectIds); Assert.Equal(3, source.Section.ObjectIds.Length);
        var request = ViewRequest.Parse(Plane.WorldXY);
        Assert.True((request.Cut! with { ScopeEnabled = true, Objects = new[] { root } }).Participates(source));
        Assert.False((request.Cut! with { ScopeEnabled = true, Exclusion = true, Objects = new[] { root } }).Participates(source));
        // Pattern edits do not mutate snapshots, and they invalidate the host witness.
        using var watch = new DocumentWatch(document); var generation = watch.Generation;
        using var changed = new HatchPattern { Name = "Changed-lines", FillType = HatchPatternFillType.Lines }; changed.AddHatchLine(hatchLine);
        document.HatchPatterns.Modify(changed, index, true); watch.VerifyHostState(); Assert.True(watch.Generation > generation);
        Assert.Equal("Wiring-lines", definition.Name);
        using var selected = new SectionStyle { HatchIndex = index, HatchScale = 4 };
        var cpId = document.Objects.AddClippingPlane(Plane.WorldXY, 10, 10, Guid.Empty);
        var cp = (ClippingPlaneObject)document.Objects.FindId(cpId);
        cp.Attributes.SectionAttributesSource = ObjectSectionAttributesSource.FromObject; cp.Attributes.SetCustomSectionStyle(selected); cp.CommitChanges();
        var sectionerRequest = ViewRequest.Parse(cp, document).Cut!;
        var fromSectioner = source with { Section = source.Section with { FromSectioner = true } };
        Assert.Equal(4, sectionerRequest.Style(fromSectioner).Scale);
        Assert.Equal(5, sectionerRequest.Style(source).Scale);
        using var noFill = new SectionStyle { HatchIndex = -1, BackgroundFillMode = SectionBackgroundFillMode.None };
        Assert.True(SectionCapture.Snapshot(document, noFill, 0xff123456, 0xff654321).HasStyle);
        Assert.False(SectionCapture.Snapshot(document, null, 0xff123456, 0xff654321).HasStyle);
    });

    [RhinoFact] public void CoincidentBlockUnsetIsInvestigatedAndConservativelyScoped() => host.Run(() =>
    {
        using var document = RhinoDoc.OpenHeadless(Fixture("block_instance")); using var cache = new SceneCache();
        var lease = BaselineTests.Capture(document, cache);
        using var batch = NativeDrawing.SolveAsync(lease, new(lease.Value.Revision, "SW", "native", 1), new[] { ViewRequest.Parse("SW") }, new(), default).GetAwaiter().GetResult();
        var view = batch.Views[0]; output.WriteLine(view.Info);
        Healthy(view); Assert.NotEmpty(view.Items);
        Assert.All(view.Items.Where(p => p.Class == LineClass.Unresolved), p => Assert.False(string.IsNullOrEmpty(p.SourceIdentity)));
        if(view.Report.InvalidExtractionCount>0) Assert.Contains(view.Diagnostics,d=>d.Contains("Unset",StringComparison.Ordinal));
    });

    [RhinoTheory]
    [InlineData("rhs_hollow", false, 1, 2, 2.04)]
    [InlineData("chs_hollow", false, 1, 2, .51 * Math.PI)]
    [InlineData("rhs_hollow", true, 2, 2, .6)]
    [InlineData("column_through_slab", false, 2, 2, 16.16)]
    [InlineData("surface_planar_open", false, 1, 1, 0)]
    public void ActualComponentPublishesRegionTreesAndNullHatches(string fixture, bool crop, int regions, int loops, double area) => host.Run(() =>
    {
        Assert.True(global::Rhino.PlugIns.PlugIn.LoadPlugIn(new Guid("b45a29b1-4343-4035-989e-044e8580d9cf")));
        using var rhino = RhinoDoc.OpenHeadless(Fixture(fixture)); rhino.ModelAbsoluteTolerance = .0001; RhinoDoc.ActiveDoc = rhino;
        using var document = new GH_Document { Enabled = true };
        var assembly = Assembly.LoadFrom(Path.Combine(AppContext.BaseDirectory, "M2DPlus.Grasshopper.gha"));
        var component = (GH_Component)Activator.CreateInstance(assembly.GetType("M2DPlus.Grasshopper.DrawingComponent", true)!)!;
        document.AddObject(component, false);
        var view = (Param_GenericObject)component.Params.Input[1];
        if (crop) view.PersistentData.Append(new GH_Rectangle(new Rectangle3d(Plane.WorldXY, new Interval(-.5, .5), new Interval(-2, 2))));
        else view.PersistentData.Append(new GH_Plane(Plane.WorldXY));
        try
        {
            document.NewSolution(true, GH_SolutionMode.Silent);
            GrasshopperTests.PumpUntil(() => component.Params.Output[8].VolatileDataCount == 1, () => component.Message + ": " + string.Join(";", component.RuntimeMessages(GH_RuntimeMessageLevel.Error)),
                () => { if (document.ScheduleDelay >= 0) document.NewSolution(false, GH_SolutionMode.Silent); });
            var info = ((GH_String)Assert.Single(component.Params.Output[8].VolatileData.AllData(true))).Value; output.WriteLine(info);
            Assert.DoesNotContain("quarantined=True", info);
            var cut = component.Params.Output[4].VolatileData; var hatch = component.Params.Output[5].VolatileData;
            Assert.Equal(regions, cut.PathCount); Assert.Equal(loops, cut.DataCount); Assert.Equal(cut.Paths, hatch.Paths);
            Assert.All(cut.Paths, path => { Assert.Equal(3, path.Length); Assert.Single(hatch.get_Branch(path).Cast<object?>()); });
            if (area == 0) Assert.Null(Assert.Single(hatch.AllData(false)));
            else
            {
                var hatches = hatch.AllData(true).Cast<GH_Hatch>().ToArray(); Assert.Equal(regions, hatches.Length);
                var actual = hatches.Sum(h => { using var mass = AreaMassProperties.Compute(h.Value); return mass.Area; });
                Assert.InRange(Math.Abs(actual - area), 0, .00001);
                Assert.All(hatches, h => Assert.NotNull(h.Value.GetUserString("M2D.SectionStyle")));
            }
        }
        finally { component.RemovedFromDocument(document); SceneCache.Shared.Clear(); }
    });
}
