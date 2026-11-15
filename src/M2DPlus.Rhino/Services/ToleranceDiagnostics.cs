using Rhino.Geometry;

namespace M2DPlus.Rhino.Services;

/// <summary>Detail-risk census; never guesses what the native solver silently omitted.</summary>
internal static class ToleranceDiagnostics
{
    /// <summary>View-independent: counts source edges at or below 2 x tolerance. Run once per prepared scene.</summary>
    internal static int SourceEdgeCensus(IReadOnlyList<PreparedLeaf> sources, double tolerance, CancellationToken token)
    {
        var threshold = 2 * tolerance;
        bool Short(Curve c)
        {
            var bounds = c.GetBoundingBox(true);
            // Include the threshold in the risk band: native classification is not
            // guaranteed at exactly 2 tol. This census never removes geometry.
            var scale = Math.Max(bounds.Min.MaximumCoordinate, bounds.Max.MaximumCoordinate);
            var error = 64 * 2.2204460492503131e-16 * Math.Max(scale, threshold);
            return c.GetLength() <= threshold + error;
        }
        var count = 0;
        foreach (var source in sources)
        {
            token.ThrowIfCancellationRequested();
            if (source.Geometry is Brep b) count += b.Edges.Count(Short);
            else if (source.Geometry is Curve c)
            {
                var segments = c.DuplicateSegments();
                try { count += segments.Length == 0 ? (Short(c) ? 1 : 0) : segments.Count(Short); }
                finally { foreach (var segment in segments) segment.Dispose(); }
            }
            else if (source.Geometry is Mesh m)
                for (var i = 0; i < m.TopologyEdges.Count; i++)
                {
                    var ends = m.TopologyEdges.GetTopologyVertices(i);
                    var a = m.TopologyVertices.MeshVertexIndices(ends.I)[0]; var z = m.TopologyVertices.MeshVertexIndices(ends.J)[0];
                    if ((i & 255) == 0) token.ThrowIfCancellationRequested();
                    if (m.Vertices.Point3dAt(a).DistanceTo(m.Vertices.Point3dAt(z)) <= threshold) count++;
                }
        }
        return count;
    }

    /// <summary>Per view: applies the scene-wide edge census and a linear-size adjacent-box gap census.</summary>
    internal static void Apply(ViewDrawing drawing, IReadOnlyList<PreparedLeaf> sources, int sourceEdgeCensus, CancellationToken token)
    {
        drawing.SubToleranceSourceEdges = sourceEdgeCensus;
        var threshold = 2 * drawing.Tolerance;
        // Linear-size adjacent-box census, not all-pairs geometry distance. Positive
        // box gaps are conservative warning evidence, never permission to join regions.
        if (!drawing.Frame.IsValid) return;
        var transform = Transform.PlaneToPlane(drawing.Frame.Plane, Plane.WorldXY);
        var boxes = sources.Select((s, i) => (Box: s.Geometry.GetBoundingBox(transform), Index: i)).Where(b => b.Box.IsValid).ToArray();
        var pairs = new HashSet<(int, int)>();
        for (var axis = 0; axis < 2; axis++)
        {
            var sorted = boxes.OrderBy(b => axis == 0 ? b.Box.Min.X : b.Box.Min.Y).ToArray();
            for (var i = 1; i < sorted.Length; i++)
            {
                if ((i & 255) == 0) token.ThrowIfCancellationRequested();
                var a = sorted[i - 1]; var b = sorted[i];
                var gap = axis == 0 ? b.Box.Min.X - a.Box.Max.X : b.Box.Min.Y - a.Box.Max.Y;
                var magnitude = Math.Max(a.Box.Max.MaximumCoordinate, b.Box.Min.MaximumCoordinate);
                var error = 64 * 2.2204460492503131e-16 * Math.Max(magnitude, threshold);
                var overlap = axis == 0 ? a.Box.Min.Y <= b.Box.Max.Y && b.Box.Min.Y <= a.Box.Max.Y : a.Box.Min.X <= b.Box.Max.X && b.Box.Min.X <= a.Box.Max.X;
                if (gap > error && gap <= threshold + error && overlap) pairs.Add((Math.Min(a.Index, b.Index), Math.Max(a.Index, b.Index)));
            }
        }
        drawing.SubToleranceBoundsGaps = pairs.Count;
    }
}
