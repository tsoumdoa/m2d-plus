using M2DPlus.Core;
using M2DPlus.Rhino.Services;
using Rhino.Geometry;
namespace NativeHillclimb;
internal static class ScorerChecks
{
    internal static void Run()
    {
        using var sourceGeometry = new LineCurve(new Point3d(0,0,0), new Point3d(10,0,0));
        var source = new PreparedLeaf("oracle-source", new[] {0}, sourceGeometry);
        ViewDrawing Lines(params (double A, double B, LineClass C)[] spans)
        {
            var drawing = new ViewDrawing();
            foreach (var s in spans) drawing.Pieces.Add(new(new LineCurve(new Point3d(s.A,0,0), new Point3d(s.B,0,0)), source, s.C));
            return drawing;
        }
        using var baseline = Lines((0, 10, LineClass.Visible));
        using var split = Lines((0, 4, LineClass.Visible), (4,10,LineClass.Visible));
        using var missing = Lines((0, 4, LineClass.Visible), (5,10,LineClass.Visible));
        using var extra = Lines((0,11,LineClass.Visible));
        using var wrongClass = Lines((0,10,LineClass.Hidden));
        using var duplicate = Lines((0,10,LineClass.Visible), (2,5,LineClass.Visible));
        using var reverse = Lines((10,0,LineClass.Visible));
        using var wrongProfile = Lines((0,10,LineClass.Visible)); wrongProfile.Pieces[0].Profile = true;
        using var duplicateAtA = Lines((0,10,LineClass.Visible), (1,2,LineClass.Visible));
        using var duplicateAtB = Lines((0,10,LineClass.Visible), (5,6,LineClass.Visible));
        var checks = new[] { Locus.Compare(baseline, split, 1e-4).Passed, !Locus.Compare(baseline, missing, 1e-4).Passed,
            !Locus.Compare(baseline, extra, 1e-4).Passed, !Locus.Compare(baseline, wrongClass, 1e-4).Passed,
            !Locus.Compare(baseline, duplicate, 1e-4).Passed, Locus.Compare(baseline, reverse, 1e-4).Passed, !Locus.Compare(duplicateAtA, duplicateAtB, 1e-4).Passed, !Locus.Compare(baseline, wrongProfile, 1e-4).Passed };
        if (checks.Any(p => !p)) throw new InvalidOperationException("Scorer oracle failure: " + string.Join(',', checks));
        using var round = new ViewDrawing(); using var splitRound = new ViewDrawing();
        var circle = new Circle(Plane.WorldXY, 3).ToNurbsCurve();
        round.Pieces.Add(new(circle, source, LineClass.Visible));
        var mid = circle.Domain.Mid;
        splitRound.Pieces.Add(new(circle.Trim(new Interval(circle.Domain.Min, mid)), source, LineClass.Visible));
        splitRound.Pieces.Add(new(circle.Trim(new Interval(mid, circle.Domain.Max)), source, LineClass.Visible));
        if (!Locus.Compare(round, splitRound, 1e-5).Passed) throw new InvalidOperationException("Curved split oracle failed");
        CullingChecks();
    }
    private static void CullingChecks()
    {
        using var doc = Rhino.RhinoDoc.CreateHeadless(null); doc.ModelAbsoluteTolerance = 1e-5;
        using var box = Brep.CreateFromBox(new BoundingBox(new Point3d(-1,-1,0), new Point3d(1,1,1)));
        if (!Experiments.Box(box, out _)) throw new InvalidOperationException("Exact box recognizer rejected box oracle");
        doc.Objects.AddBrep(box);
        doc.Objects.AddLine(new Point3d(-.5,0,-1),new Point3d(.5,0,-1));
        doc.Objects.AddLine(new Point3d(2,0,-1),new Point3d(3,0,-1));
        using var cache = new SceneCache(); using var watch = new DocumentWatch(doc); using var capture = new SceneCapture(doc, watch, cache:cache);
        while(!capture.Step(TimeSpan.FromMilliseconds(12))) { }
        using var lease = capture.Accept(); var scene = lease.Value; var sources = scene.Prepare(default);
        foreach(var hidden in new[] {true,false})
        {
            var view = ViewRequest.Parse("Top");
            var ctx = new ViewSettingsContext("oracle", "Top", view.Kind, false, 4, 1e-5);
            var resolved = Settings.Resolve(new[] {ctx}, new[] {new[] {$"hidden={(hidden ? "on" : "off")}", "farclip=none"}}, DocumentUnits.Metres);
            view = view with {Settings=resolved.Views[0]};
            using var input = Experiments.Prepare("cull", sources, scene, view);
            if(input.Removed != (hidden ? 0 : 1)) throw new InvalidOperationException("Whole-object occlusion certificate oracle failed");
            var cropSettings = Settings.Resolve(new[] {ctx with {Source=ViewSourceKind.NamedView}}, new[] {new[] {$"hidden={(hidden ? "on" : "off")}", "farclip=none"}}, DocumentUnits.Metres);
            var cropped = view with {Settings=cropSettings.Views[0], Named = new NamedFrame(new Plane(new Point3d(0,0,5), Vector3d.XAxis, Vector3d.YAxis),new Interval(-1.1,1.1),new Interval(-1.1,1.1))};
            using var croppedInput = Experiments.Prepare("cull", sources, scene, cropped);
            if(croppedInput.Removed != (hidden ? 1 : 2)) throw new InvalidOperationException("Outside-crop certificate oracle failed");
        }
    }
}
