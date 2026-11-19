using M2DPlus.Core;
using M2DPlus.Rhino.Services;
using Rhino.Geometry;
using Point = Rhino.Geometry.Point;

namespace M2DPlus.Measurements;

// Linked into worker-bench and licensed tests, never shipped in the plug-in.
// Inputs are already projected millimetre drawings in the SAME frame. Lengths count
// duplicate strokes (not a union). HLD is a reference, not independent truth.
internal static class DrawingComparison
{
    internal const double SpacingMm = .25;
    internal const double DistanceMm = .05;
    internal sealed record Agreement(string Direction, LineClass Class, double Length, long Samples,
        double Same, double OppositeOnly, double UnresolvedOnly, double Absent)
    { public double Percent => Length == 0 ? 100 : Same / Length * 100; }
    internal sealed record Points(int Total, int ClassDisagreements, int Absent);
    internal sealed record Direction(Agreement[] Classes, Points Points, int ZeroLengthCurves)
    {
        public double MissingMm => Classes.Sum(c => c.Absent);
        public double UnresolvedMm => Classes.Where(c => c.Class == LineClass.Unresolved).Sum(c => c.Length);
        public Agreement this[LineClass kind] => Classes.Single(c => c.Class == kind);
    }
    internal sealed record Result(Direction PipesToHld, Direction HldToPipes)
    {
        public double WrongVisibleMm => PipesToHld[LineClass.Visible].OppositeOnly;
        public double WrongVisiblePercent => PipesToHld[LineClass.Visible].Length == 0 ? 0 :
            100 * WrongVisibleMm / PipesToHld[LineClass.Visible].Length;
        public double WrongHiddenMm => HldToPipes[LineClass.Visible].OppositeOnly;
        public double WrongHiddenPercent => HldToPipes[LineClass.Visible].Length == 0 ? 0 :
            100 * WrongHiddenMm / HldToPipes[LineClass.Visible].Length;
        public double PipesHiddenOverHldVisibleMm => PipesToHld[LineClass.Hidden].OppositeOnly;
    }
    internal static Result Compare(ViewDrawing pipes, ViewDrawing hld) => new(
        Sample(pipes, hld, "pipes-to-hld"), Sample(hld, pipes, "hld-to-pipes"));

    internal sealed record Overlap(LineClass Class, double Mm);
    // Count each stroke's supported length, not excess length or a geometric union.
    // Exclude the entire original piece, including all of its indexed subspans.
    internal static Overlap[] SelfOverlap(ViewDrawing drawing)
    {
        using var index = new Index(drawing);
        return drawing.Items.Select(p => p.Class).Distinct().Select(kind =>
        {
            double length = 0;
            for (var id = 0; id < drawing.Items.Count; id++)
            {
                var piece = drawing.Items[id];
                if (piece.Class != kind) continue;
                using var geometry = piece.CopyGeometry();
                if (geometry is not Curve curve || curve.GetLength() == 0) continue;
                var (parameters, weight) = Stations(curve);
                foreach (var t in parameters)
                    if ((index.Hits(curve.PointAt(t), kind, id) & Bit(kind)) != 0) length += weight;
            }
            return new Overlap(kind, length);
        }).ToArray();
    }

    internal sealed record Span(int Piece, double Start, double End, double Mm);
    // Arc-length cell boundaries bracket the detected stations, with <= .25 mm
    // endpoint resolution. Parameters refer to the archived HLD projected curve.
    internal static Span[] WrongHiddenSpans(ViewDrawing pipes, ViewDrawing hld)
    {
        using var index = new Index(pipes);
        var spans = new List<Span>();
        for (var id = 0; id < hld.Items.Count; id++)
        {
            var piece = hld.Items[id];
            if (piece.Class != LineClass.Visible) continue;
            using var geometry = piece.CopyGeometry();
            if (geometry is not Curve curve || curve.GetLength() == 0) continue;
            var (parameters, weight) = Stations(curve);
            var first = -1;
            for (var i = 0; i <= parameters.Length; i++)
            {
                var wrong = i < parameters.Length && index.Hits(curve.PointAt(parameters[i]), LineClass.Visible) == Bit(LineClass.Hidden);
                if (wrong && first < 0) first = i;
                if (wrong || first < 0) continue;
                var ends = curve.NormalizedLengthParameters([(double)first / parameters.Length, (double)i / parameters.Length], 1e-8)
                    ?? throw new InvalidDataException("Span boundary sampling failed");
                spans.Add(new(id, ends[0], ends[1], (i - first) * weight)); first = -1;
            }
        }
        return spans.ToArray();
    }

    private static (double[] Parameters, double Weight) Stations(Curve curve)
    {
        var length = curve.GetLength();
        if (!double.IsFinite(length) || length <= 0) throw new InvalidDataException("Invalid sampling length");
        var count = Math.Max(33, checked((int)Math.Ceiling(length / SpacingMm)));
        var parameters = curve.NormalizedLengthParameters(Enumerable.Range(0, count).Select(i => (i + .5) / count).ToArray(), 1e-8)
            ?? throw new InvalidDataException("Arc-length sampling failed");
        return (parameters, length / count);
    }

    internal static Direction Sample(ViewDrawing from, ViewDrawing to, string direction, Rectangle3d? mask = null)
    {
        using var targets = new Index(to);
        var rows = new List<Agreement>();
        var zeroLengthCurves = 0;
        foreach (var kind in new[] { LineClass.Visible, LineClass.Hidden, LineClass.Unresolved }
                     .Concat(from.Items.Select(p => p.Class)).Distinct())
        {
            double length = 0, same = 0, opposite = 0, unresolved = 0, absent = 0; long samples = 0;
            foreach (var piece in from.Items.Where(p => p.Class == kind))
            {
                using var geometry = piece.CopyGeometry();
                if (geometry is not Curve curve) continue;
                var len = curve.GetLength();
                if (!double.IsFinite(len) || len < 0) throw new InvalidDataException("Invalid comparison curve length");
                // HLD can emit point-like projected curves. They contribute no length;
                // keep a count rather than inventing a station or silently failing.
                if (len == 0) { zeroLengthCurves++; continue; }
                var count = Math.Max(33, checked((int)Math.Ceiling(len / SpacingMm)));
                var stations = Enumerable.Range(0, count).Select(i => (i + .5) / count).ToArray();
                var parameters = curve.NormalizedLengthParameters(stations, 1e-8) ??
                    throw new InvalidDataException("Arc-length sampling failed");
                foreach (var parameter in parameters)
                {
                    var point = curve.PointAt(parameter);
                    if (!point.IsValid) throw new InvalidDataException("Invalid comparison station");
                    if (mask is { } frame)
                    {
                        frame.Plane.ClosestParameter(point, out var x, out var y);
                        if (x < frame.X.Min || x > frame.X.Max || y < frame.Y.Min || y > frame.Y.Max) continue;
                    }
                    var hits = targets.Hits(point, kind);
                    var weight = len / count; length += weight; samples++;
                    if ((hits & Bit(kind)) != 0) same += weight;
                    else if (kind == LineClass.Visible && hits == Bit(LineClass.Hidden) ||
                             kind == LineClass.Hidden && hits == Bit(LineClass.Visible)) opposite += weight;
                    else if ((hits & Bit(LineClass.Unresolved)) != 0) unresolved += weight;
                    else if (hits == 0) absent += weight;
                }
            }
            rows.Add(new(direction, kind, length, samples, same, opposite, unresolved, absent));
        }
        int total = 0, wrong = 0, missing = 0;
        foreach (var piece in from.Items)
        {
            using var geometry = piece.CopyGeometry();
            if (geometry is not Point point) continue;
            total++;
            var near = targets.Points.Where(p => p.Location.DistanceTo(point.Location) <= DistanceMm).ToArray();
            if (near.Length == 0) missing++;
            else if (!near.Any(p => p.Class == piece.Class)) wrong++;
        }
        return new(rows.ToArray(), new(total, wrong, missing), zeroLengthCurves);
    }
    private static int Bit(LineClass kind) => 1 << (int)kind;

    // Exact subcurve boxes and distances keep dense lattices tractable. The spans
    // cover the original curve exactly; no polyline approximation is involved.
    private sealed class Index : IDisposable
    {
        private readonly Dictionary<LineClass, RTree> _trees = [];
        private readonly List<Curve> _curves = [];
        private readonly List<(Curve Curve, bool Owned, int Piece)> _spans = [];
        internal readonly List<(Point3d Location, LineClass Class)> Points = [];
        internal Index(ViewDrawing drawing)
        {
            try
            {
                for (var pieceId = 0; pieceId < drawing.Items.Count; pieceId++)
                {
                    var piece = drawing.Items[pieceId];
                    var geometry = piece.CopyGeometry();
                    if (geometry is not Curve curve)
                    {
                        using (geometry) if (geometry is Point p) Points.Add((p.Location, piece.Class));
                        continue;
                    }
                    _curves.Add(curve);
                    if (!_trees.TryGetValue(piece.Class, out var tree)) _trees.Add(piece.Class, tree = new());
                    var spans = curve.GetLength() > 50 ? 16 : 1;
                    for (var i = 0; i < spans; i++)
                    {
                        var span = spans == 1 ? null : curve.Trim(curve.Domain.ParameterAt((double)i / spans),
                            curve.Domain.ParameterAt((double)(i + 1) / spans));
                        var owned = span is not null; span ??= curve;
                        var bounds = span.GetBoundingBox(true); bounds.Inflate(1e-8);
                        var id = _spans.Count; _spans.Add((span, owned, pieceId)); tree.Insert(bounds, id);
                    }
                }
            }
            catch { Dispose(); throw; }
        }
        internal int Hits(Point3d point, LineClass wanted, int excludedPiece = -1)
        {
            bool Near(LineClass kind)
            {
                if (!_trees.TryGetValue(kind, out var tree)) return false;
                var found = false; var radius = new Vector3d(DistanceMm, DistanceMm, DistanceMm);
                tree.Search(new BoundingBox(point - radius, point + radius), (_, e) =>
                {
                    if (_spans[e.Id].Piece == excludedPiece) return;
                    var curve = _spans[e.Id].Curve;
                    if (!curve.ClosestPoint(point, out var t, DistanceMm + .000001) ||
                        curve.PointAt(t).DistanceTo(point) > DistanceMm) return;
                    found = true; e.Cancel = true;
                });
                return found;
            }
            if (Near(wanted)) return Bit(wanted);
            if (excludedPiece >= 0) return 0; // Self-overlap only asks about this class.
            var hits = 0;
            foreach (var kind in _trees.Keys) if (kind != wanted && Near(kind)) hits |= Bit(kind);
            return hits;
        }
        public void Dispose()
        {
            foreach (var tree in _trees.Values) tree.Dispose();
            foreach (var (curve, owned, _) in _spans) if (owned) curve.Dispose();
            foreach (var curve in _curves) curve.Dispose();
        }
    }
}
