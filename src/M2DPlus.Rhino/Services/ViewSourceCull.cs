using M2DPlus.Core;
using M2DPlus.Core.Placement;
using M2DPlus.Core.Views;
using Rhino.Geometry;

namespace M2DPlus.Rhino.Services;

/// <summary>Parallel-view separation only. Invalid or touching bounds always survive.</summary>
internal static class ViewSourceCull
{
    internal static double Margin(double tolerance, double magnitude) =>
        ViewSourceBounds.Margin(tolerance, magnitude);

    internal static ViewRequest ResolveView(ViewRequest view, BoundingBox scene, double tolerance)
    {
        if (view.Settings is { } effective)
        {
            if (view.Named is { } named) view = view with { Named = named with { Crop = effective.Crop } };
            if (view.Cut is null && view.Error is null)
            {
                var frame = view.Resolve(scene, effective.Tolerance);
                if (effective.FarClip != FarClipMode.None && scene.IsValid &&
                    -scene.GetCorners().Min(frame.Plane.DistanceTo) >= effective.Depth - effective.Tolerance)
                    view = view with { Cut = new CutFrame(frame.Plane, effective.Crop && view.Named is not null ? frame : null,
                        effective.Depth, false, false, Array.Empty<Guid>(), Array.Empty<Guid>(), null) };
            }
            if (view.Cut is { } cut) view = view with { Cut = cut with { SourceDepth = effective.Depth, FarClip = effective.FarClip } };
        }
        return view;
    }

    internal static IReadOnlyList<PreparedLeaf> Filter(IReadOnlyList<PreparedLeaf> sources, ViewRequest view,
        BoundingBox scene, double tolerance, CancellationToken token)
    {
        if (view.Error is not null) return sources; // Perspective requests are rejected by view resolution.
        var frame = view.Resolve(scene, tolerance);
        var crop = view.Cut?.Crop ?? (view.Named is { Crop: true } ? frame : (Rectangle3d?)null);
        var cut = view.Cut;
        var depth = cut?.SourceDepth;
        var clipsFar = cut is not null && cut.FarClip != FarClipMode.None && depth is >= 0;
        if (crop is null && !clipsFar) return sources;
        var transform = Transform.PlaneToPlane(frame.Plane, Plane.WorldXY);
        var kept = new List<PreparedLeaf>(sources.Count);
        for (int i = 0; i < sources.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var source = sources[i];
            // Capture/Prepare expands blocks into owned leaves with the full instance transform.
            // Asking for the box in this frame is tighter than rotating a world axis-aligned box.
            var world = source.Geometry.GetBoundingBox(false);
            var bounds = source.Geometry.GetBoundingBox(transform);
            double magnitude = world.IsValid ? world.GetCorners().Max(p => p.DistanceTo(Point3d.Origin)) : double.NaN;
            magnitude = Math.Max(magnitude, frame.Plane.Origin.DistanceTo(Point3d.Origin));
            if (clipsFar) magnitude = Math.Max(magnitude,
                (frame.Plane.Origin - frame.Plane.Normal * depth!.Value).DistanceTo(Point3d.Origin));
            if (!Outside(bounds, crop, clipsFar && cut!.Participates(source) ? depth : null,
                Margin(tolerance, magnitude))) kept.Add(source);
            if ((i & 127) == 0 || i + 1 == sources.Count)
                NativeDrawing.ReportStep(ViewSteps.Culling, i + 1, sources.Count);
        }
        return kept.Count == sources.Count ? sources : kept;
    }

    // Z points toward the viewer; retained depth is z >= -depth. Do not cull the near side:
    // scope exclusions and native clipping behaviour need their existing route-specific treatment.
    internal static bool Outside(BoundingBox box, Rectangle3d? crop, double? depth, double margin)
    {
        if (!box.IsValid || !double.IsFinite(margin)) return false;
        return ViewSourceBounds.Outside(new(new(box.Min.X, box.Min.Y, box.Min.Z), new(box.Max.X, box.Max.Y, box.Max.Z)),
            crop is { IsValid: true } c ? new Frame2(c.X.Min, c.Y.Min, c.X.Max, c.Y.Max) : null, depth, margin);
    }
}
