using System.Diagnostics;
using M2DPlus.Core;
using M2DPlus.Rhino.Services;
using M2DPlus.Rhino.Sections;
using Rhino.Geometry;

namespace M2DPlus.Rhino.Experimental.PipeVisibility;

internal static class PipeProjection
{
    internal static string? Ineligible(PreparedScene scene, IReadOnlyList<PreparedLeaf> sources, ViewRequest view)
        => Admit(scene, sources, view, default).Reason;

    internal static PipeAdmission Admit(PreparedScene scene, IReadOnlyList<PreparedLeaf> sources, ViewRequest view, CancellationToken token)
    {
        var ids = sources.Select(s => s.Identity).ToHashSet(StringComparer.Ordinal);
        var leaves = scene.TransportLeaves.Where(l => ids.Contains(l.Identity)).ToArray();
        var unsupported = sources.Select(s => s.Geometry switch
        {
            var geometry when !geometry.IsValid => "invalid " + geometry.GetType().Name,
            Brep b when !b.IsValid || !b.IsSolid => "an invalid or open Brep",
            Brep or Curve or Point => null,
            var geometry => geometry.GetType().Name
        }).FirstOrDefault(s => s is not null);
        // SubD conversion changes the domain; only direct Extrusion/Surface conversion is admitted.
        if (unsupported is null && leaves.Any(l => l.Geometry.Value.Conversion?.StartsWith("SubD", StringComparison.Ordinal) == true))
            unsupported = "SubD geometry";
        unsupported ??= scene.Unsupported.Keys.FirstOrDefault();
        if (unsupported is null && view.Settings?.Glass == GlassMode.Clear && sources.Any(s => s.Section.Transparent))
            unsupported = "transparent bodies with glass = clear";
        var box = view.Kind == ViewSourceKind.Box && view.Cut is not null;
        var reason = PipeEligibility.Reason(scene.Millimetres, view.Error is null && view.Direction.IsValid,
            !box && view.Cut is not null && view.Kind is not (ViewSourceKind.Direction or ViewSourceKind.Vector or ViewSourceKind.NamedView),
            !box && (view.Cut is not null || view.Named is { Crop: true }), leaves.Any(l => l.Chain.Length > 0), unsupported);
        if (reason is not null) return new(reason, sources);
        try { return PipeBoxDepth.Admit(sources, view, view.Settings?.Tolerance ?? scene.Tolerance, token); }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) { return new("box depth admission failed: " + error.Message, sources); }
    }

    internal static ViewDrawing Solve(PreparedScene scene, IReadOnlyList<PreparedLeaf> sources, ViewRequest view,
        CancellationToken token, double tolerance)
    {
        var result = new ViewDrawing { Name = view.Name, Settings = view.Settings, Tolerance = tolerance,
            Frame = view.Resolve(scene.Bounds, tolerance) };
        var strokes = new List<PipeGeometry.Stroke>();
        var dots = new List<PipeGeometry.Dot>();
        var owned = new List<Curve>();
        var timer = Stopwatch.StartNew();
        try
        {
            token.ThrowIfCancellationRequested();
            var inputs = new List<PipeGeometry.Input>();
            foreach (var source in sources)
            {
                if (view.Cut is not { } cut || !cut.Participates(source))
                { inputs.Add(new(source.Identity, source.Geometry)); continue; }
                Plane? far = cut.FarClip != FarClipMode.None && cut.SourceDepth is double depth
                    ? new(cut.Plane.Origin - cut.Plane.Normal * depth, -cut.Plane.XAxis, cut.Plane.YAxis) : null;
                if (source.Geometry is Curve curve)
                    foreach (var part in SectionPreparation.ClipCurve(curve, cut.Plane, far, tolerance))
                    { owned.Add(part); inputs.Add(new(source.Identity, part)); }
                else if (source.Geometry is not Point point || cut.Plane.DistanceTo(point.Location) <= 0 &&
                    (far is null || far.Value.DistanceTo(point.Location) <= 0)) inputs.Add(new(source.Identity, source.Geometry));
            }
            using (result.Phases.Start("pipe surface visibility"))
                SurfaceEvents.Draw(inputs,
                    result.Frame.Plane, view.Direction, tolerance, scene.AngleTolerance,
                    Math.Min(8, System.Environment.ProcessorCount), strokes, dots, token, tangents: true);
            token.ThrowIfCancellationRequested();
            var byId = sources.ToDictionary(s => s.Identity, StringComparer.Ordinal);
            var project = Transform.PlanarProjection(result.Frame.Plane);
            foreach (var stroke in strokes)
            {
                token.ThrowIfCancellationRequested();
                if (stroke.Class == "Hidden" && view.Settings?.Hidden == false) continue;
                using var flat = stroke.Curve.DuplicateCurve();
                if (!flat.Transform(project)) throw new InvalidOperationException("Pipe projection failed");
                var parts = view.Cut?.Crop is { } crop ? NativeDrawing.Crop(flat, crop, tolerance) : [flat.DuplicateCurve()];
                foreach (var part in parts)
                {
                    Curve? support = null;
                    try
                    {
                        support = CurveIntervals.Trim(stroke.Curve, part.Domain) ?? throw new InvalidOperationException("Pipe crop depth support failed");
                        result.Pieces.Add(new(part, byId[stroke.Source], stroke.Class == "Hidden" ? LineClass.Hidden : LineClass.Visible, support));
                    }
                    catch { part.Dispose(); support?.Dispose(); throw; }
                }
            }
            foreach (var dot in dots)
            {
                token.ThrowIfCancellationRequested();
                if (dot.Class == "Hidden") { result.HiddenPoints++; continue; }
                var flat = dot.Position; flat.Transform(project);
                if (view.Cut?.Crop is { } crop && !NativeDrawing.Inside(flat, crop, tolerance)) continue;
                result.Pieces.Add(new(new Point(flat), byId[dot.Source], LineClass.Visible, new Point(dot.Position)));
            }
            result.Pieces.Sort(NativeDrawing.ComparePieces);
            result.Report = new("pipes (experimental)", 0, 0, scene.Unsupported, scene.Conversions, false,
                "Experimental closed-solid surface classifier; known wrong-visible junction intervals; no accuracy certification");
            result._diagnostics.Add("pipes engine: silhouettes always drawn; smooth manifold edges and seams skipped, so tangents and seams have no effect. Known wrong-visible junction intervals remain from the PoC.");
            if (view.Cut is { } boxCut)
                result._diagnostics.Add("pipes engine: box depth planes are separated from retained solids; curves/points use Route C depth clipping, output uses the shared 2D crop and Beyond/fade presentation. No physical cut/crop edges or Cut/Hatch regions are generated." +
                    (boxCut.FarClip == FarClipMode.None ? " farclip = none ignores the box back plane, as in Rhino HLD." : " Far clip output is empty because no retained solid meets the back plane."));
            result._diagnostics.Add($"Compute: pipe-surface-events-2e0f9b0; fingerprint={WorkerCodec.Fingerprint()}; threads={Math.Min(8, System.Environment.ProcessorCount)}.");
            result.Seconds = timer.Elapsed.TotalSeconds;
            return result;
        }
        catch (OperationCanceledException) { result.Dispose(); throw; }
        catch (Exception error)
        {
            result.Dispose(); result.Error = "pipes engine: " + error.Message;
            result.Report = new("pipes (experimental)", 0, 0, scene.Unsupported, scene.Conversions, false,
                "Experimental surface classifier failed; no projection published");
            result.Seconds = timer.Elapsed.TotalSeconds;
            return result;
        }
        finally { foreach (var stroke in strokes) stroke.Curve.Dispose(); foreach (var curve in owned) curve.Dispose(); }
    }
}
