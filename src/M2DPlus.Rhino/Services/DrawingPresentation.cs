using M2DPlus.Core;
using M2DPlus.Core.Placement;
using M2DPlus.Core.Styling;
using M2DPlus.Rhino.Sections;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;

namespace M2DPlus.Rhino.Services;

/// <summary>One retained visibility result. Leases protect presentation from concurrent clear/removal.</summary>
public sealed class DrawingCache : IDisposable
{
    private readonly SemaphoreSlim _solve = new(1);
    private readonly object _sync = new();
    private string? _key;
    private LeaseEntry<DrawingBatch>? _entry;
    private long _generation;
    public void Clear() { lock (_sync) { _generation++; _entry?.Dispose(); _entry = null; _key = null; } }
    public void Dispose() => Clear();

    public Task<DrawingBatch> SolveAsync(LeaseEntry<PreparedScene>.Lease scene, RequestIdentity identity,
        IReadOnlyList<ViewRequest> views, NativeSettings settings, IReadOnlyList<Plane> targets,
        CancellationToken token, IProgress<JobProgress>? progress = null, IReadOnlyList<Plane>? sources = null)
    {
        var requests = views.ToArray(); var planes = targets.ToArray(); var sourcePlanes = sources?.ToArray();
        // Cache hits can still rebuild section fills/merge; keep that work off the UI thread too.
        return Task.Run(() => SolveOwnedAsync(scene, identity, requests, settings, planes, token, progress, sourcePlanes));
    }

    private async Task<DrawingBatch> SolveOwnedAsync(LeaseEntry<PreparedScene>.Lease scene, RequestIdentity identity,
        IReadOnlyList<ViewRequest> views, NativeSettings settings, IReadOnlyList<Plane> targets,
        CancellationToken token, IProgress<JobProgress>? progress = null, IReadOnlyList<Plane>? sources = null)
    {
        // Own the incoming scene even if cancellation occurs while waiting for another native solve.
        using (scene)
        {
            await _solve.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var key = WorkerCodec.Fingerprint() + "/" + HldProjection.Policy + "/" + scene.Value.VisibilityRevision + "/cull=" + settings.CullSources + "/" + string.Join("|", views.Select(v => (v with { Cut = v.Cut is { } cut ? cut with { SectionerStyle = null } : null }).Revision + "/" + v.Settings?.VisibilityKey()));
                LeaseEntry<DrawingBatch>.Lease? lease;
                long generation;
                lock (_sync) { generation = _generation; lease = key == _key ? _entry?.Acquire() : null; }
                if (lease is null)
                {
                    // The supplied scene lease stays alive throughout native work.
                    // Preserve requested HLD flags.
                    var solveViews = views;
                    var raw = await DrawingExecution.SolveBorrowedAsync(scene.Value, identity, solveViews, settings, token, progress).ConfigureAwait(false);
                    var owner = new LeaseEntry<DrawingBatch>(raw); lease = owner.Acquire();
                    lock (_sync)
                    {
                        // Authored view errors are stable under this key; execution failures must retry.
                        if (generation == _generation && raw.Complete &&
                            raw.Views.Select((v, i) => !v.Report.Quarantined && (v.Error is null || views[i].Error is not null)).All(reusable => reusable))
                        { _entry?.Dispose(); _entry = owner; _key = key; }
                        else owner.Dispose();
                    }
                }
                using (lease)
                {
                    token.ThrowIfCancellationRequested();
                    progress?.Report(new(JobPhase.Output, 0, 0));
                    return DrawingPresentation.ApplySnapshot(lease.Value, identity, views, targets, scene.Value.SourceMetadata, sources);
                }
            }
            finally { _solve.Release(); }
        }
    }
}

public static class DrawingPresentation
{
    public static Plane3 Plain(Plane p) => new(new(p.OriginX, p.OriginY, p.OriginZ),
        new(p.XAxis.X, p.XAxis.Y, p.XAxis.Z), new(p.YAxis.X, p.YAxis.Y, p.YAxis.Z));
    public static Plane RhinoPlane(Plane3 p) => new(new Point3d(p.Origin.X, p.Origin.Y, p.Origin.Z),
        new Vector3d(p.X.X, p.X.Y, p.X.Z), new Vector3d(p.Y.X, p.Y.Y, p.Y.Z));

    public static DrawingBatch Apply(DrawingBatch raw, RequestIdentity identity, IReadOnlyList<ViewRequest> views, IReadOnlyList<Plane> targets, IReadOnlyList<Plane>? sources = null)
        => ApplySnapshot(raw, identity, views, targets, null, sources);

    internal static DrawingBatch ApplySnapshot(DrawingBatch raw, RequestIdentity identity, IReadOnlyList<ViewRequest> views, IReadOnlyList<Plane> targets,
        IReadOnlyDictionary<string, SourceSection>? metadata, IReadOnlyList<Plane>? sources = null)
    {
        var placements = raw.Views.Select((v, i) =>
        {
            var frame = v.Frame.IsValid ? v.Frame : new Rectangle3d(Plane.WorldXY, 1, 1);
            var plane = frame.Plane;
            var anchor = views[i].Cut is not null ? plane : views[i].Named?.Target is { } cameraTarget ? new Plane(plane.ClosestPoint(cameraTarget), plane.XAxis, plane.YAxis) :
                RhinoPlane(AnchorRules.Resolve(views[i].Kind, Plain(plane)));
            plane.ClosestParameter(anchor.Origin, out var dx, out var dy);
            return new PlacementView(Plain(anchor), new Frame2(frame.X.Min - dx, frame.Y.Min - dy, frame.X.Max - dx, frame.Y.Max - dy));
        }).ToArray();
        var plan = PlacementPlan.Create(placements, targets.Select(Plain).ToArray(), views.Select(v => v.Settings?.Gap ?? DistanceSetting.Auto).ToArray(), sources?.Select(Plain).ToArray());
        if (!plan.Success) throw new ArgumentException(plan.Error);
        var results = new List<ViewDrawing>();
        try
        {
            for (var i = 0; i < raw.Views.Count; i++)
            {
                var original = raw.Views[i]; var settings = views[i].Settings;
                var target = plan.Views[i]; var transform = Transform.PlaneToPlane(RhinoPlane(target.Transform.Source), RhinoPlane(target.Transform.Target));
                var result = new ViewDrawing { Name = original.Name, Error = original.Error, Frame = original.Frame,
                    Report = original.Report, Settings = settings, Seconds = original.Seconds, Tolerance = original.Tolerance,
                    NativeEdges = original.NativeEdges, NativeCalls = original.NativeCalls,
                    FastNativeSources = original.FastNativeSources,
                    CurvedCandidateEdges = original.CurvedCandidateEdges, NativeFragments = original.NativeFragments,
                    MultipleThreads = original.MultipleThreads, HiddenPoints = original.HiddenPoints, DuplicateSegments = original.DuplicateSegments,
                    ProjectingSegments = original.ProjectingSegments, EmptyNativeSlots = original.EmptyNativeSlots, ZeroLengthNativeSegments = original.ZeroLengthNativeSegments,
                    ToleranceDroppedPieces = original.ToleranceDroppedPieces, SubToleranceSourceEdges = original.SubToleranceSourceEdges,
                    SubToleranceBoundsGaps = original.SubToleranceBoundsGaps,
                    UncertainSectionComponents = original.UncertainSectionComponents };
                result.Phases.Include(original.Phases);
                using var phase = result.Phases.Start("Beyond/fade splitting");
                results.Add(result); result._diagnostics.AddRange(original.Diagnostics.Where(d => metadata is null || !d.StartsWith("Style ", StringComparison.Ordinal)));
                var frame = original.Frame; frame.Transform(transform); result.Frame = frame;
                if (settings is not null) result._diagnostics.Add(settings.Describe());
                if (plan.Remark is { } remark) result._diagnostics.Add(remark);
                if (sources is { Count: > 0 } && targets.Count > 0)
                    result._diagnostics.Add(FormattableString.Invariant($"Placement: Source → Target (1:1); origins ({target.Transform.Source.Origin.X:0.###}, {target.Transform.Source.Origin.Y:0.###}, {target.Transform.Source.Origin.Z:0.###}) → ({target.Transform.Target.Origin.X:0.###}, {target.Transform.Target.Origin.Y:0.###}, {target.Transform.Target.Origin.Z:0.###})."));
                var range = settings is null ? null : new ViewRange(settings);
                var minVisible = settings?.MinVisible ?? 0;
                if (settings?.Hidden == false && minVisible > 0)
                {
                    minVisible = 0;
                    result._diagnostics.Add("min visible ignored with hidden = off; HLD did not compute the hidden evidence required to identify partly hidden sources.");
                }
                var plane2d = original.Frame.Plane; var toPlane2d = Transform.PlaneToPlane(plane2d, Plane.WorldXY);
                var rule = new List<(int Piece, MinVisiblePiece Line)>();
                var droppedUnresolved = 0;
                foreach (var item in original.Items)
                {
                    phase.Next("Beyond/fade splitting");
                    // Keep all available solved-hidden evidence for min visible.
                    // Native may omit unrequested hidden curves.
                    if (item.SolvedClass == LineClass.Hidden && settings?.Hidden == false)
                    {
                        if (item.Class == LineClass.Unresolved) droppedUnresolved++;
                        if (minVisible > 0) { using var dropped = item.CopyGeometry(); Note(-1, dropped, item.Class, item.SourceIdentity); }
                        continue;
                    }
                    using var geometry = item.CopyGeometry();
                    var depthGeometry = item.DepthGeometry ?? geometry;
                    var parameters = new SortedSet<double>();
                    if (geometry is Curve curve)
                    {
                        parameters.Add(curve.Domain.Min); parameters.Add(curve.Domain.Max);
                        if (range?.Beyond is double beyond && item.Class == LineClass.Visible && depthGeometry is Curve depthCurve)
                        {
                            var threshold = new Plane(original.Frame.Plane.Origin - original.Frame.Plane.Normal * beyond, original.Frame.Plane.Normal);
                            using var hits = Intersection.CurvePlane(depthCurve, threshold, settings!.Tolerance);
                            if (hits is not null) foreach (var hit in hits)
                            {
                                if (hit.IsOverlap) { parameters.Add(hit.OverlapA.Min); parameters.Add(hit.OverlapA.Max); }
                                else parameters.Add(hit.ParameterA);
                            }
                        }
                        var ends = parameters.Where(t => curve.Domain.IncludesParameter(t)).ToArray();
                        for (var j = 1; j < ends.Length; j++)
                        {
                            var interval = new Interval(ends[j - 1], ends[j]);
                            using var depthPart = depthGeometry is Curve dc ? dc.Trim(interval) : null;
                            var part = CurveIntervals.Trim(curve, interval);
                            // Floating-point interpolation can collapse an interval of an
                            // already sub-tolerance projected candidate to one point.
                            // It has no drawable extent; retain failures on real curves.
                            if (part is null && curve.GetBoundingBox(true).Diagonal.Length <= original.Tolerance)
                            { result.ProjectingSegments++; continue; }
                            if (part is null) throw new InvalidOperationException(FormattableString.Invariant($"Range split failed: {curve.GetType().Name} domain={curve.Domain}; interval={interval}; endpoints={curve.PointAt(interval.Min)}/{curve.PointAt(interval.Max)}; len={curve.GetLength():R}"));
                            phase.Next("output construction");
                            Add(part, depthPart ?? depthGeometry);
                            phase.Next("Beyond/fade splitting");
                        }
                    }
                    else { phase.Next("output construction"); Add(geometry.Duplicate(), depthGeometry); }

                    void Add(GeometryBase part, GeometryBase depths)
                    {
                        try
                        {
                            var box = depths.GetBoundingBox(Transform.PlaneToPlane(original.Frame.Plane, Plane.WorldXY));
                            var span = new DepthInterval(-box.Max.Z, -box.Min.Z);
                            var kind = item.Class;
                            var representative = depths is Curve c ? -original.Frame.Plane.DistanceTo(c.PointAt(c.Domain.Mid)) : span.Representative;
                            if (part is Curve && kind == LineClass.Visible && range?.Classify(representative, false) == RangeClassification.Beyond) kind = LineClass.Beyond;
                            if (minVisible > 0) Note(result.Pieces.Count, part, kind, item.SourceIdentity);
                            if (!part.Transform(transform)) throw new InvalidOperationException("Placement failed.");
                            result.Pieces.Add(new(part, new PreparedLeaf(item.SourceIdentity, item.SourcePath, part) { Section = metadata?.GetValueOrDefault(item.SourceIdentity) ?? item.Source, SourceItem = item.SourceItem }, kind, depths.Duplicate())
                            { Profile = item.Profile, Depths = span, Intensity = range?.Intensity(span, kind) ?? 1 });
                        }
                        catch { part.Dispose(); throw; }
                    }
                }
                if (droppedUnresolved != 0) result.Report = original.Report with { UncertainCount = original.Report.UncertainCount - droppedUnresolved };
                if (minVisible > 0)
                {
                    phase.Next("min visible");
                    result.MinVisible = minVisible;
                    // A cut object is never ignored: its cut is never ignored, so neither are its lines.
                    var cut = original.Regions.SelectMany(r => r.Region.SourceIds).ToHashSet(StringComparer.Ordinal);
                    var ignored = MinVisibleRule.Evaluate(rule.Select(r => r.Line), minVisible, settings!.Tolerance, cut).Ignored;
                    foreach (var (index, _) in rule)
                    {
                        if (index < 0) continue;
                        var piece = result.Pieces[index];
                        if (piece.Class is not (LineClass.Visible or LineClass.Beyond or LineClass.FarClip) || !ignored.Contains(piece.SourceIdentity)) continue;
                        piece.Class = LineClass.Ignored; result.IgnoredPieces++;
                    }
                    result.IgnoredObjects = ignored.Count;
                }
                phase.Next("section restyle and output");
                var freshRegions = original.Regions.Select(r =>
                {
                    if (metadata is null) return r.Region;
                    var source = metadata.GetValueOrDefault(r.Region.SourceIds[0]) ?? r.Source;
                    var style = source.FromSectioner && views[i].Cut?.SectionerStyle is { HasStyle: true } sectioner ?
                        sectioner with { ObjectColour = source.Style.ObjectColour, ObjectPrintColour = source.Style.ObjectPrintColour } : source.Style;
                    result._diagnostics.Add($"Style {r.Region.SourceIds[0]}: {source.Rule}; source={style.ObjectColour:x8}");
                    return r.Region with { Style = style };
                }).ToArray();
                using var styledRegions = SectionRegionEngine.Restyle(freshRegions,
                    new SectionRequest(original.Frame.Plane, settings?.Tolerance ?? original.Tolerance, .001, Crop: views[i].Cut?.Crop,
                        Cuts: settings?.Cuts ?? M2DPlus.Core.Sections.CutMode.Separate, Hatch: settings?.Hatch ?? M2DPlus.Core.Sections.HatchMode.Styles));
                result._diagnostics.AddRange(styledRegions.Diagnostics.Select(d => d.Message));
                foreach (var region in styledRegions.Regions)
                {
                    var entry = original.Regions.Where(r => r.Region.SourceIds.Any(region.SourceIds.Contains))
                        .OrderBy(r => r.SourcePath, Comparer<int[]>.Create((a,b) => { for(var k=0;k<Math.Min(a.Length,b.Length);k++){var c=a[k].CompareTo(b[k]);if(c!=0)return c;}return a.Length.CompareTo(b.Length); })).First();
                    if (region.Status == M2DPlus.Core.Sections.SectionStatus.Uncertain)
                    {
                        foreach (var loop in region.Loops)
                        {
                            var curve = Move(loop.Curve);
                            Curve? depth = null;
                            try
                            {
                                depth = loop.Curve.DuplicateCurve();
                                result.Pieces.Add(new(curve, new PreparedLeaf(region.SourceIds[0], entry.SourcePath, curve)
                                    { Section = metadata?.GetValueOrDefault(region.SourceIds[0]) ?? entry.Source, SourceItem = entry.SourceItem },
                                    LineClass.Unresolved, depth));
                            }
                            catch { curve.Dispose(); depth?.Dispose(); throw; }
                        }
                        result.Report = result.Report with { UncertainCount = result.Report.UncertainCount + region.Loops.Count };
                        result.UncertainSectionComponents++;
                        continue;
                    }
                    var loops = region.Loops.Select(l => new SectionLoop(Move(l.Curve), l.IsHole,
                        l.Edges.Select(e => new SectionEdge(Move(e.Curve), e.Origin)).ToArray())).ToArray();
                    var hatch = (Hatch?)region.Hatch?.Duplicate(); hatch?.Transform(transform);
                    result.Regions.Add(new(region with { Loops = loops, Hatch = hatch }, (int[])entry.SourcePath.Clone()) { Source = metadata?.GetValueOrDefault(entry.Region.SourceIds[0]) ?? entry.Source, SourceItem = entry.SourceItem });
                }
                // One rule line per curve piece, in drawing-plane coordinates. Drawn pieces at least as long as the
                // threshold keep their object and need no outline; hidden ones only need their 2D extent.
                void Note(int index, GeometryBase part, LineClass kind, string source)
                {
                    if (part is not Curve c) return;
                    var role = kind switch { LineClass.Visible or LineClass.Profile or LineClass.Beyond or LineClass.FarClip => MinVisibleRole.Drawn,
                        LineClass.Hidden => MinVisibleRole.Hidden, _ => MinVisibleRole.Unresolved };
                    if (role != MinVisibleRole.Drawn)
                    {
                        var box = c.GetBoundingBox(toPlane2d); // drawing-plane coordinates, as for depths above
                        rule.Add((index, new(source, role, [new(box.Min.X, box.Min.Y), new(box.Max.X, box.Max.Y)], 0)));
                        return;
                    }
                    var length = c.GetLength();
                    IReadOnlyList<Point2> points;
                    if (length >= minVisible || c is LineCurve) points = [At(c.PointAtStart), At(c.PointAtEnd)];
                    else if (c.TryGetPolyline(out var polyline)) points = polyline.Select(At).ToArray();
                    else
                    {
                        using var approximation = c.ToPolyline(Math.Max(settings!.Tolerance, 1e-9), .1, 0, 0);
                        points = approximation is not null && approximation.TryGetPolyline(out var sampled) && sampled.Count >= 2
                            ? sampled.Select(At).ToArray() : [At(c.PointAtStart), At(c.PointAtEnd)];
                    }
                    rule.Add((index, new(source, role, points, length)));
                }
                Point2 At(Point3d p) { plane2d.ClosestParameter(p, out var u, out var v); return new(u, v); }
                Curve Move(Curve c) { var copy = c.DuplicateCurve(); if (!copy.Transform(transform)) { copy.Dispose(); throw new InvalidOperationException("Region placement failed."); } return copy; }
            }
            return new DrawingBatch(identity, results);
        }
        catch { foreach (var view in results) view.Dispose(); throw; }
    }
}
