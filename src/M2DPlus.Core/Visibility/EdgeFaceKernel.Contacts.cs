namespace M2DPlus.Core.Visibility;

public enum ContactKind { Seated, Flush, Approximate }
public readonly record struct ContactInterval(int Edge, int Face, double Start, double End, ContactKind Kind);

public static partial class EdgeFaceKernel
{
    // Contact predicates use the same filtered/exact arithmetic as occlusion.
    // The dominant-plane projection also handles faces edge-on to the camera.
    private static IReadOnlyList<ContactInterval> Contacts<T>(Polygon<T> face, CandidateEdge edge, int ei, int fi,
        IReadOnlyList<PlanarFace> faces, double tolerance, CancellationToken token, int limit,double endpointSpan) where T : IScalar<T>
    {
        var zero = T.From(0); var one = T.From(1);
        var a = P<T>.From(edge.A); var b = P<T>.From(edge.B); var n = face.Normal;
        T Abs(T v) => T.Compare(v, zero) < 0 ? zero - v : v;
        var ax = Abs(n.X); var ay = Abs(n.Y); var az = Abs(n.Z);
        int drop = T.Compare(ax, ay) >= 0 && T.Compare(ax, az) >= 0 ? 0 : T.Compare(ay, az) >= 0 ? 1 : 2;
        if (T.Compare(ax + ay + az, zero) == 0) return [];
        var r0 = P<T>.Dot(n, a - face.Anchor); var r1 = P<T>.Dot(n, b - face.Anchor);
        var band = T.From(tolerance) * (ax + ay + az);
        // A transverse crossing has no positive-length contact interval. Only
        // a straight support whose BOTH ends could be incident is approximate
        // contact; ordinary occlusion events retain their depth tolerance.
        if (T.Compare(Abs(r0), band) > 0 || T.Compare(Abs(r1), band) > 0) return [];
        bool incident = T.Compare(r0, zero) == 0 && T.Compare(r1, zero) == 0;
        P<T> Map(P<T> p) => drop switch { 0 => new(p.Y, p.Z, one), 1 => new(p.X, p.Z, one), _ => new(p.X, p.Y, one) };
        Vertex MapVertex(Vertex p) => drop switch { 0 => new(p.Y, p.Z, 1), 1 => new(p.X, p.Z, 1), _ => new(p.X, p.Y, 1) };
        var mapped = new PlanarFace(faces[fi].Loops.Select(l => l.Select(MapVertex).ToArray()).ToArray());
        var polygon = new Polygon<T>(mapped);
        var ma = Map(a); var mb = Map(b); var delta = mb - ma;
        if (T.Compare(delta.X, zero) == 0 && T.Compare(delta.Y, zero) == 0) return [];
        // Clip supplies the exact polygon event arrangement, including holes.
        var result = new List<ContactInterval>();
        void Classify(T mid, double start, double end)
        {
            token.ThrowIfCancellationRequested();
            var x = ma.X + delta.X * mid; var y = ma.Y + delta.Y * mid;
            bool plus = Side(polygon, x, y, zero - delta.Y, delta.X, token, limit);
            bool minus = Side(polygon, x, y, delta.Y, zero - delta.X, token, limit);
            bool flush = false, approximate = !incident;
            foreach (int adjacent in edge.AdjacentFaces)
            {
                if ((uint)adjacent >= (uint)faces.Count) throw new ArithmeticException("Invalid contact adjacency");
                var own = new Polygon<T>(faces[adjacent]);
                var cross = P<T>.Cross(n, own.Normal);
                bool parallel = T.Compare(cross.X, zero) == 0 && T.Compare(cross.Y, zero) == 0 && T.Compare(cross.Z, zero) == 0;
                var ownOutward = faces[adjacent].OutwardNormal is { } on ? P<T>.From(on) : own.Normal;
                var foreignOutward = faces[fi].OutwardNormal is { } fn ? P<T>.From(fn) : n;
                if (T.Compare(P<T>.Dot(ownOutward, foreignOutward), zero) <= 0) continue;
                if (!parallel)
                {
                    // Exact angled planes are seated even for very thin faces.
                    // Only a genuinely nonplanar input can make its fitted
                    // support an uncertain near-coplanar continuation.
                    approximate |= own.Loops.SelectMany(l=>l).Any(p=>T.Compare(P<T>.Dot(own.Normal,p-own.Anchor),zero)!=0)
                        && own.Loops.SelectMany(l => l).All(p => T.Compare(Abs(P<T>.Dot(n, p - face.Anchor)), band) <= 0);
                    continue;
                }
                var residual = P<T>.Dot(n, own.Anchor - face.Anchor);
                if (T.Compare(residual, zero) != 0) { approximate |= T.Compare(Abs(residual), band) <= 0; continue; }
                if (own.Loops.SelectMany(l => l).Any(p => T.Compare(P<T>.Dot(n, p - face.Anchor), zero) != 0)) { approximate = true; continue; }
                var ownMapped = new Polygon<T>(new(faces[adjacent].Loops.Select(l => l.Select(MapVertex).ToArray()).ToArray()));
                // Opposite interior half-neighbourhoods prove continuity. Normals
                // alone cannot distinguish stacked panels from coincident blocks.
                flush |= plus && Side(ownMapped, x, y, delta.Y, zero - delta.X, token, limit)
                    || minus && Side(ownMapped, x, y, zero - delta.Y, delta.X, token, limit);
            }
            // Approximate face planarity must never supply an exact contact proof.
            foreach (var p in face.Loops.SelectMany(l => l))
                approximate |= T.Compare(P<T>.Dot(n, p - face.Anchor), zero) != 0;
            result.Add(new(ei, fi, start, end, approximate ? ContactKind.Approximate : flush ? ContactKind.Flush : ContactKind.Seated));
        }
        Clip(polygon, new(MapVertex(edge.A) with { Z = 0 }, MapVertex(edge.B) with { Z = 0 }, edge.Source), 0, token, limit, Classify, tolerance,endpointSpan);
        return result;
    }

    // Exact symbolic infinitesimal: classify p + epsilon*d, epsilon > 0.
    // This is a side predicate over a complete event cell, never a sampled offset.
    private static bool Side<T>(Polygon<T> polygon, T x, T y, T dx, T dy, CancellationToken token, int limit) where T : IScalar<T>
    {
        var zero = T.From(0); bool inside = false; int operations = 0;
        int Sign(T constant, T infinitesimal) { int sign = T.Compare(constant, zero); return sign != 0 ? sign : T.Compare(infinitesimal, zero); }
        foreach (var loop in polygon.Loops)
            for (int i = 0; i < loop.Length; i++)
            {
                if ((operations++ & 255) == 0) token.ThrowIfCancellationRequested();
                if (operations > limit) throw new WorkBudgetException();
                var a = loop[i]; var b = loop[(i + 1) % loop.Length];
                if ((Sign(a.Y - y, zero - dy) > 0) == (Sign(b.Y - y, zero - dy) > 0)) continue;
                int cross = Sign((b.X - a.X) * (y - a.Y) - (b.Y - a.Y) * (x - a.X), (b.X - a.X) * dy - (b.Y - a.Y) * dx);
                if (cross == T.Compare(b.Y, a.Y)) inside = !inside;
            }
        return inside;
    }
}
