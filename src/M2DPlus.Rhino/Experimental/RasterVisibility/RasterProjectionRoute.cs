using M2DPlus.Core;
using M2DPlus.Core.Experimental.RasterVisibility;
using M2DPlus.Rhino.Services;
using Rhino.Geometry;

namespace M2DPlus.Rhino.Experimental.RasterVisibility;

internal static class RasterProjectionRoute
{
    internal static void Solve(IReadOnlyList<VisibilityInput> inputs, ViewDrawing output, ViewRequest view,
        NativeSettings settings, PreparedScene scene, CancellationToken token)
    {
        if (view.Settings?.Visibility != VisibilityMode.Raster)
        { HldProjection.Solve(inputs, output, view, settings, scene.Bounds, token); return; }
        if (inputs.Count == 0) return;
        // Same ordering and research observation boundary as HLD. Observer failures never trigger fallback.
        var ordered = VisibilityInputOrder.Native(inputs, i => i, settings);
        var originalPieces = output.Pieces.Count; var originalHiddenPoints = output.HiddenPoints;
        var originalDiagnostics = output._diagnostics.Count;
        try
        {
            token.ThrowIfCancellationRequested();
            var budget = settings.Scheduler.BudgetBytes;
            if (!HostPlatform.IsolatedWorkers) budget = Math.Min(budget, InProcessPolicy.Budget(HostPlatform.SafeMemory()));
            budget -= scene.Cost.Estimate(1);
            if (RasterPolicy.Reservation(scene.Cost) > budget)
                throw new NotSupportedException("raster memory admission: estimated meshes, triangles, image and output exceed projection budget");
            if (!(scene.MillimetresPerUnit > 0) || !double.IsFinite(scene.MillimetresPerUnit))
                throw new NotSupportedException("document unit scale unavailable");
            using var phase = output.Phases.Start("raster visibility");
            var span = scene.Bounds.Diagonal.Length;
            var resolution = Math.Clamp((int)Math.Min(RasterPolicy.MaximumResolution, span / (output.Tolerance * 64)), 64, RasterPolicy.MaximumResolution);
            using var result = RasterProjection.Solve(ordered, view, output.Frame, output.Tolerance,
                scene.AngleTolerance, resolution, Math.Min(16, Environment.ProcessorCount), token: token, budgetBytes: budget);
            token.ThrowIfCancellationRequested();
            // Publish only a complete projection. Sections, crop, precedence and presentation stay with their callers.
            output.Pieces.AddRange(result.Drawing.Pieces); result.Drawing.Pieces.Clear();
            output.HiddenPoints += result.Drawing.HiddenPoints;
            output.RasterRan = true; output.RasterPixel = result.Drawing.RasterPixel * scene.MillimetresPerUnit;
            output.RasterTriangles = result.Drawing.RasterTriangles; output.RasterCandidates = result.Drawing.RasterCandidates;
            output.RasterEstimatedBytes = result.Drawing.RasterEstimatedBytes;
            output._diagnostics.Add($"raster engine: approximate, pixel-limited visibility; small gaps, contacts, grazing faces and duplicate strokes may differ from HLD; visibility not certified. Policy={RasterPolicy.Identity}.");
            if (result.Timings.InvalidInputs > 0)
                output._diagnostics.Add($"raster engine: {result.Timings.InvalidInputs} prepared geometries fail Rhino validity checks; their tessellations and linework are approximate, not certified.");
            output._diagnostics.Add($"Raster: triangles={output.RasterTriangles}; candidates={output.RasterCandidates}; estimated projection bytes={output.RasterEstimatedBytes}; mesh tolerance={result.Timings.MeshTolerance:G6} document units.");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error)
        {
            // This also covers failure during publication or disposal of the completed temporary result.
            // Never let any experimental piece or counter survive into whole-view HLD fallback.
            while (output.Pieces.Count > originalPieces)
            {
                var index = output.Pieces.Count - 1; var piece = output.Pieces[index];
                output.Pieces.RemoveAt(index); piece.Dispose();
            }
            output.HiddenPoints = originalHiddenPoints; output.RasterRan = false; output.RasterPixel = 0;
            output.RasterTriangles = 0; output.RasterCandidates = 0; output.RasterEstimatedBytes = 0;
            while (output._diagnostics.Count > originalDiagnostics) output._diagnostics.RemoveAt(output._diagnostics.Count - 1);
            token.ThrowIfCancellationRequested();
            var reason = error is AggregateException aggregate ? string.Join("; ", aggregate.Flatten().InnerExceptions.Select(e => e.Message).Distinct()) : error.Message;
            output._diagnostics.Add("raster engine: " + reason + "; used Rhino HLD for the whole view");
            HldProjection.Solve(inputs, output, view, settings, scene.Bounds, token);
        }
    }
}
