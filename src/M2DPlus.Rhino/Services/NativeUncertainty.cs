using Rhino.Geometry;

namespace M2DPlus.Rhino.Services;

internal enum UnsetKind { EmptySlot, ZeroLength, Source, Region, Unbounded }
internal sealed record UnsetEvidence(UnsetKind Kind, PreparedLeaf? Source = null, BoundingBox? Bounds = null, string? Detail = null);

internal static class NativeUncertainty
{
    // A detached, valid proxy bounds the uncertainty of this record. The source
    // remains registered as an occluder; no reduced-scene visibility is inferred.
    internal static bool TryDetach(HiddenLineDrawingSegment segment, Transform inverse, out Curve? world)
    {
        world = null;
        try
        {
            if (segment.CurveGeometry is not { IsValid: true } proxy) return false;
            world = proxy.DuplicateCurve(); world.EnsurePrivateCopy();
            if (world.Transform(inverse) && world.IsValid && world.GetBoundingBox(true).IsValid &&
                double.IsFinite(world.GetLength()) && world.Domain.IsIncreasing) return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or NullReferenceException or ArgumentException) { }
        world?.Dispose(); world = null; return false;
    }

    internal static bool RetainLocal(HiddenLineDrawingSegment segment, UnsetEvidence evidence, Transform inverse,
        ViewDrawing output, List<BoundingBox> regions, Action<string, Curve>? observe = null)
    {
        if (evidence.Source is not { } source || !TryDetach(segment, inverse, out var world)) return false;
        Curve? projected = null;
        try
        {
            projected = world!.DuplicateCurve();
            if (!projected.Transform(Transform.PlanarProjection(output.Frame.Plane)) || !projected.IsValid) return false;
            var bounds = projected.GetBoundingBox(true);
            if (!bounds.IsValid) return false;
            observe?.Invoke(source.Identity, world);
            regions.Add(bounds);
            output.Pieces.Add(new(projected, source, M2DPlus.Core.LineClass.Unresolved, world));
            projected = null; world = null;
            output._diagnostics.Add($"Local Unset retained: source={source.Identity}; only its projected footprint is quarantined.");
            return true;
        }
        finally { projected?.Dispose(); world?.Dispose(); }
    }

    // Cuts are only a partitioning aid. A subcurve keeps confidence exclusively
    // when its *entire conservative bound* is disjoint from every uncertain box.
    // Missing/inexact plane events can therefore only withhold too much.
    internal static IEnumerable<(Curve Curve, bool Affected)> Partition(Curve curve, IReadOnlyList<BoundingBox> regions,
        double tolerance, CancellationToken token, Func<Curve, double, double, Curve?>? trim = null)
    {
        var cuts = new SortedSet<double> { curve.Domain.Min, curve.Domain.Max };
        var bounds = curve.GetBoundingBox(true);
        foreach (var box in regions.Where(b => Affects(b, bounds, tolerance)))
        {
            token.ThrowIfCancellationRequested();
            foreach (var (axis, min, max) in new[] { (Vector3d.XAxis, box.Min.X, box.Max.X),
                (Vector3d.YAxis, box.Min.Y, box.Max.Y), (Vector3d.ZAxis, box.Min.Z, box.Max.Z) })
            foreach (double d in new[] { min - 2 * tolerance, max + 2 * tolerance })
            {
                using var hits = global::Rhino.Geometry.Intersect.Intersection.CurvePlane(curve, new Plane(Point3d.Origin + axis * d, axis), tolerance / 16);
                if (hits is null) continue;
                foreach (var hit in hits)
                    if (hit.IsOverlap) { cuts.Add(hit.OverlapA.Min); cuts.Add(hit.OverlapA.Max); }
                    else cuts.Add(hit.ParameterA);
            }
        }
        var ts = cuts.Where(curve.Domain.IncludesParameter).ToArray();
        var parts = new List<(Curve Curve, bool Affected)>();
        try
        {
            for (int i = 1; i < ts.Length; i++)
            {
                token.ThrowIfCancellationRequested();
                var part = trim is null ? curve.Trim(ts[i - 1], ts[i]) : trim(curve, ts[i - 1], ts[i]);
                if (part is null)
                {
                    // Native trimming can refuse a tiny but nonzero event cell.
                    // Roll back the entire partition: never leave a confidence gap
                    // or silently drop that interval, and never fail the view.
                    foreach (var previous in parts) previous.Curve.Dispose();
                    parts.Clear(); parts.Add((curve.DuplicateCurve(), true)); break;
                }
                parts.Add((part, regions.Any(b => Affects(b, part.GetBoundingBox(true), tolerance))));
            }
            for (int i = 0; i < parts.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                var part = parts[i]; parts[i] = (null!, false); // ownership transfers to the caller
                yield return part;
            }
        }
        finally { foreach (var part in parts) part.Curve?.Dispose(); }
    }

    internal static void ApplyFootprints(ViewDrawing output, IReadOnlyList<PreparedLeaf> sources, bool quarantine,
        HashSet<string> uncertainSources, IReadOnlyList<BoundingBox> regions, double tolerance, CancellationToken token, bool localize = true,
        IReadOnlySet<string>? localSources = null)
    {
        var toXY = Transform.PlaneToPlane(output.Frame.Plane, Plane.WorldXY);
        var occluders = sources.Where(s => s.Geometry is Brep or Mesh or Surface or Extrusion)
            .Select(s => ConservativeBound(s.Geometry, toXY)).ToArray();
        int proved = 0, refused = 0;
        foreach (var piece in output.Pieces.ToArray())
        {
            token.ThrowIfCancellationRequested();
            using var geometry = piece.CopyGeometry();
            bool whole = quarantine || uncertainSources.Contains(piece.SourceIdentity);
            bool touched = regions.Any(b => Affects(b, geometry.GetBoundingBox(true), tolerance));
            bool localSource = localSources?.Contains(piece.SourceIdentity) == true;
            if (!whole && !touched && !localSource) continue;
            if (geometry is Point) { output.Pieces.Remove(piece); piece.Dispose(); continue; }
            if (whole || !localize || piece.Class == M2DPlus.Core.LineClass.Unresolved) { piece.Class = M2DPlus.Core.LineClass.Unresolved; continue; }
            if (geometry is not Curve curve) { piece.Class = M2DPlus.Core.LineClass.Unresolved; continue; }
            var source = sources.First(s => s.Identity == piece.SourceIdentity);
            foreach (var (part, affected) in Partition(curve, regions, tolerance, token))
            {
                var depth = (piece.DepthGeometry as Curve)?.Trim(part.Domain);
                var kind = affected ? M2DPlus.Core.LineClass.Unresolved : piece.Class;
                // Native Unset can accompany wrong native Visible records elsewhere
                // on that source. Disjointness localises the *record*, not visibility.
                // Restored Visible therefore requires a continuous all-body proof.
                if (kind == M2DPlus.Core.LineClass.Visible)
                {
                    if (depth is not null && Clear(ConservativeBound(depth, toXY), occluders, tolerance)) proved++;
                    else { kind = M2DPlus.Core.LineClass.Unresolved; refused++; }
                }
                output.Pieces.Add(new(part, source, kind, depth, piece.SolvedClass)
                    { Profile = piece.Profile, FarCandidate = piece.FarCandidate, KernelOwned = piece.KernelOwned });
            }
            output.Pieces.Remove(piece); piece.Dispose();
        }
        if (proved + refused != 0) output._diagnostics.Add($"Local quarantine Visible certificates: all-body bound proofs={proved}; refused without continuous proof={refused}. Sampling never upgrades visibility.");
    }

    private static BoundingBox ConservativeBound(GeometryBase geometry, Transform toXY)
    {
        var box = geometry.GetBoundingBox(true);
        if (!box.IsValid) return BoundingBox.Unset;
        var corners = box.GetCorners();
        foreach (ref var p in corners.AsSpan()) p.Transform(toXY);
        box = new(corners);
        double scale = Math.Max(1, corners.Max(p => p.DistanceTo(Point3d.Origin)));
        box.Inflate(128 * 2.220446049250313e-16 * scale);
        return box;
    }

    // Whole conservative boxes, including self-occlusion and every owned body.
    // XY disjointness or a maximum forward gap <= tolerance/2 excludes each
    // occluder throughout the span. The half-tolerance reserve covers roundoff;
    // no sample point, native label, source exclusion or spatial index decides it.
    internal static bool Clear(BoundingBox candidate, IReadOnlyList<BoundingBox> occluders, double tolerance) => candidate.IsValid &&
        occluders.All(b => b.IsValid && (b.Max.X < candidate.Min.X - tolerance || b.Min.X > candidate.Max.X + tolerance ||
            b.Max.Y < candidate.Min.Y - tolerance || b.Min.Y > candidate.Max.Y + tolerance ||
            b.Max.Z <= candidate.Min.Z + tolerance / 2));

    // The native table contains null slots after splitting/clipping. Its public Index
    // accessor returns -1 for those slots; no geometry proxy is touched in that case.
    // A real Unset record is examined only through guarded metadata/validation access.
    internal static UnsetEvidence Inspect(HiddenLineDrawingSegment segment, Func<object?, PreparedLeaf?> resolve, Transform projection)
    {
        PreparedLeaf? source = null; string? detail = null;
        try
        {
            var parent = segment.ParentCurve;
            if (segment.Index < 0 && parent is null) return new(UnsetKind.EmptySlot);
            source = resolve(parent?.SourceObject?.Tag);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NullReferenceException or ArgumentException) { }
        try
        {
            var proxy = segment.CurveGeometry;
            if (proxy is { IsValid: true })
            {
                var bounds = proxy.GetBoundingBox(projection);
                detail = FormattableString.Invariant($"length={proxy.GetLength():R}; domain={proxy.Domain.T0:R},{proxy.Domain.T1:R}; bounds={bounds}; start={proxy.PointAtStart}; end={proxy.PointAtEnd}");
                // Exactly degenerate, not merely shorter than modelling tolerance.
                if (bounds.IsValid && bounds.Diagonal.Length == 0 && proxy.GetLength() == 0)
                    return new(UnsetKind.ZeroLength, source);
                if (source is null && bounds.IsValid) return new(UnsetKind.Region, Bounds: bounds);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or NullReferenceException or ArgumentException) { }
        return source is not null ? new(UnsetKind.Source, source, Detail: detail) : new(UnsetKind.Unbounded, Detail: detail);
    }

    internal static bool Affects(BoundingBox a, BoundingBox b, double tolerance) => a.IsValid && b.IsValid &&
        a.Min.X <= b.Max.X + tolerance && a.Max.X >= b.Min.X - tolerance &&
        a.Min.Y <= b.Max.Y + tolerance && a.Max.Y >= b.Min.Y - tolerance &&
        a.Min.Z <= b.Max.Z + tolerance && a.Max.Z >= b.Min.Z - tolerance;
}
