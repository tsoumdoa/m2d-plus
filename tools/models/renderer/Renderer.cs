// MIT. Local reference renderer, deliberately separate from M2D+ drawing code.
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json;
using Rhino;
using Rhino.DocObjects;
using Rhino.FileIO;
using Rhino.Geometry;
using Point = Rhino.Geometry.Point;
using Font = System.Drawing.Font;

namespace M2DPlus.ModelRenderer;

public static class Renderer
{
    private sealed class Asset : IDisposable
    {
        public GeometryBase Shape = null!;
        public readonly List<Mesh> Meshes = [];
        public readonly List<Point3d[]> Lines = [];
        public readonly List<(Point3d Point, string Label)> Labels = [];
        public void Dispose()
        {
            Shape.Dispose();
            foreach (var mesh in Meshes) mesh.Dispose();
        }
    }
    private sealed record Instance(Asset Asset, Transform Transform, Color Color);
    private readonly record struct V(double X, double Y, double Z);
    private sealed record Frame(Point3d[] Corners, Point3d Origin, string Name);

    public static string Render(string modelPath, string sidecarPath, string outputDirectory)
    {
        using var model = File3dm.Read(modelPath) ?? throw new IOException("Cannot read model");
        using var meta = JsonDocument.Parse(File.ReadAllText(sidecarPath));
        var root = meta.RootElement;
        var id = root.GetProperty("id").GetString()!;
        bool performance = root.GetProperty("tier").ValueKind != JsonValueKind.Null;
        var objects = model.Objects.ToDictionary(o => o.Id);
        var definitions = model.AllInstanceDefinitions.ToDictionary(d => d.Id);
        var layers = model.AllLayers.ToDictionary(l => l.Index);
        var materials = model.AllMaterials.ToDictionary(m => m.Index);
        var assets = new List<Asset>();
        var localCache = new Dictionary<GeometryBase, Asset>(ReferenceEqualityComparer.Instance);
        var equivalent = new Dictionary<string, List<Asset>>();
        var instances = new List<Instance>();
        var bounds = BoundingBox.Empty;
        double tolerance = model.Settings.ModelAbsoluteTolerance;
        var meshing = MeshingParameters.FastRenderMesh;
        meshing.MinimumEdgeLength = Math.Max(tolerance, .001 * RhinoMath.UnitScale(UnitSystem.Meters, model.Settings.ModelUnitSystem));
        meshing.JaggedSeams = false;

        Color Resolve(ObjectAttributes a, Color parent)
        {
            return a.ColorSource switch
            {
                ObjectColorSource.ColorFromObject => a.ObjectColor,
                ObjectColorSource.ColorFromParent => parent,
                ObjectColorSource.ColorFromMaterial when materials.TryGetValue(a.MaterialIndex, out var mat) => mat.DiffuseColor,
                _ => layers.TryGetValue(a.LayerIndex, out var layer) ? layer.Color : Color.Gray
            };
        }

        Asset Build(GeometryBase shape)
        {
            var asset = new Asset { Shape = shape };
            Brep? brep = shape as Brep;
            bool ownsBrep = false;
            if (shape is Extrusion extrusion) { brep = extrusion.ToBrep(); ownsBrep = true; }
            else if (shape is SubD subd) { brep = subd.ToBrep(); ownsBrep = true; }
            else if (shape is Surface surface) { brep = surface.ToBrep(); ownsBrep = true; }
            if (brep is not null)
            {
                asset.Meshes.AddRange(Mesh.CreateFromBrep(brep, meshing) ?? []);
                if (!performance)
                    foreach (var edge in brep.Edges)
                        AddCurve(asset, edge, tolerance);
            }
            else if (shape is Mesh mesh) asset.Meshes.Add(mesh.DuplicateMesh());
            else if (shape is Curve curve) AddCurve(asset, curve, tolerance);
            else if (shape is Point point) asset.Labels.Add((point.Location, "•"));
            else if (shape is TextDot dot) asset.Labels.Add((dot.Point, dot.Text));
            else if (shape is Leader leader)
            {
                asset.Lines.Add(leader.Points3D);
                asset.Labels.Add((leader.Plane.Origin, leader.PlainText));
            }
            else if (shape is LinearDimension dimension)
            {
                var a = dimension.Plane.PointAt(dimension.ExtensionLine1End.X, dimension.ExtensionLine1End.Y);
                var b = dimension.Plane.PointAt(dimension.ExtensionLine2End.X, dimension.ExtensionLine2End.Y);
                asset.Lines.Add([a, b]); asset.Labels.Add((dimension.Plane.Origin, "dimension"));
            }
            else if (shape is AnnotationBase annotation) asset.Labels.Add((annotation.Plane.Origin, annotation.PlainText));
            else if (shape is Hatch hatch)
                foreach (var c in (hatch.Get3dCurves(true) ?? []).Concat(hatch.Get3dCurves(false) ?? []))
                { AddCurve(asset, c, tolerance); c.Dispose(); }
            if (ownsBrep) brep?.Dispose();
            assets.Add(asset);
            return asset;
        }

        (Asset, Transform) Prepare(GeometryBase geometry)
        {
            if (localCache.TryGetValue(geometry, out var cached)) return (cached, Transform.Identity);
            // For exploded tiers, verify actual normalized Brep geometry before
            // reusing a mesh. Bounding dimensions only choose candidates, never identity.
            if (performance && geometry is Brep original)
            {
                var box = original.GetBoundingBox(true);
                var delta = box.Diagonal;
                var key = $"{original.Faces.Count}/{delta.X:F6}/{delta.Y:F6}/{delta.Z:F6}";
                var normalized = original.DuplicateBrep();
                normalized.Transform(Transform.Translation(-box.Min.X, -box.Min.Y, -box.Min.Z));
                if (!equivalent.TryGetValue(key, out var bucket)) equivalent[key] = bucket = [];
                foreach (var candidate in bucket)
                    if (((Brep)candidate.Shape).IsDuplicate(normalized, tolerance * .01))
                    { normalized.Dispose(); return (candidate, Transform.Translation((Vector3d)box.Min)); }
                var asset = Build(normalized); bucket.Add(asset);
                return (asset, Transform.Translation((Vector3d)box.Min));
            }
            var result = Build(geometry.Duplicate()); localCache[geometry] = result;
            return (result, Transform.Identity);
        }

        void Walk(File3dmObject obj, Transform transform, Color parent, HashSet<Guid> visited)
        {
            if (obj.Attributes.GetUserString("m2d.view") == "1") return;
            var color = Resolve(obj.Attributes, parent);
            if (obj.Geometry is InstanceReferenceGeometry reference)
            {
                if (!visited.Add(reference.ParentIdefId)) throw new InvalidDataException("Definition cycle");
                foreach (var member in definitions[reference.ParentIdefId].GetObjectIds())
                    Walk(objects[member], transform * reference.Xform, color, visited);
                visited.Remove(reference.ParentIdefId);
            }
            else if (obj.Geometry is not ClippingPlaneSurface)
            {
                var box = obj.Geometry.GetBoundingBox(true); box.Transform(transform); bounds.Union(box);
                var (asset, shift) = Prepare(obj.Geometry);
                instances.Add(new Instance(asset, transform * shift, color));
            }
        }
        try
        {
            foreach (var obj in model.Objects)
                if (!obj.Attributes.IsInstanceDefinitionObject)
                    Walk(obj, Transform.Identity, Color.Gray, []);
            var frames = ReadFrames(root);
            if (id == "view-sources")
                foreach (var frame in frames)
                    foreach (var point in frame.Corners) bounds.Union(point);
            string[] views = performance ? ["SW", "Front", "Top"] : ["SW", "Front"];
            var outputs = new List<string>();
            Directory.CreateDirectory(outputDirectory);
            foreach (var view in views)
            {
                string path = Path.Combine(outputDirectory, id + "-" + view + ".png");
                Draw(instances, frames, bounds, view, id, root.GetProperty("stats").GetProperty("objects").GetInt32(), path);
                outputs.Add(path);
            }
            return JsonSerializer.Serialize(new { status = "ok", method = "offscreen CPU mesh renderer (fallback)", model = id,
                paths = outputs, uniqueMeshes = assets.Sum(a => a.Meshes.Count), sceneLeaves = instances.Count,
                notes = new[] { "1200x800, parallel projection, light background, depth-tested layer/object-colour shading",
                      frames.Count == 0 ? "View-source overlays omitted for shaded geometry references" : "Green dashed overlays show view-source frames; overlays do not clip the model",
                    "Annotations use simplified screen labels; materials are shaded opaque", "Mesh reuse requires normalized Brep.IsDuplicate, never bounding boxes alone" } });
        }
        finally { foreach (var asset in assets) asset.Dispose(); }
    }

    private static void AddCurve(Asset asset, Curve curve, double tolerance)
    {
        using var polyline = curve.ToPolyline(Math.Max(tolerance, curve.GetLength() / 2000), .08, 0, 0);
        if (polyline is not null && polyline.TryGetPolyline(out var poly)) asset.Lines.Add(poly.ToArray());
    }

    private static List<Frame> ReadFrames(JsonElement root)
    {
        var result = new List<Frame>();
        // Drawing-set frames overwhelm a shaded geometry reference; individual renders show their own frame.
        if (root.TryGetProperty("family", out var family) && family.GetString() == "view-cases") return result;
        foreach (var view in root.GetProperty("views").EnumerateArray())
        {
            if (!view.TryGetProperty("plane", out var p)) continue;
            var kind = view.GetProperty("source").GetProperty("kind").GetString();
            if (kind is not ("named view" or "clipping plane name" or "rectangle" or "box")) continue;
            double[] Vector(JsonElement e) => e.EnumerateArray().Select(x => x.GetDouble()).ToArray();
            var o = Vector(p.GetProperty("origin")); var x = Vector(p.GetProperty("x")); var y = Vector(p.GetProperty("y"));
            var plane = new Plane(new Point3d(o[0], o[1], o[2]), new Vector3d(x[0], x[1], x[2]), new Vector3d(y[0], y[1], y[2]));
            double[] frame = [-2, 2, -2, 2];
            if (view.TryGetProperty("crop", out var crop)) frame = Vector(crop);
            else if (view.TryGetProperty("frame", out var extent)) frame = Vector(extent);
            result.Add(new Frame([plane.PointAt(frame[0], frame[2]), plane.PointAt(frame[1], frame[2]),
                plane.PointAt(frame[1], frame[3]), plane.PointAt(frame[0], frame[3]), plane.PointAt(frame[0], frame[2])], plane.Origin,
                view.GetProperty("name").GetString()!));
        }
        return result;
    }

    private static void Draw(List<Instance> instances, List<Frame> frames, BoundingBox bounds, string view, string id, int count, string path)
    {
        const int width = 1200, height = 800;
        var right = view == "SW" ? new Vector3d(1, -1, 0) : Vector3d.XAxis;
        right.Unitize();
        var toward = view switch { "SW" => new Vector3d(-1, -1, 1), "Front" => new Vector3d(0, -1, 0), _ => Vector3d.ZAxis };
        toward.Unitize(); var up = Vector3d.CrossProduct(toward, right); up.Unitize();
        V Camera(Point3d point) { var v = point - bounds.Center; return new V(v * right, v * up, v * toward); }
        var corners = bounds.GetCorners().Select(Camera).ToArray();
        double minX = corners.Min(p => p.X), maxX = corners.Max(p => p.X), minY = corners.Min(p => p.Y), maxY = corners.Max(p => p.Y);
        double scale = Math.Min((width - 100) / Math.Max(maxX - minX, 1e-6), (height - 150) / Math.Max(maxY - minY, 1e-6));
        V Project(Point3d point) { var v = Camera(point); return new V(width / 2.0 + (v.X - (minX + maxX) / 2) * scale, height / 2.0 - (v.Y - (minY + maxY) / 2) * scale, v.Z); }
        var pixels = Enumerable.Repeat(Color.FromArgb(247, 249, 252).ToArgb(), width * height).ToArray();
        var depth = Enumerable.Repeat(double.NegativeInfinity, width * height).ToArray();
        void Triangle(V a, V b, V c, Color color)
        {
            double area = (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
            if (Math.Abs(area) < .01) return;
            int x0 = Math.Max(0, (int)Math.Floor(Math.Min(a.X, Math.Min(b.X, c.X))));
            int x1 = Math.Min(width - 1, (int)Math.Ceiling(Math.Max(a.X, Math.Max(b.X, c.X))));
            int y0 = Math.Max(0, (int)Math.Floor(Math.Min(a.Y, Math.Min(b.Y, c.Y))));
            int y1 = Math.Min(height - 1, (int)Math.Ceiling(Math.Max(a.Y, Math.Max(b.Y, c.Y))));
            var ab = new Vector3d((b.X - a.X) / scale, -(b.Y - a.Y) / scale, b.Z - a.Z);
            var ac = new Vector3d((c.X - a.X) / scale, -(c.Y - a.Y) / scale, c.Z - a.Z);
            var normal = Vector3d.CrossProduct(ab, ac); normal.Unitize(); if (normal.Z < 0) normal = -normal;
            double shade = .4 + .6 * Math.Max(0, normal * new Vector3d(-.35, .45, .82));
            int ink = Color.FromArgb((int)Math.Min(255, color.R * shade), (int)Math.Min(255, color.G * shade), (int)Math.Min(255, color.B * shade)).ToArgb();
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    double u = ((b.X - x - .5) * (c.Y - y - .5) - (b.Y - y - .5) * (c.X - x - .5)) / area;
                    double v = ((c.X - x - .5) * (a.Y - y - .5) - (c.Y - y - .5) * (a.X - x - .5)) / area;
                    double w = 1 - u - v;
                    if (u < -1e-8 || v < -1e-8 || w < -1e-8) continue;
                    double z = u * a.Z + v * b.Z + w * c.Z; int index = y * width + x;
                    if (z > depth[index] + bounds.Diagonal.Length * 1e-10) { depth[index] = z; pixels[index] = ink; }
                }
        }
        void Line(V a, V b, int color, bool overlay)
        {
            int n = (int)Math.Ceiling(Math.Max(Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y)));
            n = Math.Min(n, 10000);
            for (int i = 0; i <= n; i++)
            {
                if (overlay && i % 10 >= 6) continue;
                double t = n == 0 ? 0 : (double)i / n;
                int x = (int)Math.Round(a.X + t * (b.X - a.X)), y = (int)Math.Round(a.Y + t * (b.Y - a.Y));
                if (x < 0 || x >= width || y < 0 || y >= height) continue;
                int index = y * width + x; double z = a.Z + t * (b.Z - a.Z);
                if (overlay || z >= depth[index] - bounds.Diagonal.Length * 1e-5) pixels[index] = color;
            }
        }
        foreach (var instance in instances)
            foreach (var mesh in instance.Asset.Meshes)
            {
                var vertices = mesh.Vertices.Select(v => { var p = new Point3d(v); p.Transform(instance.Transform); return Project(p); }).ToArray();
                foreach (var face in mesh.Faces)
                {
                    Triangle(vertices[face.A], vertices[face.B], vertices[face.C], instance.Color);
                    if (face.IsQuad) Triangle(vertices[face.A], vertices[face.C], vertices[face.D], instance.Color);
                }
            }
        foreach (var instance in instances)
            foreach (var line in instance.Asset.Lines)
                for (int i = 1; i < line.Length; i++)
                {
                    var a = line[i - 1]; var b = line[i]; a.Transform(instance.Transform); b.Transform(instance.Transform);
                    Line(Project(a), Project(b), Color.FromArgb(55, 65, 78).ToArgb(), false);
                }
        foreach (var frame in frames)
            for (int i = 1; i < frame.Corners.Length; i++) Line(Project(frame.Corners[i - 1]), Project(frame.Corners[i]), Color.FromArgb(25, 145, 120).ToArgb(), true);
        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try { Marshal.Copy(pixels, 0, data.Scan0, pixels.Length); } finally { bitmap.UnlockBits(data); }
        using var graphics = Graphics.FromImage(bitmap);
        using var font = new Font("Segoe UI", 12);
        using var small = new Font("Segoe UI", 9);
        using var ink = new SolidBrush(Color.FromArgb(36, 48, 65));
        using var green = new SolidBrush(Color.FromArgb(20, 125, 105));
        graphics.DrawString($"{id}  |  {view}", font, ink, 22, 18);
        graphics.DrawString($"{count:N0} objects · offscreen mesh reference · green: view-source frames", small, ink, 22, 766);
        foreach (var instance in instances)
            foreach (var (point, label) in instance.Asset.Labels)
            {
                var p = point; p.Transform(instance.Transform); var v = Project(p);
                int px = (int)Math.Round(v.X), py = (int)Math.Round(v.Y);
                if (label == "•" && (px < 0 || px >= width || py < 0 || py >= height ||
                    v.Z < depth[py * width + px] - bounds.Diagonal.Length * 1e-5)) continue;
                graphics.DrawString(label, small, ink, (float)v.X, (float)v.Y);
            }
        if (id == "view-sources")
            for (int i = 0; i < frames.Count; i++)
                graphics.DrawString(frames[i].Name, small, green, 22, 52 + i * 18);
        bitmap.Save(path, ImageFormat.Png);
    }
}
