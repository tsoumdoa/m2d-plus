using Rhino.Geometry;
using Rhino;
using Rhino.DocObjects;
using System.Globalization;
using M2DPlus.Core;
using M2DPlus.Core.Sections;

namespace M2DPlus.Rhino.Services;

public sealed record ViewRequest(string Name, Vector3d Direction, string? Error = null)
{
    public NamedFrame? Named { get; init; }
    public CutFrame? Cut { get; init; }
    public string Revision => $"{Name}/{Error}/{Direction.X:R},{Direction.Y:R},{Direction.Z:R}/{Named?.Revision}/{Cut?.Revision}";
    public static ViewRequest Parse(object? value, RhinoDoc? document = null)
    {
        if (value is Guid id && document is not null) value = document.Objects.FindId(id);
        if (value is ClippingPlaneObject clippingObject) return FromClippingPlane(clippingObject.ClippingPlaneGeometry, clippingObject.Name, document ?? clippingObject.Document, clippingObject);
        if (value is ClippingPlaneSurface clippingSurface) return FromClippingPlane(clippingSurface, "Clipping plane", document, null);
        if (value is Plane plane) return FromPlane(plane, "Plane");
        if (value is Rectangle3d rectangle) return rectangle.IsValid ? FromPlane(rectangle.Plane, "Rectangle", rectangle) : new("Rectangle", default, "Invalid view rectangle.");
        if (value is Box box)
        {
            var cutPlane = box.Plane; cutPlane.Origin = box.Plane.PointAt(0, 0, box.Z.Max);
            return box.IsValid ? FromPlane(cutPlane, "Box", new Rectangle3d(cutPlane, box.X, box.Y), box.Z.Length) : new("Box", default, "Invalid view box.");
        }
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
                .OfType<ClippingPlaneObject>().FirstOrDefault(o => string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase));
            if (clipping is not null) return FromClippingPlane(clipping.ClippingPlaneGeometry, name, document, clipping);
        }
        var direction = name.ToUpperInvariant() switch
        {
            "TOP" => -Vector3d.ZAxis, "BOTTOM" => Vector3d.ZAxis,
            "FRONT" => Vector3d.YAxis, "BACK" => -Vector3d.YAxis,
            "RIGHT" => -Vector3d.XAxis, "LEFT" => Vector3d.XAxis,
            "SW" => new(1, 1, -1), "SE" => new(-1, 1, -1),
            "NE" => new(-1, -1, -1), "NW" => new(1, -1, -1), _ => Vector3d.Unset
        };
        if (!direction.IsValid) return new(name, default, $"View '{name}': no supported parallel named view or direction found.");
        direction.Unitize(); return new(name, direction);
    }
    private static ViewRequest FromPlane(Plane plane, string name, Rectangle3d? crop = null, double? depth = null) =>
        plane.IsValid ? new(name, -plane.ZAxis) { Cut = new(plane, crop, depth, false, false, Array.Empty<Guid>(), Array.Empty<Guid>(), null) }
            : new(name, default, "Invalid cut plane.");
    private static ViewRequest FromClippingPlane(ClippingPlaneSurface surface, string name, RhinoDoc? document, ClippingPlaneObject? obj)
    {
        surface.GetClipParticipation(out var objects, out var layers, out var exclude);
        if (surface.ParticipationListsEnabled && document is null) return new(name, default, "Scoped clipping plane requires its source document.");
        var layerIds = layers.Select(i => document!.Layers[i].Id).ToArray();
        var section = obj is not null && document is not null ? SectionCapture.Capture(document, obj, null).Style : null;
        return FromPlane(surface.Plane, name) with { Cut = new(surface.Plane, null,
            surface.PlaneDepthEnabled ? surface.PlaneDepth : null, surface.ParticipationListsEnabled, exclude,
            objects.ToArray(), layerIds, section) };
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
        if (Cut is { } cut)
        {
            if (cut.Crop is { } crop) return crop;
            var local = bounds.GetCorners().Select(p => { cut.Plane.ClosestParameter(p, out var u, out var v); return (u, v); }).ToArray();
            var padding = Math.Max(tolerance * 10, bounds.Diagonal.Length * .01);
            return new(cut.Plane, new Interval(local.Min(p => p.u) - padding, local.Max(p => p.u) + padding),
                new Interval(local.Min(p => p.v) - padding, local.Max(p => p.v) + padding));
        }
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

/// <summary>Selected source only; no document tables or viewport clipping state reaches compute.</summary>
public sealed record CutFrame(Plane Plane, Rectangle3d? Crop, double? SourceDepth,
    bool ScopeEnabled, bool Exclusion, IReadOnlyList<Guid> Objects, IReadOnlyList<Guid> Layers,
    SectionStyleSnapshot? SectionerStyle)
{
    // Internal test seam. Product always uses NoLine until M3 settings wiring.
    internal FarClipMode FarClip { get; init; } = FarClipMode.NoLine;
    public string Revision => System.Text.Json.JsonSerializer.Serialize(new {
        plane = new NamedFrame(Plane, new(0, 1), new(0, 1)).Revision,
        crop = Crop is { } c ? new NamedFrame(c.Plane, c.X, c.Y).Revision : null,
        SourceDepth, ScopeEnabled, Exclusion, Objects, Layers, SectionerStyle, FarClip });
    internal bool Participates(PreparedLeaf source) => !ScopeEnabled ||
        (source.Section.ObjectIds.Any(Objects.Contains) || source.Section.LayerIds.Any(Layers.Contains)) != Exclusion;
    internal SectionStyleSnapshot Style(PreparedLeaf source) => source.Section.FromSectioner && SectionerStyle is { HasStyle: true } style
        ? style with { ObjectColour = source.Section.Style.ObjectColour, ObjectPrintColour = source.Section.Style.ObjectPrintColour }
        : source.Section.Style;
}
