using M2DPlus.Core;
using M2DPlus.Rhino.Services;
using Rhino;
using Rhino.Geometry;

// Same probe is compiled against c82263c and this trial to compare the untouched HLD route.
internal static class PipeTrialBaseline
{
    internal static void Run(string model, string output, string engine)
    {
        if (Environment.GetEnvironmentVariable("M2D_RHINO_TESTS") != "1") throw new InvalidOperationException("Set M2D_RHINO_TESTS=1");
        if (File.Exists(output)) throw new IOException("Choose a fresh evidence path");
        using var core = new Rhino.Runtime.InProcess.RhinoCore(["/nosplash", "/notemplate"], Rhino.Runtime.InProcess.WindowStyle.NoWindow);
        using var doc = RhinoDoc.OpenHeadless(model);
        using var cache = new SceneCache(); using var watch = new DocumentWatch(doc);
        using var capture = new SceneCapture(doc, watch, null, cache);
        while (!capture.Step(TimeSpan.FromSeconds(30))) { }
        using var scene = capture.Accept();
        var rows = new List<object>();
        foreach (var name in new[] { "Top", "SW" })
        for (var repeat = 0; repeat < 4; repeat++)
        {
            var view = ViewRequest.Parse(name);
            var text = "hidden=on\ntangents=on\nfarclip=none" + (engine == "default" ? "" : "\nvisibility=" + engine);
            view = view with { Settings = Settings.Resolve([new("v", name, view.Kind, false, scene.Value.Bounds.Diagonal.Length, scene.Value.Tolerance)],
                [[text]], DocumentUnits.Millimetres).Views[0] };
            var nativeInputs = new List<object>(); var flags = new List<NativeHldOptions>();
            using var batch = NativeDrawing.SolveBorrowedAsync(scene.Value, new(scene.Value.Revision, "trial", "hld", repeat),
                [view], new() { ObserveHldOptions = flags.Add, ObserveNativeInputs = inputs =>
                nativeInputs.AddRange(inputs.Select(i => (object)new { i.Source.Identity, i.Far,
                    Geometry = GeometryFingerprint.Compute(i.Geometry) })) }, default, null).GetAwaiter().GetResult();
            var result = batch.Views[0];
            if (result.Error is not null) throw new InvalidOperationException(result.Error);
            var projected = new List<object>();
            foreach (var piece in result.Items)
            {
                using var geometry = piece.CopyGeometry();
                if (geometry is not Curve curve) continue;
                projected.Add(new { piece.SourceIdentity, Class = piece.Class.ToString(), Length = curve.GetLength(),
                    Samples = Enumerable.Range(0, 33).Select(i => curve.PointAt(curve.Domain.ParameterAt(i / 32d)))
                        .Select(p => new[] { p.X, p.Y, p.Z }).ToArray() });
            }
            rows.Add(new { name, repeat, result.Seconds, result.NativeCalls, result.Info, nativeInputs, flags, Policy = HldProjection.Policy, Frame = result.Frame, Pieces = projected });
        }
        File.WriteAllBytes(output, WorkerCodec.Encode(new { Runtime = Environment.Version.ToString(), Fingerprint = WorkerCodec.Fingerprint(), Model = model, engine, Rows = rows }));
        Console.WriteLine($"Wrote {output}");
    }
}
