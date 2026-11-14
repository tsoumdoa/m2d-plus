using M2DPlus.Core.Sections;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;

namespace M2DPlus.Rhino.Sections;

internal static class SectionGeometry
{
    public static void Validate(SectionRequest request)
    {
        if (!request.Plane.IsValid || !double.IsFinite(request.Tolerance) || request.Tolerance <= 0 ||
            !double.IsFinite(request.AngleTolerance) || request.AngleTolerance <= 0 || request.AngleTolerance >= Math.PI)
            throw new ArgumentException("A valid plane and positive finite tolerances are required.");
        if (!Enum.IsDefined(request.Cuts) || !Enum.IsDefined(request.Hatch)) throw new ArgumentException("Unknown section option.");
        if (request.Crop is { } crop && (!crop.IsValid || crop.Width <= request.Tolerance || crop.Height <= request.Tolerance ||
            Math.Abs(request.Plane.DistanceTo(crop.Plane.Origin)) > request.Tolerance ||
            Vector3d.CrossProduct(request.Plane.Normal, crop.Plane.Normal).Length > Math.Sin(request.AngleTolerance)))
            throw new ArgumentException("Crop must be a nondegenerate rectangle on the cut plane.");
    }

    public static Brep Convert(GeometryBase source, double tolerance)
    {
        Brep? copy;
        switch (source)
        {
            case Brep brep: copy = brep.DuplicateBrep(); break;
            case Extrusion extrusion: copy = extrusion.ToBrep(); break;
            case Mesh mesh:
                if (!mesh.IsValid || !mesh.IsManifold()) throw new SectionFailure("Non-manifold or invalid mesh.");
                if (!mesh.GetSelfIntersections(tolerance, out var perforations, true, out var overlaps,
                    false, out var overlapMesh, null, CancellationToken.None, null))
                    throw new SectionFailure("Mesh self-intersection check failed.");
                overlapMesh?.Dispose();
                if (perforations is { Length: > 0 } || overlaps is { Length: > 0 }) throw new SectionFailure("Self-intersecting mesh.");
                // Exact polyhedral conversion, rather than contour stitching plus a guessed mesh cap.
                copy = Brep.CreateFromMesh(mesh, true);
                break;
            default: throw new SectionFailure("Expected Brep, Extrusion or Mesh; convert SubD to an owned Brep before calling.");
        }
        if (copy is null) throw new SectionFailure("Geometry conversion failed.");
        if (!copy.IsValid || !copy.IsManifold) { copy.Dispose(); throw new SectionFailure("Invalid or non-manifold Brep."); }
        if (copy.IsSolid && copy.SolidOrientation == BrepSolidOrientation.Inward) copy.Flip();
        try { ValidateFaceIntersections(copy, tolerance); }
        catch { copy.Dispose(); throw; }
        return copy;
    }

    private static void ValidateFaceIntersections(Brep brep, double tolerance)
    {
        for (var i = 0; i < brep.Faces.Count; i++)
        for (var j = i + 1; j < brep.Faces.Count; j++)
        {
            var a = brep.Faces[i]; var b = brep.Faces[j];
            var box = a.GetBoundingBox(true); box.Inflate(tolerance);
            if (!BoundingBox.Intersection(box, b.GetBoundingBox(true)).IsValid) continue;
            var shared = a.AdjacentEdges().Intersect(b.AdjacentEdges()).Select(k => brep.Edges[k]).ToArray();
            // Include singular-trim vertices of triangular mesh faces, which need not have a BrepEdge.
            var aVertices = a.Loops.SelectMany(l => l.Trims).SelectMany(t => new[] { t.StartVertex.Location, t.EndVertex.Location }).ToArray();
            var bVertices = b.Loops.SelectMany(l => l.Trims).SelectMany(t => new[] { t.StartVertex.Location, t.EndVertex.Location }).ToArray();
            var sharedPoints = aVertices.Where(p => bVertices.Any(q => q.DistanceTo(p) <= tolerance)).ToArray();
            using var first = a.DuplicateFace(false); using var second = b.DuplicateFace(false);
            if (!Intersection.BrepBrep(first, second, tolerance, out var curves, out var points))
                throw new SectionFailure("Cannot validate face self-intersection relationships.");
            try
            {
                foreach (var curve in curves)
                {
                    var intervals = EdgeProvenance.Split(curve, shared.Select(c => ((Curve)c, EdgeOrigin.Original)), tolerance);
                    try
                    {
                        if (intervals.Any(e => e.Origin == EdgeOrigin.None && e.Curve.GetLength() > tolerance))
                            throw new SectionFailure("Self-intersecting Brep faces away from their shared topology.");
                    }
                    finally { foreach (var interval in intervals) interval.Dispose(); }
                }
                foreach (var p in points)
                    if (OnTrimmedFace(a, p, tolerance) && OnTrimmedFace(b, p, tolerance) &&
                        !sharedPoints.Any(q => q.DistanceTo(p) <= tolerance) &&
                        !shared.Any(e => e.ClosestPoint(p, out var t) && e.PointAt(t).DistanceTo(p) <= tolerance))
                        throw new SectionFailure($"Self-intersecting Brep faces {i}/{j} at {p} away from shared topology.");
            }
            finally { foreach (var curve in curves) curve.Dispose(); }
        }
    }

    private static bool OnTrimmedFace(BrepFace face, Point3d point, double tolerance) =>
        face.ClosestPoint(point, out var u, out var v) && face.PointAt(u, v).DistanceTo(point) <= tolerance &&
        face.IsPointOnFace(u, v) != PointFaceRelation.Exterior;

    // Keep enclosing void shells together. Disjoint material shells are classified separately.
    public static List<Brep> MaterialComponents(Brep source, double tolerance)
    {
        var shells = source.GetConnectedComponents();
        if (shells.Length == 0) return new() { source.DuplicateBrep() };
        if (shells.Length <= 1) return shells.ToList();
        try
        {
            var areas = shells.Select(b => Math.Abs(b.GetVolume())).ToArray();
            var contains = new bool[shells.Length, shells.Length];
            for (var i = 0; i < shells.Length; i++)
            for (var j = i + 1; j < shells.Length; j++)
            {
                if (!Intersection.BrepBrep(shells[i], shells[j], tolerance, out var curves, out var points))
                    throw new SectionFailure("Cannot validate disconnected shell relationships.");
                var intersects = curves.Length != 0 || points.Length != 0;
                foreach (var c in curves) c.Dispose();
                if (intersects) throw new SectionFailure("Intersecting disconnected shells are unsupported material topology.");
                if (shells[i].IsSolid && shells[j].Vertices.Count > 0)
                    contains[i, j] = ShellContains(shells[i], shells[j].Vertices[0].Location, tolerance);
                if (shells[j].IsSolid && shells[i].Vertices.Count > 0)
                    contains[j, i] = ShellContains(shells[j], shells[i].Vertices[0].Location, tolerance);
            }
            if (shells.Any(s => !s.IsSolid)) return shells.ToList();
            var nesting = RegionTopology.Nest(areas, (i, j) => contains[i, j]);
            foreach (var shell in nesting.Where(n => n.Parent.HasValue))
                if (shells[shell.Index].SolidOrientation == shells[shell.Parent!.Value].SolidOrientation)
                    throw new SectionFailure("Nested shells have equal material orientation; overlapping solids are not inferred voids.");
            var result = new List<Brep>();
            foreach (var outer in nesting.Where(n => !n.IsHole))
            {
                var b = shells[outer.Index].DuplicateBrep();
                foreach (var hole in nesting.Where(n => n.Parent == outer.Index)) b.Append(shells[hole.Index]);
                result.Add(b);
            }
            foreach (var shell in shells) shell.Dispose();
            return result;
        }
        catch { foreach (var shell in shells) shell.Dispose(); throw; }
    }

    private static bool ShellContains(Brep shell, Point3d point, double tolerance)
    {
        using var outward = shell.DuplicateBrep();
        if (outward.SolidOrientation == BrepSolidOrientation.Inward) outward.Flip();
        return outward.IsPointInside(point, tolerance, true);
    }

    public static ComponentClassification Classify(string id, int index, Brep brep, SectionRequest request)
    {
        var plane = request.Plane;
        var transform = Transform.PlaneToPlane(plane, Plane.WorldXY);
        var bounds = brep.GetBoundingBox(transform);
        var worldBounds = brep.GetBoundingBox(true);
        var coordinateScale = new[] { 1d, bounds.Min.MaximumCoordinate, bounds.Max.MaximumCoordinate,
            worldBounds.Min.MaximumCoordinate, worldBounds.Max.MaximumCoordinate, plane.Origin.MaximumCoordinate }.Max();
        var roundoff = 64 * 2.2204460492503131e-16 * coordinateScale;
        // Test linear geometry at floating-point error scale, not a smaller invented modelling tolerance.
        var polyhedral = brep.Faces.All(f => f.IsPlanar(roundoff)) && brep.Edges.All(e => e.IsLinear(roundoff));
        var distances = brep.Vertices.Select(v => plane.DistanceTo(v.Location)).ToArray();
        var min = polyhedral && distances.Length > 0 ? distances.Min() : bounds.Min.Z;
        var max = polyhedral && distances.Length > 0 ? distances.Max() : bounds.Max.Z;
        var status = SectionPolicy.Classify(min, max, request.Tolerance, polyhedral, roundoff);
        if (status == SectionStatus.Crossing && !polyhedral &&
            !(distances.Any(d => d < -request.Tolerance) && distances.Any(d => d > request.Tolerance)))
            status = SectionStatus.Uncertain;
        // Analytic extruded sections are the initial certified domain. Freeform remains an explicit gate.
        if (status == SectionStatus.Crossing && !brep.Faces.All(f => f.IsPlanar(request.Tolerance) || f.IsCylinder(request.Tolerance)))
            status = SectionStatus.Uncertain;
        return new(id, index, status, min, max, polyhedral ? "Linear-face extrema; validate transverse loops and trim/cap before fill."
            : "Conservative bounds and actual vertex witnesses; analytic-face and trim/cap validation required.");
    }

    public static Curve[] Intersections(Brep source, Plane plane, double tolerance)
    {
        if (!Intersection.BrepPlane(source, plane, tolerance, out var segments, out _))
            return Array.Empty<Curve>(); // An empty intersection is legitimate for a disjoint component.
        try { return Curve.JoinCurves(segments, tolerance); }
        finally { foreach (var c in segments) c.Dispose(); }
    }

    public static double Area(Brep region)
    {
        using var mass = AreaMassProperties.Compute(region);
        return mass?.Area ?? throw new SectionFailure("Area calculation failed.");
    }

    public static Point3d Interior(Brep region, double tolerance)
    {
        var meshes = Mesh.CreateFromBrep(region, MeshingParameters.FastRenderMesh);
        try
        {
            foreach (var mesh in meshes)
            foreach (var face in mesh.Faces)
            {
                var a = (Point3d)mesh.Vertices[face.A]; var b = (Point3d)mesh.Vertices[face.B]; var c = (Point3d)mesh.Vertices[face.C];
                var p = new Point3d((a.X + b.X + c.X) / 3, (a.Y + b.Y + c.Y) / 3, (a.Z + b.Z + c.Z) / 3);
                if (region.Faces[0].ClosestPoint(p, out var u, out var v) &&
                    region.Faces[0].PointAt(u, v).DistanceTo(p) <= tolerance &&
                    region.Faces[0].IsPointOnFace(u, v) == PointFaceRelation.Interior) return p;
            }
        }
        finally { foreach (var mesh in meshes) mesh.Dispose(); }
        throw new SectionFailure("Cannot certify an interior point on the trimmed region.");
    }

    public static bool Contains(Brep region, Point3d point, double tolerance) => region.Faces[0].ClosestPoint(point, out var u, out var v) &&
        region.Faces[0].PointAt(u, v).DistanceTo(point) <= tolerance &&
        region.Faces[0].IsPointOnFace(u, v) == PointFaceRelation.Interior;

    public static List<Brep> PlanarRegions(IReadOnlyList<Curve> loops, SectionRequest request)
    {
        var areas = new List<double>();
        foreach (var loop in loops)
        {
            if (!loop.IsValid || !loop.IsClosed || !loop.IsInPlane(request.Plane, request.Tolerance))
                throw new SectionFailure("Intersection is not a valid closed planar loop.");
            using var self = Intersection.CurveSelf(loop, request.Tolerance);
            using var mass = AreaMassProperties.Compute(loop);
            if (self.Count != 0 || mass is null || mass.Area <= request.Tolerance * request.Tolerance)
                throw new SectionFailure("Self-intersecting or degenerate section loop.");
            areas.Add(mass.Area);
        }
        var contains = new bool[loops.Count, loops.Count];
        for (var i = 0; i < loops.Count; i++)
        for (var j = i + 1; j < loops.Count; j++)
        {
            var relation = Curve.PlanarClosedCurveRelationship(loops[i], loops[j], request.Plane, request.Tolerance);
            if (relation == RegionContainment.MutualIntersection) throw new SectionFailure("Section loops intersect or touch ambiguously.");
            contains[i, j] = relation == RegionContainment.BInsideA;
            contains[j, i] = relation == RegionContainment.AInsideB;
        }
        var nesting = RegionTopology.Nest(areas, (i, j) => contains[i, j]);
        var expected = nesting.Sum(n => n.IsHole ? -areas[n.Index] : areas[n.Index]);
        var regions = Brep.CreatePlanarBreps(loops, request.Tolerance)?.ToList() ?? throw new SectionFailure("Planar region creation failed.");
        if (regions.Count == 0 || Math.Abs(regions.Sum(Area) - expected) > AreaError(expected, request.Tolerance))
        { foreach (var region in regions) region.Dispose(); throw new SectionFailure("Region nesting/area conservation failed."); }
        return regions;
    }

    public static double AreaError(double area, double tolerance) => Math.Max(tolerance * tolerance * 16, Math.Abs(area) * 1e-6);
}

internal sealed class SectionFailure(string message) : Exception(message);

