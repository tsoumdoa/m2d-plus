using M2DPlus.Core.Placement;
using M2DPlus.Core.Views;
using M2DPlus.Rhino.Services;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace M2DPlus.Rhino.Views;

/// <summary>
/// The scene as Auto depth sees it for one view command (owner decision 2026-11-16, UX §2.2): the bounding box of
/// every object document mode would draw (visible: not hidden, layer and parents on; never view boxes or M2D+ bakes,
/// <c>SceneCapture.IsDrawable</c>), minus the command's own view objects, in an <see cref="RTree"/>. Built once per
/// run with one pass over the object table (no meshing, no per-face work); each frame then searches the RTree with
/// the box around its swept extrusion and <see cref="ViewBoxDepth.Auto"/> decides. Host thread only.
/// </summary>
public sealed class ViewDepthScene : IDisposable
{
    private readonly RTree _tree = new();
    private readonly List<DepthItem> _items = new();

    private ViewDepthScene(double tolerance, double metresPerUnit) { Tolerance = tolerance; MetresPerUnit = metresPerUnit; }

    /// <summary>The union of every scene object's bounds (empty when there is none).</summary>
    public BoundingBox Bounds { get; private set; } = BoundingBox.Empty;
    public int Count => _items.Count;
    public double Tolerance { get; }
    public double MetresPerUnit { get; }

    public static ViewDepthScene Collect(RhinoDoc document, IReadOnlyCollection<Guid> excluded)
    {
        var scene = new ViewDepthScene(document.ModelAbsoluteTolerance, ViewBoxes.MetresPerUnit(document));
        var settings = new ObjectEnumeratorSettings { NormalObjects = true, LockedObjects = true, HiddenObjects = false,
            ReferenceObjects = true, IncludeLights = false, IncludeGrips = false, IncludePhantoms = false };
        var skip = excluded as ISet<Guid> ?? excluded.ToHashSet();
        foreach (var obj in document.Objects.GetObjectList(settings))
        {
            if (skip.Contains(obj.Id) || obj.IsInstanceDefinitionGeometry || !SceneCapture.IsDrawable(document, obj)) continue;
            var bounds = obj.Geometry.GetBoundingBox(false);
            if (!bounds.IsValid) continue;
            var cuttable = obj.Geometry is Brep or Extrusion or SubD or Mesh or Surface or InstanceReferenceGeometry;
            scene._tree.Insert(bounds, scene._items.Count);
            scene._items.Add(new(new Bounds3(new(bounds.Min.X, bounds.Min.Y, bounds.Min.Z), new(bounds.Max.X, bounds.Max.Y, bounds.Max.Z)), cuttable));
            var union = scene.Bounds; union.Union(bounds); scene.Bounds = union;
        }
        return scene;
    }

    /// <summary>The Auto depth of a view box with this frame (its plane's Z toward the viewer).</summary>
    public AutoDepth Auto(Rectangle3d frame)
    {
        var face = Front(frame);
        var candidates = new List<DepthItem>();
        if (Bounds.IsValid && ViewBoxDepth.SearchBounds(face, ToBounds(Bounds), Tolerance) is { } search)
            _tree.Search(new BoundingBox(new Point3d(search.Min.X, search.Min.Y, search.Min.Z), new Point3d(search.Max.X, search.Max.Y, search.Max.Z)),
                (_, e) => candidates.Add(_items[e.Id]));
        return ViewBoxDepth.Auto(face, candidates, Tolerance, MetresPerUnit);
    }

    internal static FrontFace Front(Rectangle3d frame)
    {
        var p = frame.Plane;
        var origin = frame.Corner(0);
        return new(new Plane3(new(origin.X, origin.Y, origin.Z), new(p.XAxis.X, p.XAxis.Y, p.XAxis.Z), new(p.YAxis.X, p.YAxis.Y, p.YAxis.Z)),
            frame.Width, frame.Height);
    }

    private static Bounds3 ToBounds(BoundingBox b) => new(new(b.Min.X, b.Min.Y, b.Min.Z), new(b.Max.X, b.Max.Y, b.Max.Z));

    public void Dispose() => _tree.Dispose();
}
