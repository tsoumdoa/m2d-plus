using M2DPlus.Core.Visibility.Raster;
using Xunit;

namespace M2DPlus.Core.Tests;

public sealed class RasterTests
{
    private static RasterFace Face(double[] xy, double a = 0, double b = 0, double c = 5, double max = 5) =>
        new() { Loops = xy, Starts = new[] { 0 }, A = a, B = b, C = c, MaxDepth = max };

    private static List<double> Boundary(double[] xy)
    {
        var list = new List<double>(); var n = xy.Length / 2;
        for (var k = 0; k < n; k++) { var m = (k + 1) % n; list.AddRange(new[] { xy[2 * k], xy[2 * k + 1], xy[2 * m], xy[2 * m + 1] }); }
        return list;
    }

    [Fact]
    public void TouchVisitsEveryPixelTheSegmentCrosses()
    {
        var random = new Random(7);
        for (var trial = 0; trial < 500; trial++)
        {
            double x0 = random.NextDouble() * 20, y0 = random.NextDouble() * 20, x1 = random.NextDouble() * 20, y1 = random.NextDouble() * 20;
            if (trial % 5 == 0) y1 = y0;
            if (trial % 7 == 0) x1 = x0;
            var visited = new HashSet<(int, int)>();
            ConservativeScan.Touch(x0, y0, x1, y1, 1e-9, 0, 0, 31, 31, (i, j) => visited.Add((i, j)));
            for (var s = 0; s <= 2000; s++)
            {
                var t = s / 2000.0; var x = x0 + (x1 - x0) * t; var y = y0 + (y1 - y0) * t;
                // A point on a pixel edge belongs to both neighbours; require at least the floor pixel.
                Assert.Contains(((int)Math.Floor(x), (int)Math.Floor(y)), visited);
            }
        }
    }

    [Fact]
    public void CoverageIsInnerConservativeAndDepthIsAnUpperBound()
    {
        var random = new Random(11);
        for (var trial = 0; trial < 200; trial++)
        {
            // Random convex polygon (sorted angles around a centre).
            var n = random.Next(3, 9); var cx = 10 + random.NextDouble() * 12; var cy = 10 + random.NextDouble() * 12;
            var angles = Enumerable.Range(0, n).Select(_ => random.NextDouble() * Math.PI * 2).Order().ToArray();
            var radius = 3 + random.NextDouble() * 6; // points on a circle in angle order: convex, CCW
            var xy = angles.SelectMany(t => new[] { cx + radius * Math.Cos(t), cy + radius * Math.Sin(t) }).ToArray();
            double a = random.NextDouble() - .5, b = random.NextDouble() - .5, c = 40;
            var max = Enumerable.Range(0, n).Max(k => a * xy[2 * k] + b * xy[2 * k + 1] + c);
            var buffer = new OccluderBuffer(new RasterGrid(0, 0, 1, 32, 32));
            buffer.Add(new RasterPatch { Faces = new[] { Face(xy, a, b, c, max) }, Boundary = Boundary(xy).ToArray(), Dilation = 1e-6 });
            for (var i = 0; i < 32; i++)
                for (var j = 0; j < 32; j++)
                {
                    var far = buffer.Far(i, j);
                    if (float.IsPositiveInfinity(far)) continue;
                    // Every corner inside the convex polygon (left of every CCW edge) => the whole square is inside.
                    foreach (var (px, py) in new[] { (i, j), (i + 1, j), (i, j + 1), (i + 1, j + 1) })
                    {
                        for (var k = 0; k < n; k++)
                        {
                            var m = (k + 1) % n;
                            var cross = (xy[2 * m] - xy[2 * k]) * (py - xy[2 * k + 1]) - (xy[2 * m + 1] - xy[2 * k + 1]) * (px - xy[2 * k]);
                            Assert.True(cross > 0, $"pixel {i},{j} corner outside");
                        }
                        Assert.True(far >= a * px + b * py + c - 1e-9);
                    }
                }
        }
    }

    [Fact]
    public void SharedEdgeOnOppositeSidesIsInteriorButFoldIsBoundary()
    {
        // Two triangles making a square; the diagonal is shared. Opposite sides: interior, so diagonal pixels are covered.
        double[] t1 = { 2, 2, 14, 2, 14, 14 }, t2 = { 2, 2, 14, 14, 2, 14 };
        var outer = new double[] { 2, 2, 14, 2, 14, 2, 14, 14, 14, 14, 2, 14, 2, 14, 2, 2 };
        var buffer = new OccluderBuffer(new RasterGrid(0, 0, 1, 16, 16));
        buffer.Add(new RasterPatch { Faces = new[] { Face(t1), Face(t2) }, Boundary = outer, Dilation = 1e-6 });
        Assert.False(float.IsPositiveInfinity(buffer.Far(7, 7)));
        // If the diagonal is declared a boundary (e.g. a fold), the pixels it touches are never covered.
        var folded = new OccluderBuffer(new RasterGrid(0, 0, 1, 16, 16));
        folded.Add(new RasterPatch { Faces = new[] { Face(t1), Face(t2) }, Boundary = outer.Concat(new double[] { 2, 2, 14, 14 }).ToArray(), Dilation = 1e-6 });
        Assert.True(float.IsPositiveInfinity(folded.Far(7, 7)));
        Assert.False(float.IsPositiveInfinity(folded.Far(10, 4)));
    }

    [Fact]
    public void HiddenRequiresStrictlyNearerCoverageEverywhere()
    {
        var square = new double[] { 2, 2, 14, 2, 14, 14, 2, 14 };
        var buffer = new OccluderBuffer(new RasterGrid(0, 0, 1, 16, 16));
        buffer.Add(new RasterPatch { Faces = new[] { Face(square, c: 5, max: 5) }, Boundary = Boundary(square).ToArray(), Dilation = 1e-6 });
        Assert.True(buffer.Hidden(4, 4, 10, 10, 5.5, .1, false));
        Assert.False(buffer.Hidden(4, 4, 10, 10, 5.05, .1, false));   // within the margin: a tie, never culled
        Assert.False(buffer.Hidden(1, 4, 10, 10, 9, .1, false));      // pixel column 1 is outside the occluder
        Assert.False(buffer.Hidden(4, 4, 20, 10, 9, .1, false));      // off-grid is unknown unless cropped away
        Assert.False(buffer.Hidden(4, 4, 20, 10, 9, .1, true));       // in-grid pixels 14..15 are uncovered
        Assert.True(buffer.Hidden(20, 20, 30, 30, 9, .1, true));      // wholly outside a crop: irrelevant
    }

    [Fact]
    public void FaceFootprintUsesItsPlaneAndAllTouchedPixels()
    {
        var square = new double[] { 2, 2, 14, 2, 14, 14, 2, 14 };
        var buffer = new OccluderBuffer(new RasterGrid(0, 0, 1, 16, 16));
        buffer.Add(new RasterPatch { Faces = new[] { Face(square, c: 5, max: 5) }, Boundary = Boundary(square).ToArray(), Dilation = 1e-6 });
        // A slanted triangle behind the occluder: depth 6 + 0.1 x. Hidden everywhere inside the occluder's interior.
        var inner = new RasterFace { Loops = new double[] { 4, 4, 11, 4, 4, 11 }, Starts = new[] { 0 }, A = .1, B = 0, C = 6, MaxDepth = 6 };
        Assert.True(buffer.HiddenFace(inner, 1e-6, .1, false));
        // The same triangle tilted forwards so its near corner reaches depth 5.05: within the margin there, not hidden.
        var tilted = new RasterFace { Loops = new double[] { 4, 4, 11, 4, 4, 11 }, Starts = new[] { 0 }, A = -.15, B = 0, C = 6.7, MaxDepth = 0 };
        Assert.False(buffer.HiddenFace(tilted, 1e-6, .1, false));
        // A triangle whose edge pokes past the occluder boundary (pixel column 14) is never hidden.
        var poking = new RasterFace { Loops = new double[] { 4, 4, 14.2, 4, 4, 11 }, Starts = new[] { 0 }, A = 0, B = 0, C = 9, MaxDepth = 9 };
        Assert.False(buffer.HiddenFace(poking, 1e-6, .1, false));
    }

    [Fact]
    public void BufferIsOrderIndependent()
    {
        var random = new Random(3);
        var patches = Enumerable.Range(0, 30).Select(_ =>
        {
            var x = random.NextDouble() * 20; var y = random.NextDouble() * 20; var w = 2 + random.NextDouble() * 8;
            var sq = new[] { x, y, x + w, y, x + w, y + w, x, y + w };
            var d = 1 + random.NextDouble() * 10;
            return new RasterPatch { Faces = new[] { Face(sq, c: d, max: d) }, Boundary = Boundary(sq).ToArray(), Dilation = 1e-6 };
        }).ToArray();
        var a = new OccluderBuffer(new RasterGrid(0, 0, 1, 32, 32)); var b = new OccluderBuffer(new RasterGrid(0, 0, 1, 32, 32));
        foreach (var p in patches) a.Add(p);
        Parallel.ForEach(Enumerable.Reverse(patches).ToArray(), p => { b.Add(p); });
        for (var i = 0; i < 32; i++) for (var j = 0; j < 32; j++) Assert.Equal(a.Far(i, j), b.Far(i, j));
    }
}

public sealed class SurfaceCellTests
{
    // Full cylinder of radius r about the z axis, height h: rational quadratic circle in u (9 CVs), linear in v.
    private static (double[] Net, double[] KnotsU, double[] KnotsV) Cylinder(double r, double h)
    {
        var s = Math.Sqrt(.5);
        (double X, double Y, double W)[] circle = { (r, 0, 1), (r, r, s), (0, r, 1), (-r, r, s), (-r, 0, 1), (-r, -r, s), (0, -r, 1), (r, -r, s), (r, 0, 1) };
        var net = new double[9 * 2 * 4];
        for (var i = 0; i < 9; i++)
            for (var j = 0; j < 2; j++)
            {
                var (x, y, w) = circle[i]; var k = (i * 2 + j) * 4;
                net[k] = x * w; net[k + 1] = y * w; net[k + 2] = j * h * w; net[k + 3] = w;
            }
        return (net, new double[] { 0, 0, 1, 1, 2, 2, 3, 3, 4, 4 }, new double[] { 0, 1 });
    }

    [Fact]
    public void CylinderCellsAreOnTheSurfaceAndCoverageIsCertified()
    {
        const double r = 4, h = 10, scale = 3; // pixels per unit
        // Front view (looking along +y): pixel x = 3*(x+6), pixel y = 3*(z+1), depth = y + 10.
        var map = new ViewAffine(scale, 0, 0, 6 * scale, 0, 0, scale, scale, 0, 1, 0, 10);
        var (net, ku, kv) = Cylinder(r, h);
        var grid = SurfaceCells.Build(2, 1, ku, kv, 9, 2, net, map, .25, 4096);
        Assert.NotNull(grid);
        // Every corner lies on the cylinder (decomposition and subdivision are exact).
        for (var i = 0; i <= grid!.Nu; i++)
        {
            var (x, _) = grid.Corner(i, 0); var wx = x / scale - 6;
            Assert.True(Math.Abs(wx) <= r + 1e-9);
        }
        var buffer = new OccluderBuffer(new RasterGrid(0, 0, 1, 40, 36));
        foreach (var patch in grid.Patches(1e-9, grid.Deviation + 1e-6)) buffer.Add(patch);
        var covered = 0;
        for (var i = 0; i < 40; i++)
            for (var j = 0; j < 36; j++)
            {
                var far = buffer.Far(i, j);
                if (float.IsPositiveInfinity(far)) continue;
                covered++;
                // Whole pixel inside the projected cylinder [-r, r] x [0, h]; far is at least the front surface depth there.
                foreach (var (px, py) in new[] { (i, j), (i + 1, j), (i, j + 1), (i + 1, j + 1) })
                {
                    double x = px / scale - 6, z = py / scale - 1;
                    Assert.True(Math.Abs(x) < r && z > 0 && z < h, $"pixel {i},{j}");
                    Assert.True(far >= 10 - Math.Sqrt(r * r - x * x) - 1e-6, $"depth at {i},{j}");
                }
            }
        // Most of the 24 x 30 pixel interior is certified (only a band near the silhouette and caps is lost).
        Assert.True(covered > 500, $"covered {covered}");
    }

    [Fact]
    public void DecomposeReproducesTheCurve()
    {
        // Degree-3 clamped B-spline with interior knots; decomposed spans must agree with the spline at span ends.
        var knots = new double[] { 0, 0, 0, 0, 1, 2, 3, 4, 4, 4, 4 }; // full clamped vector, 7 control points
        var pts = new double[] { 0, 0, 0, 1, 1, 2, 0, 1, 2, 2, 1, 1, 3, 0, 0, 1, 4, 1, 2, 1, 5, 3, 1, 1, 6, 0, 0, 1 };
        var spans = SurfaceCells.Decompose(3, knots, pts);
        Assert.Equal(4, spans.Count);
        for (var s = 0; s + 1 < spans.Count; s++)
            for (var c = 0; c < 4; c++) Assert.Equal(spans[s][12 + c], spans[s + 1][c], 9);
        Assert.Equal(0, spans[0][0], 9); Assert.Equal(6, spans[^1][12], 9);
    }
}

public sealed class SeamMergeTests
{
    private static PatchInput Square(double x0, double y0, double x1, double y1, double depth, bool ccw = true)
    {
        var xy = ccw ? new[] { x0, y0, x1, y0, x1, y1, x0, y1 } : new[] { x0, y0, x0, y1, x1, y1, x1, y0 };
        var segs = new List<SidedSegment>();
        for (var k = 0; k < 4; k++) { var m = (k + 1) % 4; segs.Add(new(xy[2 * k], xy[2 * k + 1], xy[2 * m], xy[2 * m + 1], ccw ? 1 : -1)); }
        return new PatchInput { Faces = new[] { new RasterFace { Loops = xy, Starts = new[] { 0 }, C = depth, MaxDepth = depth } }, Boundary = segs, Dilation = 1e-6 };
    }

    private static OccluderBuffer Raster(IEnumerable<RasterPatch> patches)
    {
        var buffer = new OccluderBuffer(new RasterGrid(0, 0, 1, 32, 32));
        foreach (var p in patches) buffer.Add(p);
        return buffer;
    }

    [Fact]
    public void AbuttingObjectsCoverTheirSeam()
    {
        // Two objects meet along x = 10.5 (a pixel-straddling seam); one is wound clockwise (a back-projected face).
        var merged = SeamMerge.Merge(new[] { Square(2, 2, 10.5, 20, 5), Square(10.5, 2, 20, 20, 7, ccw: false) }, 1e-9, out var shared);
        Assert.Equal(2, shared);
        Assert.Equal(3, merged.Count); // both originals plus the merged group
        var buffer = Raster(merged);
        Assert.Equal(7f, buffer.Far(10, 10)); // seam pixel: covered, bounded by the deeper face
        Assert.Equal(5f, buffer.Far(5, 10));
        var separate = Raster(new[] { Square(2, 2, 10.5, 20, 5), Square(10.5, 2, 20, 20, 7, ccw: false) }
            .Select(p => new RasterPatch { Faces = p.Faces, Boundary = p.Boundary.SelectMany(s => new[] { s.X0, s.Y0, s.X1, s.Y1 }).ToArray(), Dilation = p.Dilation }));
        Assert.True(float.IsPositiveInfinity(separate.Far(10, 10)));
    }

    [Fact]
    public void OverlappingSameSideFacesAreNotMerged()
    {
        // Both faces lie left of x = 10.5 (overlap, not abut): the seam must stay a boundary.
        var merged = SeamMerge.Merge(new[] { Square(2, 2, 10.5, 20, 5), Square(4, 2, 10.5, 20, 6) }, 1e-9, out var shared);
        Assert.Equal(0, shared);
        Assert.True(float.IsPositiveInfinity(Raster(merged).Far(10, 10)));
    }

    [Fact]
    public void PartialOverlapLeavesTheUnsharedPartAsBoundary()
    {
        // A tall object abuts a short one: only y in [2, 10] of the seam is shared.
        var merged = SeamMerge.Merge(new[] { Square(2, 2, 10.5, 24, 5), Square(10.5, 2, 20, 10, 5, ccw: false) }, 1e-9, out _);
        var buffer = Raster(merged);
        Assert.False(float.IsPositiveInfinity(buffer.Far(10, 5)));
        Assert.True(float.IsPositiveInfinity(buffer.Far(10, 16)));
    }
}
