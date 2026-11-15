using System.Collections.Immutable;

namespace M2DPlus.Core.Placement;

public static class AnchorRules
{
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

    public static bool Crops(ViewSourceKind source, CropMode crop) => source is ViewSourceKind.Rectangle or ViewSourceKind.Box ||
        source == ViewSourceKind.NamedView && crop == CropMode.Auto;
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
}

public static class PlacementPlan
{
    /// <summary>One target makes a row. gaps[i] is the gap before view i; Auto uses the mean input frame width / 10.</summary>
    public static PlacementResult Create(IReadOnlyList<PlacementView> views, IReadOnlyList<Plane3> targets,
        IReadOnlyList<DistanceSetting>? gaps = null)
    {
        if (targets.Count != 0 && targets.Count != 1 && targets.Count != views.Count)
            return new([], "Give 1 target, or one per view");
        if (gaps is not null && (gaps.Count != views.Count || gaps.Any(g => g.Mode == DistanceMode.Off)))
            throw new ArgumentException("Supply one nonnegative gap or Auto per view.", nameof(gaps));
        if (views.Count == 0) return new([], null);
        var autoGap = views.Average(v => v.Frame.Width / 10);
        var placed = ImmutableArray.CreateBuilder<PlacedView>();
        var offset = 0d;
        for (var i = 0; i < views.Count; i++)
        {
            var source = views[i].DrawingFrame;
            Plane3 target;
            if (targets.Count == 0) target = source;
            else if (targets.Count == views.Count) target = targets[i];
            else
            {
                if (i > 0)
                {
                    var gap = gaps?[i] is { Mode: DistanceMode.Value } g ? g.Value : autoGap;
                    offset += views[i - 1].Frame.MaxX + gap - views[i].Frame.MinX;
                }
                target = targets[0].AtOrigin(targets[0].At(offset, 0));
            }
            placed.Add(new(i, new(source, target), new(target, views[i].Frame)));
        }
        return new(placed.ToImmutable(), null);
    }
}
