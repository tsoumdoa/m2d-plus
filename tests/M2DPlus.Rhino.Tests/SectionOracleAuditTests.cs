using System.Text.Json;
using Rhino.FileIO;
using Rhino.Geometry;
using Xunit;

namespace M2DPlus.Rhino.Tests;

[Collection("Rhino")]
public sealed class SectionOracleAuditTests(RhinoHost host)
{
    [RhinoFact, Trait("Category", "RhinoIntegration")]
    public void AuditSpaceFrameTubes() => host.Run(() => {
        var root = Path.GetFullPath("../../../../../", AppContext.BaseDirectory);
        using var file = File3dm.Read(Path.Combine(root, "hillclimb/c-xval/fixtures/space-frame.3dm"));
        var report = new List<object>();
        foreach (var obj in file.Objects.Where(o => o.Attributes.Name is "CHS-0027" or "CHS-0031" or "CHS-0035" or "CHS-0039" or "CHS-0043" or "CHS-0047" or "CHS-0287" or "CHS-0291" or "CHS-0295" or "CHS-0299" or "CHS-0303" or "CHS-0307"))
        {
            var instance = Assert.IsType<InstanceReferenceGeometry>(obj.Geometry);
            var definition = file.AllInstanceDefinitions.FindId(instance.ParentIdefId);
            var body = Assert.IsType<Brep>(file.Objects.FindId(Assert.Single(definition.GetObjectIds())).Geometry);
            var transform = instance.Xform;
            var faces = body.Faces.Select(f => f.TryGetCylinder(out var c, 1e-9)
                ? $"cylinder radius={c.Radius:R} axis={c.Axis} center={c.Center} height={c.TotalHeight:R}"
                : f.TryGetPlane(out var p, 1e-9) ? $"plane {p.Origin} normal={p.Normal}" : f.UnderlyingSurface().GetType().Name).ToArray();
            var x = Vector3d.XAxis; x.Transform(transform);
            var y = Vector3d.YAxis; y.Transform(transform);
            var z = Vector3d.ZAxis; z.Transform(transform);
            using var world = body.DuplicateBrep(); world.Transform(transform);
            var center = Point3d.Origin; center.Transform(transform);
            var offset = Math.Abs(4.5 - center.Z);
            var radii = body.Faces.Where(f => f.IsCylinder(1e-9)).Select(f => { f.TryGetCylinder(out var c, 1e-9); return c.Radius; }).OrderDescending().ToArray();
            Assert.Equal(4, body.Faces.Count);
            Assert.Equal(2, radii.Length);
            Assert.Equal(.065, radii[0], 12); Assert.Equal(.05, radii[1], 12);
            Assert.Equal(1, x.Length, 12); Assert.Equal(1, y.Length, 12);
            Assert.Equal(0, x*y, 12); Assert.Equal(0, x*z, 12); Assert.Equal(0, y*z, 12);
            Assert.Equal(0, z.Z, 12);
            Assert.Equal(1.2, z.Length, 12);
            var localBounds = body.GetBoundingBox(true);
            Assert.Equal(0, localBounds.Min.Z, 12); Assert.Equal(1, localBounds.Max.Z, 12);
            Assert.All(body.Faces.Where(f => !f.IsCylinder(1e-9)), f => Assert.True(f.IsPlanar(1e-9)));
            // Parallel-to-axis plane cuts a finite circular tube into two rectangular
            // strips. This is closed-form circle arithmetic, no native intersections.
            var analytic = 2 * z.Length * (Math.Sqrt(Math.Max(0, radii[0]*radii[0]-offset*offset)) - Math.Sqrt(Math.Max(0, radii[1]*radii[1]-offset*offset)));
            Assert.InRange(offset, radii[1] + 1e-6, radii[0] - 1e-6);
            using var oracles = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "hillclimb/c-xval/oracles.json")));
            var oracle = oracles.RootElement.GetProperty("space-frame/Plan cut/" + obj.Attributes.Name + "/CHS-unit/member-0");
            Assert.Equal(analytic, oracle.GetProperty("Area").GetDouble(), 12);
            Assert.Equal(0, oracle.GetProperty("Holes").GetInt32());
            // Source membership agrees with the analytic rectangle, including points
            // immediately inside/outside each long edge and in the uncut central bore.
            var halfWidth = Math.Sqrt(radii[0]*radii[0]-offset*offset);
            var mid = center + z*.5; mid.Z=4.5;
            Assert.True(world.IsPointInside(mid, 1e-8, true));
            Assert.True(world.IsPointInside(mid + y*(halfWidth-1e-5), 1e-8, true));
            Assert.False(world.IsPointInside(mid + y*(halfWidth+1e-5), 1e-8, true));
            Assert.False(world.IsPointInside(center + z*.5, 1e-8, true));
            var worldFaces = world.Faces.Select(f => f.TryGetCylinder(out var c, 1e-9) ? $"cylinder {c.Radius:R}" : f.UnderlyingSurface().GetType().Name).ToArray();
            report.Add(new { name = obj.Attributes.Name, faces, worldFaces, center=center.ToString(), offset, analytic, x = x.ToString(), y = y.ToString(), z = z.ToString(), lx=x.Length, ly=y.Length, lz=z.Length, xy=x*y, xz=x*z, yz=y*z });
        }
        Assert.Equal(12, report.Count);
        Directory.CreateDirectory(Path.Combine(root, "artifacts"));
        File.WriteAllText(Path.Combine(root, "artifacts/c2-tube-verification.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented=true }));
    });
}
