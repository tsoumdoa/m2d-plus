namespace M2DPlus.Core;

public readonly record struct SceneCost(long GeometryBytes, long BrepFaces, long MeshTriangles, int Sources)
{
    // Empirical conservative estimates, not native allocation or execution-time guarantees.
    public long Estimate(int views) => checked(GeometryBytes * 4 + BrepFaces * 2048 + MeshTriangles * 96 + Sources * 4096L +
        views * (65536L + GeometryBytes / 8 + BrepFaces * 512 + MeshTriangles * 24 + Sources * 512L));
    // Interactive-host dispatch thresholds, never worker rejection limits. Keep the
    // host responsive and put very large scenes on the contained route even if cheap.
    public bool Admitted(int views, long budget) => views > 0 && Sources < 50_000 && Estimate(views) <= budget &&
        BrepFaces <= 600_000L / views && MeshTriangles <= 4_000_000L / views;
    public const string Refusal = "Worker unavailable: too large for in-process in this build";
}
