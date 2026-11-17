namespace M2DPlus.Core;

public readonly record struct SceneCost(long GeometryBytes, long BrepFaces, long MeshTriangles, int Sources)
{
    // Native HLD has a much larger working set for curved Brep topology than scene preparation.
    // Count per instance, including every edge of a Brep with a non-planar face.
    public long CurvedBrepFaces { get; init; }
    public long CurvedBrepEdges { get; init; }
    // Small-model SW job-commit calibration: ~0.57 GiB fixed peak, then ~55 KiB per curved
    // face/edge toward curved-S. Round the slope up to 128 KiB (over 2x), independently of
    // preparation/archives. Curved-M is a 10x topology extrapolation, never a fitted large run.
    public long NativeHldWorkingBytes => checked((CurvedBrepFaces + CurvedBrepEdges) * (128L * 1024));
    // Empirical estimates, not native allocation or execution-time guarantees. Interactive dispatch
    // must include HLD too: a small input archive must not enable heavy fallback into the UI host.
    public long Estimate(int views) => checked(PreparationEstimate(views) + views * NativeHldWorkingBytes);
    internal long PreparationEstimate(int views) => checked(GeometryBytes * 4 + BrepFaces * 2048 + MeshTriangles * 96 + Sources * 4096L +
        views * (65536L + GeometryBytes / 8 + BrepFaces * 512 + MeshTriangles * 24 + Sources * 512L));
    // Interactive-host dispatch thresholds, never worker rejection limits. Keep the
    // host responsive and put very large scenes on the contained route even if cheap.
    public bool Admitted(int views, long budget) => views > 0 && Sources < 50_000 && Estimate(views) <= budget &&
        BrepFaces <= 600_000L / views && MeshTriangles <= 4_000_000L / views;
    public const string Refusal = "Worker unavailable: too large for in-process in this build";
}
