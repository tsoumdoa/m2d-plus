using M2DPlus.Core.Sections;
using Rhino.Geometry;

namespace M2DPlus.Rhino.Sections;

/// <summary>Route C preparation. Near normal points toward removed space; far is opposite and parallel.</summary>
public static class PhysicalSectionClipper
{
    public static PhysicalClipResult Clip(SectionSource source, SectionRequest request, Plane? farPlane = null)
    {
        SectionGeometry.Validate(request);
        var result = new PhysicalClipResult();
        if (farPlane is { } far)
        {
            var depth = -request.Plane.DistanceTo(far.Origin);
            if (!far.IsValid || Vector3d.Multiply(request.Plane.Normal, far.Normal) > -Math.Cos(request.AngleTolerance) || depth < 0)
                throw new ArgumentException("Far plane must be behind, parallel to and oppositely oriented from near.");
            if (depth == 0) { result.SectionOnly = true; return result; }
            if (depth <= request.Tolerance)
            { result.Diagnostics.Add(new(source.Id, SectionStatus.Uncertain, "Near/far separation is within tolerance; background quarantined.")); return result; }
        }
        try
        {
            if (source.Geometry is Mesh { IsClosed: false }) throw new SectionFailure("Open mesh visibility trim/cap is unsupported; do not use it as a closed occluder.");
            using var copy = SectionGeometry.Convert(source.Geometry, request.Tolerance);
            var components = SectionGeometry.MaterialComponents(copy, request.Tolerance);
            try
            {
                foreach (var component in components)
                {
                    var original = component.Edges.Select(e => e.DuplicateCurve()).ToArray();
                    try
                    {
                        var current = Halfspace(source.Id, component, request, result.Diagnostics, result.ContactFeatures);
                        if (farPlane is { } farCut)
                        {
                            var next = new List<Brep>();
                            try
                            {
                                foreach (var piece in current)
                                    next.AddRange(Halfspace(source.Id, piece, request with { Plane = farCut }, result.Diagnostics, result.ContactFeatures));
                            }
                            catch { foreach (var piece in next) piece.Dispose(); throw; }
                            finally { foreach (var piece in current) piece.Dispose(); }
                            current = next;
                        }
                        try
                        {
                            foreach (var piece in current.ToArray())
                            {
                                var edges = new List<SectionEdge>();
                                try
                                {
                                    foreach (var edge in piece.Edges)
                                    {
                                        var kind = edge.IsInPlane(request.Plane, request.Tolerance) ? EdgeOrigin.NearTrim :
                                            farPlane is { } fp && edge.IsInPlane(fp, request.Tolerance) ? EdgeOrigin.FarTrim : EdgeOrigin.None;
                                        edges.AddRange(EdgeProvenance.Split(edge, original.Select(c => (c, EdgeOrigin.Original)), request.Tolerance, kind));
                                    }
                                    if (edges.Any(e => e.Origin == EdgeOrigin.None)) throw new SectionFailure("Unattributed visibility trim edge; source quarantined.");
                                    result.Pieces.Add(new(source.Id, piece, edges));
                                    current.Remove(piece); // Ownership transferred to result.
                                }
                                catch { foreach (var edge in edges) edge.Dispose(); throw; }
                            }
                        }
                        finally { foreach (var piece in current) piece.Dispose(); }
                    }
                    finally { foreach (var edge in original) edge.Dispose(); }
                }
            }
            finally { foreach (var component in components) component.Dispose(); }
        }
        catch (Exception ex) when (ex is SectionFailure or ArgumentException or InvalidOperationException)
        { result.Diagnostics.Add(new(source.Id, SectionStatus.Unsupported, ex.Message)); }
        // A partially prepared source must never be used as complete occlusion.
        if (!result.Succeeded)
        {
            foreach (var piece in result.Pieces) piece.Dispose(); result.Pieces.Clear();
            foreach (var feature in result.ContactFeatures) feature.Dispose(); result.ContactFeatures.Clear();
        }
        return result;
    }

    internal static List<Brep> Halfspace(string id, Brep source, SectionRequest request, List<SectionDiagnostic> diagnostics,
        List<SectionContactFeature>? contactFeatures = null)
    {
        var classification = SectionGeometry.Classify(id, 0, source, request);
        if (classification.Status == SectionStatus.Behind) return new() { source.DuplicateBrep() };
        if (classification.Status == SectionStatus.InFront) return new();
        if (classification.Status == SectionStatus.Uncertain)
        { diagnostics.Add(new(id, SectionStatus.Uncertain, "No trustworthy halfspace certificate; source quarantined.")); return new(); }
        if (classification.Status == SectionStatus.Contact)
        {
            // Keep retained contact volumes whole. A volume on the removed side retains its exact planar face(s).
            if (classification.Maximum <= request.Tolerance) return new() { source.DuplicateBrep() };
            var faces = new List<Brep>();
            var faceEdges = new HashSet<int>();
            foreach (var face in source.Faces)
                if (face.TryGetPlane(out var facePlane, request.Tolerance) &&
                    Math.Abs(request.Plane.DistanceTo(facePlane.Origin)) <= Roundoff(source) &&
                    Vector3d.CrossProduct(facePlane.Normal, request.Plane.Normal).Length <= 64 * 2.2204460492503131e-16)
                {
                    faces.Add(face.DuplicateFace(false));
                    foreach (var edge in face.AdjacentEdges()) faceEdges.Add(edge);
                }
            if (contactFeatures is not null)
            {
                var contactEdges = source.Edges.Where(e => e.IsInPlane(request.Plane, Roundoff(source))).ToArray();
                foreach (var edge in contactEdges.Where(e => !faceEdges.Contains(e.EdgeIndex)))
                    contactFeatures.Add(new(id, edge.DuplicateCurve()));
                foreach (var vertex in source.Vertices.Where(v => Math.Abs(request.Plane.DistanceTo(v.Location)) <= Roundoff(source)))
                    if (!contactEdges.Any(e => e.ClosestPoint(vertex.Location, out var t) && e.PointAt(t).DistanceTo(vertex.Location) <= Roundoff(source)))
                        contactFeatures.Add(new(id, new Point(vertex.Location)));
            }
            return faces;
        }
        var pieces = source.Trim(request.Plane, request.Tolerance) ?? Array.Empty<Brep>();
        var result = new List<Brep>();
        try
        {
            if (pieces.Length == 0) throw new SectionFailure("Certified crossing produced no trimmed geometry.");
            var retainedPieces = new List<Brep>();
            if (source.IsSolid)
            {
                // Cap all shells together using material regions. Capping each disconnected shell
                // independently would turn an enclosed cavity into an occluding filled solid.
                var loops = SectionGeometry.Intersections(source, request.Plane, request.Tolerance);
                try
                {
                    var caps = SectionGeometry.PlanarRegions(loops, request);
                    try { retainedPieces.AddRange(Brep.JoinBreps(pieces.Concat(caps), request.Tolerance) ?? Array.Empty<Brep>()); }
                    finally { foreach (var cap in caps) cap.Dispose(); }
                }
                finally { foreach (var loop in loops) loop.Dispose(); }
            }
            else retainedPieces.AddRange(pieces.Select(p => p.DuplicateBrep()));
            if (retainedPieces.Count == 0) throw new SectionFailure("Trim/cap join failed.");
            foreach (var retained in retainedPieces) result.Add(retained);
            foreach (var retained in result)
            {
                if (!retained.IsValid || source.IsSolid && !retained.IsSolid)
                    throw new SectionFailure("Trim/cap failed; uncut geometry is never substituted.");
                if (retained.IsSolid && retained.SolidOrientation == BrepSolidOrientation.Inward) retained.Flip();
                var bounds = retained.GetBoundingBox(Transform.PlaneToPlane(request.Plane, Plane.WorldXY));
                if (bounds.Max.Z > request.Tolerance || bounds.Min.Z >= -Roundoff(source) ||
                    source.IsSolid && Math.Abs(retained.GetVolume()) <= request.Tolerance * request.Tolerance * request.Tolerance)
                    throw new SectionFailure("Degenerate or wrong-side trim/cap result.");
            }
            return result;
        }
        catch { foreach (var piece in result) piece.Dispose(); throw; }
        finally { foreach (var piece in pieces) piece.Dispose(); }
    }

    private static double Roundoff(Brep source) => 64 * 2.2204460492503131e-16 *
        Math.Max(1, source.GetBoundingBox(true).Diagonal.Length);
}
