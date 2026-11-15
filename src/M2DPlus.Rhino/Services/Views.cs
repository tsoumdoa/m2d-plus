using Rhino.Geometry;

namespace M2DPlus.Rhino.Services;

public sealed record ViewRequest(string Name, Vector3d Direction, string? Error = null)
{
    public static ViewRequest Parse(object? value)
    {
        if (value is Vector3d vector)
            return vector.IsValid && vector.Unitize() ? new("Vector", vector) : new("Vector", default, "View vector must be finite and nonzero.");
        var name = value as string ?? value?.GetType().Name ?? "Null";
        var direction = name.ToUpperInvariant() switch
        {
            "TOP" => -Vector3d.ZAxis, "BOTTOM" => Vector3d.ZAxis,
            "FRONT" => Vector3d.YAxis, "BACK" => -Vector3d.YAxis,
            "RIGHT" => -Vector3d.XAxis, "LEFT" => Vector3d.XAxis,
            "SW" => new(1, 1, -1), "SE" => new(-1, 1, -1),
            "NE" => new(-1, -1, -1), "NW" => new(1, -1, -1), _ => Vector3d.Unset
        };
        if (!direction.IsValid) return new(name, default, $"View '{name}': not in this build yet. Use a direction keyword or Vector.");
        direction.Unitize(); return new(name, direction);
    }
    public Rectangle3d Resolve(BoundingBox bounds, double tolerance)
    {
        if (!bounds.IsValid) bounds = new BoundingBox(new Point3d(-1, -1, -1), new Point3d(1, 1, 1));
        var z = -Direction;
        var up = Vector3d.ZAxis - (Vector3d.ZAxis * z) * z;
        if (!up.Unitize()) { up = Vector3d.YAxis - (Vector3d.YAxis * z) * z; up.Unitize(); }
        var x = Vector3d.CrossProduct(up, z); x.Unitize();
        var distance = bounds.GetCorners().Max(p => (p - bounds.Center) * z);
        var plane = new Plane(bounds.Center + z * (distance + Math.Max(tolerance * 10, bounds.Diagonal.Length * .01)), x, up);
        var projected = bounds.GetCorners().Select(p => { plane.ClosestParameter(p, out var u, out var v); return (u, v); }).ToArray();
        var pad = Math.Max(tolerance * 10, bounds.Diagonal.Length * .01);
        return new(plane, new Interval(projected.Min(p => p.u) - pad, projected.Max(p => p.u) + pad),
            new Interval(projected.Min(p => p.v) - pad, projected.Max(p => p.v) + pad));
    }
}
