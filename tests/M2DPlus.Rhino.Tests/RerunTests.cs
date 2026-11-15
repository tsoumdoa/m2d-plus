using System.Diagnostics;
using System.Drawing;
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

namespace M2DPlus.Rhino.Tests;

/// <summary>
/// What re-runs the component (owner request 2026-11-15, UX §5.1): every document change is made to a running GH
/// definition and the component's solves and native HLD calls are counted. Spheres force native HLD, so a visibility
/// re-solve shows as HLD calls. Headless documents may raise no Rhino events, so after each edit the watch's snapshot
/// check runs as well (it applies the same policy as the events).
/// </summary>
[Collection("Rhino")]
[Trait("Category", "RhinoIntegration")]
public sealed class RerunTests(RhinoHost host, ITestOutputHelper output)
{
    private static readonly Guid GrasshopperId = new("b45a29b1-4343-4035-989e-044e8580d9cf");
    private static Assembly Gha => Assembly.LoadFrom(Path.Combine(AppContext.BaseDirectory, "M2DPlus.Grasshopper.gha"));

    private sealed class Rig : IDisposable
    {
        public required RhinoDoc Rhino;
        public required GH_Document Gh;
        public required GH_Component Component;
        public required Guid Drawn, Other, ViewBox;
        public required int Walls, OtherLayer, Empty, Building;
        public DocumentWatch Watch => (DocumentWatch)Field("_watch")!;
        public int Solves => (int)Field("_solveCount")!;
        private object? Field(string name) => Component.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(Component);
        public int Infos => Component.Params.Output[8].VolatileDataCount;
        public void Dispose()
        {
            Component.RemovedFromDocument(Gh); Gh.Dispose(); SceneCache.Shared.Clear(); Rhino.Dispose();
        }
    }

    /// <param name="layerName">Geometry is this layer-name text instead of a referenced object.</param>
    private static Rig Build(bool documentMode, string? layerName = null)
    {
        var rhino = RhinoDoc.CreateHeadless(null); RhinoDoc.ActiveDoc = rhino;
        GH_Document.EnableSolutions = true;
        var building = rhino.Layers.Add("Building", Color.Black);
        var walls = rhino.Layers.Add(new Layer { Name = "Walls", ParentLayerId = rhino.Layers[building].Id, Color = Color.Black });
        var other = rhino.Layers.Add("Other", Color.Black);
        var empty = rhino.Layers.Add("Empty", Color.Black);
        Guid Sphere(Point3d centre, int layer)
        { using var brep = new Sphere(centre, 1).ToBrep(); return rhino.Objects.AddBrep(brep, new ObjectAttributes { LayerIndex = layer }); }
        var drawn = Sphere(new Point3d(0, 0, 1), walls);
        var otherId = Sphere(new Point3d(4, 0, 1), other);
        // A plan view box above the model, referenced on V.
        var frame = rhino.Objects.AddSurface(new PlaneSurface(new Plane(new Point3d(-5, -5, 5), Vector3d.XAxis, Vector3d.YAxis), new Interval(0, 15), new Interval(0, 10)));
        Assert.True(Assert.Single(ViewBoxes.Validate(rhino, [frame])).Ok);
        var gh = new GH_Document { Enabled = true };
        var component = (GH_Component)Activator.CreateInstance(Gha.GetType("M2DPlus.Grasshopper.DrawingComponent", true)!)!;
        gh.AddObject(component, false);
        if (layerName is not null)
            ((Param_GenericObject)component.Params.Input[0]).PersistentData.Append(new GH_String(layerName));
        else if (!documentMode)
        {
            var reference = new GH_Brep(); reference.ReferenceID = drawn;
            ((Param_GenericObject)component.Params.Input[0]).PersistentData.Append(reference);
        }
        var view = new GH_Brep(); view.ReferenceID = frame;
        ((Param_GenericObject)component.Params.Input[1]).PersistentData.Append(view);
        gh.NewSolution(true, GH_SolutionMode.Silent);
        var rig = new Rig { Rhino = rhino, Gh = gh, Component = component, Drawn = drawn, Other = otherId, ViewBox = frame, Walls = walls, OtherLayer = other, Empty = empty, Building = building };
        Pump(rig, () => rig.Infos == 1);
        Assert.True(component.Params.Output[0].VolatileDataCount > 0, component.Message);
        return rig;
    }

    private static void Pump(Rig rig, Func<bool> until, double seconds = 20)
    {
        var timer = Stopwatch.StartNew();
        while (!until() && timer.Elapsed < TimeSpan.FromSeconds(seconds))
        {
            RhinoApp.Wait(); System.Windows.Forms.Application.DoEvents();
            if (rig.Gh.ScheduleDelay >= 0) rig.Gh.NewSolution(false, GH_SolutionMode.Silent);
            Thread.Sleep(5);
        }
        Assert.True(until(), $"{rig.Component.Message}; reruns={rig.Watch.Reruns}; last={rig.Watch.LastReason}");
    }

    /// <summary>Makes one edit and checks whether the component re-ran, and whether the re-run solved visibility again.</summary>
    private void Expect(Rig rig, string what, Action edit, bool rerun, bool? solved = null)
    {
        var solves = rig.Solves; var hld = NativeDrawing.HldCalls; var reruns = rig.Watch.Reruns;
        edit();
        rig.Watch.VerifyHostState();
        if (rerun) Pump(rig, () => rig.Solves >= solves + 2 && rig.Infos == 1);
        else
        {
            var quiet = Stopwatch.StartNew();
            while (quiet.Elapsed < TimeSpan.FromMilliseconds(600))
            { RhinoApp.Wait(); System.Windows.Forms.Application.DoEvents(); if (rig.Gh.ScheduleDelay >= 0) rig.Gh.NewSolution(false, GH_SolutionMode.Silent); Thread.Sleep(5); }
        }
        var calls = NativeDrawing.HldCalls - hld;
        output.WriteLine($"{what}: solves +{rig.Solves - solves}, HLD calls +{calls}, watch re-runs +{rig.Watch.Reruns - reruns} ({rig.Watch.LastReason}); ignored {rig.Watch.Ignored}; scope {rig.Watch.Scope?.ObjectCount}");
        if (rerun) Assert.True(rig.Solves > solves, what + ": expected a re-run");
        else
        {
            Assert.True(rig.Solves == solves, $"{what}: unexpected re-run ({rig.Watch.LastReason})");
            Assert.Equal(0, calls);
            Assert.Equal(1, rig.Infos);
        }
        if (solved is { } s) Assert.True(s ? calls > 0 : calls == 0, $"{what}: HLD calls +{calls}, expected {(s ? "some" : "none")}");
    }

    private static void Attributes(RhinoDoc doc, Guid id, Action<ObjectAttributes> change)
    {
        var obj = doc.Objects.FindId(id); var attributes = obj.Attributes.Duplicate(); change(attributes);
        Assert.True(doc.Objects.ModifyAttributes(obj, attributes, true));
    }
    private static void LayerVisible(RhinoDoc doc, int index, bool visible)
    {
        var layer = doc.Layers[index]; layer.IsVisible = visible; doc.Layers.Modify(layer, index, true);
    }
    private static Guid AddSphere(RhinoDoc doc, Point3d centre, ObjectAttributes attributes)
    { using var brep = new Sphere(centre, .5).ToBrep(); return doc.Objects.AddBrep(brep, attributes); }

    [RhinoFact] public void WithGeometryGivenVisibilityAndViewportChangesNeverReRun() => host.Run(() =>
    {
        Assert.True(global::Rhino.PlugIns.PlugIn.LoadPlugIn(GrasshopperId));
        using var rig = Build(documentMode: false);
        var doc = rig.Rhino;
        Expect(rig, "hide the drawn object", () => doc.Objects.Hide(rig.Drawn, true), rerun: false);
        Expect(rig, "show the drawn object", () => doc.Objects.Show(rig.Drawn, true), rerun: false);
        Expect(rig, "turn its layer off", () => LayerVisible(doc, rig.Walls, false), rerun: false);
        Expect(rig, "turn its layer on", () => LayerVisible(doc, rig.Walls, true), rerun: false);
        Expect(rig, "hide another object", () => doc.Objects.Hide(rig.Other, true), rerun: false);
        Expect(rig, "turn another layer off", () => LayerVisible(doc, rig.OtherLayer, false), rerun: false);
        Expect(rig, "per-viewport layer visibility", () =>
        { var layer = doc.Layers[rig.Walls]; layer.SetPerViewportVisible(Guid.NewGuid(), false); doc.Layers.Modify(layer, rig.Walls, true); }, rerun: false);
        Expect(rig, "hide in one detail", () => Attributes(doc, rig.Drawn, a => a.AddHideInDetailOverride(Guid.NewGuid())), rerun: false);
        Expect(rig, "lock the drawn object", () => doc.Objects.Lock(rig.Drawn, true), rerun: false);
        Expect(rig, "other user text", () => Attributes(doc, rig.Drawn, a => a.SetUserString("note", "x")), rerun: false);
        Expect(rig, "another object's colour", () => Attributes(doc, rig.Other, a => { a.ColorSource = ObjectColorSource.ColorFromObject; a.ObjectColor = Color.Red; }), rerun: false);
        Expect(rig, "delete another object", () => doc.Objects.Delete(rig.Other, true), rerun: false);
        Expect(rig, "add an object", () => AddSphere(doc, new Point3d(8, 0, 1), new ObjectAttributes { LayerIndex = rig.Walls }), rerun: false);
        Expect(rig, "hide the view box", () => doc.Objects.Hide(rig.ViewBox, true), rerun: false);
        Expect(rig, "show the view box", () => doc.Objects.Show(rig.ViewBox, true), rerun: false);
        Expect(rig, "no document change (a camera or display-mode change raises none)", () => { }, rerun: false);
        // The $M2D Views front-face index only reads the document: refreshing it never re-runs the component.
        Expect(rig, "front-face fill refresh", () => { using var faces = new ViewBoxFaceIndex(doc); faces.Refresh(); Assert.NotEmpty(faces.Faces); }, rerun: false);
        // Real changes to the referenced input and to the view object do re-run.
        Expect(rig, "colour of the drawn object", () => Attributes(doc, rig.Drawn, a => { a.ColorSource = ObjectColorSource.ColorFromObject; a.ObjectColor = Color.Blue; }), rerun: true, solved: false);
        Expect(rig, "rename the drawn object (Key labels)", () => Attributes(doc, rig.Drawn, a => a.Name = "Column A"), rerun: true, solved: false);
        Expect(rig, "view box setting m2d.hidden", () => Attributes(doc, rig.ViewBox, a => a.SetUserString("m2d.hidden", "off")), rerun: true, solved: false);
        Expect(rig, "move the drawn object", () => doc.Objects.Transform(rig.Drawn, Transform.Translation(0, 1, 0), true), rerun: true, solved: true);
    });

    /// <summary>Owner decision 2026-11-15: a layer name selects every object on matching layers whatever its visibility,
    /// so hiding, showing and switching those layers (or a parent) never re-run; joining or leaving the selection does.</summary>
    [RhinoFact] public void LayerNameGeometryIgnoresVisibilityButFollowsMembership() => host.Run(() =>
    {
        Assert.True(global::Rhino.PlugIns.PlugIn.LoadPlugIn(GrasshopperId));
        using var rig = Build(documentMode: false, layerName: "Walls");
        var doc = rig.Rhino;
        Expect(rig, "hide an object on the layer", () => Assert.True(doc.Objects.Hide(rig.Drawn, true)), rerun: false);
        Expect(rig, "show it", () => Assert.True(doc.Objects.Show(rig.Drawn, true)), rerun: false);
        Expect(rig, "turn the layer off", () => LayerVisible(doc, rig.Walls, false), rerun: false);
        Expect(rig, "turn the layer on", () => LayerVisible(doc, rig.Walls, true), rerun: false);
        Expect(rig, "turn the parent layer off", () => LayerVisible(doc, rig.Building, false), rerun: false);
        Expect(rig, "turn the parent layer on", () => LayerVisible(doc, rig.Building, true), rerun: false);
        Expect(rig, "per-viewport layer visibility", () =>
        { var layer = doc.Layers[rig.Walls]; layer.SetPerViewportVisible(Guid.NewGuid(), false); doc.Layers.Modify(layer, rig.Walls, true); }, rerun: false);
        Expect(rig, "hide in one detail", () => Attributes(doc, rig.Drawn, a => a.AddHideInDetailOverride(Guid.NewGuid())), rerun: false);
        Expect(rig, "add an object on another layer", () => AddSphere(doc, new Point3d(8, 4, 1), new ObjectAttributes { LayerIndex = rig.OtherLayer }), rerun: false);
        Expect(rig, "hide an object on another layer", () => Assert.True(doc.Objects.Hide(rig.Other, true)), rerun: false);
        Expect(rig, "show it", () => Assert.True(doc.Objects.Show(rig.Other, true)), rerun: false);
        // Membership and content changes re-run, hidden objects included.
        Guid added = Guid.Empty;
        Expect(rig, "add a hidden object on the layer", () =>
        { added = AddSphere(doc, new Point3d(8, 0, 1), new ObjectAttributes { LayerIndex = rig.Walls }); Assert.True(doc.Objects.Hide(added, true)); }, rerun: true, solved: true);
        Assert.Contains(rig.Component.Params.Output[10].VolatileData.AllData(true).Cast<GH_String>(), k => k.Value.Contains("Walls"));
        Expect(rig, "move an object onto the layer", () => Attributes(doc, rig.Other, a => a.LayerIndex = rig.Walls), rerun: true, solved: true);
        Expect(rig, "move it off again", () => Attributes(doc, rig.Other, a => a.LayerIndex = rig.OtherLayer), rerun: true, solved: true);
        Expect(rig, "edit an object on the layer", () => doc.Objects.Transform(rig.Drawn, Transform.Translation(0, 1, 0), true), rerun: true, solved: true);
        Expect(rig, "colour of an object on the layer", () => Attributes(doc, rig.Drawn, a => { a.ColorSource = ObjectColorSource.ColorFromObject; a.ObjectColor = Color.Red; }), rerun: true, solved: false);
        Expect(rig, "delete the hidden object on the layer", () => Assert.True(doc.Objects.Delete(doc.Objects.FindId(added), true, ignoreModes: true)), rerun: true, solved: true);
    });

    [RhinoFact] public void DocumentModeFollowsModelVisibilityButNeverViewportChanges() => host.Run(() =>
    {
        Assert.True(global::Rhino.PlugIns.PlugIn.LoadPlugIn(GrasshopperId));
        using var rig = Build(documentMode: true);
        var doc = rig.Rhino;
        Expect(rig, "per-viewport layer visibility", () =>
        { var layer = doc.Layers[rig.Walls]; layer.SetPerViewportVisible(Guid.NewGuid(), false); doc.Layers.Modify(layer, rig.Walls, true); }, rerun: false);
        Expect(rig, "hide in one detail", () => Attributes(doc, rig.Drawn, a => a.AddHideInDetailOverride(Guid.NewGuid())), rerun: false);
        Expect(rig, "lock an object", () => Assert.True(doc.Objects.Lock(rig.Drawn, true)), rerun: false);
        Expect(rig, "turn an empty layer off", () => LayerVisible(doc, rig.Empty, false), rerun: false);
        Expect(rig, "add a hidden object", () => { var id = AddSphere(doc, new Point3d(8, 0, 1), new ObjectAttributes { LayerIndex = rig.OtherLayer }); doc.Objects.Hide(id, true); }, rerun: false);
        Expect(rig, "add a baked M2D+ object", () =>
        {
            var attributes = new ObjectAttributes { LayerIndex = rig.Walls }; attributes.SetUserString("m2d.class", "visible");
            doc.Objects.AddLine(new Line(new Point3d(0, 0, 0), new Point3d(1, 0, 0)), attributes);
        }, rerun: false);
        Expect(rig, "hide the view box", () => doc.Objects.Hide(rig.ViewBox, true), rerun: false);
        Expect(rig, "show the view box", () => doc.Objects.Show(rig.ViewBox, true), rerun: false);
        Expect(rig, "front-face fill refresh", () => { using var faces = new ViewBoxFaceIndex(doc); faces.Refresh(); }, rerun: false);
        Expect(rig, "no document change", () => { }, rerun: false);
        Expect(rig, "colour of a drawn object", () => Attributes(doc, rig.Other, a => { a.ColorSource = ObjectColorSource.ColorFromObject; a.ObjectColor = Color.Red; }), rerun: true, solved: false);
        Expect(rig, "hide a drawn object", () => Assert.True(doc.Objects.Hide(rig.Other, true)), rerun: true, solved: true);
        Expect(rig, "show it again", () => Assert.True(doc.Objects.Show(rig.Other, true)), rerun: true);
        Expect(rig, "turn a layer with objects off", () => LayerVisible(doc, rig.OtherLayer, false), rerun: true);
        Expect(rig, "turn it on", () => LayerVisible(doc, rig.OtherLayer, true), rerun: true);
        Expect(rig, "add a visible object", () => AddSphere(doc, new Point3d(8, 4, 1), new ObjectAttributes { LayerIndex = rig.Walls }), rerun: true, solved: true);
    });

    [RhinoFact] public void PreviewColoursAreCapturedPerSourceWithoutASolve() => host.Run(() =>
    {
        Assert.True(global::Rhino.PlugIns.PlugIn.LoadPlugIn(GrasshopperId));
        using var rig = Build(documentMode: true);
        var doc = rig.Rhino;
        var walls = doc.Layers[rig.Walls]; walls.Color = Color.FromArgb(0, 128, 0); doc.Layers.Modify(walls, rig.Walls, true);
        Attributes(doc, rig.Other, a => { a.ColorSource = ObjectColorSource.ColorFromObject; a.ObjectColor = Color.FromArgb(200, 0, 0); });
        rig.Watch.VerifyHostState();
        Pump(rig, () => Colours(rig) is { Count: > 0 } c && c.Count >= 2 && rig.Infos == 1);
        var hld = NativeDrawing.HldCalls;
        var colours = Colours(rig)!;
        Assert.Equal(Color.FromArgb(0, 128, 0).ToArgb(), unchecked((int)colours[rig.Drawn.ToString()]));
        Assert.Equal(Color.FromArgb(200, 0, 0).ToArgb(), unchecked((int)colours[rig.Other.ToString()]));
        // A layer colour change reaches the preview colours through the cached solve: no HLD call.
        walls = doc.Layers[rig.Walls]; walls.Color = Color.FromArgb(0, 0, 160); doc.Layers.Modify(walls, rig.Walls, true);
        rig.Watch.VerifyHostState();
        Pump(rig, () => Colours(rig) is { } c && c.TryGetValue(rig.Drawn.ToString(), out var argb) && unchecked((int)argb) == Color.FromArgb(0, 0, 160).ToArgb() && rig.Infos == 1);
        Assert.Equal(hld, NativeDrawing.HldCalls);
    });

    private static IReadOnlyDictionary<string, uint>? Colours(Rig rig)
    {
        var sources = rig.Component.GetType().GetField("_drawingSources", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(rig.Component);
        return (IReadOnlyDictionary<string, uint>?)sources?.GetType().GetProperty("Colours")!.GetValue(sources);
    }
}
