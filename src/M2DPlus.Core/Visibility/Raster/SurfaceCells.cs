namespace M2DPlus.Core.Visibility.Raster;

/// <summary>
/// Certified cells of a rational tensor-product NURBS surface, for curved occluders.
/// <para>The surface is decomposed into rational Bezier patches by knot insertion (exact), then each patch is split at
/// parameter halves (de Casteljau, homogeneous, exact) into a regular grid. With positive weights each cell lies in the
/// convex hull of its control net (so its depth lies within the net's depth range and its projection within the net's
/// projected bounding box), and each cell side, itself a rational Bezier curve whose control points are the net's border
/// row, lies within <see cref="Grid.Deviation"/> pixels of the chord between its projected corners.</para>
/// <para>Coverage certificate (degree theory): for any union D of cells, the projection f is continuous on D and the
/// projected boundary f(dD) is within the deviation of the polygon P of boundary chords. For a point x farther than that
/// from P, deg(f, D, x) = winding(P, x); if that is nonzero, x has a preimage in D, i.e. the surface covers x. No assumption
/// about folds or contours is needed.</para>
/// </summary>
public static class SurfaceCells
{
    public sealed class Grid
    {
        public required int Nu { get; init; }
        public required int Nv { get; init; }
        /// <summary>Corner pixel coordinates, (Nu + 1) x (Nv + 1), index (i * (Nv + 1) + j) * 2.</summary>
        public required double[] Corners { get; init; }
        /// <summary>Per cell (i * Nv + j): projected hull box x0, y0, x1, y1 and hull depth min, max.</summary>
        public required double[] Cells { get; init; }
        public required double Deviation { get; init; }
        public (double X, double Y) Corner(int i, int j) => (Corners[(i * (Nv + 1) + j) * 2], Corners[(i * (Nv + 1) + j) * 2 + 1]);
        /// <summary>
        /// Occluder patches: cells grouped by projected orientation (each group is a union D of cells). The region is the
        /// directed boundary of D (left-hand side in parameter space), so its nonzero-winding set is where deg(f, D, x) != 0
        /// away from the dilated chords; depth faces are the cells' hull boxes at their hull maximum depth plus slack.
        /// Coincident opposite borders (closed surfaces) are treated as adjacent.
        /// </summary>
        public List<RasterPatch> Patches(double depthSlack, double dilation)
        {
            var result = new List<RasterPatch>();
            bool Same((double X, double Y) a, (double X, double Y) b) => Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y) <= 1e-7;
            var closedU = Enumerable.Range(0, Nv + 1).All(j => Same(Corner(0, j), Corner(Nu, j)));
            var closedV = Enumerable.Range(0, Nu + 1).All(i => Same(Corner(i, 0), Corner(i, Nv)));
            foreach (var sign in new[] { 1, -1 })
            {
                bool In(int i, int j)
                {
                    if (closedU) i = (i % Nu + Nu) % Nu;
                    if (closedV) j = (j % Nv + Nv) % Nv;
                    return i >= 0 && j >= 0 && i < Nu && j < Nv && sign * Orientation(i, j) > 0;
                }
                var segments = new List<double>(); var faces = new List<RasterFace>();
                void Side((double X, double Y) a, (double X, double Y) b) { segments.Add(a.X); segments.Add(a.Y); segments.Add(b.X); segments.Add(b.Y); }
                for (var i = 0; i < Nu; i++)
                    for (var j = 0; j < Nv; j++)
                    {
                        if (!In(i, j)) continue;
                        if (!In(i, j - 1)) Side(Corner(i, j), Corner(i + 1, j));
                        if (!In(i + 1, j)) Side(Corner(i + 1, j), Corner(i + 1, j + 1));
                        if (!In(i, j + 1)) Side(Corner(i + 1, j + 1), Corner(i, j + 1));
                        if (!In(i - 1, j)) Side(Corner(i, j + 1), Corner(i, j));
                        var c = (i * Nv + j) * 6;
                        double x0 = Cells[c], y0 = Cells[c + 1], x1 = Cells[c + 2], y1 = Cells[c + 3], d = Cells[c + 5] + depthSlack;
                        faces.Add(new RasterFace { Loops = new[] { x0, y0, x1, y0, x1, y1, x0, y1 }, Starts = new[] { 0 }, C = d, MaxDepth = d });
                    }
                if (faces.Count == 0 || segments.Count == 0) continue;
                var region = segments.ToArray();
                result.Add(new RasterPatch { Faces = faces, Boundary = region, Region = region, Dilation = dilation });
            }
            return result;
        }

        /// <summary>Signed area of the projected corner quad (orientation of the cell in the view).</summary>
        public double Orientation(int i, int j)
        {
            var (ax, ay) = Corner(i, j); var (bx, by) = Corner(i + 1, j); var (cx, cy) = Corner(i + 1, j + 1); var (dx, dy) = Corner(i, j + 1);
            return .5 * ((cx - ax) * (dy - by) - (cy - ay) * (dx - bx));
        }
    }

    /// <summary>
    /// Builds the cell grid. <paramref name="cv"/> holds homogeneous control points (wx, wy, wz, w), index (i * countV + j) * 4.
    /// Knots use the Rhino convention (count + degree - 1 values). Returns null if the knots are not clamped, a weight is not
    /// positive, or the deviation target needs more than <paramref name="maxCells"/> cells.
    /// </summary>
    public static Grid? Build(int degreeU, int degreeV, double[] knotsU, double[] knotsV, int countU, int countV, double[] cv,
        ViewAffine map, double epsilon, int maxCells)
    {
        if (!Clamped(knotsU, degreeU) || !Clamped(knotsV, degreeV)) return null;
        for (var k = 3; k < cv.Length; k += 4) if (!(cv[k] > 0)) return null;
        // Decompose along u for each v column, then along v for each u-span row.
        var uSpans = new List<double[][]>(); // per u-span: (degreeU+1) rows of countV points (4 each)
        {
            var columns = new List<List<double[]>>();
            for (var j = 0; j < countV; j++)
            {
                var pts = new double[countU * 4];
                for (var i = 0; i < countU; i++) Array.Copy(cv, (i * countV + j) * 4, pts, i * 4, 4);
                columns.Add(Decompose(degreeU, Full(knotsU), pts));
            }
            var spans = columns[0].Count;
            for (var s = 0; s < spans; s++)
            {
                var rows = new double[degreeU + 1][];
                for (var a = 0; a <= degreeU; a++)
                {
                    rows[a] = new double[countV * 4];
                    for (var j = 0; j < countV; j++) Array.Copy(columns[j][s], a * 4, rows[a], j * 4, 4);
                }
                uSpans.Add(rows);
            }
        }
        // patches[su][sv] = (degreeU+1) x (degreeV+1) net, index (a * (degreeV+1) + b) * 4.
        var patches = new List<List<double[]>>();
        foreach (var rows in uSpans)
        {
            var decomposed = rows.Select(r => Decompose(degreeV, Full(knotsV), r)).ToArray();
            var list = new List<double[]>();
            for (var sv = 0; sv < decomposed[0].Count; sv++)
            {
                var net = new double[(degreeU + 1) * (degreeV + 1) * 4];
                for (var a = 0; a <= degreeU; a++) Array.Copy(decomposed[a][sv], 0, net, a * (degreeV + 1) * 4, (degreeV + 1) * 4);
                list.Add(net);
            }
            patches.Add(list);
        }
        int pu = patches.Count, pv = patches[0].Count;
        for (int levelU = 0, levelV = 0; levelU <= 12 && levelV <= 12; )
        {
            if ((long)(pu << levelU) * (pv << levelV) > maxCells) return null;
            var (grid, needU, needV) = Subdivide(patches, degreeU, degreeV, levelU, levelV, map, epsilon);
            if (grid is not null) return grid;
            if (needU) levelU++;
            if (needV) levelV++;
        }
        return null;
    }

    private static (Grid?, bool NeedU, bool NeedV) Subdivide(List<List<double[]>> patches, int du, int dv, int levelU, int levelV, ViewAffine map, double epsilon)
    {
        int pu = patches.Count, pv = patches[0].Count, su = 1 << levelU, sv = 1 << levelV;
        int nu = pu * su, nv = pv * sv;
        var corners = new double[(nu + 1) * (nv + 1) * 2];
        var cells = new double[nu * nv * 6];
        double deviation = 0; bool needU = false, needV = false;
        int ru = du + 1, rv = dv + 1;
        for (var a = 0; a < pu; a++)
            for (var b = 0; b < pv; b++)
            {
                // Split along u into su strips, then each strip along v into sv cells.
                var strips = SplitRepeated(patches[a][b], ru, rv, levelU, alongU: true);
                for (var s = 0; s < su; s++)
                {
                    var cellsV = SplitRepeated(strips[s], ru, rv, levelV, alongU: false);
                    for (var t = 0; t < sv; t++)
                    {
                        var net = cellsV[t]; var i = a * su + s; var j = b * sv + t;
                        var px = new double[ru * rv]; var py = new double[ru * rv];
                        double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue, d0 = double.MaxValue, d1 = double.MinValue;
                        for (var k = 0; k < ru * rv; k++)
                        {
                            var w = net[4 * k + 3];
                            var (x, y, d) = map.Map(net[4 * k] / w, net[4 * k + 1] / w, net[4 * k + 2] / w);
                            px[k] = x; py[k] = y;
                            x0 = Math.Min(x0, x); x1 = Math.Max(x1, x); y0 = Math.Min(y0, y); y1 = Math.Max(y1, y); d0 = Math.Min(d0, d); d1 = Math.Max(d1, d);
                        }
                        var c = (i * nv + j) * 6;
                        cells[c] = x0; cells[c + 1] = y0; cells[c + 2] = x1; cells[c + 3] = y1; cells[c + 4] = d0; cells[c + 5] = d1;
                        void Corner(int ci, int cj, int k) { corners[(ci * (nv + 1) + cj) * 2] = px[k]; corners[(ci * (nv + 1) + cj) * 2 + 1] = py[k]; }
                        Corner(i, j, 0); Corner(i + 1, j, (ru - 1) * rv); Corner(i, j + 1, rv - 1); Corner(i + 1, j + 1, ru * rv - 1);
                        // Side rows: v = const sides vary along u (index a*rv + b with b fixed), u = const sides along v.
                        double SideDeviation(Func<int, int> index, int count)
                        {
                            var m = 0.0; int first = index(0), last = index(count - 1);
                            for (var q = 1; q < count - 1; q++) m = Math.Max(m, BezierFlatten.SegmentDistance(px[index(q)], py[index(q)], px[first], py[first], px[last], py[last]));
                            return m;
                        }
                        var alongU = Math.Max(SideDeviation(q => q * rv, ru), SideDeviation(q => q * rv + rv - 1, ru));
                        var alongV = Math.Max(SideDeviation(q => q, rv), SideDeviation(q => (ru - 1) * rv + q, rv));
                        if (alongU > epsilon) needU = true;
                        if (alongV > epsilon) needV = true;
                        deviation = Math.Max(deviation, Math.Max(alongU, alongV));
                    }
                }
            }
        if (needU || needV) return (null, needU, needV);
        return (new Grid { Nu = nu, Nv = nv, Corners = corners, Cells = cells, Deviation = deviation }, false, false);
    }

    /// <summary>Splits a net 2^level times at parameter halves along u (rows of the net) or v.</summary>
    private static List<double[]> SplitRepeated(double[] net, int ru, int rv, int level, bool alongU)
    {
        var current = new List<double[]> { net };
        for (var l = 0; l < level; l++)
        {
            var next = new List<double[]>(current.Count * 2);
            foreach (var n in current) { SplitNet(n, ru, rv, alongU, out var left, out var right); next.Add(left); next.Add(right); }
            current = next;
        }
        return current;
    }

    private static void SplitNet(double[] net, int ru, int rv, bool alongU, out double[] left, out double[] right)
    {
        left = new double[net.Length]; right = new double[net.Length];
        var lines = alongU ? rv : ru; var count = alongU ? ru : rv;
        var curve = new double[count * 4];
        for (var line = 0; line < lines; line++)
        {
            for (var q = 0; q < count; q++) Array.Copy(net, Index(line, q) * 4, curve, q * 4, 4);
            BezierFlatten.Split(curve, out var l, out var r);
            for (var q = 0; q < count; q++) { Array.Copy(l, q * 4, left, Index(line, q) * 4, 4); Array.Copy(r, q * 4, right, Index(line, q) * 4, 4); }
        }
        int Index(int line, int q) => alongU ? q * rv + line : line * rv + q;
    }

    private static bool Clamped(double[] knots, int degree)
    {
        if (knots.Length < 2 * degree) return false;
        for (var k = 1; k < degree; k++)
            if (knots[k] != knots[0] || knots[^(k + 1)] != knots[^1]) return false;
        return knots[^1] > knots[0];
    }

    /// <summary>Rhino knots (count + degree - 1) to the full clamped vector (count + degree + 1).</summary>
    private static double[] Full(double[] knots)
    {
        var full = new double[knots.Length + 2];
        full[0] = knots[0]; Array.Copy(knots, 0, full, 1, knots.Length); full[^1] = knots[^1];
        return full;
    }

    /// <summary>Bezier decomposition of a clamped curve by knot insertion (Piegl and Tiller A5.6), homogeneous points.</summary>
    public static List<double[]> Decompose(int p, double[] u, double[] pw)
    {
        var n = pw.Length / 4 - 1; var m = n + p + 1;
        var result = new List<double[]>();
        var q = new double[(p + 1) * 4]; Array.Copy(pw, 0, q, 0, (p + 1) * 4);
        var alphas = new double[p + 1];
        int a = p, b = p + 1;
        while (b < m)
        {
            var i = b;
            while (b < m && u[b + 1] == u[b]) b++;
            var mult = b - i + 1;
            var next = new double[(p + 1) * 4];
            if (mult < p)
            {
                var numer = u[b] - u[a];
                for (var j = p; j > mult; j--) alphas[j - mult - 1] = numer / (u[a + j] - u[a]);
                var r = p - mult;
                for (var j = 1; j <= r; j++)
                {
                    var save = r - j; var s = mult + j;
                    for (var k = p; k >= s; k--)
                    {
                        var alpha = alphas[k - s];
                        for (var c = 0; c < 4; c++) q[k * 4 + c] = alpha * q[k * 4 + c] + (1 - alpha) * q[(k - 1) * 4 + c];
                    }
                    if (b < m) Array.Copy(q, p * 4, next, save * 4, 4);
                }
            }
            result.Add(q);
            if (b < m)
            {
                for (var k = p - mult; k <= p; k++) Array.Copy(pw, (b - p + k) * 4, next, k * 4, 4);
                q = next; a = b; b++;
            }
        }
        return result;
    }
}
