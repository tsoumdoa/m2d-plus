using System.Diagnostics;
using M2DPlus.Core;
using M2DPlus.Rhino.Services;
using Rhino.Geometry;
using Point = Rhino.Geometry.Point;

// This assembly is a standalone measurement tool. Nothing here is shipped or called by the product.
internal static class RasterProjection
{
    internal sealed record Candidate(Curve Curve, VisibilityInput Input, int[] Adjacent, string Feature, bool Profile = false);
    internal sealed record Metrics(int RequestedResolution, int Width, int Height, double Pixel, double MeshTolerance,
        long BufferBytes, int Triangles, int Candidates, int Silhouettes, int MeshSilhouetteSegments,
        double Tessellate, double Raster, double Silhouette, double MeshSilhouette, double Edges, double Classify, double Total);
    internal sealed class Result(ViewDrawing drawing, Metrics metrics, List<Candidate> candidates) : IDisposable
    {
        internal ViewDrawing Drawing { get; } = drawing;
        internal Metrics Timings { get; } = metrics;
        internal List<Candidate> Candidates { get; } = candidates;
        public void Dispose() { Drawing.Dispose(); foreach (var candidate in Candidates) candidate.Curve.Dispose(); }
    }
    internal static Result Solve(IReadOnlyList<VisibilityInput> inputs, ViewRequest view, Rectangle3d frame,
        double tolerance, double angle, int resolution, int threads, bool meshSilhouettes = false)
    {
        var total = Stopwatch.StartNew();
        var worldToLocal = Transform.PlaneToPlane(frame.Plane, Plane.WorldXY);
        var bounds = BoundingBox.Empty;
        foreach (var input in inputs) bounds.Union(input.Geometry.GetBoundingBox(worldToLocal));
        var pad = Math.Max(tolerance * 16, bounds.Diagonal.Length * .01);
        bounds.Inflate(pad);
        var spanX = bounds.Max.X - bounds.Min.X; var spanY = bounds.Max.Y - bounds.Min.Y;
        var nominalPixel = Math.Max(spanX, spanY) / resolution;
        var meshTolerance = Math.Max(tolerance, nominalPixel * .125);
        var options = new ParallelOptions { MaxDegreeOfParallelism = threads };
        var meshes = new List<(Mesh Mesh, int Face)>[inputs.Count];
        var candidates = new List<Candidate>();
        var drawing = new ViewDrawing { Name = view.Name, Frame = frame, Settings = view.Settings, Tolerance = tolerance };
        var faceOffsets = new int[inputs.Count]; var nextFace = 0;
        for (var i = 0; i < inputs.Count; i++)
        { faceOffsets[i] = nextFace; nextFace += inputs[i].Geometry is Brep brep ? brep.Faces.Count : inputs[i].Geometry is Mesh m ? m.Faces.Count : 1; }
        RasterBuffer.Vertex Local(Point3d p)
        { p.Transform(worldToLocal); return new(p.X, p.Y, p.Z); }
        var clock = Stopwatch.StartNew();
        try
        {
            Parallel.For(0, inputs.Count, options, i =>
            {
                var batch = meshes[i] = [];
                using var parameters = new MeshingParameters
                {
                    Tolerance = meshTolerance, RelativeTolerance = 0, RefineGrid = true,
                    JaggedSeams = false, SimplePlanes = true, MinimumEdgeLength = tolerance,
                    GridAngle = .2, RefineAngle = .2
                };
                switch (inputs[i].Geometry)
                {
                    case Brep b:
                        // The usual result is one render mesh per face. Degenerate faces can be
                        // omitted by the whole-Brep mesher, so use explicit face ownership on mismatch.
                        var parts = Mesh.CreateFromBrep(b, parameters) ?? throw new InvalidOperationException("Brep tessellation failed");
                        if (parts.Length != b.Faces.Count)
                        {
                            foreach (var part in parts) part.Dispose();
                            for (var f = 0; f < b.Faces.Count; f++)
                            {
                                using var single = b.Faces[f].DuplicateFace(false);
                                var faceMeshes = Mesh.CreateFromBrep(single, parameters) ?? [];
                                if (faceMeshes.Length == 0)
                                {
                                    // A view-scale tolerance can exceed a small face's dimensions.
                                    // Retry at document tolerance instead of silently dropping thin material.
                                    using var retry = new MeshingParameters(parameters) { Tolerance = tolerance,
                                        MinimumEdgeLength = 0, SimplePlanes = false, GridMinCount = 16 };
                                    faceMeshes = Mesh.CreateFromBrep(single, retry) ?? [];
                                }
                                if (faceMeshes.Length == 0)
                                {
                                    using var area = AreaMassProperties.Compute(single);
                                    if (area is null || area.Area > tolerance * tolerance)
                                        throw new NotSupportedException($"Prepared face could not be tessellated: source={inputs[i].Source.Identity}; face={f}; valid={single.IsValid}; area={area?.Area}; bounds={single.GetBoundingBox(true)}");
                                }
                                foreach (var part in faceMeshes) batch.Add((part, faceOffsets[i] + f));
                            }
                        }
                        else for (var f = 0; f < parts.Length; f++) batch.Add((parts[f], faceOffsets[i] + f));
                        break;
                    case Mesh m: batch.Add((m.DuplicateMesh(), faceOffsets[i])); break;
                    case Curve or Point: break;
                    default: throw new NotSupportedException("Prepared registration type: " + inputs[i].Geometry.GetType().Name);
                }
            });
            var triangles = new List<RasterBuffer.Triangle>();
            for (var i = 0; i < meshes.Length; i++)
                foreach (var (mesh, face) in meshes[i])
                {
                    for (var f = 0; f < mesh.Faces.Count; f++)
                    {
                        var mf = mesh.Faces[f]; var id = inputs[i].Geometry is Mesh ? face + f : face;
                        var a = Local(mesh.Vertices.Point3dAt(mf.A)); var b = Local(mesh.Vertices.Point3dAt(mf.B)); var c = Local(mesh.Vertices.Point3dAt(mf.C));
                        triangles.Add(new(a, b, c, id));
                        if (mf.IsQuad) triangles.Add(new(a, c, Local(mesh.Vertices.Point3dAt(mf.D)), id));
                    }
                }
            var tessellate = clock.Elapsed.TotalSeconds; clock.Restart();
            var grid = new RasterBuffer(bounds.Min.X, bounds.Min.Y, spanX, spanY, resolution, triangles.ToArray(), threads);
            var raster = clock.Elapsed.TotalSeconds; clock.Restart();
            var batches = new List<Candidate>[inputs.Count];
            Parallel.For(0, inputs.Count, options, i =>
            {
                var input = inputs[i]; var batch = batches[i] = [];
                if (input.Geometry is Brep b)
                {
                    foreach (var edge in b.Edges)
                    {
                        var adjacent = edge.AdjacentFaces(); var seam = adjacent is [var a, var z] && a == z;
                        if (seam ? view.Settings?.Seams != true : edge.IsSmoothManifoldEdge(angle) && view.Settings?.Tangents != true) continue;
                        batch.Add(new(edge.DuplicateCurve(), input, adjacent.Select(f => faceOffsets[i] + f).ToArray(),
                            seam ? "seam" : adjacent.Any(f => b.Faces[f].IsPlanar(tolerance)) ? "cap / sharp edge" : "curved trim / contact edge"));
                    }
                }
                else if (input.Geometry is Curve c) batch.Add(new(c.DuplicateCurve(), input, [], "wire / glass / contact feature"));
                else if (input.Geometry is Mesh m)
                {
                    m.FaceNormals.ComputeFaceNormals();
                    for (var e = 0; e < m.TopologyEdges.Count; e++)
                    {
                        var faces = m.TopologyEdges.GetConnectedFaces(e);
                        var normals = faces.Select(f => (Vector3d)m.FaceNormals[f]).ToArray();
                        var profile = normals.Length == 2 && (normals[0] * view.Direction) * (normals[1] * view.Direction) <= 0;
                        var sharp = normals.Length != 2 || Vector3d.VectorAngle(normals[0], normals[1]) > angle;
                        if (profile || sharp || view.Settings?.Tangents == true)
                            batch.Add(new(new LineCurve(m.TopologyEdges.EdgeLine(e)), input, faces.Select(f => faceOffsets[i] + f).ToArray(), "mesh edge", profile));
                    }
                }
            });
            foreach (var batch in batches) candidates.AddRange(batch);
            var edges = clock.Elapsed.TotalSeconds; clock.Restart();
            // Native tangency curves preserve exact loci. Time this independently from meshing and classification.
            Parallel.For(0, inputs.Count, options, i =>
            {
                var batch = batches[i] = [];
                if (inputs[i].Geometry is not Brep b) return;
                foreach (var s in Silhouette.Compute(b, SilhouetteType.Tangent, view.Direction, tolerance, angle) ?? [])
                {
                    if (s.Curve is not { } c) continue;
                    var component = s.GeometryComponentIndex;
                    var face = component.ComponentIndexType == ComponentIndexType.BrepFace ? component.Index : -1;
                    // Rhino often leaves the component unset. Match the tangent curve to trimmed faces,
                    // rather than treating an edge component number as a face number.
                    var matched = new HashSet<int>();
                    if (face >= 0 && face < b.Faces.Count) matched.Add(faceOffsets[i] + face);
                    else for (var station = 0; station < 5; station++)
                    {
                        var p = c.PointAt(c.Domain.ParameterAt((station + .5) / 5));
                        var nearest = -1; var distance = double.PositiveInfinity;
                        for (var f = 0; f < b.Faces.Count; f++)
                            if (b.Faces[f].ClosestPoint(p, out var u, out var v))
                            { var d = b.Faces[f].PointAt(u, v).DistanceTo(p); if (d < distance) { distance = d; nearest = f; } }
                        if (nearest >= 0) matched.Add(faceOffsets[i] + nearest);
                    }
                    var adjacent = matched.ToArray();
                    batch.Add(new(c.DuplicateCurve(), inputs[i], adjacent, "native tangent silhouette", true));
                }
            });
            var silhouette = clock.Elapsed.TotalSeconds;
            var silhouettes = batches.Sum(b => b.Count);
            var exactSilhouettes = batches.SelectMany(b => b).ToArray();
            clock.Restart();
            var meshBatches = new List<Candidate>[inputs.Count];
            Parallel.For(0, inputs.Count, options, i =>
            {
                var batch = meshBatches[i] = [];
                if (inputs[i].Geometry is not Brep) return;
                foreach (var (mesh, face) in meshes[i])
                {
                    mesh.FaceNormals.ComputeFaceNormals();
                    for (var e = 0; e < mesh.TopologyEdges.Count; e++)
                    {
                        var adjacent = mesh.TopologyEdges.GetConnectedFaces(e);
                        if (adjacent.Length != 2) continue; // Naked boundaries are supplied by exact Brep edges.
                        if (((Vector3d)mesh.FaceNormals[adjacent[0]] * view.Direction) * ((Vector3d)mesh.FaceNormals[adjacent[1]] * view.Direction) > 0) continue;
                        // A coplanar pair is not a silhouette.
                        if (Math.Abs((Vector3d)mesh.FaceNormals[adjacent[0]] * view.Direction) < 1e-10 &&
                            Math.Abs((Vector3d)mesh.FaceNormals[adjacent[1]] * view.Direction) < 1e-10) continue;
                        batch.Add(new(new LineCurve(mesh.TopologyEdges.EdgeLine(e)), inputs[i], [face], "mesh tangent silhouette", true));
                    }
                }
            });
            var meshSilhouette = clock.Elapsed.TotalSeconds; var meshCount = meshBatches.Sum(b => b.Count);
            var meshCurves = meshBatches.SelectMany(b => b).ToArray();
            candidates.AddRange(meshSilhouettes ? meshCurves : exactSilhouettes);
            foreach (var candidate in meshSilhouettes ? exactSilhouettes : meshCurves) candidate.Curve.Dispose();
            clock.Restart();
            var output = new List<DrawingPiece>[candidates.Count];
            Parallel.For(0, candidates.Count, options, i =>
            {
                var candidate = candidates[i]; using var curve = candidate.Curve.ToNurbsCurve(); var batch = output[i] = [];
                using var flat = curve.DuplicateCurve();
                if (!flat.Transform(Transform.PlanarProjection(frame.Plane)))
                {
                    var box = curve.GetBoundingBox(worldToLocal);
                    if (Math.Sqrt(box.Diagonal.X * box.Diagonal.X + box.Diagonal.Y * box.Diagonal.Y) <= tolerance) return;
                    throw new InvalidOperationException("Candidate NURBS projection failed");
                }
                var length = flat.GetLength(); if (length <= tolerance) return;
                var count = Math.Max(1, checked((int)Math.Ceiling(length / (grid.Pixel * .4))));
                var stations = flat.DivideByCount(count, true) ?? throw new InvalidOperationException("Candidate division failed");
                bool Visible(double t) => grid.Visible(Local(curve.PointAt(t)), candidate.Adjacent, tolerance * 2 + meshTolerance * 2, candidate.Profile);
                var start = curve.Domain.Min; var previousT = start; var previous = Visible(start);
                void Emit(double end)
                {
                    if (end <= start) return;
                    var depth = curve.Trim(start, end); if (depth is null) return;
                    var part = depth.DuplicateCurve();
                    if (!part.Transform(Transform.PlanarProjection(frame.Plane)))
                    {
                        var box = depth.GetBoundingBox(worldToLocal); depth.Dispose(); part.Dispose();
                        if (Math.Sqrt(box.Diagonal.X * box.Diagonal.X + box.Diagonal.Y * box.Diagonal.Y) <= tolerance) return;
                        throw new InvalidOperationException("Output NURBS projection failed");
                    }
                    if (part.GetLength() <= tolerance) { depth.Dispose(); part.Dispose(); return; }
                    batch.Add(new(part, candidate.Input.Source, previous ? LineClass.Visible : LineClass.Hidden, depth)
                    { Profile = candidate.Profile, FarCandidate = candidate.Input.Far });
                }
                foreach (var t in stations)
                {
                    var visible = Visible(t);
                    if (visible != previous)
                    {
                        var lo = previousT; var hi = t;
                        for (var refine = 0; refine < 8; refine++)
                        { var mid = (lo + hi) * .5; if (Visible(mid) == previous) lo = mid; else hi = mid; }
                        var boundary = (lo + hi) * .5; Emit(boundary); start = boundary; previous = visible;
                    }
                    previousT = t;
                }
                Emit(curve.Domain.Max);
            });
            foreach (var batch in output) drawing.Pieces.AddRange(batch);
            for (var i = 0; i < inputs.Count; i++)
            {
                if (inputs[i].Geometry is not Point point) continue;
                if (!grid.Visible(Local(point.Location), [], tolerance * 2 + meshTolerance * 2)) { drawing.HiddenPoints++; continue; }
                var p = point.Location; p.Transform(Transform.PlanarProjection(frame.Plane));
                drawing.Pieces.Add(new(new Point(p), inputs[i].Source, LineClass.Visible, new Point(point.Location)));
            }
            var classify = clock.Elapsed.TotalSeconds;
            // Both silhouette alternatives were profiled; charge only the selected one to the engine.
            var seconds = total.Elapsed.TotalSeconds - (meshSilhouettes ? silhouette : meshSilhouette);
            drawing.Seconds = seconds;
            return new(drawing, new(resolution, grid.Width, grid.Height, grid.Pixel, meshTolerance, grid.Bytes,
                triangles.Count, candidates.Count, silhouettes, meshCount, tessellate, raster, silhouette, meshSilhouette, edges, classify, seconds), candidates);
        }
        catch { drawing.Dispose(); foreach (var candidate in candidates) candidate.Curve.Dispose(); throw; }
        finally { foreach (var batch in meshes) if (batch is not null) foreach (var (mesh, _) in batch) mesh.Dispose(); }
    }
}
