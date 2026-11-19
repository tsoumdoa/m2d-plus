using M2DPlus.Core;
using M2DPlus.Rhino.Services;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;

namespace M2DPlus.Rhino.Experimental.PipeVisibility;

internal sealed record PipeAdmission(string? Reason, IReadOnlyList<PreparedLeaf> Sources);

/// <summary>No physical trimming of solids in the trial. Enclosures certify only
/// strict separation; ambiguous planes get a native intersection and fail closed.</summary>
internal static class PipeBoxDepth
{
    internal static PipeAdmission Admit(IReadOnlyList<PreparedLeaf> sources, ViewRequest view,
        double tolerance, CancellationToken token)
    {
        if (view.Kind != ViewSourceKind.Box || view.Cut is not { } cut) return new(null, sources);
        var range = new ViewRange(cut.SourceDepth ?? view.Settings?.Depth ?? 0, farClip: cut.FarClip, tolerance: tolerance);
        if (range.Regime != DepthRegime.Volume) return new("box depth has no trustworthy projection volume", sources);
        Plane? far = range.ClipsFar ? new(cut.Plane.Origin - cut.Plane.Normal * range.Depth, -cut.Plane.XAxis, cut.Plane.YAxis) : null;
        var retained = new List<PreparedLeaf>();
        var frontCuts = 0; var backCuts = 0; var frontUncertain = 0; var backUncertain = 0;
        foreach (var source in sources)
        {
            token.ThrowIfCancellationRequested();
            if (!cut.Participates(source) || source.Geometry is not Brep body) { retained.Add(source); continue; }
            int front = Side(body, cut.Plane, tolerance), back = far is { } fp ? Side(body, fp, tolerance) : -1;
            if (front > 0 || back > 0) continue;
            if (front == 0) { if (Intersects(body, cut.Plane, tolerance)) frontCuts++; else frontUncertain++; }
            if (back == 0) { if (Intersects(body, far!.Value, tolerance)) backCuts++; else backUncertain++; }
            retained.Add(source);
        }
        var reasons = new List<string>();
        if (frontCuts > 0) reasons.Add($"box front plane cuts {frontCuts} object(s)");
        if (backCuts > 0) reasons.Add($"box back plane cuts {backCuts} object(s)");
        if (frontUncertain > 0) reasons.Add($"box front plane separation cannot be certified for {frontUncertain} object(s)");
        if (backUncertain > 0) reasons.Add($"box back plane separation cannot be certified for {backUncertain} object(s)");
        return new(reasons.Count == 0 ? null : string.Join("; ", reasons), retained);
    }

    private static int Side(Brep body, Plane plane, double tolerance)
    {
        var bounds = body.GetBoundingBox(Transform.PlaneToPlane(plane, Plane.WorldXY));
        var magnitude = plane.Origin.DistanceTo(Point3d.Origin) + (bounds.IsValid ? bounds.GetCorners().Max(p => p.DistanceTo(Point3d.Origin)) : double.NaN);
        return bounds.IsValid ? PipeEligibility.PlaneSide(bounds.Min.Z, bounds.Max.Z,
            ViewSourceCull.Margin(tolerance, magnitude)) : 0;
    }

    private static bool Intersects(Brep body, Plane plane, double tolerance)
    {
        // Rhino's false/no-loci return does not prove separation (disconnected
        // shells and failed native intersections included). Keep HLD in that case.
        Curve[]? curves = null;
        try
        {
            Intersection.BrepPlane(body, plane, tolerance, out curves, out var points);
            return curves is { Length: > 0 } || points is { Length: > 0 };
        }
        finally { if (curves is not null) foreach (var curve in curves) curve.Dispose(); }
    }
}
