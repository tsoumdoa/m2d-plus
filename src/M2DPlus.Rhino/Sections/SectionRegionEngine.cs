using M2DPlus.Core.Sections;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;

namespace M2DPlus.Rhino.Sections;

/// <summary>Detached compute only. No RhinoDoc, scene, scheduler, GH, or visibility calls.</summary>
public static class SectionRegionEngine
{
    /// <summary>Rebuild fill/merge from certified cut regions. Visibility is unaffected by either policy.</summary>
    internal static SectionResult Restyle(IReadOnlyList<SectionRegion> regions, SectionRequest request)
    {
        var result = new SectionResult(); var candidates = new List<Candidate>();
        try
        {
            foreach (var region in regions)
            {
                var style = SectionPolicy.EffectiveStyle(region.Style, request.Hatch);
                if (region.Status == SectionStatus.OutlineOnly)
                {
                    result.Regions.Add(region with { Loops = region.Loops.Select(l => new SectionLoop(l.Curve.DuplicateCurve(), l.IsHole,
                        l.Edges.Select(e => new SectionEdge(e.Curve.DuplicateCurve(), e.Origin)).ToArray())).ToArray(), Hatch = null, Style = style });
                    continue;
                }
                foreach (var face in Brep.CreatePlanarBreps(region.Loops.Select(l => l.Curve), request.Tolerance) ?? throw new SectionFailure("Restyle region failed."))
                    candidates.Add(new(face, region.SourceIds.ToArray(), style,
                        region.Loops.SelectMany(l => l.Edges).Where(e => (e.Origin & EdgeOrigin.PhysicalCut) != 0).Select(e => e.Curve.DuplicateCurve()).ToArray()));
            }
            if (request.Cuts == CutMode.Merge) candidates = Merge(candidates, request, result);
            var ordinal = 0;
            foreach (var candidate in candidates) result.Regions.Add(Build(candidate, $"presentation/{ordinal++}", request));
            return result;
        }
        catch { result.Dispose(); throw; }
        finally { foreach (var candidate in candidates) candidate.Dispose(); }
    }
    private sealed record Candidate(Brep Face, string[] Sources, SectionStyleSnapshot Style, Curve[] Physical) : IDisposable
    {
        public void Dispose() { Face.Dispose(); foreach (var curve in Physical) curve.Dispose(); }
    }

    /// <summary>Worker threads for per-source cutting. Never used for native visibility or for validation. Tests set 1 for a serial run.</summary>
    // Serial by default: RhinoCommon Intersection.CurveCurve is not repeatable under concurrency (perf-f FINDINGS §5).
    // The gate covers the known calls, but the parallel gain is small (L 5.7 s, M plan 1.9 s). Determinism first.
    internal static int Parallelism { get; set; } = 1;
    private static int _liveOutcomes;
    /// <summary>Per-source results not yet merged or disposed; zero whenever no Compute is running.</summary>
    internal static int LiveOutcomes => Volatile.Read(ref _liveOutcomes);

    public static SectionResult Compute(IEnumerable<SectionSource> sources, SectionRequest request,
        CancellationToken cancellationToken = default, bool prepareVisibility = false, Plane? farPlane = null, M2DPlus.Rhino.Services.PhaseTimings? phases = null) =>
        Compute(sources, request, null, cancellationToken, prepareVisibility, farPlane, phases);

    /// <param name="verdicts">The scene's validation verdicts; null shares verdicts within this call only.</param>
    internal static SectionResult Compute(IEnumerable<SectionSource> sources, SectionRequest request, SectionVerdictCache? verdicts,
        CancellationToken cancellationToken, bool prepareVisibility = false, Plane? farPlane = null, M2DPlus.Rhino.Services.PhaseTimings? phases = null)
        => SectionCoordinates.Compute(sources, request, verdicts, cancellationToken, prepareVisibility, farPlane, phases);

    internal static SectionResult ComputeLocal(IEnumerable<SectionSource> sources, SectionRequest request, SectionVerdictCache? verdicts,
        CancellationToken cancellationToken, bool prepareVisibility, Plane? farPlane, M2DPlus.Rhino.Services.PhaseTimings? phases)
    {
        SectionGeometry.Validate(request);
        var ordered = sources.OrderBy(s => s.Id, StringComparer.Ordinal).ToArray();
        if (ordered.Any(s => string.IsNullOrWhiteSpace(s.Id)) || ordered.Select(s => s.Id).Distinct(StringComparer.Ordinal).Count() != ordered.Length)
            throw new ArgumentException("Source ids must be nonempty and unique.");
        var result = new SectionResult();
        var candidates = new List<Candidate>();
        using var transient = verdicts is null ? new SectionVerdictCache() : null;
        try
        {
            // Validation verdicts are settled first, one at a time on this thread, so no verdict can depend on
            // which worker asked first or on what other workers were doing at the time.
            var verdict = new string[ordered.Length];
            var validationTimer = System.Diagnostics.Stopwatch.StartNew();
            for (var i = 0; i < ordered.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                verdict[i] = SectionGeometry.Verdict(ordered[i], request.Tolerance, verdicts ?? transient!);
            }
            phases?.Add("section validation", validationTimer.Elapsed.TotalSeconds); // Nested in sections.
            // Pure geometry on separately owned copies: no document, live object, scheduler or native visibility call.
            SourceOutcome Process(int index)
            {
                var source = ordered[index];
                cancellationToken.ThrowIfCancellationRequested();
                var outcome = new SourceOutcome(source.Id);
                var outlineOrdinals = new Dictionary<string, int>(StringComparer.Ordinal);
                using var shared = new SharedSource(source, request, verdict[index]);
                try
                {
                    var style = SectionPolicy.EffectiveStyle(source.Style, request.Hatch);
                    ValidateStyle(style);
                    if (request.Hatch == HatchMode.Styles && source.Style.Background == BackgroundMode.Viewport)
                        outcome.Diagnostics.Add(new(source.Id, SectionStatus.Crossing, "Viewport background resolved to object display colour."));
                    if (prepareVisibility)
                    {
                        var trimTimer = System.Diagnostics.Stopwatch.StartNew();
                        var visibility = PhysicalSectionClipper.Clip(shared, request, farPlane);
                        if (phases is not null) lock (phases) phases.Add("route C trim", trimTimer.Elapsed.TotalSeconds); // Summed CPU when parallel.
                        outcome.Visibility = visibility;
                        outcome.Diagnostics.AddRange(visibility.Diagnostics);
                    }
                    var components = shared.Components(); // Borrowed: shared with the visibility clip.
                    {
                        for (var i = 0; i < components.Count; i++)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            var component = components[i];
                            var classification = shared.ClassifyNear(source.Id, i, component, out var evidence);
                            using var certificate = evidence;
                            outcome.Components.Add(classification);
                            var stable = classification.Certificate.StartsWith(StableSectionCertificate.Route, StringComparison.Ordinal);
                            if (stable) outcome.Diagnostics.Add(new(source.Id, SectionStatus.Crossing, classification.Certificate));
                            if (classification.Status == SectionStatus.Uncertain)
                                outcome.Diagnostics.Add(new(source.Id, SectionStatus.Uncertain, classification.Certificate));
                            if (classification.Status != SectionStatus.Crossing) continue;
                            var loops = certificate?.Loops.Select(c => c.DuplicateCurve()).ToArray() ?? SectionGeometry.Intersections(component, request.Plane, request.Tolerance);
                            try
                            {
                                if (loops.Length == 0) throw new SectionFailure("Crossing certificate has no transverse intersection.");
                                if (!component.IsSolid)
                                {
                                    AddOutlines(source.Id, loops, style, request, outcome.Outlines, outlineOrdinals);
                                    outcome.Diagnostics.Add(new(source.Id, SectionStatus.OutlineOnly, "Open source: section outline only, Hatch null."));
                                    continue;
                                }
                                if (stable)
                                {
                                    // The slab/winding proof authorizes the planar region independently.
                                    // Visibility preparation retains its own quarantine on any trim failure.
                                }
                                else if (prepareVisibility)
                                {
                                    if (!outcome.Visibility!.Succeeded && !(farPlane is { } fp && -request.Plane.DistanceTo(fp.Origin) <= request.Tolerance))
                                        throw new SectionFailure("Crossing trim/cap could not be certified.");
                                }
                                else
                                {
                                    var checkDiagnostics = new List<SectionDiagnostic>();
                                    var clipped = PhysicalSectionClipper.Halfspace(source.Id, component, request, checkDiagnostics);
                                    foreach (var piece in clipped) piece.Dispose();
                                    if (checkDiagnostics.Count != 0) throw new SectionFailure("Crossing trim/cap could not be certified.");
                                }
                                var regions = SectionGeometry.PlanarRegions(loops, request);
                                try
                                {
                                    foreach (var region in regions)
                                    foreach (var cropped in Crop(region, request))
                                        outcome.Candidates.Add(new(cropped, new[] { source.Id }, style, loops.Select(l => l.DuplicateCurve()).ToArray()));
                                }
                                finally { foreach (var region in regions) region.Dispose(); }
                            }
                            finally { foreach (var loop in loops) loop.Dispose(); }
                        }
                    }
                }
                catch (Exception ex) when (ex is SectionFailure or ArgumentException or InvalidOperationException)
                {
                    outcome.DropRegions();
                    outcome.Diagnostics.Add(new(source.Id, SectionStatus.Unsupported, ex.Message));
                }
                catch { outcome.Dispose(); throw; }
                return outcome;
            }
            var outcomes = new SourceOutcome?[ordered.Length];
            try
            {
                var threads = Math.Min(Parallelism, ordered.Length);
                if (threads > 1)
                    Parallel.For(0, ordered.Length, new ParallelOptions { MaxDegreeOfParallelism = threads, CancellationToken = cancellationToken },
                        i => outcomes[i] = Process(i));
                else for (var i = 0; i < ordered.Length; i++) outcomes[i] = Process(i);
            }
            catch (Exception ex)
            {
                // Parallel.For has joined every worker by now, so no outcome can appear after this sweep.
                foreach (var outcome in outcomes) outcome?.Dispose();
                cancellationToken.ThrowIfCancellationRequested();
                if (ex is AggregateException aggregate)
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(aggregate.Flatten().InnerExceptions[0]).Throw();
                throw;
            }
            // Merge in source order, so serial and parallel runs build identical lists.
            foreach (var outcome in outcomes)
            {
                if (outcome!.Visibility is { } visibility) result.Visibility.Add(outcome.Id, visibility);
                result.Diagnostics.AddRange(outcome.Diagnostics); result.Components.AddRange(outcome.Components);
                result.Regions.AddRange(outcome.Outlines); candidates.AddRange(outcome.Candidates);
                outcome.Transfer();
            }
            cancellationToken.ThrowIfCancellationRequested();
            ReportOverlaps(candidates, request, result);
            if (request.Cuts == CutMode.Merge) candidates = Merge(candidates, request, result);
            var failedSources = new HashSet<string>(StringComparer.Ordinal);
            var ordinals = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var candidate in candidates.OrderBy(c => SectionPolicy.MembershipKey(c.Sources), StringComparer.Ordinal)
                .ThenBy(c => c.Face.GetBoundingBox(Transform.PlaneToPlane(request.Plane, Plane.WorldXY)).Min.X)
                .ThenBy(c => c.Face.GetBoundingBox(Transform.PlaneToPlane(request.Plane, Plane.WorldXY)).Min.Y)
                .ThenBy(c => SectionGeometry.Area(c.Face)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var key = SectionPolicy.MembershipKey(candidate.Sources);
                var ordinal = ordinals.GetValueOrDefault(key);
                ordinals[key] = ordinal + 1;
                try { result.Regions.Add(Build(candidate, $"{key}/region/{ordinal}", request)); }
                catch (Exception ex) when (ex is SectionFailure or ArgumentException or InvalidOperationException)
                {
                    foreach (var sourceId in candidate.Sources) failedSources.Add(sourceId);
                    result.Diagnostics.Add(new(key, SectionStatus.Unsupported, ex.Message));
                }
            }
            for (var i = result.Regions.Count - 1; i >= 0; i--)
                if (result.Regions[i].SourceIds.Any(failedSources.Contains))
                { result.Regions[i].Dispose(); result.Regions.RemoveAt(i); }
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
        catch { result.Dispose(); throw; }
        finally { foreach (var candidate in candidates) candidate.Dispose(); }
    }

    /// <summary>One source's share of the result. Owns its geometry until merged (Transfer) or abandoned (Dispose).</summary>
    private sealed class SourceOutcome
    {
        private int _live = 1;
        public string Id { get; }
        public PhysicalClipResult? Visibility;
        public readonly List<SectionDiagnostic> Diagnostics = new();
        public readonly List<ComponentClassification> Components = new();
        public readonly List<Candidate> Candidates = new();
        public readonly List<SectionRegion> Outlines = new();
        public SourceOutcome(string id) { Id = id; Interlocked.Increment(ref _liveOutcomes); }
        public void DropRegions()
        {
            foreach (var candidate in Candidates) candidate.Dispose(); Candidates.Clear();
            foreach (var outline in Outlines) outline.Dispose(); Outlines.Clear();
        }
        public void Transfer() { if (Interlocked.Exchange(ref _live, 0) == 1) Interlocked.Decrement(ref _liveOutcomes); }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _live, 0) != 1) return;
            Visibility?.Dispose(); DropRegions(); Interlocked.Decrement(ref _liveOutcomes);
        }
    }

    private static void ValidateStyle(SectionStyleSnapshot style)
    {
        if (!double.IsFinite(style.Scale) || style.Scale <= 0 || !double.IsFinite(style.Rotation) ||
            !double.IsFinite(style.PatternOrigin.X) || !double.IsFinite(style.PatternOrigin.Y) ||
            !double.IsFinite(style.Boundary.WidthScale) || style.Boundary.WidthScale <= 0 ||
            style.Boundary.LinetypeSegments.Any(d => !double.IsFinite(d)))
            throw new SectionFailure("Invalid resolved hatch scale, rotation or origin.");
        if (style.Pattern is { Solid: false } pattern && (pattern.Lines.Count == 0 || pattern.Lines.Any(l =>
            !double.IsFinite(l.Angle) || !double.IsFinite(l.Base.X) || !double.IsFinite(l.Base.Y) ||
            !double.IsFinite(l.Offset.X) || !double.IsFinite(l.Offset.Y) || l.Offset.X == 0 && l.Offset.Y == 0 ||
            l.Dashes.Any(d => !double.IsFinite(d))))) throw new SectionFailure("Invalid resolved pattern definition.");
    }

    private static List<Brep> Crop(Brep region, SectionRequest request)
    {
        if (request.Crop is not { } rectangle) return new() { region.DuplicateBrep() };
        using var crop = rectangle.ToNurbsCurve();
        using var split = region.Faces[0].Split(new[] { crop }, request.Tolerance);
        if (split is null) throw new SectionFailure("Region-level crop split failed.");
        var result = new List<Brep>();
        try
        {
            foreach (var face in split.Faces)
            {
                var piece = face.DuplicateFace(false);
                if (crop.Contains(SectionGeometry.Interior(piece, request.Tolerance), request.Plane, request.Tolerance) == PointContainment.Outside)
                { piece.Dispose(); continue; }
                var area = SectionGeometry.Area(piece);
                if (area <= request.Tolerance * request.Tolerance) { piece.Dispose(); throw new SectionFailure("Crop produced a tolerance-size sliver."); }
                result.Add(piece);
            }
            var originalArea = SectionGeometry.Area(region);
            if (result.Sum(SectionGeometry.Area) > originalArea + SectionGeometry.AreaError(originalArea, request.Tolerance))
                throw new SectionFailure("Crop increased material area.");
            return result;
        }
        catch { foreach (var piece in result) piece.Dispose(); throw; }
    }

    private static SectionRegion Build(Candidate candidate, string id, SectionRequest request)
    {
        var loops = new List<SectionLoop>();
        using var crop = request.Crop?.ToNurbsCurve();
        var references = candidate.Physical.Select(c => (c, EdgeOrigin.PhysicalCut)).ToList();
        if (crop is not null) references.Add((crop, EdgeOrigin.Crop));
        try
        {
            foreach (var loop in candidate.Face.Loops.OrderBy(l => l.LoopType == BrepLoopType.Outer ? 0 : 1))
            {
                var curve = loop.To3dCurve();
                var segments = curve.DuplicateSegments();
                var edges = new List<SectionEdge>();
                // These faces are constructed exclusively from physical loops, crop, and
                // their planar arrangement. Unmatched intervals inherit physical ancestry;
                // native periodic reparameterization need not reproduce a CurveCurve overlap.
                // Crop matches still override that ancestry (and retain dual origins).
                try { foreach (var segment in segments) edges.AddRange(EdgeProvenance.Split(segment, references, request.Tolerance, EdgeOrigin.PhysicalCut)); }
                finally { foreach (var segment in segments) segment.Dispose(); }
                if (edges.Any(e => e.Origin == EdgeOrigin.None))
                { curve.Dispose(); foreach (var edge in edges) edge.Dispose(); throw new SectionFailure("Unattributed section boundary interval."); }
                loops.Add(new(curve, loop.LoopType == BrepLoopType.Inner, edges));
            }
            return new(id, loops, SectionHatches.Build(candidate.Face, candidate.Style, request.Plane), candidate.Sources,
                candidate.Style, SectionStatus.Crossing, SectionGeometry.Area(candidate.Face), null);
        }
        catch { foreach (var loop in loops) loop.Dispose(); throw; }
    }

    private static void AddOutlines(string id, IEnumerable<Curve> curves, SectionStyleSnapshot style,
        SectionRequest request, List<SectionRegion> target, Dictionary<string, int> ordinals)
    {
        using var crop = request.Crop?.ToNurbsCurve();
        foreach (var curve in curves)
        {
            var parts = new[] { curve.DuplicateCurve() };
            if (crop is not null)
            {
                double[] parameters;
                lock (SectionGeometry.CurveIntersectionGate)
                {
                    using var events = Intersection.CurveCurve(curve, crop, request.Tolerance, request.Tolerance);
                    parameters = events.SelectMany(e => e.IsOverlap ? new[] { e.OverlapA.T0, e.OverlapA.T1 } : new[] { e.ParameterA }).ToArray();
                }
                foreach (var p in parts) p.Dispose();
                parts = curve.Split(parameters) ?? new[] { curve.DuplicateCurve() };
            }
            foreach (var part in parts)
            {
                if (crop is not null && crop.Contains(part.PointAtNormalizedLength(.5), request.Plane, request.Tolerance) == PointContainment.Outside)
                { part.Dispose(); continue; }
                var ordinal = ordinals.GetValueOrDefault(id);
                ordinals[id] = ordinal + 1;
                target.Add(new($"{SectionPolicy.MembershipKey(new[] { id })}/outline/{ordinal}",
                    new[] { new SectionLoop(part, false, new[] { new SectionEdge(part.DuplicateCurve(), EdgeOrigin.PhysicalCut) }) },
                    null, new[] { id }, style, SectionStatus.OutlineOnly, 0, "Open geometry has no material fill."));
            }
        }
    }

    // Build all atomic cells, select those lying in source material, then combine the selected cells.
    // This preserves holes even where another object's outer loop crosses them.
    private static List<Candidate> Merge(List<Candidate> candidates, SectionRequest request, SectionResult result)
    {
        var groups = new List<List<Candidate>>();
        foreach (var candidate in candidates)
        {
            var group = groups.FirstOrDefault(g => SectionPolicy.Compatible(g[0].Style, candidate.Style));
            if (group is null) groups.Add(new() { candidate }); else group.Add(candidate);
        }
        var merged = new List<Candidate>();
        try
        {
            foreach (var group in groups)
            {
                if (group.Count == 1)
                { var c = group[0]; merged.Add(new(c.Face.DuplicateBrep(), c.Sources, c.Style, c.Physical.Select(p => p.DuplicateCurve()).ToArray())); continue; }
                var boundaries = group.SelectMany(c => c.Face.Loops.Select(l => l.To3dCurve())).ToArray();
                try
                {
                    using var atomic = Curve.CreateBooleanRegions(boundaries, request.Plane, false, request.Tolerance);
                    if (atomic is null) throw new SectionFailure("Style-compatible union arrangement failed.");
                    var seeds = new List<Point3d>();
                    for (var i = 0; i < atomic.RegionCount; i++)
                    {
                        var loops = atomic.RegionCurves(i);
                        try
                        {
                            var faces = SectionGeometry.PlanarRegions(loops, request);
                            try { foreach (var face in faces) { var point = SectionGeometry.Interior(face, request.Tolerance); if (group.Any(c => SectionGeometry.Contains(c.Face, point, request.Tolerance))) seeds.Add(point); } }
                            finally { foreach (var face in faces) face.Dispose(); }
                        }
                        finally { foreach (var curve in loops) curve.Dispose(); }
                    }
                    using var union = Curve.CreateBooleanRegions(boundaries, request.Plane, seeds, true, request.Tolerance);
                    if (union is null || union.RegionCount == 0) throw new SectionFailure("Style-compatible region union failed.");
                    var groupStart = merged.Count;
                    for (var i = 0; i < union.RegionCount; i++)
                    {
                        var loops = union.RegionCurves(i);
                        try
                        {
                            var faces = SectionGeometry.PlanarRegions(loops, request);
                            foreach (var face in faces)
                            {
                                var members = group.Where(c => seeds.Any(p => SectionGeometry.Contains(face, p, request.Tolerance) && SectionGeometry.Contains(c.Face, p, request.Tolerance))).ToArray();
                                var ids = members.SelectMany(c => c.Sources).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
                                if (ids.Length == 0) { face.Dispose(); throw new SectionFailure("Union lost source membership."); }
                                merged.Add(new(face, ids, group[0].Style, members.SelectMany(c => c.Physical).Select(c => c.DuplicateCurve()).ToArray()));
                            }
                        }
                        finally { foreach (var curve in loops) curve.Dispose(); }
                    }
                    var area = merged.Skip(groupStart).Sum(c => SectionGeometry.Area(c.Face));
                    var inputArea = group.Sum(c => SectionGeometry.Area(c.Face));
                    if (area > inputArea + SectionGeometry.AreaError(inputArea, request.Tolerance) ||
                        area < group.Max(c => SectionGeometry.Area(c.Face)) - SectionGeometry.AreaError(inputArea, request.Tolerance))
                        throw new SectionFailure("Union area bounds failed.");
                }
                finally { foreach (var curve in boundaries) curve.Dispose(); }
            }
            foreach (var candidate in candidates) candidate.Dispose();
            return merged;
        }
        catch (SectionFailure ex)
        {
            foreach (var candidate in merged) candidate.Dispose();
            result.Diagnostics.Add(new("*", SectionStatus.Unsupported, ex.Message));
            return candidates; // Explicitly diagnosed separate output; caller must respect RequiresQuarantine.
        }
    }

    private static void ReportOverlaps(List<Candidate> candidates, SectionRequest request, SectionResult result)
    {
        // Regions whose bounds on the cut plane are more than tolerance apart cannot overlap.
        var toPlane = Transform.PlaneToPlane(request.Plane, Plane.WorldXY);
        var bounds = candidates.Select(c => c.Face.GetBoundingBox(toPlane)).ToArray();
        for (var i = 0; i < candidates.Count; i++)
        for (var j = i + 1; j < candidates.Count; j++)
        {
            var a = candidates[i]; var b = candidates[j];
            if (bounds[i].Min.X > bounds[j].Max.X + request.Tolerance || bounds[j].Min.X > bounds[i].Max.X + request.Tolerance ||
                bounds[i].Min.Y > bounds[j].Max.Y + request.Tolerance || bounds[j].Min.Y > bounds[i].Max.Y + request.Tolerance) continue;
            if (SectionPolicy.Compatible(a.Style, b.Style)) continue;
            var aLoops = a.Face.Loops.Select(l => l.To3dCurve()).ToArray();
            var bLoops = b.Face.Loops.Select(l => l.To3dCurve()).ToArray();
            try
            {
                var overlap = false;
                using var arrangement = Curve.CreateBooleanRegions(aLoops.Concat(bLoops), request.Plane, false, request.Tolerance);
                if (arrangement is null) throw new SectionFailure("Cannot classify incompatible fill overlap.");
                for (var cell = 0; cell < arrangement.RegionCount && !overlap; cell++)
                {
                    var loops = arrangement.RegionCurves(cell);
                    try
                    {
                        var faces = SectionGeometry.PlanarRegions(loops, request);
                        try
                        {
                            overlap = faces.Any(face => {
                                var p = SectionGeometry.Interior(face, request.Tolerance);
                                return SectionGeometry.Contains(a.Face, p, request.Tolerance) && SectionGeometry.Contains(b.Face, p, request.Tolerance);
                            });
                        }
                        finally { foreach (var face in faces) face.Dispose(); }
                    }
                    finally { foreach (var loop in loops) loop.Dispose(); }
                }
                if (overlap) result.Diagnostics.Add(new(SectionPolicy.MembershipKey(a.Sources.Concat(b.Sources)),
                    SectionStatus.Crossing, "Info: overlapping incompatible fills retained separately in stable source order."));
            }
            catch (SectionFailure ex) { result.Diagnostics.Add(new(SectionPolicy.MembershipKey(a.Sources.Concat(b.Sources)), SectionStatus.Uncertain, ex.Message)); }
            finally { foreach (var c in aLoops.Concat(bLoops)) c.Dispose(); }
        }
    }
}

