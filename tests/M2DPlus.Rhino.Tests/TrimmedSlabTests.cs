using M2DPlus.Core.Sections;
using M2DPlus.Rhino.Sections;
using Rhino.Geometry;
using Xunit;

namespace M2DPlus.Rhino.Tests;

[Collection("Rhino")]
[Trait("Category", "RhinoIntegration")]
public sealed class TrimmedSlabTests(RhinoHost host)
{
    private const double Tol = .0001;

    // An exact polynomial graph z=x²-y² over [-2,2]×[-1.5,1.5],
    // with a rectangular through-hole [-.4,.4]×[-.6,.6] and bottom z=-10.
    // Its untrimmed top is tangent to z=0 at (0,0), inside the removed hole.
    private static Brep Saddle(bool hole, bool touchesTangency = false)
    {
        using var surface = NurbsSurface.Create(3, false, 3, 3, 3, 3);
        surface.KnotsU.CreateUniformKnots(1); surface.KnotsV.CreateUniformKnots(1);
        var x = new[] { -2d, 0, 2 }; var y = new[] { -1.5, 0, 1.5 };
        var xx = new[] { 4d, -4, 4 }; var yy = new[] { 2.25, -2.25, 2.25 };
        for (var i = 0; i < 3; i++) for (var j = 0; j < 3; j++)
            surface.Points.SetPoint(i, j, new Point3d(x[i], y[j], xx[i] - yy[j]));
        using var whole = surface.ToBrep();
        Brep top;
        if (hole)
        {
            var firstU = touchesTangency ? .5 : .4;
            var rim = new List<Curve>();
            foreach (var v in new[] { .3, .7 })
            { using var iso = surface.IsoCurve(0, v); rim.Add(iso.Trim(firstU, .6)!); }
            foreach (var u in new[] { firstU, .6 })
            { using var iso = surface.IsoCurve(1, u); rim.Add(iso.Trim(.3, .7)!); }
            try
            {
                using var split = whole.Faces[0].Split(rim, Tol);
                Assert.NotNull(split);
                top = split.Faces.Single(f => f.Loops.Count == 2).DuplicateFace(false);
            }
            finally { foreach (var c in rim) c.Dispose(); }
        }
        else top = whole.DuplicateBrep();
        var parts = new List<Brep> { top }; var bottomLoops = new List<Curve>();
        var bottom = new Plane(new Point3d(0, 0, -10), Vector3d.ZAxis);
        try
        {
            foreach (var loop in top.Loops)
            {
                using var boundary = loop.To3dCurve();
                var flat = boundary.DuplicateCurve(); flat.Transform(Transform.PlanarProjection(bottom)); bottomLoops.Add(flat);
            }
            parts.AddRange(Brep.CreatePlanarBreps(bottomLoops, Tol));
            foreach (var edge in top.Edges)
            {
                using var flat = edge.DuplicateCurve(); flat.Transform(Transform.PlanarProjection(bottom));
                using var wall = NurbsSurface.CreateRuledSurface(edge, flat);
                parts.Add(wall.ToBrep());
            }
            var joined = Brep.JoinBreps(parts, Tol);
            var solid = Assert.Single(joined);
            if (solid.SolidOrientation == BrepSolidOrientation.Inward) solid.Flip();
            Assert.True(solid.IsValid && solid.IsSolid);
            return solid;
        }
        finally { foreach (var p in parts) p.Dispose(); foreach (var c in bottomLoops) c.Dispose(); }
    }

    [RhinoTheory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void TangencyInsideTrimmedAwayHoleDoesNotRefuseTransverseMaterial(int placement) => host.Run(() =>
    {
        using var solid = Saddle(true);
        var plane = Plane.WorldXY;
        var transform = placement == 0 ? Transform.Identity :
            Transform.Translation(65986, 40014, 14800) * Transform.Rotation(.37, new Vector3d(1, 2, 3), Point3d.Origin);
        if (placement == 2) transform *= Transform.Mirror(Plane.WorldYZ);
        Assert.True(solid.Transform(transform)); Assert.True(plane.Transform(transform));
        using var result = SectionRegionEngine.Compute([new("saddle", solid, SectionStyleSnapshot.Default())],
            new(plane, Tol, .001), prepareVisibility: true);
        Assert.False(result.RequiresQuarantine, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        Assert.Equal(2, result.Regions.Count);
        Assert.InRange(Math.Abs(result.Regions.Sum(r => r.Area) - 7.18), 0, 1e-6);
        Assert.All(result.Regions, r => { Assert.Single(r.Loops); Assert.NotNull(r.Hatch); });
        var clip = result.Visibility["saddle"];
        Assert.True(clip.Succeeded); Assert.NotEmpty(clip.Pieces);
        Assert.All(clip.Pieces, p => Assert.True(p.Geometry.IsValid && p.Geometry.IsSolid));
    });

    [RhinoTheory]
    [InlineData(false)] [InlineData(true)]
    public void ActualSaddleTangencyStillRefusesMaterialCertificate(bool onTrimBoundary) => host.Run(() =>
    {
        using var solid = Saddle(onTrimBoundary, onTrimBoundary);
        Assert.False(StableSectionCertificate.Try(solid, new(Plane.WorldXY, Tol, .001), out var reason, out var evidence));
        evidence?.Dispose();
        Assert.Contains("transversality", reason);
    });
}
