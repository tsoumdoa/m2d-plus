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

/// <summary>One route C solve. Certified clips remain owned through native extraction and point rays.</summary>
internal static class SectionDrawing
{
    private sealed record Candidate(PreparedLeaf Source, bool Far = false);

    internal static ViewDrawing Solve(PreparedScene scene, IReadOnlyList<PreparedLeaf> sources,
        ViewRequest view, NativeSettings settings, CancellationToken token)
    {
        using var work = new SectionWork();
        var timer = Stopwatch.StartNew(); var invalid = 0; var quarantine = scene.HasPotentialOccluderUnsupported;
        var uncertainSources = new HashSet<string>(StringComparer.Ordinal); var uncertainRegions = new List<BoundingBox>();
        var localSources = new HashSet<string>(StringComparer.Ordinal);
        // Snapshot region/occluder evidence before visibility. Native candidate
        // classifications cannot invalidate an independent section certificate.
        var regionUncertainSources = new HashSet<string>(StringComparer.Ordinal);
        var regionUncertainRegions = new List<BoundingBox>();
        var result = new ViewDrawing { Name = view.Name, Settings = view.Settings, Tolerance = view.Settings?.Tolerance ?? scene.Tolerance, MultipleThreads = settings.MultipleThreads,
            Frame = DrawingFrames.Resolve(view, sources, scene.Bounds, view.Settings?.Tolerance ?? scene.Tolerance) };
        var cut = view.Cut!; var tol = view.Settings?.Tolerance ?? scene.Tolerance;
        var request = new SectionRequest(cut.Plane, tol, .001, cut.Crop, CutMode.Separate, HatchMode.Styles);
        var autoDepth = scene.Bounds.IsValid ? Math.Max(0, -scene.Bounds.GetCorners().Min(cut.Plane.DistanceTo)) + tol * 10 : tol * 10;
        var range = new ViewRange(cut.SourceDepth ?? autoDepth, farClip: cut.FarClip, tolerance: tol);
        Plane? far = range.ClipsFar ? new Plane(cut.Plane.Origin - cut.Plane.Normal * range.Depth, -cut.Plane.XAxis, cut.Plane.YAxis) : null;
        var participating = sources.Where(cut.Participates).ToArray();
        var bodies = participating.Where(s => s.Geometry is Brep or Mesh).ToArray();
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
            using var sections = SectionRegionEngine.Compute(bodies.Select(s => new SectionSource(s.Identity, s.Geometry, cut.Style(s)) { Canonical = s.Canonical }),
                request, scene.SectionVerdicts, token, prepareVisibility: true, farPlane: far, phases: result.Phases);
            result.Phases.Add("sections", -result.Phases.Seconds.GetValueOrDefault("route C trim"));
            result.UncertainSectionComponents = sections.Components.Count(c => c.Status == SectionStatus.Uncertain);
            foreach (var diagnostic in sections.Diagnostics.Where(d => d.Status is SectionStatus.Uncertain or SectionStatus.Unsupported))
            {
                var affected = sources.FirstOrDefault(s => s.Identity == diagnostic.SourceId);
                if (affected is null) { quarantine = true; continue; }
                var footprint = affected.Geometry.GetBoundingBox(Transform.PlanarProjection(cut.Plane));
                if (!footprint.IsValid) quarantine = true;
                else { uncertainSources.Add(affected.Identity); uncertainRegions.Add(footprint); }
            }
            regionUncertainSources.UnionWith(uncertainSources);
            regionUncertainRegions.AddRange(uncertainRegions);
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
                    .Any(s => ProjectedOverlap(s.Geometry, region, cut.Plane, tol));
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

            foreach (var source in sources)
            {
                token.ThrowIfCancellationRequested();
                if (!cut.Participates(source)) { registered.Add((source.Geometry, new(source))); continue; }
                if (source.Geometry is Brep or Mesh)
                {
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
                        foreach (var retained in ClipCurve(curve, cut.Plane, far, tol))
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
            if (!settings.NativeOnly && registered.Any(r => PlanarExtraction.MayProduceKernelCandidates(r.Geometry,tol)))
            {
                phase.Pause();
                using var routed = new ViewDrawing { Frame = result.Frame, Tolerance = tol };
                route = ComposedVisibility.Solve(registered.Select(r => new VisibilityInput(r.Geometry, r.Tag.Source, r.Tag.Far)).ToArray(), routed, view, settings, token, scene.Bounds);
                phase.Next("section output construction");
                result.Phases.Include(routed.Phases);
                result.KernelEdges = routed.KernelEdges; result.NativeEdges = routed.NativeEdges; result.NativeCalls = routed.NativeCalls;
                result.FastNativeSources = routed.FastNativeSources; result.MultipleThreads = routed.MultipleThreads;
                result.CurvedCandidateEdges = routed.CurvedCandidateEdges; result.PlanarNativeEdges = routed.PlanarNativeEdges; result.NativeFragments = routed.NativeFragments;
                result.HiddenPoints += routed.HiddenPoints; result.DuplicateSegments += routed.DuplicateSegments;
                result.EmptyNativeSlots += routed.EmptyNativeSlots; result.ZeroLengthNativeSegments += routed.ZeroLengthNativeSegments;
                result.ProjectingSegments += routed.ProjectingSegments; invalid += routed.RoutingInvalid;
                quarantine |= routed.RoutingQuarantine; uncertainSources.UnionWith(routed.UncertainRoutingSources);
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
                    if (item.Class == LineClass.Hidden && view.Settings?.Hidden == false) continue;
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
                                        foreach (var cropped in NativeDrawing.Crop(part, crop, tol)) result.Pieces.Add(new(cropped, source, kind, (item.DepthGeometry as Curve)?.Trim(cropped.Domain)) { Profile = item.Profile, KernelOwned = item.KernelOwned });
                                    else result.Pieces.Add(new(part.DuplicateCurve(), source, kind, (item.DepthGeometry as Curve)?.Trim(part.Domain)) { Profile = item.Profile, KernelOwned = item.KernelOwned });
                                }
                            }
                        }
                    }
                }
            }
            else
            {
            var occluders = registered.Where(p => !p.Tag.Far && p.Geometry is Brep or Mesh).Select(p => p.Geometry).ToArray();
            using var viewport = new ViewportInfo(); var frame = result.Frame;
            var radius = Math.Max(scene.Bounds.IsValid ? scene.Bounds.Diagonal.Length : 0,16*tol);
            NativeViewport.Configure(viewport,frame,view.Direction,scene.Bounds,tol);
            var nativeOptions = NativeHldOptions.For(view, settings);
            var parameters = new HiddenLineDrawingParameters { AbsoluteTolerance = tol, Flatten = false,
                IncludeHiddenCurves = nativeOptions.IncludeHiddenCurves, IncludeTangentEdges = view.Settings?.Tangents ?? false, IncludeTangentSeams = view.Settings?.Seams ?? false };
            parameters.SetViewport(viewport);
            var curvesAndBodies = registered.Where(p => p.Geometry is not Point).ToArray();
            foreach (var entry in VisibilityInputOrder.Native(curvesAndBodies, r => new VisibilityInput(r.Geometry,r.Tag.Source,r.Tag.Far), settings))
                if (!parameters.AddGeometry(entry.Geometry, entry.Tag))
                { invalid++; quarantine = true; result._diagnostics.Add($"Native rejected {entry.Tag.Source.Identity}"); }
            if (curvesAndBodies.Length != 0)
            {
                NativeDrawing.CountHld(); result.NativeCalls++;
                phase.Next("native solve");
                nativeOptions.Begin(result, curvesAndBodies.Select(r => new VisibilityInput(r.Geometry,r.Tag.Source,r.Tag.Far)), settings);
                using var drawing = HiddenLineDrawing.Compute(parameters, nativeOptions.MultipleThreads, NativeDrawing.HldProgress, token);
                phase.Next("section output construction");
                if (drawing is null || !drawing.WorldToHiddenLine.TryGetInverse(out var inverse)) throw new InvalidOperationException("Route C HLD failed.");
                var project = Transform.PlanarProjection(cut.Plane) * inverse;
                var nativeParents = new HashSet<int>(); int nativeFragments = 0;
                // Only cut loops near a segment can overlap it. Split still applies its own exact bounds test.
                foreach (var segment in drawing.Segments)
                {
                    token.ThrowIfCancellationRequested(); var visibility = segment.SegmentVisibility;
                    if (visibility == HiddenLineDrawingSegment.Visibility.Unset)
                    {
                        var evidence = NativeUncertainty.Inspect(segment, tag => (tag as Candidate)?.Source, project);
                        if (evidence.Kind == UnsetKind.EmptySlot) result.EmptyNativeSlots++;
                        else if (evidence.Kind == UnsetKind.ZeroLength) result.ZeroLengthNativeSegments++;
                        else
                        {
                            invalid++;
                            if (!settings.NativeOnly && NativeUncertainty.RetainLocal(segment, evidence, inverse, result, uncertainRegions,
                                (id, world) => settings.ObserveCompositionRecord?.Invoke(id, -1, false, LineClass.Unresolved, world)))
                                localSources.Add(evidence.Source!.Identity);
                            else if (evidence.Source is { } affected) uncertainSources.Add(affected.Identity);
                            else if (evidence.Bounds is { } bounds) uncertainRegions.Add(bounds);
                            else quarantine = true;
                            result._diagnostics.Add($"Unset segment {segment.Index}: {evidence.Kind}; source={evidence.Source?.Identity}; bounds={evidence.Bounds}; {evidence.Detail}");
                        }
                        continue;
                    }
                    if (visibility != HiddenLineDrawingSegment.Visibility.Projecting && segment.ParentCurve is { } parent) nativeParents.Add(parent.Index);
                    if (visibility == HiddenLineDrawingSegment.Visibility.Duplicate) { result.DuplicateSegments++; continue; }
                    if (visibility == HiddenLineDrawingSegment.Visibility.Projecting) { result.ProjectingSegments++; continue; }
                    if (segment.ParentCurve?.SourceObject?.Tag is not Candidate candidate || segment.CurveGeometry is not { } proxy)
                    { invalid++; quarantine = true; continue; }
                    if (visibility == HiddenLineDrawingSegment.Visibility.Hidden && view.Settings?.Hidden == false) continue;
                    if (candidate.Far && visibility != HiddenLineDrawingSegment.Visibility.Visible) continue;
                    nativeFragments++;
                    using var depthCurve = proxy.DuplicateCurve(); depthCurve.Transform(inverse);
                    using var curve = proxy.DuplicateCurve(); curve.EnsurePrivateCopy();
                    if (!curve.Transform(project) || !curve.IsValid) throw new InvalidOperationException("Invalid Route C extraction.");
                    var classification = candidate.Far ? LineClass.FarClip : visibility == HiddenLineDrawingSegment.Visibility.Visible
                        ? LineClass.Visible : visibility == HiddenLineDrawingSegment.Visibility.Hidden ? LineClass.Hidden : LineClass.Unresolved;
                    var farParts = EdgeProvenance.Split(curve, (sourceFar.GetValueOrDefault(candidate.Source.Identity) ?? Enumerable.Empty<Curve>()).Select(c => (c, EdgeOrigin.FarTrim)), tol);
                    foreach (var farPart in farParts)
                    {
                        using (farPart)
                        {
                            var isFar = (farPart.Origin & EdgeOrigin.FarTrim) != 0;
                            if (candidate.Far && !isFar) continue;
                            if (isFar && (!range.GenerateFarClipLines || visibility != HiddenLineDrawingSegment.Visibility.Visible)) continue;
                            var kind = isFar ? LineClass.FarClip : classification;
                            var nearby = NearbyCuts(farPart.Curve);
                            IEnumerable<Curve> references = isFar ? nearby : nearby.Concat(sourceTrim.GetValueOrDefault(candidate.Source.Identity) ?? Enumerable.Empty<Curve>());
                            foreach (var part in Subtract(farPart.Curve, references, tol))
                            {
                                using (part)
                                {
                                    if (cut.Crop is { } crop)
                                        foreach (var cropped in NativeDrawing.Crop(part, crop, tol)) result.Pieces.Add(new(cropped, candidate.Source, kind, depthCurve.Trim(cropped.Domain)) { Profile = segment.IsSceneSilhouette });
                                    else result.Pieces.Add(new(part.DuplicateCurve(), candidate.Source, kind, depthCurve.Trim(part.Domain)) { Profile = segment.IsSceneSilhouette });
                                }
                            }
                        }
                    }
                }
                result.NativeEdges = result.CurvedCandidateEdges = nativeParents.Count; result.NativeFragments = nativeFragments;
            }
            foreach (var entry in registered.Where(p => p.Geometry is Point))
            {
                token.ThrowIfCancellationRequested(); var point = ((Point)entry.Geometry).Location;
                if (Hidden(point, occluders, cut.Plane.Normal, radius, tol)) { result.HiddenPoints++; continue; }
                var location = cut.Plane.ClosestPoint(point);
                if (cut.Crop is { } crop && !NativeDrawing.Inside(location, crop, tol)) continue;
                result.Pieces.Add(new(new Point(location), entry.Tag.Source, LineClass.Visible, new Point(point)));
            }
            phase.Next("native ray verification");
            NativeVisibilityVerifier.Apply(registered.Select(r => new VisibilityInput(r.Geometry,r.Tag.Source,r.Tag.Far)).ToArray(), result, settings, token);
            }
        }
        catch (OperationCanceledException) { result.Dispose(); throw; }
        catch (Exception ex) { result.Dispose(); result.Error = ex.Message; quarantine = true; }
        finally
        {
            result._diagnostics.Add(work.Diagnostic);
            foreach (var geometry in owned) geometry.Dispose();
            foreach (var curve in suppression.Concat(cutLoci).Concat(farLoci)) curve.Dispose();
            if (quarantine || uncertainSources.Count != 0 || uncertainRegions.Count != 0)
            {
                try { NativeUncertainty.ApplyFootprints(result, sources, quarantine, uncertainSources, uncertainRegions, tol, token, !settings.NativeOnly, localSources); }
                catch { result.Dispose(); throw; }
                foreach (var region in result.Regions.ToArray())
                {
                    if (!quarantine && !region.Region.SourceIds.Any(regionUncertainSources.Contains) && !region.Region.Loops.Any(l => regionUncertainRegions.Any(b => NativeUncertainty.Affects(b, l.Curve.GetBoundingBox(true), tol)))) continue;
                    var source = sources.First(s => s.Identity == region.Region.SourceIds[0]);
                    foreach (var loop in region.Region.Loops) result.Pieces.Add(new(loop.Curve.DuplicateCurve(), source, LineClass.Unresolved));
                    region.Region.Dispose(); result.Regions.Remove(region);
                }
            }
            if (result.EmptyNativeSlots + result.ZeroLengthNativeSegments != 0) result._diagnostics.Add($"Benign native records dropped: {result.EmptyNativeSlots} empty slots; {result.ZeroLengthNativeSegments} exactly zero-length point curves.");
            result.Pieces.Sort(NativeDrawing.ComparePieces);
            result.Report = new("Route C physical section + " + route, result.Items.Count(p => p.Class == LineClass.Unresolved),
                invalid, scene.Unsupported, scene.Conversions, quarantine) { ValidationDomain = "analytic section/visibility controls; unverified freeform crossings quarantined" };
            result.Seconds = timer.Elapsed.TotalSeconds;
        }
        return result;
    }

    private static bool ProjectedOverlap(GeometryBase geometry, SectionRegion region, Plane plane, double tolerance)
    {
        var transform = Transform.PlaneToPlane(plane, Plane.WorldXY); var a = geometry.GetBoundingBox(transform);
        var b = BoundingBox.Empty; foreach (var loop in region.Loops) b.Union(loop.Curve.GetBoundingBox(transform));
        return a.Max.Z > tolerance && a.Min.X <= b.Max.X + tolerance && a.Max.X >= b.Min.X - tolerance &&
            a.Min.Y <= b.Max.Y + tolerance && a.Max.Y >= b.Min.Y - tolerance;
    }

    internal static bool Hidden(Point3d point, IEnumerable<GeometryBase> occluders, Vector3d towardViewer, double radius, double tolerance)
    {
        using var ray = new LineCurve(point, point + towardViewer * (radius * 4 + 10));
        foreach (var geometry in occluders)
        {
            if (geometry is Brep brep)
            {
                if (brep.IsSolid && brep.IsPointInside(point, tolerance, true)) return true;
                if (!Intersection.CurveBrep(ray, brep, tolerance, out var overlaps, out var hits))
                    throw new InvalidOperationException("Point/occluder intersection failed; visibility is uncertain.");
                try { if (hits.Any(p => (p - point) * towardViewer > tolerance) || overlaps.Any(c => (c.PointAtNormalizedLength(.5) - point) * towardViewer > tolerance)) return true; }
                finally { foreach (var overlap in overlaps) overlap.Dispose(); }
            }
            else if (geometry is Mesh mesh)
            {
                if (mesh.IsClosed && mesh.IsPointInside(point, tolerance, true)) return true;
                var distance = Intersection.MeshRay(mesh, new Ray3d(point, towardViewer));
                if (distance > tolerance) return true;
            }
        }
        return false;
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

    private static IEnumerable<Curve> ClipCurve(Curve curve, Plane near, Plane? far, double tolerance)
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
