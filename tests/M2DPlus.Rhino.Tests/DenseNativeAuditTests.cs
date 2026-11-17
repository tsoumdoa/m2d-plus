using M2DPlus.Core;
using M2DPlus.Rhino.Services;
using Rhino.Geometry;
using Xunit;

namespace M2DPlus.Rhino.Tests;

/// <summary>Certified meshes and the dense audit of native Visible lines (mesh tier, steps B and C).</summary>
[Collection("Rhino")]
[Trait("Category", "RhinoIntegration")]
public sealed class DenseNativeAuditTests(RhinoHost host)
{
    private const double Tol = 1e-3;
    private static ViewDrawing Output() => new() { Frame = new Rectangle3d(Plane.WorldXY, new Interval(-5, 5), new Interval(-5, 5)), Tolerance = Tol };
    private static VisibilityInput Input(GeometryBase g, int i = 0) => new(g, new(i.ToString(), [i], g));
    private static NativeSettings On => new() { DenseAudit = true };

    private static Brep Freeform()
    {
        var points = Enumerable.Range(0, 4).SelectMany(i => Enumerable.Range(0, 4).Select(j =>
        {
            double x = -1 + 2 * i / 3d, y = -1 + 2 * j / 3d; return new Point3d(x, y, .3 * x * y + .1 * x * x * y * y);
        }));
        using var surface = NurbsSurface.CreateFromPoints(points, 4, 4, 3, 3); return surface.ToBrep();
    }

    [RhinoFact] public void BandsHoldOnASphereATorusAndAFreeformSheet() => host.Run(() =>
    {
        using var sphere = new Sphere(Point3d.Origin, 1).ToBrep(); using var torus = new Torus(Plane.WorldXY, 2, .5).ToBrep(); using var sheet = Freeform();
        foreach (var (body, distance) in new (Brep, Func<Point3d, double>)[]
        {
            (sphere, p => Math.Abs(p.DistanceTo(Point3d.Origin) - 1)),
            (torus, p => Math.Abs(Math.Sqrt(Math.Pow(Math.Sqrt(p.X * p.X + p.Y * p.Y) - 2, 2) + p.Z * p.Z) - .5)),
            (sheet, p => sheet.Faces[0].ClosestPoint(p, out double u, out double v) ? sheet.Faces[0].PointAt(u, v).DistanceTo(p) : double.NaN),
        })
        {
            var mesh = CertifiedMesh.Build(body, Tol, 10 * Tol, default); Assert.NotNull(mesh);
            Assert.True(mesh.Count > 0); double worst = 0, loosest = 0; int proved = 0;
            for (int t = 0; t < mesh.Count; t++)
            {
                Point3d a = mesh.Vertices[mesh.Triangles[t * 3]], b = mesh.Vertices[mesh.Triangles[t * 3 + 1]], c = mesh.Vertices[mesh.Triangles[t * 3 + 2]];
                foreach (var q in new[] { (a + b + c) / 3, (a + b) / 2, (b + c) / 2, (a + c) / 2 })
                {
                    double d = distance(q); Assert.True(d <= mesh.Bands[t] + 1e-12, $"triangle {t}: {d} over its band {mesh.Bands[t]}"); worst = Math.Max(worst, d);
                }
                loosest = Math.Max(loosest, mesh.Bands[t]); if (!double.IsNaN(mesh.Parameters[t * 4])) proved++;
            }
            // Useful, not only safe: most triangles proved from the control net, and bands of the order asked for.
            // (Rhino's mesher misses its tolerance by a factor of two to three here; the bands say so.)
            Assert.True(mesh.Uncertified < .2 * mesh.Count, $"{mesh.Uncertified} of {mesh.Count} measured");
            var sorted = mesh.Bands.OrderBy(x => x).ToArray();
            Assert.True(sorted[sorted.Length / 2] <= 10 * Tol && loosest <= 30 * Tol, $"median {sorted[sorted.Length / 2]}, loosest {loosest}, measured worst {worst}");
            Assert.True(proved > .8 * mesh.Count);
        }
    });

    [RhinoFact] public void OneMeshServesEveryInstanceOfADefinition() => host.Run(() =>
    {
        using var local = new Sphere(Point3d.Origin, 1).ToBrep(); var leaves = new List<PreparedLeaf>(); var worlds = new List<Brep>();
        string key = "dense-audit-test-" + Guid.NewGuid().ToString("N");
        foreach (var move in new[] { Transform.Translation(3, 0, 0), Transform.Translation(-3, 1, 0), Transform.Rotation(1, Vector3d.XAxis, Point3d.Origin) })
        {
            var world = local.DuplicateBrep(); world.Transform(move); worlds.Add(world);
            leaves.Add(new(leaves.Count.ToString(), [leaves.Count], world) { Canonical = new(key, 1, () => local.DuplicateBrep()), Placement = move });
        }
        long before = CertifiedMesh.Builds;
        var found = leaves.Select(l => CertifiedMesh.For(l.Geometry, l, Tol, 10 * Tol, default)).ToArray();
        Assert.Equal(before + 1, CertifiedMesh.Builds);
        Assert.All(found, f => Assert.Same(found[0]!.Value.Mesh, f!.Value.Mesh));
        // Placed, the first instance's mesh is around (3, 0, 0).
        var sheet = found[0]!.Value.Mesh.Place(found[0]!.Value.Placement, 1, Plane.WorldXY, 0, 0);
        Assert.All(sheet.Vertices, v => Assert.InRange(Math.Sqrt((v.X - 3) * (v.X - 3) + v.Y * v.Y + v.Z * v.Z), 1 - 1e-9, 1 + 1e-9));
        // A body that is not the leaf's own geometry (a clipped piece) is meshed by itself.
        using var other = new Sphere(Point3d.Origin, .5).ToBrep();
        Assert.NotSame(found[0]!.Value.Mesh, CertifiedMesh.For(other, leaves[0], Tol, 10 * Tol, default)!.Value.Mesh);
        foreach (var w in worlds) w.Dispose();
    });

    [RhinoTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void AVisibleLineBehindABodyIsMovedToUnresolvedWhereItIsCoveredAndNowhereElse(bool freeform) => host.Run(() =>
    {
        using var body = freeform ? Freeform() : new Sphere(Point3d.Origin, 1).ToBrep();
        using var line = new LineCurve(new Point3d(-2, .1, -2), new Point3d(2, .1, -2)); using var output = Output();
        var audit = new DenseNativeAudit([Input(body)], output, new(), default); Assert.True(audit.Active);
        var spans = audit.Spans(line, LineClass.Visible, default);
        Assert.Equal([LineClass.Visible, LineClass.Unresolved, LineClass.Visible], spans.Select(s => s.Class).ToArray());
        Assert.Equal(line.Domain.Min, spans[0].A); Assert.Equal(line.Domain.Max, spans[^1].B);
        double a = line.PointAt(spans[1].A).X, b = line.PointAt(spans[1].B).X, edge = freeform ? 1 : Math.Sqrt(1 - .01);
        // Inside the true cover, and all of it but a few bands at each end.
        Assert.InRange(a, -edge, -edge + .1); Assert.InRange(b, edge - .1, edge);
        // Hidden and Unresolved lines are not looked at.
        Assert.Equal(LineClass.Hidden, Assert.Single(audit.Spans(line, LineClass.Hidden, default)).Class);
    });

    [RhinoFact] public void ALineOnTheFarSideOfItsOwnBodyIsMoved() => host.Run(() =>
    {
        // Self-occlusion: the only body is the one the line lies on.
        using var sphere = new Sphere(Point3d.Origin, 1).ToBrep(); using var output = Output();
        using var far = new ArcCurve(new Arc(new Point3d(-.5, 0, -Math.Sqrt(.75)), new Point3d(0, 0, -1), new Point3d(.5, 0, -Math.Sqrt(.75))));
        using var near = new ArcCurve(new Arc(new Point3d(-.5, 0, Math.Sqrt(.75)), new Point3d(0, 0, 1), new Point3d(.5, 0, Math.Sqrt(.75))));
        var audit = new DenseNativeAudit([Input(sphere)], output, new(), default);
        Assert.Equal(LineClass.Unresolved, Assert.Single(audit.Spans(far, LineClass.Visible, default)).Class);
        Assert.Equal(LineClass.Visible, Assert.Single(audit.Spans(near, LineClass.Visible, default)).Class);
    });

    [RhinoFact] public void ItIsOffByDefaultAndOnlyEverAddsUnresolved() => host.Run(() =>
    {
        using var sphere = new Sphere(Point3d.Origin, 1).ToBrep(); using var line = new LineCurve(new Point3d(-2, 0, -2), new Point3d(2, 0, -2));
        ViewDrawing Draw(NativeSettings settings)
        {
            var output = Output(); var source = Input(line, 1).Source;
            output.Pieces.Add(new(line.DuplicateCurve(), source, LineClass.Visible, line.DuplicateCurve()));
            output.Pieces.Add(new(line.DuplicateCurve(), source, LineClass.Hidden, line.DuplicateCurve()));
            DenseNativeAudit.Apply([Input(sphere), Input(line, 1)], output, settings, default); return output;
        }
        using var off = Draw(new() { DenseAudit = false }); using var on = Draw(On); using var raw = Draw(On with { NativeOnly = true });
        Assert.Equal(2, off.Pieces.Count); Assert.DoesNotContain(off.Diagnostics, d => d.StartsWith("Dense native audit", StringComparison.Ordinal));
        Assert.Equal(2, raw.Pieces.Count);
        Assert.Equal(1, on.Pieces.Count(p => p.Class == LineClass.Hidden));
        Assert.Equal(1, on.Pieces.Count(p => p.Class == LineClass.Unresolved));
        Assert.Equal(2, on.Pieces.Count(p => p.Class == LineClass.Visible));
        Assert.Contains(on.Diagnostics, d => d.StartsWith("Dense native audit: native Visible proved Hidden", StringComparison.Ordinal) && d.Contains("spans=1;", StringComparison.Ordinal));
    });

    [RhinoFact] public void AStripWithTooManyTrianglesIsNotAuditedAndSaysSo() => host.Run(() =>
    {
        using var sphere = new Sphere(Point3d.Origin, 1).ToBrep(); using var line = new LineCurve(new Point3d(-2, 0, -2), new Point3d(2, 0, -2)); using var output = Output();
        var audit = new DenseNativeAudit([Input(sphere)], output, new(MaxStripTriangles: 10), default);
        Assert.True(audit.Active);
        Assert.Equal(LineClass.Visible, Assert.Single(audit.Spans(line, LineClass.Visible, default)).Class);
        audit.Report(output);
        Assert.Contains(output.Diagnostics, d => d.Contains("strips over the limit and not audited=1 (length=4)", StringComparison.Ordinal));
    });

    [RhinoFact] public void StripsGiveTheSameAnswerAsOneIndex() => host.Run(() =>
    {
        // A row of spheres and a line behind them all: cut into many strips or none, the same stretches are moved.
        var spheres = Enumerable.Range(0, 6).Select(i => new Sphere(new Point3d(-3.75 + 1.5 * i, 0, 0), .6).ToBrep()).ToArray();
        using var line = new LineCurve(new Point3d(-4.9, .05, -2), new Point3d(4.9, .05, -2));
        List<(double A, double B, LineClass Class)> Run(DenseAuditOptions options)
        {
            using var output = Output(); var audit = new DenseNativeAudit(spheres.Select((s, i) => Input(s, i)).ToArray(), output, options, default);
            var spans = audit.Spans(line, LineClass.Visible, default); audit.Report(output);
            if (options.StripTriangles < 1000) Assert.DoesNotContain(output.Diagnostics, d => d.Contains("strips=1 ", StringComparison.Ordinal));
            return spans;
        }
        var whole = Run(new()); var strips = Run(new(StripTriangles: 500));
        Assert.Equal(13, whole.Count); Assert.Equal(whole.Count, strips.Count);
        for (int i = 0; i < whole.Count; i++) { Assert.Equal(whole[i].Class, strips[i].Class); Assert.Equal(whole[i].A, strips[i].A, 9); Assert.Equal(whole[i].B, strips[i].B, 9); }
        foreach (var s in spheres) s.Dispose();
    });
}
