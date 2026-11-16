using Rhino.Geometry;

namespace M2DPlus.Rhino.Sections;

/// <summary>Owned rigid translations around the section plane; no tolerance, certificate or uncertainty policy changes.</summary>
internal static class SectionCoordinates
{
    internal static SectionResult Compute(IEnumerable<SectionSource> sources, SectionRequest request, SectionVerdictCache? verdicts,
        CancellationToken token, bool prepareVisibility, Plane? farPlane, Services.PhaseTimings? phases)
    {
        SectionGeometry.Validate(request);
        var origin = request.Plane.Origin;
        var toLocal = Transform.Translation(-origin.X, -origin.Y, -origin.Z);
        var toWorld = Transform.Translation(origin.X, origin.Y, origin.Z);
        var owned = new List<GeometryBase>();
        try
        {
            var localSources = sources.Select(source =>
            {
                token.ThrowIfCancellationRequested();
                CheckPrecision(source.Geometry.GetBoundingBox(true), request.Tolerance);
                var geometry = source.Geometry.Duplicate(); owned.Add(geometry);
                geometry.EnsurePrivateCopy();
                if (!geometry.Transform(toLocal)) throw new SectionFailure("Cannot translate section source to local coordinates.");
                return source with { Geometry = geometry };
            }).ToArray();
            var plane = request.Plane; plane.Transform(toLocal);
            var crop = request.Crop; if (crop is { } rectangle) { rectangle.Transform(toLocal); crop = rectangle; }
            if (farPlane is { } far) { far.Transform(toLocal); farPlane = far; }
            var result = SectionRegionEngine.ComputeLocal(localSources, request with { Plane = plane, Crop = crop }, verdicts, token, prepareVisibility, farPlane, phases);
            try
            {
                // Each published geometry has a single owner. Transform both the loop and its
                // independently owned provenance copies; never infer an edge from moved samples.
                void Restore(GeometryBase geometry)
                {
                    if (!geometry.Transform(toWorld)) throw new SectionFailure("Cannot restore section result to world coordinates.");
                }
                foreach (var region in result.Regions)
                {
                    foreach (var loop in region.Loops) { Restore(loop.Curve); foreach (var edge in loop.Edges) Restore(edge.Curve); }
                    if (region.Hatch is { } hatch) Restore(hatch);
                }
                foreach (var visibility in result.Visibility.Values)
                {
                    foreach (var piece in visibility.Pieces) { Restore(piece.Geometry); foreach (var edge in piece.Edges) Restore(edge.Curve); }
                    foreach (var feature in visibility.ContactFeatures) Restore(feature.Geometry);
                }
                return result;
            }
            catch { result.Dispose(); throw; }
        }
        finally { foreach (var geometry in owned) geometry.Dispose(); }
    }

    internal static Brep ValidationCopy(Brep source, double tolerance)
    {
        var bounds = source.GetBoundingBox(true);
        if (!bounds.IsValid) throw new SectionFailure("Invalid bounds for local section validation.");
        var centre = bounds.Center;
        CheckPrecision(bounds, tolerance);
        var local = source.DuplicateBrep();
        try
        {
            if (!local.Transform(Transform.Translation(-centre.X, -centre.Y, -centre.Z)) || !local.IsValid)
                throw new SectionFailure("Cannot translate section validation geometry to local coordinates.");
            return local;
        }
        catch { local.Dispose(); throw; }
    }

    private static void CheckPrecision(BoundingBox bounds, double tolerance)
    {
        // Translation cannot recover precision already lost in the supplied coordinates.
        // Refuse that arithmetic domain rather than making a coarse source look precise.
        var magnitude = Math.Max(Math.Max(Math.Abs(bounds.Min.X), Math.Abs(bounds.Max.X)),
            Math.Max(Math.Max(Math.Abs(bounds.Min.Y), Math.Abs(bounds.Max.Y)), Math.Max(Math.Abs(bounds.Min.Z), Math.Abs(bounds.Max.Z))));
        if (!double.IsFinite(magnitude) || 128 * 2.2204460492503131e-16 * magnitude > tolerance / 64)
            throw new SectionFailure("Coordinate precision is insufficient for local section validation at this tolerance.");
    }
}
