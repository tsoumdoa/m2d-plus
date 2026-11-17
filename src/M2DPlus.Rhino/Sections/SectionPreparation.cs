using M2DPlus.Rhino.Services;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;
using M2DPlus.Core;

namespace M2DPlus.Rhino.Sections;

/// <summary>Owned route C preparation. Region admission remains independent of projection success:
/// SectionRegionEngine rechecks a near cap when another component fails trimming,
/// retains stable-region exceptions and owns all refusal geometry until disposal.</summary>
internal static class SectionPreparation
{
    internal static SectionResult Compute(IEnumerable<SectionSource> sources, SectionRequest request,
        SectionVerdictCache verdicts, CancellationToken token, Plane? far, PhaseTimings phases) =>
        SectionRegionEngine.Compute(sources, request, verdicts, token, prepareVisibility: true, farPlane: far, phases: phases);
    internal static List<BoundingBox> FailedClipFootprints(IEnumerable<PreparedLeaf> bodies, SectionResult sections, Plane plane, ViewDrawing output)
    {
        var footprints = new List<BoundingBox>();
        foreach (var body in bodies.Where(s => !sections.Visibility.TryGetValue(s.Identity, out var clip) || !clip.Succeeded))
        {
            footprints.AddRange(SectionFootprints(body.Geometry, plane).Where(b => b.IsValid));
            output._diagnostics.Add($"{HldProjection.FailedClip}{body.Identity}; only overlapping projected pieces are Unresolved.");
        }
        return footprints;
    }
    // Each mesh face lies inside the convex hull of its vertices (including
    // either diagonal and a bilinear quad). A projected face box therefore
    // covers every possible occlusion locus without filling empty space between
    // disconnected or curved mesh panels with one whole-source rectangle.
    internal const int MaximumMeshFootprints = 128;
    internal static BoundingBox[] SectionFootprints(GeometryBase geometry, Plane plane)
    {
        var projection = Transform.PlanarProjection(plane);
        if (geometry is not Mesh mesh || !mesh.IsValid || mesh.Faces.Count > MaximumMeshFootprints)
            return [geometry.GetBoundingBox(projection)];
        return mesh.Faces.Select(f => new BoundingBox(new[] { f.A, f.B, f.C, f.D }.Distinct().Select(i =>
        {
            var p = (Point3d)mesh.Vertices[i]; p.Transform(projection); return p;
        }))).ToArray();
    }

    internal static void AddRefusedMarker(ViewDrawing result, PreparedLeaf source, GeometryBase geometry,
        SectionRequest request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var tolerance = request.Tolerance;
        using var input = geometry.Duplicate(); input.EnsurePrivateCopy();
        var origin = SectionCoordinates.Origin(request.Plane, input.GetBoundingBox(true));
        var toLocal = Transform.Translation(-origin.X, -origin.Y, -origin.Z);
        var toWorld = Transform.Translation(origin.X, origin.Y, origin.Z);
        var local = SectionCoordinates.LocalRequest(request, origin);
        var plane = local.Plane;
        if (!input.Transform(toLocal)) throw new SectionFailure("Cannot translate refused section marker.");
        var markers = new List<Curve>();
        try
        {
            try
            {
                token.ThrowIfCancellationRequested();
                if (input is Brep brep)
                {
                    var success = Intersection.BrepPlane(brep,plane,tolerance,out var curves,out _);
                    try
                    {
                        foreach (var curve in curves)
                        {
                            token.ThrowIfCancellationRequested();
                            if (!success || !curve.IsValid || curve.GetLength() <= 0) continue;
                            var copy = curve.DuplicateCurve();
                            if (copy.Transform(Transform.PlanarProjection(plane))) markers.Add(copy); else copy.Dispose();
                        }
                    }
                    finally { foreach (var curve in curves) curve.Dispose(); }
                }
                else if (input is Mesh mesh)
                    foreach (var line in Intersection.MeshPlane(mesh,plane) ?? [])
                        if (line.IsValid && line.Count > 1) markers.Add(new PolylineCurve(line));
                else if (input is Curve outline) markers.Add(outline.DuplicateCurve());
                token.ThrowIfCancellationRequested();
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            { foreach (var curve in markers) curve.Dispose(); markers.Clear(); }
            bool footprint = markers.Count == 0;
            if (footprint)
            {
                token.ThrowIfCancellationRequested();
                var bounds = input.GetBoundingBox(Transform.PlaneToPlane(plane,Plane.WorldXY));
                if (bounds.IsValid)
                {
                    var x = new Interval(bounds.Min.X,bounds.Max.X);var y = new Interval(bounds.Min.Y,bounds.Max.Y);
                    if (x.Length <= tolerance) x = new(x.Mid-tolerance,x.Mid+tolerance);
                    if (y.Length <= tolerance) y = new(y.Mid-tolerance,y.Mid+tolerance);
                    markers.Add(new Rectangle3d(plane,x,y).ToNurbsCurve());
                }
                else
                {
                    var frame = result.Frame; frame.Transform(toLocal); markers.Add(frame.ToNurbsCurve());
                }
            }
            var firstMarker = result.Pieces.Count;
            foreach (var curve in markers)
            {
                token.ThrowIfCancellationRequested();
                IEnumerable<Curve> parts = local.Crop is { } crop ? NativeDrawing.Crop(curve, crop, tolerance) : [curve.DuplicateCurve()];
                foreach (var part in parts)
                {
                    try
                    {
                        token.ThrowIfCancellationRequested();
                        if (!part.Transform(toWorld)) throw new SectionFailure("Cannot restore refused section marker.");
                        result.Pieces.Add(new(part,source,LineClass.Unresolved));
                    }
                    catch { part.Dispose(); throw; }
                }
            }
            // The uncertain outline can surround the entire crop, leaving no
            // clipped boundary ink. Mark the intersection of its conservative
            // footprint and the crop, rather than disappearing from the view.
            if (result.Pieces.Count == firstMarker && local.Crop is { } covered)
            {
                token.ThrowIfCancellationRequested();
                var b = input.GetBoundingBox(Transform.PlaneToPlane(covered.Plane, Plane.WorldXY));
                var x = new Interval(Math.Max(covered.X.Min,b.Min.X),Math.Min(covered.X.Max,b.Max.X));
                var y = new Interval(Math.Max(covered.Y.Min,b.Min.Y),Math.Min(covered.Y.Max,b.Max.Y));
                if (b.IsValid && x.T1>x.T0 && y.T1>y.T0)
                {
                    using var rectangle = new Rectangle3d(covered.Plane,x,y).ToNurbsCurve();
                    foreach (var part in NativeDrawing.Crop(rectangle,covered,tolerance))
                    {
                        try
                        {
                            token.ThrowIfCancellationRequested();
                            if (!part.Transform(toWorld)) throw new SectionFailure("Cannot restore refused footprint marker.");
                            result.Pieces.Add(new(part,source,LineClass.Unresolved));
                        }
                        catch { part.Dispose();throw; }
                    }
                    footprint = true;
                }
            }
            result._diagnostics.Add($"Uncertified section marker: source={source.Identity}; {(footprint ? "projected source footprint" : "uncertified intersection outline")}; Unresolved only, no hatch.");
        }
        finally { foreach (var curve in markers) curve.Dispose(); }
    }

    internal static bool ProjectedOverlap(GeometryBase geometry, SectionRegion region, Plane plane, double tolerance)
    {
        var transform = Transform.PlaneToPlane(plane, Plane.WorldXY); var a = geometry.GetBoundingBox(transform);
        var b = BoundingBox.Empty; foreach (var loop in region.Loops) b.Union(loop.Curve.GetBoundingBox(transform));
        return a.Max.Z > tolerance && a.Min.X <= b.Max.X + tolerance && a.Max.X >= b.Min.X - tolerance &&
            a.Min.Y <= b.Max.Y + tolerance && a.Max.Y >= b.Min.Y - tolerance;
    }

    internal static IEnumerable<Curve> ClipCurve(Curve curve, Plane near, Plane? far, double tolerance)
    {
        var parameters = new SortedSet<double> { curve.Domain.Min, curve.Domain.Max };
        foreach (var plane in far is { } fp ? new[] { near, fp } : new[] { near })
        {
            using var hits = Intersection.CurvePlane(curve, plane, tolerance);
            if (hits is null) continue;
            foreach (var hit in hits)
            {
                if (hit.IsOverlap) { parameters.Add(hit.OverlapA.Min); parameters.Add(hit.OverlapA.Max); }
                else parameters.Add(hit.ParameterA);
            }
        }
        var ends = parameters.ToArray();
        for (var i = 1; i < ends.Length; i++)
        {
            var mid = curve.PointAt((ends[i - 1] + ends[i]) / 2);
            if (near.DistanceTo(mid) > 0 || far is { } plane && plane.DistanceTo(mid) > 0) continue;
            yield return curve.Trim(ends[i - 1], ends[i]) ?? throw new InvalidOperationException("Curve halfspace trim failed.");
        }
    }
}
