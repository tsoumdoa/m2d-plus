using System.Diagnostics;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Types;
using M2DPlus.Core.Views;
using M2DPlus.Rhino.Views;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using Xunit;

namespace M2DPlus.Rhino.Tests;

/// <summary>Owner decision 2026-11-16 (beta.4): the "View box depth" prompt through its non-interactive overloads, and
/// Auto depth by view type, in a real headless Rhino. Expected depths are worked out from the boxes each test adds:
/// a plan is one storey (3 m in document units), a section or elevation runs to the back of what is inside the frame
/// plus 1 % of that geometry's diagonal, and nothing inside gives the frame's longer side.</summary>
public sealed partial class ViewObjectTests
{
    private static Guid Solid(RhinoDoc doc, double x0, double y0, double z0, double x1, double y1, double z1, ObjectAttributes? attributes = null)
    {
        using var box = Brep.CreateFromBox(new BoundingBox(new Point3d(x0, y0, z0), new Point3d(x1, y1, z1)));
        return doc.Objects.AddBrep(box, attributes);
    }

    private static double Diagonal(double dx, double dy, double dz) => Math.Sqrt(dx * dx + dy * dy + dz * dz);

    private static RhinoDoc Document(UnitSystem units)
    {
        var doc = RhinoDoc.CreateHeadless(null); RhinoDoc.ActiveDoc = doc;
        doc.ModelUnitSystem = units; doc.ModelAbsoluteTolerance = Tol;
        return doc;
    }

    /// <summary>A horizontal surface at height z over [x0, x1] × [y0, y1]: Convert to view boxes makes it a plan.</summary>
    private static Guid PlanSurface(RhinoDoc doc, double z, double x0, double y0, double x1, double y1) =>
        doc.Objects.AddSurface(new PlaneSurface(new Plane(new Point3d(0, 0, z), Vector3d.XAxis, Vector3d.YAxis), new Interval(x0, x1), new Interval(y0, y1)));

    /// <summary>A vertical surface in the plane y = <paramref name="y"/> over x ∈ [x0, x1], z ∈ [z0, z1].</summary>
    private static Guid FrontalSurface(RhinoDoc doc, double y, double x0, double x1, double z0, double z1) =>
        doc.Objects.AddSurface(new PlaneSurface(new Plane(new Point3d(0, y, 0), Vector3d.ZAxis, Vector3d.XAxis), new Interval(z0, z1), new Interval(x0, x1)));

    private static (double Depth, Vector3d Look) ReadDepth(RhinoDoc doc, Guid id)
    {
        var check = ViewBoxes.ReadBox(Assert.IsType<Extrusion>(doc.Objects.FindId(id).Geometry), Tol, Angle);
        Assert.True(check.IsValid, check.Reason);
        return (check.Depth!.Value, -check.Frame!.Value.Plane.ZAxis);
    }

    [RhinoFact] public void AnExplicitDepthWithUnitsAppliesToEveryBoxInTheRun() => host.Run(() =>
    {
        using var doc = Document(UnitSystem.Millimeters);
        Solid(doc, 0, 0, 0, 10_000, 10_000, 3000);
        var a = FrontalSurface(doc, -2000, 0, 4000, 0, 3000);
        var b = FrontalSurface(doc, -2000, 5000, 9000, 0, 3000);
        Assert.True(ViewBoxes.TryParseDepth(doc, "8 m", out var eight, out var error), error);
        var results = ViewBoxes.Validate(doc, [a, b], eight);
        Assert.All(results, r => Assert.True(r.Ok && r.Changed, r.Message));
        Assert.All(results, r => Assert.Contains("depth 8000 (given)", r.Message));
        Assert.Equal(8000, ReadDepth(doc, a).Depth, 9);
        Assert.Equal(8000, ReadDepth(doc, b).Depth, 9);
        // Feet and bare numbers (document units) through the same settings parser; bad text explains itself.
        Assert.True(ViewBoxes.TryParseDepth(doc, "10'", out var feet, out _));
        Assert.Equal(3048, feet.Value!.Value, 9);
        Assert.True(ViewBoxes.TryParseDepth(doc, "2500", out var bare, out _));
        Assert.Equal(2500, bare.Value!.Value);
        Assert.False(ViewBoxes.TryParseDepth(doc, "8 parsecs", out _, out var bad));
        Assert.Contains("Unknown distance unit: parsecs", bad);

        // The menu path: the prompt is asked once for the whole run, with this selection's Auto fill and hint.
        var c = FrontalSurface(doc, -2000, 0, 4000, 0, 2000);
        var asked = new List<ViewDepthPrompt>();
        var given = ViewBoxes.Validate(doc, [c, a], request => { asked.Add(request); return request.TryAccept("3000 mm", out var choice, out _) ? choice : null; });
        var request = Assert.Single(asked);
        Assert.Single(request.Autos); // a is already a view box: it is not asked about and keeps its depth
        Assert.Equal("Auto: elevations to the back of what's inside the frame.", request.Hint);
        Assert.Equal(ViewBoxDepth.FormatDepth(12_000 + Diagonal(10_000, 10_000, 3000) * .01, .001), request.AutoText);
        Assert.Equal(3000, ReadDepth(doc, c).Depth, 9);
        Assert.Equal(8000, ReadDepth(doc, a).Depth, 9);
        Assert.Contains(given, r => r.Id == a && r.Ok && !r.Changed);
    });

    [RhinoFact] public void AutoForAPlanInAMillimetreFileIsOneStoreyAndExtendsWhenNothingIsWithinIt() => host.Run(() =>
    {
        using var doc = Document(UnitSystem.Millimeters);
        // A storey: slab −300…0, walls 0…3000, and a storey below (slab −3600…−3300) that one storey never reaches.
        Solid(doc, 0, 0, -300, 10_000, 10_000, 0);
        Solid(doc, 0, 0, 0, 200, 10_000, 3000); Solid(doc, 9800, 0, 0, 10_000, 10_000, 3000);
        Solid(doc, 0, 0, -3600, 10_000, 10_000, -3300);
        var plan = PlanSurface(doc, 1200, -500, -500, 10_500, 10_500);
        var result = Assert.Single(ViewBoxes.Validate(doc, [plan]));
        Assert.True(result.Ok, result.Message);
        Assert.Contains("looking down", result.Message);
        Assert.Contains("depth 3000 (auto: plan, one storey below the cut)", result.Message);
        var (depth, look) = ReadDepth(doc, plan);
        Assert.Equal(3000, depth, 9);
        AssertVector(-Vector3d.ZAxis, look);

        // A hall 9 m tall: cut at 6 m, the floor is 6 m down, so nothing (but the cut walls) is within one storey.
        using var hall = Document(UnitSystem.Millimeters);
        Solid(hall, 0, 0, -300, 10_000, 10_000, 0);
        Solid(hall, 0, 0, 0, 200, 10_000, 9000);
        var high = PlanSurface(hall, 6000, -500, -500, 10_500, 10_500);
        var extended = Assert.Single(ViewBoxes.Validate(hall, [high]));
        Assert.Contains("plan, nothing within one storey", extended.Message);
        Assert.Equal(6300 + Diagonal(10_000, 10_000, 9300) * .01, ReadDepth(hall, high).Depth, 6);

        // In a metre file one storey is 3.
        using var metres = Document(UnitSystem.Meters);
        Solid(metres, 0, 0, -.3, 10, 10, 0); Solid(metres, 0, 0, 0, .2, 10, 3);
        var m = PlanSurface(metres, 1.2, -.5, -.5, 10.5, 10.5);
        Assert.True(Assert.Single(ViewBoxes.Validate(metres, [m])).Ok);
        Assert.Equal(3, ReadDepth(metres, m).Depth, 9);
    });

    [RhinoFact] public void AutoForASectionCountsOnlyTheGeometryInsideTheFrame() => host.Run(() =>
    {
        using var doc = Document(UnitSystem.Meters);
        Solid(doc, 0, 0, 0, 10, 20, 6);                       // the building, cut by the frame at y = 5
        Solid(doc, 40, 0, 0, 50, 400, 80);                    // a tower beside the frame, far behind: not inside
        Solid(doc, 0, -60, 0, 10, -50, 6);                    // in front of the frame: not inside
        Solid(doc, 0, 30, 0, 10, 32, 3);                      // a garden wall behind, inside the frame
        var section = FrontalSurface(doc, 5, 0, 10, 0, 7);
        // Look north (toward the scene's middle) through the building.
        var result = Assert.Single(ViewBoxes.Validate(doc, [section]));
        Assert.True(result.Ok, result.Message);
        Assert.Contains("auto: section", result.Message);
        var (depth, look) = ReadDepth(doc, section);
        AssertVector(Vector3d.YAxis, look);
        Assert.Equal(27 + Diagonal(10, 32, 6) * .01, depth, 9);
    });

    [RhinoFact] public void AutoForAnElevationAndForAFrameWithNothingInside() => host.Run(() =>
    {
        using var doc = Document(UnitSystem.Meters);
        Solid(doc, 0, 0, 0, 10, 20, 6);
        Solid(doc, 60, 0, 0, 70, 300, 6);                     // beside the frame
        var elevation = FrontalSurface(doc, -3, -1, 11, -1, 7);
        var empty = FrontalSurface(doc, -3, 20, 26, 0, 4);     // x 20…26: between the two, nothing inside
        var results = ViewBoxes.Validate(doc, [elevation, empty]);
        Assert.All(results, r => Assert.True(r.Ok, r.Message));
        Assert.Contains("auto: elevation", results[0].Message);
        Assert.Equal(23 + Diagonal(10, 20, 6) * .01, ReadDepth(doc, elevation).Depth, 9);
        Assert.Contains("nothing inside the frame", results[1].Message);
        Assert.Equal(6, ReadDepth(doc, empty).Depth, 9);
    });

    [RhinoFact] public void ViewBoxesBakedHiddenAndOffObjectsNeverCountForAuto() => host.Run(() =>
    {
        using var doc = Document(UnitSystem.Meters);
        Solid(doc, 0, 0, 0, 10, 20, 6);
        // Far behind, inside the frame, each excluded: a view box, a baked drawing (marker or bake layer), a view-box
        // layer object, a hidden object and an object on a layer that is off.
        AddViewBox(doc, "Old view", new Rectangle3d(new Plane(new Point3d(0, 100, 0), Vector3d.XAxis, Vector3d.ZAxis), new Interval(0, 10), new Interval(0, 6)), 50);
        var marker = new ObjectAttributes(); marker.SetUserString(M2DPlus.Core.Bake.BakeTags.Bake, "Plan/Visible");
        Solid(doc, 0, 200, 0, 10, 210, 6, marker);
        var bakeParent = doc.Layers.Add(new Layer { Name = "M2D+" });
        var bakeLayer = doc.Layers.Add(new Layer { Name = "Plan", ParentLayerId = doc.Layers[bakeParent].Id });
        Solid(doc, 0, 220, 0, 10, 230, 6, new ObjectAttributes { LayerIndex = bakeLayer });
        Solid(doc, 0, 240, 0, 10, 250, 6, new ObjectAttributes { LayerIndex = ViewBoxes.ViewsLayer(doc) });
        var hidden = Solid(doc, 0, 260, 0, 10, 270, 6);
        doc.Objects.Hide(hidden, true);
        var off = doc.Layers.Add(new Layer { Name = "Off", IsVisible = false });
        Solid(doc, 0, 280, 0, 10, 290, 6, new ObjectAttributes { LayerIndex = off });

        var section = FrontalSurface(doc, 5, 0, 10, 0, 7);
        Assert.True(Assert.Single(ViewBoxes.Validate(doc, [section])).Ok);
        Assert.Equal(15 + Diagonal(10, 20, 6) * .01, ReadDepth(doc, section).Depth, 9);
        // Showing the hidden object brings it back in, as document mode would draw it.
        doc.Objects.Show(hidden, true);
        var again = FrontalSurface(doc, 5, 0, 10, 0, 7);
        Assert.True(Assert.Single(ViewBoxes.Validate(doc, [again])).Ok);
        Assert.Equal(265 + Diagonal(10, 270, 6) * .01, ReadDepth(doc, again).Depth, 9);
    });

    [RhinoFact] public void CancelChangesNothingAndLeavesNoUndoRecord() => host.Run(() =>
    {
        Assert.True(global::Rhino.PlugIns.PlugIn.LoadPlugIn(GrasshopperId));
        using var doc = Document(UnitSystem.Meters);
        Solid(doc, 0, 0, 0, 10, 20, 6);
        var surface = FrontalSurface(doc, 5, 0, 10, 0, 7);
        // The last undo record before the cancelled run: adding this marker.
        var record = doc.BeginUndoRecord("marker");
        var marker = Solid(doc, 50, 50, 0, 51, 51, 1);
        doc.EndUndoRecord(record);

        var prompted = 0;
        var cancelled = ViewBoxes.Validate(doc, [surface], _ => { prompted++; return null; });
        Assert.Equal(1, prompted);
        var only = Assert.Single(cancelled);
        Assert.True(only.Cancelled); Assert.False(only.Changed);
        Assert.IsType<BrepObject>(doc.Objects.FindId(surface));
        Assert.False(ViewBoxes.IsViewBox(doc.Objects.FindId(surface)));
        Assert.Equal(-1, doc.Layers.FindByFullPath(ViewBoxes.LayerName, -1));
        // Create view cancelled: no object.
        using var viewport = new ViewportInfo { IsParallelProjection = true };
        viewport.SetCameraLocation(new Point3d(5, -50, 3)); viewport.SetCameraDirection(Vector3d.YAxis); viewport.SetCameraUp(Vector3d.ZAxis);
        viewport.SetFrustum(-6, 6, -4, 4, 1, 500);
        var count = doc.Objects.Count;
        Assert.True(ViewBoxes.Create(doc, viewport, [], _ => null).Cancelled);
        Assert.Equal(count, doc.Objects.Count);
        // Undo now undoes the marker, so neither cancelled run recorded anything.
        Assert.True(doc.Undo());
        Assert.Null(doc.Objects.FindId(marker));
        Assert.IsType<BrepObject>(doc.Objects.FindId(surface));

        // Through the GH menu path: V keeps its reference type and the component gets no warning.
        using var gh = new GH_Document();
        var component = NewComponent(gh);
        dynamic view = component.Params.Input[1];
        view.SetValues(new string[0], new IGH_Goo[] { new GH_Surface(surface) }, "test");
        var warnings = component.RuntimeMessages(GH_RuntimeMessageLevel.Warning).ToArray();
        var execute = Gha.GetType("M2DPlus.Grasshopper.UI.ViewObjects", true)!.GetMethod("Execute")!;
        var results = ((IEnumerable<ViewObjectResult>)execute.Invoke(null, ["views-validate", component.Params.Input[1], doc, ((dynamic)component).Ui,
            (DepthPrompt)(_ => null)])!).ToArray();
        Assert.True(Assert.Single(results).Cancelled);
        Assert.IsType<GH_Surface>(Assert.Single((IEnumerable<IGH_Goo>)((dynamic)component.Params.Input[1]).PersistentData.AllData(true)));
        Assert.Equal(warnings, component.RuntimeMessages(GH_RuntimeMessageLevel.Warning));
        Assert.IsType<BrepObject>(doc.Objects.FindId(surface));
        // And with no prompt (scripts, tests, headless) it never asks: Auto.
        var auto = ((IEnumerable<ViewObjectResult>)execute.Invoke(null, ["views-validate", component.Params.Input[1], doc, ((dynamic)component).Ui, null])!).ToArray();
        Assert.True(Assert.Single(auto).Changed);
        Assert.True(ViewBoxes.IsViewBox(doc.Objects.FindId(surface)));
    });

    [RhinoFact] public void ReconvertingViewBoxesNeverPromptsAndKeepsTheirDepth() => host.Run(() =>
    {
        using var doc = Document(UnitSystem.Meters);
        Solid(doc, 0, 0, 0, 10, 20, 6);
        var box = AddViewBox(doc, "Kept", new Rectangle3d(new Plane(new Point3d(0, 5, 0), Vector3d.XAxis, Vector3d.ZAxis), new Interval(0, 10), new Interval(0, 6)), 42);
        var results = ViewBoxes.Validate(doc, [box], _ => throw new InvalidOperationException("must not ask"));
        Assert.True(Assert.Single(results).Ok);
        Assert.Equal(42, ReadDepth(doc, box).Depth, 9);
    });

    [RhinoFact] public void CreateViewUsesAutoForWhatIsInsideTheFrameOrTheGivenDepth() => host.Run(() =>
    {
        using var doc = Document(UnitSystem.Meters);
        Solid(doc, 0, 0, 0, 10, 20, 6);
        Solid(doc, 100, 0, 0, 110, 500, 6); // far behind but outside the viewport's frame
        // A parallel Front viewport over x ∈ [−1, 11], z ∈ [−1, 7] looking north.
        using var viewport = new ViewportInfo { IsParallelProjection = true };
        viewport.SetCameraLocation(new Point3d(5, -50, 3)); viewport.SetCameraDirection(Vector3d.YAxis); viewport.SetCameraUp(Vector3d.ZAxis);
        viewport.SetFrustum(-6, 6, -4, 4, 1, 1000);
        // The front face is just in front of the whole scene (y = −margin of the scene); auto reaches y = 20 plus 1 %
        // of the building's diagonal.
        var scene = new BoundingBox(new Point3d(0, 0, 0), new Point3d(110, 500, 6));
        var front = -scene.Diagonal.Length * .01;
        var auto = ViewBoxes.Create(doc, viewport);
        Assert.True(auto.Ok, auto.Message);
        Assert.Contains("auto: elevation", auto.Message);
        Assert.Equal(20 - front + Diagonal(10, 20, 6) * .01, ReadDepth(doc, auto.Id).Depth, 6);
        var given = ViewBoxes.Create(doc, viewport, [], M2DPlus.Core.Views.DepthChoice.Of(12));
        Assert.Contains("depth 12 (given)", given.Message);
        Assert.Equal(12, ReadDepth(doc, given.Id).Depth, 9);
    });

    [RhinoFact] public void AutoDepthIsFastOnFiftyThousandObjects() => host.Run(() =>
    {
        using var doc = Document(UnitSystem.Meters);
        // 224 × 224 = 50 176 columns 1 × 1 at spacing 2, 3 m tall; slabs under them.
        for (var i = 0; i < 224; i++)
            for (var j = 0; j < 224; j++)
            {
                using var column = Extrusion.CreateBoxExtrusion(new Box(new BoundingBox(new Point3d(2 * i, 2 * j, 0), new Point3d(2 * i + 1, 2 * j + 1, 3))), true);
                doc.Objects.AddExtrusion(column);
            }
        Solid(doc, 0, 0, -.3, 448, 448, 0);
        var plan = PlanSurface(doc, 1.2, 10, 10, 40, 40);
        var section = FrontalSurface(doc, 100.5, 0, 60, -1, 5);
        var timer = Stopwatch.StartNew();
        var results = ViewBoxes.Validate(doc, [plan, section]);
        var elapsed = timer.Elapsed.TotalMilliseconds;
        Assert.All(results, r => Assert.True(r.Ok, r.Message));
        Assert.Equal(3, ReadDepth(doc, plan).Depth, 9);
        Assert.Contains("auto: section", results[1].Message);
        // The section cuts a row of columns, looks toward the scene's middle (north) and reaches the slab's back at 448.
        var (depth, look) = ReadDepth(doc, section);
        AssertVector(Vector3d.YAxis, look);
        Assert.InRange(depth, 448 - 100.5, 448 - 100.5 + 10);
        timer.Restart();
        using var scene = ViewDepthScene.Collect(doc, []);
        var collect = timer.Elapsed.TotalMilliseconds;
        timer.Restart();
        for (var k = 0; k < 100; k++) scene.Auto(new Rectangle3d(new Plane(new Point3d(4 * k, 0, 1.5), Vector3d.XAxis, Vector3d.YAxis), new Interval(0, 20), new Interval(0, 20)));
        var hundred = timer.Elapsed.TotalMilliseconds;
        output.WriteLine($"{scene.Count} objects: convert 2 {elapsed:0} ms; collect {collect:0} ms; 100 Auto depths {hundred:0} ms");
        Assert.True(collect < 5000 && hundred < 2000, $"collect {collect:0} ms, 100 autos {hundred:0} ms");
    });
}
