using M2DPlus.Core.Visibility;
using Xunit;

namespace M2DPlus.Core.Tests;

public sealed class VisibilityTests
{
    private static Vertex V(double x, double y, double z = 1) => new(x, y, z);
    private static PlanarFace Square => new([[V(0, 0), V(2, 0), V(2, 2), V(0, 2)]]);
    [Fact]
    public void HoleBoundaryDepthCrossingAndSelfContactMatchIndependentOracle()
    {
        var face = Square with { Loops = [Square.Loops[0], [V(.5, .5), V(1.5, .5), V(1.5, 1.5), V(.5, 1.5)]] };
        CandidateEdge[] edges = [new(V(-1, 1, 0), V(3, 1, 0), "hole"), new(V(-1, 0, 0), V(3, 0, 0), "boundary"),
            new(V(-1, .25, -2), V(3, .25, 4), "depth"), new(V(0, 0), V(2, 0), "self")];
        Compare([face], edges);
        var r = EdgeFaceKernel.Solve([face], edges, new(1e-9));
        Assert.Equal(2, r.Intervals.Count(i => i.Edge == 0 && i.Class == VisibilityClass.Hidden));
        Assert.Contains(r.Intervals, i => i.Edge == 1 && i.Class == VisibilityClass.Hidden && i.Start == .25 && i.End == .75);
        Assert.Contains(r.Intervals, i => i.Edge == 3 && i.Class == VisibilityClass.Visible && i.Start == 0 && i.End == 1);
    }
    [Fact]
    public void SeededAdversarialScenesAndPermutationMatchOracle()
    {
        var random = new Random(78123);
        for (int trial = 0; trial < 16; trial++)
        {
            var faces = Enumerable.Range(0, 5).Select(_ =>
            {
                double x = random.Next(-3, 3), y = random.Next(-3, 3), z = random.Next(-2, 3);
                return new PlanarFace([[V(x, y, z), V(x + 2, y, z + .25), V(x + 2, y + 2, z + .5), V(x, y + 2, z + .25)]]);
            }).ToArray();
            var edges = Enumerable.Range(0, 10).Select(i => new CandidateEdge(V(random.Next(-4, 5), random.Next(-4, 5), random.Next(-3, 4)),
                V(random.Next(-4, 5), random.Next(-4, 5), random.Next(-3, 4)), i.ToString())).ToArray();
            Compare(faces, edges); Compare(faces.AsEnumerable().Reverse().ToArray(), edges);
        }
    }
    [Fact]
    public void NonfiniteAndNonplanarOccludersQuarantineWholeView()
    {
        CandidateEdge[] edges = [new(V(-1, 1), V(3, 1), "test")];
        foreach (var face in new[] { new PlanarFace([[V(0, 0), V(2, 0), V(2, 2), V(0, 2, 4)]]),
            new PlanarFace([[V(double.NaN, 0), V(2, 0), V(2, 2)]]) })
            Assert.All(EdgeFaceKernel.Solve([face], edges, new(.0001)).Intervals, i => Assert.Equal(VisibilityClass.Unresolved, i.Class));
    }
    [Fact]
    public void CancellationIsObserved()
    {
        using var c = new CancellationTokenSource(); c.Cancel();
        Assert.Throws<OperationCanceledException>(() => EdgeFaceKernel.Solve([Square], [], new(1e-9), c.Token));
    }
    [Fact]
    public void CoincidentLociAreEmittedOnceWithVisiblePrecedenceAndStableOwnership()
    {
        CandidateEdge[] edges = [new(V(0, 0), V(3, 0), "z"), new(V(4, 0), V(1, 0), "a")];
        var result = new VisibilityResult([new(0, 0, 1, VisibilityClass.Hidden), new(1, 0, 1, VisibilityClass.Visible)], 0, 0, []);
        var loci = VisibilityLoci.Create(edges, result, .0001);
        Assert.Equal(3, loci.Where(l => l.Class == VisibilityClass.Visible).Sum(l => l.Length), 8);
        Assert.Equal(1, loci.Where(l => l.Class == VisibilityClass.Hidden).Sum(l => l.Length), 8);
        Assert.All(loci, l => Assert.Equal("a", l.Source));
        var reordered = VisibilityLoci.Union(loci.AsEnumerable().Reverse().ToArray(), .0001);
        Assert.Equal(loci.Sum(l => l.Length), reordered.Sum(l => l.Length), 8);
    }
    [Fact]
    public void NearParallelAndLargeOffsetPredicatesMatchExactOracle()
    {
        double o = 1e9;
        var f = new PlanarFace([[V(o, o, 1), V(o + 2, o, 1 + 1e-12), V(o + 2, o + 2, 1 + 1e-12), V(o, o + 2, 1)]]);
        Compare([f], [new(V(o - 1, o + 1, 0), V(o + 3, o + 1, 2), "near")]);
    }
    [Fact]
    public void UnrepresentableExactEndpointIsUnresolvedRatherThanConfidentlyRounded()
    {
        var f = new PlanarFace([[V(1e100, 0), V(2e100, 0), V(2e100, 2e100), V(1e100, 2e100)]]);
        var e = new CandidateEdge(V(0, 1e100, 0), V(3e100, 1e100, 0), "too-large");
        Assert.All(EdgeFaceKernel.Solve([f], [e], new(.0001)).Intervals, i => Assert.Equal(VisibilityClass.Unresolved, i.Class));
    }
    [Fact]
    public void ParallelReferenceStillVisitsEveryPairAndPreservesExactResults()
    {
        var edges = Enumerable.Range(0, 20).Select(i => new CandidateEdge(V(-1, i * .1, 0), V(3, i * .1, 0), i.ToString())).ToArray();
        var serial = ExactVisibilityOracle.Solve([Square, Square], edges, .0001);
        var parallel = ExactVisibilityOracle.Solve([Square, Square], edges, .0001, threads: 8);
        Assert.Equal(40, serial.FaceTests); Assert.Equal(40, parallel.FaceTests);
        Assert.Equal(serial.Intervals, parallel.Intervals);
    }
    [Fact]
    public void LongDivergingSupportsAreNeverDeduplicatedUsingOnlyTheirStarts()
    {
        Locus[] input = [new(0, 0, 1000, 0, VisibilityClass.Visible, "a"), new(0, 0, 1000, .001, VisibilityClass.Visible, "b")];
        var output = VisibilityLoci.Union(input, .0001);
        Assert.Equal(2, output.Length);
        Assert.True(VisibilityLoci.Compare(input, output, .0001, .1).Missed == 0);
    }
    [Fact]
    public void SubUlpDrawingToleranceQuarantinesEvenExactlyRepresentableParameters()
    {
        var e = new CandidateEdge(V(1e16, 0, 0), V(1e16 + 100, 0, 0), "offset");
        Assert.All(EdgeFaceKernel.Solve([], [e], new(.0001)).Intervals, i => Assert.Equal(VisibilityClass.Unresolved, i.Class));
    }
    [Fact]
    public void VisibleJunctionAbsorbsOnlyToleranceSmallHiddenResidues()
    {
        Locus[] input = [new(0, 0, 1, 0, VisibilityClass.Hidden, "a"),
            new(1e-15, 0, 1 - 1e-15, 0, VisibilityClass.Visible, "b"),
            new(.5, -1e-15, .5, 1e-15, VisibilityClass.Hidden, "corner"),
            new(.5, 0, .5, 1, VisibilityClass.Visible, "vertical"),
            new(10, 0, 10 + 1e-6, 0, VisibilityClass.Hidden, "c")];
        var output = VisibilityLoci.Union(input, .0001);
        Assert.Equal(2, output.Count(l => l.Class == VisibilityClass.Visible));
        Assert.Single(output, l => l.Class == VisibilityClass.Hidden);
        Assert.True(output.Single(l => l.Class == VisibilityClass.Hidden).X0 >= 10);
    }
    private static void Compare(PlanarFace[] faces, CandidateEdge[] edges)
    {
        var expected = ExactVisibilityOracle.Solve(faces, edges, .0001).Intervals;
        foreach (bool index in new[] { false, true })
        {
            var actual = EdgeFaceKernel.Solve(faces, edges, new(.0001, 2, index)).Intervals;
            Assert.Equal(expected.Count, actual.Count);
            for (int i = 0; i < expected.Count; i++)
            {
                Assert.Equal(expected[i].Edge, actual[i].Edge); Assert.Equal(expected[i].Class, actual[i].Class);
                Assert.Equal(expected[i].Start, actual[i].Start, 10); Assert.Equal(expected[i].End, actual[i].End, 10);
            }
        }
    }
}
