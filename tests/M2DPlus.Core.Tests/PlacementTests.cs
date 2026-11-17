using M2DPlus.Core.Placement;
using Xunit;

namespace M2DPlus.Core.Tests;

public sealed class PlacementTests
{
    private static readonly Plane3 Source = new(new(12, 6, 7), Vector3.UnitY, Vector3.UnitZ);
    private static readonly Plane3 Target = new(new(100, 30, 4), -Vector3.UnitX, Vector3.UnitZ);
    private static void Near(Vector3 a, Vector3 b)
    { Assert.Equal(a.X, b.X, 10); Assert.Equal(a.Y, b.Y, 10); Assert.Equal(a.Z, b.Z, 10); }

    [Fact]
    public void RectangleAndPlaneKeepOwnOriginBoxMovesToTopAndClippingFlipIsRightHanded()
    {
        Assert.Equal(Source, AnchorRules.Resolve(ViewSourceKind.Rectangle, Source));
        Assert.Equal(Source, AnchorRules.Resolve(ViewSourceKind.Plane, Source));
        Near(Source.At(0, 0, 8), AnchorRules.Resolve(ViewSourceKind.Box, Source, 8).Origin);
        var flipped = AnchorRules.Resolve(ViewSourceKind.ClippingPlane, Source, flipClippingPlane: true);
        Near(-Source.X, flipped.X); Near(Source.Y, flipped.Y); Near(-Source.Z, flipped.Z);
        Assert.Equal(1, flipped.X.Cross(flipped.Y).Dot(flipped.Z), 12);
    }

    [Fact]
    public void NamedViewUsesCameraTargetWhileDirectionsProjectWorldOrigin()
    {
        var cameraTarget = new Vector3(5, 6, 7);
        Near(cameraTarget, AnchorRules.Resolve(ViewSourceKind.NamedView, Source, cameraTarget: cameraTarget).Origin);
        foreach (var kind in new[] { ViewSourceKind.Vector, ViewSourceKind.Direction })
        {
            var anchor = AnchorRules.Resolve(kind, Source);
            Near(Source.Project(Vector3.Zero), anchor.Origin); Near(Source.X, anchor.X); Near(Source.Y, anchor.Y);
        }
    }

    [Fact]
    public void NamedViewCameraReferenceProjectsToFrontForInPlaceAndTargetPlacement()
    {
        var front = Plane3.WorldXY.AtOrigin(new(0, 0, 20));
        var anchor = AnchorRules.Resolve(ViewSourceKind.NamedView, front, cameraTarget: new Vector3(5, 7, 3));
        var view = new PlacementView(anchor, new(-10, -6, 10, 6), front);
        var inPlace = PlacementPlan.Create([view], []).Views[0];
        Near(new(5, 7, 20), inPlace.Frame.Plane.Origin);
        Near(new(6, 8, 20), inPlace.Transform.Map(new(6, 8, 20)));
        var placed = PlacementPlan.Create([view], [Target]).Views[0];
        Near(Target.Origin, placed.Transform.Map(new(5, 7, 20)));
        Near(Target.At(1, 1), placed.Transform.Map(new(6, 8, 20)));
    }

    [Fact]
    public void FrameInferenceProjectsInputBoundsAndNeverRefitsToOutput()
    {
        var frame = Frame2.FromInputBounds(Source, [Source.At(-2, -4, 0), Source.At(8, 6, 10), Source.At(2, 3, -5)]);
        Assert.Equal(new Frame2(-2, -4, 8, 6), frame);
        Assert.Throws<ArgumentException>(() => Frame2.FromInputBounds(Source, []));
        Near(Vector3.UnitX, new Vector3(1e-300, 0, 0).Unit());
        Near(Vector3.UnitX, new Vector3(1e300, 0, 0).Unit());
    }

    [Fact]
    public void DirectionUpUsesNorthForVerticalAndWorldZForElevation()
    {
        var top = AnchorRules.FromDirection(-Vector3.UnitZ, new(5, 7, 12));
        Near(new(0, 0, 12), top.Origin); Near(Vector3.UnitY, top.Y); Near(Vector3.UnitX, top.X);
        var bottom = AnchorRules.FromDirection(Vector3.UnitZ, new(1, 2, 3));
        Near(Vector3.UnitY, bottom.Y); Near(-Vector3.UnitX, bottom.X);
        var front = AnchorRules.FromDirection(Vector3.UnitY, new(0, -20, 0));
        Near(Vector3.UnitZ, front.Y); Near(new(0, -20, 0), front.Origin);
    }

    [Fact]
    public void RigidMapPreservesModelDistancesLocalCoordinatesAndInverse()
    {
        var move = new RigidPlacement(Source, Target);
        var a = Source.At(4, -6, 2); var b = Source.At(9, 1, -3);
        Near(Target.At(4, -6, 2), move.Map(a)); Near(a, move.Inverse().Map(move.Map(a)));
        Assert.Equal((b - a).Length, (move.Map(b) - move.Map(a)).Length, 10);
        Near(Target.X, move.MapVector(Source.X)); Near(Target.Z, move.MapVector(Source.Z));
    }

    [Fact]
    public void OneToOneTargetsAndInPlaceUseExactInputFrames()
    {
        var frame = new Frame2(-3, -2, 7, 8);
        var views = new[] { new PlacementView(Source, frame), new PlacementView(Target, frame) };
        var inPlace = PlacementPlan.Create(views, []);
        Assert.True(inPlace.Success); Near(Source.At(2, 3), inPlace.Views[0].Transform.Map(Source.At(2, 3)));
        var placed = PlacementPlan.Create(views, [Target, Plane3.WorldXY]);
        Near(Target.Origin, placed.Views[0].Transform.Map(Source.Origin));
        Near(Vector3.Zero, placed.Views[1].Transform.Map(Target.Origin));
        Assert.Same(frame, placed.Views[0].Frame.Bounds); Near(Target.At(-3, -2), placed.Views[0].Frame.Corners[0]);
    }

    [Fact]
    public void OneTargetRowUsesMeanFrameWidthAndDoesNotShiftY()
    {
        var views = new[] { new PlacementView(Source, new(-4, -2, 6, 8)),
            new PlacementView(Source, new(-10, 4, 20, 6)), new PlacementView(Source, new(3, -5, 23, 9)) };
        var row = PlacementPlan.Create(views, [Target]);
        Assert.True(row.Success);
        // Mean width = 20, gap = 2. First anchor stays at target origin even with a negative frame minimum.
        Near(Target.Origin, row.Views[0].Transform.Target.Origin);
        Near(Target.At(18, 0), row.Views[1].Transform.Target.Origin);
        Near(Target.At(37, 0), row.Views[2].Transform.Target.Origin);
        for (var i = 1; i < views.Length; i++)
        {
            var previousMax = Target.Coordinates(row.Views[i - 1].Frame.Corners[1]).X;
            var nextMin = Target.Coordinates(row.Views[i].Frame.Corners[0]).X;
            Assert.Equal(2, nextMin - previousMax, 10);
            Assert.Equal(0, Target.Coordinates(row.Views[i].Transform.Target.Origin).Y, 10);
        }
    }

    [Fact]
    public void ExplicitGapsAndScaleSettingsCannotChangeRigidPlacement()
    {
        var view = new PlacementView(Source, new(0, 0, 10, 5));
        var row = PlacementPlan.Create([view, view, view], [Target], [DistanceSetting.Auto, DistanceSetting.At(3), DistanceSetting.At(0)]);
        Near(Target.At(13, 0), row.Views[1].Transform.Target.Origin);
        Near(Target.At(23, 0), row.Views[2].Transform.Target.Origin);
        Assert.Equal(10, (row.Views[1].Transform.Map(Source.At(10, 0)) - row.Views[1].Transform.Map(Source.Origin)).Length, 10);
    }

    [Fact]
    public void SurfaceFrameKeepsVUpAndReversesUWithTheActualFaceNormal()
    {
        var centre = new Vector3(9, 4, 2);
        var normal = Vector3.UnitY.Cross(Vector3.UnitZ);
        var forward = AnchorRules.FromSurface(centre, Vector3.UnitY * 3, Vector3.UnitZ * 7, normal);
        var reverse = AnchorRules.FromSurface(centre, Vector3.UnitY * 3, Vector3.UnitZ * 7, -normal);
        Near(centre, forward.Origin); Near(centre, reverse.Origin);
        Near(Vector3.UnitY, forward.X); Near(-Vector3.UnitY, reverse.X);
        Near(Vector3.UnitZ, forward.Y); Near(forward.Y, reverse.Y);
        Near(normal, forward.Z); Near(-normal, reverse.Z);
        Assert.Equal(1, reverse.X.Cross(reverse.Y).Dot(reverse.Z), 12);
    }

    [Fact]
    public void SlightlySkewedSurfaceUsesTheBoundaryEdgeNearestUAndKeepsVUp()
    {
        var skewV = new Vector3(Math.Tan(Math.PI / 18000), 1, 0);
        var edges = new[] { -Vector3.UnitX, skewV, Vector3.UnitX, -skewV };
        foreach (var sign in new[] { 1, -1 })
        {
            var plane = AnchorRules.FromSurface(Vector3.Zero, Vector3.UnitX, skewV, Vector3.UnitZ * sign, edges);
            Near(Vector3.UnitX * sign, plane.X); Near(Vector3.UnitY, plane.Y); Near(Vector3.UnitZ * sign, plane.Z);
            Assert.True(plane.Y.Dot(skewV) > 0);
        }
        // The parameter tangent can be oblique to the outline, too; the boundary edge determines the frame.
        var oblique = AnchorRules.FromSurface(Vector3.Zero, new(1, .1, 0), Vector3.UnitY, Vector3.UnitZ, edges);
        Near(Vector3.UnitX, oblique.X);
    }

    [Theory]
    [InlineData(2, 3, true)]
    [InlineData(2, 3, false)]
    [InlineData(3, 2, true)]
    [InlineData(3, 2, false)]
    public void SourceCountMismatchReturnsTheExactMessageAndNoPartialPlacement(int sourceCount, int viewCount, bool withTarget)
    {
        var view = new PlacementView(Source, new(-2, -4, 5, 6));
        var result = PlacementPlan.Create(Enumerable.Repeat(view, viewCount).ToArray(), withTarget ? [Target] : [],
            sources: Enumerable.Repeat(Source, sourceCount).ToArray());
        Assert.False(result.Success); Assert.Empty(result.Views);
        Assert.Equal("Give 1 source, or one per view", result.Error);
    }

    [Fact]
    public void ExplicitSourceMapsAsGivenWithoutFlatteningTheDrawingOrFrame()
    {
        var view = new PlacementView(Plane3.WorldXY.AtOrigin(new(2, 3, 8)), new(-2, -1, 7, 6));
        var placed = PlacementPlan.Create([view], [Target], sources: [Source]).Views[0];
        var orient = new RigidPlacement(Source, Target);
        Near(Target.Origin, placed.Transform.Map(Source.Origin));
        Near(Target.At(2, 3, 4), placed.Transform.Map(Source.At(2, 3, 4)));
        var corners = new PlacedFrame(view.DrawingFrame, view.Frame).Corners;
        for (var i = 0; i < corners.Length; i++) Near(orient.Map(corners[i]), placed.Frame.Corners[i]);
        Near(orient.MapVector(view.DrawingFrame.Y), placed.Frame.Plane.Y);
    }

    [Fact]
    public void SourceCanBeSharedOrMatchedPerView()
    {
        var views = new[] { new PlacementView(Plane3.WorldXY, new(-1, -2, 4, 5)), new PlacementView(Target, new(0, 0, 8, 3)) };
        var shared = PlacementPlan.Create(views, [Target, Plane3.WorldXY], sources: [Source]);
        var matched = PlacementPlan.Create(views, [Target, Plane3.WorldXY], sources: [Source, Target]);
        Near(Vector3.Zero, shared.Views[1].Transform.Map(Source.Origin));
        Near(Vector3.Zero, matched.Views[1].Transform.Map(Target.Origin));
        Assert.Equal(shared.Views[0], matched.Views[0]);
    }

    [Fact]
    public void SourceRowUsesTransformedFrameWidthsAndKeepsSourceYAndDepth()
    {
        var views = new[] { new PlacementView(Plane3.WorldXY, new(-1, -2, 4, 5)),
            new PlacementView(Plane3.WorldXY.AtOrigin(new(20, 10, 7)), new(-3, -4, 8, 6)) };
        var sources = new[] { new Plane3(new(3, 9, 2), Vector3.UnitY, -Vector3.UnitX), Plane3.WorldXY.AtOrigin(new(10, 5, 3)) };
        var result = PlacementPlan.Create(views, [Target], sources: sources);
        var bounds = result.Views.Select(v => Frame2.FromInputBounds(Target, v.Frame.Corners)).ToArray();
        // First frame rotates 90 degrees in Source coordinates: widths 7 and 11, hence auto gap = .9.
        Assert.Equal(.9, bounds[1].MinX - bounds[0].MaxX, 10);
        for (var i = 0; i < views.Length; i++)
        {
            var p = sources[i].At(2, 6, 4);
            Near(result.Views[i].Transform.Target.At(2, 6, 4), result.Views[i].Transform.Map(p));
        }
        Near(Target.Origin, result.Views[0].Transform.Map(sources[0].Origin));
    }

    [Fact]
    public void SourceWithoutTargetIsIgnoredWithARemarkAndCountErrorsAreExplicit()
    {
        var view = new PlacementView(Source, new(-2, -4, 5, 6));
        var result = PlacementPlan.Create([view], [], sources: [Target]);
        Near(Source.At(3, 4, 5), result.Views[0].Transform.Map(Source.At(3, 4, 5)));
        Assert.Equal("Source is given without Target; the drawing stays in place.", result.Remark);
        foreach (var targets in new Plane3[][] { [], [Target] })
            Assert.Equal("Give 1 source, or one per view", PlacementPlan.Create([view, view, view], targets, sources: [Source, Target]).Error);
    }

    [Fact]
    public void MismatchedTargetsAndInvalidGeometryAreExplicitErrors()
    {
        var view = new PlacementView(Source, new(0, 0, 10, 5));
        Assert.Equal("Give 1 target, or one per view", PlacementPlan.Create([view, view, view], [Target, Target]).Error);
        Assert.Empty(PlacementPlan.Create([], []).Views);
        Assert.Throws<ArgumentException>(() => new Plane3(Vector3.Zero, Vector3.UnitX, Vector3.UnitX));
        Assert.Throws<ArgumentException>(() => new Frame2(5, 0, 2, 3));
        Assert.Throws<ArgumentException>(() => AnchorRules.FromDirection(Vector3.Zero, Vector3.Zero));
    }
}
