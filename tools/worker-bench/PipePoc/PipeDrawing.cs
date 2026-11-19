using System.Diagnostics;
using M2DPlus.Rhino.Experimental.PipeVisibility;
using M2DPlus.Rhino.Services;
using Rhino;
using Rhino.DocObjects;
using Rhino.FileIO;
using Rhino.Geometry;

internal static class PipeDrawing
{


    internal static void Run(string model, string output, string viewName)
    {
        if (System.Environment.GetEnvironmentVariable("M2D_RHINO_TESTS") != "1")
            throw new InvalidOperationException("Set M2D_RHINO_TESTS=1 to start the headless research host.");
        if (!File.Exists(model)) throw new FileNotFoundException("Input model missing", model);
        output = Path.GetFullPath(output);
        if (!Path.GetExtension(output).Equals(".3dm", StringComparison.OrdinalIgnoreCase) ||
            File.Exists(output) || Directory.Exists(output))
            throw new IOException("Choose a new .3dm output file.");

        using var core = new Rhino.Runtime.InProcess.RhinoCore(["/nosplash", "/notemplate"],
            Rhino.Runtime.InProcess.WindowStyle.NoWindow);
        using var doc = RhinoDoc.OpenHeadless(model) ?? throw new IOException("Unable to open input model.");
        var inputs = doc.Objects.GetObjectList(ObjectType.AnyObject)
            .Select(o => new PipeGeometry.Input(o.Id.ToString(), o.Geometry)).OrderBy(i => i.Name, StringComparer.Ordinal).ToList();
        if (!inputs.Any(i => i.Geometry is Brep) || inputs.Any(i => i.Geometry is not (Brep or Curve or Rhino.Geometry.Point)))
            throw new NotSupportedException("Supply closed pipe Breps with optional loose curves and points.");

        var bounds = BoundingBox.Empty;
        foreach (var input in inputs) bounds.Union(input.Geometry.GetBoundingBox(true));
        if (doc.ModelUnitSystem != UnitSystem.Millimeters)
            throw new NotSupportedException("The pipe experiment uses millimetre geometry.");
        var tolerance = doc.ModelAbsoluteTolerance;
        var view = ViewRequest.Parse(viewName);
        var frame = view.Resolve(bounds, tolerance).Plane;
        var strokes = new List<PipeGeometry.Stroke>();
        var dots = new List<PipeGeometry.Dot>();
        var timer = Stopwatch.StartNew();
        try
        {
            SurfaceEvents.Draw(inputs, frame, view.Direction, tolerance, doc.ModelAngleToleranceRadians,
                Math.Min(8, System.Environment.ProcessorCount), strokes, dots);
            using var drawing = new File3dm();
            drawing.Settings.ModelUnitSystem = doc.ModelUnitSystem;
            drawing.Settings.ModelAbsoluteTolerance = tolerance;
            var visible = drawing.AllLayers.AddLayer("Visible", System.Drawing.Color.Black);
            var hidden = drawing.AllLayers.AddLayer("Hidden", System.Drawing.Color.Gray);
            if (visible < 0 || hidden < 0) throw new IOException("Unable to create drawing layers.");
            var project = Transform.PlanarProjection(Plane.WorldXY) * Transform.PlaneToPlane(frame, Plane.WorldXY);
            ObjectAttributes Attributes(string kind, string source) => new()
            {
                Name = kind + "|" + source,
                LayerIndex = kind == "Visible" ? visible : hidden
            };
            foreach (var stroke in strokes)
            {
                using var attrs = Attributes(stroke.Class, stroke.Source);
                if (!stroke.Curve.Transform(project) || drawing.Objects.AddCurve(stroke.Curve, attrs) == Guid.Empty)
                    throw new IOException("Failed to export drawing curve.");
            }
            foreach (var dot in dots.Where(d => d.Class == "Visible"))
            {
                var point = dot.Position;
                point.Transform(project);
                using var attrs = Attributes(dot.Class, dot.Source);
                if (drawing.Objects.AddPoint(point, attrs) == Guid.Empty) throw new IOException("Failed to export point.");
            }
            timer.Stop();
            // Publish a complete archive even when two invocations choose the same output.
            var folder = Path.GetDirectoryName(output)!;
            Directory.CreateDirectory(folder);
            var temporary = Path.Combine(folder, $".pipe-poc-{Guid.NewGuid():N}.3dm");
            try
            {
                if (!drawing.Write(temporary, 8)) throw new IOException("Drawing write failed.");
                File.Move(temporary, output);
            }
            finally { File.Delete(temporary); }
            Console.WriteLine($"{viewName}: {strokes.Count(s => s.Class == "Visible")} visible curves, " +
                $"{strokes.Count(s => s.Class == "Hidden")} hidden curves. Drawing time {timer.Elapsed.TotalMilliseconds:F0} ms. {output}");
        }
        finally { foreach (var stroke in strokes) stroke.Curve.Dispose(); }
    }
}

