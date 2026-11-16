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
        var footprintProofRegions = new List<BoundingBox>();
        SectionResult? preparedSections = null;
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
            var sections = preparedSections = SectionRegionEngine.Compute(bodies.Select(s => new SectionSource(s.Identity, s.Geometry, cut.Style(s)) { Canonical = s.Canonical }),
                request, scene.SectionVerdicts, token, prepareVisibility: true, farPlane: far, phases: result.Phases);
            result.Phases.Add("sections", -result.Phases.Seconds.GetValueOrDefault("route C trim"));
            result.UncertainSectionComponents = sections.Components.Count(c => c.Status == SectionStatus.Uncertain);
            foreach (var diagnostic in sections.Diagnostics.Where(d => d.Status is SectionStatus.Uncertain or SectionStatus.Unsupported))
            {
                var affected = sources.FirstOrDefault(s => s.Identity == diagnostic.SourceId);
                if (affected is null) { quarantine = true; continue; }
                var footprints = SectionFootprints(affected.Geometry, cut.Plane);
                if (footprints.Length == 0 || footprints.Any(b => !b.IsValid)) quarantine = true;
                else { uncertainSources.Add(affected.Identity); uncertainRegions.AddRange(footprints); }
                // Narrow the actual footprint, but do not silently restore native
                // Visible elsewhere in its former box. It still needs the same
                // continuous all-body certificate used before narrowing.
                if (affected.Geometry is Mesh) footprintProofRegions.Add(affected.Geometry.GetBoundingBox(Transform.PlanarProjection(cut.Plane)));
            }
            result._diagnostics.Add($"Admission: {scene.Cost.GeometryBytes} geometry bytes; {scene.Cost.BrepFaces} faces; {scene.Cost.MeshTriangles} triangles; {scene.SourceCount} sources.");
            result._diagnostics.Add($"Route C; depth={range.Depth:R}; farclip={range.FarClip}; regime={range.Regime}; cuts={request.Cuts}; hatch={request.Hatch}; no native clipping planes.");
            result._diagnostics.AddRange(sections.Diagnostics.Select(d => $"{d.SourceId}: {d.Status}: {d.Message}"));
            // Better section topology/trim evidence changes the native input set,
            // whose order-dependent errors are not spatially local. Require an
            // independent whole-span proof for Visible throughout that solve.
            var recoveredSources = sections.Diagnostics.Where(d =>
                    d.Message.Contains(StableSectionCertificate.PoleRecovery, StringComparison.Ordinal) ||
                    d.Message.StartsWith(PhysicalSectionClipper.RecoveredEdges, StringComparison.Ordinal))
                .Select(d => d.SourceId).Concat(sections.Components.Where(c =>
                    c.Certificate.StartsWith("Conservative one-sided bound", StringComparison.Ordinal)).Select(c => c.SourceId))
                .ToHashSet(StringComparer.Ordinal);
            if (recoveredSources.Count != 0)
            {
                localSources.UnionWith(sources.Select(s => s.Identity));
                result._diagnostics.Add($"Section recovery visibility proofs: {recoveredSources.Count} recovered sources change native inputs; Visible throughout this solve requires a continuous all-body bound certificate.");
            }
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

            // Keep section certificates, validation and region ownership above unchanged.
            // Refused-cut markers and cut uncertainty belong before this projection boundary.
            if (view.Settings?.CutOnly == true)
            {
                route = "sections";
                if (result.Regions.Count == 0 && result.Pieces.Count == 0 && uncertainSources.Count == 0 && !quarantine)
                    result._diagnostics.Add(ViewNotices.CutOnlyEmpty);
                return result;
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
            try
            {
            token.ThrowIfCancellationRequested();
            if (quarantine || uncertainSources.Count != 0 || uncertainRegions.Count != 0 || footprintProofRegions.Count != 0 || localSources.Count != 0)
            {
                try { NativeUncertainty.ApplyFootprints(result, sources, quarantine, uncertainSources, uncertainRegions, tol, token, !settings.NativeOnly, localSources, footprintProofRegions,
                    registered.Select(r => new VisibilityInput(r.Geometry,r.Tag.Source,r.Tag.Far)).ToArray()); }
                catch { result.Dispose(); throw; }
                // SectionRegionEngine publishes only complete certified material loops
                // (or explicitly unfilled open outlines). Its failed candidates are
                // already absent. Another source's section/clip failure, or a failure
                // preparing this source for visibility, cannot change these loops.
                // Participating sources are removed in front of this exact cut plane;
                // excluded foreground sources were checked before region admission.
                // Unknown uncaptured occluders still withhold presentation globally.
                if (scene.HasPotentialOccluderUnsupported)
                    foreach (var region in result.Regions.ToArray())
                    {
                        var source = sources.First(s => s.Identity == region.Region.SourceIds[0]);
                        foreach (var loop in region.Region.Loops) result.Pieces.Add(new(loop.Curve.DuplicateCurve(), source, LineClass.Unresolved));
                        region.Region.Dispose(); result.Regions.Remove(region);
                    }
            }
            if (preparedSections is { } completed)
                foreach (var refusal in completed.Refusals)
                {
                    token.ThrowIfCancellationRequested();
                    var source = participating.First(s => s.Identity == refusal.SourceId);
                    AddRefusedMarker(result, source, refusal.Geometry, request, token);
                }
            if (result.EmptyNativeSlots + result.ZeroLengthNativeSegments != 0) result._diagnostics.Add($"Benign native records dropped: {result.EmptyNativeSlots} empty slots; {result.ZeroLengthNativeSegments} exactly zero-length point curves.");
            if(view.Settings?.Hidden==false)
                foreach(var piece in result.Pieces.Where(p=>p.Class==LineClass.Hidden).ToArray())
                { result.Pieces.Remove(piece);piece.Dispose(); }
            result.Pieces.Sort(NativeDrawing.ComparePieces);
            result.Report = new("Route C physical section + " + route, result.Items.Count(p => p.Class == LineClass.Unresolved),
                invalid, scene.Unsupported, scene.Conversions, quarantine) { ValidationDomain = "analytic section/visibility controls; unverified freeform crossings quarantined" };
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
