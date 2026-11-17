using M2DPlus.Core;
using M2DPlus.Core.Bake;
using M2DPlus.Rhino.Services;
using Rhino;
using Rhino.Geometry;
using Xunit;
using Xunit.Abstractions;

namespace M2DPlus.Rhino.Tests;

/// <summary>min visible end to end in a licensed Rhino: a wall with a 3 mm gap shows a sliver of a column behind it.
/// The column is ignored at 5 mm and kept at 0 or 2 mm; changing only the threshold never solves again; hidden invalidates projection.</summary>
[Collection("Rhino")]
[Trait("Category", "RhinoIntegration")]
public sealed class MinVisibleRhinoTests(RhinoHost host, ITestOutputHelper output)
{
    private static ViewRequest With(ViewRequest view, params string[] lines)
    {
        var context = new ViewSettingsContext("view", view.Name, view.Kind, view.Cut is not null, 10000, .001);
        var settings = Settings.Resolve([context], [new[] { "beyond=off", "farclip=none" }.Concat(lines).ToArray()], DocumentUnits.Millimetres);
        Assert.False(settings.HasErrors, string.Join(";", settings.Diagnostics));
        return view with { Settings = settings.Views[0] };
    }
    private static Guid Box(RhinoDoc doc, Point3d a, Point3d b) { using var box = Brep.CreateFromBox(new BoundingBox(a, b)); return doc.Objects.AddBrep(box); }
    private static double Length(ViewDrawing view, LineClass kind, string? source = null) => view.Items.Where(p => p.Class == kind && (source is null || p.SourceIdentity == source))
        .Sum(p => { using var g = p.CopyGeometry(); return g is Curve c ? c.GetLength() : 0; });

    [RhinoFact] public void SliverThroughAWallGapIsIgnoredAt5MmKeptAt0AndNeverResolves() => host.Run(() =>
    {
        using var doc = RhinoDoc.CreateHeadless(null);
        doc.ModelUnitSystem = UnitSystem.Millimeters; doc.ModelAbsoluteTolerance = .001;
        // Front elevation (looking +Y). Three wall panels with 3 mm gaps at x = 0..3 and 997..1000. Behind the first
        // gap a box column, behind the second a round one (curved: native HLD), each with its top and bottom edges
        // inside the wall's height, so only 3 mm of each edge shows through its gap.
        var left = Box(doc, new(-1000, 0, 0), new(0, 100, 3000)).ToString();
        var right = Box(doc, new(3, 0, 0), new(997, 100, 3000)).ToString();
        Box(doc, new(1000, 0, 0), new(2000, 100, 3000));
        var column = Box(doc, new(-200, 500, 500), new(200, 800, 2500)).ToString();
        string round;
        // An elliptic column, deeper along the view than it is wide: the same elevation as a round one, but not one of
        // the exact analytic solids, so it still exercises native HLD.
        using (var ellipse = new Ellipse(new Plane(new Point3d(998.5, 650, 500), Vector3d.XAxis, Vector3d.YAxis), 150, 195).ToNurbsCurve())
        using (var wall = Surface.CreateExtrusion(ellipse, Vector3d.ZAxis * 2000).ToBrep())
        using (var cylinder = wall.CapPlanarHoles(doc.ModelAbsoluteTolerance))
            round = doc.Objects.AddBrep(cylinder).ToString();
        using var scene = new SceneCache(); using var cache = new DrawingCache();
        var front = ViewRequest.Parse("Front");
        DrawingBatch Draw(params string[] lines)
        {
            var lease = BaselineTests.Capture(doc, scene);
            return cache.SolveAsync(lease, new(lease.Value.Revision, "minvisible", "minvisible", 1), [With(front, lines)], new(), [], default).GetAwaiter().GetResult();
        }

        using var kept = Draw("min visible = 0");
        var off = kept.Views[0]; output.WriteLine(off.Info);
        Assert.Null(off.Error); Assert.False(off.Report.Quarantined, off.Info);
        var sliver = Length(off, LineClass.Visible, column);
        Assert.InRange(sliver, 5.9, 6.1); // the column's top and bottom glimpses, 3 mm each
        Assert.True(Length(off, LineClass.Hidden, column) > 1000);
        Assert.Equal(0, Length(off, LineClass.Ignored));
        Assert.InRange(Length(off, LineClass.Visible, round), 5.9, 6.1);
        Assert.True(off.NativeCalls > 0, off.Info); // the round column exercises native HLD
        Assert.Contains("Ignored: off", off.Info);
        var solves = NativeDrawing.VisibilitySolves; var hld = NativeDrawing.HldCalls;

        using var ignoredBatch = Draw("min visible = 5 mm");
        var ignored = ignoredBatch.Views[0]; output.WriteLine(ignored.Info);
        Assert.Equal(0, Length(ignored, LineClass.Visible, column));
        Assert.Equal(sliver, Length(ignored, LineClass.Ignored, column), 6);
        Assert.Equal(Length(off, LineClass.Hidden, column), Length(ignored, LineClass.Hidden, column), 6); // hidden lines stay
        Assert.Equal(Length(off, LineClass.Visible, left), Length(ignored, LineClass.Visible, left), 6);   // fully visible walls kept
        Assert.Equal(Length(off, LineClass.Visible, right), Length(ignored, LineClass.Visible, right), 6);
        Assert.Equal(0, Length(ignored, LineClass.Visible, round));
        Assert.All(ignored.Items.Where(p => p.Class == LineClass.Ignored), p => Assert.Contains(p.SourceIdentity, new[] { column, round }));
        Assert.Equal(2, ignored.IgnoredObjects); Assert.True(ignored.IgnoredPieces >= 4);
        Assert.Contains("Ignored (min visible 5): 2 objects", ignored.Info);

        using var twoMm = Draw("min visible = 2 mm"); // each glimpse is 3 mm: kept
        Assert.Equal(sliver, Length(twoMm.Views[0], LineClass.Visible, column), 6);
        using var hiddenOff = Draw("min visible = 0.005 m", "hidden = off"); // the rule still sees hidden pieces
        Assert.Equal(0, Length(hiddenOff.Views[0], LineClass.Hidden));
        Assert.Equal(0, hiddenOff.Views[0].IgnoredPieces);
        Assert.Contains(ViewNotices.For(hiddenOff.Views[0]), n => n.Text.Contains("min visible ignored", StringComparison.Ordinal));
        using var hiddenOffKept = Draw("hidden = off");
        Assert.InRange(Math.Abs(6 - Length(hiddenOffKept.Views[0], LineClass.Visible, column)), 0, doc.ModelAbsoluteTolerance);
        Assert.Equal(off.Report.UncertainCount, hiddenOffKept.Views[0].Report.UncertainCount);
        // M3 gate, extended: threshold and hidden edits are presentation only.
        Assert.Equal(solves+1, NativeDrawing.VisibilitySolves); Assert.Equal(hld+1, NativeDrawing.HldCalls);

        // Bake: ignored lines go to their own Ignored layer, beside the existing scheme; nothing is deleted.
        var bake = DrawingBake.Bake(doc, Guid.NewGuid(), [new BakeDrawing(ignored, "Gap")]);
        var baked = bake.Added.Select(id => doc.Objects.FindId(id)).ToArray();
        var onIgnored = baked.Where(o => doc.Layers[o.Attributes.LayerIndex].FullPath is var p && p.StartsWith("M2D+::Gap::", StringComparison.Ordinal) && p.EndsWith("::Ignored", StringComparison.Ordinal)).ToArray();
        Assert.Equal(ignored.IgnoredPieces, onIgnored.Length);
        Assert.All(onIgnored, o => Assert.Equal("ignored", o.Attributes.GetUserString(BakeTags.Class)));
        Assert.Contains(baked, o => doc.Layers[o.Attributes.LayerIndex].FullPath is var p && p.StartsWith("M2D+::Gap::", StringComparison.Ordinal) && p.EndsWith("::Visible", StringComparison.Ordinal));
    });
}
