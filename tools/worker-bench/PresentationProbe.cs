using System.Diagnostics;
using M2DPlus.Rhino.Sections;
using M2DPlus.Rhino.Services;

internal static class PresentationProbe
{
    internal static void Run(string input, string output, int selected)
    {
        Directory.CreateDirectory(output);
        using var core = new Rhino.Runtime.InProcess.RhinoCore(["/nosplash", "/notemplate"], Rhino.Runtime.InProcess.WindowStyle.NoWindow);
        var rows = new List<object>();
        foreach (var index in selected < 0 ? Enumerable.Range(0, 10) : [selected])
        {
            var request = WorkerCodec.Decode<WorkerCodec.Request>(File.ReadAllBytes(Path.Combine(input, $"request-{index}.json")));
            using var drawing = WorkerCodec.RestoreDrawing(File.ReadAllBytes(Path.Combine(input, $"drawing-{index}.json")));
            var before = WorkerCodec.Hash(WorkerCodec.CaptureDrawing(drawing, true));
            using var raw = new DrawingBatch(request.Identity, [drawing]);
            var clock = Stopwatch.StartNew();
            try
            {
                using var presented = DrawingPresentation.Apply(raw, request.Identity, [request.View], []);
                var view = presented.Views.Single();
                var seconds = clock.Elapsed.TotalSeconds;
                if (view.Error is not null) throw new InvalidOperationException(view.Error);
                if (view.Regions.Count != drawing.Regions.Count) throw new InvalidOperationException("Presentation changed the certified region count.");
                if (view.Regions.Sum(r => r.Region.Loops.Count) != drawing.Regions.Sum(r => r.Region.Loops.Count) ||
                    view.Regions.Count(r => r.Region.Hatch is not null) != drawing.Regions.Count(r => r.Region.Hatch is not null))
                    throw new InvalidOperationException("Presentation changed certified loop or hatch counts.");
                var expected = drawing.Regions.GroupBy(r => string.Join("/", r.Region.SourceIds.Order())).ToDictionary(g => g.Key, g => g.Sum(r => r.Region.Area));
                var actual = view.Regions.GroupBy(r => string.Join("/", r.Region.SourceIds.Order())).ToDictionary(g => g.Key, g => g.Sum(r => r.Region.Area));
                foreach (var (source, area) in expected)
                    if (!actual.TryGetValue(source, out var value) || Math.Abs(area - value) > SectionGeometry.AreaError(area, drawing.Tolerance))
                        throw new InvalidOperationException("Presentation changed certified material area for " + source);
                if (before != WorkerCodec.Hash(WorkerCodec.CaptureDrawing(drawing, true))) throw new InvalidOperationException("Presentation changed its borrowed drawing.");
                File.WriteAllBytes(Path.Combine(output, $"drawing-{index}.json"), WorkerCodec.CaptureDrawing(view));
                rows.Add(new { index, seconds, regions = view.Regions.Count, hatches = view.Regions.Count(r => r.Region.Hatch is not null), before,
                    after = WorkerCodec.Hash(WorkerCodec.CaptureDrawing(view, true)), view.Error, unchangedInput = true, areaAgrees = true });
                Console.WriteLine($"Plan {index}: presentation {seconds:F3}s; regions={view.Regions.Count}; areas agree; input unchanged");
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(output, $"failure-{index}.txt"), ex.ToString());
                var failures = new List<object>();
                foreach (var entry in drawing.Regions)
                {
                    try { using var single = SectionRegionEngine.Restyle([entry.Region], new(drawing.Frame.Plane, drawing.Tolerance, .001)); }
                    catch (Exception regionFailure)
                    {
                        failures.Add(new { entry.Region.Id, entry.Region.SourceIds, error = regionFailure.Message,
                            loops = entry.Region.Loops.Select(l => new { type = l.Curve.GetType().Name, l.Curve.Domain,
                                edges = l.Edges.Select(e => new { type = e.Curve.GetType().Name, e.Curve.Domain, e.Origin,
                                    start = e.Curve.PointAtStart, end = e.Curve.PointAtEnd }).ToArray() }).ToArray() });
                        var saved = WorkerCodec.Decode<WorkerCodec.Drawing>(WorkerCodec.CaptureDrawing(drawing, true));
                        File.WriteAllBytes(Path.Combine(output, "failed-region.json"), WorkerCodec.Encode(saved with { Pieces = [], Regions = saved.Regions.Where(r => r.Id == entry.Region.Id).ToArray() }));
                    }
                }
                File.WriteAllBytes(Path.Combine(output, "failed-regions.json"), WorkerCodec.Encode(failures));
                throw;
            }
            finally { File.WriteAllBytes(Path.Combine(output, "presentation.json"), WorkerCodec.Encode(new { fingerprint = WorkerCodec.Fingerprint(), rows })); }
        }
    }
}
