using System.Diagnostics;
using M2DPlus.Core;
using M2DPlus.Core.Sections;
using M2DPlus.Rhino.Sections;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;
using Point = Rhino.Geometry.Point;

namespace M2DPlus.Rhino.Services;

public sealed record DrawingRegion(SectionRegion Region, int[] SourcePath)
{
    public int SourceItem { get; init; }
    internal SourceSection Source { get; init; } = SourceSection.Plain;
}

/// <summary>One route C solve. Certified clips remain owned through native extraction, including points.</summary>
internal static class SectionDrawing
{
    private sealed record Candidate(PreparedLeaf Source, bool Far = false);

    internal static ViewDrawing Solve(PreparedScene scene, IReadOnlyList<PreparedLeaf> sources,
        ViewRequest view, NativeSettings settings, CancellationToken token, Rectangle3d? resolvedFrame = null)
    {
        using var work = new SectionWork();
        var timer = Stopwatch.StartNew(); var invalid = 0; var quarantine = false;
        var failedClipFootprints = new List<BoundingBox>();
        SectionResult? preparedSections = null;
        var result = new ViewDrawing { Name = view.Name, Settings = view.Settings, Tolerance = view.Settings?.Tolerance ?? scene.Tolerance, MultipleThreads = settings.MultipleThreads,
            Frame = resolvedFrame ?? DrawingFrames.Resolve(view, sources, scene.Bounds, view.Settings?.Tolerance ?? scene.Tolerance) };
        var cut = view.Cut!; var tol = view.Settings?.Tolerance ?? scene.Tolerance;
        var request = new SectionRequest(cut.Plane, tol, .001, cut.Crop, CutMode.Separate, HatchMode.Styles);
        var autoDepth = scene.Bounds.IsValid ? Math.Max(0, -scene.Bounds.GetCorners().Min(cut.Plane.DistanceTo)) + tol * 10 : tol * 10;
        var range = new ViewRange(cut.SourceDepth ?? autoDepth, farClip: cut.FarClip, tolerance: tol);
        Plane? far = range.ClipsFar ? new Plane(cut.Plane.Origin - cut.Plane.Normal * range.Depth, -cut.Plane.XAxis, cut.Plane.YAxis) : null;
        var participating = sources.Where(cut.Participates).ToArray();
        // A separated source needs no section topology/provenance certificate.
        // Keep it on the ordinary projection route, or exclude it when wholly
        // outside the retained halfspaces. An uncertain/touching bound, including
        // a crossing of the far plane, still takes the full section pipeline.
        var bodyRelations = participating.Where(s => s.Geometry is Brep or Mesh)
            .ToDictionary(s => s.Identity, s => SectionProjectionBounds.Classify(s.Geometry, cut.Plane, far, tol), StringComparer.Ordinal);
        var bodies = participating.Where(s => bodyRelations.GetValueOrDefault(s.Identity) == SectionProjectionRelation.Section &&
            s.Geometry is Brep or Mesh).ToArray();
        var owned = new List<GeometryBase>();
        var suppression = new List<Curve>(); var cutLoci = new List<Curve>(); var farLoci = new List<Curve>();
        var sourceTrim = new Dictionary<string, List<Curve>>(StringComparer.Ordinal);
        var sourceFar = new Dictionary<string, List<Curve>>(StringComparer.Ordinal);
        using var cutIndex = new RTree();
        IEnumerable<Curve> NearbyCuts(Curve curve)
        {
            var bounds = curve.GetBoundingBox(true); bounds.Inflate(2 * tol);
            var hits = new List<int>(); cutIndex.Search(bounds, (_, e) => hits.Add(e.Id)); hits.Sort();
            return hits.Select(i => cutLoci[i]); // Same reference order as the full scan.
        }
        var registered = new List<(GeometryBase Geometry, Candidate Tag)>();
        string route = "native HLD (in process)";
        try
        {
            using var phase = result.Phases.Start("sections");
            var sections = preparedSections = SectionPreparation.Compute(bodies.Select(s => new SectionSource(s.Identity, s.Geometry, cut.Style(s)) { Canonical = s.Canonical }),
                request, scene.SectionVerdicts, token, far, result.Phases);
            result.Phases.Add("sections", -result.Phases.Seconds.GetValueOrDefault("route C trim"));
            result._diagnostics.Add($"Section refusals={sections.Refusals.Count}; independently certified regions={sections.Regions.Count}.");
            result.UncertainSectionComponents = sections.Components.Count(c => c.Status == SectionStatus.Uncertain);
            if (view.Settings?.CutOnly != true && range.Regime == DepthRegime.Volume)
                failedClipFootprints.AddRange(SectionPreparation.FailedClipFootprints(bodies, sections, cut.Plane, result));
            result._diagnostics.Add($"Admission: {scene.Cost.GeometryBytes} geometry bytes; {scene.Cost.BrepFaces} faces; {scene.Cost.MeshTriangles} triangles; {scene.SourceCount} sources.");
            result._diagnostics.Add($"Route C; depth={range.Depth:R}; farclip={range.FarClip}; regime={range.Regime}; cuts={request.Cuts}; hatch={request.Hatch}; no native clipping planes.");
            result._diagnostics.AddRange(sections.Diagnostics.Select(d => $"{d.SourceId}: {d.Status}: {d.Message}"));
            result._diagnostics.AddRange(bodies.Select(s => $"Style {s.Identity}: {s.Section.Rule}; raw ARGB={cut.Style(s).PatternColour:x8}; source={cut.Style(s).ObjectColour:x8}"));
            var byId = sources.GroupBy(s => s.Identity).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            foreach (var region in sections.Regions.ToArray())
            {
                var source = region.SourceIds.Select(id => byId[id]).OrderBy(s => s.Path, Comparer<int[]>.Create((a,b) => { for (var i=0;i<Math.Min(a.Length,b.Length);i++) { var c=a[i].CompareTo(b[i]); if(c!=0)return c; } return a.Length.CompareTo(b.Length); })).First();
                // Whole excluded foreground occluders can cover regions. Preserve full region topology only
                // when its projected footprint is disjoint; ambiguous presentation is explicitly unresolved.
                var blocked = sources.Where(s => !cut.Participates(s) && !(view.Settings?.Glass == GlassMode.Clear && s.Section.Transparent) && s.Geometry is Brep or Mesh)
                    .Any(s => SectionPreparation.ProjectedOverlap(s.Geometry, region, cut.Plane, tol));
                foreach (var loop in region.Loops)
                {
                    var locus = loop.Curve.DuplicateCurve();
                    cutIndex.Insert(locus.GetBoundingBox(true), cutLoci.Count); cutLoci.Add(locus);
                }
                if (blocked)
                {
                    foreach (var loop in region.Loops) result.Pieces.Add(new(loop.Curve.DuplicateCurve(), source, LineClass.Unresolved));
                    result._diagnostics.Add($"{source.Identity}: section presentation overlaps an excluded foreground occluder; region/Hatch withheld, outline Unresolved.");
                    continue;
                }
                result.Regions.Add(new(region, (int[])source.Path.Clone()) { Source = source.Section, SourceItem = source.SourceItem }); sections.Regions.Remove(region);
            }

            // Keep section certificates, validation and region ownership above unchanged.
            // Refused-cut markers and cut uncertainty belong before this projection boundary.
            if (view.Settings?.CutOnly == true || range.Regime != DepthRegime.Volume)
            {
                route = "sections";
                if (view.Settings?.CutOnly == true)
                {
                    if (result.Regions.Count == 0 && result.Pieces.Count == 0 && sections.Refusals.Count == 0)
                        result._diagnostics.Add(ViewNotices.CutOnlyEmpty);
                }
                else result._diagnostics.Add(range.Regime == DepthRegime.SectionOnly ? ViewNotices.ZeroDepth : ViewNotices.ThinDepth);
                return result;
            }

            foreach (var source in sources)
            {
                token.ThrowIfCancellationRequested();
                if (!cut.Participates(source)) { registered.Add((source.Geometry, new(source))); continue; }
                if (source.Geometry is Brep or Mesh)
                {
                    if (bodyRelations[source.Identity] == SectionProjectionRelation.Excluded) continue;
                    if (bodyRelations[source.Identity] == SectionProjectionRelation.Projection)
                    { registered.Add((source.Geometry, new(source))); continue; }
                    if (!sections.Visibility.TryGetValue(source.Identity, out var clip) || !clip.Succeeded) continue;
                    foreach (var piece in clip.Pieces)
                    {
                        registered.Add((piece.Geometry, new(source)));
                        foreach (var edge in piece.Edges.Where(e => (e.Origin & EdgeOrigin.Original) == 0))
                        { var projected = edge.Curve.DuplicateCurve(); projected.Transform(Transform.PlanarProjection(cut.Plane)); suppression.Add(projected);
                            if (!sourceTrim.TryGetValue(source.Identity, out var trims)) sourceTrim[source.Identity] = trims = new();
                            trims.Add(projected);
                            if ((edge.Origin & EdgeOrigin.FarTrim) != 0)
                            {
                                var farCurve = projected.DuplicateCurve(); farLoci.Add(farCurve);
                                if (!sourceFar.TryGetValue(source.Identity, out var farCurves)) sourceFar[source.Identity] = farCurves = new();
                                farCurves.Add(farCurve);
                            } }
                    }
                    foreach (var feature in clip.ContactFeatures) registered.Add((feature.Geometry, new(source)));
                    if (range.GenerateFarClipLines && far is { } farPlane && sourceFar.ContainsKey(source.Identity))
                    {
                        using var original = SectionGeometry.Convert(source.Geometry, tol);
                        foreach (var curve in SectionGeometry.Intersections(original, farPlane, tol))
                        { owned.Add(curve); registered.Add((curve, new(source, true))); }
                    }
                }
                else if (range.Regime == DepthRegime.Volume)
                {
                    if (source.Geometry is Curve curve)
                        foreach (var retained in SectionPreparation.ClipCurve(curve, cut.Plane, far, tol))
                        { owned.Add(retained); registered.Add((retained, new(source))); }
                    else if (source.Geometry is Point point && cut.Plane.DistanceTo(point.Location) <= 0 &&
                        (far is null || far.Value.DistanceTo(point.Location) <= 0)) registered.Add((point, new(source)));
                }
            }
            if (view.Settings?.Glass == GlassMode.Clear)
            {
                foreach (var entry in registered.Where(p => p.Tag.Source.Section.Transparent && p.Geometry is Brep or Mesh).ToArray())
                {
                    registered.Remove(entry);
                    foreach (var curve in GlassGeometry.Features(entry.Geometry, view))
                    { owned.Add(curve); registered.Add((curve, entry.Tag)); }
                }
            }
            phase.Next("section output construction");
            {
                phase.Pause();
                using var routed = new ViewDrawing { Frame = result.Frame, Tolerance = tol, Settings = view.Settings };
                HldProjection.Solve(registered.Select(r => new VisibilityInput(r.Geometry, r.Tag.Source, r.Tag.Far)).ToArray(), routed, view, settings, scene.Bounds, token);
                phase.Next("section output construction");
                result.Phases.Include(routed.Phases);
                result.NativeEdges = routed.NativeEdges; result.NativeCalls = routed.NativeCalls;
                result.MultipleThreads = routed.MultipleThreads;
                result.CurvedCandidateEdges = routed.CurvedCandidateEdges; result.NativeFragments = routed.NativeFragments;
                result.HiddenPoints += routed.HiddenPoints; result.DuplicateSegments += routed.DuplicateSegments;
                result.EmptyNativeSlots += routed.EmptyNativeSlots; result.ZeroLengthNativeSegments += routed.ZeroLengthNativeSegments;
                result.ProjectingSegments += routed.ProjectingSegments; invalid += routed.RoutingInvalid;
                quarantine |= routed.RoutingQuarantine;
                result._diagnostics.AddRange(routed.Diagnostics);
                foreach (var item in routed.Items)
                {
                    token.ThrowIfCancellationRequested();
                    var source = byId[item.SourceIdentity];
                    using var geometry = item.CopyGeometry();
                    if (geometry is Point point)
                    {
                        if (cut.Crop is not { } pc || NativeDrawing.Inside(point.Location, pc, tol))
                            result.Pieces.Add(new(point.Duplicate(), source, item.Class, item.DepthGeometry?.Duplicate()));
                        continue;
                    }
                    if (geometry is not Curve curve) continue;
                    if (item.FarCandidate && item.Class != LineClass.Visible) continue;
                    var farParts = EdgeProvenance.Split(curve, (sourceFar.GetValueOrDefault(source.Identity) ?? Enumerable.Empty<Curve>()).Select(c => (c, EdgeOrigin.FarTrim)), tol);
                    foreach (var farPart in farParts)
                    {
                        using (farPart)
                        {
                            var isFar = (farPart.Origin & EdgeOrigin.FarTrim) != 0;
                            if (item.FarCandidate && !isFar) continue;
                            if (isFar && (!range.GenerateFarClipLines || item.Class != LineClass.Visible)) continue;
                            var kind = isFar ? LineClass.FarClip : item.Class;
                            var nearby = NearbyCuts(farPart.Curve);
                            IEnumerable<Curve> references = isFar ? nearby : nearby.Concat(sourceTrim.GetValueOrDefault(source.Identity) ?? Enumerable.Empty<Curve>());
                            foreach (var part in Subtract(farPart.Curve, references, tol))
                            {
                                using (part)
                                {
                                    if (cut.Crop is { } crop)
                                        foreach (var cropped in NativeDrawing.Crop(part, crop, tol)) result.Pieces.Add(new(cropped, source, kind, (item.DepthGeometry as Curve)?.Trim(cropped.Domain)) { Profile = item.Profile });
                                    else result.Pieces.Add(new(part.DuplicateCurve(), source, kind, (item.DepthGeometry as Curve)?.Trim(part.Domain)) { Profile = item.Profile });
                                }
                            }
                        }
                    }
                }
            }

        }
        catch (OperationCanceledException) { result.Dispose(); throw; }
        catch (Exception ex) { result.Dispose(); result.Error = ex.Message; }
        finally
        {
            result._diagnostics.Add(work.Diagnostic);
            try
            {
            token.ThrowIfCancellationRequested();
            if (quarantine || failedClipFootprints.Count != 0)
                HldProjection.ApplyFailure(result, quarantine, failedClipFootprints, tol, token);
            if (preparedSections is { } completed)
                foreach (var refusal in completed.Refusals)
                {
                    token.ThrowIfCancellationRequested();
                    var source = participating.First(s => s.Identity == refusal.SourceId);
                    SectionPreparation.AddRefusedMarker(result, source, refusal.Geometry, request, token);
                }
            if (result.EmptyNativeSlots + result.ZeroLengthNativeSegments != 0) result._diagnostics.Add($"Benign native records dropped: {result.EmptyNativeSlots} empty slots; {result.ZeroLengthNativeSegments} exactly zero-length point curves.");
            if(view.Settings?.Hidden==false)
                foreach(var piece in result.Pieces.Where(p=>p.Class==LineClass.Hidden).ToArray())
                { result.Pieces.Remove(piece);piece.Dispose(); }
            result._diagnostics.Add($"Compute: {HldProjection.Policy}; fingerprint={WorkerCodec.Fingerprint()}; processors={System.Environment.ProcessorCount}.");
            result.Pieces.Sort(NativeDrawing.ComparePieces);
            result.Report = new("Route C physical section + " + route, result.Items.Count(p => p.Class == LineClass.Unresolved),
                invalid, scene.Unsupported, scene.Conversions, quarantine) { ValidationDomain = "certified material sections; Rhino HLD visibility not certified" };
            result.Seconds = timer.Elapsed.TotalSeconds;
            }
            catch { result.Dispose(); throw; }
            finally
            {
                preparedSections?.Dispose();
                foreach (var geometry in owned) geometry.Dispose();
                foreach (var curve in suppression.Concat(cutLoci).Concat(farLoci)) curve.Dispose();
            }
        }
        return result;
    }

    internal static IEnumerable<Curve> Subtract(Curve curve, IEnumerable<Curve> loci, double tolerance)
    {
        var pieces = EdgeProvenance.Split(curve, loci.Select(c => (c, EdgeOrigin.PhysicalCut)), tolerance);
        try
        {
            foreach (var piece in pieces)
                if (piece.Origin == EdgeOrigin.None) yield return piece.Curve.DuplicateCurve();
        }
        finally { foreach (var piece in pieces) piece.Dispose(); }
    }

}
