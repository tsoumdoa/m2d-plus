using M2DPlus.Core;
using M2DPlus.Rhino.Services;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using Xunit;

namespace M2DPlus.Rhino.Tests;

[Collection("Rhino")]
[Trait("Category", "RhinoIntegration")]
public sealed class ViewRequestDispatchTests(RhinoHost host)
{
    private static Plane CutPlane => new(new Point3d(3, 4, 5), Vector3d.YAxis, -Vector3d.ZAxis);

    private static CutFrame AssertClipping(ViewRequest request)
    {
        Assert.Null(request.Error);
        Assert.Equal(ViewSourceKind.ClippingPlane, request.Kind);
        Assert.Equal(-CutPlane.ZAxis, request.Direction);
        var cut = Assert.IsType<CutFrame>(request.Cut);
        Assert.Equal(CutPlane, cut.Plane);
        Assert.Null(cut.Crop);
        return cut;
    }

    [RhinoTheory]
    [InlineData(false, 7.5)]
    [InlineData(true, 7.5)]
    public void RawClippingSurfaceKeepsItsPlaneAndOptionalDepth(bool depthEnabled, double depth) => host.Run(() =>
    {
        using var display = new PlaneSurface(CutPlane, new Interval(-1, 1), new Interval(-2, 2));
        using var surface = new ClippingPlaneSurface(display) { PlaneDepth = depth, PlaneDepthEnabled = depthEnabled };
        // The scene lies opposite the clipping plane's look direction. Rectangle handling would face the surface
        // toward it and crop to the display rectangle; clipping geometry must keep its own plane and no crop.
        var scene = new BoundingBox(new Point3d(-20, -10, -10), new Point3d(-10, 10, 10));
        var request = ViewRequest.Parse(surface, scene: scene);
        var cut = AssertClipping(request);
        Assert.Equal("Clipping plane", request.Name);
        Assert.Equal(Guid.Empty, request.ObjectId);
        Assert.Equal(depthEnabled ? (double?)depth : null, cut.SourceDepth);
        Assert.False(cut.ScopeEnabled);
        Assert.Empty(cut.Objects);
        Assert.Empty(cut.Layers);
        Assert.Null(cut.SectionerStyle);
    });

    [RhinoFact]
    public void RawScopedClippingSurfaceRequiresItsDocumentAndCapturesParticipation() => host.Run(() =>
    {
        using var document = RhinoDoc.CreateHeadless(null);
        var layer = document.Layers.Add("Scoped", System.Drawing.Color.DarkRed);
        var objectId = document.Objects.AddPoint(Point3d.Origin);
        using var surface = new ClippingPlaneSurface(CutPlane);
        surface.SetClipParticipation([objectId], [layer], true);
        surface.ParticipationListsEnabled = true;

        var missingDocument = ViewRequest.Parse(surface);
        Assert.Equal("Scoped clipping plane requires its source document.", missingDocument.Error);
        Assert.Null(missingDocument.Cut);

        var cut = AssertClipping(ViewRequest.Parse(surface, document));
        Assert.True(cut.ScopeEnabled);
        Assert.True(cut.Exclusion);
        Assert.Equal(objectId, Assert.Single(cut.Objects));
        Assert.Equal(document.Layers[layer].Id, Assert.Single(cut.Layers));

        using var otherDocument = RhinoDoc.CreateHeadless(null);
        var unresolved = ViewRequest.Parse(surface, otherDocument);
        Assert.Equal("Clipping-plane layer scope does not resolve in the source document.", unresolved.Error);
        Assert.Null(unresolved.Cut);
        surface.ParticipationListsEnabled = false;
        Assert.Null(ViewRequest.Parse(surface).Error); // Inactive scope does not need a document.
    });

    [RhinoFact] public void ClippingGuidKeepsItsDepthAndHasNoCrop() => host.Run(() =>
    {
        using var document = RhinoDoc.CreateHeadless(null);
        var id = document.Objects.AddClippingPlane(CutPlane, 2, 4, Guid.Empty);
        var obj = Assert.IsType<ClippingPlaneObject>(document.Objects.FindId(id));
        var surface = obj.ClippingPlaneGeometry;
        surface.PlaneDepth = 6;
        surface.PlaneDepthEnabled = true;
        Assert.True(obj.CommitChanges());
        var request = ViewRequest.Parse(id, document);
        Assert.Equal(id, request.ObjectId);
        Assert.Equal(6, AssertClipping(request).SourceDepth);
    });
}
