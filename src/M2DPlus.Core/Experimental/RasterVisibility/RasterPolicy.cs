namespace M2DPlus.Core.Experimental.RasterVisibility;

/// <summary>Versioned precision and whole-projection memory estimates, not hard native allocation guarantees.</summary>
public static class RasterPolicy
{
    public const string Identity = "raster-ray-triangles-v2/pixel8192-mesh32-double/bias2tol+mesh/no-face-shortcut";
    public const int MaximumResolution = 8192;
    public const long MiB = 1024L * 1024;
    // Retained meshes, triangle list capacity and final array, candidate/output headroom.
    public static long MeshBytes(long triangles) => checked(64 * MiB + triangles * 320);
    public static long Reservation(SceneCost scene) => checked(128 * MiB +
        (scene.MeshTriangles + scene.BrepFaces * 512) * 320 + scene.Sources * 4096L);
    public static double MeshTolerance(double tolerance, double nominalPixel) => Math.Max(tolerance, nominalPixel / 32);
    public static double DepthAllowance(double tolerance, double meshTolerance) => 2 * tolerance + meshTolerance;
}
