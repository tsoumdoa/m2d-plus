using Rhino.Geometry;

namespace M2DPlus.Rhino.Services;

/// <summary>Owns per-view XY reductions through native extraction. Prepared/definition meshes stay borrowed.</summary>
internal sealed class MeshViewCrop : IDisposable
{
    internal const int MinimumFaces = 10_000;
    private readonly List<Mesh> _owned = new();
    internal IReadOnlyList<PreparedLeaf> Sources { get; }
    internal long DroppedFaces { get; private set; }
    internal int ReducedMeshes => _owned.Count;

    internal MeshViewCrop(IReadOnlyList<PreparedLeaf> sources, ViewRequest view, BoundingBox scene,
        double tolerance, CancellationToken token, bool enabled = true)
    {
        Sources = sources;
        if (!enabled || view.Error is not null || view.Settings?.CutOnly == true) return;
        var frame = view.Resolve(scene, tolerance);
        var crop = view.Cut?.Crop ?? (view.Named is { Crop: true } ? frame : (Rectangle3d?)null);
        if (crop is not { IsValid: true } rectangle || !frame.IsValid) return;
        var transform = Transform.PlaneToPlane(frame.Plane, Plane.WorldXY);
        var cut = view.Cut;
        // Match SectionDrawing's effective far plane. Never reduce a body requiring its topology,
        // cap/provenance certificate or Cut/Hatch, even if the reduced box would avoid the cut.
        Plane? far = null;
        if (cut is not null)
        {
            var autoDepth = scene.IsValid ? Math.Max(0, -scene.GetCorners().Min(cut.Plane.DistanceTo)) + tolerance * 10 : tolerance * 10;
            var range = new M2DPlus.Core.ViewRange(cut.SourceDepth ?? autoDepth, farClip: cut.FarClip, tolerance: tolerance);
            if (range.ClipsFar)
                far = new Plane(cut.Plane.Origin - cut.Plane.Normal * range.Depth, -cut.Plane.XAxis, cut.Plane.YAxis);
        }
        var result = new List<PreparedLeaf>(sources.Count);
        try
        {
            foreach (var source in sources)
            {
                token.ThrowIfCancellationRequested();
                // Scoped nonparticipants keep original bounds too: these gate foreground region withholding.
                if (source.Geometry is not Mesh mesh || mesh.Faces.Count < MinimumFaces || mesh.Ngons.Count != 0 ||
                    source.Section.Transparent || (cut is not null && (!cut.Participates(source) ||
                    SectionProjectionBounds.Classify(mesh, cut.Plane, far, tolerance) != SectionProjectionRelation.Projection)))
                { result.Add(source); continue; }
                Mesh? reduced;
                // A failed reduction is local: draw that mesh whole, as before.
                try { reduced = Reduce(mesh, transform, rectangle, tolerance, token); }
                catch (Exception ex) when (ex is not OperationCanceledException) { reduced = null; }
                if (reduced is null) { result.Add(source); continue; }
                _owned.Add(reduced);
                DroppedFaces += mesh.Faces.Count - reduced.Faces.Count;
                // Only projection uses these leaves; no canonical shape may describe the cropped geometry.
                result.Add(source with { Geometry = reduced, Canonical = null, Placement = null, DefinitionKey = null });
            }
            if (_owned.Count != 0) Sources = result;
        }
        catch { Dispose(); throw; }
    }

    private static Mesh? Reduce(Mesh mesh, Transform transform, Rectangle3d crop, double tolerance, CancellationToken token)
    {
        var bounds = mesh.GetBoundingBox(transform);
        if (!bounds.IsValid || (bounds.Min.X >= crop.X.Min && bounds.Max.X <= crop.X.Max &&
            bounds.Min.Y >= crop.Y.Min && bounds.Max.Y <= crop.Y.Max)) return null;
        if (!mesh.IsValid) return null;
        var vertices = new Point3d[mesh.Vertices.Count];
        double magnitude = Math.Max(1, crop.Plane.Origin.DistanceTo(Point3d.Origin));
        for (int i = 0; i < vertices.Length; i++)
        {
            if ((i & 1023) == 0) token.ThrowIfCancellationRequested();
            var point = mesh.Vertices.Point3dAt(i);
            if (!point.IsValid) return null;
            magnitude = Math.Max(magnitude, point.DistanceTo(Point3d.Origin));
            point.Transform(transform);
            if (!point.IsValid) return null;
            vertices[i] = point;
        }
        // The new boundary belongs beyond this band, not on the output trimming tolerance.
        double margin = 32 * ViewSourceCull.Margin(tolerance, magnitude);
        if (!double.IsFinite(margin) || margin <= 0) return null;
        var keep = new bool[mesh.Faces.Count];
        var ringVertices = new HashSet<Point3d>();
        int count = 0;
        for (int i = 0; i < keep.Length; i++)
        {
            if ((i & 511) == 0) token.ThrowIfCancellationRequested();
            var face = mesh.Faces[i];
            var a = vertices[face.A]; var b = vertices[face.B]; var c = vertices[face.C]; var d = vertices[face.D];
            if (Math.Max(Math.Max(a.X, b.X), Math.Max(c.X, d.X)) < crop.X.Min - margin ||
                Math.Min(Math.Min(a.X, b.X), Math.Min(c.X, d.X)) > crop.X.Max + margin ||
                Math.Max(Math.Max(a.Y, b.Y), Math.Max(c.Y, d.Y)) < crop.Y.Min - margin ||
                Math.Min(Math.Min(a.Y, b.Y), Math.Min(c.Y, d.Y)) > crop.Y.Max + margin) continue;
            keep[i] = true; count++;
            ringVertices.Add(a); ringVertices.Add(b); ringVertices.Add(c); ringVertices.Add(d);
        }
        if (count == 0 || count >= keep.Length * .8) return null;
        // One vertex-neighbour ring, including unwelded coincident vertices. Use the seed set only:
        // growing it here would flood the whole connected component. XY alone never trims depth.
        for (int i = 0; i < keep.Length; i++)
        {
            if ((i & 511) == 0) token.ThrowIfCancellationRequested();
            if (keep[i]) continue;
            var f = mesh.Faces[i];
            if (ringVertices.Contains(vertices[f.A]) || ringVertices.Contains(vertices[f.B]) ||
                ringVertices.Contains(vertices[f.C]) || ringVertices.Contains(vertices[f.D]))
            { keep[i] = true; count++; }
        }
        if (count >= keep.Length * .8) return null;
        var copy = mesh.DuplicateMesh();
        try
        {
            token.ThrowIfCancellationRequested();
            copy.Faces.DeleteFaces(Enumerable.Range(0, keep.Length).Where(i => !keep[i]), true);
            if (!copy.IsValid) { copy.Dispose(); return null; }
            return copy;
        }
        catch { copy.Dispose(); throw; }
    }

    public void Dispose() { foreach (var mesh in _owned) mesh.Dispose(); _owned.Clear(); }
}
