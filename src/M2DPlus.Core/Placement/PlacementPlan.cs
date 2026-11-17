using System.Collections.Immutable;

namespace M2DPlus.Core.Placement;

public static class AnchorRules
{
    /// <summary>Use the boundary edge nearest U as drawing-right, orthonormalized against the face normal.
    /// V stays drawing-up when Rhino Flip reverses the face normal; reverse X to keep a right-handed frame.</summary>
    public static Plane3 FromSurface(Vector3 centre, Vector3 u, Vector3 v, Vector3 faceNormal,
        IReadOnlyList<Vector3>? rectangleEdges = null)
    {
        u = u.Unit(); v = v.Unit();
        var normal = faceNormal.Unit();
        var x = rectangleEdges is { Count: > 0 }
            ? rectangleEdges.Select(e => e.Unit()).OrderByDescending(e => Math.Abs(e.Dot(u))).First() : u;
        if (x.Dot(u) < 0) x = -x;
        x = (x - normal * x.Dot(normal)).Unit();
        var y = normal.Cross(x).Unit();
        if (Math.Abs(y.Dot(v)) < 1e-8) throw new ArgumentException("The surface V direction cannot establish drawing-up.");
        if (y.Dot(v) < 0) { x = -x; y = -y; }
        return new(centre, x, y);
    }
    /// <summary>sourcePlane is the original box plane for Box, scene-front camera plane for NamedView,
    /// and source's own plane otherwise. Named views retain the camera target as the placement reference.</summary>
    public static Plane3 Resolve(ViewSourceKind source, Plane3 sourcePlane, double boxTop = 0,
        Vector3? cameraTarget = null, bool flipClippingPlane = false) => source switch
    {
        ViewSourceKind.Box => sourcePlane.AtOrigin(sourcePlane.At(0, 0, boxTop)),
        ViewSourceKind.ClippingPlane => flipClippingPlane ? sourcePlane.FlipNormal() : sourcePlane,
        ViewSourceKind.NamedView => sourcePlane.AtOrigin(cameraTarget ?? throw new ArgumentException("A named view needs its camera target.")),
        ViewSourceKind.Vector or ViewSourceKind.Direction => sourcePlane.AtOrigin(sourcePlane.Project(Vector3.Zero)),
        ViewSourceKind.Rectangle or ViewSourceKind.Plane => sourcePlane,
        _ => throw new ArgumentOutOfRangeException(nameof(source))
    };

    /// <summary>Vector/direction: look along direction, world Z projected up, world Y for vertical looks.</summary>
    public static Plane3 FromDirection(Vector3 lookDirection, Vector3 sceneFrontPoint)
    {
        var z = -lookDirection.Unit();
        var up = Vector3.UnitZ - z * Vector3.UnitZ.Dot(z);
        if (up.Length < 1e-10) up = Vector3.UnitY - z * Vector3.UnitY.Dot(z);
        up = up.Unit();
        var plane = new Plane3(sceneFrontPoint, up.Cross(z), up);
        return plane.AtOrigin(plane.Project(Vector3.Zero));
    }
}

/// <summary>ProjectionPlane is supplied for named views: the camera target defines the lateral anchor,
/// while the actual flat drawing lies at the scene front. The reference is projected onto that plane.</summary>
public sealed record PlacementView(Plane3 Anchor, Frame2 Frame, Plane3? ProjectionPlane = null)
{
    public Plane3 DrawingFrame
    {
        get
        {
            if (ProjectionPlane is null) return Anchor;
            if (Anchor.Z.Dot(ProjectionPlane.Z) < 1 - 1e-8) throw new ArgumentException("Projection plane must have the anchor's normal.");
            return Anchor.AtOrigin(ProjectionPlane.Project(Anchor.Origin));
        }
    }
}
public sealed record PlacedFrame(Plane3 Plane, Frame2 Bounds)
{
    public ImmutableArray<Vector3> Corners => [Plane.At(Bounds.MinX, Bounds.MinY), Plane.At(Bounds.MaxX, Bounds.MinY),
        Plane.At(Bounds.MaxX, Bounds.MaxY), Plane.At(Bounds.MinX, Bounds.MaxY)];
}
public sealed record PlacedView(int Index, RigidPlacement Transform, PlacedFrame Frame);
public sealed record PlacementResult(ImmutableArray<PlacedView> Views, string? Error)
{
    public bool Success => Error is null;
    public string? Remark { get; init; }
}

public static class PlacementPlan
{
    /// <summary>One target makes a row. gaps[i] is the gap before view i; Auto uses the mean input frame width / 10.</summary>
    public static PlacementResult Create(IReadOnlyList<PlacementView> views, IReadOnlyList<Plane3> targets,
        IReadOnlyList<DistanceSetting>? gaps = null, IReadOnlyList<Plane3>? sources = null)
    {
        if (targets.Count != 0 && targets.Count != 1 && targets.Count != views.Count)
            return new([], "Give 1 target, or one per view");
        if (sources is { Count: > 0 } && sources.Count != 1 && sources.Count != views.Count)
            return new([], "Give 1 source, or one per view");
        if (gaps is not null && (gaps.Count != views.Count || gaps.Any(g => g.Mode == DistanceMode.Off)))
            throw new ArgumentException("Supply one nonnegative gap or Auto per view.", nameof(gaps));
        if (views.Count == 0) return new([], null);
        var drawingFrames = views.Select(v => v.DrawingFrame).ToArray();
        var anchors = views.Select((_, i) => targets.Count > 0 && sources is { Count: > 0 }
            ? sources[sources.Count == 1 ? 0 : i] : drawingFrames[i]).ToArray();
        // With an explicit Source the drawing may be tilted or offset from it. Measure the real frame in Source
        // coordinates for the row, without projecting the drawing onto Source or replacing its plane.
        var extents = views.Select((v, i) => sources is { Count: > 0 } && targets.Count > 0
            ? Frame2.FromInputBounds(anchors[i], new PlacedFrame(drawingFrames[i], v.Frame).Corners) : v.Frame).ToArray();
        var autoGap = extents.Average(f => f.Width / 10);
        var placed = ImmutableArray.CreateBuilder<PlacedView>();
        var offset = 0d;
        for (var i = 0; i < views.Count; i++)
        {
            var source = anchors[i];
            Plane3 target;
            if (targets.Count == 0) target = source;
            else if (targets.Count == views.Count) target = targets[i];
            else
            {
                if (i > 0)
                {
                    var gap = gaps?[i] is { Mode: DistanceMode.Value } g ? g.Value : autoGap;
                    offset += extents[i - 1].MaxX + gap - extents[i].MinX;
                }
                target = targets[0].AtOrigin(targets[0].At(offset, 0));
            }
            var transform = new RigidPlacement(source, target);
            var drawing = drawingFrames[i];
            var framePlane = new Plane3(transform.Map(drawing.Origin), transform.MapVector(drawing.X), transform.MapVector(drawing.Y));
            placed.Add(new(i, transform, new(framePlane, views[i].Frame)));
        }
        return new(placed.ToImmutable(), null) { Remark = targets.Count == 0 && sources is { Count: > 0 }
            ? "Source is given without Target; the drawing stays in place." : null };
    }
}
