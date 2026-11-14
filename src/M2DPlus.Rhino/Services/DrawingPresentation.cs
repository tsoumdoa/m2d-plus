using M2DPlus.Core;
using M2DPlus.Core.Placement;
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
        CancellationToken token, IProgress<JobProgress>? progress = null)
    {
        var requests = views.ToArray(); var planes = targets.ToArray();
        // Cache hits can still rebuild section fills/merge; keep that work off the UI thread too.
        return Task.Run(() => SolveOwnedAsync(scene, identity, requests, settings, planes, token, progress));
    }

    private async Task<DrawingBatch> SolveOwnedAsync(LeaseEntry<PreparedScene>.Lease scene, RequestIdentity identity,
        IReadOnlyList<ViewRequest> views, NativeSettings settings, IReadOnlyList<Plane> targets,
        CancellationToken token, IProgress<JobProgress>? progress = null)
    {
        // Own the incoming scene even if cancellation occurs while waiting for another native solve.
        using (scene)
        {
            await _solve.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var key = scene.Value.VisibilityRevision + "/" + settings + "/" + string.Join("|", views.Select(v => (v with { Cut = v.Cut is { } cut ? cut with { SectionerStyle = null } : null }).Revision + "/" +
                    string.Join(";", new[] { "depth", "farclip", "crop", "hidden", "tangents", "seams", "glass", "tolerance" }
                        .Select(k => v.Settings?[k].Value?.ToString()))));
                LeaseEntry<DrawingBatch>.Lease? lease;
                long generation;
                lock (_sync) { generation = _generation; lease = key == _key ? _entry?.Acquire() : null; }
                if (lease is null)
                {
                    // The supplied scene lease stays alive throughout native work.
                    var raw = await NativeDrawing.SolveBorrowedAsync(scene.Value, identity, views, settings, token, progress).ConfigureAwait(false);
                    var owner = new LeaseEntry<DrawingBatch>(raw); lease = owner.Acquire();
                    lock (_sync)
                    {
                        if (generation == _generation) { _entry?.Dispose(); _entry = owner; _key = key; }
                        else owner.Dispose();
                    }
                }
                using (lease) { token.ThrowIfCancellationRequested(); return DrawingPresentation.ApplySnapshot(lease.Value, identity, views, targets, scene.Value.SourceMetadata); }
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

    public static DrawingBatch Apply(DrawingBatch raw, RequestIdentity identity, IReadOnlyList<ViewRequest> views, IReadOnlyList<Plane> targets)
        => ApplySnapshot(raw, identity, views, targets, null);

    internal static DrawingBatch ApplySnapshot(DrawingBatch raw, RequestIdentity identity, IReadOnlyList<ViewRequest> views, IReadOnlyList<Plane> targets,
        IReadOnlyDictionary<string, SourceSection>? metadata)
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
        var plan = PlacementPlan.Create(placements, targets.Select(Plain).ToArray(), views.Select(v => v.Settings?.Gap ?? DistanceSetting.Auto).ToArray());
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
                    KernelEdges = original.KernelEdges, NativeEdges = original.NativeEdges, NativeCalls = original.NativeCalls,
                    CurvedCandidateEdges = original.CurvedCandidateEdges, PlanarNativeEdges = original.PlanarNativeEdges, NativeFragments = original.NativeFragments,
                    MultipleThreads = original.MultipleThreads, HiddenPoints = original.HiddenPoints, DuplicateSegments = original.DuplicateSegments,
                    ProjectingSegments = original.ProjectingSegments, EmptyNativeSlots = original.EmptyNativeSlots, ZeroLengthNativeSegments = original.ZeroLengthNativeSegments,
                    ToleranceDroppedPieces = original.ToleranceDroppedPieces, SubToleranceSourceEdges = original.SubToleranceSourceEdges,
                    SubToleranceBoundsGaps = original.SubToleranceBoundsGaps,
                    UncertainSectionComponents = original.UncertainSectionComponents };
                results.Add(result); result._diagnostics.AddRange(original.Diagnostics.Where(d => metadata is null || !d.StartsWith("Style ", StringComparison.Ordinal)));
                var frame = original.Frame; frame.Transform(transform); result.Frame = frame;
                if (settings is not null) result._diagnostics.Add(settings.Describe());
                var range = settings is null ? null : new ViewRange(settings);
                foreach (var item in original.Items)
                {
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
                            Add(part, depthPart ?? depthGeometry);
                        }
                    }
                    else Add(geometry.Duplicate(), depthGeometry);

                    void Add(GeometryBase part, GeometryBase depths)
                    {
                        try
                        {
                            var box = depths.GetBoundingBox(Transform.PlaneToPlane(original.Frame.Plane, Plane.WorldXY));
                            var span = new DepthInterval(-box.Max.Z, -box.Min.Z);
                            var kind = item.Class;
                            var representative = depths is Curve c ? -original.Frame.Plane.DistanceTo(c.PointAt(c.Domain.Mid)) : span.Representative;
                            if (part is Curve && kind == LineClass.Visible && range?.Classify(representative, false) == RangeClassification.Beyond) kind = LineClass.Beyond;
                            if (!part.Transform(transform)) throw new InvalidOperationException("Placement failed.");
                            result.Pieces.Add(new(part, new PreparedLeaf(item.SourceIdentity, item.SourcePath, part) { Section = metadata?.GetValueOrDefault(item.SourceIdentity) ?? item.Source, SourceItem = item.SourceItem }, kind)
                            { Profile = item.Profile, KernelOwned = item.KernelOwned, Depths = span, Intensity = range?.Intensity(span, kind) ?? 1 });
                        }
                        catch { part.Dispose(); throw; }
                    }
                }
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
                    var loops = region.Loops.Select(l => new SectionLoop(Move(l.Curve), l.IsHole,
                        l.Edges.Select(e => new SectionEdge(Move(e.Curve), e.Origin)).ToArray())).ToArray();
                    var hatch = (Hatch?)region.Hatch?.Duplicate(); hatch?.Transform(transform);
                    result.Regions.Add(new(region with { Loops = loops, Hatch = hatch }, (int[])entry.SourcePath.Clone()) { Source = metadata?.GetValueOrDefault(entry.Region.SourceIds[0]) ?? entry.Source, SourceItem = entry.SourceItem });
                }
                Curve Move(Curve c) { var copy = c.DuplicateCurve(); if (!copy.Transform(transform)) { copy.Dispose(); throw new InvalidOperationException("Region placement failed."); } return copy; }
            }
            return new DrawingBatch(identity, results);
        }
        catch { foreach (var view in results) view.Dispose(); throw; }
    }
}
