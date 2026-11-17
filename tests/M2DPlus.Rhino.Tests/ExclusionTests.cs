using System.Drawing;
using System.Security.Cryptography;
using System.Text;
using M2DPlus.Core;
using M2DPlus.Rhino.Services;
using M2DPlus.Rhino.Views;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using Xunit;
using Xunit.Abstractions;

namespace M2DPlus.Rhino.Tests;

/// <summary>
/// The hard rule (owner feedback on beta.1, UX §3.1): view boxes and M2D+ bakes are never scene geometry, in document
/// mode, referenced Geometry (select-all), layer-name items (<c>*</c>, <c>$M2D Views</c>, <c>M2D+*</c>) and block
/// instances. Every drawing is compared, by a hash of its classified geometry, with the same drawing made after the
/// other view boxes (or the bake) were deleted.
/// </summary>
[Collection("Rhino")]
[Trait("Category", "RhinoIntegration")]
public sealed class ExclusionTests(RhinoHost host, ITestOutputHelper output)
{
    private const double Tol = .001;

    /// <summary>Order-independent: every piece's class and geometry, every cut loop and hatch.</summary>
    internal static string Hash(ViewDrawing view)
    {
        var lines = view.Items.Select(p => p.Class + ":" + p.GeometricKey)
            .Concat(view.Regions.SelectMany(r => r.Region.Loops.Select(l => "loop:" + GeometryFingerprint.Compute(l.Curve))))
            .Concat(view.Regions.Select(r => "hatch:" + (r.Region.Hatch is { } h ? GeometryFingerprint.Compute(h) : "none")))
            .Order(StringComparer.Ordinal);
        Assert.Null(view.Error);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", lines))));
    }

    private static Guid Box(RhinoDoc doc, Point3d a, Point3d b, ObjectAttributes? attributes = null)
    { using var box = Brep.CreateFromBox(new BoundingBox(a, b)); return doc.Objects.AddBrep(box, attributes); }

    /// <summary>A small building (three boxes and a column) and three view boxes around and through it: a plan that
    /// cuts it (A), an elevation in front (B) and a section through it from the side (C).</summary>
    private static (Guid A, Guid B, Guid C) Model(RhinoDoc doc)
    {
        doc.ModelAbsoluteTolerance = Tol;
        Box(doc, new(0, 0, 0), new(6, 4, 3)); Box(doc, new(1, 5, 0), new(4, 8, 5)); Box(doc, new(7, 1, 0), new(9, 3, 2));
        using (var column = new Cylinder(new Circle(new Plane(new Point3d(5, 6, 0), Vector3d.ZAxis), .4), 4).ToBrep(true, true)) doc.Objects.AddBrep(column);
        Guid View(Plane frame, double w, double h)
        {
            var id = doc.Objects.AddSurface(new PlaneSurface(frame, new Interval(0, w), new Interval(0, h)));
            Assert.True(Assert.Single(ViewBoxes.Validate(doc, [id])).Ok);
            return id;
        }
        var a = View(new Plane(new Point3d(-2, -2, 1.5), Vector3d.XAxis, Vector3d.YAxis), 14, 13);
        var b = View(new Plane(new Point3d(-2, -3, -1), Vector3d.XAxis, Vector3d.ZAxis), 14, 8);
        var c = View(new Plane(new Point3d(5, -2, -1), Vector3d.YAxis, Vector3d.ZAxis), 13, 8);
        return (a, b, c);
    }

    private static ViewDrawing Draw(RhinoDoc doc, Guid view, IEnumerable<CaptureInput>? inputs, out IReadOnlyList<(SceneCapture.Exclusion Why, string Label)> dropped)
    {
        using var cache = new SceneCache();
        using var watch = new DocumentWatch(doc);
        using var capture = new SceneCapture(doc, watch, inputs, cache, excluded: new HashSet<Guid> { view });
        while (!capture.Step(TimeSpan.FromMilliseconds(12))) { }
        dropped = capture.Dropped.ToArray();
        var lease = capture.Accept();
        var request = ViewRequest.Parse(view, doc);
        var batch = NativeDrawing.SolveAsync(lease, new(lease.Value.Revision, request.Revision, "x", 1), [request], new(), default).GetAwaiter().GetResult();
        return batch.Views[0];
    }

    private static CaptureInput[] All(RhinoDoc doc) => doc.Objects.GetObjectList(new ObjectEnumeratorSettings { NormalObjects = true, LockedObjects = true, HiddenObjects = true })
        .Select(o => o.Id).Order().Select((id, i) => new CaptureInput(id, [0], $"input/{{0}}/{i}/{id}") { SourceItem = i }).ToArray();
    private static CaptureInput[] Layers(params string[] patterns) => patterns.Select((p, i) => new CaptureInput(p, [0], $"input/{{0}}/{i}/layer") { SourceItem = i }).ToArray();

    [RhinoFact] public void ViewBoxesAreNeverSceneGeometryInAnyMode() => host.Run(() =>
    {
        foreach (var mode in new[] { "document", "select-all", "layer *", "layer Default + $M2D Views" })
            foreach (var which in new[] { 0, 1, 2 })
            {
                ViewDrawing Run(bool others)
                {
                    using var doc = RhinoDoc.CreateHeadless(null);
                    var boxes = Model(doc); var ids = new[] { boxes.A, boxes.B, boxes.C };
                    if (!others) foreach (var other in ids.Where((_, i) => i != which)) Assert.True(doc.Objects.Delete(other, true));
                    var inputs = mode switch { "document" => null, "select-all" => All(doc), "layer *" => Layers("*"), _ => Layers("Default", ViewBoxes.LayerName) };
                    var view = Draw(doc, ids[which], inputs, out var dropped);
                    if (mode == "select-all")
                        Assert.Equal(others ? 3 : 1, dropped.Count(d => d.Why == SceneCapture.Exclusion.ViewBox));
                    else Assert.Empty(dropped);
                    Assert.True(view.Items.Count + view.Regions.Count > 0, $"{mode} view {which} others={others}: empty. {view.Info}");
                    return view;
                }
                using var with = Run(true); using var without = Run(false);
                output.WriteLine($"{mode} view {which}: {with.Items.Count} / {without.Items.Count} fragments");
                HldFixtureComparison.Equal(without,with);
            }
    });

    [RhinoFact] public void AViewBoxInsideABlockIsNeverDrawn() => host.Run(() =>
    {
        ViewDrawing Run(bool withViewBox)
        {
            using var doc = RhinoDoc.CreateHeadless(null);
            var (a, b, _) = Model(doc);
            using var part = Brep.CreateFromBox(new BoundingBox(new Point3d(10, 5, 0), new Point3d(11, 6, 2)));
            var geometry = new List<GeometryBase> { part }; var attributes = new List<ObjectAttributes> { new() };
            if (withViewBox) { geometry.Add(doc.Objects.FindId(b).Geometry.Duplicate()); attributes.Add(doc.Objects.FindId(b).Attributes.Duplicate()); }
            Assert.True(doc.Objects.Delete(b, true));
            var definition = doc.InstanceDefinitions.Add("with view box", "", Point3d.Origin, geometry, attributes);
            doc.Objects.AddInstanceObject(definition, Transform.Identity);
            return Draw(doc, a, null, out _);
        }
        using var without=Run(false);using var with=Run(true);HldFixtureComparison.Equal(without,with);
    });

    [RhinoFact] public void ExplicitlyReferencedViewBoxesAndBakesAreReportedInInfo() => host.Run(() =>
    {
        using var doc = RhinoDoc.CreateHeadless(null);
        var (a, b, c) = Model(doc);
        var baked = new ObjectAttributes(); baked.SetUserString(M2DPlus.Core.Bake.BakeTags.Class, "visible"); baked.SetUserString(M2DPlus.Core.Bake.BakeTags.Bake, Guid.NewGuid() + "/A");
        doc.Objects.AddLine(new Line(new Point3d(0, -1, 0), new Point3d(6, -1, 0)), baked);
        using var cache = new SceneCache();
        using var watch = new DocumentWatch(doc);
        using var capture = new SceneCapture(doc, watch, All(doc), cache, excluded: new HashSet<Guid> { a });
        while (!capture.Step(TimeSpan.FromMilliseconds(12))) { }
        output.WriteLine(capture.DroppedSummary);
        Assert.Equal(3, capture.Dropped.Count(d => d.Why == SceneCapture.Exclusion.ViewBox));
        Assert.Single(capture.Dropped, d => d.Why == SceneCapture.Exclusion.Baked);
        Assert.Contains("3 view boxes", capture.DroppedSummary);
        Assert.Contains("1 baked M2D+ object", capture.DroppedSummary);
        using var lease = capture.Accept();
        Assert.Equal(4, lease.Value.SourceCount);
    });

    /// <summary>A bake, then the same drawing again in document mode: identical. Baked curves moved to a normal layer stay
    /// out (their markers move with them), and a layer-name <c>*</c> input ignores both bakes and view boxes.</summary>
    [RhinoFact] public void BakedGeometryIsNeverSceneGeometry() => host.Run(() =>
    {
        using var doc = RhinoDoc.CreateHeadless(null);
        var (a, _, _) = Model(doc);
        using var first = Draw(doc, a, null, out _);
        void Compare(ViewDrawing actual) { using(actual) HldFixtureComparison.Equal(first,actual); }
        var owner = Guid.NewGuid();
        var bake = DrawingBake.Bake(doc, owner, [new BakeDrawing(first, "Plan A")]);
        Assert.True(bake.Added.Count > 4, string.Join("\n", bake.Messages));
        // 1. Rerun in document mode: the bake is not drawn and hides nothing.
        Compare(Draw(doc, a, null, out _));
        // 2. Move some baked curves to a normal layer: their bake markers still exclude them.
        var plain = doc.Layers.Add("Plain", Color.Black);
        foreach (var id in bake.Added.Where(id => doc.Objects.FindId(id).Geometry is Curve).Take(5))
        {
            var attributes = doc.Objects.FindId(id).Attributes.Duplicate(); attributes.LayerIndex = plain;
            Assert.True(doc.Objects.ModifyAttributes(id, attributes, true));
        }
        Compare(Draw(doc, a, null, out _));
        // 3. A layer-name "*" (and "M2D+*", and the bake root's parent) ignores both bakes and view boxes.
        Compare(Draw(doc, a, Layers("*"), out var star));
        Assert.Empty(star);
        Compare(Draw(doc, a, Layers("Default", "Plain", "M2D+*", "M2D+", ViewBoxes.LayerName), out _));
        // Select-all references them explicitly: they are left out and reported.
        Compare(Draw(doc, a, All(doc), out var dropped));
        Assert.Contains(dropped, d => d.Why == SceneCapture.Exclusion.Baked);
        Assert.Equal(bake.Added.Count, dropped.Count(d => d.Why == SceneCapture.Exclusion.Baked));
    });
}
