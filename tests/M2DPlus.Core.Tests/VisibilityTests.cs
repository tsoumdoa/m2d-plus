using M2DPlus.Core.Visibility;
using Xunit;

namespace M2DPlus.Core.Tests;

public sealed class VisibilityTests
{
    private static Vertex V(double x, double y, double z = 1) => new(x, y, z);
    private static PlanarFace Square => new([[V(0, 0), V(2, 0), V(2, 2), V(0, 2)]]);
    [Fact] public void IndependentPointOracleVisitsAllPairsAndRecordsSanctionedContacts()
    {
        var outside=new PlanarFace([[V(10,10),V(12,10),V(12,12),V(10,12)]]);
        var oracle=new ExactVisibilityOracle.AllPairsPoints([Square,outside],.125);
        var pairs=oracle.Prepare(0,0,2,2);
        Assert.Equal(2,oracle.CompletePairs);
        foreach(double z in new[]{1d,.875})
        {
            var result=oracle.At(V(1,1,z),pairs);
            Assert.Equal(VisibilityClass.Visible,result.Class);
            Assert.Equal(1-z,Assert.Single(result.Contacts).Gap);
        }
        Assert.Equal(VisibilityClass.Hidden,oracle.At(V(1,1,Math.BitDecrement(.875)),pairs).Class);
        Assert.Equal(3,oracle.ExactPointPairs);
    }
    [Fact] public void IndependentPointOraclePreservesHolesAndClosedBoundaryOcclusion()
    {
        var face=Square with {Loops=[Square.Loops[0],[V(.5,.5),V(1.5,.5),V(1.5,1.5),V(.5,1.5)]]};
        var oracle=new ExactVisibilityOracle.AllPairsPoints([face],.125);var pairs=oracle.Prepare(0,0,2,2);
        Assert.Equal(VisibilityClass.Visible,oracle.At(V(1,1,0),pairs).Class);
        var boundary=oracle.At(V(.5,1,0),pairs);
        Assert.Equal(VisibilityClass.Hidden,boundary.Class);Assert.Equal(0,boundary.BlockingFace);
    }
    [Fact]
    public void FilteredPointQueriesMatchAnalyticHoleBoundaryAndDepthContacts()
    {
        const double tol = 1e-6;
        var face = Square with { Loops = [Square.Loops[0], [V(.5,.5),V(1.5,.5),V(1.5,1.5),V(.5,1.5)]] };
        var query = new EdgeFaceKernel.PointQuery([face], tol);
        foreach (double x in new[] { -.1, 0, Math.BitIncrement(0d), .25, .5, 1, 1.5, 2, Math.BitIncrement(2d) })
        foreach (double y in new[] { 0d, .25, .5, 1, 1.5, 2 })
        foreach (double z in new[] { 0d, 1 - tol, Math.BitDecrement(1 - tol), 1d })
        {
            var p = V(x,y,z);
            bool inMaterial = x >= 0 && x <= 2 && y >= 0 && y <= 2 && !(x > .5 && x < 1.5 && y > .5 && y < 1.5);
            var expected = inMaterial && 1 - z > tol ? VisibilityClass.Hidden : VisibilityClass.Visible;
            Assert.Equal(expected, query.Classify(p));
        }
    }

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
    public void SubnormalProjectedAreaMustNotBeRoundedToAnEdgeOnFace()
    {
        // A binary64-thin polygon still has nonzero exact area. Its closed
        // boundary occludes; coefficient seeding must certify it or fall back.
        var points = new[] { V(0, 0), V(2, 0), V(2, double.Epsilon), V(0, double.Epsilon) };
        CandidateEdge[] edges = [new(V(.5, 0, 0), V(1.5, 0, 0), "subnormal")];
        foreach (var loop in new[] { points, points.AsEnumerable().Reverse().ToArray() })
        {
            PlanarFace[] faces = [new([loop])];
            Compare(faces, edges);
            var span = Assert.Single(EdgeFaceKernel.Solve(faces, edges, new(.0001)).Intervals);
            Assert.Equal(VisibilityClass.Hidden, span.Class);
            Assert.Equal(0, span.Start); Assert.Equal(1, span.End);
        }
    }
    [Theory]
    [InlineData(1e-137, 1e-150)]
    [InlineData(1, 1e-13)]
    [InlineData(1e137, 1e124)]
    public void ScaledParallelSupportsKeepTheirSeparateVisibility(double scale, double tolerance)
    {
        PlanarFace[] faces = [new([[V(-scale, .5 * scale, scale), V(2 * scale, .5 * scale, scale),
            V(2 * scale, 1.5 * scale, scale), V(-scale, 1.5 * scale, scale)]])];
        CandidateEdge[] edges = [new(V(0, 0, 0), V(scale, 0, 0), "visible"),
            new(V(0, scale, 0), V(scale, scale, 0), "hidden")];
        var result = EdgeFaceKernel.Solve(faces, edges, new(tolerance));
        Assert.Equal(VisibilityClass.Visible, Assert.Single(result.Intervals, i => i.Edge == 0).Class);
        Assert.Equal(VisibilityClass.Hidden, Assert.Single(result.Intervals, i => i.Edge == 1).Class);
        var loci = VisibilityLoci.Create(edges, result, tolerance);
        Assert.Equal(2, loci.Length);
        Assert.Single(loci, l => l.Class == VisibilityClass.Visible);
        Assert.Single(loci, l => l.Class == VisibilityClass.Hidden);
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

    [Fact] public void PointQueriesUseClosedBoundariesStrictDepthAndHoles()
    {
        var face = new PlanarFace([[new(-2,-2,1), new(2,-2,1), new(2,2,1), new(-2,2,1)],
            [new(-1,-1,1),new(1,-1,1),new(1,1,1),new(-1,1,1)]]);
        var query = new EdgeFaceKernel.PointQuery([face], .125);
        Assert.Equal(VisibilityClass.Visible, query.Classify(new(0,0,0)));
        Assert.Equal(VisibilityClass.Hidden, query.Classify(new(2,0,0)));
        Assert.Equal(VisibilityClass.Visible, query.Classify(new(1.5,0,.875)));
        Assert.Equal(VisibilityClass.Hidden, query.Classify(new(1.5,0,.874)));
    }
    [Fact] public void OwnedUnionNeverAssignsDisjointExtensionsToAnUnrelatedSource()
    {
        var union = VisibilityLoci.Union([new(0,0,1,0,VisibilityClass.Visible,"a"),
            new(1,0,2,0,VisibilityClass.Visible,"b")], 1e-6, preserveOwnership: true);
        Assert.Equal("a", union.Single(l => l.X0 == 0).Source);
        Assert.Equal("b", union.Single(l => l.X1 == 2).Source);
    }

    [Fact] public void UnrepresentablePointCoordinatesRemainUnresolvedEvenWithoutOccluders()
    {
        var query = new EdgeFaceKernel.PointQuery([], 1e-4);
        Assert.Equal(VisibilityClass.Unresolved, query.Classify(new(1e151,0,0)));
        Assert.Equal(VisibilityClass.Unresolved, query.Classify(new(1e14,0,0)));
    }

    [Fact] public void RoundedDepthBoundaryNeedsAnIntervalCertificateRatherThanAWiderDepthTolerance()
    {
        const double tolerance = 1e-4;
        var face = new PlanarFace([[V(-1,-1,-1),V(1,-1,1),V(1,1,1),V(-1,1,-1)]]);
        var edge = new CandidateEdge(V(0,0,0),V(1,0,0),"boundary");
        var oracle = new ExactVisibilityOracle.AllPairsPoints([face], tolerance);
        var pairs = oracle.Prepare(0,0,1,0);
        double rounded = Math.BitIncrement(tolerance);
        Assert.Equal(VisibilityClass.Hidden, oracle.OnEdge(edge,rounded,pairs).Class);
        var visible = Assert.Single(ExactVisibilityOracle.Solve([face],[edge],tolerance).Intervals,
            interval => interval.Class == VisibilityClass.Visible);
        Assert.Equal(tolerance, visible.End);
        Assert.InRange(rounded-visible.End,0,tolerance);
        Assert.Equal(VisibilityClass.Visible, oracle.OnEdge(edge,visible.End/2,pairs).Class);
    }
}
