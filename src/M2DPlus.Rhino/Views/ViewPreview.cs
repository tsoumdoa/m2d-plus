using M2DPlus.Rhino.Services;
using Rhino.Geometry;

namespace M2DPlus.Rhino.Views;

/// <summary>What the component previews for one view (UX rev. 4 §2.2): the frame, a look-direction arrow from the
/// frame's centre, and the depth volume (back rectangle plus four depth edges). Plain values; the GH layer draws
/// these exact lines in DrawViewportWires.</summary>
public sealed record ViewPreview(string Name, Rectangle3d Frame, double Depth)
{
    public Vector3d Look => -Frame.Plane.ZAxis;
    public Point3d[] FrontCorners => Corners(0);
    public Point3d[] BackCorners => Corners(Depth);
    public Line[] FrontEdges => Edges(FrontCorners);
    public Line[] BackEdges => Edges(BackCorners);
    public Line[] DepthEdges => FrontCorners.Zip(BackCorners, (a, b) => new Line(a, b)).ToArray();

    /// <summary>From the frame's centre along the look direction: a third of the shorter side, never past the back.</summary>
    public Line Arrow
    {
        get
        {
            var centre = Frame.Center;
            var length = Math.Min(Math.Min(Frame.Width, Frame.Height) / 3, Depth > 0 ? Depth : double.MaxValue);
            return new Line(centre, centre + Look * length);
        }
    }

    public BoundingBox Bounds
    {
        get { var box = new BoundingBox(FrontCorners); box.Union(new BoundingBox(BackCorners)); return box; }
    }

    private Point3d[] Corners(double depth) =>
        [.. new[] { 0, 1, 2, 3 }.Select(i => Frame.Corner(i) + Look * depth)];

    private static Line[] Edges(Point3d[] c) => [new(c[0], c[1]), new(c[1], c[2]), new(c[2], c[3]), new(c[3], c[0])];

    /// <summary>The preview of a resolved view. A box or cropped view needs no scene; other views use the scene bounds
    /// for their frame and auto depth, as the engine does. Null when there is nothing meaningful to show yet.</summary>
    public static ViewPreview? For(ViewRequest view, BoundingBox scene, double tolerance)
    {
        if (view.Error is not null) return null;
        if (view.Cut is { } cut)
        {
            if (cut.Crop is null && !scene.IsValid) return null;
            var frame = view.Resolve(scene, tolerance);
            var depth = cut.SourceDepth ?? (scene.IsValid ? Math.Max(0, -scene.GetCorners().Min(cut.Plane.DistanceTo)) + tolerance * 10 : 0);
            return new(view.Name, frame, depth);
        }
        if (!scene.IsValid) return null;
        var front = view.Resolve(scene, tolerance);
        var extent = scene.GetCorners().Max(p => -front.Plane.DistanceTo(p));
        return new(view.Name, front, Math.Max(0, extent));
    }
}
