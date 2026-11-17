using Rhino.Geometry;
using Rhino;
using Rhino.DocObjects;
using System.Globalization;
using M2DPlus.Core;
using M2DPlus.Core.Sections;
using M2DPlus.Rhino.Views;

namespace M2DPlus.Rhino.Services;

public sealed record ViewRequest(string Name, Vector3d Direction, string? Error = null)
{
    public EffectiveSettings? Settings { get; init; }
    public NamedFrame? Named { get; init; }
    public CutFrame? Cut { get; init; }
    /// <summary>The source type, for settings resolution (a view box resolves as a Box).</summary>
    public ViewSourceKind Kind { get; init; } = ViewSourceKind.Direction;
    /// <summary>The referenced Rhino object (view box, frame surface/curve, clipping plane), else empty.</summary>
    public Guid ObjectId { get; init; }
    /// <summary>A remark about how the source was read, e.g. a frame surface that is not converted yet.</summary>
    public string? Note { get; init; }
    public string Revision => $"{Name}/{Error}/{Direction.X:R},{Direction.Y:R},{Direction.Z:R}/{Named?.Revision}/{Cut?.Revision}";
    /// <param name="scene">Scene bounds used to face a not-yet-converted frame surface at the model; computed from the
    /// document when needed and not given.</param>
    public static ViewRequest Parse(object? value, RhinoDoc? document = null, BoundingBox? scene = null)
    {
        if (value is Guid id && document is not null) value = (object?)document.Objects.FindId(id) ?? $"Object {id:D}";
        if (value is ClippingPlaneObject clippingObject) return FromClippingPlane(clippingObject.ClippingPlaneGeometry, clippingObject.Name, document ?? clippingObject.Document, clippingObject)
            with { ObjectId = clippingObject.Id };
        if (value is RhinoObject viewObject) return FromViewObject(viewObject, document ?? viewObject.Document, scene);
        // ClippingPlaneSurface is a Surface, but its display rectangle is not a view crop.
        if (value is ClippingPlaneSurface clippingSurface) return FromClippingPlane(clippingSurface, "Clipping plane", document, null);
        if (value is Extrusion or Brep or Surface or Curve && value is GeometryBase frameGeometry)
        {
            var tolerance = document?.ModelAbsoluteTolerance ?? .001;
            var angle = document?.ModelAngleToleranceRadians ?? RhinoMath.ToRadians(1);
            var check = ViewBoxes.Establish(frameGeometry, tolerance, angle);
            var label = value is Extrusion ? "View box" : value is Curve ? "Rectangle curve" : "Surface";
            if (!check.IsValid) return new(label, default, $"{label}: {check.Reason}");
            if (value is Extrusion) return FromBox(check.Frame!.Value, check.Depth!.Value, label);
            var faced = check.UsesSurfaceAxes ? check.Frame!.Value : Faced(check.Frame!.Value, document, scene, []);
            return FromPlane(faced.Plane, label, faced) with { Kind = ViewSourceKind.Rectangle,
                Note = check.UsesSurfaceAxes ? null : SurfaceDirectionRemark };
        }
        if (value is Plane plane) return FromPlane(plane, "Plane") with { Kind = ViewSourceKind.Plane };
        if (value is Rectangle3d rectangle) return rectangle.IsValid ? FromPlane(rectangle.Plane, "Rectangle", rectangle) with { Kind = ViewSourceKind.Rectangle } : new("Rectangle", default, "Invalid view rectangle.");
        if (value is Box box)
        {
            var cutPlane = box.Plane; cutPlane.Origin = box.Plane.PointAt(0, 0, box.Z.Max);
            return box.IsValid ? FromPlane(cutPlane, "Box", new Rectangle3d(cutPlane, box.X, box.Y), box.Z.Length) with { Kind = ViewSourceKind.Box } : new("Box", default, "Invalid view box.");
        }
        if (value is ViewInfo view) return FromViewport(view.Name, view.Viewport);
        if (value is ViewportInfo viewport) return FromViewport("View", viewport);
        if (value is Vector3d vector)
            return vector.IsValid && vector.Unitize() ? new("Vector", vector) { Kind = ViewSourceKind.Vector } : new("Vector", default, "View vector must be finite and nonzero.");
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
    /// <summary>A referenced Rhino object: an M2D+ view box (resolves exactly like a Box: cut at the front face,
    /// crop to the profile, far limit at the back), or a planar rectangle surface/curve that is not converted yet
    /// (untrimmed surfaces keep centre + U/V/normal; other rectangles face the model, with a remark).</summary>
    private static ViewRequest FromViewObject(RhinoObject obj, RhinoDoc? document, BoundingBox? scene)
    {
        var tolerance = document?.ModelAbsoluteTolerance ?? .001;
        var angle = document?.ModelAngleToleranceRadians ?? RhinoMath.ToRadians(1);
        var name = ViewBoxes.Label(obj);
        if (ViewBoxes.IsViewBox(obj))
        {
            if (obj.Geometry is not Extrusion extrusion)
                return new(name, default, $"View '{name}' is no longer a view box: Rhino turned it into a polysurface. Undo that edit, or reference a planar surface and use Convert to view boxes.") { ObjectId = obj.Id };
            var box = ViewBoxes.ReadBox(extrusion, tolerance, angle, obj.Attributes);
            return box.IsValid ? FromBox(box.Frame!.Value, box.Depth!.Value, name) with { ObjectId = obj.Id }
                : new(name, default, $"View box '{name}': {box.Reason}") { ObjectId = obj.Id };
        }
        if (obj.Geometry is Extrusion)
            return new(name, default, $"View '{name}' is an extrusion, not an M2D+ view box. Reference a planar surface or a closed rectangle and use Convert to view boxes.") { ObjectId = obj.Id };
        var check = ViewBoxes.Establish(obj.Geometry, tolerance, angle);
        if (!check.IsValid) return new(name, default, $"View '{name}' can't frame a view: {check.Reason}") { ObjectId = obj.Id };
        var frame = check.UsesSurfaceAxes ? check.Frame!.Value : Faced(check.Frame!.Value, document, scene, [obj.Id]);
        return FromPlane(frame.Plane, name, frame) with { Kind = ViewSourceKind.Rectangle, ObjectId = obj.Id,
            Note = (check.UsesSurfaceAxes ? "" : SurfaceDirectionRemark + " ") +
                $"'{name}' is drawn as a rectangle view. Right-click View > Convert to view boxes to make it an editable view box (depth, look direction, settings)." };
    }
    private const string SurfaceDirectionRemark = "Use an untrimmed rectangular surface to control the view direction with its normal.";
    private static Rectangle3d Faced(Rectangle3d frame, RhinoDoc? document, BoundingBox? scene, Guid[] excluded) =>
        ViewBoxes.Face(frame, scene ?? (document is null ? BoundingBox.Empty : ViewBoxes.SceneBounds(document, excluded)), document?.ModelAbsoluteTolerance ?? .001);
    private static ViewRequest FromBox(Rectangle3d frame, double depth, string name) =>
        FromPlane(frame.Plane, name, frame, depth) with { Kind = ViewSourceKind.Box };
    private static ViewRequest FromPlane(Plane plane, string name, Rectangle3d? crop = null, double? depth = null) =>
        plane.IsValid ? new(name, -plane.ZAxis) { Cut = new(plane, crop, depth, false, false, Array.Empty<Guid>(), Array.Empty<Guid>(), null) }
            : new(name, default, "Invalid cut plane.");
    private static ViewRequest FromClippingPlane(ClippingPlaneSurface surface, string name, RhinoDoc? document, ClippingPlaneObject? obj)
    {
        surface.GetClipParticipation(out var objects, out var layers, out var exclude);
        if (surface.ParticipationListsEnabled && document is null) return new(name, default, "Scoped clipping plane requires its source document.");
        var layerIds = new List<Guid>();
        if (surface.ParticipationListsEnabled)
            foreach (var index in layers)
            {
                if (index < 0 || index >= document!.Layers.Count || document.Layers[index] is not { IsDeleted: false } layer)
                    return new(name, default, "Clipping-plane layer scope does not resolve in the source document.");
                layerIds.Add(layer.Id);
            }
        var section = obj is not null && document is not null ? SectionCapture.Capture(document, obj, null).Style : null;
        return FromPlane(surface.Plane, name) with { Kind = ViewSourceKind.ClippingPlane, Cut = new(surface.Plane, null,
            surface.PlaneDepthEnabled ? surface.PlaneDepth : null, surface.ParticipationListsEnabled, exclude,
            objects.ToArray(), layerIds.ToArray(), section) };
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
        return new(name, direction) { Kind = ViewSourceKind.NamedView, Named = new(camera, new(left, right), new(bottom, top)) { Target = viewport.TargetPoint.IsValid ? viewport.TargetPoint : camera.Origin } };
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
    public Point3d? Target { get; init; }
    public string Revision => Target?.ToString() + "/" + string.Join(",", new[] { Camera.OriginX, Camera.OriginY, Camera.OriginZ,
        Camera.XAxis.X, Camera.XAxis.Y, Camera.XAxis.Z, Camera.YAxis.X, Camera.YAxis.Y, Camera.YAxis.Z,
        X.Min, X.Max, Y.Min, Y.Max }.Select(v => v.ToString("R", CultureInfo.InvariantCulture))) + "/" + Crop;
}

/// <summary>Selected source only; no document tables or viewport clipping state reaches compute.</summary>
public sealed record CutFrame(Plane Plane, Rectangle3d? Crop, double? SourceDepth,
    bool ScopeEnabled, bool Exclusion, IReadOnlyList<Guid> Objects, IReadOnlyList<Guid> Layers,
    SectionStyleSnapshot? SectionerStyle)
{
    // Legacy test seam; resolved M3 settings override this value.
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
