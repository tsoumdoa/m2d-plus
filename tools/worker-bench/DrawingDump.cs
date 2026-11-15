using System.Globalization;
using Rhino.Geometry;
using M2DPlus.Rhino.Services;
using Point = Rhino.Geometry.Point;

internal static class DrawingDump
{
    /// <summary>Canonical, order-independent export. Coordinates are written round-trip exact.</summary>
    internal static object Dump(ViewDrawing drawing, double tolerance)
    {
        static string N(double v) => v.ToString("R", CultureInfo.InvariantCulture);
        static double[] Flat(IEnumerable<Point3d> points) => points.SelectMany(p => new[] { p.X, p.Y, p.Z }).ToArray();
        double[] Points(Curve curve)
        {
            if (curve.TryGetPolyline(out var exact)) return Flat(exact);
            using var approximation = curve.ToPolyline(tolerance / 8, .02, 0, 0);
            return approximation is not null && approximation.TryGetPolyline(out var polyline) ? Flat(polyline) : Flat(new[] { curve.PointAtStart, curve.PointAtEnd });
        }
        var pieces = new List<(string Key, object Value)>();
        foreach (var item in drawing.Items)
        {
            using var geometry = item.CopyGeometry();
            var points = geometry is Curve curve ? Points(curve) : geometry is Point point ? Flat(new[] { point.Location }) : Array.Empty<double>();
            var key = $"{item.Class}|{item.SourceIdentity}|{item.Profile}|{string.Join(",", points.Select(N))}";
            pieces.Add((key, new { c = item.Class.ToString(), s = item.SourceIdentity, profile = item.Profile, exact = geometry is Curve c2 && c2.TryGetPolyline(out _), p = points }));
        }
        var regions = new List<(string Key, object Value)>();
        foreach (var entry in drawing.Regions)
        {
            var region = entry.Region;
            var loops = region.Loops.Select(l => new { hole = l.IsHole, length = l.Curve.GetLength(), origins = l.Edges.Select(e => (int)e.Origin).ToArray(), p = Points(l.Curve) }).ToArray();
            var key = $"{region.Id}|{string.Join(";", region.SourceIds)}|{N(region.Area)}|{string.Join("/", loops.Select(l => string.Join(",", l.p.Select(N))))}";
            regions.Add((key, new { id = region.Id, sources = region.SourceIds, status = region.Status.ToString(), area = region.Area, hatch = region.Hatch is not null, loops }));
        }
        return new
        {
            error = drawing.Error, tolerance, quarantined = drawing.Report.Quarantined, uncertain = drawing.Report.UncertainCount, invalid = drawing.Report.InvalidExtractionCount,
            hiddenPoints = drawing.HiddenPoints, duplicates = drawing.DuplicateSegments, projecting = drawing.ProjectingSegments,
            diagnostics = drawing.Diagnostics.Order(StringComparer.Ordinal).ToArray(),
            pieces = pieces.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Value).ToArray(),
            regions = regions.OrderBy(r => r.Key, StringComparer.Ordinal).Select(r => r.Value).ToArray()
        };
    }
}
