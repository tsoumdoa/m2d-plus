using M2DPlus.Core.Visibility;
using Xunit;

namespace M2DPlus.Core.Tests;

/// <summary>Meshes with bands against shapes known in closed form. Larger Z is nearer the viewer.</summary>
public sealed class BandedMeshIndexTests
{
    private const double Tol = 1e-4;
    private static Vertex V(double x, double y, double z) => new(x, y, z);

    /// <summary>A welded latitude-longitude sphere about the origin; the band is the largest radial gap to its triangles.</summary>
    private static BandedSheet Sphere(double r, int n, int id = 0)
    {
        var vertices = new List<Vertex> { V(0, 0, r), V(0, 0, -r) }; var triangles = new List<int>();
        for (int i = 1; i < n; i++)
            for (int j = 0; j < 2 * n; j++)
            { double a = Math.PI * i / n, b = Math.PI * j / n; vertices.Add(V(r * Math.Sin(a) * Math.Cos(b), r * Math.Sin(a) * Math.Sin(b), r * Math.Cos(a))); }
        int At(int i, int j) => 2 + (i - 1) * 2 * n + j % (2 * n);
        for (int j = 0; j < 2 * n; j++)
        {
            triangles.AddRange([0, At(1, j), At(1, j + 1)]); triangles.AddRange([1, At(n - 1, j + 1), At(n - 1, j)]);
            for (int i = 1; i < n - 1; i++) { triangles.AddRange([At(i, j), At(i + 1, j), At(i + 1, j + 1)]); triangles.AddRange([At(i, j), At(i + 1, j + 1), At(i, j + 1)]); }
        }
        var bands = new double[triangles.Count / 3];
        for (int t = 0; t < bands.Length; t++)
        {
            Vertex a = vertices[triangles[t * 3]], b = vertices[triangles[t * 3 + 1]], c = vertices[triangles[t * 3 + 2]];
            double nx = (b.Y - a.Y) * (c.Z - a.Z) - (b.Z - a.Z) * (c.Y - a.Y), ny = (b.Z - a.Z) * (c.X - a.X) - (b.X - a.X) * (c.Z - a.Z), nz = (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
            bands[t] = r - Math.Abs(nx * a.X + ny * a.Y + nz * a.Z) / Math.Sqrt(nx * nx + ny * ny + nz * nz) + 1e-12;
        }
        return new() { Id = id, Vertices = vertices.ToArray(), Triangles = triangles.ToArray(), Bands = bands };
    }

    /// <summary>Cover of the sphere in front of a point: how far in front of it the sphere's front surface is.</summary>
    private static double Cover(Vertex p, double r) { double d2 = p.X * p.X + p.Y * p.Y; return d2 >= r * r ? double.NegativeInfinity : Math.Sqrt(r * r - d2) - p.Z; }

    private static List<BandedMeshIndex.Piece> Run(BandedMeshIndex index, Vertex a, Vertex b, double error = 0, double least = 1e-3)
    {
        var output = new List<BandedMeshIndex.Piece>(); index.Classify(a, b, Tol, error, least, new(index), output); return output;
    }
    private static Vertex Lerp(Vertex a, Vertex b, double t) => V(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.Z + (b.Z - a.Z) * t);

    [Fact]
    public void ASphereNeverGivesAWrongAnswerAndDecidesMostOfTheLength()
    {
        const double r = 1; var index = new BandedMeshIndex([Sphere(r, 48)]); var random = new Random(11);
        double decided = 0, total = 0, hidden = 0, visible = 0;
        for (int k = 0; k < 400; k++)
        {
            // Segments well behind the sphere's centre plane, so the sphere's own back surface is never within a band.
            Vertex a = V(3 * random.NextDouble() - 1.5, 3 * random.NextDouble() - 1.5, -1.5 - random.NextDouble()), b = V(3 * random.NextDouble() - 1.5, 3 * random.NextDouble() - 1.5, -1.5 - random.NextDouble());
            double length = Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y) + (b.Z - a.Z) * (b.Z - a.Z));
            var pieces = Run(index, a, b);
            Assert.Equal(0, pieces[0].T0); Assert.Equal(1, pieces[^1].T1);
            foreach (var piece in pieces)
            {
                total += (piece.T1 - piece.T0) * length;
                if (piece.Class == VisibilityClass.Unresolved) continue;
                decided += (piece.T1 - piece.T0) * length;
                if (piece.Class == VisibilityClass.Hidden) hidden += (piece.T1 - piece.T0) * length; else visible += (piece.T1 - piece.T0) * length;
                for (int i = 0; i <= 8; i++)
                {
                    double cover = Cover(Lerp(a, b, piece.T0 + (piece.T1 - piece.T0) * i / 8), r);
                    if (piece.Class == VisibilityClass.Hidden) Assert.True(cover > Tol, $"Hidden with cover {cover}");
                    else Assert.True(!(cover > Tol), $"Visible with cover {cover}");
                }
            }
        }
        Assert.True(decided > .97 * total, $"decided {decided} of {total}");
        Assert.True(hidden > 0 && visible > 0);
    }

    [Fact]
    public void TheCandidateErrorWidensWhatIsUndecided()
    {
        var index = new BandedMeshIndex([Sphere(1, 48)]); Vertex a = V(-2, .3, -2), b = V(2, .3, -2);
        double Undecided(double error) => Run(index, a, b, error).Where(p => p.Class == VisibilityClass.Unresolved).Sum(p => p.T1 - p.T0) * 4;
        double none = Undecided(0), some = Undecided(.02);
        Assert.True(some > none + .03, $"{none} then {some}");
        // Still nothing wrong: decided pieces hold for every point within the error of the piece.
        foreach (var piece in Run(index, a, b, .02))
            for (int i = 0; i <= 8; i++)
                foreach (double dy in new[] { -.02, 0, .02 })
                {
                    var p = Lerp(a, b, piece.T0 + (piece.T1 - piece.T0) * i / 8); double cover = Cover(V(p.X, p.Y + dy, p.Z), 1);
                    if (piece.Class == VisibilityClass.Hidden) Assert.True(cover > Tol);
                    if (piece.Class == VisibilityClass.Visible) Assert.True(!(cover > Tol));
                }
    }

    [Fact]
    public void ALineOnTheFarSideOfItsOwnSphereIsHiddenByTheFrontAndUndecidedOnlyNearTheOutline()
    {
        // A meridian arc of the back hemisphere: its own surface touches it, the front hemisphere proves it Hidden.
        var index = new BandedMeshIndex([Sphere(1, 48)]);
        var middle = Run(index, V(-.3, 0, -Math.Sqrt(1 - .09)), V(.3, 0, -Math.Sqrt(1 - .09)), .05);
        Assert.All(middle, p => Assert.Equal(VisibilityClass.Hidden, p.Class));
        // In front of everything, on the front surface itself: the own surface is within a band, nothing proves it.
        var front = Run(index, V(-.3, 0, Math.Sqrt(1 - .09)), V(.3, 0, Math.Sqrt(1 - .09)), .05);
        Assert.All(front, p => Assert.Equal(VisibilityClass.Unresolved, p.Class));
    }

    [Fact]
    public void AnOpenSheetHidesOnlyAwayFromItsBoundary()
    {
        var quad = new BandedSheet { Vertices = [V(0, 0, 1), V(1, 0, 1), V(1, 1, 1), V(0, 1, 1)], Triangles = [0, 1, 2, 0, 2, 3], Bands = [.01, .01] };
        var index = new BandedMeshIndex([quad]);
        Assert.All(Run(index, V(.3, .3, 0), V(.7, .6, 0)), p => Assert.Equal(VisibilityClass.Hidden, p.Class));
        Assert.All(Run(index, V(.3, 1.2, 0), V(.7, 1.2, 0)), p => Assert.Equal(VisibilityClass.Visible, p.Class));
        // Under the boundary: within the band of the edge the sheet may or may not reach, so neither answer.
        Assert.All(Run(index, V(.3, 1.005, 0), V(.7, 1.005, 0)), p => Assert.Equal(VisibilityClass.Unresolved, p.Class));
        Assert.All(Run(index, V(.3, .995, 0), V(.7, .995, 0)), p => Assert.Equal(VisibilityClass.Unresolved, p.Class));
        // Crossing the boundary: Hidden, a stretch of about two bands Unresolved, Visible.
        var across = Run(index, V(.5, .5, 0), V(.5, 1.5, 0), least: 1e-4);
        Assert.Equal([VisibilityClass.Hidden, VisibilityClass.Unresolved, VisibilityClass.Visible], across.Select(p => p.Class).ToArray());
        Assert.InRange(across[1].T1 - across[1].T0, .02, .03);
        // In front of the sheet, and within a tolerance behind it (the tie rule): not Hidden.
        Assert.All(Run(index, V(.3, .3, 2), V(.7, .6, 2)), p => Assert.Equal(VisibilityClass.Visible, p.Class));
        Assert.All(Run(index, V(.3, .3, 1 - .5 * Tol), V(.7, .6, 1 - .5 * Tol)), p => Assert.NotEqual(VisibilityClass.Hidden, p.Class));
    }

    [Fact]
    public void TwoLayersOfOneSheetJoinedInDepthProveNothingButSeparateSheetsDo()
    {
        // A fold: two layers closer than their bands, so their depth ranges join into one group crossed twice.
        var fold = new BandedSheet { Vertices = [V(0, 0, 1), V(1, 0, 1), V(1, 1, 1.004), V(0, 1, 1.004), V(1, 0, 1.008), V(0, 0, 1.008)], Triangles = [0, 1, 2, 0, 2, 3, 3, 2, 4, 3, 4, 5], Bands = [.01, .01, .01, .01] };
        Assert.All(Run(new BandedMeshIndex([fold]), V(.3, .3, 0), V(.7, .6, 0)), p => Assert.NotEqual(VisibilityClass.Visible, p.Class));
        Assert.All(Run(new BandedMeshIndex([fold]), V(.3, .3, 0), V(.7, .6, 0)), p => Assert.Equal(VisibilityClass.Unresolved, p.Class));
        var one = new BandedSheet { Id = 1, Vertices = [V(0, 0, 1), V(1, 0, 1), V(1, 1, 1), V(0, 1, 1)], Triangles = [0, 1, 2, 0, 2, 3], Bands = [.01, .01] };
        var two = new BandedSheet { Id = 2, Vertices = [V(0, 0, 1.01), V(1, 0, 1.01), V(1, 1, 1.01), V(0, 1, 1.01)], Triangles = [0, 1, 2, 0, 2, 3], Bands = [.01, .01] };
        Assert.All(Run(new BandedMeshIndex([one, two]), V(.3, .3, 0), V(.7, .6, 0)), p => Assert.Equal(VisibilityClass.Hidden, p.Class));
    }

    [Fact]
    public void AnEmptyIndexSeesEverythingAndBadSheetsAreRefused()
    {
        Assert.All(Run(new BandedMeshIndex([]), V(0, 0, 0), V(1, 0, 0)), p => Assert.Equal(VisibilityClass.Visible, p.Class));
        Assert.Throws<ArgumentException>(() => new BandedMeshIndex([new BandedSheet { Vertices = [V(0, 0, 0)], Triangles = [0, 0, 0], Bands = [] }]));
        Assert.Throws<ArgumentException>(() => new BandedMeshIndex([new BandedSheet { Vertices = [V(0, 0, 0)], Triangles = [0, 0, 0], Bands = [double.NaN] }]));
    }
}
