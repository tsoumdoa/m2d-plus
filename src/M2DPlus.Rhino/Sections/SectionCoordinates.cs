using Rhino.Geometry;

namespace M2DPlus.Rhino.Sections;

/// <summary>Owned rigid translations around the section plane; no tolerance, certificate or uncertainty policy changes.</summary>
internal static class SectionCoordinates
{
    internal static SectionResult Restyle(IReadOnlyList<SectionRegion> regions, SectionRequest request,
        Func<Curve[], Plane, double, CurveBooleanRegions?>? arrangement = null)
    {
        var bounds = BoundingBox.Empty;
        foreach (var loop in regions.SelectMany(r => r.Loops)) bounds.Union(loop.Curve.GetBoundingBox(true));
        var origin = Origin(request.Plane, bounds);
        var toLocal = Transform.Translation(-origin.X, -origin.Y, -origin.Z);
        var toWorld = Transform.Translation(origin.X, origin.Y, origin.Z);
        var owned = new List<SectionRegion>();
        SectionResult? result = null;
        try
        {
            foreach (var region in regions)
            {
                foreach (var loop in region.Loops) CheckPrecision(loop.Curve.GetBoundingBox(true), request.Tolerance);
                owned.Add(CopyRegion(region, toLocal));
            }
            result = SectionRegionEngine.RestyleLocal(owned, LocalRequest(request, origin), arrangement);
            Restore(result, toWorld);
            return result;
        }
        catch (Exception ex) when (ex is SectionFailure or ArgumentException or InvalidOperationException)
        {
            result?.Dispose();
            var refused = new SectionResult();
            try
            {
                foreach (var region in regions) SectionRegionEngine.RefuseRestyle(refused, region, ex.Message);
                return refused;
            }
            catch { refused.Dispose(); throw; }
        }
        finally { foreach (var region in owned) region.Dispose(); }
    }

    // Restyle only borrows the input loops/provenance. Hatches are rebuilt from
    // the requested style; their plane-relative pattern origin stays unchanged.
    internal static SectionRegion CopyRegion(SectionRegion region, Transform transform)
    {
        var owned = new List<Curve>();
        Curve Copy(Curve curve)
        {
            var copy = curve.DuplicateCurve(); owned.Add(copy); copy.EnsurePrivateCopy();
            if (!copy.Transform(transform)) throw new SectionFailure("Cannot translate section presentation geometry.");
            return copy;
        }
        try
        {
            return region with { Loops = region.Loops.Select(l => new SectionLoop(Copy(l.Curve), l.IsHole,
                l.Edges.Select(e => new SectionEdge(Copy(e.Curve), e.Origin)).ToArray())).ToArray(), Hatch = null };
        }
        catch { foreach (var curve in owned) curve.Dispose(); throw; }
    }

    internal static SectionResult Compute(IEnumerable<SectionSource> sources, SectionRequest request, SectionVerdictCache? verdicts,
        CancellationToken token, bool prepareVisibility, Plane? farPlane, Services.PhaseTimings? phases)
    {
        SectionGeometry.Validate(request);
        var input = sources.ToArray();
        var bounds = BoundingBox.Empty;
        foreach (var source in input) bounds.Union(source.Geometry.GetBoundingBox(true));
        var origin = Origin(request.Plane, bounds);
        var toLocal = Transform.Translation(-origin.X, -origin.Y, -origin.Z);
        var toWorld = Transform.Translation(origin.X, origin.Y, origin.Z);
        var owned = new List<GeometryBase>();
        try
        {
            var localSources = input.Select(source =>
            {
                token.ThrowIfCancellationRequested();
                CheckPrecision(source.Geometry.GetBoundingBox(true), request.Tolerance);
                var geometry = source.Geometry.Duplicate(); owned.Add(geometry);
                geometry.EnsurePrivateCopy();
                if (!geometry.Transform(toLocal)) throw new SectionFailure("Cannot translate section source to local coordinates.");
                return source with { Geometry = geometry };
            }).ToArray();
            if (farPlane is { } far) { far.Origin = far.ClosestPoint(origin); far.Transform(toLocal); farPlane = far; }
            var result = SectionRegionEngine.ComputeLocal(localSources, LocalRequest(request, origin), verdicts, token, prepareVisibility, farPlane, phases);
            try
            {
                Restore(result, toWorld);
                return result;
            }
            catch { result.Dispose(); throw; }
        }
        finally { foreach (var geometry in owned) geometry.Dispose(); }
    }

    internal static Point3d Origin(Plane plane, BoundingBox bounds) => bounds.IsValid ? plane.ClosestPoint(bounds.Center) : plane.Origin;

    internal static SectionRequest LocalRequest(SectionRequest request, Point3d origin)
    {
        var move = Transform.Translation(-origin.X, -origin.Y, -origin.Z);
        var pattern = request.PatternPlane ?? request.Plane; pattern.Transform(move);
        var plane = new Plane(Point3d.Origin, request.Plane.XAxis, request.Plane.YAxis);
        Rectangle3d? crop = null;
        if (request.Crop is { } rectangle)
        {
            // Reparameterize the rectangle as well as moving its geometry. A
            // distant rectangle origin must not reintroduce large coordinates.
            var anchor = rectangle.Plane.ClosestPoint(origin);
            rectangle.Plane.ClosestParameter(anchor, out var u, out var v);
            var cropPlane = rectangle.Plane; cropPlane.Origin = anchor; cropPlane.Transform(move);
            crop = new Rectangle3d(cropPlane, new Interval(rectangle.X.T0-u, rectangle.X.T1-u), new Interval(rectangle.Y.T0-v, rectangle.Y.T1-v));
        }
        return request with { Plane = plane, Crop = crop, PatternPlane = pattern };
    }

    private static void Restore(SectionResult result, Transform toWorld)
    {
        // Loops and provenance copies have independent owners; restore both.
        void Move(GeometryBase geometry)
        {
            if (!geometry.Transform(toWorld)) throw new SectionFailure("Cannot restore section result to world coordinates.");
        }
        foreach (var region in result.Regions)
        {
            foreach (var loop in region.Loops) { Move(loop.Curve); foreach (var edge in loop.Edges) Move(edge.Curve); }
            if (region.Hatch is { } hatch) Move(hatch);
        }
        foreach (var visibility in result.Visibility.Values)
        {
            foreach (var piece in visibility.Pieces) { Move(piece.Geometry); foreach (var edge in piece.Edges) Move(edge.Curve); }
            foreach (var feature in visibility.ContactFeatures) Move(feature.Geometry);
        }
        foreach (var refusal in result.Refusals) Move(refusal.Geometry);
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
