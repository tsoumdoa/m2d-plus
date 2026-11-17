using M2DPlus.Core.Visibility;
using Xunit;

namespace M2DPlus.Core.Tests;

/// <summary>
/// Control-net certificates against surfaces evaluated here by de Casteljau, independently of the certificate's cells.
/// </summary>
public sealed class SurfaceCertificateTests
{
    private static readonly Vertex X = new(1, 0, 0), Y = new(0, 1, 0);

    /// <summary>One Bezier patch on [0,1]²: homogeneous control points, V index fastest.</summary>
    private sealed record Patch(int P, int Q, double[] H, bool Rational)
    {
        public Vertex At(double u, double v)
        {
            var rows = new double[(P + 1) * 4];
            for (int i = 0; i <= P; i++)
            {
                var line = new double[(Q + 1) * 4]; Array.Copy(H, i * (Q + 1) * 4, line, 0, line.Length);
                Casteljau(line, Q, v); Array.Copy(line, 0, rows, i * 4, 4);
            }
            Casteljau(rows, P, u); return new(rows[0] / rows[3], rows[1] / rows[3], rows[2] / rows[3]);
        }
        private static void Casteljau(double[] t, int n, double x)
        {
            for (int level = 0; level < n; level++)
                for (int k = 0; k < n - level; k++)
                    for (int c = 0; c < 4; c++) t[k * 4 + c] = (1 - x) * t[k * 4 + c] + x * t[(k + 1) * 4 + c];
        }
        public SurfaceCertificate Certificate(double cell) => SurfaceCertificate.Create(P, Q, [0, 1], [0, 1], H, Rational, [], [], cell);
    }

    private static Patch Bicubic(int seed)
    {
        var random = new Random(seed); var h = new double[16 * 4];
        for (int i = 0; i < 4; i++)
            for (int j = 0; j < 4; j++)
            { int o = (i * 4 + j) * 4; h[o] = i / 3.0; h[o + 1] = j / 3.0; h[o + 2] = .2 * (random.NextDouble() - .5); h[o + 3] = 1; }
        return new(3, 3, h, false);
    }

    /// <summary>A quarter of a cylinder of radius r about the Y axis (rational quadratic arc from +X to +Z) of length 1.</summary>
    private static Patch QuarterCylinder(double r)
    {
        double w = Math.Sqrt(.5); var arc = new[] { (X: r, Z: 0d, W: 1d), (X: r, Z: r, W: w), (X: 0d, Z: r, W: 1d) }; var h = new double[3 * 2 * 4];
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 2; j++)
            { int o = (i * 2 + j) * 4; h[o] = arc[i].X * arc[i].W; h[o + 1] = j * arc[i].W; h[o + 2] = arc[i].Z * arc[i].W; h[o + 3] = arc[i].W; }
        return new(2, 1, h, true);
    }

    private static double Measured(Patch patch, (double U, double V) a, (double U, double V) b, (double U, double V) c)
    {
        Vertex pa = patch.At(a.U, a.V), pb = patch.At(b.U, b.V), pc = patch.At(c.U, c.V); double worst = 0;
        for (int i = 0; i <= 12; i++)
            for (int j = 0; i + j <= 12; j++)
            {
                double x = i / 12.0, y = j / 12.0, z = 1 - x - y; var s = patch.At(a.U * x + b.U * y + c.U * z, a.V * x + b.V * y + c.V * z);
                double dx = s.X - (pa.X * x + pb.X * y + pc.X * z), dy = s.Y - (pa.Y * x + pb.Y * y + pc.Y * z), dz = s.Z - (pa.Z * x + pb.Z * y + pc.Z * z);
                worst = Math.Max(worst, Math.Sqrt(dx * dx + dy * dy + dz * dz));
            }
        return worst;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void DeviationBoundsAPolynomialPatchAndIsNotWildlyLoose(int seed)
    {
        var patch = Bicubic(seed); var cert = patch.Certificate(.05); var random = new Random(seed * 7); double ratio = 0; int n = 0;
        for (int k = 0; k < 200; k++)
        {
            double u = random.NextDouble() * .9, v = random.NextDouble() * .9, size = .02 + .08 * random.NextDouble();
            var a = (u, v); var b = (u + size, v + .3 * size); var c = (u + .2 * size, v + size);
            double bound = cert.Deviation(a.Item1, a.Item2, b.Item1, b.Item2, c.Item1, c.Item2), measured = Measured(patch, a, b, c);
            Assert.True(measured <= bound, $"measured {measured} over bound {bound}");
            if (measured > 1e-9) { ratio += bound / measured; n++; }
        }
        Assert.InRange(ratio / n, 1, 12);
    }

    [Fact]
    public void DeviationBoundsARationalPatch()
    {
        var patch = QuarterCylinder(.1); var cert = patch.Certificate(.01); var random = new Random(5);
        for (int k = 0; k < 200; k++)
        {
            double u = random.NextDouble() * .85, v = random.NextDouble() * .85, size = .02 + .1 * random.NextDouble();
            var a = (u, v); var b = (u + size, v); var c = (u, v + size);
            double bound = cert.Deviation(u, v, u + size, v, u, v + size), measured = Measured(patch, a, b, c);
            Assert.True(measured <= bound, $"measured {measured} over bound {bound}");
            Assert.True(bound < 20 * Math.Max(measured, 1e-6), $"bound {bound} against measured {measured}");
        }
    }

    [Fact]
    public void DeviationIsInfiniteAcrossACreaseAndOutsideTheSurface()
    {
        var patch = Bicubic(1); var two = new double[7 * 4 * 4];
        for (int i = 0; i < 7; i++) Array.Copy(patch.H, Math.Min(i, 6 - i) * 16, two, i * 16, 16);       // mirrored: a crease at the middle
        var cert = SurfaceCertificate.Create(3, 3, [0, 1, 2], [0, 1], two, false, [1], [], .05);
        Assert.True(double.IsPositiveInfinity(cert.Deviation(.9, .1, 1.1, .1, .9, .2)));
        Assert.True(double.IsFinite(cert.Deviation(.8, .1, .95, .1, .8, .2)));
        Assert.True(double.IsPositiveInfinity(cert.Deviation(1.9, .1, 2.1, .1, 1.9, .2)));
    }

    [Fact]
    public void GraphHoldsForAPatchSeenFromAboveAndFailsAcrossAFold()
    {
        var flat = Bicubic(2).Certificate(.05);
        Assert.True(flat.Graph(0, 1, 0, 1, X, Y));
        // Seen along X the patch is nearly edge-on everywhere.
        Assert.False(flat.Graph(0, 1, 0, 1, Y, new(0, 0, 1)));
        var cylinder = QuarterCylinder(.1).Certificate(.005);
        // Seen along +Z the quarter runs from edge-on (u = 0) to face-on (u = 1).
        Assert.False(cylinder.Graph(0, 1, 0, 1, X, Y));
        Assert.True(cylinder.Graph(.3, 1, 0, 1, X, Y));
        // Seen along the bisector of X and Z nothing of the quarter is edge-on.
        double s = Math.Sqrt(.5);
        Assert.True(cylinder.Graph(0, 1, 0, 1, new(s, 0, -s), Y));
    }

    [Fact]
    public void FoldHoldsAtACylinderSilhouetteAndNotOnASaddleSeenAlongItsAsymptote()
    {
        var cylinder = QuarterCylinder(.1).Certificate(.005);
        // View along +Z; the silhouette is the ruling at u = 0, where the normal is +X: angle 0 in the drawing.
        Assert.True(cylinder.Fold(0, .4, 0, 1, X, Y, -.01, .01));
        Assert.False(cylinder.Fold(0, .4, 0, 1, X, Y, 0, 2));
        // z = x y seen along +X: at the origin the view direction is an asymptotic direction, the normal is +Z.
        var h = new double[4 * 4];
        for (int i = 0; i < 2; i++) for (int j = 0; j < 2; j++) { int o = (i * 2 + j) * 4; double x = i - .5, y = j - .5; h[o] = x; h[o + 1] = y; h[o + 2] = x * y; h[o + 3] = 1; }
        var saddle = SurfaceCertificate.Create(1, 1, [0, 1], [0, 1], h, false, [], [], .05);
        Assert.False(saddle.Fold(.3, .7, .3, .7, Y, new(0, 0, 1), Math.PI / 2 - .01, Math.PI / 2 + .01));
    }

    [Fact]
    public void FoldRefusesABoxAcrossACrease()
    {
        var patch = QuarterCylinder(.1); var two = new double[5 * 2 * 4];
        for (int i = 0; i < 5; i++) Array.Copy(patch.H, Math.Min(i, 4 - i) * 8, two, i * 8, 8);
        var cert = SurfaceCertificate.Create(2, 1, [0, 1, 2], [0, 1], two, true, [1], [], .005);
        Assert.True(cert.Fold(0, .4, 0, 1, X, Y, -.01, .01));
        Assert.False(cert.Fold(0, 1.2, 0, 1, X, Y, -.01, .01));
    }

    [Fact]
    public void ACertifiedFoldBoxIsMetOnlyAtTheTouchingPoint()
    {
        // Brute force on the quarter cylinder: every sampled point of the box other than the ruling u = 0 is off the
        // tangent line (x = r) by more than rounding.
        var patch = QuarterCylinder(.1); var cert = patch.Certificate(.005);
        Assert.True(cert.Fold(0, .5, 0, 1, X, Y, -.01, .01));
        for (int i = 1; i <= 50; i++)
            for (int j = 0; j <= 10; j++)
                Assert.True(Math.Abs(patch.At(.5 * i / 50, j / 10.0).X - .1) > 1e-9);
    }

    [Fact]
    public void CreateRejectsBadInput()
    {
        var patch = Bicubic(1);
        Assert.Throws<ArgumentException>(() => SurfaceCertificate.Create(3, 3, [0, 1], [0, 1], new double[8], false, [], [], .05));
        Assert.Throws<ArgumentException>(() => SurfaceCertificate.Create(3, 3, [0, 0], [0, 1], patch.H, false, [], [], .05));
        var bad = (double[])patch.H.Clone(); bad[3] = 0;
        Assert.Throws<ArgumentException>(() => SurfaceCertificate.Create(3, 3, [0, 1], [0, 1], bad, true, [], [], .05));
    }
}
