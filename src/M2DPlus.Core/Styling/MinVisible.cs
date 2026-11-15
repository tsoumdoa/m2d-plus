using System.Collections.Immutable;

namespace M2DPlus.Core.Styling;

/// <summary>A point in the drawing plane (view frame coordinates, document units).</summary>
public readonly record struct Point2(double X, double Y);

/// <summary>How a line piece takes part in the <c>min visible</c> rule.</summary>
public enum MinVisibleRole
{
    /// <summary>Drawn linework the rule measures and may move: Visible (and profile), Beyond and Far clip.</summary>
    Drawn,
    /// <summary>A hidden piece. Never moved; it only tells how far the object reaches behind other geometry.</summary>
    Hidden,
    /// <summary>A piece whose visibility is undecided. An object with one is never ignored.</summary>
    Unresolved,
}

/// <summary>One projected line piece of one source. <paramref name="Points"/> is the piece as a 2D polyline in the
/// drawing plane (at least two points, ends first and last); <paramref name="Length"/> is its exact 2D length.</summary>
public sealed record MinVisiblePiece(string Source, MinVisibleRole Role, IReadOnlyList<Point2> Points, double Length);

public sealed record MinVisibleResult(ImmutableHashSet<string> Ignored)
{
    public static MinVisibleResult None { get; } = new(ImmutableHashSet<string>.Empty.WithComparer(StringComparer.Ordinal));
}

/// <summary>
/// The owner's <c>min visible</c> rule (REQUIREMENTS, "Ignoring objects seen only through small gaps"): an object is
/// ignored when it is partly hidden in the view and its longest continuous visible piece, measured in 2D in the
/// drawing, is shorter than the threshold. It is a pure presentation filter over the per-source classification.
/// <list type="bullet">
/// <item><b>Object</b> = one source identity. Pieces of one block instance belong to that instance only.</item>
/// <item><b>Continuous piece</b> = drawn pieces of the object joined where they touch, end to end or an end on
/// another piece, within <c>tolerance</c>. Its length is the sum of the joined pieces' 2D lengths, so an edge split
/// by the solve into adjacent fragments, or an outline of several edges, counts as one piece. Pieces with a gap
/// between them are never joined: each is a separate glimpse.</item>
/// <item><b>Partly hidden</b> = some hidden linework of the object reaches outside the 2D bounding box of its drawn
/// linework (by more than <c>tolerance</c>). An object's own outline is never hidden by the object itself, so
/// self-occluded back edges stay inside it: a small object that nothing else covers is never ignored. An object
/// covered only in its middle, with both extremes drawn, also counts as fully visible and is kept.</item>
/// <item>Never ignored: objects with no drawn piece, objects with an unresolved piece, and the sources the caller
/// exempts (cut objects). Points, cuts and hatches are not pieces here and are never moved.</item>
/// </list>
/// </summary>
public static class MinVisibleRule
{
    public static MinVisibleResult Evaluate(IEnumerable<MinVisiblePiece> pieces, double threshold, double tolerance,
        IReadOnlySet<string>? exempt = null)
    {
        ArgumentNullException.ThrowIfNull(pieces);
        if (!double.IsFinite(threshold) || threshold <= 0) return MinVisibleResult.None;
        var tol = double.IsFinite(tolerance) && tolerance > 0 ? tolerance : 1e-12;
        var ignored = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
        foreach (var group in pieces.GroupBy(p => p.Source, StringComparer.Ordinal))
        {
            if (exempt?.Contains(group.Key) == true) continue;
            var drawn = new List<MinVisiblePiece>(); var hidden = new List<MinVisiblePiece>(); var unresolved = false;
            foreach (var piece in group)
            {
                if (piece.Points.Count < 2) throw new ArgumentException("A piece needs at least two points.", nameof(pieces));
                if (piece.Role == MinVisibleRole.Drawn) drawn.Add(piece);
                else if (piece.Role == MinVisibleRole.Hidden) hidden.Add(piece);
                else unresolved = true;
            }
            if (unresolved || drawn.Count == 0 || hidden.Count == 0) continue;
            if (drawn.Any(p => p.Length >= threshold)) continue;
            if (!PartlyHidden(drawn, hidden, tol)) continue;
            if (drawn.Sum(p => p.Length) >= threshold && LongestJoined(drawn, threshold, tol) >= threshold) continue;
            ignored.Add(group.Key);
        }
        return new(ignored.ToImmutable());
    }

    internal static bool PartlyHidden(IReadOnlyList<MinVisiblePiece> drawn, IReadOnlyList<MinVisiblePiece> hidden, double tolerance)
    {
        double minX = double.PositiveInfinity, minY = double.PositiveInfinity, maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
        foreach (var piece in drawn)
            foreach (var p in piece.Points) { minX = Math.Min(minX, p.X); minY = Math.Min(minY, p.Y); maxX = Math.Max(maxX, p.X); maxY = Math.Max(maxY, p.Y); }
        return hidden.Any(piece => piece.Points.Any(p => p.X < minX - tolerance || p.X > maxX + tolerance || p.Y < minY - tolerance || p.Y > maxY + tolerance));
    }

    /// <summary>The longest joined length, stopping early once it reaches <paramref name="enough"/>. Every piece here
    /// is shorter than the threshold, so a grid with threshold-sized cells finds every contact in a few cells.</summary>
    internal static double LongestJoined(IReadOnlyList<MinVisiblePiece> drawn, double enough, double tolerance)
    {
        var parent = Enumerable.Range(0, drawn.Count).ToArray();
        var length = drawn.Select(p => p.Length).ToArray();
        var cell = Math.Max(enough, tolerance * 4);
        var grid = new Dictionary<(long, long), List<int>>();
        for (var i = 0; i < drawn.Count; i++)
        {
            var points = drawn[i].Points;
            var (x0, y0, x1, y1) = (points.Min(p => p.X) - tolerance, points.Min(p => p.Y) - tolerance, points.Max(p => p.X) + tolerance, points.Max(p => p.Y) + tolerance);
            for (var cx = Key(x0); cx <= Key(x1); cx++)
                for (var cy = Key(y0); cy <= Key(y1); cy++)
                {
                    if (!grid.TryGetValue((cx, cy), out var list)) grid[(cx, cy)] = list = new();
                    list.Add(i);
                }
        }
        var best = length.Max();
        for (var i = 0; i < drawn.Count && best < enough; i++)
            foreach (var end in new[] { drawn[i].Points[0], drawn[i].Points[^1] })
            {
                if (!grid.TryGetValue((Key(end.X), Key(end.Y)), out var near)) continue;
                foreach (var j in near)
                {
                    if (j == i || Find(i) == Find(j) || !Touches(end, drawn[j].Points, tolerance)) continue;
                    var (a, b) = (Find(i), Find(j));
                    parent[b] = a; length[a] += length[b];
                    best = Math.Max(best, length[a]);
                    if (best >= enough) return best;
                }
            }
        return best;

        long Key(double v) => (long)Math.Floor(v / cell);
        int Find(int i) { while (parent[i] != i) i = parent[i] = parent[parent[i]]; return i; }
    }

    internal static bool Touches(Point2 point, IReadOnlyList<Point2> polyline, double tolerance)
    {
        var squared = tolerance * tolerance;
        for (var k = 1; k < polyline.Count; k++)
        {
            var (a, b) = (polyline[k - 1], polyline[k]);
            double dx = b.X - a.X, dy = b.Y - a.Y, px = point.X - a.X, py = point.Y - a.Y;
            var lengthSquared = dx * dx + dy * dy;
            var t = lengthSquared == 0 ? 0 : Math.Clamp((px * dx + py * dy) / lengthSquared, 0, 1);
            double ex = px - t * dx, ey = py - t * dy;
            if (ex * ex + ey * ey <= squared) return true;
        }
        return false;
    }
}
