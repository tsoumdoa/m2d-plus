using System.Diagnostics;
using M2DPlus.Core;
using M2DPlus.Core.Visibility.Raster;
using M2DPlus.Rhino.Services;
using Rhino;
using Rhino.Geometry;

namespace M2DPlus.Rhino.Visibility;

/// <summary>Opt-in raster visibility prepass. Null in <see cref="NativeSettings"/> keeps the plain native route.</summary>
public sealed record RasterOptions
{
    /// <summary>Pixels along the longer side of the view frame.</summary>
    public int MaxSide { get; init; } = 2048;
    /// <summary>Required strict depth separation, in model tolerances, between an occluder and a culled object.</summary>
    public double MarginTolerances { get; init; } = 10;
    /// <summary>Angle (radians) under which an interior edge between two planar faces counts as a tangent edge.</summary>
    public double TangentAngle { get; init; } = RhinoMath.ToRadians(1);
    /// <summary>Emit isolated curves, points and convex planar solids directly (no other remaining surface overlaps them).</summary>
    public bool CertifyIsolated { get; init; }
    /// <summary>Planar faces with free-form (NURBS) trims become occluders via certified Bezier chord bounds (pixels); 0 disables.</summary>
    public double FreeformTrimPixels { get; init; }
    /// <summary>Untrimmed curved faces become occluders via certified Bezier cells (max side deviation, pixels); 0 disables.</summary>
    public double CurvedPixels { get; init; }
    /// <summary>Culled curved Breps emit their hidden candidates (non-tangent edges and view silhouettes) directly instead of joining the native call.</summary>
    public bool CurvedCulledDirect { get; init; }
    /// <summary>Planar target faces use polygon footprints with a per-pixel plane lower bound instead of their boxes.</summary>
    public bool PolygonFootprints { get; init; }
    /// <summary>Scale the grid with the scene: clamp(64 * sqrt(sources), 256, MaxSide) pixels (small scenes need no 2048^2 buffer).</summary>
    public bool AdaptiveResolution { get; init; }
    /// <summary>Merge planar occluder patches of different objects across exactly shared (collinear, opposite-side) boundaries.</summary>
    public bool MergeSeams { get; init; }
    /// <summary>Cell budget per curved face.</summary>
    public int CurvedMaxCells { get; init; } = 4096;
}

public sealed record PrepassStats(int Sources, int Occluders, int Culled, int CulledDirect, int CulledNative, int Outside, int Isolated,
    int NativeSources, long CoveredPixels, int Width, int Height, double PrepareSeconds, double RasterSeconds, double ClassifySeconds);

internal sealed class PrepassResult
{
    internal List<PreparedLeaf> Remaining { get; } = new();
    internal List<PreparedLeaf> CulledDirect { get; } = new();
    internal List<PreparedLeaf> CulledCurved { get; } = new();
    /// <summary>Certified hidden but kept in the single native call (no direct candidate set); never a second native call.</summary>
    internal HashSet<string> CulledNative { get; } = new(StringComparer.Ordinal);
    internal List<PreparedLeaf> Isolated { get; } = new();
    internal int HiddenPoints;
    internal List<string> Removed { get; } = new();
    /// <summary>Sources whose output the prepass emits itself (culled-direct and isolated); never solved by native.</summary>
    internal HashSet<string> Direct { get; } = new(StringComparer.Ordinal);
    internal PrepassStats Stats = null!;
}

/// <summary>Maps world points to pixel coordinates and positive depth for one parallel view frame.</summary>
internal sealed class ViewMap
{
    internal readonly Point3d O; internal readonly Vector3d X, Y, D;
    internal readonly double Offset; internal readonly RasterGrid Grid;
    internal ViewMap(Rectangle3d frame, BoundingBox bounds, int maxSide)
    {
        O = frame.Plane.Origin; X = frame.Plane.XAxis; Y = frame.Plane.YAxis; D = -frame.Plane.ZAxis;
        var near = double.MaxValue;
        foreach (var c in bounds.GetCorners()) near = Math.Min(near, (c - O) * D);
        Offset = 1 + Math.Max(0, -near) + bounds.Diagonal.Length * 1e-3;
        Grid = RasterGrid.Fit(frame.X.Min, frame.X.Max, frame.Y.Min, frame.Y.Max, maxSide);
    }
    internal double U(Point3d p) => (p - O) * X;
    internal double V(Point3d p) => (p - O) * Y;
    internal double Px(Point3d p) => (U(p) - Grid.U0) / Grid.Pixel;
    internal double Py(Point3d p) => (V(p) - Grid.V0) / Grid.Pixel;
    internal double Depth(Point3d p) => (p - O) * D + Offset;
    internal ViewAffine Affine => new(X.X / Grid.Pixel, X.Y / Grid.Pixel, X.Z / Grid.Pixel, ((Point3d.Origin - O) * X - Grid.U0) / Grid.Pixel,
        Y.X / Grid.Pixel, Y.Y / Grid.Pixel, Y.Z / Grid.Pixel, ((Point3d.Origin - O) * Y - Grid.V0) / Grid.Pixel,
        D.X, D.Y, D.Z, (Point3d.Origin - O) * D + Offset);
}

internal static class RasterPrepass
{
    /// <summary>Diagnostics hook (bench only): receives the occluder buffer of each classification.</summary>
    internal static Action<OccluderBuffer>? DebugBuffer { get; set; }

    private sealed class Source
    {
        internal required PreparedLeaf Leaf;
        internal List<RasterPatch>? Patches;
        internal PatchInput? Seamable;
        // Per-part pixel windows with a lower depth bound: x0, y0, x1, y1, near.
        internal readonly List<(double X0, double Y0, double X1, double Y1, double Near)> Footprint = new();
        // Planar face footprints (lower plane, minimum depth stored as MaxDepth) with their dilation.
        internal readonly List<(RasterFace Face, double Dilation)> FaceFootprint = new();
        internal bool Direct;   // culled output can be emitted directly (planar Brep edges, curves)
        internal bool Point;
        internal bool Invalid;  // no certified footprint: never culled
        internal bool Surface;  // can occlude
        internal bool Convex;   // closed convex planar solid with linear edges
        internal bool Brep;     // Brep or extrusion
    }

    internal static PrepassResult Classify(IReadOnlyList<PreparedLeaf> sources, ViewRequest view, Rectangle3d frame,
        BoundingBox bounds, double tolerance, RasterOptions options, CancellationToken token)
    {
        var timer = Stopwatch.StartNew();
        var side = options.AdaptiveResolution ? (int)Math.Clamp(64 * Math.Sqrt(sources.Count), 256, options.MaxSide) : options.MaxSide;
        var map = new ViewMap(frame, bounds, side);
        var items = new Source[sources.Count];
        Parallel.For(0, sources.Count, new ParallelOptions { CancellationToken = token }, i => items[i] = Describe(sources[i], map, tolerance, options));
        var prepare = timer.Elapsed.TotalSeconds; timer.Restart();
        var buffer = new OccluderBuffer(map.Grid);
        long covered = 0;
        var all = items.Where(s => s.Patches is not null).SelectMany(s => s.Patches!).ToList();
        var seamable = items.Where(s => s.Seamable is not null).Select(s => s.Seamable!).ToList();
        var shared = 0;
        if (seamable.Count != 0)
            all.AddRange(SeamMerge.Merge(seamable, Math.Max(1e-9, 1e-3 * tolerance / map.Grid.Pixel), out shared));
        // Large patches first: better load balance.
        all.Sort((a, b) => b.Faces.Count.CompareTo(a.Faces.Count));
        Parallel.ForEach(all, new ParallelOptions { CancellationToken = token }, p => Interlocked.Add(ref covered, buffer.Add(p)));
        var raster = timer.Elapsed.TotalSeconds; timer.Restart();
        DebugBuffer?.Invoke(buffer);
        var crop = view.Named is { Crop: true };
        var margin = options.MarginTolerances * tolerance + 1e-9 * (1 + bounds.Diagonal.Length);
        var r = 2 * tolerance / map.Grid.Pixel + 1e-6;
        var verdict = new int[items.Length]; // 0 keep, 1 culled, 2 outside
        Parallel.For(0, items.Length, new ParallelOptions { CancellationToken = token }, k =>
        {
            var s = items[k];
            if (s.Invalid || s.Footprint.Count + s.FaceFootprint.Count == 0) return;
            var outside = crop;
            foreach (var f in s.Footprint)
            {
                int i0 = (int)Math.Floor(f.X0 - r), j0 = (int)Math.Floor(f.Y0 - r), i1 = (int)Math.Floor(f.X1 + r), j1 = (int)Math.Floor(f.Y1 + r);
                if (crop && i1 >= 0 && j1 >= 0 && i0 < map.Grid.Width && j0 < map.Grid.Height) outside = false;
                if (!buffer.Hidden(i0, j0, i1, j1, f.Near, margin, crop)) return;
            }
            foreach (var (face, dilation) in s.FaceFootprint)
            {
                var box = FaceBox(face, dilation + r);
                if (crop && box.X1 >= 0 && box.Y1 >= 0 && box.X0 < map.Grid.Width && box.Y0 < map.Grid.Height) outside = false;
                if (!buffer.HiddenFace(face, dilation + r, margin, crop)) return;
            }
            verdict[k] = outside ? 2 : 1;
        });
        if (options.CertifyIsolated) Isolate(items, verdict, r);
        var result = new PrepassResult();
        int culled = 0, outsideCount = 0, occluders = 0;
        for (var k = 0; k < items.Length; k++)
        {
            var s = items[k]; if (s.Patches is not null || s.Seamable is not null) occluders++;
            if (verdict[k] == 0) { result.Remaining.Add(s.Leaf); continue; }
            if (verdict[k] == 3) { result.Isolated.Add(s.Leaf); result.Direct.Add(s.Leaf.Identity); continue; }
            result.Removed.Add(s.Leaf.Identity);
            if (verdict[k] == 2) { outsideCount++; continue; }
            culled++;
            if (s.Point) result.HiddenPoints++;
            else if (s.Direct) { result.CulledDirect.Add(s.Leaf); result.Direct.Add(s.Leaf.Identity); }
            else if (s.Brep && options.CurvedCulledDirect) { result.CulledCurved.Add(s.Leaf); result.Direct.Add(s.Leaf.Identity); }
            else { result.CulledNative.Add(s.Leaf.Identity); result.Remaining.Add(s.Leaf); }
        }
        result.Stats = new(items.Length, occluders, culled, result.CulledDirect.Count, result.CulledNative.Count, outsideCount, result.Isolated.Count,
            result.Remaining.Count, covered, map.Grid.Width, map.Grid.Height, prepare, raster, timer.Elapsed.TotalSeconds);
        return result;
    }

    /// <summary>
    /// Isolation certificate: a remaining curve or point whose conservative screen box meets no remaining surface's box cannot
    /// be occluded; a remaining surface whose box meets no other remaining object's box can neither occlude nor be occluded.
    /// Culled objects cannot matter, because anything in front of a culled object is behind a strictly nearer occluder chain
    /// that ends in a remaining surface (which would overlap).
    /// Only kinds whose self-visibility is exact without a solve qualify: curves, points, convex planar solids, single planar faces.
    /// </summary>
    private static void Isolate(Source[] items, int[] verdict, double r)
    {
        var boxes = new (double X0, double Y0, double X1, double Y1)[items.Length];
        var present = new List<int>();
        for (var k = 0; k < items.Length; k++)
        {
            if (verdict[k] != 0 || items[k].Footprint.Count + items[k].FaceFootprint.Count == 0) continue;
            var f = items[k].Footprint.Concat(items[k].FaceFootprint.Select(ff => FaceBox(ff.Face, ff.Dilation))).ToList();
            boxes[k] = (f.Min(b => b.X0) - r, f.Min(b => b.Y0) - r, f.Max(b => b.X1) + r, f.Max(b => b.Y1) + r);
            present.Add(k);
        }
        // An invalid (unfootprinted) remaining surface blocks every isolation claim.
        if (Enumerable.Range(0, items.Length).Any(k => verdict[k] == 0 && items[k].Footprint.Count + items[k].FaceFootprint.Count == 0 && !items[k].Point)) return;
        const double cell = 64;
        var grid = new Dictionary<(int, int), List<int>>();
        foreach (var k in present)
            for (var i = (int)Math.Floor(boxes[k].X0 / cell); i <= (int)Math.Floor(boxes[k].X1 / cell); i++)
                for (var j = (int)Math.Floor(boxes[k].Y0 / cell); j <= (int)Math.Floor(boxes[k].Y1 / cell); j++)
                { if (!grid.TryGetValue((i, j), out var list)) grid[(i, j)] = list = new(); list.Add(k); }
        for (var k = 0; k < items.Length; k++)
        {
            var s = items[k];
            if (verdict[k] != 0 || s.Invalid || s.Footprint.Count + s.FaceFootprint.Count == 0 || !(s.Point || (s.Direct && !s.Surface) || s.Convex)) continue;
            var b = boxes[k]; var alone = true;
            for (var i = (int)Math.Floor(b.X0 / cell); alone && i <= (int)Math.Floor(b.X1 / cell); i++)
                for (var j = (int)Math.Floor(b.Y0 / cell); alone && j <= (int)Math.Floor(b.Y1 / cell); j++)
                    if (grid.TryGetValue((i, j), out var list))
                        foreach (var o in list)
                            if (o != k && (s.Surface || items[o].Surface || items[o].Invalid) && boxes[o].X0 <= b.X1 && b.X0 <= boxes[o].X1 && boxes[o].Y0 <= b.Y1 && b.Y0 <= boxes[o].Y1) { alone = false; break; }
            if (alone) verdict[k] = 3;
        }
    }

    private static Source Describe(PreparedLeaf leaf, ViewMap map, double tolerance, RasterOptions options)
    {
        var source = new Source { Leaf = leaf };
        try
        {
            switch (leaf.Geometry)
            {
                case Point point:
                    source.Point = true; AddPoints(source, map, new[] { point.Location }, 0); break;
                case Curve curve:
                    {
                        source.Direct = true;
                        using var nurbs = curve.ToNurbsCurve();
                        if (nurbs is null) { source.Invalid = true; break; }
                        AddPoints(source, map, nurbs.Points.Select(p => p.Location), tolerance); break;
                    }
                case Mesh mesh:
                    source.Surface = true;
                    AddPoints(source, map, mesh.Vertices.ToPoint3dArray(), tolerance); break;
                case Brep brep: DescribeBrep(source, brep, map, tolerance, options); break;
                case Extrusion extrusion:
                    {
                        using var brep = extrusion.ToBrep(true);
                        if (brep is null) { source.Invalid = true; break; }
                        DescribeBrep(source, brep, map, tolerance, options); break;
                    }
                default: source.Invalid = true; break;
            }
        }
        catch (Exception) { source.Invalid = true; source.Patches = null; }
        return source;
    }

    private static (double X0, double Y0, double X1, double Y1, double Near) FaceBox(RasterFace face, double dilation)
    {
        double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
        for (var k = 0; k < face.Loops.Length; k += 2)
        { x0 = Math.Min(x0, face.Loops[k]); x1 = Math.Max(x1, face.Loops[k]); y0 = Math.Min(y0, face.Loops[k + 1]); y1 = Math.Max(y1, face.Loops[k + 1]); }
        return (x0 - dilation, y0 - dilation, x1 + dilation, y1 + dilation, face.MaxDepth);
    }

    private static void AddPoints(Source source, ViewMap map, IEnumerable<Point3d> points, double slack)
    {
        double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue, near = double.MaxValue;
        var any = false;
        foreach (var p in points)
        {
            if (!p.IsValid) { source.Invalid = true; return; }
            any = true;
            var x = map.Px(p); var y = map.Py(p);
            x0 = Math.Min(x0, x); x1 = Math.Max(x1, x); y0 = Math.Min(y0, y); y1 = Math.Max(y1, y);
            near = Math.Min(near, map.Depth(p));
        }
        if (!any) { source.Invalid = true; return; }
        var r = slack / map.Grid.Pixel;
        source.Footprint.Add((x0 - r, y0 - r, x1 + r, y1 + r, near - slack));
    }

    private sealed class FaceLoops
    {
        internal readonly List<double> Xy = new();
        internal readonly List<int> Starts = new();
        internal readonly List<int> SegmentEdge = new(); // edge index per segment, -1 for none
        internal double MaxDepth = double.MinValue, MinDepth = double.MaxValue;
        /// <summary>Bound on the distance between the true boundary and the polygon, in pixels (arcs and Bezier spans).</summary>
        internal double DeviationPixels;
        /// <summary>Extra depth allowance in model units (arc bulge beyond the chord polygon's depth range).</summary>
        internal double DepthPad;
        /// <summary>Start (point index) of the outer loop, or -1.</summary>
        internal int OuterStart = -1;
    }

    private static void DescribeBrep(Source source, Brep brep, ViewMap map, double tolerance, RasterOptions options)
    {
        source.Surface = true; source.Brep = true;
        var grid = map.Grid; var pixel = grid.Pixel;
        var allPlanar = true;
        var faces = new List<(RasterFace Face, FaceLoops Loops, int Index)>();
        var sides = new Dictionary<int, List<double>>();
        double maxDeviation = 0;
        foreach (var face in brep.Faces)
        {
            if (!face.TryGetPlane(out var plane, tolerance))
            {
                allPlanar = false;
                if (options.CurvedPixels > 0 && face.IsSurface && Curved(source, face, map, tolerance, options)) continue;
                using var nurbs = face.UnderlyingSurface().ToNurbsSurface();
                if (nurbs is null) { source.Invalid = true; return; }
                AddPoints(source, map, nurbs.Points.Select(c => c.Location), tolerance);
                continue;
            }
            var loops = Loops(face, map, tolerance, options.FreeformTrimPixels);
            if (loops is null)
            {
                // Planar but with free-form trims: footprint from edge control points (a planar region lies in the hull of its boundary).
                var points = new List<Point3d>();
                foreach (var ei in face.AdjacentEdges()) { using var c = brep.Edges[ei].ToNurbsCurve(); if (c is null) { source.Invalid = true; return; } points.AddRange(c.Points.Select(p => p.Location)); }
                AddPoints(source, map, points, tolerance);
                continue;
            }
            var n = plane.Normal; var nD = n * map.D;
            if (options.PolygonFootprints && Math.Abs(nD) >= .02)
            {
                // Lower plane: the face lies within tolerance of its plane, so its depth is at least the plane minus tol/|nD| and pads.
                var lower = 2 * tolerance / Math.Abs(nD) + loops.DepthPad + 1e-9 * map.Offset;
                double fX = n * map.X, fY = n * map.Y, fw = n * (map.O - plane.Origin);
                source.FaceFootprint.Add((new RasterFace { Loops = loops.Xy.ToArray(), Starts = loops.Starts.ToArray(),
                    A = -pixel * fX / nD, B = -pixel * fY / nD, C = -(fw + grid.U0 * fX + grid.V0 * fY) / nD + map.Offset - lower,
                    MaxDepth = loops.MinDepth - loops.DepthPad - tolerance }, loops.DeviationPixels + 2 * tolerance / pixel + 1e-6));
            }
            else
            {
                double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
                for (var k = 0; k < loops.Xy.Count; k += 2)
                { x0 = Math.Min(x0, loops.Xy[k]); x1 = Math.Max(x1, loops.Xy[k]); y0 = Math.Min(y0, loops.Xy[k + 1]); y1 = Math.Max(y1, loops.Xy[k + 1]); }
                var rr = loops.DeviationPixels + tolerance / pixel;
                source.Footprint.Add((x0 - rr, y0 - rr, x1 + rr, y1 + rr, loops.MinDepth - loops.DepthPad - tolerance));
            }
            if (Math.Abs(nD) < .02) continue; // grazing: never an occluder
            // Front-facing selection only tightens depth; soundness does not depend on orientation.
            if (!Front(face, map)) continue;
            var slack = 2 * tolerance / Math.Abs(nD) + loops.DepthPad + 1e-9 * map.Offset;
            var nX = n * map.X; var nY = n * map.Y; var w = n * (map.O - plane.Origin);
            var rf = new RasterFace
            {
                Loops = loops.Xy.ToArray(), Starts = loops.Starts.ToArray(),
                A = -pixel * nX / nD, B = -pixel * nY / nD, C = -(w + grid.U0 * nX + grid.V0 * nY) / nD + map.Offset + slack,
                MaxDepth = loops.MaxDepth + slack
            };
            maxDeviation = Math.Max(maxDeviation, loops.DeviationPixels);
            faces.Add((rf, loops, face.FaceIndex));
            // Side of each linear edge on which this face lies, in projection.
            foreach (var loop in face.Loops)
                foreach (var trim in loop.Trims)
                {
                    if (trim.Edge is not { } edge || !edge.IsLinear(tolerance)) continue;
                    var e = edge.PointAtEnd - edge.PointAtStart;
                    var t = trim.Domain.Mid; var uv = trim.PointAt(t); var tangent = trim.TangentAt(t);
                    if (!face.Evaluate(uv.X, uv.Y, 1, out _, out var d) || d is null || d.Length < 2) continue;
                    var m = d[0] * -tangent.Y + d[1] * tangent.X;
                    double ex = e * map.X, ey = e * map.Y, mx = m * map.X, my = m * map.Y;
                    var cross = ex * my - ey * mx; var scale = Math.Sqrt((ex * ex + ey * ey) * (mx * mx + my * my));
                    if (!(scale > 0)) continue;
                    if (!sides.TryGetValue(edge.EdgeIndex, out var list)) sides[edge.EdgeIndex] = list = new();
                    list.Add(Math.Abs(cross) > 1e-9 * scale ? Math.Sign(cross) : 0);
                }
        }
        source.Direct = allPlanar;
        source.Convex = allPlanar && (brep.Faces.Count == 1 || ConvexSolid(brep, tolerance));
        if (faces.Count == 0) return;
        bool Interior(int edgeIndex) => edgeIndex >= 0 && brep.Edges[edgeIndex].TrimCount == 2 &&
            sides.TryGetValue(edgeIndex, out var s) && s.Count == 2 && s[0] * s[1] < 0;
        var boundary = new List<double>(); var sided = new List<SidedSegment>();
        foreach (var (face, loops, _) in faces)
        {
            var points = loops.Xy.Count / 2;
            // Trims keep the face on their left in parameter space; the planar face maps to the screen with one orientation,
            // so the face lies left of every projected loop segment iff the projected outer loop is counter-clockwise.
            var side = 0;
            if (loops.OuterStart >= 0)
            {
                var l0 = loops.Starts.IndexOf(loops.OuterStart); var e0 = l0 + 1 < loops.Starts.Count ? loops.Starts[l0 + 1] : points;
                double area = 0;
                for (var k = loops.OuterStart; k < e0; k++) { var m = k + 1 < e0 ? k + 1 : loops.OuterStart; area += loops.Xy[2 * k] * loops.Xy[2 * m + 1] - loops.Xy[2 * m] * loops.Xy[2 * k + 1]; }
                side = area > 1e-9 ? 1 : area < -1e-9 ? -1 : 0;
            }
            for (var l = 0; l < loops.Starts.Count; l++)
            {
                var first = loops.Starts[l]; var end = l + 1 < loops.Starts.Count ? loops.Starts[l + 1] : points;
                for (var k = first; k < end; k++)
                {
                    if (Interior(loops.SegmentEdge[k])) continue;
                    var m = k + 1 < end ? k + 1 : first;
                    boundary.Add(loops.Xy[2 * k]); boundary.Add(loops.Xy[2 * k + 1]); boundary.Add(loops.Xy[2 * m]); boundary.Add(loops.Xy[2 * m + 1]);
                    sided.Add(new SidedSegment(loops.Xy[2 * k], loops.Xy[2 * k + 1], loops.Xy[2 * m], loops.Xy[2 * m + 1], loops.SegmentEdge[k] >= 0 ? side : 0));
                }
            }
        }
        if (options.MergeSeams)
        {
            source.Seamable = new PatchInput { Faces = faces.Select(f => f.Face).ToArray(), Boundary = sided, Dilation = 2 * tolerance / pixel + maxDeviation + 1e-6 };
            return;
        }
        (source.Patches ??= new()).Add(new RasterPatch
        {
            Faces = faces.Select(f => f.Face).ToArray(), Boundary = boundary.ToArray(),
            Dilation = 2 * tolerance / pixel + maxDeviation + 1e-6
        });
    }

    /// <summary>
    /// Untrimmed curved face: per-cell footprints from the control-net hulls, and up to two occluder patches (cells grouped by
    /// projected orientation), each covering the nonzero-winding region of its projected boundary chords (see SurfaceCells).
    /// </summary>
    private static bool Curved(Source source, BrepFace face, ViewMap map, double tolerance, RasterOptions options)
    {
        using var ns = face.UnderlyingSurface().ToNurbsSurface();
        if (ns is null) return false;
        int du = ns.Degree(0), dv = ns.Degree(1), cu = ns.Points.CountU, cv = ns.Points.CountV;
        var knotsU = new double[ns.KnotsU.Count]; for (var k = 0; k < knotsU.Length; k++) knotsU[k] = ns.KnotsU[k];
        var knotsV = new double[ns.KnotsV.Count]; for (var k = 0; k < knotsV.Length; k++) knotsV[k] = ns.KnotsV[k];
        var net = new double[cu * cv * 4];
        for (var i = 0; i < cu; i++)
            for (var j = 0; j < cv; j++)
            {
                var cp = ns.Points.GetControlPoint(i, j); var w = cp.Weight; var p = cp.Location;
                var k = (i * cv + j) * 4; net[k] = p.X * w; net[k + 1] = p.Y * w; net[k + 2] = p.Z * w; net[k + 3] = w;
            }
        var grid = SurfaceCells.Build(du, dv, knotsU, knotsV, cu, cv, net, map.Affine, options.CurvedPixels, options.CurvedMaxCells);
        if (grid is null) return false;
        // Runtime check of the data convention: grid corners must be surface points.
        var domU = ns.Domain(0); var domV = ns.Domain(1);
        foreach (var (i, j, u, v) in new[] { (0, 0, domU.Min, domV.Min), (grid.Nu, grid.Nv, domU.Max, domV.Max), (grid.Nu, 0, domU.Max, domV.Min) })
        {
            var p = ns.PointAt(u, v); var (x, y) = grid.Corner(i, j);
            if (Math.Abs(map.Px(p) - x) + Math.Abs(map.Py(p) - y) > 1e-6) return false;
        }
        var pixel = map.Grid.Pixel;
        var slack = tolerance + 1e-9 * map.Offset;
        for (var i = 0; i < grid.Nu; i++)
            for (var j = 0; j < grid.Nv; j++)
            {
                var c = (i * grid.Nv + j) * 6; var r = tolerance / pixel;
                source.Footprint.Add((grid.Cells[c] - r, grid.Cells[c + 1] - r, grid.Cells[c + 2] + r, grid.Cells[c + 3] + r, grid.Cells[c + 4] - slack));
            }
        (source.Patches ??= new()).AddRange(grid.Patches(slack, grid.Deviation + tolerance / pixel + 1e-6));
        return true;
    }

    /// <summary>Closed, all-planar, linear-edged solid with every vertex on the inner side of every face plane.</summary>
    internal static bool ConvexSolid(Brep brep, double tolerance)
    {
        if (!brep.IsSolid || brep.Faces.Count < 4) return false;
        foreach (var edge in brep.Edges) if (!edge.IsLinear(tolerance)) return false;
        var vertices = brep.Vertices.Select(v => v.Location).ToArray();
        foreach (var face in brep.Faces)
        {
            if (!face.TryGetPlane(out var plane, tolerance) || Outward(face) is not { } n) return false;
            if (n * plane.Normal < 0) plane.Flip();
            foreach (var v in vertices) if (plane.DistanceTo(v) > tolerance) return false;
        }
        return true;
    }

    /// <summary>Outward unit normal of a planar face (surface normal, reversed with the face orientation).</summary>
    internal static Vector3d? Outward(BrepFace face)
    {
        var u = face.Domain(0).Mid; var v = face.Domain(1).Mid;
        if (!face.Evaluate(u, v, 1, out _, out var d) || d is null || d.Length < 2) return null;
        var n = Vector3d.CrossProduct(d[0], d[1]); if (face.OrientationIsReversed) n = -n;
        return n.Unitize() ? n : null;
    }

    /// <summary>Projects a curve onto the frame plane as NURBS. Never transform an ArcCurve/LineCurve by a projection:
    /// ArcCurve keeps a circle under non-similarities, so a tilted arc would not become its true ellipse.</summary>
    private static bool Project(ref Curve curve, Transform projection)
    {
        var nurbs = curve.ToNurbsCurve(); curve.Dispose();
        if (nurbs is null) { curve = new LineCurve(Point3d.Origin, Point3d.Origin); return false; }
        curve = nurbs; return nurbs.Transform(projection);
    }

    private static bool Front(BrepFace face, ViewMap map)
    {
        var u = face.Domain(0).Mid; var v = face.Domain(1).Mid;
        if (!face.Evaluate(u, v, 1, out _, out var d) || d is null || d.Length < 2) return true;
        var n = Vector3d.CrossProduct(d[0], d[1]); if (face.OrientationIsReversed) n = -n;
        return n * map.D < 0;
    }

    /// <summary>Face loops as pixel polygons in trim order, or null when an edge cannot be bounded (free-form when disabled).</summary>
    private static FaceLoops? Loops(BrepFace face, ViewMap map, double tolerance, double freeformPixels)
    {
        var result = new FaceLoops();
        void Add(Point3d p, int edge)
        {
            result.Xy.Add(map.Px(p)); result.Xy.Add(map.Py(p)); result.SegmentEdge.Add(edge);
            var d = map.Depth(p); result.MaxDepth = Math.Max(result.MaxDepth, d); result.MinDepth = Math.Min(result.MinDepth, d);
        }
        foreach (var loop in face.Loops)
        {
            var start = result.Xy.Count / 2;
            foreach (var trim in loop.Trims)
            {
                if (trim.TrimType == BrepTrimType.Singular) continue;
                if (trim.Edge is not { } edge) return null;
                var reversed = trim.IsReversed();
                if (edge.IsLinear(tolerance)) { Add(reversed ? edge.PointAtEnd : edge.PointAtStart, edge.EdgeIndex); continue; }
                if (edge.TryGetArc(out var arc, tolerance))
                {
                    var count = Math.Max(4, (int)Math.Ceiling(arc.Angle / (Math.PI / 32)));
                    var step = arc.Angle / count;
                    var sagitta = arc.Radius * (1 - Math.Cos(step / 2)) + tolerance;
                    result.DeviationPixels = Math.Max(result.DeviationPixels, sagitta / map.Grid.Pixel); result.DepthPad = Math.Max(result.DepthPad, sagitta);
                    // Orient the arc along the trim: the trim start is the edge end when reversed.
                    var from = reversed ? edge.PointAtEnd : edge.PointAtStart;
                    var forward = arc.StartPoint.DistanceTo(from) <= arc.EndPoint.DistanceTo(from);
                    for (var k = 0; k < count; k++)
                    {
                        var a = forward ? k * step : arc.Angle - k * step;
                        Add(arc.PointAt(arc.AngleDomain.Min + a), -1);
                    }
                    continue;
                }
                if (!(freeformPixels > 0)) return null;
                var flat = Flatten(edge, map, freeformPixels);
                if (flat is null) return null;
                result.DeviationPixels = Math.Max(result.DeviationPixels, flat.Deviation + tolerance / map.Grid.Pixel);
                result.MaxDepth = Math.Max(result.MaxDepth, flat.MaxDepth); result.MinDepth = Math.Min(result.MinDepth, flat.MinDepth);
                var pts = flat.Points; var count2 = pts.Count / 2;
                for (var k = 0; k < count2 - 1; k++)
                {
                    var idx = reversed ? count2 - 1 - k : k;
                    result.Xy.Add(pts[2 * idx]); result.Xy.Add(pts[2 * idx + 1]); result.SegmentEdge.Add(-1);
                }
            }
            if (result.Xy.Count / 2 - start < 3) { result.Xy.RemoveRange(start * 2, result.Xy.Count - start * 2); result.SegmentEdge.RemoveRange(start, result.SegmentEdge.Count - start); continue; }
            if (loop.LoopType == BrepLoopType.Outer) result.OuterStart = start;
            result.Starts.Add(start);
        }
        return result.Starts.Count == 0 ? null : result;
    }

    private static bool Matches(double[] cv, double t, Point3d expected)
    {
        var n = cv.Length / 4; var work = (double[])cv.Clone();
        for (var level = 1; level < n; level++)
            for (var i = 0; i < n - level; i++)
                for (var c = 0; c < 4; c++) work[4 * i + c] = (1 - t) * work[4 * i + c] + t * work[4 * (i + 1) + c];
        var p = new Point3d(work[0] / work[3], work[1] / work[3], work[2] / work[3]);
        return p.DistanceTo(expected) <= 1e-9 * (1 + Math.Abs(expected.X) + Math.Abs(expected.Y) + Math.Abs(expected.Z));
    }

    /// <summary>Certified pixel polyline of an edge (in edge direction) from its rational Bezier spans, or null.</summary>
    private static BezierFlatten.Result? Flatten(Curve curve, ViewMap map, double epsilon)
    {
        using var nurbs = curve.ToNurbsCurve();
        if (nurbs is null) return null;
        var spans = BezierCurve.CreateBeziers(nurbs);
        if (spans is null || spans.Length == 0) return null;
        var result = new BezierFlatten.Result(); var affine = map.Affine; var ok = true;
        try
        {
            for (var k = 0; k < spans.Length && ok; k++)
            {
                var span = spans[k]; var n = span.ControlVertexCount; var cv = new double[4 * n];
                for (var i = 0; i < n; i++)
                {
                    var p = span.GetControlVertex4d(i);
                    if (span.IsRational) { cv[4 * i] = p.X; cv[4 * i + 1] = p.Y; cv[4 * i + 2] = p.Z; cv[4 * i + 3] = p.W; }
                    else { cv[4 * i] = p.X; cv[4 * i + 1] = p.Y; cv[4 * i + 2] = p.Z; cv[4 * i + 3] = 1; }
                }
                // Verify the homogeneous convention on this span (start, middle, end) before trusting the hull bound.
                if (!Matches(cv, 0, span.PointAt(0)) || !Matches(cv, 1, span.PointAt(1)) || !Matches(cv, .5, span.PointAt(.5))) return null;
                ok = BezierFlatten.Span(cv, affine, epsilon, 64, result, k == 0);
            }
        }
        finally { foreach (var span in spans) span.Dispose(); }
        return ok ? result : null;
    }

    /// <summary>
    /// Exact output of an isolated source: a curve is wholly visible; a convex solid shows an edge iff an adjacent face is
    /// front-facing or edge-on (an edge-on face of a convex solid projects onto its silhouette), else it is hidden.
    /// Edges between two edge-on faces are parallel to the view and project to points. Coincident visible edges are emitted once.
    /// </summary>
    internal static IEnumerable<(Curve Projected, Curve World, LineClass Class, bool Profile)> IsolatedCurves(PreparedLeaf leaf, Rectangle3d frame, double tolerance, RasterOptions options)
    {
        var projection = Transform.PlanarProjection(frame.Plane); var d = -frame.Plane.ZAxis;
        if (leaf.Geometry is Curve curve)
        {
            var world = curve.DuplicateCurve(); var projected = curve.DuplicateCurve();
            if (Project(ref projected, projection) && projected.GetLength() > tolerance) yield return (projected, world, LineClass.Visible, false);
            else { projected.Dispose(); world.Dispose(); }
            yield break;
        }
        var brep = leaf.Geometry as Brep; Brep? owned = null;
        if (leaf.Geometry is Extrusion extrusion) brep = owned = extrusion.ToBrep(true);
        if (brep is null) yield break;
        try
        {
            var side = new int[brep.Faces.Count];
            foreach (var face in brep.Faces)
            {
                var n = Outward(face) ?? throw new InvalidOperationException("Face normal unavailable.");
                var dot = n * d; side[face.FaceIndex] = dot < -1e-9 ? -1 : dot > 1e-9 ? 1 : 0;
            }
            var emitted = new List<(Point3d A, Point3d B)>();
            foreach (var edge in brep.Edges)
            {
                if (edge.Valence == EdgeAdjacency.Interior && edge.IsSmoothManifoldEdge(options.TangentAngle)) continue;
                var faces = edge.AdjacentFaces();
                // A single face never occludes its own boundary.
                var visible = brep.Faces.Count == 1 || faces.Any(f => side[f] <= 0); var profile = visible && (brep.Faces.Count == 1 || faces.Any(f => side[f] > 0));
                var world = edge.DuplicateCurve(); var projected = world.DuplicateCurve();
                if (!Project(ref projected, projection) || projected.GetLength() <= tolerance) { projected.Dispose(); world.Dispose(); continue; }
                if (visible)
                {
                    var a = projected.PointAtStart; var b = projected.PointAtEnd;
                    if (emitted.Any(e => (e.A.DistanceTo(a) <= tolerance && e.B.DistanceTo(b) <= tolerance) || (e.A.DistanceTo(b) <= tolerance && e.B.DistanceTo(a) <= tolerance)))
                    { projected.Dispose(); world.Dispose(); continue; }
                    emitted.Add((a, b));
                }
                yield return (projected, world, visible ? LineClass.Visible : LineClass.Hidden, profile);
            }
        }
        finally { owned?.Dispose(); }
    }

    /// <summary>
    /// Hidden candidates of a culled curved Brep, matching the native candidate set with tangent edges and seams off:
    /// every edge that is not a smooth manifold edge (seams are smooth), plus the parallel-view tangent silhouettes.
    /// </summary>
    internal static IEnumerable<(Curve Projected, Curve World)> CurvedHiddenCurves(PreparedLeaf leaf, Rectangle3d frame, double tolerance, RasterOptions options)
    {
        var projection = Transform.PlanarProjection(frame.Plane);
        var brep = leaf.Geometry as Brep; Brep? owned = null;
        if (leaf.Geometry is Extrusion extrusion) brep = owned = extrusion.ToBrep(true);
        if (brep is null) yield break;
        var world = new List<Curve>();
        try
        {
            foreach (var edge in brep.Edges)
            {
                if (edge.Valence == EdgeAdjacency.Interior && edge.IsSmoothManifoldEdge(options.TangentAngle)) continue;
                world.Add(edge.DuplicateCurve());
            }
            var silhouettes = Silhouette.Compute(brep, SilhouetteType.Tangent, -frame.Plane.ZAxis, tolerance, options.TangentAngle)
                ?? throw new InvalidOperationException("Silhouette computation failed.");
            foreach (var silhouette in silhouettes) if (silhouette.Curve is { } c) world.Add(c.DuplicateCurve());
        }
        finally { owned?.Dispose(); }
        foreach (var w in world)
        {
            var projected = w.DuplicateCurve();
            if (!Project(ref projected, projection) || projected.GetLength() <= tolerance) { projected.Dispose(); w.Dispose(); continue; }
            yield return (projected, w);
        }
    }

    /// <summary>Hidden output for a culled source whose candidate set is exactly its non-tangent edges (all-planar Brep) or itself (curve).</summary>
    internal static IEnumerable<(Curve Projected, Curve World)> HiddenCurves(PreparedLeaf leaf, Rectangle3d frame, double tolerance, RasterOptions options)
    {
        var projection = Transform.PlanarProjection(frame.Plane);
        IEnumerable<Curve> World()
        {
            if (leaf.Geometry is Curve curve) { yield return curve.DuplicateCurve(); yield break; }
            var brep = leaf.Geometry as Brep; Brep? owned = null;
            if (leaf.Geometry is Extrusion extrusion) brep = owned = extrusion.ToBrep(true);
            if (brep is null) yield break;
            try
            {
                foreach (var edge in brep.Edges)
                {
                    if (edge.Valence == EdgeAdjacency.Interior && edge.IsSmoothManifoldEdge(options.TangentAngle)) continue;
                    yield return edge.DuplicateCurve();
                }
            }
            finally { owned?.Dispose(); }
        }
        foreach (var world in World())
        {
            var projected = world.DuplicateCurve();
            if (!Project(ref projected, projection) || projected.GetLength() <= tolerance) { projected.Dispose(); world.Dispose(); continue; }
            yield return (projected, world);
        }
    }
}
