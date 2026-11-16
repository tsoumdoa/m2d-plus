using M2DPlus.Core.Visibility;
using Xunit;

namespace M2DPlus.Core.Tests;

/// <summary>
/// Cut tubes (planes and cylinders kept on one side) against an oracle that knows the solid only as a point-membership
/// test written out here, marched along the view ray. Larger Z is nearer the viewer.
/// </summary>
public sealed class AnalyticCsgTests
{
    private const double Tol = 1e-4;
    private const VisibilityClass Visible = VisibilityClass.Visible, Hidden = VisibilityClass.Hidden, Unresolved = VisibilityClass.Unresolved;
    private static Vertex V(double x, double y, double z) => new(x, y, z);

    /// <summary>The test's own description of a cut tube: independent of the engine's surface list.</summary>
    private sealed record Cut(Vertex O, Vertex U, double R, double Ri, (Vertex P, Vertex N)[] Planes, (Vertex O, Vertex U, double R)[] Cutters)
    {
        private static double Radial(Vertex x, Vertex o, Vertex u)
        {
            double wx = x.X - o.X, wy = x.Y - o.Y, wz = x.Z - o.Z, h = wx * u.X + wy * u.Y + wz * u.Z;
            double ax = wx - h * u.X, ay = wy - h * u.Y, az = wz - h * u.Z; return Math.Sqrt(ax * ax + ay * ay + az * az);
        }
        public bool Inside(Vertex x)
        {
            double r = Radial(x, O, U); if (r > R || r < Ri) return false;
            foreach (var (p, n) in Planes) if ((x.X - p.X) * n.X + (x.Y - p.Y) * n.Y + (x.Z - p.Z) * n.Z > 0) return false;
            foreach (var c in Cutters) if (Radial(x, c.O, c.U) < c.R) return false;
            return true;
        }
        /// <summary>Distance to the nearest bounding surface (not to the solid): small means the oracle is near a boundary.</summary>
        public double Clearance(Vertex x)
        {
            double r = Radial(x, O, U), d = Math.Min(Math.Abs(r - R), Ri > 0 ? Math.Abs(r - Ri) : double.MaxValue);
            foreach (var (p, n) in Planes) d = Math.Min(d, Math.Abs((x.X - p.X) * n.X + (x.Y - p.Y) * n.Y + (x.Z - p.Z) * n.Z));
            foreach (var c in Cutters) d = Math.Min(d, Math.Abs(Radial(x, c.O, c.U) - c.R));
            return d;
        }
        public AnalyticCsg Solid(double slack = 0)
        {
            var list = new List<CsgPrimitive> { new(true, O, U, R, true, slack) };
            if (Ri > 0) list.Add(new(true, O, U, Ri, false, slack));
            foreach (var (p, n) in Planes) list.Add(new(false, p, n, 0, true, slack));
            foreach (var c in Cutters) list.Add(new(true, c.O, c.U, c.R, false, slack));
            return new AnalyticCsg(list, V(O.X - 8, O.Y - 8, O.Z - 8), V(O.X + 8, O.Y + 8, O.Z + 8));
        }
    }

    private static Vertex Direction(Random random)
    {
        while (true)
        {
            double x = random.NextDouble() * 2 - 1, y = random.NextDouble() * 2 - 1, z = random.NextDouble() * 2 - 1, l = Math.Sqrt(x * x + y * y + z * z);
            if (l > .2 && l <= 1) return V(x / l, y / l, z / l);
        }
    }

    /// <summary>A tube about 5 long, capped by two tilted planes, coped by up to two cylinders that cross its axis.</summary>
    private static Cut Random(Random random, double offset = 0)
    {
        var o = V(offset + random.NextDouble() * 2 - 1, offset + random.NextDouble() * 2 - 1, random.NextDouble() * 2 - 1); var u = Direction(random);
        double r = .2 + .4 * random.NextDouble(), ri = random.Next(3) == 0 ? 0 : r * (.5 + .4 * random.NextDouble());
        Vertex Tilt(double sign)
        {
            var t = Direction(random); double k = .5 * random.NextDouble();
            double x = sign * u.X + k * t.X, y = sign * u.Y + k * t.Y, z = sign * u.Z + k * t.Z, l = Math.Sqrt(x * x + y * y + z * z); return V(x / l, y / l, z / l);
        }
        Vertex At(double h) => V(o.X + h * u.X, o.Y + h * u.Y, o.Z + h * u.Z);
        var planes = new[] { (At(-2.5), Tilt(-1)), (At(2.5), Tilt(1)) };
        var cutters = Enumerable.Range(0, random.Next(3)).Select(_ =>
        {
            var through = At(random.NextDouble() * 4 - 2); var d = Direction(random);
            return (V(through.X + .1 * (random.NextDouble() - .5), through.Y + .1 * (random.NextDouble() - .5), through.Z), d, r * (.6 + .8 * random.NextDouble()));
        }).ToArray();
        return new Cut(o, u, r, ri, planes, cutters);
    }

    /// <summary>Marches the ray in front of p. Returns the longest run of inside samples and the smallest clearance met.</summary>
    private static (int Longest, double Clearance) March(Cut cut, Vertex p, double step = 1e-3, double far = 12)
    {
        int run = 0, longest = 0; double clearance = double.MaxValue;
        for (double s = Tol + step; s < far; s += step)
        {
            var x = V(p.X, p.Y, p.Z + s); clearance = Math.Min(clearance, cut.Clearance(x));
            if (cut.Inside(x)) longest = Math.Max(longest, ++run); else run = 0;
        }
        return (longest, clearance);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1e3)]
    public void PointsAgreeWithTheMembershipOracle(double offset)
    {
        var random = new Random(31); int hidden = 0, visible = 0, unresolved = 0, skipped = 0;
        for (int trial = 0; trial < 30; trial++)
        {
            var cut = Random(random, offset); var solid = cut.Solid();
            for (int k = 0; k < 12; k++)
            {
                var p = V(cut.O.X + random.NextDouble() * 4 - 2, cut.O.Y + random.NextDouble() * 4 - 2, cut.O.Z - 4 + random.NextDouble() * 6);
                var answer = solid.Occlusion(p, Tol); var (longest, _) = March(cut, p);
                if (answer == Unresolved) { unresolved++; continue; }
                // Decisive only: 4 samples in a row (3 mm) is cover; none at all, with the four rays beside it also clear, is none.
                bool clear = longest == 0 && new[] { (2e-5, 0d), (-2e-5, 0d), (0d, 2e-5), (0d, -2e-5) }.All(o => March(cut, V(p.X + o.Item1, p.Y + o.Item2, p.Z)).Longest == 0);
                if (longest >= 4) { Assert.True(answer == Hidden, $"trial {trial}, point {k}: {longest} mm of cover called {answer}"); hidden++; }
                else if (clear) { Assert.True(answer == Visible, $"trial {trial}, point {k}: no cover called {answer}"); visible++; }
                else skipped++;
            }
        }
        Assert.True(hidden > 12 && visible > 180, $"hidden {hidden}, visible {visible}, unresolved {unresolved}, skipped {skipped}");
        Assert.True(unresolved < 10, $"unresolved {unresolved}");
    }

    [Fact]
    public void ACertificateHoldsForEveryRayInItsDiscAndDepthRange()
    {
        // If the solid gives one answer for a disc of rays, every ray sampled in the disc must have that answer.
        var random = new Random(77); int certified = 0, checkedRays = 0, undecided = 0;
        for (int trial = 0; trial < 40; trial++)
        {
            var cut = Random(random); var solid = cut.Solid();
            for (int k = 0; k < 12; k++)
            {
                var p = V(cut.O.X + random.NextDouble() * 3 - 1.5, cut.O.Y + random.NextDouble() * 3 - 1.5, cut.O.Z - 4 + random.NextDouble() * 5);
                double radius = Math.Pow(10, -1 - 3 * random.NextDouble()), spread = radius * random.NextDouble();
                var answer = solid.Evaluate(p, radius, p.Z - spread, p.Z + spread, Tol);
                if (answer == Unresolved) { undecided++; continue; }
                certified++;
                for (int j = 0; j < 6; j++)
                {
                    double angle = random.NextDouble() * 2 * Math.PI, rho = radius * Math.Sqrt(random.NextDouble());
                    var q = V(p.X + rho * Math.Cos(angle), p.Y + rho * Math.Sin(angle), p.Z + spread * (2 * random.NextDouble() - 1));
                    var (longest, _) = March(cut, q); checkedRays++;
                    if (answer == Hidden) Assert.True(longest >= 1, $"trial {trial}: certified Hidden over radius {radius}, but a ray in the disc has no cover");
                    else Assert.True(longest <= 1, $"trial {trial}: certified Visible over radius {radius}, but a ray in the disc has {longest} mm of cover");
                }
            }
        }
        Assert.True(certified > 120 && checkedRays > 700, $"certified {certified}, undecided {undecided}");
    }

    [Fact]
    public void IntervalsOfRandomLinesAgreeWithTheOracleAllAlongEachInterval()
    {
        var random = new Random(5150); int hidden = 0, visible = 0, skipped = 0, undecidedLength = 0, intervals = 0;
        for (int trial = 0; trial < 14; trial++)
        {
            var cuts = Enumerable.Range(0, 1 + random.Next(2)).Select(_ => Random(random)).ToArray();
            var scene = new AnalyticOccluders(cuts.Select(c => (AnalyticSolid)c.Solid()).ToArray());
            var a = V(random.NextDouble() * 6 - 3, random.NextDouble() * 6 - 3, -6 + random.NextDouble() * 4);
            var b = V(random.NextDouble() * 6 - 3, random.NextDouble() * 6 - 3, -6 + random.NextDouble() * 4);
            var candidate = AnalyticCandidate.Straight(a, b); var list = scene.Intervals(candidate, Tol);
            Assert.Equal(0, list[0].Start); Assert.Equal(1, list[^1].End);
            for (int i = 1; i < list.Count; i++) { Assert.Equal(list[i - 1].End, list[i].Start); Assert.NotEqual(list[i - 1].Class, list[i].Class); }
            double length = Math.Sqrt(Math.Pow(b.X - a.X, 2) + Math.Pow(b.Y - a.Y, 2) + Math.Pow(b.Z - a.Z, 2)); intervals += list.Count;
            foreach (var interval in list)
            {
                if (interval.Class == Unresolved) { if ((interval.End - interval.Start) * length > 2 * Tol) undecidedLength++; continue; }
                double margin = 5e-3 / length;
                for (double t = interval.Start + margin; t <= interval.End - margin; t += 8e-2 / length)
                {
                    var p = candidate.At(t); int longest = 0; bool clear = true;
                    foreach (var cut in cuts)
                    {
                        var (run, _) = March(cut, p, 2e-3); longest = Math.Max(longest, run);
                        if (run != 0 || new[] { (2e-5, 0d), (-2e-5, 0d), (0d, 2e-5), (0d, -2e-5) }.Any(o => March(cut, V(p.X + o.Item1, p.Y + o.Item2, p.Z), 2e-3).Longest != 0)) clear = false;
                    }
                    if (longest >= 3) { Assert.True(interval.Class == Hidden, $"trial {trial}: Visible interval {interval.Start}..{interval.End} has cover at t={t}"); hidden++; }
                    else if (clear) { Assert.True(interval.Class == Visible, $"trial {trial}: Hidden interval {interval.Start}..{interval.End} has no cover at t={t}"); visible++; }
                    else skipped++;
                }
            }
        }
        Assert.True(hidden > 12 && visible > 200, $"hidden {hidden}, visible {visible}, skipped {skipped}, intervals {intervals}");
        Assert.Equal(0, undecidedLength);   // nothing longer than two tolerances is left undecided on an exact line
    }

    [Fact]
    public void ACopedPipeAgainstAHandWorkedCase()
    {
        // A tube along X (outer 1, bore .8) from x = -3; its far end is coped by a cylinder of radius 1 along Y through
        // the origin. Top view. The tube's wall ends where it meets the cutter.
        var cut = new Cut(V(0, 0, 0), V(1, 0, 0), 1, .8, [(V(-3, 0, 0), V(-1, 0, 0)), (V(.5, 0, 0), V(1, 0, 0))], [(V(0, 0, 0), V(0, 1, 0), 1)]);
        var solid = cut.Solid(); var scene = new AnalyticOccluders([solid]);
        // Under the tube's crown (y = 0) the top wall holds z in [.8, 1], less what the cutter takes (x² + z² < 1):
        // z from max(.8, √(1 - x²)) to 1. That is the whole wall for |x| ≥ .6 and a sliver x²/2 thick near x = 0.
        Assert.Equal(Hidden, solid.Occlusion(V(-2, 0, -5), Tol)); Assert.Equal(Hidden, solid.Occlusion(V(-.7, 0, -5), Tol));
        Assert.Equal(Hidden, solid.Occlusion(V(-.3, 0, -5), Tol)); Assert.Equal(Hidden, solid.Occlusion(V(.2, 0, -5), Tol));
        Assert.Equal(Visible, solid.Occlusion(V(0, 0, -5), Tol));           // the cutter just reaches the crown
        Assert.Equal(Visible, solid.Occlusion(V(.6, 0, -5), Tol)); Assert.Equal(Visible, solid.Occlusion(V(-3.1, 0, -5), Tol));
        // Beside the bore (y = .9): solid wall from top to bottom, cut away where x² + z² < 1.
        Assert.Equal(Hidden, solid.Occlusion(V(-2, .9, -5), Tol)); Assert.Equal(Visible, solid.Occlusion(V(-2, 1.01, -5), Tol));
        // A line along the tube under its crown: Hidden from the end plane to the far plane, except about x = 0 where
        // the sliver is thinner than the depth rule's cover. x²/2 ≤ tolerance/16 (|x| ≤ 3.5 mm) is Visible,
        // x²/2 ≥ tolerance/8 (|x| ≥ 5 mm) is Hidden, and the 1.5 mm between is the rule's undecided window.
        var intervals = scene.Intervals(AnalyticCandidate.Straight(V(-4, 0, -5), V(2, 0, -5)), Tol);
        Assert.Equal([Visible, Hidden, Unresolved, Visible, Unresolved, Hidden, Visible], intervals.Select(i => i.Class));
        double X(double t) => t * 6 - 4; double inner = Math.Sqrt(Tol / 8), outer = Math.Sqrt(Tol / 4);
        Assert.InRange(X(intervals[1].Start), -3 - Tol, -3 + Tol); Assert.InRange(X(intervals[5].End), .5 - Tol, .5 + Tol);
        Assert.InRange(X(intervals[2].Start), -outer - Tol, -outer + Tol); Assert.InRange(X(intervals[2].End), -inner - Tol, -inner + Tol);
        Assert.InRange(X(intervals[4].Start), inner - Tol, inner + Tol); Assert.InRange(X(intervals[4].End), outer - Tol, outer + Tol);
    }

    [Fact]
    public void OwnLinesFollowTheSurfacesTheyLieOn()
    {
        // The same coped tube. Its outer outline (a ruling at y = 1, z = 0, tangent to the view) is not covered by
        // its own wall. Its bore outline (y = .8, z = 0) is under the wall. A point of its end rim on the top of the
        // tube (x = -3, y = 0, z = 1) has nothing above it; the same rim at the bottom (z = -1) has the whole tube.
        var cut = new Cut(V(0, 0, 0), V(1, 0, 0), 1, .8, [(V(-3, 0, 0), V(-1, 0, 0)), (V(.5, 0, 0), V(1, 0, 0))], [(V(0, 0, 0), V(0, 1, 0), 1)]);
        var solid = cut.Solid(); const int outer = 1 << 0, bore = 1 << 1, end = 1 << 2;
        Assert.Equal(Visible, solid.Occlusion(V(-2, 1, 0), Tol, outer, tangent: true));
        Assert.Equal(Hidden, solid.Occlusion(V(-2, .8, 0), Tol, bore, tangent: true));
        Assert.Equal(Visible, solid.Occlusion(V(-3, 0, 1), Tol, outer | end)); Assert.Equal(Hidden, solid.Occlusion(V(-3, 0, -1), Tol, outer | end));
        // Over a piece of the rim the certificate still holds: 1 mm of rim near the bottom, all Hidden.
        Assert.Equal(Hidden, solid.Evaluate(V(-3, 0, -1), 1e-3, -1, -1 + 1e-6, Tol, outer | end));
        // Near the outline the rim's own cover shrinks to nothing: a piece across it is not certified.
        Assert.Equal(Unresolved, solid.Evaluate(V(-3, 1, 0), 1e-2, -1e-2, 1e-2, Tol, outer | end));
    }

    [Fact]
    public void SlackWidensTheUndecidedBandAndNeverFlipsAnAnswer()
    {
        var random = new Random(9); int agreed = 0, widened = 0;
        for (int trial = 0; trial < 40; trial++)
        {
            var cut = Random(random); var exact = cut.Solid(); var loose = cut.Solid(slack: 5e-3);
            for (int k = 0; k < 40; k++)
            {
                var p = V(cut.O.X + random.NextDouble() * 3 - 1.5, cut.O.Y + random.NextDouble() * 3 - 1.5, cut.O.Z - 4 + random.NextDouble() * 5);
                var a = exact.Occlusion(p, Tol); var b = loose.Occlusion(p, Tol);
                if (b == Unresolved) { if (a != Unresolved) widened++; continue; }
                Assert.Equal(a, b); agreed++;
            }
        }
        Assert.True(agreed > 1000 && widened > 5, $"agreed {agreed}, widened {widened}");
    }

    [Fact]
    public void InvalidInputIsRefusedAndHugeCoordinatesAreUnresolved()
    {
        Assert.Throws<ArgumentException>(() => new AnalyticCsg([], V(0, 0, 0), V(1, 1, 1)));
        Assert.Throws<ArgumentException>(() => new AnalyticCsg([new(true, V(0, 0, 0), V(0, 0, 0), 1, true)], V(0, 0, 0), V(1, 1, 1)));
        Assert.Throws<ArgumentException>(() => new AnalyticCsg([new(true, V(0, 0, 0), V(1, 0, 0), -1, true)], V(0, 0, 0), V(1, 1, 1)));
        var far = new AnalyticCsg([new(true, V(1e13, 0, 0), V(1, 0, 0), 1, true)], V(1e13 - 5, -1, -1), V(1e13 + 5, 1, 1));
        Assert.Equal(Unresolved, far.Occlusion(V(1e13, 0, -5), Tol));
    }
}
