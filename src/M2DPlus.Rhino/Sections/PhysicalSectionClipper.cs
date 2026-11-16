using M2DPlus.Core.Sections;
using Rhino.Geometry;

namespace M2DPlus.Rhino.Sections;

/// <summary>Route C preparation. Near normal points toward removed space; far is opposite and parallel.</summary>
public static class PhysicalSectionClipper
{
    internal const string RecoveredEdges = "Continuous original trim-edge recovery";
    public static PhysicalClipResult Clip(SectionSource source, SectionRequest request, Plane? farPlane = null)
    {
        using var shared = new SharedSource(source, request, null);
        return Clip(shared, request, farPlane);
    }

    internal static PhysicalClipResult Clip(SharedSource shared, SectionRequest request, Plane? farPlane)
    {
        var source = shared.Source;
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
            var components = shared.Components(); // Borrowed: shared with the region pass.
            {
                for (var index = 0; index < components.Count; index++)
                {
                    var component = components[index];
                    var original = component.Edges.Select(e => e.DuplicateCurve()).ToArray();
                    try
                    {
                        var current = Halfspace(source.Id, component, request, result.Diagnostics, out var status, result.ContactFeatures, shared, index);
                        var whole = status == SectionStatus.Behind;
                        if (farPlane is { } farCut)
                        {
                            var next = new List<Brep>();
                            try
                            {
                                foreach (var piece in current)
                                {
                                    next.AddRange(Halfspace(source.Id, piece, request with { Plane = farCut }, result.Diagnostics, out status, result.ContactFeatures));
                                    whole &= status == SectionStatus.Behind;
                                }
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
                                    // A piece wholly behind every plane is an untouched duplicate: all its edges are original.
                                    if (whole) edges.AddRange(piece.Edges.Select(e => new SectionEdge(e.DuplicateCurve(), EdgeOrigin.Original)));
                                    else foreach (var edge in piece.Edges)
                                    {
                                        var kind = edge.IsInPlane(request.Plane, request.Tolerance) ? EdgeOrigin.NearTrim :
                                            farPlane is { } fp && edge.IsInPlane(fp, request.Tolerance) ? EdgeOrigin.FarTrim : EdgeOrigin.None;
                                        edges.AddRange(EdgeProvenance.Split(edge, original.Select(c => (c, EdgeOrigin.Original)), request.Tolerance, kind));
                                    }
                                    // Native overlap attribution can omit a rim or a tiny
                                    // interval at a split seam. Only a continuous enclosing
                                    // box proof against the original edges can recover it.
                                    var recoveredEdges = false;
                                    for (var e = 0; e < edges.Count; e++)
                                        if (edges[e].Origin == EdgeOrigin.None &&
                                            SectionGeometry.CoveredBySharedTopology(edges[e].Curve, original, request.Tolerance))
                                        { edges[e] = edges[e] with { Origin = EdgeOrigin.Original }; recoveredEdges = true; }
                                    if (recoveredEdges && !result.Diagnostics.Any(d => d.Message.StartsWith(RecoveredEdges, StringComparison.Ordinal)))
                                        result.Diagnostics.Add(new(source.Id, SectionStatus.Crossing, $"{RecoveredEdges}; native line confidence requires a separate proof."));
                                    if (edges.FirstOrDefault(e => e.Origin == EdgeOrigin.None) is { } missing)
                                        throw new SectionFailure($"Unattributed visibility trim edge; source quarantined. start={missing.Curve.PointAtStart}; end={missing.Curve.PointAtEnd}; length={missing.Curve.GetLength():R}.");
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
        List<SectionContactFeature>? contactFeatures = null) => Halfspace(id, source, request, diagnostics, out _, contactFeatures);

    /// <param name="shared">When given, <paramref name="source"/> is its component <paramref name="index"/> and the near-plane classification is shared.</param>
    internal static List<Brep> Halfspace(string id, Brep source, SectionRequest request, List<SectionDiagnostic> diagnostics,
        out SectionStatus status, List<SectionContactFeature>? contactFeatures = null, SharedSource? shared = null, int index = 0)
    {
        StableSectionCertificate.Evidence? evidence;
        var classification = shared is not null ? shared.ClassifyNear(id, index, source, out evidence)
            : SectionGeometry.Classify(id, 0, source, request, out evidence);
        status = classification.Status;
        using var certificate = evidence;
        if (classification.Status == SectionStatus.Behind) return new() { source.DuplicateBrep() };
        if (classification.Status == SectionStatus.InFront) return new();
        if (classification.Status == SectionStatus.Uncertain)
        { diagnostics.Add(new(id, SectionStatus.Uncertain, "No trustworthy halfspace certificate; source quarantined. " + classification.Certificate)); return new(); }
        if (classification.Certificate.StartsWith(StableSectionCertificate.Route, StringComparison.Ordinal))
            diagnostics.Add(new(id, SectionStatus.Crossing, classification.Certificate));
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
        if (pieces.Length == 0) pieces = SplitHalfspace(source, request);
        var result = new List<Brep>();
        try
        {
            if (pieces.Length == 0) throw new SectionFailure("Certified crossing produced no trimmed geometry.");
            var retainedPieces = new List<Brep>();
            if (source.IsSolid)
            {
                // Cap all shells together using material regions. Capping each disconnected shell
                // independently would turn an enclosed cavity into an occluding filled solid.
                var loops = certificate?.Loops.Select(c => c.DuplicateCurve()).ToArray() ?? SectionGeometry.Intersections(source, request.Plane, request.Tolerance);
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

    // Rhino 8.36's infinite-plane Trim returns no pieces for valid curved RHS sweeps.
    // Split with a finite plane covering the complete source, then retain only pieces
    // whose entire bounds are on the retained side. No centroid or membership guess.
    // The usual cap, solidity, volume, side and provenance checks remain mandatory.
    private static Brep[] SplitHalfspace(Brep source, SectionRequest request)
    {
        var bounds = source.GetBoundingBox(Transform.PlaneToPlane(request.Plane, Plane.WorldXY));
        var margin = Math.Max(1, bounds.Diagonal.Length) + 16 * request.Tolerance;
        using var surface = new PlaneSurface(request.Plane,
            new(bounds.Min.X - margin, bounds.Max.X + margin), new(bounds.Min.Y - margin, bounds.Max.Y + margin));
        using var cutter = surface.ToBrep();
        var split = source.Split(cutter, request.Tolerance) ?? Array.Empty<Brep>();
        var kept = new List<Brep>();
        try
        {
            foreach (var piece in split)
            {
                var box = piece.GetBoundingBox(Transform.PlaneToPlane(request.Plane, Plane.WorldXY));
                if (box.Max.Z <= request.Tolerance && box.Min.Z < -request.Tolerance) kept.Add(piece);
                else if (!(box.Min.Z >= -request.Tolerance && box.Max.Z > request.Tolerance))
                    throw new SectionFailure("Split halfspace has an ambiguous or straddling piece.");
            }
            return kept.ToArray();
        }
        catch { kept.Clear(); throw; }
        finally { foreach (var piece in split) if (!kept.Contains(piece)) piece.Dispose(); }
    }

    private static double Roundoff(Brep source) => 64 * 2.2204460492503131e-16 *
        Math.Max(1, source.GetBoundingBox(true).Diagonal.Length);
}
