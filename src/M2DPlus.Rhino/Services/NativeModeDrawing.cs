using System.Diagnostics;
using M2DPlus.Core;
using M2DPlus.Core.Sections;
using M2DPlus.Rhino.Sections;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace M2DPlus.Rhino.Services;

/// <summary>Rhino owns all visibility, including planar/analytic bodies, points and clipping sections.
/// No composition, physical section clipping, glass substitution, verification or uncertainty promotion.</summary>
internal static class NativeModeDrawing
{
    internal static ViewDrawing Solve(PreparedScene scene, IReadOnlyList<PreparedLeaf> sources,
        ViewRequest view, NativeSettings settings, CancellationToken token)
    {
        var effective = view.Settings!;
        var result = new ViewDrawing { Name = view.Name, Settings = effective, Tolerance = effective.Tolerance, MultipleThreads = true };
        var timer = Stopwatch.StartNew();
        var invalid = 0;
        var unset = 0;
        var cuts = new Dictionary<PreparedLeaf, List<Curve>>();
        try
        {
            if (view.Error is not null) { result.Error = view.Error; return result; }
            result.Frame = view.Resolve(scene.Bounds, effective.Tolerance);
            result._diagnostics.Add("native: joints ignored; Rhino controls contact edges and duplicate ownership.");
            if (effective.Glass == GlassMode.Clear)
                result._diagnostics.Add("native: glass = clear ignored; all bodies retain Rhino occlusion.");
            if (view.Cut is not null)
                result._diagnostics.Add("native: HLD clipping-plane section cut lines only, no closed material regions or hatches; cuts and hatch settings ignored.");
            if (sources.Count == 0) return result;

            using var phase = result.Phases.Start("native setup");
            using var viewport = new ViewportInfo();
            NativeViewport.Configure(viewport, result.Frame, view.Direction, scene.Bounds, effective.Tolerance);
            var options = new NativeHldOptions(true, effective.RequestedHidden);
            var parameters = new HiddenLineDrawingParameters
            {
                AbsoluteTolerance = effective.Tolerance, Flatten = false,
                IncludeHiddenCurves = options.IncludeHiddenCurves,
                IncludeTangentEdges = effective.Tangents, IncludeTangentSeams = effective.Seams,
                OccludingSectionOption = true
            };
            parameters.SetViewport(viewport);
            var planes = new List<Plane>();
            if (view.Cut is { } cut)
            {
                planes.Add(cut.Plane);
                if (effective.FarClip != FarClipMode.None && effective.Depth > 0)
                    planes.Add(new Plane(cut.Plane.Origin - cut.Plane.Normal * effective.Depth, -cut.Plane.Normal));
                foreach (var plane in planes) parameters.AddClippingPlane(plane);
            }
            if (view.Cut is { ScopeEnabled: true })
                result._diagnostics.Add("native: clipping participation is delegated to Rhino HLD; excluded sources may still be clipped (observed for curves on Rhino 8.36). No scope correction is applied.");
            foreach (var source in VisibilityInputOrder.Native(sources, s => new VisibilityInput(s.Geometry, s), settings))
            {
                token.ThrowIfCancellationRequested();
                // Supply the requested per-source clipping lists; native mode retains Rhino's selective-clipping errors.
                var active = view.Cut is { } scope && scope.Participates(source) ? planes : new List<Plane>();
                if (!parameters.AddGeometryAndPlanes(source.Geometry, source, true, active))
                { invalid++; result._diagnostics.Add($"native: Rhino rejected source {source.Identity}."); }
            }
            settings.ObserveNativeInputs?.Invoke(sources.Select(s => new VisibilityInput(s.Geometry, s)).ToArray());
            options.Begin(result, sources.Select(s => new VisibilityInput(s.Geometry, s)), settings);
            NativeDrawing.CountHld(); result.NativeCalls++;
            phase.Next("native solve");
            using var drawing = HiddenLineDrawing.Compute(parameters, true, NativeDrawing.HldProgress, token);
            token.ThrowIfCancellationRequested();
            if (drawing is null) throw new InvalidOperationException("Native HLD returned null without cancellation.");
            if (!drawing.WorldToHiddenLine.TryGetInverse(out var inverse)) throw new InvalidOperationException("Invalid native frame.");
            var toPlane = Transform.PlanarProjection(result.Frame.Plane) * inverse;
            var crop = view.Cut?.Crop ?? (view.Named is { Crop: true } ? result.Frame : (Rectangle3d?)null);
            var cutOnly = effective.CutOnly || view.Cut is not null && effective.FarClip != FarClipMode.None && effective.Depth == 0;
            var parents = new HashSet<int>();
            phase.Next("output construction");
            foreach (var segment in drawing.Segments)
            {
                token.ThrowIfCancellationRequested();
                var visibility = segment.SegmentVisibility;
                // Unset proxies need not be dereferenceable. In native mode they are omitted, as native output,
                // without auditing or inventing an Unresolved class.
                if (visibility == HiddenLineDrawingSegment.Visibility.Unset) { unset++; continue; }
                if (visibility == HiddenLineDrawingSegment.Visibility.Duplicate) { result.DuplicateSegments++; continue; }
                if (visibility == HiddenLineDrawingSegment.Visibility.Projecting) { result.ProjectingSegments++; continue; }
                if (visibility != HiddenLineDrawingSegment.Visibility.Visible && visibility != HiddenLineDrawingSegment.Visibility.Hidden)
                { invalid++; continue; }
                var parent = segment.ParentCurve;
                if (parent?.SourceObject?.Tag is not PreparedLeaf source || segment.CurveGeometry is not { } proxy)
                { invalid++; continue; }
                parents.Add(parent.Index); result.NativeFragments++;
                var section = parent.SilhouetteType == SilhouetteType.SectionCut;
                var near = section && parent.ClippingPlaneIndex == 0;
                if (visibility == HiddenLineDrawingSegment.Visibility.Hidden && (!effective.RequestedHidden || section)) continue;
                if (cutOnly && !near) continue;
                if (section && !near && effective.FarClip != FarClipMode.Line) continue;
                using var flat = proxy.DuplicateCurve();
                using var depth = proxy.DuplicateCurve();
                if (!flat.Transform(toPlane) || !depth.Transform(inverse)) throw new InvalidOperationException("Native curve extraction failed.");
                foreach (var part in crop is { } frame ? NativeDrawing.Crop(flat, frame, effective.Tolerance) : [flat.DuplicateCurve()])
                {
                    if (near)
                    {
                        if (!cuts.TryGetValue(source, out var lines)) cuts.Add(source, lines = new());
                        lines.Add(part);
                    }
                    else
                    {
                        var kind = section ? LineClass.FarClip : visibility == HiddenLineDrawingSegment.Visibility.Hidden ? LineClass.Hidden : LineClass.Visible;
                        result.Pieces.Add(new(part, source, kind, depth.Trim(part.Domain)) { Profile = segment.IsSceneSilhouette });
                    }
                }
            }
            result.NativeEdges = result.CurvedCandidateEdges = parents.Count;
            if (!cutOnly)
                foreach (var point in drawing.Points)
                {
                    token.ThrowIfCancellationRequested();
                    if (point.PointVisibility == HiddenLineDrawingPoint.Visibility.Hidden) { result.HiddenPoints++; continue; }
                    if (point.PointVisibility != HiddenLineDrawingPoint.Visibility.Visible || point.SourceObject?.Tag is not PreparedLeaf source || !point.Location.IsValid)
                    { invalid++; continue; }
                    var world = point.Location; world.Transform(inverse);
                    var location = point.Location; location.Transform(toPlane);
                    if (crop is { } frame && !NativeDrawing.Inside(location, frame, effective.Tolerance)) continue;
                    result.Pieces.Add(new(new Point(location), source, LineClass.Visible, new Point(world)));
                }
            foreach (var (source, lines) in cuts)
            {
                // OutlineOnly is the existing Cut/null-Hatch contract for open outlines. Do not certify Rhino's curves
                // as material regions: the exact builder requires solid certificates and its own intersections.
                var loops = lines.Select(c => new SectionLoop(c.DuplicateCurve(), false, [])).ToArray();
                var region = new SectionRegion(source.Identity + "/native", loops, null, [source.Identity],
                    view.Cut!.Style(source), SectionStatus.OutlineOnly, 0, null);
                result.Regions.Add(new(region, source.Path) { Source = source.Section, SourceItem = source.SourceItem });
            }
            if (unset != 0 || invalid != 0)
                result._diagnostics.Add($"native: omitted {unset} Unset records and {invalid} rejected/invalid records; no visibility audit.");
        }
        catch (OperationCanceledException) { result.Dispose(); throw; }
        catch (Exception ex) { result.Dispose(); result.Error = ex.Message; }
        finally
        {
            foreach (var line in cuts.Values.SelectMany(c => c)) line.Dispose();
            result.Pieces.Sort(NativeDrawing.ComparePieces);
            result.Report = new("Rhino HiddenLineDrawing (all sources)", 0, invalid, scene.Unsupported, scene.Conversions, false)
                { ValidationDomain = "native: Rhino HiddenLineDrawing, not certified" };
            result.Seconds = timer.Elapsed.TotalSeconds;
        }
        return result;
    }
}
