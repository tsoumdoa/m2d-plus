using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Reflection;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Parameters;
using Grasshopper.Kernel.Types;
using M2DPlus.Rhino.Services;
using M2DPlus.Rhino.Views;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using Xunit;
using Xunit.Abstractions;
using Environment = System.Environment;
using Font = System.Drawing.Font;

namespace M2DPlus.Rhino.Tests;

/// <summary>UX rev. 4 view objects: Convert to view boxes → view box, user text, Flip, Create from viewport, the Depth input and
/// the settings precedence, in a real headless Rhino and Grasshopper. Expectations come from the input geometry.</summary>
[Collection("Rhino")]
[Trait("Category", "RhinoIntegration")]
public sealed partial class ViewObjectTests(RhinoHost host, ITestOutputHelper output)
{
    private static readonly Guid GrasshopperId = new("b45a29b1-4343-4035-989e-044e8580d9cf");
    private static Assembly Gha => Assembly.LoadFrom(Path.Combine(AppContext.BaseDirectory, "M2DPlus.Grasshopper.gha"));
    private static string? ScreenshotFolder => Environment.GetEnvironmentVariable("M2D_UI_SCREENSHOTS");
    private const double Tol = .001, Angle = Math.PI / 180;
    private sealed class Cleanup(Action action) : IDisposable { public void Dispose() => action(); }

    // A vertical 4 × 4 frame at y = −5, its surface normal facing +Y, looking at a 2 × 2 × 2 box at the origin.
    private static Plane Front => new(new Point3d(-2, -5, 0), Vector3d.XAxis, Vector3d.ZAxis); // normal −Y
    private static PlaneSurface FrontSurface() => new(new Plane(new Point3d(-2, -5, 0), Vector3d.ZAxis, Vector3d.XAxis), new Interval(0, 4), new Interval(0, 4));
    private static Brep ModelBox() => Brep.CreateFromBox(new BoundingBox(new Point3d(-1, -1, 0), new Point3d(1, 1, 2)));

    private static void AssertVector(Vector3d expected, Vector3d actual, double tolerance = 1e-9) =>
        Assert.True((expected - actual).Length < tolerance, $"expected {expected}, got {actual}");
    private static void AssertPoint(Point3d expected, Point3d actual, double tolerance = 1e-9) =>
        Assert.True(expected.DistanceTo(actual) < tolerance, $"expected {expected}, got {actual}");

    [RhinoFact] public void EstablishAcceptsRectanglesAndExplainsEveryRefusal() => host.Run(() =>
    {
        // Good: an untrimmed planar rectangle surface. Frame Y stands up (world Z), X = Y × normal, origin lower-left.
        var good = ViewBoxes.Establish(FrontSurface().ToBrep(), Tol, Angle);
        Assert.True(good.IsValid, good.Reason);
        var frame = good.Frame!.Value;
        AssertVector(Vector3d.ZAxis, frame.Plane.YAxis);
        Assert.Equal(4, frame.Width, 9); Assert.Equal(4, frame.Height, 9);
        AssertVector(Vector3d.CrossProduct(frame.Plane.YAxis, frame.Plane.ZAxis), frame.Plane.XAxis);
        AssertPoint(new Point3d(frame.Plane.XAxis.X > 0 ? -2 : 2, -5, 0), frame.Plane.Origin);

        // Trimmed planar surfaces whose outer loop is a rectangle are fine, including a split (collinear) edge and a
        // rotated rectangle; the frame lies exactly on the loop.
        var rotated = Transform.Rotation(Math.PI / 6, Vector3d.ZAxis, Point3d.Origin);
        var corners = new[] { new Point3d(0, 0, 0), new Point3d(3, 0, 0), new Point3d(5, 0, 0), new Point3d(5, 2, 0), new Point3d(0, 2, 0), new Point3d(0, 0, 0) };
        var outline = new PolylineCurve(corners); outline.Transform(rotated);
        var trimmed = Brep.CreatePlanarBreps(outline, Tol)!.Single();
        Assert.True(trimmed.Faces[0].UnderlyingSurface().GetBoundingBox(true).Diagonal.Length > 0);
        var trimmedFrame = ViewBoxes.Establish(trimmed, Tol, Angle);
        Assert.True(trimmedFrame.IsValid, trimmedFrame.Reason);
        Assert.Equal(10, trimmedFrame.Frame!.Value.Area, 9);
        // Horizontal frame: Y is the rectangle edge nearest north (30° off here).
        Assert.Equal(Math.Cos(Math.PI / 6), trimmedFrame.Frame.Value.Plane.YAxis * Vector3d.YAxis, 9);
        // A trimmed surface on a much larger plane: the frame is the trim, not the underlying surface.
        var big = new PlaneSurface(Plane.WorldXY, new Interval(-50, 50), new Interval(-50, 50)).ToBrep();
        var cutter = new Rectangle3d(Plane.WorldXY, new Interval(1, 3), new Interval(1, 2)).ToNurbsCurve();
        var split = big.Faces[0].Split([cutter], Tol)!;
        var inner = split.Faces.Select(f => f.DuplicateFace(false)).Single(b => b.GetArea() < 3);
        var innerFrame = ViewBoxes.Establish(inner, Tol, Angle);
        Assert.True(innerFrame.IsValid, innerFrame.Reason);
        Assert.Equal(2, innerFrame.Frame!.Value.Area, 6);

        // A closed rectangle curve works as well.
        Assert.True(ViewBoxes.Establish(new Rectangle3d(Front, 4, 4).ToNurbsCurve(), Tol, Angle).IsValid);

        // Refusals, each with its reason.
        var bent = NurbsSurface.CreateFromCorners(new Point3d(0, 0, 0), new Point3d(4, 0, 0), new Point3d(4, 4, 1), new Point3d(0, 4, 0));
        Assert.Contains("not planar", ViewBoxes.Establish(bent.ToBrep(), Tol, Angle).Reason);
        var parallelogram = Brep.CreatePlanarBreps(new PolylineCurve([new(0, 0, 0), new(4, 0, 0), new(5, 3, 0), new(1, 3, 0), new(0, 0, 0)]), Tol)!.Single();
        Assert.Contains("not right angles", ViewBoxes.Establish(parallelogram, Tol, Angle).Reason);
        var triangle = Brep.CreatePlanarBreps(new PolylineCurve([new(0, 0, 0), new(4, 0, 0), new(0, 3, 0), new(0, 0, 0)]), Tol)!.Single();
        Assert.Contains("3 corners, not 4", ViewBoxes.Establish(triangle, Tol, Angle).Reason);
        var disc = Brep.CreatePlanarBreps(new Circle(3).ToNurbsCurve(), Tol)!.Single();
        Assert.Contains("not all straight", ViewBoxes.Establish(disc, Tol, Angle).Reason);
        Assert.Contains("polysurface with 6 faces", ViewBoxes.Establish(ModelBox(), Tol, Angle).Reason);
        var sliver = new PlaneSurface(Plane.WorldXY, new Interval(0, 4), new Interval(0, Tol * 5)).ToBrep();
        Assert.Contains("degenerate", ViewBoxes.Establish(sliver, Tol, Angle).Reason);
        Assert.Contains("not closed", ViewBoxes.Establish(new LineCurve(Point3d.Origin, new Point3d(1, 0, 0)), Tol, Angle).Reason);
    });

    [RhinoFact] public void ViewBoxBuildsAndReadsBackExactlyAndFacesTheModel() => host.Run(() =>
    {
        var scene = ModelBox().GetBoundingBox(true);
        var check = ViewBoxes.Establish(FrontSurface().ToBrep(), Tol, Angle);
        // The surface normal faces +Y (toward the model), so Face points the view along +Y: frame Z = −Y.
        var faced = ViewBoxes.Face(check.Frame!.Value, scene, Tol);
        AssertVector(-Vector3d.YAxis, faced.Plane.ZAxis);
        AssertVector(Vector3d.XAxis, faced.Plane.XAxis); // looking north with Z up, right is east
        AssertPoint(new Point3d(-2, -5, 0), faced.Plane.Origin);
        // Auto depth: the model's back face is at y = 1, 6 behind the frame, plus 1 % of the scene diagonal.
        var depth = ViewBoxes.AutoDepth(faced, scene, Tol);
        Assert.Equal(6 + scene.Diagonal.Length * .01, depth, 9);

        using var box = ViewBoxes.Build(faced, depth);
        Assert.True(box.IsValid);
        var read = ViewBoxes.ReadBox(box, Tol, Angle);
        Assert.True(read.IsValid, read.Reason);
        Assert.Equal(depth, read.Depth!.Value, 9);
        AssertPoint(faced.Plane.Origin, read.Frame!.Value.Plane.Origin);
        AssertVector(faced.Plane.XAxis, read.Frame.Value.Plane.XAxis);
        AssertVector(faced.Plane.YAxis, read.Frame.Value.Plane.YAxis);
        Assert.Equal((4d, 4d), (Math.Round(read.Frame.Value.Width, 9), Math.Round(read.Frame.Value.Height, 9)));
        // Independent: the box's bounding box spans the frame and runs from y = −5 to y = −5 + depth.
        var bounds = box.GetBoundingBox(true);
        Assert.Equal((-2d, 2d, -5d, 0d, 4d), (Math.Round(bounds.Min.X, 9), Math.Round(bounds.Max.X, 9), Math.Round(bounds.Min.Y, 9), bounds.Min.Z, bounds.Max.Z));
        Assert.Equal(-5 + depth, bounds.Max.Y, 9);

        // It resolves exactly like a Box: cut at the front face, crop = profile, far limit = path length.
        var request = ViewRequest.Parse(box);
        Assert.Null(request.Error);
        Assert.Equal(M2DPlus.Core.ViewSourceKind.Box, request.Kind);
        Assert.Equal(depth, request.Cut!.SourceDepth!.Value, 9);
        AssertPoint(faced.Plane.Origin, request.Cut.Plane.Origin);
        AssertVector(-Vector3d.YAxis, request.Cut.Plane.ZAxis);
        Assert.Equal(16, request.Cut.Crop!.Value.Area, 9);
        AssertVector(Vector3d.YAxis, request.Direction);

        // A horizontal frame always looks down (a plan), whatever its normal; Flip reverses it, keeping the rectangle.
        var plan = new Rectangle3d(new Plane(new Point3d(0, 0, 1), -Vector3d.XAxis, Vector3d.YAxis), 4, 2); // normal −Z, north up
        var down = ViewBoxes.Face(plan, scene, Tol);
        AssertVector(Vector3d.ZAxis, down.Plane.ZAxis);
        AssertVector(Vector3d.YAxis, down.Plane.YAxis);
        AssertVector(Vector3d.XAxis, down.Plane.XAxis);
        var flipped = ViewBoxes.Flip(down);
        AssertVector(-Vector3d.ZAxis, flipped.Plane.ZAxis);
        AssertVector(down.Plane.YAxis, flipped.Plane.YAxis);
        Assert.Equal(down.Area, flipped.Area, 9);
        AssertPoint(down.Center, flipped.Center);
        Assert.Equal("down", ViewBoxes.Direction(-down.Plane.ZAxis));
        Assert.Equal("north", ViewBoxes.Direction(-faced.Plane.ZAxis));
    });

    [RhinoFact] public void ValidateConvertsInPlaceKeepsIdAndGhReferenceAndUndoesAsOneStep() => host.Run(() =>
    {
        Assert.True(global::Rhino.PlugIns.PlugIn.LoadPlugIn(GrasshopperId));
        using var doc = RhinoDoc.CreateHeadless(null); RhinoDoc.ActiveDoc = doc;
        doc.ModelAbsoluteTolerance = Tol;
        doc.Objects.AddBrep(ModelBox());
        var surfaceId = doc.Objects.AddSurface(FrontSurface());
        var wiredId = doc.Objects.AddSurface(new PlaneSurface(new Plane(new Point3d(-2, 5, 0), Vector3d.ZAxis, Vector3d.XAxis), new Interval(0, 4), new Interval(0, 4)));
        var badId = doc.Objects.AddBrep(Brep.CreatePlanarBreps(new PolylineCurve([new(0, 0, 3), new(4, 0, 3), new(5, 3, 3), new(1, 3, 3), new(0, 0, 3)]), Tol)!.Single());
        using var gh = new GH_Document();
        var component = NewComponent(gh);
        dynamic view = component.Params.Input[1];
        // V stores Surface-typed references (as a Surface param would); Validate must re-type them to survive.
        view.SetValues(new string[0], new IGH_Goo[] { new GH_Surface(surfaceId), new GH_Surface(badId) }, "test");

        var report = Run("views-validate", component, doc);
        foreach (var line in report) output.WriteLine(line.ToString());
        var converted = Assert.Single(report, r => r.Id == surfaceId);
        Assert.True(converted.Ok && converted.Changed, converted.Message);
        Assert.Contains("looking north", converted.Message);
        var refused = Assert.Single(report, r => r.Id == badId);
        Assert.False(refused.Ok); Assert.Contains("not right angles", refused.Message);
        Assert.Contains(((GH_Component)component).RuntimeMessages(GH_RuntimeMessageLevel.Warning), m => m.Contains("not right angles"));

        // Same object id, now an extrusion view box with the marker, a name and default user text.
        var obj = doc.Objects.FindId(surfaceId);
        Assert.IsType<ExtrusionObject>(obj);
        Assert.True(ViewBoxes.IsViewBox(obj));
        Assert.Equal("1", obj.Attributes.GetUserString("m2d.view"));
        Assert.Equal("M2D View 01", obj.Attributes.Name);
        Assert.All(ViewBoxes.Keys, k => Assert.Equal("default", obj.Attributes.GetUserString("m2d." + k)));
        Assert.Equal("default", obj.Attributes.GetUserString("m2d.color"));
        Assert.Equal(ViewBoxes.LayerName, doc.Layers[obj.Attributes.LayerIndex].FullPath);
        Assert.True(doc.Objects.FindId(badId) is BrepObject);
        // The GH reference survives: V now holds a Brep reference to the same id, and it loads the extrusion.
        IGH_Goo[] stored = ((IEnumerable<IGH_Goo>)((dynamic)component.Params.Input[1]).PersistentData.AllData(true)).ToArray();
        var reference = Assert.IsType<GH_Brep>(stored[0]);
        Assert.Equal(surfaceId, reference.ReferenceID);
        var reload = new GH_Brep(surfaceId);
        Assert.True(reload.LoadGeometry(doc));
        Assert.Equal(6, reload.Value.Faces.Count);
        // And it parses as a Box-like view, with the frame and depth Validate chose.
        var request = ViewRequest.Parse(surfaceId, doc);
        Assert.Null(request.Error);
        Assert.Equal(surfaceId, request.ObjectId);
        Assert.Equal(M2DPlus.Core.ViewSourceKind.Box, request.Kind);
        // Auto depth reaches the back of the drawable scene plus 1 % of its diagonal. Every object V references is a
        // view object, not scene (the refused parallelogram too), so the scene is the box and the unreferenced surface.
        var scene = BoundingBox.Empty;
        foreach (var other in doc.Objects.Where(o => o.Id != surfaceId && o.Id != badId)) scene.Union(other.Geometry.GetBoundingBox(false));
        Assert.Equal(scene.Max.Y + 5 + scene.Diagonal.Length * .01, request.Cut!.SourceDepth!.Value, 6);

        // One Rhino undo restores the surface and its attributes together.
        Assert.True(doc.Undo());
        var restored = doc.Objects.FindId(surfaceId);
        Assert.IsType<BrepObject>(restored);
        Assert.False(ViewBoxes.IsViewBox(restored));
        Assert.True(string.IsNullOrEmpty(restored.Attributes.Name));
        Assert.Null(restored.Attributes.GetUserString("m2d.hidden"));

        // A surface wired through a Surface parameter is refused, never converted: the wire would break.
        var surfaceParam = new Param_Surface();
        surfaceParam.PersistentData.Append(new GH_Surface(wiredId));
        gh.AddObject(surfaceParam, false);
        component.Params.Input[1].AddSource(surfaceParam);
        surfaceParam.CollectData(); component.Params.Input[1].CollectData();
        var wired = Assert.Single(Run("views-validate", component, doc), r => r.Id == wiredId);
        Assert.False(wired.Ok);
        Assert.Contains("Surface wire", wired.Message);
        Assert.IsType<BrepObject>(doc.Objects.FindId(wiredId));
    });

    [RhinoFact] public void UserTextDefaultsNeverOverwriteValuesAndRowsSkipDefault() => host.Run(() =>
    {
        using var doc = RhinoDoc.CreateHeadless(null); RhinoDoc.ActiveDoc = doc;
        doc.Objects.AddBrep(ModelBox());
        var attributes = new ObjectAttributes { Name = "Section A" };
        attributes.SetUserString("m2d.hidden", "off");
        attributes.SetUserString("m2d.scale", "1:50");
        var id = doc.Objects.AddSurface(FrontSurface(), attributes);
        var result = Assert.Single(ViewBoxes.Validate(doc, [id]));
        Assert.True(result.Ok, result.Message);
        var obj = doc.Objects.FindId(id);
        Assert.Equal("Section A", obj.Attributes.Name);
        Assert.Equal("off", obj.Attributes.GetUserString("m2d.hidden"));
        Assert.Equal("1:50", obj.Attributes.GetUserString("m2d.scale"));
        Assert.Equal("default", obj.Attributes.GetUserString("m2d.farclip"));
        Assert.Equal(ViewBoxes.Keys.Length + 2, obj.Attributes.GetUserStrings().AllKeys.Count(k => k!.StartsWith("m2d.", StringComparison.Ordinal)));
        var row = ViewBoxes.ReadRow(obj.Attributes);
        Assert.Equal(new Dictionary<string, string> { ["hidden"] = "off", ["scale"] = "1:50" }, row.Values.OrderBy(p => p.Key).ToDictionary());

        // Validating a view box again re-adds only what the user deleted, and keeps every edit.
        var edited = obj.Attributes.Duplicate();
        edited.DeleteUserString("m2d.cuts");
        edited.SetUserString("m2d.beyond", "2 m");
        doc.Objects.ModifyAttributes(id, edited, true);
        var again = Assert.Single(ViewBoxes.Validate(doc, [id]));
        Assert.True(again.Ok && again.Changed, again.Message);
        Assert.Contains("added 1 user-text keys", again.Message);
        obj = doc.Objects.FindId(id);
        Assert.Equal("default", obj.Attributes.GetUserString("m2d.cuts"));
        Assert.Equal("2 m", obj.Attributes.GetUserString("m2d.beyond"));
        Assert.Equal("off", obj.Attributes.GetUserString("m2d.hidden"));
        var unchanged = Assert.Single(ViewBoxes.Validate(doc, [id]));
        Assert.False(unchanged.Changed);
    });

    [RhinoFact] public void FlipReversesTheLookDirectionKeepsTheFrontFaceAndUndoes() => host.Run(() =>
    {
        using var doc = RhinoDoc.CreateHeadless(null); RhinoDoc.ActiveDoc = doc;
        doc.Objects.AddBrep(ModelBox());
        var id = doc.Objects.AddSurface(FrontSurface());
        Assert.True(ViewBoxes.Validate(doc, [id]).Single().Ok);
        var before = ViewRequest.Parse(id, doc);
        var result = Assert.Single(ViewBoxes.Flip(doc, [id]));
        Assert.True(result.Ok, result.Message);
        Assert.Contains("now looking south", result.Message);
        var after = ViewRequest.Parse(id, doc);
        Assert.Equal(id, after.ObjectId);
        AssertVector(-before.Direction, after.Direction);
        Assert.Equal(before.Cut!.SourceDepth!.Value, after.Cut!.SourceDepth!.Value, 9);
        // The front face (the frame) is unchanged; the volume now extends to the other side: y from −5 to −5 − depth.
        AssertPoint(before.Cut.Crop!.Value.Center, after.Cut.Crop!.Value.Center);
        Assert.Equal(before.Cut.Crop.Value.Area, after.Cut.Crop.Value.Area, 9);
        var bounds = doc.Objects.FindId(id).Geometry.GetBoundingBox(true);
        Assert.Equal(-5, bounds.Max.Y, 9);
        Assert.Equal(-5 - before.Cut.SourceDepth.Value, bounds.Min.Y, 9);
        // Right-handed and not mirrored: X × Y = Z after the flip.
        AssertVector(Vector3d.CrossProduct(after.Cut.Plane.XAxis, after.Cut.Plane.YAxis), after.Cut.Plane.ZAxis);
        Assert.True(doc.Undo());
        AssertVector(before.Direction, ViewRequest.Parse(id, doc).Direction);
        // Only view boxes flip; the reason says what to do.
        var surface = doc.Objects.AddSurface(FrontSurface());
        Assert.Contains("Convert to view boxes first", ViewBoxes.Flip(doc, [surface]).Single().Message);
    });

    [RhinoFact] public void CreateViewFromAParallelViewportBakesANamedBoxOnTheViewsLayer() => host.Run(() =>
    {
        Assert.True(global::Rhino.PlugIns.PlugIn.LoadPlugIn(GrasshopperId));
        using var doc = RhinoDoc.CreateHeadless(null); RhinoDoc.ActiveDoc = doc;
        doc.ModelAbsoluteTolerance = Tol;
        doc.Objects.AddBrep(ModelBox(), new ObjectAttributes { Name = "M2D View 01" });
        // A headless document has no views, so a parallel Front viewport is built directly: camera at y = −20
        // looking +Y, X = east, Y = up, frustum 10 × 6 around (0, z = 1).
        using var viewport = new ViewportInfo { IsParallelProjection = true };
        viewport.SetCameraLocation(new Point3d(0, -20, 1)); viewport.SetCameraDirection(Vector3d.YAxis); viewport.SetCameraUp(Vector3d.ZAxis);
        viewport.SetFrustum(-5, 5, -3, 3, 1, 100);
        var scene = ModelBox().GetBoundingBox(true);
        var frame = ViewBoxes.FrameFromViewport(viewport, scene, Tol);
        Assert.True(frame.IsValid, frame.Reason);
        var margin = scene.Diagonal.Length * .01;
        // Front face just in front of the model (y = −1 − margin), depth through to just behind it.
        Assert.Equal(-1 - margin, frame.Frame!.Value.Plane.Origin.Y, 9);
        Assert.Equal(2 + 2 * margin, frame.Depth!.Value, 9);
        Assert.Equal((10d, 6d), (Math.Round(frame.Frame.Value.Width, 9), Math.Round(frame.Frame.Value.Height, 9)));
        AssertPoint(new Point3d(-5, -1 - margin, -2), frame.Frame.Value.Plane.Origin);
        AssertVector(-Vector3d.YAxis, frame.Frame.Value.Plane.ZAxis);

        using var gh = new GH_Document();
        var component = NewComponent(gh);
        var createFrom = Gha.GetType("M2DPlus.Grasshopper.UI.ViewObjects", true)!.GetMethod("CreateFrom")!;
        var created = (ViewObjectResult)createFrom.Invoke(null, [component.Params.Input[1], doc, viewport])!;
        Assert.True(created.Ok, created.Message);
        var obj = doc.Objects.FindId(created.Id);
        Assert.Equal("M2D View 02", obj.Attributes.Name); // 01 is taken
        var layer = doc.Layers[obj.Attributes.LayerIndex];
        Assert.Equal("$M2D Views", layer.FullPath);
        Assert.Equal(Guid.Empty, layer.ParentLayerId);
        Assert.Equal(ViewBoxes.Cobalt.ToArgb(), layer.Color.ToArgb());
        Assert.Equal(-1, layer.PlotWeight);
        Assert.Equal("default", obj.Attributes.GetUserString("m2d.color"));
        Assert.True(doc.Layers.FindByFullPath("M2D+::Views", -1) < 0);
        Assert.True(ViewBoxes.IsViewBox(obj));
        var read = ViewBoxes.ReadBox((Extrusion)obj.Geometry, Tol, Angle);
        Assert.Equal(frame.Depth.Value, read.Depth!.Value, 9);
        IGH_Goo[] stored = ((IEnumerable<IGH_Goo>)((dynamic)component.Params.Input[1]).PersistentData.AllData(true)).ToArray();
        Assert.Equal(created.Id, Assert.IsType<GH_Brep>(Assert.Single(stored)).ReferenceID);
        Assert.True(doc.Undo());
        Assert.Null(doc.Objects.FindId(created.Id));

        // Perspective viewports are refused with the reason.
        using var perspective = new ViewportInfo { IsPerspectiveProjection = true };
        Assert.Contains("parallel views only", ViewBoxes.FrameFromViewport(perspective, scene, Tol).Reason);
    });

    // ------------------------------------------------------------------ component: Depth input and precedence

    // Two unit boxes behind a vertical view plane through y = 0 looking +Y: A at depth 1–2 (x 0–1), B at depth 6–7 (x 3–4).
    private static Plane PlaneView => new(Point3d.Origin, Vector3d.XAxis, Vector3d.ZAxis);
    private static RhinoDoc TwoBoxes()
    {
        var doc = RhinoDoc.CreateHeadless(null);
        doc.ModelUnitSystem = UnitSystem.Meters;
        doc.ModelAbsoluteTolerance = Tol;
        doc.Objects.AddBrep(Brep.CreateFromBox(new BoundingBox(new Point3d(0, 1, 0), new Point3d(1, 2, 1))));
        doc.Objects.AddBrep(Brep.CreateFromBox(new BoundingBox(new Point3d(3, 6, 0), new Point3d(4, 7, 1))));
        return doc;
    }

    private sealed record Drawn(string Info, double MaxX, string[] Messages);

    /// <summary>Solves one component in a fresh GH document against the active Rhino document.</summary>
    private static Drawn Draw(IGH_Goo view, string? depth = null, string? settings = null, Action<GH_Component>? setup = null)
    {
        using var gh = new GH_Document { Enabled = true };
        var component = NewComponent(gh);
        using var cleanup = new Cleanup(() => { component.RemovedFromDocument(gh); SceneCache.Shared.Clear(); });
        ((Param_GenericObject)component.Params.Input[1]).PersistentData.Append(view);
        if (settings is not null) ((Param_String)component.Params.Input[3]).PersistentData.Append(new GH_String(settings));
        if (depth is not null) ((Param_String)component.Params.Input[4]).PersistentData.Append(new GH_String(depth));
        setup?.Invoke(component);
        gh.NewSolution(true, GH_SolutionMode.Silent);
        GrasshopperTests.PumpUntil(() => component.Params.Output[8].VolatileDataCount == 1, () => $"{component.Message}: " +
            string.Join("; ", component.RuntimeMessages(GH_RuntimeMessageLevel.Error)),
            () => { if (gh.ScheduleDelay >= 0) gh.NewSolution(false, GH_SolutionMode.Silent); });
        return Read(component);
    }

    private static Drawn Read(GH_Component component)
    {
        var info = ((GH_String)component.Params.Output[8].VolatileData.AllData(true).Single()).Value;
        var curves = component.Params.Output[0].VolatileData.AllData(true).Concat(component.Params.Output[2].VolatileData.AllData(true)).OfType<GH_Curve>().Select(c => c.Value).ToArray();
        var maxX = curves.Length == 0 ? double.NaN : curves.Max(c => c.GetBoundingBox(true).Max.X);
        var messages = new[] { GH_RuntimeMessageLevel.Remark, GH_RuntimeMessageLevel.Warning, GH_RuntimeMessageLevel.Error }
            .SelectMany(component.RuntimeMessages).ToArray();
        return new(info, maxX, messages);
    }

    [RhinoFact] public void DepthInputSetsThePlaneViewFarLimitAndBeatsSettingsText() => host.Run(() =>
    {
        Assert.True(global::Rhino.PlugIns.PlugIn.LoadPlugIn(GrasshopperId));
        using var doc = TwoBoxes(); RhinoDoc.ActiveDoc = doc;
        var plane = new GH_Plane(PlaneView);
        var auto = Draw(plane);
        output.WriteLine(auto.Info);
        Assert.Equal(4, auto.MaxX, 3); // auto depth reaches B (x up to 4)
        Assert.Contains("depth = 7.01 (auto)", auto.Info);
        var four = Draw(plane, depth: "4");
        output.WriteLine(four.Info + "\n" + string.Join("\n", four.Messages));
        Assert.Equal(1, four.MaxX, 3);    // B lies 6–7 behind: beyond a 4 far limit
        Assert.Contains("depth = 4 (set by Depth input)", four.Info);
        var text = Draw(plane, settings: "depth = 8");
        Assert.Equal(4, text.MaxX, 3);
        Assert.Contains("depth = 8 (set by input)", text.Info);
        var both = Draw(plane, depth: "400 cm", settings: "depth = 8");
        Assert.Equal(1, both.MaxX, 3);    // Depth input beats Settings text; units convert (400 cm = 4 m)
        Assert.Contains("depth = 4 (set by Depth input)", both.Info);
        var rectangle = Draw(new GH_Rectangle(new Rectangle3d(PlaneView, new Interval(-1, 5), new Interval(-1, 2))), depth: "4");
        Assert.Equal(1, rectangle.MaxX, 3);
        var wrongCount = Draw(plane, setup: c => { var d = (Param_String)c.Params.Input[4]; d.PersistentData.Append(new GH_String("1")); d.PersistentData.Append(new GH_String("2")); });
        Assert.Contains(wrongCount.Messages, m => m.Contains("Give one Depth, or one per view"));
        Assert.Equal(4, wrongCount.MaxX, 3);
    });

    [RhinoFact] public void BoxGeometryWinsOverTheDepthInputWithARemark() => host.Run(() =>
    {
        Assert.True(global::Rhino.PlugIns.PlugIn.LoadPlugIn(GrasshopperId));
        using var doc = TwoBoxes(); RhinoDoc.ActiveDoc = doc;
        // A GH Box 3 deep: its geometry clips B even though Depth = 10 would include it.
        var box = new GH_Box(new Box(PlaneView, new Interval(-1, 5), new Interval(-1, 2), new Interval(-3, 0)));
        var gh = Draw(box, depth: "10");
        output.WriteLine(gh.Info);
        Assert.Equal(1, gh.MaxX, 3);
        Assert.Contains(gh.Messages, m => m.Contains("geometry sets the depth (3)") && m.Contains("Depth input"));

        // A validated view box from a frame at y = −0.5 reaches the back of the scene (B included);
        // Depth = 2 would drop B, but the box wins.
        var id = doc.Objects.AddSurface(new PlaneSurface(new Plane(new Point3d(-1, -.5, -1), Vector3d.ZAxis, Vector3d.XAxis), new Interval(0, 3), new Interval(0, 6)));
        Assert.True(ViewBoxes.Validate(doc, [id]).Single().Ok);
        var viewBox = Draw(new GH_Brep(id), depth: "2");
        output.WriteLine(viewBox.Info);
        Assert.Equal(4, viewBox.MaxX, 3);
        Assert.Contains(viewBox.Messages, m => m.Contains("is a box: its geometry sets the depth"));
        // The view box itself is never drawn as scene geometry (document mode): it would enclose and hide everything.
        // Two boxes seen face-on: 4 visible edges each, nothing hidden.
        Assert.Contains("Visible=4, hidden=0", viewBox.Info);
    });

    [RhinoFact] public void SettingsPrecedenceIsInputThenViewObjectThenMenuAndFollowsUserTextEdits() => host.Run(() =>
    {
        Assert.True(global::Rhino.PlugIns.PlugIn.LoadPlugIn(GrasshopperId));
        using var doc = TwoBoxes(); RhinoDoc.ActiveDoc = doc;
        var id = doc.Objects.AddSurface(new PlaneSurface(new Plane(new Point3d(-1, -.5, -1), Vector3d.ZAxis, Vector3d.XAxis), new Interval(0, 3), new Interval(0, 6)));
        Assert.True(ViewBoxes.Validate(doc, [id]).Single().Ok);
        void SetUserText(string key, string value)
        {
            var attributes = doc.Objects.FindId(id).Attributes.Duplicate();
            attributes.SetUserString(key, value);
            doc.Objects.ModifyAttributes(id, attributes, true);
        }
        SetUserText("m2d.hidden", "off");
        var objectWins = Draw(new GH_Brep(id), setup: c => ((dynamic)c).Ui.Execute("set:hidden=on", false));
        Assert.Contains("hidden = off (view object)", objectWins.Info);
        var inputWins = Draw(new GH_Brep(id), settings: "hidden = on");
        Assert.Contains("hidden = on (set by input)", inputWins.Info);
        SetUserText("m2d.hidden", "default");
        var menu = Draw(new GH_Brep(id), setup: c => ((dynamic)c).Ui.Execute("set:hidden=off", false));
        Assert.Contains("hidden = off (component menu)", menu.Info);
        Assert.DoesNotContain(menu.Messages, m => m.Contains("not applied"));

        // A user-text edit re-runs the component with the new value. Interactive Rhino raises ModifyObjectAttributes,
        // which the component's document watch turns into Invalidate + re-solve; headless documents raise no object
        // events at all (measured), so this expires the component the same way and checks nothing stale is reused.
        using var gh = new GH_Document { Enabled = true };
        var component = NewComponent(gh);
        using var cleanup = new Cleanup(() => { component.RemovedFromDocument(gh); SceneCache.Shared.Clear(); });
        ((Param_GenericObject)component.Params.Input[1]).PersistentData.Append(new GH_Brep(id));
        gh.NewSolution(true, GH_SolutionMode.Silent);
        void Pump(Func<bool> condition) => GrasshopperTests.PumpUntil(condition, () => component.Message,
            () => { if (gh.ScheduleDelay >= 0) gh.NewSolution(false, GH_SolutionMode.Silent); });
        Pump(() => component.Params.Output[8].VolatileDataCount == 1);
        Assert.DoesNotContain("seams = on", Read(component).Info);
        SetUserText("m2d.seams", "on");
        component.ExpireSolution(true);
        Pump(() => component.Params.Output[8].VolatileDataCount == 1 && Read(component).Info.Contains("seams = on (view object)"));

        // The preview: frame, arrow and depth volume of the view box, read from the component.
        var previews = ((IEnumerable<ViewPreview>)((dynamic)component).ViewPreviews).ToArray();
        var preview = Assert.Single(previews);
        AssertVector(Vector3d.YAxis, preview.Look);
        Assert.Equal(18, preview.Frame.Area, 9);
        Assert.Equal(ViewRequest.Parse(id, doc).Cut!.SourceDepth!.Value, preview.Depth, 9);
        Assert.Equal(-.5, preview.Arrow.From.Y, 9);
        Assert.True(preview.Arrow.To.Y > preview.Arrow.From.Y);
        if (ScreenshotFolder is { } folder) RenderPreview(doc, id, previews[0], Path.Combine(folder, "view-box-preview.png"));
    });

    // ------------------------------------------------------------------ helpers

    private static GH_Component NewComponent(GH_Document document)
    {
        var component = (GH_Component)Activator.CreateInstance(Gha.GetType("M2DPlus.Grasshopper.DrawingComponent", true)!)!;
        component.CreateAttributes();
        document.AddObject(component, false);
        return component;
    }

    /// <summary>Runs a view command exactly as the menus do (ViewObjects.Execute) and returns every object's outcome.</summary>
    private static ViewObjectResult[] Run(string command, GH_Component component, RhinoDoc doc) =>
        ((IEnumerable<ViewObjectResult>)Gha.GetType("M2DPlus.Grasshopper.UI.ViewObjects", true)!.GetMethod("Execute")!
            .Invoke(null, [command, component.Params.Input[1], doc, ((dynamic)component).Ui])!).ToArray();

    /// <summary>An offscreen axonometric render of the preview lines exactly as DrawViewportWires draws them (frame,
    /// arrow, dotted depth volume) over the model's edges and the view box itself. Not a Rhino viewport capture.</summary>
    private static void RenderPreview(RhinoDoc doc, Guid viewBox, ViewPreview preview, string file)
    {
        var panels = new[] { ("Component not selected (subtle)", Color.FromArgb(150, 96, 120, 156)), ("Component selected (cobalt #1F6FD1)", ViewBoxes.Cobalt) };
        const int width = 760, height = 580;
        using var image = new Bitmap(width * panels.Length, height);
        using var g = Graphics.FromImage(image);
        g.SmoothingMode = SmoothingMode.AntiAlias; g.Clear(Color.FromArgb(190, 190, 190));
        var model = doc.Objects.GetObjectList(ObjectType.Brep).Where(o => !ViewBoxes.IsViewBox(o)).SelectMany(o => ((Brep)o.Geometry).Edges.Select(e => e.PointAtStart).Zip(((Brep)o.Geometry).Edges.Select(e => e.PointAtEnd), (a, b) => new Line(a, b))).ToArray();
        var box = (Extrusion)doc.Objects.FindId(viewBox).Geometry;
        using var boxBrep = box.ToBrep();
        var boxEdges = boxBrep.Edges.Select(e => new Line(e.PointAtStart, e.PointAtEnd)).ToArray();
        var all = model.SelectMany(l => new[] { l.From, l.To }).Concat(preview.FrontCorners).Concat(preview.BackCorners).ToArray();
        // South-west axonometric: x right-down, y right-up, z up.
        PointF Project(Point3d p) => new((float)(p.X * .87 + p.Y * .5), (float)(-p.Z + p.X * .25 - p.Y * .43));
        var projected = all.Select(Project).ToArray();
        var (minX, maxX, minY, maxY) = (projected.Min(p => p.X), projected.Max(p => p.X), projected.Min(p => p.Y), projected.Max(p => p.Y));
        var scale = Math.Min((width - 120) / (maxX - minX), (height - 140) / (maxY - minY));
        for (var i = 0; i < panels.Length; i++)
        {
            var (caption, colour) = panels[i];
            var state = g.Save();
            g.TranslateTransform(i * width, 0);
            g.FillRectangle(new SolidBrush(Color.FromArgb(206, 206, 206)), 0, 0, width, height);
            PointF P(Point3d p) { var q = Project(p); return new(60 + (q.X - minX) * scale, 100 + (q.Y - minY) * scale); }
            using (var edgePen = new Pen(Color.FromArgb(40, 40, 40), 1.5f)) foreach (var l in model) g.DrawLine(edgePen, P(l.From), P(l.To));
            using (var boxPen = new Pen(Color.FromArgb(70, 31, 111, 209), 1)) foreach (var l in boxEdges) g.DrawLine(boxPen, P(l.From), P(l.To));
            var faint = Color.FromArgb(i == 1 ? 150 : 90, colour);
            using (var dotted = new Pen(faint, 1) { DashStyle = DashStyle.Dot })
                foreach (var l in preview.DepthEdges.Concat(preview.BackEdges)) g.DrawLine(dotted, P(l.From), P(l.To));
            using (var frame = new Pen(colour, i == 1 ? 2.5f : 1.5f)) foreach (var l in preview.FrontEdges) g.DrawLine(frame, P(l.From), P(l.To));
            using (var arrow = new Pen(colour, 2) { CustomEndCap = new AdjustableArrowCap(5, 6) }) g.DrawLine(arrow, P(preview.Arrow.From), P(preview.Arrow.To));
            using var font = new Font("Segoe UI", 11); using var bold = new Font("Segoe UI", 12, FontStyle.Bold);
            g.DrawString(preview.Name, font, new SolidBrush(colour), P(preview.Frame.Corner(3)).X, P(preview.Frame.Corner(3)).Y - 20);
            g.DrawString(caption, bold, Brushes.Black, 16, 12);
            g.DrawString($"Looks {ViewBoxes.Direction(preview.Look)} · depth {preview.Depth:0.00} m (auto) · frame {preview.Frame.Width:0} × {preview.Frame.Height:0} m",
                font, Brushes.Black, 16, 46);
            using var small = new Font("Segoe UI", 9);
            g.DrawString("solid: frame (front face = cut plane) · arrow: look direction" + Environment.NewLine +
                "dotted: depth volume · faint: the view box object · black: model",
                small, Brushes.Black, 16, height - 64);
            g.Restore(state);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        image.Save(file, ImageFormat.Png);
    }
}
