namespace M2DPlus.Core.Visibility;

/// <summary>Parallel view coordinates: larger Z is closer to the viewer.</summary>
public readonly record struct Vertex(double X, double Y, double Z);
public sealed record PlanarFace(Vertex[][] Loops)
{
    public string? Source { get; init; }
    /// <summary>Certified outward normal of a closed, outward-oriented solid only.
    /// Null for unoriented/open sources; loop winding must never substitute for this certificate.</summary>
    public Vertex? OutwardNormal { get; init; }
    /// <summary>Owned coordinates before rounded view projection, for view-independent contact certificates.</summary>
    public Vertex[][]? WorldLoops { get; init; }
    public Vertex? WorldOutwardNormal { get; init; }
}
public sealed record CandidateEdge(Vertex A, Vertex B, string Source)
{
    /// <summary>Indices into the solve's face list; empty for non-surface candidates.</summary>
    public int[] AdjacentFaces { get; init; } = [];
    public Vertex? WorldA { get; init; }
    public Vertex? WorldB { get; init; }
}
public enum VisibilityClass { Visible, Hidden, Unresolved }
public readonly record struct EdgeInterval(int Edge, double Start, double End, VisibilityClass Class);
public sealed record VisibilityResult(IReadOnlyList<EdgeInterval> Intervals, long FaceTests, long ExactFallbacks,
    IReadOnlyList<string> Diagnostics)
{
    public IReadOnlyList<ContactInterval> Contacts { get; init; } = [];
}
public sealed record VisibilityOptions(double DepthTolerance, int Threads = 1, bool UseIndex = true)
{
    public bool Joints { get; init; } = true;
    // Deterministic per-candidate limits. Exhaustion is uncertainty, never visibility.
    public int MaxPairOperations { get; init; } = 65536;
    public int MaxFacesPerEdge { get; init; } = 65536;
    /// <summary>Work items done of the total: prepared faces, then solved edges, as one count that only grows.</summary>
    public Action<int, int>? Progress { get; init; }
    public Action<VisibilityWork>? ObserveWork { get; init; }
}
public sealed record VisibilityWork(long ExactFallbacks, double ExactSeconds, int ExactFaces, int BudgetEdges);

internal static class ContactGeometry
{
    internal static PlanarFace Face(PlanarFace face) => face.WorldLoops is null ? face : face with
    { Loops=face.WorldLoops,OutwardNormal=face.WorldOutwardNormal,WorldLoops=null,WorldOutwardNormal=null };
    internal static CandidateEdge Edge(CandidateEdge edge) => edge.WorldA is {} a && edge.WorldB is {} b
        ? edge with { A=a,B=b,WorldA=null,WorldB=null } : edge;
}

internal readonly record struct P<T>(T X, T Y, T Z) where T : IScalar<T>
{
    public static P<T> From(Vertex p) => new(T.From(p.X), T.From(p.Y), T.From(p.Z));
    public static P<T> operator -(P<T> a, P<T> b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static P<T> Cross(P<T> a, P<T> b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
    public static T Dot(P<T> a, P<T> b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
}

internal sealed class Polygon<T> where T : IScalar<T>
{
    public P<T>[][] Loops { get; }
    public P<T> Normal { get; }
    public T D { get; }
    public P<T> Anchor { get; }
    public Polygon(PlanarFace face, P<T> normal, T d, P<T> anchor)
    {
        Loops = face.Loops.Select(l => l.Select(P<T>.From).ToArray()).ToArray();
        Normal = normal; D = d; Anchor = anchor;
    }
    public Polygon(PlanarFace face)
    {
        Loops = face.Loops.Select(l => l.Select(P<T>.From).ToArray()).ToArray();
        var zero = T.From(0);
        foreach (var loop in Loops)
            for (int i = 1; i + 1 < loop.Length; i++)
            {
                var n = P<T>.Cross(loop[i] - loop[0], loop[i + 1] - loop[0]);
                if (T.Compare(n.X, zero) == 0 && T.Compare(n.Y, zero) == 0 && T.Compare(n.Z, zero) == 0) continue;
                Normal = n; Anchor = loop[0]; D = P<T>.Dot(n, Anchor); return;
            }
        Normal = new(zero, zero, zero); D = zero; Anchor = Loops[0][0];
    }
}
