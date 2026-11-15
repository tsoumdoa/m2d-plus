using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using M2DPlus.Rhino.Services;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using Point = Rhino.Geometry.Point;

namespace M2DPlus.PerfF;

/// <summary>
/// PERF-F curved/freeform native-cost experiments. Raw HiddenLineDrawing over the prepared scene with the product's
/// viewport and flags (serial, hidden on, no tangents/seams), so subsets and exact re-parameterisations can be timed
/// back to back in one process. Subset timings are cost attribution only, never drawings.
/// </summary>
internal static class Curved
{
    private sealed record Item(GeometryBase Geometry, string Id, string Definition);
    private sealed record Solve(double Seconds, int Inputs, int Visible, int Hidden, int Unset, int Other, double VisibleLength, double HiddenLength, string? Dump, string? Failure);

    internal static void Run(PreparedScene scene, IReadOnlyList<PreparedLeaf> prepared, ViewRequest view, string stem, string output, string[] experiments, double timeout)
    {
        var tolerance = scene.Tolerance;
        var items = prepared.Where(p => p.Geometry is not Point).Select(p => new Item(p.Geometry, p.Identity, "definition/" + string.Join("/", p.Identity.Split('/').Skip(1)))).ToArray();
        var definitions = items.GroupBy(i => i.Definition).OrderBy(g => g.Key, StringComparer.Ordinal).ToArray();
        var rows = new List<object>();
        void Record(string name, Solve solve, object? extra = null)
        {
            Console.WriteLine($"--- {stem} [{name}] {solve.Seconds:F3}s inputs={solve.Inputs} visible={solve.Visible} ({solve.VisibleLength:F3}) hidden={solve.Hidden} ({solve.HiddenLength:F3}) unset={solve.Unset} {solve.Failure}");
            rows.Add(new { name, solve, extra });
            File.WriteAllText(Path.Combine(output, stem + "-curved.json"), JsonSerializer.Serialize(new
            {
                view = view.Name, tolerance, rhino = global::Rhino.RhinoApp.Version.ToString(), runtime = System.Environment.Version.ToString(),
                contention = "shared machine; two other long benchmark jobs running", rows
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        foreach (var experiment in experiments)
        {
            switch (experiment)
            {
                case "census":
                    foreach (var group in definitions)
                    {
                        var brep = group.First().Geometry as Brep;
                        var kinds = new SortedDictionary<string, int>(StringComparer.Ordinal);
                        if (brep is not null)
                            foreach (var face in brep.Faces)
                            {
                                var kind = face.IsPlanar(tolerance) ? "planar" : face.IsCylinder(tolerance) ? "cylinder" : face.IsCone(tolerance) ? "cone" :
                                    face.IsSphere(tolerance) ? "sphere" : face.IsTorus(tolerance) ? "torus" : "freeform";
                                kinds[kind] = kinds.GetValueOrDefault(kind) + 1;
                            }
                        var edges = brep?.Edges.Count ?? 0; var linear = brep?.Edges.Count(e => e.IsLinear(tolerance)) ?? 0;
                        var arcs = brep?.Edges.Count(e => !e.IsLinear(tolerance) && e.IsArc(tolerance)) ?? 0;
                        var spans = brep?.Edges.Sum(e => e.SpanCount) ?? 0;
                        Console.WriteLine($"    def {Short(group.Key)} x{group.Count()} {group.First().Geometry.GetType().Name} faces={brep?.Faces.Count} {string.Join(",", kinds.Select(k => k.Key + "=" + k.Value))} edges={edges} linear={linear} arcs={arcs} edgeSpans={spans} solid={brep?.IsSolid}");
                        rows.Add(new { name = "census", definition = Short(group.Key), instances = group.Count(), faces = brep?.Faces.Count, kinds, edges, linear, arcs, spans });
                    }
                    break;
                case "full": Record("full", Hld(items, view, scene, tolerance, timeout, Path.Combine(output, stem + "-full.json"))); break;
                case "full2": Record("full2", Hld(items, view, scene, tolerance, timeout, Path.Combine(output, stem + "-full2.json"))); break;
                case "tol10": Record("tol10 (informational, not equivalent)", Hld(items, view, scene, tolerance * 10, timeout, null)); break;
                case "shrink":
                {
                    var timer = Stopwatch.StartNew(); var changed = 0;
                    var normalised = items.Select(i =>
                    {
                        if (i.Geometry is not Brep brep) return i;
                        var copy = brep.DuplicateBrep(); var ok = true;
                        ok &= copy.Faces.ShrinkFaces();
                        if (!ok || !copy.IsValid) { copy.Dispose(); return i; }
                        changed++; return i with { Geometry = copy };
                    }).ToArray();
                    var prepare = timer.Elapsed.TotalSeconds;
                    Record(experiment, Hld(normalised, view, scene, tolerance, timeout, Path.Combine(output, stem + "-" + experiment + ".json")), new { prepareSeconds = prepare, changed });
                    foreach (var pair in normalised.Zip(items)) if (!ReferenceEquals(pair.First.Geometry, pair.Second.Geometry)) pair.First.Geometry.Dispose();
                    break;
                }
                case "defonly":
                    foreach (var group in definitions)
                        Record("only " + Short(group.Key) + " x" + group.Count(), Hld(group.ToArray(), view, scene, tolerance, timeout, null));
                    break;
                case "defout":
                    foreach (var group in definitions)
                        Record("without " + Short(group.Key) + " x" + group.Count(), Hld(items.Where(i => i.Definition != group.Key).ToArray(), view, scene, tolerance, timeout, null));
                    break;
                case "kinds":
                {
                    bool Planar(Item i) => i.Geometry is Brep b && b.Faces.All(f => f.IsPlanar(tolerance));
                    bool PlanarOrCylinder(Item i) => i.Geometry is Brep b && b.Faces.All(f => f.IsPlanar(tolerance) || f.IsCylinder(tolerance));
                    Record("planar-only sources", Hld(items.Where(Planar).ToArray(), view, scene, tolerance, timeout, null));
                    Record("non-planar sources only (what native keeps after planar routing)", Hld(items.Where(i => !Planar(i)).ToArray(), view, scene, tolerance, timeout, null));
                    Record("non-(planar|cylinder) sources only (what native keeps with analytic cylinders in the kernel)", Hld(items.Where(i => !PlanarOrCylinder(i)).ToArray(), view, scene, tolerance, timeout, null));
                    break;
                }
                case var one when one.StartsWith("selfcheck-instance@", StringComparison.Ordinal):
                    // One instance's own world-space copy, and for contrast the first instance of the same definition.
                    foreach (var item in items.Where(i => i.Id.StartsWith(one[19..], StringComparison.Ordinal)))
                    {
                        SelfCheck((Brep)item.Geometry, "instance " + item.Id[..8], tolerance, rows);
                        SelfCheck((Brep)items.First(i => i.Definition == item.Definition).Geometry, "first instance of the same definition", tolerance, rows);
                    }
                    File.WriteAllText(Path.Combine(output, stem + "-selfcheck-instance.json"), JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));
                    break;
                case var check when check.StartsWith("selfcheck@", StringComparison.Ordinal):
                    foreach (var group in definitions.Where(g => Short(g.Key).StartsWith(check[10..], StringComparison.Ordinal)))
                        SelfCheck((Brep)group.First().Geometry, Short(group.Key), tolerance, rows);
                    File.WriteAllText(Path.Combine(output, stem + "-selfcheck.json"), JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));
                    break;
                default:
                {
                    // norm:<op+op>[@definitionPrefix]  exact re-parameterisations / exact occlusion-equivalent replacements.
                    //   closed|bipolar|kinky|shrink: RhinoCommon face splits (same point set).
                    //   hemi: closed sphere -> its viewer-facing hemisphere (same projected disk and front depth; rim = silhouette).
                    if (!experiment.StartsWith("norm:", StringComparison.Ordinal)) throw new ArgumentException("Unknown curved experiment " + experiment);
                    var spec = experiment[5..].Split('@'); var ops = spec[0].Split('+');
                    var subset = spec.Length > 1 ? items.Where(i => Short(i.Definition).StartsWith(spec[1], StringComparison.Ordinal)).ToArray() : items;
                    var timer = Stopwatch.StartNew(); var changed = 0;
                    var toViewer = -view.Direction; toViewer.Unitize();
                    var normalised = subset.Select(i =>
                    {
                        if (i.Geometry is not Brep brep) return i;
                        Brep copy;
                        if (ops.Contains("hemi") && brep.Faces.Count == 1 && brep.IsSolid && brep.Faces[0].TryGetSphere(out var sphere, tolerance * .01))
                        {
                            var x = Vector3d.CrossProduct(toViewer, Math.Abs(toViewer.Z) < .9 ? Vector3d.ZAxis : Vector3d.XAxis); x.Unitize();
                            var arc = new Arc(sphere.Center + toViewer * sphere.Radius, sphere.Center + (toViewer + x) * (sphere.Radius / Math.Sqrt(2)), sphere.Center + x * sphere.Radius);
                            using var profile = new ArcCurve(arc);
                            using var revolved = RevSurface.Create(profile, new Line(sphere.Center, sphere.Center + toViewer));
                            copy = revolved.ToBrep();
                        }
                        else
                        {
                            copy = brep.DuplicateBrep();
                            if (ops.Contains("shrink")) copy.Faces.ShrinkFaces();
                            if (ops.Contains("closed")) copy.Faces.SplitClosedFaces(0);
                            if (ops.Contains("bipolar")) copy.Faces.SplitBipolarFaces();
                            if (ops.Contains("kinky")) copy.Faces.SplitKinkyFaces();
                        }
                        if (!copy.IsValid) { copy.Dispose(); return i; }
                        changed++; return i with { Geometry = copy };
                    }).ToArray();
                    var prepare = timer.Elapsed.TotalSeconds;
                    var name = experiment.Replace(':', '-').Replace('@', '-').Replace('+', '_');
                    Record(experiment, Hld(normalised, view, scene, tolerance, timeout, Path.Combine(output, stem + "-" + name + ".json")), new { prepareSeconds = prepare, changed,
                        faces = normalised.Sum(n => (n.Geometry as Brep)?.Faces.Count ?? 0) });
                    foreach (var pair in normalised.Zip(subset)) if (!ReferenceEquals(pair.First.Geometry, pair.Second.Geometry)) pair.First.Geometry.Dispose();
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Independent look at a body the section validation rejects, using public RhinoCommon only. For every face pair it
    /// intersects the two faces and asks of each intersection curve: how far does it get from the edges the faces share,
    /// and does it really lie inside both trimmed faces there? A real self-intersection has points well away from the
    /// shared edges (many tolerances) that are inside both faces. A tangent-seam artefact stays within about a tolerance.
    /// </summary>
    private static void SelfCheck(Brep brep, string name, double tolerance, List<object> rows)
    {
        Console.WriteLine($"--- selfcheck {name}: valid={brep.IsValid} solid={brep.IsSolid} manifold={brep.IsManifold} faces={brep.Faces.Count} edges={brep.Edges.Count} bbox={brep.GetBoundingBox(true).Diagonal.Length:F4}");
        for (var i = 0; i < brep.Faces.Count; i++)
        for (var j = i + 1; j < brep.Faces.Count; j++)
        {
            var a = brep.Faces[i]; var b = brep.Faces[j];
            var shared = a.AdjacentEdges().Intersect(b.AdjacentEdges()).Select(k => brep.Edges[k]).ToArray();
            using var first = a.DuplicateFace(false); using var second = b.DuplicateFace(false);
            if (!global::Rhino.Geometry.Intersect.Intersection.BrepBrep(first, second, tolerance, out var curves, out var points))
            { Console.WriteLine($"    faces {i}/{j}: BrepBrep failed"); continue; }
            // Dihedral angle along each shared edge: near 0 or 180 degrees means the faces meet tangentially.
            var dihedral = new List<double>();
            foreach (var edge in shared)
                foreach (var t in new[] { .1, .3, .5, .7, .9 })
                {
                    var p = edge.PointAtNormalizedLength(t);
                    if (a.ClosestPoint(p, out var ua, out var va) && b.ClosestPoint(p, out var ub, out var vb))
                        dihedral.Add(RhinoMath.ToDegrees(Vector3d.VectorAngle(a.NormalAt(ua, va), b.NormalAt(ub, vb))));
                }
            var index = 0;
            foreach (var curve in curves)
            {
                double far = 0, farInsideBoth = 0; int samples = 0, insideBoth = 0; var farPoint = Point3d.Unset;
                for (var k = 0; k <= 200; k++)
                {
                    var p = curve.PointAtNormalizedLength(k / 200.0); samples++;
                    var toShared = shared.Length == 0 ? double.PositiveInfinity : shared.Min(e => e.ClosestPoint(p, out var t) ? e.PointAt(t).DistanceTo(p) : double.PositiveInfinity);
                    bool Inside(BrepFace face) => face.ClosestPoint(p, out var u, out var v) && face.PointAt(u, v).DistanceTo(p) <= tolerance && face.IsPointOnFace(u, v) == PointFaceRelation.Interior;
                    var both = Inside(a) && Inside(b);
                    far = Math.Max(far, toShared);
                    if (both) { insideBoth++; if (toShared > farInsideBoth) { farInsideBoth = toShared; farPoint = p; } }
                }
                Console.WriteLine($"    faces {i}/{j} shared edges={shared.Length} dihedral deg=[{(dihedral.Count == 0 ? "-" : $"{dihedral.Min():F3}..{dihedral.Max():F3}")}] curve {index}: length={curve.GetLength():G6} closed={curve.IsClosed} max distance from shared edges={far:G6} ({far / tolerance:F1} tol); interior to both faces at {insideBoth}/{samples} samples, farthest such={farInsideBoth:G6} ({farInsideBoth / tolerance:F1} tol) at {farPoint}");
                rows.Add(new { name, faces = new[] { i, j }, sharedEdges = shared.Length, dihedralMin = dihedral.Count == 0 ? (double?)null : dihedral.Min(), dihedralMax = dihedral.Count == 0 ? (double?)null : dihedral.Max(),
                    curve = index++, length = curve.GetLength(), maxDistanceFromSharedEdges = far, insideBoth, samples, farthestInsideBoth = farInsideBoth });
                // Why the product check objects: its own interval attribution of this curve against the shared edges.
                Console.WriteLine($"        curve domain [{curve.Domain.T0:G9}, {curve.Domain.T1:G9}]");
                foreach (var edge in shared)
                {
                    using var events = global::Rhino.Geometry.Intersect.Intersection.CurveCurve(curve, edge, tolerance, tolerance);
                    Console.WriteLine($"        vs edge {edge.EdgeIndex} (length {edge.GetLength():G6}): " + string.Join("; ", events.Select(e => e.IsOverlap ? $"overlap A[{e.OverlapA.T0:G9}, {e.OverlapA.T1:G9}]" : $"point A={e.ParameterA:G9}")));
                }
                var intervals = M2DPlus.Rhino.Sections.EdgeProvenance.Split(curve, shared.Select(e => ((Curve)e, M2DPlus.Core.Sections.EdgeOrigin.Original)), tolerance);
                foreach (var interval in intervals)
                {
                    if (interval.Origin == M2DPlus.Core.Sections.EdgeOrigin.None)
                    {
                        var mid = interval.Curve.PointAtNormalizedLength(.5);
                        var gap = shared.Min(e => e.ClosestPoint(mid, out var t) ? e.PointAt(t).DistanceTo(mid) : double.PositiveInfinity);
                        Console.WriteLine($"        UNATTRIBUTED interval domain [{interval.Curve.Domain.T0:G9}, {interval.Curve.Domain.T1:G9}] length {interval.Curve.GetLength():G6}; its midpoint is {gap:G6} from the nearest shared edge");
                    }
                    interval.Dispose();
                }
                curve.Dispose();
            }
            if (curves.Length == 0) Console.WriteLine($"    faces {i}/{j} shared edges={shared.Length}: no intersection curves, {points.Length} points");
        }
    }

    private static string Short(string key) { var parts = key.Split('/'); return parts.Length > 1 ? parts[1][..Math.Min(8, parts[1].Length)] : key[..Math.Min(8, key.Length)]; }

    private static Solve Hld(IReadOnlyList<Item> items, ViewRequest view, PreparedScene scene, double tolerance, double timeout, string? dump)
    {
        if (items.Count == 0) return new(0, 0, 0, 0, 0, 0, 0, 0, null, "empty subset");
        var frame = view.Resolve(scene.Bounds, scene.Tolerance);
        using var viewport = new ViewportInfo(); var radius = scene.Bounds.IsValid ? Math.Max(scene.Bounds.Diagonal.Length, 1) : 1;
        viewport.IsParallelProjection = true;
        viewport.SetCameraLocation(frame.Plane.Origin + frame.Plane.ZAxis * (radius * 4 + 10));
        viewport.SetCameraDirection(view.Direction); viewport.SetCameraUp(frame.Plane.YAxis);
        viewport.SetFrustum(frame.X.Min, frame.X.Max, frame.Y.Min, frame.Y.Max, .001, radius * 10 + 100);
        var parameters = new HiddenLineDrawingParameters { AbsoluteTolerance = tolerance, Flatten = false, IncludeHiddenCurves = true, IncludeTangentEdges = false, IncludeTangentSeams = false };
        parameters.SetViewport(viewport);
        foreach (var item in items) if (!parameters.AddGeometry(item.Geometry, item.Id)) return new(0, items.Count, 0, 0, 0, 0, 0, 0, null, "native rejected " + item.Id);
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(timeout));
        var timer = Stopwatch.StartNew();
        HiddenLineDrawing? drawing;
        try { drawing = HiddenLineDrawing.Compute(parameters, false, null, limit.Token); }
        catch (OperationCanceledException) { return new(timer.Elapsed.TotalSeconds, items.Count, 0, 0, 0, 0, 0, 0, null, "timeout"); }
        var seconds = timer.Elapsed.TotalSeconds;
        if (drawing is null) return new(seconds, items.Count, 0, 0, 0, 0, 0, 0, null, limit.IsCancellationRequested ? "timeout" : "null drawing");
        using (drawing)
        {
            if (!drawing.WorldToHiddenLine.TryGetInverse(out var inverse)) return new(seconds, items.Count, 0, 0, 0, 0, 0, 0, null, "bad frame");
            var toPlane = Transform.PlaneToPlane(frame.Plane, Plane.WorldXY) * Transform.PlanarProjection(frame.Plane) * inverse;
            int visible = 0, hidden = 0, unset = 0, other = 0; double visibleLength = 0, hiddenLength = 0;
            var pieces = new List<object>();
            foreach (var segment in drawing.Segments)
            {
                var visibility = segment.SegmentVisibility;
                if (visibility == HiddenLineDrawingSegment.Visibility.Unset) { unset++; continue; }
                if (visibility is not (HiddenLineDrawingSegment.Visibility.Visible or HiddenLineDrawingSegment.Visibility.Hidden)) { other++; continue; }
                if (segment.CurveGeometry is not { } proxy) { other++; continue; }
                using var curve = proxy.DuplicateCurve(); curve.Transform(toPlane);
                var length = curve.GetLength();
                if (visibility == HiddenLineDrawingSegment.Visibility.Visible) { visible++; visibleLength += length; } else { hidden++; hiddenLength += length; }
                if (dump is null) continue;
                double[] points;
                if (curve.TryGetPolyline(out var exact)) points = exact.SelectMany(p => new[] { p.X, p.Y }).ToArray();
                else
                {
                    using var approximation = curve.ToPolyline(tolerance / 8, .02, 0, 0);
                    points = approximation is not null && approximation.TryGetPolyline(out var polyline) ? polyline.SelectMany(p => new[] { p.X, p.Y }).ToArray()
                        : new[] { curve.PointAtStart.X, curve.PointAtStart.Y, curve.PointAtEnd.X, curve.PointAtEnd.Y };
                }
                pieces.Add(new { c = visibility.ToString(), s = segment.ParentCurve?.SourceObject?.Tag as string, p = points });
            }
            if (dump is not null) File.WriteAllText(dump, JsonSerializer.Serialize(new { tolerance, planar2d = true, pieces }));
            return new(seconds, items.Count, visible, hidden, unset, other, visibleLength, hiddenLength, dump, null);
        }
    }
}
