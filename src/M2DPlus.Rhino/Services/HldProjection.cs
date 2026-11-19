using M2DPlus.Core;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace M2DPlus.Rhino.Services;

/// <summary>The only visibility adapter. Failed records stay local; only a failed solve quarantines projection.</summary>
internal static class HldProjection
{
    internal const string Policy = "Rhino HLD threaded/requested-hidden/local-failures/physical-sections/mesh-face-crop-v1/schema4";
    internal const string SkippedSource = "HLD source skipped: ";
    internal const string FailedClip = "Section clip failed: ";
    internal static void Solve(IReadOnlyList<VisibilityInput> inputs, ViewDrawing output, ViewRequest view,
        NativeSettings settings, BoundingBox bounds, CancellationToken token)
    {
        output.MultipleThreads = true; // Policy also applies when no registrations survive.
        if (inputs.Count == 0) return;
        using var phase = output.Phases.Start("native setup");
        using var viewport = new ViewportInfo();
        NativeViewport.Configure(viewport, output.Frame, view.Direction, bounds, output.Tolerance);
        var options = NativeHldOptions.For(view, settings);
        var parameters = new HiddenLineDrawingParameters { AbsoluteTolerance = output.Tolerance, Flatten = false,
            IncludeHiddenCurves = options.IncludeHiddenCurves, IncludeTangentEdges = view.Settings?.Tangents ?? false,
            IncludeTangentSeams = view.Settings?.Seams ?? false };
        parameters.SetViewport(viewport);
        var ordered = VisibilityInputOrder.Native(inputs, i => i, settings);
        var accepted = ordered.Where(input => Register(input, i => parameters.AddGeometry(i.Geometry, i), output, token)).ToArray();
        options.Begin(output, accepted, settings);
        if (accepted.Length == 0) return;
        phase.Next("native solve");
        NativeDrawing.CountHld(); output.NativeCalls++;
        using var drawing = HiddenLineDrawing.Compute(parameters, true, NativeDrawing.HldProgress, token);
        token.ThrowIfCancellationRequested();
        if (!TryFrame(drawing, output, out var inverse)) return;
        var project = Transform.PlanarProjection(output.Frame.Plane) * inverse;
        var parents = new HashSet<int>();
        phase.Next("output construction");
        foreach (var segment in drawing!.Segments)
        {
            token.ThrowIfCancellationRequested();
            Curve? flat = null; Curve? depth = null;
            try
            {
                var visibility = segment.SegmentVisibility;
                if (visibility == HiddenLineDrawingSegment.Visibility.Unset)
                {
                    var evidence = NativeExtractionDiagnostics.Inspect(segment, tag => tag as VisibilityInput, project);
                    if (evidence.Kind == ExtractionKind.EmptySlot) { output.EmptyNativeSlots++; continue; }
                    if (evidence.Kind == ExtractionKind.ZeroLength) { output.ZeroLengthNativeSegments++; continue; }
                    output.RoutingInvalid++;
                    var retained = evidence.Input is { } input && NativeExtractionDiagnostics.TryDetach(segment, inverse, out var world) &&
                        RetainDetachedUnset(output, input, world!);
                    output._diagnostics.Add($"Unset native curve: source={evidence.Source?.Identity ?? "unavailable"}; {evidence.Kind}; {(retained ? "only this curve retained Unresolved" : "record dropped")}; {evidence.Detail}");
                    continue;
                }
                if (!options.IncludeHiddenCurves && visibility == HiddenLineDrawingSegment.Visibility.Hidden) continue;
                if (visibility == HiddenLineDrawingSegment.Visibility.Duplicate) { output.DuplicateSegments++; continue; }
                if (visibility == HiddenLineDrawingSegment.Visibility.Projecting) { output.ProjectingSegments++; continue; }
                if (segment.ParentCurve?.SourceObject?.Tag is not VisibilityInput source || segment.CurveGeometry is not { } proxy)
                { Drop(output, "Native segment dropped: missing source tag or curve."); continue; }
                parents.Add(segment.ParentCurve.Index);
                flat = proxy.DuplicateCurve(); depth = proxy.DuplicateCurve();
                flat.EnsurePrivateCopy(); depth.EnsurePrivateCopy();
                if (!flat.Transform(project) || !depth.Transform(inverse) || !flat.IsValid || !depth.IsValid)
                    throw new InvalidOperationException("Invalid native segment transform/curve.");
                var kind = visibility == HiddenLineDrawingSegment.Visibility.Visible ? LineClass.Visible
                    : visibility == HiddenLineDrawingSegment.Visibility.Hidden ? LineClass.Hidden : LineClass.Unresolved;
                output.Pieces.Add(new(flat, source.Source, kind, depth) { Profile = segment.IsSceneSilhouette, FarCandidate = source.Far });
                flat = null; depth = null; output.NativeFragments++;
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NullReferenceException)
            { Drop(output, "Native segment dropped: " + ex.Message); }
            finally { flat?.Dispose(); depth?.Dispose(); }
        }
        output.NativeEdges = output.CurvedCandidateEdges = parents.Count;
        foreach (var point in drawing.Points)
        {
            token.ThrowIfCancellationRequested();
            var inspection = InspectPoint(() => point.PointVisibility, () => point.SourceObject?.Tag as VisibilityInput, () => point.Location);
            if (inspection.Input is not { } input || !inspection.Location.IsValid)
            { Drop(output, inspection.Diagnostic!); continue; }
            if (inspection.Visibility == HiddenLineDrawingPoint.Visibility.Hidden) { output.HiddenPoints++; continue; }
            var world = inspection.Location; world.Transform(inverse);
            var location = inspection.Location; location.Transform(project);
            if (!world.IsValid || !location.IsValid) { Drop(output, $"{input.Source.Identity}: invalid native point transform; point suppressed."); continue; }
            output.Pieces.Add(new(new Point(location), input.Source, LineClass.Visible, new Point(world)));
        }
    }

    internal static bool Register(VisibilityInput input, Func<VisibilityInput, bool> add, ViewDrawing output, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try { if (add(input)) return true; }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NullReferenceException)
        { output._diagnostics.Add($"{SkippedSource}{input.Source.Identity}; {ex.Message}"); output.RoutingInvalid++; return false; }
        output._diagnostics.Add($"{SkippedSource}{input.Source.Identity}; AddGeometry rejected the source; other sources continue.");
        output.RoutingInvalid++; return false;
    }

    internal sealed record PointInspection(HiddenLineDrawingPoint.Visibility Visibility, VisibilityInput? Input, Point3d Location, string? Diagnostic = null);
    internal static PointInspection InspectPoint(Func<HiddenLineDrawingPoint.Visibility> visibility, Func<VisibilityInput?> source, Func<Point3d> location)
    {
        // Unset proxies may not have a dereferenceable SourceObject. Never read it first.
        try
        {
            var kind = visibility();
            if (kind == HiddenLineDrawingPoint.Visibility.Unset)
                return new(kind, null, Point3d.Unset, "native point Unset; point suppressed before source access.");
            var input = source();
            if (input is null) return new(kind, null, Point3d.Unset, "Source-less native point suppressed.");
            if (kind is not (HiddenLineDrawingPoint.Visibility.Visible or HiddenLineDrawingPoint.Visibility.Hidden))
                return new(kind, null, Point3d.Unset, $"{input.Source.Identity}: native point {kind}; point suppressed.");
            var position = location();
            return new(kind, input, position, position.IsValid ? null : $"{input.Source.Identity}: invalid native point suppressed.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NullReferenceException)
        { return new(HiddenLineDrawingPoint.Visibility.Unset, null, Point3d.Unset, "Native point suppressed: " + ex.Message); }
    }

    /// <summary>Consumes the detached world curve on every path. Unset never spreads to neighbouring records.</summary>
    internal static bool RetainDetachedUnset(ViewDrawing output, VisibilityInput input, Curve world)
    {
        Curve? flat = null; var transferred = false;
        try
        {
            flat = world.DuplicateCurve();
            if (!flat.Transform(Transform.PlanarProjection(output.Frame.Plane)) || !flat.IsValid) return false;
            output.Pieces.Add(new(flat, input.Source, LineClass.Unresolved, world) { FarCandidate = input.Far });
            flat = null; transferred = true; return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NullReferenceException) { return false; }
        finally { flat?.Dispose(); if (!transferred) world.Dispose(); }
    }
    private static void Drop(ViewDrawing output, string diagnostic) { output.RoutingInvalid++; output._diagnostics.Add(diagnostic); }

    internal static bool TryFrame(HiddenLineDrawing? drawing, ViewDrawing output, out Transform inverse)
    {
        inverse = Transform.Unset;
        if (drawing is not null && drawing.WorldToHiddenLine.TryGetInverse(out inverse)) return true;
        output.RoutingQuarantine = true;
        Drop(output, "HLD solve failed: null drawing or unavailable frame; projection quarantined.");
        ApplyFailure(output, true, [], output.Tolerance, default);
        return false;
    }

    // Footprints apply exclusively to participating bodies whose physical clip failed.
    internal static void ApplyFailure(ViewDrawing output, bool quarantine, IReadOnlyList<BoundingBox> failedClips,
        double tolerance, CancellationToken token)
    {
        foreach (var piece in output.Pieces.ToArray())
        {
            token.ThrowIfCancellationRequested();
            using var geometry = piece.CopyGeometry();
            if (!quarantine && !failedClips.Any(b => NativeExtractionDiagnostics.Affects(b, geometry.GetBoundingBox(true), tolerance))) continue;
            if (geometry is Point) { output.Pieces.Remove(piece); piece.Dispose(); }
            else piece.Class = LineClass.Unresolved;
        }
        if (quarantine) output._diagnostics.Add("HLD solve unavailable: projection quarantined; certified sections retained independently.");
    }
}
