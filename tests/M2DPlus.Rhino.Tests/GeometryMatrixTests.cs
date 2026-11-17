using M2DPlus.Core;
using M2DPlus.Rhino.Services;
using Rhino;
using Rhino.Geometry;
using Xunit;
using Xunit.Abstractions;
using Point = Rhino.Geometry.Point;

namespace M2DPlus.Rhino.Tests;

public sealed class RhinoTheoryAttribute : TheoryAttribute
{
    public RhinoTheoryAttribute()
    { if (System.Environment.GetEnvironmentVariable("M2D_RHINO_TESTS") != "1") Skip = "Licensed serial Rhino.Inside integration; explicitly opt in."; }
}

/// <summary>Property gates for this native, uncut route. Research Custom/cut verdicts are not native uncut truth.</summary>
[Collection("Rhino")]
[Trait("Category", "RhinoIntegration")]
public sealed class GeometryMatrixTests(RhinoHost host, ITestOutputHelper output)
{
    [RhinoTheory]
    [InlineData("brep_box", "P:square", 8)]
    [InlineData("extrusion_solid", "P:square", 8)]
    [InlineData("brep_curved_sphere", "P:circle", 2 * Math.PI)]
    [InlineData("surface_planar_open", "P:edge-on", 2)]
    [InlineData("brep_open_box", "P:square", 8)]
    [InlineData("brep_concave_polysurface", "P:L-perimeter", 8)]
    [InlineData("brep_trimmed_planar_annulus", "P:annulus", 6 * Math.PI)]
    [InlineData("mesh_closed_quad", "P:square", 8)]
    [InlineData("mesh_closed_tri", "P:square", 8)]
    [InlineData("mesh_open_quad", "P:square", 8)]
    [InlineData("line_3d", "P:line", 6)]
    [InlineData("polyline_3d", "P:polyline", 0)]
    [InlineData("circle_3d", "P:ellipse", 0)]
    [InlineData("arc_3d", "P:ellipse-arc", 0)]
    [InlineData("rhs_hollow", "P:hollow-rectangle", 13.6)]
    [InlineData("chs_hollow", "P:hollow-circle", 2 * Math.PI * 1.7)]
    [InlineData("i_beam", "P:I-perimeter", 11.2)]
    [InlineData("point", "P:point", 0)]
    [InlineData("text_entity", "F:annotation-not-drawn", 0)]
    [InlineData("text_dot", "F:annotation-not-drawn", 0)]
    [InlineData("box_probe", "P:occlusion-intervals", 0)]
    [InlineData("curve_on_surface", "P:contact-line", 0)]
    [InlineData("brep_trimmed_curved", "U:trimmed-silhouette", 0)]
    [InlineData("mesh_nonmanifold", "U:nonmanifold-visibility", 0)]
    [InlineData("pipe_curved", "U:pipe-visibility", 0)]
    [InlineData("column_through_slab", "U:penetration-visibility", 0)]
    [InlineData("pipe_behind_glass", "U:opaque-glass", 0)]
    [InlineData("pipe_behind_glass_material", "U:opaque-glass", 0)]
    [InlineData("mesh_terrain_brep_building", "U:mixed-visibility", 0)]
    [InlineData("nurbs_curve_3d", "U:freeform-projection", 0)]
    [InlineData("subd_rounded_cube", "U:converted-subd-accuracy", 0)]
    [InlineData("subd_converted_brep", "U:converted-subd-accuracy", 0)]
    [InlineData("block_instance", "U:expanded-block-visibility", 0)]
    [InlineData("block_instance_expanded", "U:expanded-block-visibility", 0)]
    [InlineData("block_nested_mirrored", "U:expanded-block-visibility", 0)]
    [InlineData("block_nested_mirrored_expanded", "U:expanded-block-visibility", 0)]
    [InlineData("points_with_occluder", "U:point-visibility", 0)]
    [InlineData("elliptical_columns_100", "U:repeated-ellipse-visibility", 0)]
    public void FrozenMatrixProperty(string fixture, string gate, double expectedLength) => host.Run(() =>
    {
        using var document = RhinoDoc.OpenHeadless(Path.Combine(AppContext.BaseDirectory, "fixtures", fixture + ".3dm"));
        using var cache = new SceneCache();
        using var batch = BaselineTests.Solve(BaselineTests.Capture(document, cache), ViewRequest.Parse("Top"));
        var view = Assert.Single(batch.Views); Assert.Null(view.Error); Assert.False(view.Report.Quarantined, view.Info);
        Assert.Equal(0, view.Report.InvalidExtractionCount);
        if (gate.StartsWith("F:"))
        {
            // Explicit expected failure: annotations remain undrawn. An improvement changes this gate visibly.
            Assert.Empty(view.Items); Assert.Contains(view.Report.UnsupportedByType.Keys, k => k.StartsWith("Not drawn (non-occluding)"));
        }
        else if (gate.StartsWith("U:"))
        {
            Assert.NotEmpty(view.Items); Assert.Contains("visibility not certified", view.Report.ValidationDomain);
        }
        else if (gate == "P:point")
        { var item = Assert.Single(view.Items); using var g = item.CopyGeometry(); Assert.IsType<Point>(g); Assert.Equal(LineClass.Visible, item.Class); }
        else if (gate == "P:occlusion-intervals") CheckProbe(document, view, false);
        else if (gate == "P:contact-line") CheckProbe(document, view, true);
        else
        {
            var curves = new List<Curve>();
            try
            {
                foreach (var item in view.Items) { Assert.Equal(LineClass.Visible, item.Class); curves.Add(Assert.IsAssignableFrom<Curve>(item.CopyGeometry())); }
                if (gate == "P:ellipse" || gate == "P:ellipse-arc") expectedLength = EllipseLength(gate == "P:ellipse" ? 2 * Math.PI : 1.5 * Math.PI);
                if (gate == "P:polyline") expectedLength = 4 * Math.Sqrt(2);
                Assert.InRange(Math.Abs(curves.Sum(c => c.GetLength()) - expectedLength), 0, 5e-4);
                if (gate == "P:square") CheckSquare(curves);
                if (gate == "P:circle" || gate == "P:hollow-circle" || gate == "P:annulus")
                    foreach (var c in curves) for (var i = 0; i <= 64; i++)
                    {
                        var p = c.PointAtNormalizedLength(i / 64d); var r = Math.Sqrt(p.X * p.X + p.Y * p.Y);
                        var radii = gate == "P:annulus" ? new[] { 1d, 2d } : gate == "P:hollow-circle" ? new[] { .7, 1d } : new[] { 1d };
                        Assert.Contains(radii, expected => Math.Abs(r - expected) < 1e-4);
                    }
                if (gate == "P:ellipse" || gate == "P:ellipse-arc")
                    foreach (var c in curves) for (var i = 0; i <= 64; i++)
                    { var p = c.PointAtNormalizedLength(i / 64d); Assert.InRange(Math.Abs(p.X * p.X + 2 * p.Y * p.Y - 1), 0, 2e-5); }
            }
            finally { foreach (var curve in curves) curve.Dispose(); }
        }
        output.WriteLine($"{fixture}: {gate}; native={view.Seconds:F3}s; pieces={view.Items.Count}; whole-output truth only where explicitly checked; research section properties deferred to M2");
    });

    private static void CheckSquare(List<Curve> curves)
    {
        // All and only the four side loci. Intervals enforce coverage without overlaps or retracing.
        var sides = new List<(double, double)>[4]; for (var i = 0; i < 4; i++) sides[i] = new();
        foreach (var c in curves)
        {
            using var poly = c.ToPolyline(1e-7, .01, 0, 0); Assert.True(poly.TryGetPolyline(out var points));
            for (var i = 1; i < points.Count; i++)
            {
                var a = points[i - 1]; var b = points[i]; var vertical = Math.Abs(a.X - b.X) < 1e-6;
                var constant = vertical ? a.X : a.Y; Assert.InRange(Math.Abs(Math.Abs(constant) - 1), 0, 1e-5);
                Assert.True(vertical || Math.Abs(a.Y - b.Y) < 1e-6);
                var min = Math.Min(vertical ? a.Y : a.X, vertical ? b.Y : b.X); var max = Math.Max(vertical ? a.Y : a.X, vertical ? b.Y : b.X);
                if (max - min > 1e-7) sides[(vertical ? 0 : 2) + (constant > 0 ? 1 : 0)].Add((min, max));
            }
        }
        foreach (var side in sides)
        {
            var end = -1d; Assert.NotEmpty(side);
            foreach (var interval in side.OrderBy(s => s.Item1)) { Assert.Equal(end, interval.Item1, 5); end = interval.Item2; }
            Assert.Equal(1, end, 5);
        }
    }
    private static void CheckProbe(RhinoDoc document, ViewDrawing view, bool contact)
    {
        var id = document.Objects.GetObjectList(global::Rhino.DocObjects.ObjectType.Curve).Single().Id.ToString();
        var pieces = view.Items.Where(p => p.SourceIdentity == id).ToArray(); Assert.NotEmpty(pieces);
        var intervals = pieces.Select(p => { using var g = p.CopyGeometry(); var b = g.GetBoundingBox(true); Assert.InRange(Math.Abs(b.Min.Y) + Math.Abs(b.Max.Y), 0, 1e-6); return (p.Class, b.Min.X, b.Max.X); }).OrderBy(p => p.Item2).ToArray();
        if (contact) { var one = Assert.Single(intervals); Assert.Equal(LineClass.Visible, one.Class); Assert.Equal(-.8, one.Item2, 5); Assert.Equal(.8, one.Item3, 5); }
        else
        {
            Assert.Equal(3, intervals.Length); var bounds = new[] { -3d, -1d, 1d, 3d };
            for (var i = 0; i < 3; i++) { Assert.Equal(i == 1 ? LineClass.Hidden : LineClass.Visible, intervals[i].Class); Assert.InRange(Math.Abs(intervals[i].Item2 - bounds[i]), 0, 1e-4); Assert.InRange(Math.Abs(intervals[i].Item3 - bounds[i + 1]), 0, 1e-4); }
        }
    }
    private static double EllipseLength(double end)
    {
        // Independent integration of radius-1 circle tilted 45 degrees: a=1, b=1/sqrt(2).
        const int n = 20000; var h = end / n; var sum = 0d;
        for (var i = 0; i <= n; i++) { var t = i * h; var f = Math.Sqrt(Math.Sin(t) * Math.Sin(t) + .5 * Math.Cos(t) * Math.Cos(t)); sum += (i == 0 || i == n ? 1 : i % 2 == 0 ? 2 : 4) * f; }
        return sum * h / 3;
    }

    [RhinoFact] public void ExpectedFailureRawClippingUnsetProxiesAreCountedWithoutDereferencing() => host.Run(() =>
    {
        // Research F extraction property. This is deliberately a RAW diagnostic experiment;
        // M1b never enables native clipping or exposes section views.
        using var document = RhinoDoc.OpenHeadless(Path.Combine(AppContext.BaseDirectory, "fixtures", "box_probe.3dm"));
        using var viewport = new global::Rhino.DocObjects.ViewportInfo(); viewport.IsParallelProjection = true;
        viewport.SetCameraLocation(new Point3d(0, 0, 100)); viewport.SetCameraDirection(-Vector3d.ZAxis); viewport.SetCameraUp(Vector3d.YAxis);
        viewport.SetFrustum(-200, 200, -200, 200, .001, 1000);
        var parameters = new HiddenLineDrawingParameters { AbsoluteTolerance = 1e-4, IncludeHiddenCurves = true,
            IncludeTangentEdges = false, IncludeTangentSeams = false, Flatten = false };
        parameters.SetViewport(viewport); parameters.AddClippingPlane(Plane.WorldXY);
        foreach (var obj in document.Objects.GetObjectList(global::Rhino.DocObjects.ObjectType.AnyObject)) Assert.True(parameters.AddGeometry(obj.Geometry, obj.Id));
        using var drawing = HiddenLineDrawing.Compute(parameters, true); Assert.NotNull(drawing);
        var unset = drawing.Segments.Count(s => s.SegmentVisibility == HiddenLineDrawingSegment.Visibility.Unset);
        Assert.True(unset > 0, "Expected native clipping extraction failure improved: review and upgrade this F gate.");
        output.WriteLine($"F raw clipping: {unset} Unset proxies (ParentCurve and CurveGeometry never read). Engine section views remain disabled.");
    });
}
