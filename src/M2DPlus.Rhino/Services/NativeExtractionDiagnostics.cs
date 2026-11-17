using Rhino.Geometry;

namespace M2DPlus.Rhino.Services;

internal enum ExtractionKind { EmptySlot, ZeroLength, Source, Unattributed }
internal sealed record ExtractionEvidence(ExtractionKind Kind, VisibilityInput? Input = null, string? Detail = null)
{
    internal PreparedLeaf? Source => Input?.Source;
}

internal static class NativeExtractionDiagnostics
{
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

    internal static ExtractionEvidence Inspect(HiddenLineDrawingSegment segment, Func<object?, VisibilityInput?> resolve, Transform projection)
    {
        VisibilityInput? input = null; string? detail = null;
        try
        {
            var parent = segment.ParentCurve;
            if (segment.Index < 0 && parent is null) return new(ExtractionKind.EmptySlot);
            input = resolve(parent?.SourceObject?.Tag);
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
                    return new(ExtractionKind.ZeroLength, input);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or NullReferenceException or ArgumentException) { }
        return new(input is not null ? ExtractionKind.Source : ExtractionKind.Unattributed, input, detail);
    }

    internal static bool Affects(BoundingBox a, BoundingBox b, double tolerance) => a.IsValid && b.IsValid &&
        a.Min.X <= b.Max.X + tolerance && a.Max.X >= b.Min.X - tolerance &&
        a.Min.Y <= b.Max.Y + tolerance && a.Max.Y >= b.Min.Y - tolerance &&
        a.Min.Z <= b.Max.Z + tolerance && a.Max.Z >= b.Min.Z - tolerance;
}
