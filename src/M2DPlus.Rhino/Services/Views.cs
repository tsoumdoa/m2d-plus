using Rhino.Geometry;
using Rhino;
using Rhino.DocObjects;
using System.Globalization;

namespace M2DPlus.Rhino.Services;

public sealed record ViewRequest(string Name, Vector3d Direction, string? Error = null)
{
    public NamedFrame? Named { get; init; }
    public string Revision => $"{Name}/{Error}/{Direction.X:R},{Direction.Y:R},{Direction.Z:R}/{Named?.Revision}";
    public static ViewRequest Parse(object? value, RhinoDoc? document = null)
    {
        if (value is ViewInfo view) return FromViewport(view.Name, view.Viewport);
        if (value is ViewportInfo viewport) return FromViewport("View", viewport);
        if (value is Vector3d vector)
            return vector.IsValid && vector.Unitize() ? new("Vector", vector) : new("Vector", default, "View vector must be finite and nonzero.");
        var name = value as string ?? value?.GetType().Name ?? "Null";
        if (value is string && document is not null)
        {
            var named = document.NamedViews.FirstOrDefault(v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase));
            if (named is not null) return FromViewport(named.Name, named.Viewport);
            var clipping = document.Objects.GetObjectList(ObjectType.ClipPlane)
                .Any(o => string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase));
            if (clipping) return new(name, default, $"Clipping plane '{name}': section views are not in this build yet (M2).");
        }
        var direction = name.ToUpperInvariant() switch
        {
            "TOP" => -Vector3d.ZAxis, "BOTTOM" => Vector3d.ZAxis,
            "FRONT" => Vector3d.YAxis, "BACK" => -Vector3d.YAxis,
            "RIGHT" => -Vector3d.XAxis, "LEFT" => Vector3d.XAxis,
            "SW" => new(1, 1, -1), "SE" => new(-1, 1, -1),
            "NE" => new(-1, -1, -1), "NW" => new(1, -1, -1), _ => Vector3d.Unset
        };
        if (!direction.IsValid) return new(name, default, $"View '{name}': no supported parallel named view or direction found; section views are not in this build yet.");
        direction.Unitize(); return new(name, direction);
    }
    private static ViewRequest FromViewport(string name, ViewportInfo viewport)
    {
        if (!viewport.IsParallelProjection) return new(name, default, "Perspective comes in a later version. Set the named view to Parallel.");
        var direction = viewport.CameraDirection;
        var camera = new Plane(viewport.CameraLocation, viewport.CameraX, viewport.CameraY);
        if (!viewport.GetFrustum(out var left, out var right, out var bottom, out var top, out _, out _) ||
            !camera.IsValid || !direction.IsValid || !direction.Unitize() ||
            !new[] { left, right, bottom, top }.All(RhinoMath.IsValidDouble) || right <= left || top <= bottom)
            return new(name, default, $"Named view '{name}' has an invalid frame.");
        return new(name, direction) { Named = new(camera, new(left, right), new(bottom, top)) };
    }
    public Rectangle3d Resolve(BoundingBox bounds, double tolerance)
    {
        if (!bounds.IsValid) bounds = new BoundingBox(new Point3d(-1, -1, -1), new Point3d(1, 1, 1));
        var z = -Direction;
        if (Named is { } named)
        {
            var camera = named.Camera;
            var distanceToFront = bounds.GetCorners().Max(p => (p - camera.Origin) * camera.ZAxis);
            camera.Origin += camera.ZAxis * (distanceToFront + Math.Max(tolerance * 10, bounds.Diagonal.Length * .01));
            if (named.Crop) return new(camera, named.X, named.Y);
            var box = BoundingBox.Empty;
            foreach (var p in bounds.GetCorners()) { camera.ClosestParameter(p, out var u, out var v); box.Union(new Point3d(u, v, 0)); }
            var padding = Math.Max(tolerance * 10, bounds.Diagonal.Length * .01);
            return new(camera, new Interval(box.Min.X - padding, box.Max.X + padding), new Interval(box.Min.Y - padding, box.Max.Y + padding));
        }
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

/// <summary>Owned plain values captured on the host; named views never carry viewport clipping planes into compute.</summary>
public sealed record NamedFrame(Plane Camera, Interval X, Interval Y, bool Crop = true)
{
    public string Revision => string.Join(",", new[] { Camera.OriginX, Camera.OriginY, Camera.OriginZ,
        Camera.XAxis.X, Camera.XAxis.Y, Camera.XAxis.Z, Camera.YAxis.X, Camera.YAxis.Y, Camera.YAxis.Z,
        X.Min, X.Max, Y.Min, Y.Max }.Select(v => v.ToString("R", CultureInfo.InvariantCulture))) + "/" + Crop;
}
