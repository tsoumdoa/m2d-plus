namespace M2DPlus.Core;

public readonly record struct SceneCost(long GeometryBytes, long BrepFaces, long MeshTriangles, int Sources)
{
    // Empirical conservative estimates, not native allocation or execution-time guarantees.
    public long Estimate(int views) => checked(GeometryBytes * 4 + BrepFaces * 2048 + MeshTriangles * 96 + Sources * 4096L +
        views * (65536L + GeometryBytes / 8 + BrepFaces * 512 + MeshTriangles * 24 + Sources * 512L));
    public bool Admitted(int views, long budget) => views > 0 && Estimate(views) <= budget &&
        BrepFaces * views <= 600_000 && MeshTriangles * views <= 4_000_000;
    public const string Refusal = "Worker unavailable: too large for in-process in this build";
}
