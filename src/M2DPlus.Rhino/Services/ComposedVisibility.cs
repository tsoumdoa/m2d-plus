using M2DPlus.Core;
using M2DPlus.Core.Visibility;
using M2DPlus.Core.Sections;
using M2DPlus.Rhino.Sections;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;
using Point = Rhino.Geometry.Point;

namespace M2DPlus.Rhino.Services;

internal sealed record VisibilityInput(GeometryBase Geometry, PreparedLeaf Source, bool Far = false, bool KernelCandidate = false);

/// <summary>Union over occluders, with separate ownership of planar and native candidates.</summary>
internal static class ComposedVisibility
{
    private sealed record Tag(VisibilityInput Input, int Edge = -1);
    private sealed record NativeRecord(Curve World, Tag Tag, LineClass Class, bool Profile, bool Duplicate);
    private sealed record NativeSpan(double A, double B, LineClass Class);

    internal static string Solve(IReadOnlyList<VisibilityInput> inputs, ViewDrawing output, ViewRequest view,
        NativeSettings settings, CancellationToken token)
    {
        var plane = output.Frame.Plane; double tol = output.Tolerance;
        var extraction = new PlanarExtraction();
        var edgeInputs = new List<VisibilityInput>(); var curved = new List<VisibilityInput>();
        var points = new List<VisibilityInput>(); var owned = new List<GeometryBase>();
        var nativeEdges = new Dictionary<int, List<NativeSpan>>();
        var nativeCurves = new List<(Curve Curve, Tag Tag, LineClass Class, bool Profile)>();
        var records = new List<NativeRecord>();
        Vertex Project(Point3d p) { var d = p - plane.Origin; return new(d * plane.XAxis, d * plane.YAxis, d * plane.ZAxis); }
        Point3d World(Vertex p) => plane.PointAt(p.X, p.Y) + plane.ZAxis * p.Z;
        try
        {
            foreach (var input in VisibilityInputOrder.Canonical(inputs, i => i))
            {
                token.ThrowIfCancellationRequested();
                if (input.Geometry is Point) { points.Add(input); continue; }
                int start = extraction.Edges.Count;
                if (extraction.Add(input.Geometry, Transform.Identity, plane, tol, input.Source.Identity, token, nativeMeshCandidates: true, tangents: view.Settings?.Tangents ?? false))
                    for (int i = start; i < extraction.Edges.Count; i++) edgeInputs.Add(input);
                else curved.Add(input);
            }
            settings.ObserveKernelSources?.Invoke(edgeInputs.Select(i => i.Source.Identity).Distinct(StringComparer.Ordinal).ToArray());
            var kernel = EdgeFaceKernel.Solve(extraction.Faces, extraction.Edges, new(tol, Math.Min(System.Environment.ProcessorCount, 8)), token);
            output._diagnostics.AddRange(kernel.Diagnostics);
            EdgeFaceKernel.PointQuery? query = null;
            try { if (curved.Count != 0 || points.Count != 0) query = new(extraction.Faces, tol, token); }
            catch (ArithmeticException ex) { output._diagnostics.Add(ex.Message); }
            var kernelByEdge = kernel.Intervals.GroupBy(i => i.Edge).ToDictionary(g => g.Key, g => g.ToArray());
            var native = curved.Select(i => new Tag(i)).ToList();
            var curvedBounds = curved.Where(i => i.Geometry is Brep or Mesh or Surface or Extrusion)
                .Select(i => i.Geometry.GetBoundingBox(Transform.PlaneToPlane(plane, Plane.WorldXY))).ToArray();
            bool Overlaps(BoundingBox a) => curvedBounds.Any(b => a.Min.X <= b.Max.X + tol && a.Max.X >= b.Min.X - tol &&
                a.Min.Y <= b.Max.Y + tol && a.Max.Y >= b.Min.Y - tol);
            // Curves cannot occlude. No curved occluder means no planar candidates enter HLD.
            for (int i = 0; i < extraction.Edges.Count; i++)
            {
                token.ThrowIfCancellationRequested(); var e = extraction.Edges[i];
                // H(planar) union H(curved) is already Hidden when planar coverage
                // is complete. These non-occluding curves need no foreign query.
                if (!kernelByEdge.TryGetValue(i, out var known) || known.All(k => k.Class == VisibilityClass.Hidden)) continue;
                if (!Overlaps(new BoundingBox(new[] { new Point3d(e.A.X, e.A.Y, e.A.Z), new Point3d(e.B.X, e.B.Y, e.B.Z) }))) continue;
                var curve = new LineCurve(World(e.A), World(e.B)); owned.Add(curve);
                native.Add(new(new(curve, edgeInputs[i].Source, edgeInputs[i].Far), i)); nativeEdges.Add(i, []);
            }
            foreach (var p in points)
                if (Overlaps(p.Geometry.GetBoundingBox(Transform.PlaneToPlane(plane, Plane.WorldXY)))) native.Add(new(p));
            int missingCoverage = 0, duplicateUnresolved = 0;
            var handledPoints = new HashSet<VisibilityInput>();
            if (native.Count != 0)
            {
                using var viewport = new ViewportInfo();
                var bounds = BoundingBox.Empty; foreach (var i in inputs) bounds.Union(i.Geometry.GetBoundingBox(true));
                double radius = Math.Max(1, bounds.IsValid ? bounds.Diagonal.Length : 1);
                viewport.IsParallelProjection = true;
                viewport.SetCameraLocation(plane.Origin + plane.ZAxis * (radius * 4 + 10));
                viewport.SetCameraDirection(view.Direction); viewport.SetCameraUp(plane.YAxis);
                viewport.SetFrustum(output.Frame.X.Min, output.Frame.X.Max, output.Frame.Y.Min, output.Frame.Y.Max, .001, radius * 10 + 100);
                var parameters = new HiddenLineDrawingParameters { AbsoluteTolerance = tol, Flatten = false, IncludeHiddenCurves = true,
                    IncludeTangentEdges = view.Settings?.Tangents ?? false, IncludeTangentSeams = view.Settings?.Seams ?? false };
                parameters.SetViewport(viewport);
                foreach (var tag in VisibilityInputOrder.Native(native, t => t.Input with { KernelCandidate = t.Edge >= 0 }, settings))
                {
                    token.ThrowIfCancellationRequested();
                    if (!parameters.AddGeometry(tag.Input.Geometry, tag)) throw new InvalidOperationException("Native rejected composition candidate");
                }
                NativeDrawing.CountHld(); output.NativeCalls++;
                using var drawing = HiddenLineDrawing.Compute(parameters, settings.MultipleThreads, NativeDrawing.NativeProgress, token);
                token.ThrowIfCancellationRequested();
                if (drawing is null || !drawing.WorldToHiddenLine.TryGetInverse(out var inverse)) throw new InvalidOperationException("Composition HLD failed");
                var nativeParents = new HashSet<int>();
                foreach (var segment in drawing.Segments)
                {
                    token.ThrowIfCancellationRequested(); var visibility = segment.SegmentVisibility;
                    if (visibility == HiddenLineDrawingSegment.Visibility.Unset)
                    {
                        var evidence = NativeUncertainty.Inspect(segment, t => (t as Tag)?.Input.Source, Transform.PlanarProjection(plane) * inverse);
                        if (evidence.Kind == UnsetKind.EmptySlot) { output.EmptyNativeSlots++; continue; }
                        if (evidence.Kind == UnsetKind.ZeroLength) { output.ZeroLengthNativeSegments++; continue; }
                        // A real Unset can be the only native candidate. Its source is quarantined below.
                        if (evidence.Source is { } source) output.UncertainRoutingSources.Add(source.Identity);
                        else output.RoutingQuarantine = true;
                        output.RoutingInvalid++;
                        output._diagnostics.Add($"Composition Unset {segment.Index}: {evidence.Kind}; source={evidence.Source?.Identity}; {evidence.Detail}");
                        continue;
                    }
                    if (visibility == HiddenLineDrawingSegment.Visibility.Projecting) { output.ProjectingSegments++; continue; }
                    if (segment.ParentCurve?.SourceObject?.Tag is not Tag tag || segment.CurveGeometry is not { } proxy)
                        throw new InvalidOperationException("Unattributed composition candidate");
                    if (tag.Edge < 0) nativeParents.Add(segment.ParentCurve.Index);
                    var kind = visibility == HiddenLineDrawingSegment.Visibility.Visible ? LineClass.Visible :
                        visibility == HiddenLineDrawingSegment.Visibility.Hidden ? LineClass.Hidden : LineClass.Unresolved;
                    if (visibility == HiddenLineDrawingSegment.Visibility.Duplicate)
                    {
                        output.DuplicateSegments++;
                        if (extraction.Faces.Count == 0 && tag.Edge < 0) continue;
                    }
                    var world = proxy.DuplicateCurve(); world.EnsurePrivateCopy(); world.Transform(inverse); owned.Add(world);
                    records.Add(new(world, tag, kind, segment.IsSceneSilhouette, visibility == HiddenLineDrawingSegment.Visibility.Duplicate));
                }
                output.CurvedCandidateEdges = nativeParents.Count;
                foreach (var record in ResolveDuplicates(records, plane, tol, owned, token))
                {
                    var tag = record.Tag; var world = record.World;
                    if (tag.Edge >= 0)
                    {
                        var e = extraction.Edges[tag.Edge]; var a = World(e.A); var b = World(e.B); var delta = b - a;
                        double Param(Point3d p) => Math.Clamp(((p - a) * delta) / delta.SquareLength, 0, 1);
                        double u = Param(world.PointAtStart), v = Param(world.PointAtEnd);
                        nativeEdges[tag.Edge].Add(new(Math.Min(u, v), Math.Max(u, v), record.Class));
                    }
                    else if (!record.Duplicate || record.Class == LineClass.Unresolved)
                    {
                        if (record.Duplicate) duplicateUnresolved++;
                        nativeCurves.Add((world, tag, record.Class, record.Profile));
                    }
                }

                foreach (var point in drawing.Points)
                {
                    token.ThrowIfCancellationRequested();
                    if (point.SourceObject?.Tag is not Tag tag) { output.RoutingQuarantine = true; output.RoutingInvalid++; continue; }
                    handledPoints.Add(tag.Input);
                    var world = point.Location; world.Transform(inverse);
                    var k = query?.Classify(Project(world), token) ?? VisibilityClass.Unresolved;
                    if (point.PointVisibility == HiddenLineDrawingPoint.Visibility.Hidden || k == VisibilityClass.Hidden) { output.HiddenPoints++; continue; }
                    if (point.PointVisibility != HiddenLineDrawingPoint.Visibility.Visible || k == VisibilityClass.Unresolved)
                    { output.RoutingInvalid++; output._diagnostics.Add($"Uncertain point suppressed: {tag.Input.Source.Identity}"); continue; }
                    output.Pieces.Add(new(new Point(plane.ClosestPoint(world)), tag.Input.Source, LineClass.Visible, new Point(world)));
                }
            }
            output.KernelEdges = kernelByEdge.Count; output.PlanarNativeEdges = nativeEdges.Count;
            output.NativeEdges = nativeEdges.Count + output.CurvedCandidateEdges; output.NativeFragments = nativeCurves.Count;
            foreach (var interval in kernel.Intervals)
            {
                token.ThrowIfCancellationRequested(); var e = extraction.Edges[interval.Edge];
                var input = edgeInputs[interval.Edge]; var kind = PlanarExtraction.OutputClass(interval.Class);
                void Emit(double a, double b, LineClass c)
                {
                    if (b <= a) return;
                    var wa = World(e.A); var wb = World(e.B); var delta = wb - wa;
                    Add(new LineCurve(wa + delta * a, wa + delta * b), input, c, false, output, kernelOwned: true);
                }
                if (kind == LineClass.Hidden || !nativeEdges.TryGetValue(interval.Edge, out var spans)) { Emit(interval.Start, interval.End, kind); continue; }
                var ends = spans.SelectMany(s => new[] { Math.Max(interval.Start, s.A), Math.Min(interval.End, s.B) })
                    .Where(t => t > interval.Start && t < interval.End).Append(interval.Start).Append(interval.End).Distinct().Order().ToArray();
                for (int j = 1; j < ends.Length; j++)
                {
                    double mid = (ends[j - 1] + ends[j]) / 2;
                    var hits = spans.Where(s => s.A <= mid && s.B >= mid).ToArray();
                    if (hits.Length == 0)
                    {
                        if (missingCoverage++ < 8) output._diagnostics.Add($"Missing native coverage: edge={interval.Edge}; source={input.Source.Identity}; t={ends[j-1]:R}..{ends[j]:R}; A={e.A}; B={e.B}");
                    }
                    var c = hits.Length == 0 || hits.Any(s => s.Class == LineClass.Unresolved) || kind == LineClass.Unresolved ? LineClass.Unresolved :
                        kind == LineClass.Hidden || hits.Any(s => s.Class == LineClass.Hidden) ? LineClass.Hidden : LineClass.Visible;
                    Emit(ends[j - 1], ends[j], c);
                }
            }
            foreach (var c in nativeCurves)
            {
                token.ThrowIfCancellationRequested();
                if (c.Class != LineClass.Visible || extraction.Faces.Count == 0)
                { Add(c.Curve.DuplicateCurve(), c.Tag.Input, c.Class, c.Profile, output); continue; }
                foreach (var part in ClipCurve(c.Curve, extraction.Faces, query, plane, tol, token))
                    Add(part.Curve, c.Tag.Input, part.Class, c.Profile, output);
            }
            foreach (var p in points.Where(p => !handledPoints.Contains(p)))
            {
                token.ThrowIfCancellationRequested();
                // Missing records from a submitted native point are uncertainty, not visible.
                if (native.Any(t => t.Input == p)) { output.RoutingInvalid++; continue; }
                var world = ((Point)p.Geometry).Location;
                var k = query?.Classify(Project(world), token) ?? VisibilityClass.Unresolved;
                if (k == VisibilityClass.Hidden) output.HiddenPoints++;
                else if (k == VisibilityClass.Visible) output.Pieces.Add(new(new Point(plane.ClosestPoint(world)), p.Source, LineClass.Visible, new Point(world)));
                else { output.RoutingInvalid++; output._diagnostics.Add($"Kernel cannot decide point {p.Source.Identity}"); }
            }
            output._diagnostics.Add($"Composition coverage: missing={missingCoverage}; unresolved curved duplicate intervals={duplicateUnresolved}.");
            foreach (var piece in output.Pieces)
                if (output.RoutingQuarantine || output.UncertainRoutingSources.Contains(piece.SourceIdentity)) piece.Class = LineClass.Unresolved;
            Reconcile(output, inputs, token);
            return $"{(curved.Count == 0 ? "Planar kernel" : "Planar kernel + curved native HLD")}; kernel edges={output.KernelEdges}; native candidates={output.NativeEdges}; planar edges submitted={nativeEdges.Count}; curved candidate edges={output.CurvedCandidateEdges}; native fragments={output.NativeFragments}; native calls={output.NativeCalls}";
        }
        finally { foreach (var g in owned) g.Dispose(); }
    }

    private static IEnumerable<NativeRecord> ResolveDuplicates(List<NativeRecord> records, Plane plane, double tolerance,
        List<GeometryBase> owned, CancellationToken token)
    {
        var projection = Transform.PlanarProjection(plane); var toXY = Transform.PlaneToPlane(plane, Plane.WorldXY);
        using var tree = new RTree();
        var projected = new Curve[records.Count]; var depths = new BoundingBox[records.Count];
        try
        {
            for (int i = 0; i < records.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                projected[i] = records[i].World.DuplicateCurve(); projected[i].Transform(projection);
                depths[i] = records[i].World.GetBoundingBox(toXY);
                if (!records[i].Duplicate && records[i].Class != LineClass.Unresolved)
                { var bounds = projected[i].GetBoundingBox(true); bounds.Inflate(tolerance); tree.Insert(bounds, i); }
            }
            for (int i = 0; i < records.Count; i++)
            {
                token.ThrowIfCancellationRequested(); var record = records[i];
                if (!record.Duplicate) { yield return record; continue; }
                var nearby = new List<int>(); tree.Search(projected[i].GetBoundingBox(true), (_, a) => nearby.Add(a.Id));
                var references = new List<Curve>();
                try
                {
                    foreach (int j in nearby)
                    {
                        token.ThrowIfCancellationRequested();
                        // A complete closer-depth envelope proves that a retained native
                        // witness owns this projected overlap, even after planar clipping.
                        if (depths[j].Min.Z >= depths[i].Max.Z)
                        { references.Add(projected[j].DuplicateCurve()); continue; }
                        using var projectedEvents = Intersection.CurveCurve(projected[i], projected[j], tolerance, tolerance);
                        if (projectedEvents is not null)
                            foreach (var overlap in projectedEvents)
                            {
                                if (!overlap.IsOverlap) continue;
                                using var a = CurveIntervals.Trim(record.World, overlap.OverlapA);
                                using var b = CurveIntervals.Trim(records[j].World, overlap.OverlapB);
                                if (a is null || b is null || !FrontWitness(a, b, plane, tolerance)) continue;
                                var reference = CurveIntervals.Trim(projected[j], overlap.OverlapB);
                                if (reference is not null) references.Add(reference);
                            }
                        using var events = Intersection.CurveCurve(record.World, records[j].World, tolerance, tolerance);
                        if (events is null) continue;
                        foreach (var hit in events)
                        {
                            if (!hit.IsOverlap) continue;
                            var reference = records[j].World.Trim(hit.OverlapB);
                            if (reference is null) continue;
                            reference.Transform(projection); references.Add(reference);
                        }
                    }
                    var parts = EdgeProvenance.Split(projected[i], references.Select(c => (c, EdgeOrigin.Original)), tolerance);
                    foreach (var part in parts)
                    {
                        using (part)
                        {
                            var world = CurveIntervals.Trim(record.World, part.Curve.Domain);
                            if (world is null) continue; // exactly point-projecting alias
                            owned.Add(world);
                            yield return record with { World = world, Class = part.Origin == EdgeOrigin.Original ? LineClass.Hidden : LineClass.Unresolved };
                        }
                    }
                }
                finally { foreach (var c in references) c.Dispose(); }
            }
        }
        finally { foreach (var c in projected) c?.Dispose(); }
    }

    internal static bool FrontWitness(Curve candidate, Curve witness, Plane plane, double tolerance)
    {
        var transform = Transform.PlaneToPlane(plane, Plane.WorldXY);
        using var a = candidate.ToNurbsCurve(); using var b = witness.ToNurbsCurve();
        a.Transform(transform); b.Transform(transform);
        double DistanceXY(Point3d p, Point3d q) => Math.Sqrt((p.X-q.X)*(p.X-q.X)+(p.Y-q.Y)*(p.Y-q.Y));
        if (DistanceXY(a.PointAtStart, b.PointAtEnd) < DistanceXY(a.PointAtStart, b.PointAtStart)) b.Reverse();
        if (a.Degree != b.Degree || a.Points.Count != b.Points.Count || a.Knots.Count != b.Knots.Count) return false;
        a.Domain = new(0,1); b.Domain = new(0,1);
        for (int i=0; i<a.Knots.Count; i++) if (a.Knots[i] != b.Knots[i]) return false;
        double ratio = a.Points[0].Weight / b.Points[0].Weight;
        for (int i=0; i<a.Points.Count; i++)
        {
            var p = a.Points[i]; var q = b.Points[i];
            if (p.Weight <= 0 || q.Weight <= 0 || p.Weight != q.Weight * ratio ||
                DistanceXY(p.Location,q.Location) > tolerance / 16 || q.Location.Z < p.Location.Z) return false;
        }
        // Matching rational basis with positive weights: the depth difference is
        // bounded by its control coefficients throughout the complete overlap.
        return true;
    }

    private static void Reconcile(ViewDrawing output, IReadOnlyList<VisibilityInput> inputs, CancellationToken token)
    {
        var lines = new List<(DrawingPiece Piece, Curve Curve)>(); var loci = new List<Locus>();
        var plane = output.Frame.Plane;
        try
        {
            foreach (var piece in output.Items)
            {
                token.ThrowIfCancellationRequested(); var g = piece.CopyGeometry();
                if (g is not Curve c || !c.IsLinear(output.Tolerance) || piece.DepthGeometry is not Curve wc || !wc.IsLinear(output.Tolerance)) { g.Dispose(); continue; }
                plane.ClosestParameter(c.PointAtStart, out var ax, out var ay); plane.ClosestParameter(c.PointAtEnd, out var bx, out var by);
                int index = lines.Count; lines.Add((piece, c));
                loci.Add(new(ax, ay, bx, by, piece.Class == LineClass.Visible ? VisibilityClass.Visible : piece.Class == LineClass.Hidden ? VisibilityClass.Hidden : VisibilityClass.Unresolved,
                    index.ToString("D10", System.Globalization.CultureInfo.InvariantCulture)));
            }
            var union = VisibilityLoci.Union(loci, output.Tolerance, preserveOwnership: true, token);
            var byId = inputs.Select(i => i.Source).DistinctBy(s => s.Identity).ToDictionary(s => s.Identity, StringComparer.Ordinal);
            foreach (var line in lines) output.Pieces.Remove(line.Piece);
            foreach (var l in union)
            {
                token.ThrowIfCancellationRequested(); var (piece, curve) = lines[int.Parse(l.Source, System.Globalization.CultureInfo.InvariantCulture)];
                var a = plane.PointAt(l.X0, l.Y0); var b = plane.PointAt(l.X1, l.Y1);
                if (a == b) { output.ProjectingSegments++; continue; }
                curve.ClosestPoint(a, out var ta); curve.ClosestPoint(b, out var tb);
                var world = piece.DepthGeometry as Curve;
                if (world is null) throw new InvalidOperationException("Missing world support during locus reconciliation");
                var projected = new LineCurve(a, b);
                var depth = new LineCurve(world.PointAt(ta), world.PointAt(tb)) { Domain = projected.Domain };
                output.Pieces.Add(new(projected, byId[piece.SourceIdentity], PlanarExtraction.OutputClass(l.Class), depth)
                    { Profile = piece.Profile, FarCandidate = piece.FarCandidate, KernelOwned = piece.KernelOwned });
            }
        }
        finally { foreach (var line in lines) { line.Curve.Dispose(); line.Piece.Dispose(); } }
    }

    private static void Add(Curve world, VisibilityInput input, LineClass kind, bool profile, ViewDrawing output, bool kernelOwned = false)
    {
        Curve? projected = null;
        try
        {
            projected = world.DuplicateCurve();
            if (!projected.Transform(Transform.PlanarProjection(output.Frame.Plane))) throw new InvalidOperationException("Composition projection failed");
            if (projected.PointAtStart == projected.PointAtEnd && projected.GetBoundingBox(true).Diagonal.SquareLength == 0)
            { output.ProjectingSegments++; projected.Dispose(); world.Dispose(); return; }
            output.Pieces.Add(new(projected, input.Source, kind, world) { Profile = profile, FarCandidate = input.Far, KernelOwned = kernelOwned });
        }
        catch { world.Dispose(); projected?.Dispose(); throw; }
    }

    private static IEnumerable<(Curve Curve, LineClass Class)> ClipCurve(Curve world, IReadOnlyList<PlanarFace> faces,
        EdgeFaceKernel.PointQuery? query, Plane plane, double tolerance, CancellationToken token)
    {
        var ends = new SortedSet<double> { world.Domain.Min, world.Domain.Max };
        var transform = Transform.PlaneToPlane(plane, Plane.WorldXY);
        var bounds = world.GetBoundingBox(transform); bool failed = query is null;
        using var local = world.DuplicateCurve(); local.Transform(transform);
        var ids = query?.OverlappingFaces(bounds.Min.X, bounds.Min.Y, bounds.Max.X, bounds.Max.Y) ?? Enumerable.Range(0, faces.Count).ToArray();
        foreach (int fi in ids)
        {
            var face = faces[fi];
            token.ThrowIfCancellationRequested(); var all = face.Loops.SelectMany(l => l).ToArray();
            if (bounds.Min.X > all.Max(p => p.X) + tolerance || bounds.Max.X < all.Min(p => p.X) - tolerance ||
                bounds.Min.Y > all.Max(p => p.Y) + tolerance || bounds.Max.Y < all.Min(p => p.Y) - tolerance || bounds.Min.Z > all.Max(p => p.Z)) continue;
            var a = all[0]; Vector3d normal = Vector3d.Zero;
            for (int i = 1; i + 1 < all.Length && normal.IsZero; i++)
                normal = Vector3d.CrossProduct(new Vector3d(all[i].X - a.X, all[i].Y - a.Y, all[i].Z - a.Z),
                    new Vector3d(all[i + 1].X - a.X, all[i + 1].Y - a.Y, all[i + 1].Z - a.Z));
            if (normal.Z == 0) continue;
            void Events(Plane boundary)
            {
                token.ThrowIfCancellationRequested();
                if (!boundary.IsValid) { failed = true; return; }
                using var hits = Intersection.CurvePlane(local, boundary, tolerance);
                if (hits is null) return; // RhinoCommon contract: no intersections recorded.
                foreach (var hit in hits)
                    if (hit.IsOverlap) { ends.Add(hit.OverlapA.Min); ends.Add(hit.OverlapA.Max); }
                    else ends.Add(hit.ParameterA);
            }
            Events(new Plane(new Point3d(a.X, a.Y, a.Z - tolerance), normal));
            foreach (var loop in face.Loops)
                for (int i = 0; i < loop.Length; i++)
                {
                    var p = loop[i]; var q = loop[(i + 1) % loop.Length];
                    var n = new Vector3d(q.Y - p.Y, p.X - q.X, 0);
                    if (!n.IsZero) Events(new Plane(new Point3d(p.X, p.Y, 0), n));
                }
        }
        var cuts = ends.Where(t => t >= world.Domain.Min && t <= world.Domain.Max).ToArray();
        double start = cuts[0]; LineClass? previous = null;
        for (int i = 1; i < cuts.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            var mid = local.PointAt((cuts[i - 1] + cuts[i]) / 2);
            var kind = PlanarExtraction.OutputClass(failed ? VisibilityClass.Unresolved : query!.Classify(new(mid.X, mid.Y, mid.Z), token));
            if (previous is { } old && old != kind)
            {
                var part = CurveIntervals.Trim(world, new(start, cuts[i - 1])) ?? throw new InvalidOperationException("Composition curve trim failed");
                yield return (part, old); start = cuts[i - 1];
            }
            previous = kind;
        }
        if (previous is { } last)
            yield return (CurveIntervals.Trim(world, new(start, cuts[^1])) ?? throw new InvalidOperationException("Composition curve trim failed"), last);
    }
}
