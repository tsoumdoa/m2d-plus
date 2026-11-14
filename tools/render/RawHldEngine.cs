using System.Diagnostics;
using M2DPlus.Core;
using M2DPlus.Rhino.Services;
using Rhino.DocObjects;
using Rhino.Geometry;
using Point = Rhino.Geometry.Point;

namespace M2DPlus.Render;

/// <summary>Comparison adapter, over the same owned/normalised inputs. Raw HLD is never a correctness oracle.</summary>
internal sealed class RawHldEngine : IRenderEngine
{
    public string Name => "Raw Rhino HLD over captured/normalised inputs";
    public DrawingBatch Draw(LeaseEntry<PreparedScene>.Lease lease, IReadOnlyList<ViewRequest> views)
    {
        using (lease)
        {
            var scene = lease.Value;
            if (!scene.Cost.Admitted(views.Count, NativeScheduler.Global.BudgetBytes)) throw new InvalidOperationException(SceneCost.Refusal);
            return NativeScheduler.Global.RunAsync(scene.Cost.Estimate(views.Count), token =>
            {
                var sources = scene.Prepare(token); var results = new List<ViewDrawing>();
                try
                {
                    foreach (var view in views) results.Add(DrawView(scene, sources, view));
                    return new DrawingBatch(new(scene.Revision, "render/raw", "single-threaded", 1), results);
                }
                catch { foreach (var result in results) result.Dispose(); throw; }
            }, default).GetAwaiter().GetResult();
        }
    }
    private static ViewDrawing DrawView(PreparedScene scene, IReadOnlyList<PreparedLeaf> sources, ViewRequest request)
    {
        var timer = Stopwatch.StartNew(); var result = new ViewDrawing { Name = request.Name, Tolerance = scene.Tolerance };
        var invalid = 0;
        try
        {
            if (request.Error is not null) { result.Error = request.Error; return result; }
            if (request.Named is not null) throw new NotSupportedException("Raw render adapter currently compares direction/vector views only.");
            var frame = request.Resolve(scene.Bounds, scene.Tolerance); result.Frame = frame;
            if (sources.Count == 0) return result;
            using var viewport = new ViewportInfo(); var radius = Math.Max(scene.Bounds.Diagonal.Length, 1);
            viewport.IsParallelProjection = true; viewport.SetCameraLocation(frame.Plane.Origin + frame.Plane.ZAxis * (radius * 4 + 10));
            viewport.SetCameraDirection(request.Direction); viewport.SetCameraUp(frame.Plane.YAxis);
            viewport.SetFrustum(frame.X.Min, frame.X.Max, frame.Y.Min, frame.Y.Max, .001, radius * 10 + 100);
            var parameters = new HiddenLineDrawingParameters { AbsoluteTolerance = scene.Tolerance, Flatten = false,
                IncludeHiddenCurves = true, IncludeTangentEdges = false, IncludeTangentSeams = false };
            parameters.SetViewport(viewport);
            foreach (var source in sources) if (!parameters.AddGeometry(source.Geometry, source)) invalid++;
            using var drawing = HiddenLineDrawing.Compute(parameters, false);
            if (drawing is null || !drawing.WorldToHiddenLine.TryGetInverse(out var inverse)) throw new InvalidOperationException("Raw HLD failed");
            var transform = Transform.PlanarProjection(frame.Plane) * inverse;
            foreach (var segment in drawing.Segments)
            {
                var visibility = segment.SegmentVisibility;
                if (visibility == HiddenLineDrawingSegment.Visibility.Unset) { invalid++; continue; }
                if (visibility == HiddenLineDrawingSegment.Visibility.Duplicate) { result.DuplicateSegments++; continue; }
                if (visibility == HiddenLineDrawingSegment.Visibility.Projecting) { result.ProjectingSegments++; continue; }
                if (segment.ParentCurve?.SourceObject?.Tag is not PreparedLeaf source || segment.CurveGeometry is not { } proxy) { invalid++; continue; }
                var curve = proxy.DuplicateCurve();
                try
                {
                    curve.EnsurePrivateCopy(); if (!curve.Transform(transform)) throw new InvalidOperationException("Raw extraction transform failed");
                    result.Pieces.Add(new(curve, source, visibility switch { HiddenLineDrawingSegment.Visibility.Visible => LineClass.Visible,
                        HiddenLineDrawingSegment.Visibility.Hidden => LineClass.Hidden, _ => LineClass.Unresolved }));
                }
                catch { curve.Dispose(); throw; }
            }
            foreach (var point in drawing.Points)
            {
                if (point.PointVisibility == HiddenLineDrawingPoint.Visibility.Unset || point.SourceObject?.Tag is not PreparedLeaf source || !point.Location.IsValid) { invalid++; continue; }
                if (point.PointVisibility == HiddenLineDrawingPoint.Visibility.Hidden) { result.HiddenPoints++; continue; }
                if (point.PointVisibility != HiddenLineDrawingPoint.Visibility.Visible) { invalid++; continue; }
                var location = point.Location; location.Transform(transform); result.Pieces.Add(new(new Point(location), source, LineClass.Visible));
            }
        }
        catch (Exception e) { result.Dispose(); result.Error = e.Message; invalid++; }
        finally
        {
            var quarantine = invalid != 0 || scene.HasPotentialOccluderUnsupported;
            if (quarantine) foreach (var piece in result.Pieces) piece.Class = LineClass.Unresolved;
            result.Report = new("Raw Rhino HLD comparison (not an oracle)", result.Pieces.Count(p => p.Class == LineClass.Unresolved), invalid, scene.Unsupported, scene.Conversions, quarantine);
            result.Seconds = timer.Elapsed.TotalSeconds;
        }
        return result;
    }
}
