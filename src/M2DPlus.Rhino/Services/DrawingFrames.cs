using M2DPlus.Core;
using Rhino.Geometry;

namespace M2DPlus.Rhino.Services;

internal static class DrawingFrames
{
    /// <summary>Project volume-restricted INPUT boxes, including edge/plane intersections. Never inspect output bounds.</summary>
    internal static Rectangle3d Resolve(ViewRequest view, IReadOnlyList<PreparedLeaf> sources, BoundingBox scene, double tolerance)
    {
        var frame = view.Resolve(scene, tolerance);
        if (view.Cut?.Crop is not null || view.Named is { Crop: true } || view.Settings is null) return frame;
        var plane = frame.Plane; var points = new List<Point3d>();
        var range = new ViewRange(view.Settings);
        foreach (var source in sources)
        {
            var corners = source.Geometry.GetBoundingBox(true).GetCorners();
            var participates = view.Cut?.Participates(source) ?? true;
            if (!participates) { points.AddRange(corners); continue; }
            bool Inside(Point3d p) => plane.DistanceTo(p) <= tolerance && (!range.ClipsFar || -plane.DistanceTo(p) <= range.Depth + tolerance);
            points.AddRange(corners.Where(Inside));
            // BoundingBox corner order: four lower, four upper corners, each ring consecutive.
            foreach (var pair in new[] { (0,1), (1,2), (2,3), (3,0), (4,5), (5,6), (6,7), (7,4), (0,4), (1,5), (2,6), (3,7) })
            {
                var a = corners[pair.Item1]; var b = corners[pair.Item2];
                var da = plane.DistanceTo(a); var db = plane.DistanceTo(b);
                foreach (var threshold in range.ClipsFar ? new[] { 0d, -range.Depth } : new[] { 0d })
                {
                    if (da == db) continue;
                    var t = (threshold - da) / (db - da);
                    if (t < 0 || t > 1) continue;
                    var p = a + (b - a) * t; if (Inside(p)) points.Add(p);
                }
            }
        }
        if (points.Count == 0) return new(plane, new Interval(-1, 1), new Interval(-1, 1));
        var bounds = BoundingBox.Empty;
        foreach (var p in points) { plane.ClosestParameter(p, out var x, out var y); bounds.Union(new Point3d(x, y, 0)); }
        var padX = bounds.Diagonal.X <= tolerance ? tolerance * 10 : 0;
        var padY = bounds.Diagonal.Y <= tolerance ? tolerance * 10 : 0;
        return new(plane, new Interval(bounds.Min.X - padX, bounds.Max.X + padX), new Interval(bounds.Min.Y - padY, bounds.Max.Y + padY));
    }
}
