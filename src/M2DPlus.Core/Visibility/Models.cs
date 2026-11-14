namespace M2DPlus.Core.Visibility;

/// <summary>Parallel view coordinates: larger Z is closer to the viewer.</summary>
public readonly record struct Vertex(double X, double Y, double Z);
public sealed record PlanarFace(Vertex[][] Loops);
public sealed record CandidateEdge(Vertex A, Vertex B, string Source);
public enum VisibilityClass { Visible, Hidden, Unresolved }
public readonly record struct EdgeInterval(int Edge, double Start, double End, VisibilityClass Class);
public sealed record VisibilityResult(IReadOnlyList<EdgeInterval> Intervals, long FaceTests, long ExactFallbacks,
    IReadOnlyList<string> Diagnostics);
public sealed record VisibilityOptions(double DepthTolerance, int Threads = 1, bool UseIndex = true);

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
    public Polygon(PlanarFace face, P<T> normal, T d)
    {
        Loops = face.Loops.Select(l => l.Select(P<T>.From).ToArray()).ToArray();
        Normal = normal; D = d;
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
                Normal = n; D = P<T>.Dot(n, loop[0]); return;
            }
        Normal = new(zero, zero, zero); D = zero;
    }
}
