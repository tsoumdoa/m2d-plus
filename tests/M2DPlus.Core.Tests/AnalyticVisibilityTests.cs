using M2DPlus.Core.Visibility;
using Xunit;

namespace M2DPlus.Core.Tests;

/// <summary>
/// Exact analytic occluders against truth worked out by hand, and against a brute-force reference that knows a solid
/// only as a point-membership test (no roots, no events). Larger Z is nearer the viewer.
/// </summary>
public sealed class AnalyticVisibilityTests
{
    private const double Tol = 1e-4;
    private const VisibilityClass Visible = VisibilityClass.Visible, Hidden = VisibilityClass.Hidden, Unresolved = VisibilityClass.Unresolved;
    private static Vertex V(double x, double y, double z) => new(x, y, z);
    private static AnalyticOccluders Scene(params AnalyticSolid[] solids) => new(solids);

    /// <summary>A cylinder of radius r along Y from y0 to y1 at (x, z); caps square to the axis.</summary>
    private static AnalyticCylinder Column(double x, double z, double r, double y0, double y1, double inner = 0) =>
        new(V(x, 0, z), V(0, 1, 0), r, inner, [(V(x, y0, z), V(0, -1, 0)), (V(x, y1, z), V(0, 1, 0))]);

    private static void Ordered(List<AnalyticInterval> intervals)
    {
        Assert.Equal(0, intervals[0].Start); Assert.Equal(1, intervals[^1].End);
        for (int i = 1; i < intervals.Count; i++) { Assert.Equal(intervals[i - 1].End, intervals[i].Start); Assert.NotEqual(intervals[i - 1].Class, intervals[i].Class); }
    }

    [Fact]
    public void LineBehindASphereIsHiddenBetweenTheOutlineCrossings()
    {
        var scene = Scene(new AnalyticSphere(V(0, 0, 0), 1));
        foreach (double y in new[] { 0, .3, .9, .999 })
        {
            var intervals = scene.Intervals(AnalyticCandidate.Straight(V(-3, y, -5), V(3, y, -5)), Tol);
            Ordered(intervals);
            Assert.Equal([Visible, Hidden, Visible], intervals.Select(i => i.Class));
            double half = Math.Sqrt(1 - y * y);
            // Change points on the outline, to a quarter of a tolerance along the 6-long line.
            Assert.InRange(intervals[1].Start * 6 - 3, -half - Tol / 4, -half + Tol / 4);
            Assert.InRange(intervals[1].End * 6 - 3, half - Tol / 4, half + Tol / 4);
        }
        Assert.Equal(Visible, Assert.Single(scene.Intervals(AnalyticCandidate.Straight(V(-3, 1.5, -5), V(3, 1.5, -5)), Tol)).Class);
        Assert.Equal(Visible, Assert.Single(scene.Intervals(AnalyticCandidate.Straight(V(-3, 0, 5), V(3, 0, 5)), Tol)).Class);
    }

    [Fact]
    public void SphereTieRuleContactIsVisibleAndPenetrationIsHidden()
    {
        var sphere = new AnalyticSphere(V(0, 0, 0), 1);
        Assert.Equal(Visible, sphere.Occlusion(V(0, 0, 1), Tol));                       // on the front surface
        Assert.Equal(Visible, sphere.Occlusion(V(0, 0, 1 - Tol * .9), Tol));            // less than a tolerance under it
        Assert.Equal(Hidden, sphere.Occlusion(V(0, 0, 1 - Tol * 1.2), Tol));            // more than a tolerance under it
        Assert.Equal(Hidden, sphere.Occlusion(V(0, 0, -1), Tol));                       // on the back surface
        Assert.Equal(Hidden, sphere.Occlusion(V(.6, 0, -.8), Tol, AnalyticSolid.Outer)); // an own point on the back
        Assert.Equal(Visible, sphere.Occlusion(V(.6, 0, .8), Tol, AnalyticSolid.Outer)); // an own point on the front
        Assert.Equal(Visible, sphere.Occlusion(V(1, 0, 0), Tol, AnalyticSolid.Outer, tangent: true));
        // Sideways the ball is closed: a ray along its outline is covered (the outline carries the ball's own line),
        // one two graze widths outside it is not, and between the two nothing is claimed.
        Assert.Equal(Hidden, sphere.Occlusion(V(1, 0, -3), Tol));
        Assert.Equal(Hidden, sphere.Occlusion(V(1 - 1e-9, 0, -3), Tol));
        Assert.Equal(Hidden, sphere.Occlusion(V(1 - Tol, 0, -3), Tol));
        Assert.Equal(Unresolved, sphere.Occlusion(V(1 + 1.5 * AnalyticSolid.Graze(Tol), 0, -3), Tol));
        Assert.Equal(Visible, sphere.Occlusion(V(1 + 4 * AnalyticSolid.Graze(Tol), 0, -3), Tol));
        // A touch at the point's own depth is still contact: the depth rule decides.
        Assert.Equal(Visible, sphere.Occlusion(V(1, 0, 0), Tol));
        Assert.Equal(Visible, sphere.Occlusion(V(1, 0, Tol / 2), Tol));
        // The same far from the origin: the sideways test does not depend on the size of the coordinates.
        var far = new AnalyticSphere(V(1e5, 1e5, 0), 1);
        Assert.Equal(Hidden, far.Occlusion(V(1e5 + 1, 1e5, -300), Tol));
        Assert.Equal(Visible, far.Occlusion(V(1e5 + 1 + 1e-8, 1e5, -300), Tol));
        Assert.Equal(Hidden, far.Occlusion(V(1e5 + 1 - 1e-8, 1e5, -300), Tol));
        // A line running exactly along the outline of a nearer sphere is covered over the sphere's height.
        Assert.Equal(Hidden, Assert.Single(Scene(sphere).Intervals(AnalyticCandidate.Straight(V(1, 0, -3), V(1, 0, -2)), Tol)).Class);
    }

    [Fact]
    public void TheUndecidedWindowIsNarrowAndNeverGivesAWrongAnswer()
    {
        var sphere = new AnalyticSphere(V(0, 0, 0), 1);
        // Depth under the front surface from 1.00 to 1.20 tolerances: Visible, then at most Unresolved, then Hidden.
        var seen = new List<VisibilityClass>();
        for (int k = 0; k <= 200; k++)
        {
            double under = Tol * (1 + k / 1000.0); var c = sphere.Occlusion(V(0, 0, 1 - under), Tol);
            if (under <= Tol * 1.06) Assert.Equal(Visible, c);
            if (under >= Tol * 1.13) Assert.Equal(Hidden, c);
            if (seen.Count == 0 || seen[^1] != c) seen.Add(c);
        }
        Assert.Equal([Visible, Unresolved, Hidden], seen);
    }

    [Fact]
    public void CylinderHidesWhatIsBehindItAndItsCapsBoundIt()
    {
        var scene = Scene(Column(0, 0, .5, -1, 1));
        var behind = scene.Intervals(AnalyticCandidate.Straight(V(-2, 0, -3), V(2, 0, -3)), Tol);
        Ordered(behind);
        Assert.Equal([Visible, Hidden, Visible], behind.Select(i => i.Class));
        Assert.InRange(behind[1].Start * 4 - 2, -.5 - Tol / 4, -.5 + Tol / 4);
        Assert.InRange(behind[1].End * 4 - 2, .5 - Tol / 4, .5 + Tol / 4);
        // Above the top cap, in front, and a vertical line that leaves the cylinder's height.
        Assert.Equal(Visible, Assert.Single(scene.Intervals(AnalyticCandidate.Straight(V(-2, 1.2, -3), V(2, 1.2, -3)), Tol)).Class);
        Assert.Equal(Visible, Assert.Single(scene.Intervals(AnalyticCandidate.Straight(V(-2, 0, 3), V(2, 0, 3)), Tol)).Class);
        var vertical = scene.Intervals(AnalyticCandidate.Straight(V(.2, -2, -3), V(.2, 2, -3)), Tol);
        Assert.Equal([Visible, Hidden, Visible], vertical.Select(i => i.Class));
        Assert.InRange(vertical[1].Start * 4 - 2, -1 - Tol / 4, -1 + Tol / 4);
        Assert.InRange(vertical[1].End * 4 - 2, 1 - Tol / 4, 1 + Tol / 4);
    }

    [Fact]
    public void ObliqueViewOfACylinderMatchesTheRayThroughItsCaps()
    {
        // Axis tilted 45° towards the viewer: u = (0, 1, 1)/√2, radius 0.5, from s = -1 to s = 1 along the axis.
        double k = Math.Sqrt(.5); var u = V(0, k, k);
        var solid = new AnalyticCylinder(V(0, 0, 0), u, .5, 0, [(V(0, -k, -k), V(0, -k, -k)), (V(0, k, k), V(0, k, k))]);
        // Truth by hand for a ray x = 0, y = y0: inside the cylinder for |y0·k - z·k| ≤ 0.5 and between the caps for
        // |y0·k + z·k| ≤ 1, so z from max(y0 - .5/k, -1/k - y0) to min(y0 + .5/k, 1/k - y0).
        foreach (double y0 in new[] { -1.2, -.9, -.3, 0, .4, .9, 1.2, 1.6 })
        {
            double lo = Math.Max(y0 - .5 / k, -1 / k - y0), hi = Math.Min(y0 + .5 / k, 1 / k - y0);
            foreach (double z in new[] { -4, lo - .01, lo + .01, (lo + hi) / 2, hi - .01, hi + .01, 4 })
            {
                bool truth = hi > lo && hi - Math.Max(lo, z + Tol) > Tol;
                Assert.Equal(truth ? Hidden : Visible, solid.Occlusion(V(0, y0, z), Tol));
            }
        }
    }

    [Fact]
    public void OwnRimIsHiddenBehindTheWallAndVisibleInFrontWithTheChangeAtTheOutline()
    {
        // Seen from the side and from above: axis along Y, view direction tilted so that the top cap faces the viewer.
        double tilt = Math.PI / 6; var u = V(0, Math.Cos(tilt), Math.Sin(tilt));
        var top = V(0, Math.Cos(tilt), Math.Sin(tilt)); var bottom = V(0, -Math.Cos(tilt), -Math.Sin(tilt));
        var solid = new AnalyticCylinder(V(0, 0, 0), u, .5, 0, [(bottom, bottom), (top, top)]);
        var x = V(1, 0, 0); var y = V(0, -Math.Sin(tilt), Math.Cos(tilt));   // across the axis; y points to the viewer
        Vertex Rim(Vertex centre, double angle) => V(centre.X + .5 * (Math.Cos(angle) * x.X + Math.Sin(angle) * y.X),
            centre.Y + .5 * (Math.Cos(angle) * x.Y + Math.Sin(angle) * y.Y), centre.Z + .5 * (Math.Cos(angle) * x.Z + Math.Sin(angle) * y.Z));
        for (int degrees = 0; degrees < 360; degrees += 5)
        {
            double angle = degrees * Math.PI / 180;
            // Top rim: the cap faces the viewer, so the whole rim is visible.
            Assert.Equal(Visible, solid.Occlusion(Rim(top, angle), Tol, AnalyticSolid.Outer));
            // Bottom rim: visible on the near half (sin > 0), hidden behind the wall on the far half.
            if (degrees % 180 == 0) continue; // the outline itself: the tie window
            Assert.Equal(Math.Sin(angle) > 0 ? Visible : Hidden, solid.Occlusion(Rim(bottom, angle), Tol, AnalyticSolid.Outer));
        }
        // Along the bottom rim the change sits at the outline: chord through the body 2·r·sin|angle|·cos(tilt)… > tolerance.
        var piece = new AnalyticCandidate(Rim(bottom, -.02), Rim(bottom, 0), Rim(bottom, .02), 0, AnalyticSolid.Outer);
        var intervals = Scene(solid).Intervals(piece, Tol);
        Ordered(intervals);
        Assert.Equal([Hidden, Visible], intervals.Select(i => i.Class));
        Assert.InRange(intervals[0].End, .49, .51);
        // The outline of the body itself never hides behind the body.
        Assert.Equal(Visible, solid.Occlusion(Rim(V(0, 0, 0), 0), Tol, AnalyticSolid.Outer, tangent: true));
    }

    [Fact]
    public void HollowCylinderIsSeenThroughAlongItsBoreOnly()
    {
        // Axis towards the viewer: a ring of radii 0.6 and 1 in the drawing, from z = -1 to z = 1.
        var tube = new AnalyticCylinder(V(0, 0, 0), V(0, 0, 1), 1, .6, [(V(0, 0, -1), V(0, 0, -1)), (V(0, 0, 1), V(0, 0, 1))]);
        Assert.Equal(Visible, tube.Occlusion(V(.3, 0, -5), Tol));    // through the bore
        Assert.Equal(Hidden, tube.Occlusion(V(.8, 0, -5), Tol));     // behind the wall
        Assert.Equal(Visible, tube.Occlusion(V(1.2, 0, -5), Tol));   // beside it
        Assert.Equal(Visible, tube.Occlusion(V(.8, 0, 1), Tol));     // on the near annulus
        Assert.Equal(Hidden, tube.Occlusion(V(.8, 0, -1), Tol));     // on the far annulus
        var across = Scene(tube).Intervals(AnalyticCandidate.Straight(V(-2, 0, -5), V(2, 0, -5)), Tol);
        Ordered(across);
        Assert.Equal([Visible, Hidden, Visible, Hidden, Visible], across.Select(i => i.Class));
        foreach (var (index, x) in new[] { (1, -1.0), (2, -.6), (3, .6), (4, 1.0) })
            Assert.InRange(across[index].Start * 4 - 2, x - Tol / 4, x + Tol / 4);
        // Seen from the side a hollow tube hides like a solid one: the ray always crosses a wall.
        var side = Column(0, 0, 1, -1, 1, inner: .6);
        Assert.Equal(Hidden, side.Occlusion(V(0, 0, -5), Tol));
        Assert.Equal(Hidden, side.Occlusion(V(.3, 0, 0), Tol));      // inside the bore, behind the near wall
        // A point on the bore's outline, own and tangent: behind the near wall, not seen through the bore.
        Assert.Equal(Hidden, side.Occlusion(V(.6, 0, 0), Tol, AnalyticSolid.Inner, tangent: true));
        Assert.Equal(Visible, side.Occlusion(V(1, 0, 0), Tol, AnalyticSolid.Outer, tangent: true));
    }

    [Fact]
    public void ACapSeenEdgeOnBelongsToTheSolid()
    {
        var solid = Column(0, 0, .5, -1, 1);   // top cap at y = 1, seen exactly edge-on
        Assert.Equal(Hidden, solid.Occlusion(V(0, 1, -3), Tol));                  // a ray in the cap plane
        Assert.Equal(Hidden, solid.Occlusion(V(0, 1 + 4e-11, -3), Tol));          // level with it to rounding
        Assert.Equal(Hidden, solid.Occlusion(V(0, 1 - 1e-9, -3), Tol));           // under it
        Assert.Equal(Unresolved, solid.Occlusion(V(0, 1 + 1.5 * AnalyticSolid.Graze(Tol), -3), Tol));    // in the window
        Assert.Equal(Visible, solid.Occlusion(V(0, 1 + 4 * AnalyticSolid.Graze(Tol), -3), Tol)); // above it
        // Its own top rim lies in that plane: the far half is behind the body, the near half in front of it. Both
        // project onto the same line, so the drawing shows it Visible.
        for (int degrees = 15; degrees < 360; degrees += 15)
        {
            if (degrees == 180) continue;
            double angle = degrees * Math.PI / 180;
            Assert.Equal(Math.Sin(angle) > 0 ? Visible : Hidden, solid.Occlusion(V(.5 * Math.Cos(angle), 1, .5 * Math.Sin(angle)), Tol, AnalyticSolid.Outer));
        }
        // Two columns stacked flush: nothing behind them shows through the joint at y = 1.
        var stacked = Scene(Column(0, 0, .5, -1, 1), Column(0, 0, .5, 1, 3));
        Assert.Equal(Hidden, Assert.Single(stacked.Intervals(AnalyticCandidate.Straight(V(-.3, 1, -3), V(.3, 1, -3)), Tol)).Class);
        Assert.Equal(Hidden, Assert.Single(stacked.Intervals(AnalyticCandidate.Straight(V(.2, -.5, -3), V(.2, 2.5, -3)), Tol)).Class);
    }

    [Fact]
    public void AlignedIdenticalMembersShareAnOutlineWithoutHidingIt()
    {
        var scene = Scene(Column(0, 0, .25, -1, 1), Column(0, -4, .25, -1, 1));
        // The rear column's outline lies exactly on the front column's outline in the drawing: covered by the front
        // column, whose own outline is the line drawn there. The front outline itself is Visible.
        var outline = new AnalyticCandidate(V(.25, -1, -4), V(.25, 0, -4), V(.25, 1, -4), 1, AnalyticSolid.Outer, Tangent: true);
        Assert.Equal(Hidden, Assert.Single(scene.Intervals(outline, Tol)).Class);
        var front = new AnalyticCandidate(V(.25, -1, 0), V(.25, 0, 0), V(.25, 1, 0), 0, AnalyticSolid.Outer, Tangent: true);
        Assert.Equal(Visible, Assert.Single(scene.Intervals(front, Tol)).Class);
        // Points of the rear column are behind the front one, its rims level with the front caps included.
        Assert.Equal(Hidden, scene.Classify(V(0, .9, -4 + .25), Tol));
        Assert.Equal(Hidden, scene.Classify(V(.1, -.9, -4 - Math.Sqrt(.25 * .25 - .01)), Tol));
        Assert.Equal(Hidden, scene.Classify(V(0, 1, -4 + .25), Tol));
        // A slightly thinner column in front no longer covers the rear outline; a slightly thicker one does.
        Assert.Equal(Visible, Scene(Column(0, 0, .25 - 1e-3, -1, 1)).Classify(V(.25, 0, -4), Tol));
        Assert.Equal(Hidden, Scene(Column(0, 0, .25 + 1e-3, -1, 1)).Classify(V(.25, 0, -4), Tol));
    }

    [Fact]
    public void CylinderSheetCoversOnlyBehindItsOwnExtent()
    {
        // A quarter cylinder, axis along Y, radius 1, angles 0..90° measured from +X towards +Z (the viewer), heights -1..1.
        var sheet = new AnalyticCylinderSheet(V(0, 0, 0), V(0, 1, 0), V(1, 0, 0), 1, 0, Math.PI / 2, -1, 1);
        // y = u × x = (0,1,0) × (1,0,0) = (0,0,-1): angles run from +X towards -Z, away from the viewer.
        Assert.Equal(Hidden, sheet.Occlusion(V(.5, 0, -3), Tol));      // behind the sheet (x in 0..1, far side)
        Assert.Equal(Visible, sheet.Occlusion(V(-.5, 0, -3), Tol));    // behind the missing part
        Assert.Equal(Visible, sheet.Occlusion(V(.5, 1.5, -3), Tol));   // above it
        Assert.Equal(Visible, sheet.Occlusion(V(.5, 0, 0), Tol));      // in front of it
        Assert.Equal(Hidden, sheet.Occlusion(V(1, 0, -3), Tol));       // behind its outline: the sheet is closed
        Assert.Equal(Visible, sheet.Occlusion(V(1 + 4 * AnalyticSolid.Graze(Tol), 0, -3), Tol)); // just beside it
        Assert.Equal(Visible, sheet.Occlusion(V(.5, 0, -Math.Sqrt(.75)), Tol, AnalyticSolid.Outer)); // an own point
        var behind = Scene(sheet).Intervals(AnalyticCandidate.Straight(V(-2, 0, -3), V(2, 0, -3)), Tol);
        Ordered(behind);
        Assert.Equal([Visible, Hidden, Visible], behind.Select(i => i.Class));
        Assert.InRange(behind[1].Start * 4 - 2, -Tol / 4, Tol / 4);
        Assert.InRange(behind[1].End * 4 - 2, 1 - Tol / 4, 1 + Tol / 4);
        // A half cylinder opening towards the viewer hides its own far side nowhere; one opening away covers it.
        var open = new AnalyticCylinderSheet(V(0, 0, 0), V(0, 1, 0), V(1, 0, 0), 1, 0, Math.PI, -1, 1);
        Assert.Equal(Hidden, open.Occlusion(V(0, 0, -3), Tol));
        Assert.Equal(Visible, open.Occlusion(V(0, 0, -.5), Tol));
        var closed = new AnalyticCylinderSheet(V(0, 0, 0), V(0, 1, 0), V(1, 0, 0), 1, Math.PI, Math.PI, -1, 1);
        Assert.Equal(Hidden, closed.Occlusion(V(0, 0, -.5), Tol));     // inside the trough, behind the near half
        Assert.Equal(Visible, closed.Occlusion(V(0, 0, 2), Tol));
    }

    // ---- Brute force: a solid known only by membership, sampled along the ray.

    private abstract record Shape { public abstract bool Inside(double x, double y, double z); public abstract AnalyticSolid Solid { get; } public abstract double Reach { get; } }
    private sealed record Ball(Vertex C, double R) : Shape
    {
        public override bool Inside(double x, double y, double z) => (x - C.X) * (x - C.X) + (y - C.Y) * (y - C.Y) + (z - C.Z) * (z - C.Z) <= R * R;
        public override AnalyticSolid Solid => new AnalyticSphere(C, R);
        public override double Reach => R;
    }
    private sealed record Pipe(Vertex O, Vertex U, double R, double Ri, double H0, double H1, Vertex N0, Vertex N1) : Shape
    {
        public override bool Inside(double x, double y, double z)
        {
            double wx = x - O.X, wy = y - O.Y, wz = z - O.Z, h = wx * U.X + wy * U.Y + wz * U.Z;
            double ax = wx - h * U.X, ay = wy - h * U.Y, az = wz - h * U.Z, r2 = ax * ax + ay * ay + az * az;
            if (r2 > R * R || r2 < Ri * Ri) return false;
            // Cap planes through the axis points at H0 and H1 with outward normals N0, N1.
            double d0 = (wx - H0 * U.X) * N0.X + (wy - H0 * U.Y) * N0.Y + (wz - H0 * U.Z) * N0.Z;
            double d1 = (wx - H1 * U.X) * N1.X + (wy - H1 * U.Y) * N1.Y + (wz - H1 * U.Z) * N1.Z;
            return d0 <= 0 && d1 <= 0;
        }
        public override AnalyticSolid Solid => new AnalyticCylinder(O, U, R, Ri,
            [(V(O.X + H0 * U.X, O.Y + H0 * U.Y, O.Z + H0 * U.Z), N0), (V(O.X + H1 * U.X, O.Y + H1 * U.Y, O.Z + H1 * U.Z), N1)]);
        public override double Reach => Math.Max(Math.Abs(H0), Math.Abs(H1)) + 3 * R;
    }

    private static Vertex Direction(Random random)
    {
        while (true)
        {
            double x = random.NextDouble() * 2 - 1, y = random.NextDouble() * 2 - 1, z = random.NextDouble() * 2 - 1, l = Math.Sqrt(x * x + y * y + z * z);
            if (l > .2 && l <= 1) return V(x / l, y / l, z / l);
        }
    }

    private static Shape Random(Random random, double offset)
    {
        var o = V(offset + random.NextDouble() * 4 - 2, offset + random.NextDouble() * 4 - 2, random.NextDouble() * 4 - 2);
        double r = .1 + random.NextDouble();
        if (random.Next(3) == 0) return new Ball(o, r);
        var u = Direction(random); double h0 = -.5 - random.NextDouble() * 2, h1 = .5 + random.NextDouble() * 2;
        // Caps tilted up to about 30° from square, which cannot meet inside a cylinder this long.
        Vertex Tilt(double sign)
        {
            var t = Direction(random); double k = .5 * random.NextDouble();
            double x = sign * u.X + k * t.X, y = sign * u.Y + k * t.Y, z = sign * u.Z + k * t.Z, l = Math.Sqrt(x * x + y * y + z * z);
            return V(x / l, y / l, z / l);
        }
        return new Pipe(o, u, r, random.Next(2) == 0 ? 0 : r * (.3 + .5 * random.NextDouble()), h0 - r, h1 + r, Tilt(-1), Tilt(1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1e3)]
    [InlineData(1e6)]
    public void RandomSolidsAgreeWithABruteForceMembershipReference(double offset)
    {
        var random = new Random(20261116); int hidden = 0, visible = 0, unresolved = 0, skipped = 0;
        for (int trial = 0; trial < 50; trial++)
        {
            var shape = Random(random, offset); var solid = shape.Solid;
            for (int k = 0; k < 20; k++)
            {
                var p = V(offset + random.NextDouble() * 6 - 3, offset + random.NextDouble() * 6 - 3, random.NextDouble() * 12 - 8);
                // Sample the ray in front of p every millimetre; count the longest inside run.
                const double step = 1e-3; double far = 12 + shape.Reach; int run = 0, longest = 0;
                for (double s = Tol + step; s < far; s += step)
                {
                    if (shape.Inside(p.X, p.Y, p.Z + s)) longest = Math.Max(longest, ++run); else run = 0;
                }
                var answer = solid.Occlusion(p, Tol);
                if (answer == Unresolved) { unresolved++; continue; }
                // Decisive only: a run of 4 samples (3 mm or more) is cover. No sample inside on this ray and on four
                // rays beside it leaves only a ray grazing an outline or rim within micrometres; those are skipped.
                if (longest >= 4) { Assert.Equal(Hidden, answer); hidden++; }
                else if (longest == 0 && shape.Inside(p.X, p.Y, p.Z + Tol) == false && Clear(shape, p, far)) { Assert.Equal(Visible, answer); visible++; }
                else skipped++;
            }
        }
        // The reference must have teeth: plenty of both answers, few undecided.
        Assert.True(hidden > 40 && visible > 600, $"hidden {hidden}, visible {visible}, unresolved {unresolved}, skipped {skipped}");
        Assert.True(unresolved < 20, $"unresolved {unresolved}");
    }

    /// <summary>No sample inside on four rays 20 µm to each side either: the ray is not grazing anything.</summary>
    private static bool Clear(Shape shape, Vertex p, double far)
    {
        foreach (var (dx, dy) in new[] { (2e-5, 0d), (-2e-5, 0d), (0d, 2e-5), (0d, -2e-5) })
            for (double s = Tol; s < far; s += 1e-3)
                if (shape.Inside(p.X + dx, p.Y + dy, p.Z + s)) return false;
        return true;
    }

    [Fact]
    public void IntervalsOfRandomLinesAgreeWithTheMembershipReferenceAllAlongEachInterval()
    {
        // The oracle here is independent of the engine: the solids are known only as point-membership tests, marched
        // along the view ray. Every interval is sampled every 5 cm along its whole length (not only at its middle),
        // away from its ends by 5 mm. A Hidden interval must have cover on every sample; a Visible one must have none.
        var random = new Random(4242); int hidden = 0, visible = 0, skipped = 0, undecided = 0;
        for (int trial = 0; trial < 24; trial++)
        {
            var shapes = Enumerable.Range(0, 1 + random.Next(3)).Select(_ => Random(random, 0)).ToArray(); var scene = Scene(shapes.Select(s => s.Solid).ToArray());
            var a = V(random.NextDouble() * 8 - 4, random.NextDouble() * 8 - 4, random.NextDouble() * 6 - 8);
            var b = V(random.NextDouble() * 8 - 4, random.NextDouble() * 8 - 4, random.NextDouble() * 6 - 8);
            var candidate = AnalyticCandidate.Straight(a, b); var intervals = scene.Intervals(candidate, Tol); Ordered(intervals);
            double length = Math.Sqrt(Math.Pow(b.X - a.X, 2) + Math.Pow(b.Y - a.Y, 2) + Math.Pow(b.Z - a.Z, 2)), far = 12 + shapes.Max(s => s.Reach);
            foreach (var interval in intervals)
            {
                if (interval.Class == Unresolved) { undecided++; continue; }
                double margin = 5e-3 / length, from = interval.Start + margin, to = interval.End - margin;
                for (double t = from; t <= to; t += 5e-2 / length)
                {
                    var p = candidate.At(t); const double step = 2e-3; int run = 0, longest = 0;
                    for (double s = Tol + step; s < far; s += step)
                    {
                        if (shapes.Any(shape => shape.Inside(p.X, p.Y, p.Z + s))) longest = Math.Max(longest, ++run); else run = 0;
                    }
                    if (longest >= 3) { Assert.True(interval.Class == Hidden, $"trial {trial}: Visible interval {interval.Start}..{interval.End} has {longest * 2} mm of cover at t={t}"); hidden++; }
                    else if (longest == 0 && shapes.All(shape => Clear(shape, p, far))) { Assert.True(interval.Class == Visible, $"trial {trial}: Hidden interval {interval.Start}..{interval.End} has no cover at t={t}"); visible++; }
                    else skipped++;
                }
            }
        }
        Assert.True(hidden > 40 && visible > 400, $"hidden {hidden}, visible {visible}, skipped {skipped}, undecided intervals {undecided}");
    }

    [Fact]
    public void AShortUndecidedRunIsOnlyAbsorbedAtAChangePoint()
    {
        // A line just above the top of a sphere by a tie's worth of cover: around the closest point the depth window
        // is undecided. With slack (a native curve) that run lies between two Visible stretches and must stay
        // Unresolved; it is not a change point. An exact candidate may take a run up to one tolerance as a tie.
        var scene = Scene(new AnalyticSphere(V(0, 0, 0), 1));
        double z = 1 - Tol - Tol * 3 / 32;   // cover beyond the tolerance: 3/32 of a tolerance at the middle (the window is 1/16..1/8)
        var line = AnalyticCandidate.Straight(V(-.5, 0, z), V(.5, 0, z));
        var exact = scene.Intervals(line, Tol); var native = scene.Intervals(line, Tol, slack: Tol / 2, absorb: 4 * Tol);
        Assert.Contains(native, i => i.Class == Unresolved);
        Assert.All(exact.Concat(native), i => Assert.NotEqual(Hidden, i.Class));
        foreach (var run in native.Where(i => i.Class == Unresolved)) Assert.True(run.Start > 0 && run.End < 1);
    }

    [Fact]
    public void IntervalsOfRandomLinesMatchPointAnswersAtTheirMiddles()
    {
        var random = new Random(7);
        for (int trial = 0; trial < 300; trial++)
        {
            var solids = Enumerable.Range(0, 1 + random.Next(4)).Select(_ => Random(random, 0).Solid).ToArray(); var scene = Scene(solids);
            var a = V(random.NextDouble() * 8 - 4, random.NextDouble() * 8 - 4, random.NextDouble() * 6 - 8);
            var b = V(random.NextDouble() * 8 - 4, random.NextDouble() * 8 - 4, random.NextDouble() * 6 - 8);
            var candidate = AnalyticCandidate.Straight(a, b); var intervals = scene.Intervals(candidate, Tol);
            Ordered(intervals);
            double length = Math.Sqrt(Math.Pow(b.X - a.X, 2) + Math.Pow(b.Y - a.Y, 2) + Math.Pow(b.Z - a.Z, 2));
            foreach (var interval in intervals)
            {
                // Away from the ends of an interval by a tolerance, every point has the interval's class.
                double margin = Tol / length;
                if (interval.End - interval.Start <= 4 * margin) continue;
                foreach (double f in new[] { .1, .37, .5, .81 })
                {
                    double t = interval.Start + margin + (interval.End - interval.Start - 2 * margin) * f;
                    var point = scene.Classify(candidate.At(t), Tol);
                    if (point != Unresolved && interval.Class != Unresolved) Assert.Equal(interval.Class, point);
                }
            }
            // And nothing is lost between them: 200 points along the line agree with the interval that holds them.
            for (int k = 1; k < 200; k++)
            {
                double t = k / 200.0; var holder = intervals.First(i => t >= i.Start && t <= i.End);
                if (Math.Min(t - holder.Start, holder.End - t) * length < Tol) continue;
                var point = scene.Classify(candidate.At(t), Tol);
                if (point != Unresolved && holder.Class != Unresolved) Assert.Equal(holder.Class, point);
            }
        }
    }

    [Fact]
    public void CoordinatesTooLargeForTheToleranceAreUnresolvedNotGuessed()
    {
        // At 1e13 one unit in the last place is about 2 mm: no decision at a 0.1 mm tolerance can be proven.
        var sphere = new AnalyticSphere(V(1e13, 0, 0), 1);
        Assert.Equal(Unresolved, sphere.Occlusion(V(1e13 + .5, 0, -3), Tol));
        var solid = new AnalyticCylinder(V(1e13, 0, 0), V(0, 1, 0), .5, 0, [(V(1e13, -1, 0), V(0, -1, 0)), (V(1e13, 1, 0), V(0, 1, 0))]);
        Assert.Equal(Unresolved, solid.Occlusion(V(1e13 + .25, 0, -3), Tol));
        Assert.Equal(Unresolved, Scene(sphere).Classify(V(double.NaN, 0, 0), Tol));
    }

    [Fact]
    public void InvalidSolidsAreRefused()
    {
        Assert.Throws<ArgumentException>(() => new AnalyticSphere(V(0, 0, 0), 0));
        Assert.Throws<ArgumentException>(() => new AnalyticCylinder(V(0, 0, 0), V(0, 1, 0), 1, 1, [(V(0, 0, 0), V(0, -1, 0)), (V(0, 1, 0), V(0, 1, 0))]));
        Assert.Throws<ArgumentException>(() => new AnalyticCylinder(V(0, 0, 0), V(0, 1, 0), 1, 0, [(V(0, 0, 0), V(1, 0, 0)), (V(0, 1, 0), V(0, 1, 0))]));
        Assert.Throws<ArgumentException>(() => new AnalyticCylinderSheet(V(0, 0, 0), V(0, 1, 0), V(1, 0, 0), 1, 0, 0, 0, 1));
    }
}
