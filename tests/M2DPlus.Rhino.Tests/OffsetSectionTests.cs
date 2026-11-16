using System.Diagnostics;
using M2DPlus.Core.Sections;
using M2DPlus.Core.Visibility;
using M2DPlus.Rhino.Services;
using M2DPlus.Rhino.Sections;
using Rhino.Geometry;
using Xunit;
using Xunit.Abstractions;

namespace M2DPlus.Rhino.Tests;

[Collection("Rhino")]
[Trait("Category", "RhinoIntegration")]
public sealed class OffsetSectionTests(RhinoHost host, ITestOutputHelper output)
{
    private const double Tol = .01;
    private static Brep Column(double offset) => Brep.CreateFromBox(new BoundingBox(
        new Point3d(offset + 20000, offset + 13300, offset + 12800),
        new Point3d(offset + 20400, offset + 13700, offset + 15800)));

    [RhinoTheory]
    [InlineData(0d)]
    [InlineData(1e5)]
    [InlineData(1e6)]
    [InlineData(1e7)]
    public void TranslatedColumnValidationKeepsTheOriginalArchiveAndValidTopology(double offset) => host.Run(() =>
    {
        using var column = Column(offset);
        var archive = Services.WorkerCodec.Archive(column);
        using var work = new SectionWork();
        using var valid = SectionGeometry.Convert(column, Tol);
        Assert.True(valid.IsValid && valid.IsSolid);
        Assert.Equal(archive, Services.WorkerCodec.Archive(column));
        output.WriteLine(work.Diagnostic);
        Assert.Contains("shared-topology budget hits=0", work.Diagnostic);
    });

    [RhinoFact]
    public void OriginAndSurveySectionsHaveTheSameClassesTopologyAndAnalyticAreas() => host.Run(() =>
    {
        string? signature = null;
        foreach (var offset in new[] { 0d, 1e5, 1e6 })
        {
            var move = Transform.Translation(offset, offset, offset);
            using var box = Brep.CreateFromBox(new BoundingBox(new Point3d(-2000, -100, 0), new Point3d(2000, 100, 3000)));
            using var round = new Cylinder(new Circle(new Plane(new Point3d(5000, 0, 0), Vector3d.ZAxis), 200), 3000).ToBrep(true, true);
            using var front = Brep.CreateFromBox(new BoundingBox(new Point3d(8000, 0, 2000), new Point3d(8400, 400, 3000)));
            using var behind = Brep.CreateFromBox(new BoundingBox(new Point3d(10000, 0, 0), new Point3d(10400, 400, 1000)));
            foreach (var body in new[] { box, round, front, behind }) Assert.True(body.Transform(move));
            var plane = new Plane(new Point3d(offset, offset, offset + 1500), Vector3d.ZAxis);
            using var result = SectionRegionEngine.Compute(new[] { ("box", box), ("round", round), ("front", front), ("behind", behind) }
                .Select(s => new SectionSource(s.Item1, s.Item2, SectionStyleSnapshot.Default())), new(plane, Tol, .001), prepareVisibility: true);
            Assert.False(result.RequiresQuarantine, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
            Assert.Equal(2, result.Regions.Count);
            Assert.All(result.Regions, r => { Assert.Single(r.Loops); Assert.False(r.Loops[0].IsHole); Assert.NotNull(r.Hatch); });
            Assert.InRange(Math.Abs(result.Regions.Single(r => r.SourceIds.Contains("box")).Area - 800000), 0, .001);
            Assert.InRange(Math.Abs(result.Regions.Single(r => r.SourceIds.Contains("round")).Area - Math.PI * 40000), 0, .01);
            var actual = string.Join("|", result.Components.Select(c => $"{c.SourceId}:{c.Status}")) + "/" +
                string.Join("|", result.Regions.Select(r => $"{r.Id}:{r.Status}:{r.Loops.Count}"));
            signature ??= actual;
            Assert.Equal(signature, actual);
            Assert.All(result.Visibility.Values, clip => Assert.True(clip.Succeeded));
        }
    });

    [RhinoPerformanceFact, Trait("Category", "Performance")]
    public void OffsetValidationMedianStaysWithinOneAndAHalfTimesOrigin() => host.Run(() =>
    {
        // Same exact integer-sized columns, alternating order, fresh owned copies; timer reserve
        // absorbs test-host jitter but not the old hundreds-of-thousands-of-nodes fallback.
        double Measure(double offset)
        {
            var clock = Stopwatch.StartNew();
            for (var i = 0; i < 12; i++)
            {
                using var column = Column(offset + i * 3200);
                using var valid = SectionGeometry.Convert(column, Tol);
            }
            return clock.Elapsed.TotalSeconds;
        }
        Measure(0); Measure(1e5);
        var origin = new List<double>(); var survey = new List<double>();
        for (var pass = 0; pass < 5; pass++)
            foreach (var offset in pass % 2 == 0 ? new[] { 0d, 1e5 } : new[] { 1e5, 0d })
                (offset == 0 ? origin : survey).Add(Measure(offset));
        var a = origin.Order().ElementAt(2); var b = survey.Order().ElementAt(2);
        output.WriteLine($"Origin median={a:F6}s; 100 m median={b:F6}s; ratio={b / a:F3}.");
        Assert.True(b <= 1.5 * a + .02, $"Origin={a:F6}s; offset={b:F6}s");
    });

    [RhinoFact]
    public void ExplodedTranslationsReuseOnlyIdenticalLocalArchives() => host.Run(() =>
    {
        using var verdicts = new SectionVerdictCache();
        for (var i = 0; i < 30; i++)
        {
            using var column = Brep.CreateFromBox(new BoundingBox(new Point3d(100000 + i * 5000, 100000, 100000),
                new Point3d(100400 + i * 5000 + (i == 29 ? .001 : 0), 100400, 103000)));
            Assert.Equal("", verdicts.Verdict(new("source-" + i, 1, column.DuplicateBrep), Tol));
        }
        Assert.Equal(30, verdicts.Computations);
        Assert.Equal(2, verdicts.LocalComputations); // A 0.001 mm change is not rounded into the first shape.
    });

    [RhinoFact]
    public void OriginAndSurveyPlansPublishTheSameLineClassesAndLoci() => host.Run(() =>
    {
        (string Source, M2DPlus.Core.LineClass Class, Point3d A, Point3d B)[]? expected = null;
        foreach (var offset in new[] { 0d, 1e5, 1e6 })
        {
            using var doc = global::Rhino.RhinoDoc.CreateHeadless(null); doc.ModelAbsoluteTolerance = Tol;
            using var wall = Brep.CreateFromBox(new BoundingBox(new Point3d(offset, offset, offset), new Point3d(offset + 4000, offset + 200, offset + 3000)));
            using var column = Brep.CreateFromBox(new BoundingBox(new Point3d(offset + 5000, offset, offset), new Point3d(offset + 5400, offset + 400, offset + 3000)));
            using var line = new LineCurve(new Point3d(offset - 1000, offset - 1000, offset + 500), new Point3d(offset + 6000, offset - 1000, offset + 500));
            CaptureInput[] inputs = [new(wall, [], "wall"), new(column, [], "column"), new(line, [], "line")];
            using var cache = new SceneCache(); using var lease = BaselineTests.Capture(doc, cache, inputs);
            var view = ViewRequest.Parse(new Plane(new Point3d(offset, offset, offset + 1500), Vector3d.ZAxis));
            using var batch = NativeDrawing.SolveBorrowedAsync(lease.Value, new(lease.Value.Revision, "offset", "default", 1), [view], new(), default, null).GetAwaiter().GetResult();
            var drawing = Assert.Single(batch.Views); Assert.Null(drawing.Error); Assert.False(drawing.Report.Quarantined, drawing.Info);
            var pieces = drawing.Items.Select(item =>
            {
                using var geometry = item.CopyGeometry(); var curve = Assert.IsAssignableFrom<Curve>(geometry);
                Point3d Local(Point3d p) => p - new Vector3d(offset, offset, offset);
                return (item.SourceIdentity, item.Class, Local(curve.PointAtStart), Local(curve.PointAtEnd));
            }).ToArray();
            // Event partitions can differ after floating-point translation. Compare the
            // union of collinear intervals with the same source/class, as for elevations.
            // The inverse transform can collapse an arithmetic needle to an exact point
            // (the origin control has a 1.5e-19 mm wall fragment). Use the same 1e-7 mm
            // geometry comparison resolution for degeneracy and endpoint equality.
            var actual = pieces.Where(p => p.Item3.DistanceTo(p.Item4) > 1e-7).GroupBy(p => (p.SourceIdentity, p.Class)).SelectMany(group =>
                VisibilityLoci.Union(group.Select(p => new Locus(p.Item3.X, p.Item3.Y, p.Item4.X, p.Item4.Y,
                    VisibilityClass.Visible, p.SourceIdentity)).ToArray(), Tol).Select(l =>
                    (SourceIdentity: group.Key.SourceIdentity, Class: group.Key.Class,
                        new Point3d(l.X0, l.Y0, 1500), new Point3d(l.X1, l.Y1, 1500)))).ToArray();
            if (expected is null) { expected = actual; continue; }
            Assert.True(expected.Length == actual.Length, $"offset={offset:R}; expected={string.Join(";", expected)}; actual={string.Join(";", actual)}; info={drawing.Info}");
            foreach (var piece in expected)
                Assert.Contains(actual, p => p.SourceIdentity == piece.Source && p.Class == piece.Class &&
                    (p.Item3.DistanceTo(piece.A) < 1e-7 && p.Item4.DistanceTo(piece.B) < 1e-7 ||
                     p.Item3.DistanceTo(piece.B) < 1e-7 && p.Item4.DistanceTo(piece.A) < 1e-7));
        }
    });

    [RhinoFact]
    public void TranslationCannotAuthorizeInsufficientInputPrecision() => host.Run(() =>
    {
        using var column = Column(0);
        Assert.True(column.Transform(Transform.Translation(1e15, 1e15, 1e15)));
        Assert.Throws<SectionFailure>(() => SectionGeometry.ValidateFaceIntersections(column, Tol));
    });
}
