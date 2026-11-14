using Rhino.Geometry;

namespace M2DPlus.Rhino.Services;

internal enum UnsetKind { EmptySlot, ZeroLength, Source, Region, Unbounded }
internal sealed record UnsetEvidence(UnsetKind Kind, PreparedLeaf? Source = null, BoundingBox? Bounds = null, string? Detail = null);

internal static class NativeUncertainty
{
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
