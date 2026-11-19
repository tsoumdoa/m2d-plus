using M2DPlus.Core.Placement;

namespace M2DPlus.Core.Views;

/// <summary>A parallel retained volume in world coordinates. Z points toward the viewer; cut participants have a front limit at zero.</summary>
public sealed record ViewSourceVolume(Plane3 Plane, Frame2? Crop, double? Depth)
{
    public bool IsUnbounded => Crop is null && Depth is not >= 0;

    /// <summary>Project all corners of a conservative world box; only certain separation rejects it.</summary>
    public bool Outside(Bounds3 world, double tolerance, bool participates = true)
    {
        if (world.Min.X > world.Max.X || world.Min.Y > world.Max.Y || world.Min.Z > world.Max.Z) return false;
        try
        {
            // Same eight points/arithmetic as the enumerable form, without per-source arrays/LINQ.
            var first = new Vector3(world.Min.X, world.Min.Y, world.Min.Z);
            var min = Plane.Coordinates(first); var max = min;
            var magnitude = Math.Max(Plane.Origin.Length, first.Length);
            for (var i = 1; i < 8; i++)
            {
                var corner = new Vector3((i & 4) == 0 ? world.Min.X : world.Max.X,
                    (i & 2) == 0 ? world.Min.Y : world.Max.Y, (i & 1) == 0 ? world.Min.Z : world.Max.Z);
                var point = Plane.Coordinates(corner);
                min = new(Math.Min(min.X, point.X), Math.Min(min.Y, point.Y), Math.Min(min.Z, point.Z));
                max = new(Math.Max(max.X, point.X), Math.Max(max.Y, point.Y), Math.Max(max.Z, point.Z));
                magnitude = Math.Max(magnitude, corner.Length);
            }
            var local = new Bounds3(min, max);
            if (Depth is >= 0) magnitude = Math.Max(magnitude, Plane.At(0, 0, -Depth.Value).Length);
            var margin = ViewSourceBounds.Margin(tolerance, magnitude);
            if (!double.IsFinite(margin) || margin < 0) return false;
            return participates && local.Min.Z > margin || ViewSourceBounds.Outside(local, Crop, participates ? Depth : null, margin);
        }
        catch (ArgumentException) { return false; } // Overflow/invalid arithmetic never excludes geometry.
    }

    /// <summary>An empty request or any unbounded view keeps the whole scene.</summary>
    public static bool OutsideAll(Bounds3 world, IReadOnlyList<ViewSourceVolume> volumes, double tolerance) =>
        volumes.Count > 0 && volumes.All(v => !v.IsUnbounded && v.Outside(world, tolerance));
}
