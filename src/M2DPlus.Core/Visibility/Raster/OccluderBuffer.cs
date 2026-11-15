using System.Buffers;

namespace M2DPlus.Core.Visibility.Raster;

/// <summary>
/// One planar face of an opaque occluder, in pixel coordinates. Depth over the face's plane is
/// A*x + B*y + C; callers fold the face's planarity and numerical slack into C and <see cref="MaxDepth"/>,
/// so that the true surface depth at any point of the face is at most the plane value and at most MaxDepth.
/// </summary>
public sealed class RasterFace
{
    public required double[] Loops { get; init; }
    public required int[] Starts { get; init; }
    public double A { get; init; }
    public double B { get; init; }
    public double C { get; init; }
    public double MaxDepth { get; init; }
    /// <summary>Upper bound over the closed pixel square (i, j): the plane maximum at a corner, capped by MaxDepth.</summary>
    public double UpperBound(int i, int j)
    {
        var x = A > 0 ? i + 1 : i; var y = B > 0 ? j + 1 : j;
        return Math.Min(MaxDepth, A * x + B * y + C);
    }
}

/// <summary>
/// A set of faces of one opaque object (a "patch") with the segments that can bound the union of their
/// projections. <see cref="Boundary"/> must contain every face loop segment except those on an edge shared by
/// exactly two faces of the patch that lie on opposite sides of it in projection; such an edge is interior to the
/// union locally, so the boundary of the union is a subset of these segments.
/// </summary>
public sealed class RasterPatch
{
    public required IReadOnlyList<RasterFace> Faces { get; init; }
    /// <summary>Flattened (x0, y0, x1, y1) boundary segments in pixels.</summary>
    public required double[] Boundary { get; init; }
    /// <summary>Boundary dilation in pixels: model-to-polygon positional error plus numerical slack.</summary>
    public double Dilation { get; init; }
    /// <summary>
    /// Optional covered region as closed cycles of directed segments (x0, y0, x1, y1), filled by the nonzero rule. When set,
    /// faces only raise depth (every point of the region must lie in some face's touched pixels) and <see cref="Boundary"/>
    /// must contain these segments. Used for curved surfaces, where coverage follows from a degree argument.
    /// </summary>
    public double[]? Region { get; init; }
}

/// <summary>
/// Conservative "certified-opaque far depth" buffer. For each pixel, <see cref="Far"/> is an upper bound on the depth
/// at which every ray through every point of the closed pixel square has already met an opaque surface
/// (+infinity when unknown). Certificate, per patch and pixel:
/// <list type="number">
/// <item>The pixel is not touched by any (dilated) boundary segment, so its square does not meet the boundary of
/// the union U of the patch's projected faces; the square is connected, so it lies wholly inside or wholly
/// outside U. Its centre is inside some face, so the square lies inside U.</item>
/// <item>Every point of the square is therefore covered by some face that touches the pixel; that face's depth there
/// is at most its upper bound over the square, so the first opaque hit is no deeper than the maximum of those
/// bounds over all faces touching the pixel.</item>
/// </list>
/// Depths are stored as positive floats rounded up; the minimum over patches is order-independent.
/// </summary>
public sealed class OccluderBuffer
{
    private const byte Inside = 1, Edge = 2;
    private readonly int[] _far;
    public RasterGrid Grid { get; }
    public OccluderBuffer(RasterGrid grid)
    {
        Grid = grid; _far = new int[checked((int)grid.Pixels)];
        Array.Fill(_far, BitConverter.SingleToInt32Bits(float.PositiveInfinity));
    }
    public float Far(int i, int j) => BitConverter.Int32BitsToSingle(Volatile.Read(ref _far[j * Grid.Width + i]));
    internal int FarBits(int index) => _far[index];

    /// <summary>Adds one patch. Thread-safe; the result does not depend on the order of calls.</summary>
    public long Add(RasterPatch patch)
    {
        if (patch.Faces.Count == 0) return 0;
        var region = patch.Region;
        double xMin = double.MaxValue, yMin = double.MaxValue, xMax = double.MinValue, yMax = double.MinValue;
        foreach (var face in patch.Faces)
            for (var k = 0; k < face.Loops.Length; k += 2)
            {
                xMin = Math.Min(xMin, face.Loops[k]); xMax = Math.Max(xMax, face.Loops[k]);
                yMin = Math.Min(yMin, face.Loops[k + 1]); yMax = Math.Max(yMax, face.Loops[k + 1]);
            }
        var r = patch.Dilation;
        var i0 = Math.Max(0, (int)Math.Floor(xMin - r)); var j0 = Math.Max(0, (int)Math.Floor(yMin - r));
        var i1 = Math.Min(Grid.Width - 1, (int)Math.Floor(xMax + r)); var j1 = Math.Min(Grid.Height - 1, (int)Math.Floor(yMax + r));
        if (i0 > i1 || j0 > j1) return 0;
        var w = i1 - i0 + 1; var h = j1 - j0 + 1; var n = w * h;
        var depth = ArrayPool<double>.Shared.Rent(n); var flags = ArrayPool<byte>.Shared.Rent(n);
        try
        {
            Array.Fill(depth, double.NegativeInfinity, 0, n); Array.Clear(flags, 0, n);
            foreach (var face in patch.Faces)
            {
                void Raise(int i, int j)
                {
                    var k = (j - j0) * w + (i - i0); var bound = face.UpperBound(i, j);
                    if (bound > depth[k]) depth[k] = bound;
                }
                if (region is null) ConservativeScan.Centres(face.Loops, face.Starts, i0, j0, i1, j1, (i, j) => { flags[(j - j0) * w + (i - i0)] |= Inside; Raise(i, j); });
                else ConservativeScan.Centres(face.Loops, face.Starts, i0, j0, i1, j1, Raise);
                var points = face.Loops.Length / 2;
                for (var loop = 0; loop < face.Starts.Length; loop++)
                {
                    var first = face.Starts[loop]; var end = loop + 1 < face.Starts.Length ? face.Starts[loop + 1] : points;
                    for (var k = first; k < end; k++)
                    {
                        var m = k + 1 < end ? k + 1 : first;
                        ConservativeScan.Touch(face.Loops[2 * k], face.Loops[2 * k + 1], face.Loops[2 * m], face.Loops[2 * m + 1], r, i0, j0, i1, j1, Raise);
                    }
                }
            }
            if (region is not null) ConservativeScan.NonZeroCentres(region, i0, j0, i1, j1, (i, j) => flags[(j - j0) * w + (i - i0)] |= Inside);
            var b = patch.Boundary;
            for (var k = 0; k + 3 < b.Length; k += 4)
                ConservativeScan.Touch(b[k], b[k + 1], b[k + 2], b[k + 3], r, i0, j0, i1, j1, (i, j) => flags[(j - j0) * w + (i - i0)] |= Edge);
            long covered = 0;
            for (var j = j0; j <= j1; j++)
                for (var i = i0; i <= i1; i++)
                {
                    var k = (j - j0) * w + (i - i0);
                    if (flags[k] != Inside || !(depth[k] > 0)) continue; // non-positive depths are never stored: no coverage claimed
                    AtomicMin(ref _far[j * Grid.Width + i], BitConverter.SingleToInt32Bits(ConservativeScan.Up(depth[k])));
                    covered++;
                }
            return covered;
        }
        finally { ArrayPool<double>.Shared.Return(depth); ArrayPool<byte>.Shared.Return(flags); }
    }

    // Positive IEEE floats order like their bit patterns.
    private static void AtomicMin(ref int target, int value)
    {
        var current = Volatile.Read(ref target);
        while (value < current)
        {
            var previous = Interlocked.CompareExchange(ref target, value, current);
            if (previous == current) return;
            current = previous;
        }
    }

    /// <summary>
    /// Face-footprint variant of the window test: every pixel the (dilated) planar face touches, i.e. its centre is inside or
    /// a dilated loop segment meets it, must be hidden behind occluders strictly nearer than the face's lower depth bound over
    /// that pixel: max(MinDepth, plane minimum at a corner). <paramref name="face"/> holds the lower plane in A, B, C (slack
    /// already subtracted) and the minimum depth in MaxDepth's place.
    /// </summary>
    public bool HiddenFace(RasterFace face, double dilation, double margin, bool outsideIrrelevant)
    {
        var points = face.Loops.Length / 2;
        double xMin = double.MaxValue, yMin = double.MaxValue, xMax = double.MinValue, yMax = double.MinValue;
        for (var k = 0; k < points; k++)
        { xMin = Math.Min(xMin, face.Loops[2 * k]); xMax = Math.Max(xMax, face.Loops[2 * k]); yMin = Math.Min(yMin, face.Loops[2 * k + 1]); yMax = Math.Max(yMax, face.Loops[2 * k + 1]); }
        int i0 = (int)Math.Floor(xMin - dilation), j0 = (int)Math.Floor(yMin - dilation), i1 = (int)Math.Floor(xMax + dilation), j1 = (int)Math.Floor(yMax + dilation);
        if (i0 < 0 || j0 < 0 || i1 >= Grid.Width || j1 >= Grid.Height)
        {
            if (!outsideIrrelevant) return false;
            i0 = Math.Max(i0, 0); j0 = Math.Max(j0, 0); i1 = Math.Min(i1, Grid.Width - 1); j1 = Math.Min(j1, Grid.Height - 1);
            if (i0 > i1 || j0 > j1) return true;
        }
        var hidden = true;
        void Check(int i, int j)
        {
            if (!hidden) return;
            var x = face.A > 0 ? i : i + 1; var y = face.B > 0 ? j : j + 1;
            var near = Math.Max(face.MaxDepth, face.A * x + face.B * y + face.C);
            var limit = BitConverter.SingleToInt32Bits(ConservativeScan.Down(near - margin));
            if (limit <= 0 || _far[j * Grid.Width + i] >= limit) hidden = false;
        }
        ConservativeScan.Centres(face.Loops, face.Starts, i0, j0, i1, j1, Check);
        for (var loop = 0; loop < face.Starts.Length && hidden; loop++)
        {
            var first = face.Starts[loop]; var end = loop + 1 < face.Starts.Length ? face.Starts[loop + 1] : points;
            for (var k = first; k < end && hidden; k++)
            {
                var m = k + 1 < end ? k + 1 : first;
                ConservativeScan.Touch(face.Loops[2 * k], face.Loops[2 * k + 1], face.Loops[2 * m], face.Loops[2 * m + 1], dilation, i0, j0, i1, j1, Check);
            }
        }
        return hidden;
    }

    /// <summary>
    /// True when every pixel of the inclusive window lies wholly behind certified opaque occluders that are strictly
    /// nearer than <paramref name="near"/> (a lower bound on the target's depth over the window) by
    /// <paramref name="margin"/>. Pixels outside the grid make the answer false unless <paramref name="outsideIrrelevant"/>.
    /// </summary>
    public bool Hidden(int i0, int j0, int i1, int j1, double near, double margin, bool outsideIrrelevant)
    {
        if (i0 < 0 || j0 < 0 || i1 >= Grid.Width || j1 >= Grid.Height)
        {
            if (!outsideIrrelevant) return false;
            i0 = Math.Max(i0, 0); j0 = Math.Max(j0, 0); i1 = Math.Min(i1, Grid.Width - 1); j1 = Math.Min(j1, Grid.Height - 1);
            if (i0 > i1 || j0 > j1) return true;
        }
        var limit = BitConverter.SingleToInt32Bits(ConservativeScan.Down(near - margin));
        if (limit <= 0) return false;
        for (var j = j0; j <= j1; j++)
        {
            var row = j * Grid.Width;
            for (var i = i0; i <= i1; i++) if (_far[row + i] >= limit) return false;
        }
        return true;
    }
}
