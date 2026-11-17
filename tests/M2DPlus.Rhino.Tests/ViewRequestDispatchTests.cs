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
    [InlineData(true, 0)]
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

    [RhinoTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void RawScopedClippingSurfaceRequiresItsDocumentAndCapturesParticipation(bool exclusion) => host.Run(() =>
    {
        using var document = RhinoDoc.CreateHeadless(null);
        var layer = document.Layers.Add("Scoped", System.Drawing.Color.DarkRed);
        var objectId = document.Objects.AddPoint(Point3d.Origin);
        using var surface = new ClippingPlaneSurface(CutPlane);
        surface.SetClipParticipation([objectId], [layer], exclusion);
        surface.ParticipationListsEnabled = true;

        var missingDocument = ViewRequest.Parse(surface);
        Assert.Equal("Scoped clipping plane requires its source document.", missingDocument.Error);
        Assert.Null(missingDocument.Cut);

        var cut = AssertClipping(ViewRequest.Parse(surface, document));
        Assert.True(cut.ScopeEnabled);
        Assert.Equal(exclusion, cut.Exclusion);
        Assert.Equal(objectId, Assert.Single(cut.Objects));
        Assert.Equal(document.Layers[layer].Id, Assert.Single(cut.Layers));
    });

    [RhinoFact] public void ClippingObjectGuidAndNameKeepDepthScopeAndSectionStyle() => host.Run(() =>
    {
        using var document = RhinoDoc.CreateHeadless(null);
        var layer = document.Layers.Add("Scoped", System.Drawing.Color.DarkRed);
        var objectId = document.Objects.AddPoint(Point3d.Origin);
        var id = document.Objects.AddClippingPlane(CutPlane, 2, 4, Guid.Empty);
        var obj = Assert.IsType<ClippingPlaneObject>(document.Objects.FindId(id));
        obj.Attributes.Name = "Section";
        using var style = new SectionStyle { HatchScale = 4 };
        obj.Attributes.SectionAttributesSource = ObjectSectionAttributesSource.FromObject;
        obj.Attributes.SetCustomSectionStyle(style);
        Assert.True(obj.CommitChanges());
        var surface = obj.ClippingPlaneGeometry;
        surface.PlaneDepth = 6;
        surface.PlaneDepthEnabled = true;
        surface.SetClipParticipation([objectId], [layer], true);
        surface.ParticipationListsEnabled = true;
        Assert.True(obj.CommitChanges());

        // An object can infer its document; GUID and name references resolve through the supplied document.
        var requests = new[] { ViewRequest.Parse(obj), ViewRequest.Parse(id, document), ViewRequest.Parse("Section", document) };
        Assert.Equal(id, requests[0].ObjectId);
        Assert.Equal(id, requests[1].ObjectId);
        foreach (var request in requests)
        {
            var cut = AssertClipping(request);
            Assert.Equal("Section", request.Name);
            Assert.Equal(6, cut.SourceDepth);
            Assert.True(cut.ScopeEnabled);
            Assert.True(cut.Exclusion);
            Assert.Equal(objectId, Assert.Single(cut.Objects));
            Assert.Equal(document.Layers[layer].Id, Assert.Single(cut.Layers));
            var capturedStyle = Assert.IsType<M2DPlus.Core.Sections.SectionStyleSnapshot>(cut.SectionerStyle);
            Assert.True(capturedStyle.HasStyle);
            Assert.Equal(4, capturedStyle.Scale);
        }
    });
}
