namespace M2DPlus.Core.Experimental.RasterVisibility;

/// <summary>Versioned precision and whole-projection memory estimates, not hard native allocation guarantees.</summary>
public static class RasterPolicy
{
    public const string Identity = "raster-ray-triangles-v3/pixel8192-mesh32-double/bias2tol+mesh/no-face-shortcut";
    public const int MaximumResolution = 8192;
    public const long MiB = 1024L * 1024;
    // Retained meshes, triangle list capacity and final array, candidate/output headroom.
    public static long MeshBytes(long triangles) => checked(64 * MiB + triangles * 320);
    // Stage 1: planar faces normally need two triangles. Broad curved controls
    // need up to ~30k triangles/face, versus ~2,100 in densely trimmed scenes.
    // A continuous, monotone envelope covers the measured sparse/medium/dense
    // controls without a sudden reservation drop at a face-count threshold.
    // This is an empirical estimate; runtime guards still bound actual counts.
    public static long EstimatedTriangles(SceneCost scene)
    {
        var curved = scene.CurvedBrepFaces;
        var curvedTriangles = Math.Min(checked(curved * 32768), Math.Min(
            checked(1_048_576 + curved * 12288), checked(8_388_608 + curved * 1024)));
        return checked(scene.MeshTriangles + (scene.BrepFaces - curved) * 2 + curvedTriangles);
    }
    public static long MinimumProjection(SceneCost scene) => checked(128 * MiB +
        EstimatedTriangles(scene) * 320 + scene.Sources * 4096L);
    public static long ProjectionEstimate(SceneCost scene) => checked(MinimumProjection(scene) + 512 * MiB);
    // Estimate(n) already reserves HLD's native working set. Raster and HLD are
    // alternatives: add only Raster's excess, including the full image ceiling.
    public static long Reservation(SceneCost scene) => Math.Max(0, ProjectionEstimate(scene) - scene.NativeHldWorkingBytes);
    public static long MinimumReservation(SceneCost scene) => Math.Max(0, MinimumProjection(scene) - scene.NativeHldWorkingBytes);
    // Reuse both HLD's working set and unused rounded-cap headroom.
    public static long WorkerExtra(SceneCost scene) => Math.Max(0, checked(
        (scene.Estimate(1) - scene.NativeHldWorkingBytes) * 3 + WorkerMemoryPolicy.GiB +
        Math.Max(scene.NativeHldWorkingBytes, ProjectionEstimate(scene)) - WorkerMemoryPolicy.Required(scene)));
    public static long ProjectionReservation(SceneCost scene, int views, long budget)
    {
        var available = checked(budget - scene.Estimate(views) + scene.NativeHldWorkingBytes);
        return available < MinimumProjection(scene) ? 0 : Math.Min(ProjectionEstimate(scene), available);
    }
    public static long AdditionalReservation(SceneCost scene, long projectionBytes) =>
        Math.Max(0, projectionBytes - scene.NativeHldWorkingBytes);
    public static double MeshTolerance(double tolerance, double nominalPixel) => Math.Max(tolerance, nominalPixel / 32);
    public static double DepthAllowance(double tolerance, double meshTolerance) => 2 * tolerance + meshTolerance;
}
