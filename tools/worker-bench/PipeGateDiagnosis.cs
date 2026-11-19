using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text;
using System.Text.Json;
using M2DPlus.Core;
using M2DPlus.Measurements;
using M2DPlus.Rhino.Experimental.PipeVisibility;
using M2DPlus.Rhino.Services;
using Rhino.FileIO;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;

// Research evidence only: no feature tags or visibility behavior in the product change.
internal static class PipeGateDiagnosis
{
    private sealed record Feature(string Source, string Kind, Curve Curve);
    private sealed record Cell(int Piece, string Source, string Kind, double Mm, double Start, double End,
        bool OwnBodyHides, string[] HidingSources, bool RayHidesAtPipesSupport,
        double PipesSupportDistanceMm, string PipesSource);
    internal static void Write(string folder, string model, ViewDrawing pipes, List<ViewDrawing> hlds,
        DrawingComparison.Result[] comparisons, string? sourceArchives = null)
    {
        var repeat = Enumerable.Range(0, hlds.Count).MaxBy(i => comparisons[i].WrongHiddenMm);
        var hld = hlds[repeat];
        var spans = DrawingComparison.WrongHiddenSpans(pipes, hld);
        using var original = File3dm.Read(model) ?? throw new IOException(model);
        var direction = ViewRequest.Parse(hld.Name).Direction;
        var eye = -direction; eye.Unitize();
        var features = new List<Feature>();
        var rays = new Dictionary<string, PipeRayFaces>();
        var cells = new List<Cell>();
        var controls = new List<object>();
        var hidden = pipes.Items.Where(p => p.Class == LineClass.Hidden).Select(p => (Piece: p, Curve: p.CopyGeometry() as Curve)).Where(p => p.Curve is not null).ToArray();
        try
        {
            foreach (var obj in original.Objects)
            {
                var source = obj.Id.ToString();
                if (obj.Geometry is Curve loose) { features.Add(new(source, "loose curve", loose.DuplicateCurve())); continue; }
                if (obj.Geometry is not Brep body) continue;
                rays.Add(source, new([body], eye, original.Settings.ModelAbsoluteTolerance,
                    2 * body.GetBoundingBox(true).Diagonal.Length, 1));
                foreach (var edge in body.Edges)
                {
                    if (edge.IsSmoothManifoldEdge(original.Settings.ModelAngleToleranceRadians)) continue;
                    var faces = edge.AdjacentFaces();
                    if (faces is [var a, var b] && a == b) continue;
                    var cap = faces.Any(i => body.Faces[i].IsPlanar(.001));
                    var kind = cap ? "pipe end circle / edge-on cap" :
                        !cap && faces.Any(i => !body.Faces[i].IsPlanar(.001)) ? "junction trim" : "sharp edge";
                    features.Add(new(source, kind, edge.DuplicateCurve()));
                }
                foreach (var silhouette in Silhouette.Compute(body, SilhouetteType.Tangent, direction,
                             original.Settings.ModelAbsoluteTolerance, original.Settings.ModelAngleToleranceRadians) ?? [])
                {
                    if (silhouette.Curve is not { } curve) continue;
                    var point = curve.PointAt(curve.Domain.Mid);
                    var face = body.Faces.OrderBy(f => f.ClosestPoint(point, out var u, out var v) ? f.PointAt(u, v).DistanceTo(point) : double.MaxValue).First();
                    var inner = false;
                    if (face.TryGetCylinder(out var cylinder, .001) && face.ClosestPoint(point, out var u, out var v))
                    {
                        var radial = point - cylinder.Center; radial -= (radial * cylinder.Axis) * cylinder.Axis;
                        var normal = face.UnderlyingSurface().NormalAt(u, v);
                        if (face.OrientationIsReversed ^ (body.SolidOrientation == BrepSolidOrientation.Inward)) normal.Reverse();
                        inner = normal * radial < 0;
                    }
                    features.Add(new(source, inner ? "inner silhouette" : "outer silhouette", curve.DuplicateCurve()));
                }
            }
            foreach (var span in spans)
            {
                var piece = hld.Items[span.Piece];
                using var flat = (Curve)piece.CopyGeometry();
                var count = Math.Max(1, (int)Math.Ceiling(span.Mm / DrawingComparison.SpacingMm));
                using var part = flat.Trim(span.Start, span.End) ?? throw new InvalidDataException("Wrong-hidden trim failed");
                var stations = part.NormalizedLengthParameters(Enumerable.Range(0, count).Select(i => (i + .5) / count).ToArray(), 1e-8)!;
                var midpoint = part.NormalizedLengthParameter(.5, out var middle) ? middle : throw new InvalidDataException("Control midpoint failed");
                var controlPoint = (piece.DepthGeometry as Curve)?.PointAt(midpoint) ?? throw new InvalidDataException("HLD support missing");
                var tolerance = original.Settings.ModelAbsoluteTolerance;
                var nativeHits = new List<string>();
                var nativeInterior = new List<string>();
                var nativeDetails = new List<object>();
                foreach (var obj in original.Objects.Where(o => o.Geometry is Brep))
                {
                    using var rayCurve = new LineCurve(controlPoint + 2 * tolerance * eye,
                        controlPoint + 2 * original.Objects.Select(o => o.Geometry.GetBoundingBox(true).Diagonal.Length).Max() * eye);
                    if (!Intersection.CurveBrep(rayCurve, (Brep)obj.Geometry, tolerance, out var overlaps, out var hits))
                        throw new InvalidDataException("Native diagnostic ray intersection failed");
                    try
                    {
                        var depths = hits.Select(p => (p - controlPoint) * eye).Where(d => d > 2 * tolerance).Order().ToArray();
                        if (depths.Length == 0) continue;
                        nativeHits.Add(obj.Id.ToString());
                        var previous = 2 * tolerance; var interiorMm = 0d;
                        foreach (var depth in depths)
                        {
                            var inside = controlPoint + (.5 * (previous + depth)) * eye;
                            var body = (Brep)obj.Geometry;
                            if (body.IsPointInside(inside, tolerance, true) && body.ClosestPoint(inside).DistanceTo(inside) > tolerance)
                                interiorMm += depth - previous;
                            previous = depth;
                        }
                        if (interiorMm > 0) nativeInterior.Add(obj.Id.ToString());
                        nativeDetails.Add(new { Source = obj.Id.ToString(), DepthsMm = depths, InteriorMm = interiorMm });
                    }
                    finally { foreach (var overlap in overlaps) overlap.Dispose(); }
                }
                controls.Add(new { span.Piece, span.Start, span.End, span.Mm, piece.SourceIdentity,
                    Point = new[] { controlPoint.X, controlPoint.Y, controlPoint.Z },
                    RayHidingSources = rays.Where(pair => pair.Value.Hidden(controlPoint)).Select(pair => pair.Key).ToArray(),
                    NativePositiveHitSources = nativeHits, NativeInteriorSources = nativeInterior, NativeHitDetails = nativeDetails });
                for (var i = 0; i < count; i++)
                {
                    var t = stations[i]; var point = flat.PointAt(t);
                    var spatial = (piece.DepthGeometry as Curve)?.PointAt(t) ?? throw new InvalidDataException("HLD support missing");
                    var feature = features.Where(f => piece.SourceIdentity.Contains(f.Source, StringComparison.OrdinalIgnoreCase))
                        .Select(f => (Feature: f, Distance: Distance(f.Curve, spatial)))
                        .OrderBy(f => f.Distance).First();
                    if (feature.Distance > .05) throw new InvalidDataException($"Unattributed feature {piece.SourceIdentity}: {feature.Distance}");
                    var nearest = hidden.Select(p =>
                    {
                        p.Curve!.ClosestPoint(point, out var parameter);
                        return (p.Piece, Distance: p.Curve.PointAt(parameter).DistanceTo(point), Parameter: parameter);
                    }).OrderBy(p => p.Distance).First();
                    var pipesSpatial = (nearest.Piece.DepthGeometry as Curve)?.PointAt(nearest.Parameter);
                    var own = rays.TryGetValue(feature.Feature.Source, out var ray) && ray.Hidden(spatial);
                    var hiding = rays.Where(pair => pair.Value.Hidden(spatial)).Select(pair => pair.Key).ToArray();
                    var pipesHides = pipesSpatial is { } support && rays.Values.Any(r => r.Hidden(support));
                    var ends = part.NormalizedLengthParameters([(double)i / count, (double)(i + 1) / count], 1e-8)!;
                    cells.Add(new(span.Piece, feature.Feature.Source, feature.Feature.Kind, span.Mm / count, ends[0], ends[1], own, hiding, pipesHides,
                        pipesSpatial?.DistanceTo(spatial) ?? double.NaN, nearest.Piece.SourceIdentity));
                }
            }
            var categories = new[] { "outer silhouette", "inner silhouette", "pipe end circle / edge-on cap", "sharp edge", "junction trim", "loose curve" };
            var breakdown = categories.Select(kind => new { Kind = kind, Mm = cells.Where(c => c.Kind == kind).Sum(c => c.Mm),
                OwnBodyHidesMm = cells.Where(c => c.Kind == kind && c.OwnBodyHides).Sum(c => c.Mm),
                NoRayHitAtHldSupportMm = cells.Where(c => c.Kind == kind && c.HidingSources.Length == 0).Sum(c => c.Mm),
                NoRayHitAtPipesSupportMm = cells.Where(c => c.Kind == kind && !c.RayHidesAtPipesSupport).Sum(c => c.Mm) }).ToArray();
            var options = new JsonSerializerOptions { WriteIndented = true };
            var bySource = cells.GroupBy(c => (c.Source, c.Kind)).OrderBy(g => g.Key.Source).ThenBy(g => g.Key.Kind)
                .Select(g => new { g.Key.Source, g.Key.Kind, Mm = g.Sum(c => c.Mm), OwnBodyHidesMm = g.Where(c => c.OwnBodyHides).Sum(c => c.Mm) }).ToArray();
            File.WriteAllText(Path.Combine(folder, "wrong-hidden.json"), JsonSerializer.Serialize(new
            {
                Model = Path.GetFullPath(model), SourceArchives = sourceArchives ?? folder,
                Reference = $"hld-{repeat}.json", comparisons[repeat].WrongHiddenMm,
                Method = "HLD Visible opposite-only midpoint cells; source GUID and 3D support matched to original sharp edges/apparent contours at <=0.05 mm; closest feature wins. OwnBodyHides replays the unchanged Pipes ray query with only the HLD source body.",
                Spans = spans, MidpointNativeRayControls = controls, Breakdown = breakdown,
                BySource = bySource, Cells = cells
            }, options));
            var text = new StringBuilder($"# {hld.Name} wrong-hidden diagnosis\n\nReference: hld-{repeat}.json; {comparisons[repeat].WrongHiddenMm:F3} mm.\n\n| Feature | mm | Hidden by own body alone, mm |\n|---|---:|---:|\n");
            foreach (var row in breakdown) text.AppendLine($"| {row.Kind} | {row.Mm:F3} | {row.OwnBodyHidesMm:F3} |");
            text.AppendLine("\n| Source GUID | Feature | mm | Own-body mm |\n|---|---|---:|---:|");
            foreach (var row in bySource) text.AppendLine($"| {row.Source} | {row.Kind} | {row.Mm:F3} | {row.OwnBodyHidesMm:F3} |");
            text.AppendLine("\nFeature attribution uses the archived HLD source and 3D support, matched to original edges and tangent silhouettes. The JSON retains each sampled cell and the nearest Pipes hidden source/support distance, plus one native trimmed-Brep ray/interior control at each error span midpoint. Native forward surface hits alone do not prove material occlusion. Mechanism is an inference: own-body silhouette hits suggest grazing/self-occlusion; cap hits suggest end/edge-on coincidence; other-body trim hits suggest junction/contact boundary disagreement. A trim-chart error is not established by this comparison. No classifier changes are made.");
            text.AppendLine("\nThe native controls also test midpoint solid interior between consecutive ray intersections with clearance greater than document tolerance. Positive interior supports a real material obstruction at that control point, rather than just a tangent hit. These are representative controls, not a length-integrated independent accuracy oracle; HLD remains only the gate reference.");
            File.WriteAllText(Path.Combine(folder, "wrong-hidden.md"), text.ToString());
            Render(Path.Combine(folder, "wrong-hidden.png"), hld, spans, false);
            Render(Path.Combine(folder, "wrong-hidden-worst-cluster.png"), hld, spans, true);
        }
        finally
        {
            foreach (var feature in features) feature.Curve.Dispose();
            foreach (var ray in rays.Values) ray.Dispose();
            foreach (var item in hidden) item.Curve?.Dispose();
        }
    }
    private static double Distance(Curve curve, Point3d point) => curve.ClosestPoint(point, out var t) ? curve.PointAt(t).DistanceTo(point) : double.MaxValue;
    private static void Render(string path, ViewDrawing drawing, DrawingComparison.Span[] spans, bool crop)
    {
        const int size = 1800, margin = 60;
        var frame = drawing.Frame.Plane;
        PointF XY(Point3d p) { frame.ClosestParameter(p, out var x, out var y); return new((float)x, (float)y); }
        var strokes = new List<(PointF[] Points, bool Red)>();
        foreach (var piece in drawing.Items)
        {
            using var geometry = piece.CopyGeometry();
            if (geometry is Curve curve) strokes.Add((Polyline(curve), false));
        }
        foreach (var span in spans)
        {
            using var curve = (Curve)drawing.Items[span.Piece].CopyGeometry();
            using var part = curve.Trim(span.Start, span.End)!;
            strokes.Add((Polyline(part), true));
        }
        PointF[] Polyline(Curve c)
        {
            var count = Math.Max(2, (int)Math.Ceiling(c.GetLength() / .5));
            return Enumerable.Range(0, count + 1).Select(i => XY(c.PointAtNormalizedLength((double)i / count))).ToArray();
        }
        var all = strokes.SelectMany(s => s.Points).ToArray();
        double minX = all.Min(p => p.X), maxX = all.Max(p => p.X), minY = all.Min(p => p.Y), maxY = all.Max(p => p.Y);
        var width = maxX - minX; var height = maxY - minY;
        if (crop && spans.Length > 0)
        {
            // Densest fixed-size neighborhood of red samples, rather than longest stroke.
            var red = strokes.Where(s => s.Red).SelectMany(s => s.Points).ToArray();
            var radius = Math.Max(width, height) * .08;
            var center = red.MaxBy(p => red.Count(q => Math.Abs(p.X - q.X) <= radius && Math.Abs(p.Y - q.Y) <= radius));
            minX = center.X - radius; maxX = center.X + radius; minY = center.Y - radius; maxY = center.Y + radius;
        }
        var scale = Math.Min((size - 2 * margin) / (maxX - minX), (size - 2 * margin) / (maxY - minY));
        using var bitmap = new Bitmap(size, size); using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.White); graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.SetClip(new Rectangle(margin, margin, size - 2 * margin, size - 2 * margin));
        foreach (var (points, red) in strokes.OrderBy(s => s.Red))
        {
            using var pen = new Pen(red ? Color.Red : Color.FromArgb(175, 175, 175), red ? 3 : 1);
            graphics.DrawLines(pen, points.Select(p => new PointF((float)(margin + (p.X - minX) * scale), (float)(size - margin - (p.Y - minY) * scale))).ToArray());
        }
        graphics.ResetClip(); using var font = new Font("Arial", 20);
        graphics.DrawString($"{drawing.Name}: HLD drawing grey; wrong-hidden red" + (crop ? " — densest cluster" : ""), font, Brushes.Black, 30, 15);
        bitmap.Save(path, ImageFormat.Png);
    }
}
