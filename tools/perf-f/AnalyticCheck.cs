using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using M2DPlus.Core;
using M2DPlus.PerfF.CurvedCore;
using M2DPlus.Rhino.Services;
using Rhino;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;
using Environment = System.Environment;

namespace M2DPlus.PerfF;

/// <summary>
/// Stage 1 check of the production analytic tier (hillclimb/curved-engine, "Stage 1 in production").
///   av:&lt;view&gt; [glass] [samples:&lt;n&gt;]
/// Solves the view through the product with the analytic tier off (before) and on (after), back to back on the same
/// prepared scene, and reports time, native calls and class lengths. It then compares the two drawings as classed loci
/// and checks sampled points of the after drawing with direct ray/Brep intersections on the original sources: no
/// mesh, no index deciding a ray, no HLD class, none of the engine's code.
/// </summary>
internal static class AnalyticCheck
{
    private sealed record Body(GeometryBase Geometry, Brep? Brep, BoundingBox Box, string Id, bool Exact);

    private static ViewDrawing SolveView(PreparedScene scene, IReadOnlyList<PreparedLeaf> sources, ViewRequest view, NativeSettings settings, double timeout)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(timeout));
        var solve = typeof(NativeDrawing).GetMethod("SolveView", BindingFlags.NonPublic | BindingFlags.Static)!;
        return (ViewDrawing)solve.Invoke(null, new object[] { scene, sources, view, settings, limit.Token })!;
    }

    private static double[] Flat(Curve curve, Plane plane, double tol)
    {
        Polyline? polyline;
        if (!curve.TryGetPolyline(out polyline))
        {
            using var approximation = curve.ToPolyline(tol / 8, .02, 0, 0);
            if (approximation is null || !approximation.TryGetPolyline(out polyline)) polyline = new Polyline(new[] { curve.PointAtStart, curve.PointAtEnd });
        }
        var flat = new double[polyline.Count * 2];
        for (int k = 0; k < polyline.Count; k++) { plane.ClosestParameter(polyline[k], out var u, out var v); flat[2 * k] = u; flat[2 * k + 1] = v; }
        return flat;
    }

    private static string Hash(ViewDrawing drawing, Plane plane, double tol)
    {
        var lines = new List<string>();
        foreach (var item in drawing.Items)
        {
            using var g = item.CopyGeometry();
            var p = g is Curve c ? Flat(c, plane, tol) : g is global::Rhino.Geometry.Point q ? new[] { q.Location.X, q.Location.Y, q.Location.Z } : Array.Empty<double>();
            lines.Add($"{item.Class}|{item.SourceIdentity}|{item.Profile}|{string.Join(",", p.Select(v => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture)))}");
        }
        lines.Sort(StringComparer.Ordinal);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", lines))))[..16];
    }

    internal static void Run(PreparedScene scene, IReadOnlyList<PreparedLeaf> prepared, ViewRequest view, string stem, string output, string[] variants, double timeout)
    {
        var tol = scene.Tolerance; int samples = 300; bool glass = variants.Contains("glass");
        foreach (var v in variants) if (v.StartsWith("samples:", StringComparison.Ordinal)) samples = int.Parse(v[8..]);
        if (glass)
        {
            var context = new ViewSettingsContext("view", view.Name, view.Kind, view.Cut is not null, 10, tol, null);
            var resolved = Settings.Resolve(new[] { context }, new[] { new[] { "glass=clear" } }, DocumentUnits.Metres);
            if (resolved.HasErrors) throw new InvalidOperationException(string.Join(";", resolved.Diagnostics));
            view = view with { Settings = resolved.Views[0] }; stem += "-glass";
        }
        if (variants.Contains("bodies"))
            foreach (var leaf in prepared)
            {
                if (leaf.Geometry is not (Brep or Extrusion or Surface)) { Console.WriteLine($"    BODY {leaf.Identity[^12..]} {leaf.Geometry.GetType().Name}"); continue; }
                var (shape, refusal) = AnalyticVisibility.Examine(leaf.Geometry, tol);
                Console.WriteLine($"    BODY {leaf.Identity[^12..]} {(shape is null ? "REFUSED: " + refusal : shape is AnalyticVisibility.CutTubeShape ct ? $"recognised cut tube: {ct.Surfaces.Count} surfaces ({ct.Real} faces' surfaces), face bound {ct.FaceBound:G3}, edge bound {ct.EdgeBound:G3}, {ct.Edges.Count} edges; {ct.Note}" : "recognised " + shape)}");
                if (leaf.Geometry is not Brep brep) continue;
                foreach (var sil in Silhouette.Compute(brep, SilhouetteType.Tangent, view.Direction, tol, .001) ?? [])
                    Console.WriteLine($"        SILHOUETTE type {sil?.SilhouetteType} component {sil?.GeometryComponentIndex.ComponentIndexType} {sil?.GeometryComponentIndex.Index} curve {sil?.Curve?.GetType().Name} length {sil?.Curve?.GetLength():F3}");
                Console.WriteLine($"        faces {brep.Faces.Count}, edges {brep.Edges.Count}, orientation {brep.SolidOrientation}, solid {brep.IsSolid}, valid {brep.IsValid}, volume {brep.GetVolume():R}");
                foreach (var face in brep.Faces)
                {
                    string kind = face.TryGetPlane(out var pl, tol) ? $"plane n=({pl.Normal.X:F8}, {pl.Normal.Y:F8}, {pl.Normal.Z:F8}) at {pl.Origin}"
                        : face.TryGetCylinder(out var cy, tol) ? $"cylinder r={cy.Radius:R} axis=({cy.Axis.X:F9}, {cy.Axis.Y:F9}, {cy.Axis.Z:F9}) centre {cy.Center} h {cy.Height1:F3}..{cy.Height2:F3}"
                        : face.TryGetSphere(out var sp, tol) ? $"sphere r={sp.Radius:R}" : face.TryGetCone(out var co, tol) ? "cone" : face.TryGetTorus(out var to, tol) ? "torus"
                        : face.UnderlyingSurface().GetType().Name + string.Concat(new[] { 10.0, 100, 1000, 10000 }.Select(k => face.TryGetCylinder(out var loose, k * tol) ? $" [cylinder r={loose.Radius:F4} at {k} tol]" : face.TryGetPlane(out _, k * tol) ? $" [plane at {k} tol]" : "").Take(4).Where(t => t.Length > 0).Take(1));
                    Console.WriteLine($"        face {face.FaceIndex}: {kind}; loops {face.Loops.Count}; edges {face.AdjacentEdges().Length}; area {AreaMassProperties.Compute(face)?.Area:F3}");
                }
            }
        if (variants.Contains("fit"))
        {   // Stage 2 probe: every face against every exact cylinder and plane found anywhere in the scene.
            var cylinders = new List<(Line Axis, double R, string From)>(); var planes = new List<(Plane P, string From)>();
            foreach (var leaf in prepared)
                if (leaf.Geometry is Brep b)
                    foreach (var face in b.Faces)
                    {
                        if (face.TryGetPlane(out var pl, tol)) { if (!planes.Any(k => Math.Abs(k.P.DistanceTo(pl.Origin)) < tol && k.P.Normal.IsParallelTo(pl.Normal, 1e-9) != 0)) planes.Add((pl, $"{leaf.Identity[^12..]}/{face.FaceIndex}")); }
                        else if (face.TryGetCylinder(out var cy, tol))
                        {
                            var u = cy.Axis; u.Unitize(); var axis = new Line(cy.Center, cy.Center + u);
                            if (!cylinders.Any(k => Math.Abs(k.R - cy.Radius) < tol && k.Axis.Direction.IsParallelTo(u, 1e-9) != 0 && k.Axis.DistanceTo(cy.Center, false) < tol)) cylinders.Add((axis, cy.Radius, $"{leaf.Identity[^12..]}/{face.FaceIndex}"));
                        }
                    }
            // Each tube's other radius on the same axis too (a bore that is only a NURBS face).
            foreach (var c in cylinders.ToArray())
                foreach (double r in cylinders.Select(k => k.R).Distinct().ToArray())
                    if (!cylinders.Any(k => Math.Abs(k.R - r) < tol && k.Axis.Direction.IsParallelTo(c.Axis.Direction, 1e-9) != 0 && k.Axis.DistanceTo(c.Axis.From, false) < tol)) cylinders.Add((c.Axis, r, c.From + "*"));
            Console.WriteLine($"    FIT pool: {cylinders.Count} cylinders, {planes.Count} planes");
            foreach (var leaf in prepared)
            {
                if (leaf.Geometry is not Brep brep) continue;
                Console.WriteLine($"    FIT BODY {leaf.Identity[^12..]} faces {brep.Faces.Count} max edge tolerance {brep.Edges.Max(e => e.Tolerance):R} max vertex tolerance {brep.Vertices.Max(v => v.Tolerance):R}");
                var match = new string[brep.Faces.Count];
                foreach (var face in brep.Faces)
                {
                    var points = new List<Point3d>(); var du = face.Domain(0); var dv = face.Domain(1);
                    for (int i = 0; i <= 40; i++)
                        for (int j = 0; j <= 40; j++)
                        {
                            double u = du.ParameterAt(i / 40.0), v = dv.ParameterAt(j / 40.0);
                            if (face.IsPointOnFace(u, v) != PointFaceRelation.Exterior) points.Add(face.PointAt(u, v));
                        }
                    foreach (var loop in face.Loops) foreach (var trim in loop.Trims) if (trim.Edge is { } trimEdge) for (int k = 0; k <= 16; k++) points.Add(trimEdge.PointAt(trimEdge.Domain.ParameterAt(k / 16.0)));
                    var best = cylinders.Select(c => (Name: $"cylinder r={c.R:F4} of {c.From}", Dev: points.Max(q => Math.Abs(c.Axis.DistanceTo(q, false) - c.R))))
                        .Concat(planes.Select(p => (Name: $"plane of {p.From}", Dev: points.Max(q => Math.Abs(p.P.DistanceTo(q))))))
                        .OrderBy(x => x.Dev).First();
                    var nurbs = face.UnderlyingSurface() as NurbsSurface;
                    bool isExact = face.TryGetPlane(out _, tol) || face.TryGetCylinder(out _, tol);
                    match[face.FaceIndex] = best.Name;
                    if (!isExact && best.Dev > 1e-2 && nurbs is not null)
                    {   // Rulings of a degree-1 direction, in the frame of the body's own tube: where do the two ends of
                        // each ruling sit (radius, height, angle), and which pool surfaces do the two boundary curves lie on?
                        var own = cylinders.Where(k => k.From.StartsWith(leaf.Identity[^12..], StringComparison.Ordinal) && !k.From.EndsWith('*')).OrderByDescending(k => k.R).First();
                        var u = own.Axis.Direction; u.Unitize(); var frame = new Plane(own.Axis.From, u);
                        int ruled = nurbs.Degree(0) == 1 ? 0 : nurbs.Degree(1) == 1 ? 1 : -1; if (ruled < 0) { Console.WriteLine("            not ruled"); continue; }
                        var dr = nurbs.Domain(ruled); var dc = nurbs.Domain(1 - ruled); double worstH = 0, worstA = 0, worstStraight = 0; var ends = new[] { new List<Point3d>(), new List<Point3d>() };
                        for (int k = 0; k <= 64; k++)
                        {
                            double c = dc.ParameterAt(k / 64.0);
                            Point3d At(double r) => ruled == 0 ? nurbs.PointAt(r, c) : nurbs.PointAt(c, r);
                            var a = At(dr.Min); var b = At(dr.Max); var mid = At(dr.Mid); ends[0].Add(a); ends[1].Add(b);
                            (double H, double Angle, double Rho) Polar(Point3d q) { var d = q - own.Axis.From; double x = d * frame.XAxis, y = d * frame.YAxis; return (d * u, Math.Atan2(y, x), Math.Sqrt(x * x + y * y)); }
                            var pa = Polar(a); var pb = Polar(b); double da = Math.Abs(pa.Angle - pb.Angle); if (da > Math.PI) da = 2 * Math.PI - da;
                            worstH = Math.Max(worstH, Math.Abs(pa.H - pb.H)); worstA = Math.Max(worstA, da * own.R); worstStraight = Math.Max(worstStraight, new Line(a, b).DistanceTo(mid, false));
                        }
                        for (int row = 0; row < 2; row++)
                        {   // Each end of the ruled surface as a curve: circle, ellipse, or neither.
                            using var iso = nurbs.IsoCurve(1 - ruled, row == 0 ? dr.Min : dr.Max);
                            string what = iso.TryGetCircle(out var circle, 1e-6) ? $"circle r={circle.Radius:F6} centre {circle.Center} normal ({circle.Normal.X:F7}, {circle.Normal.Y:F7}, {circle.Normal.Z:F7})"
                                : iso.TryGetEllipse(out var ellipse, 1e-6) ? $"ellipse {ellipse.Radius1:F6} x {ellipse.Radius2:F6} centre {ellipse.Plane.Origin} normal ({ellipse.Plane.Normal.X:F7}, {ellipse.Plane.Normal.Y:F7}, {ellipse.Plane.Normal.Z:F7})"
                                : iso.TryGetArc(out var arcIso, 1e-6) ? $"arc r={arcIso.Radius:F6} angle {arcIso.Angle:F4} centre {arcIso.Center}" : "neither circle nor ellipse; planar " + iso.IsPlanar(1e-6);
                            Console.WriteLine($"            end {row}: {what}; closed {iso.IsClosed}");
                        }
                        Console.WriteLine($"            ruling direction at start ({(nurbs.PointAt(ruled == 0 ? dr.Max : dc.Min, ruled == 0 ? dc.Min : dr.Max) - nurbs.PointAt(ruled == 0 ? dr.Min : dc.Min, ruled == 0 ? dc.Min : dr.Min))}); own axis ({u.X:F7}, {u.Y:F7}, {u.Z:F7})");
                        Console.WriteLine($"            rulings in own tube frame: height change across a ruling up to {worstH:E3}, sideways shift up to {worstA:E3}, straightness {worstStraight:E3}");
                        for (int side = 0; side < 2; side++)
                        {
                            var fits = cylinders.Select(c => (Name: $"cylinder r={c.R:F4} of {c.From}", Dev: ends[side].Max(q => Math.Abs(c.Axis.DistanceTo(q, false) - c.R))))
                                .Concat(planes.Select(p => (Name: $"plane of {p.From}", Dev: ends[side].Max(q => Math.Abs(p.P.DistanceTo(q))))))
                                .OrderBy(x => x.Dev).Take(3).ToArray();
                            Console.WriteLine($"            boundary {side}: " + string.Join("; ", fits.Select(f => $"{f.Name} dev {f.Dev:E2}")));
                        }
                    }
                }
            }
            return;
        }
        bool mixed = variants.Contains("mixed");     // the mixed route, off by default
        var off = new NativeSettings { Analytic = false }; var on = new NativeSettings { AnalyticMixed = mixed };
        // `why`: what HLD returned in the composed solve, to show beside a flagged sample.
        var heard = new List<(string Id, int Edge, bool Duplicate, LineClass Kind, Curve World)>();
        var listening = !variants.Contains("why") ? on : new NativeSettings { AnalyticMixed = mixed, ObserveCompositionRecord = (id, edge, duplicate, kind, world) => { lock (heard) heard.Add((id, edge, duplicate, kind, world.DuplicateCurve())); } };
        using (var cold = SolveView(scene, prepared, view, off, timeout)) { }
        (ViewDrawing Drawing, double Wall, string Busy) Timed(NativeSettings settings)
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            var busy = Spike.Busy.Start(); var timer = Stopwatch.StartNew(); var d = SolveView(scene, prepared, view, settings, timeout);
            return (d, timer.Elapsed.TotalSeconds, busy.Stop());
        }
        var (before, beforeWall, beforeBusy) = Timed(off);
        using var after1 = SolveView(scene, prepared, view, listening, timeout);   // first analytic solve pays recognition once per scene
        var (after, afterWall, afterBusy) = Timed(on);
        using var keepBefore = before; using var keepAfter = after;
        var plane = after.Frame.Plane; var e = plane.ZAxis; var toView = Transform.PlaneToPlane(plane, Plane.WorldXY);
        bool cut = after.Regions.Count != 0 || after.Items.Any(p => p.Class is LineClass.Cut or LineClass.Beyond or LineClass.FarClip) || view.Cut is not null;

        Dictionary<string, double> Lengths(ViewDrawing d) => d.Items.GroupBy(p => p.Class.ToString()).ToDictionary(g => g.Key,
            g => g.Sum(p => { using var c = p.CopyGeometry(); return c is Curve k ? k.GetLength() : 0; }));
        string Show(Dictionary<string, double> l) => string.Join(", ", l.OrderBy(k => k.Key).Select(k => $"{k.Key} {k.Value:F1}"));
        var beforeLengths = Lengths(before); var afterLengths = Lengths(after);
        string Analytic(ViewDrawing d) => d.Diagnostics.FirstOrDefault(x => x.StartsWith("Analytic solids", StringComparison.Ordinal)) ?? "Analytic solids: none recognised or tier off.";
        Console.WriteLine($"--- {stem} [before, analytic off] wall {beforeWall:F3}s native calls {before.NativeCalls} native solve {before.Phases.Seconds.GetValueOrDefault("native solve"):F3}s; {Show(beforeLengths)}; error {before.Error}; busy {beforeBusy}");
        Console.WriteLine($"    route: {before.Report.Route}");
        Console.WriteLine($"--- {stem} [after, analytic on] wall {afterWall:F3}s (first solve {after1.Seconds:F3}s) native calls {after.NativeCalls} native solve {after.Phases.Seconds.GetValueOrDefault("native solve"):F3}s; {Show(afterLengths)}; error {after.Error}; busy {afterBusy}");
        Console.WriteLine($"    route: {after.Report.Route}");
        Console.WriteLine($"    {Analytic(after)}");
        foreach (var (label, d) in new[] { ("before", before), ("after", after) })
        {
            foreach (var x in d.Diagnostics.Where(x => x.StartsWith("Native ray verifier", StringComparison.Ordinal) || x.StartsWith("Composition coverage", StringComparison.Ordinal)))
                Console.WriteLine($"    {label}: {x[..Math.Min(x.Length, 330)]}");
            Console.WriteLine($"    {label}: Unset notes {d.Diagnostics.Count(x => x.Contains("Unset", StringComparison.Ordinal))}; quarantined sources {d.UncertainRoutingSources.Count}; routing invalid {d.RoutingInvalid}; duplicates {d.DuplicateSegments}");
        }
        Console.WriteLine($"    phases after: {string.Join(", ", after.Phases.Seconds.Where(p => p.Value >= .005).Select(p => $"{p.Key} {p.Value:F2}"))}");
        string hashBefore = Hash(before, plane, tol), hashAfter = Hash(after, plane, tol), hashAfter1 = Hash(after1, plane, tol);
        Console.WriteLine($"    drawing hash before {hashBefore} after {hashAfter} ({(hashBefore == hashAfter ? "identical" : "different")}); repeat of after {(hashAfter == hashAfter1 ? "identical" : "DIFFERENT")}");

        if (variants.Contains("why"))
            foreach (var item in after.Items.Where(p => p.Class == LineClass.Unresolved).Select(p => { using var g = p.CopyGeometry(); var c = g as Curve; return (p, Length: c?.GetLength() ?? 0, Mid: c?.PointAt(c.Domain.Mid) ?? Point3d.Unset, Linear: c?.IsLinear(tol) ?? false); }).OrderByDescending(x => x.Length).Take(10))
            {
                plane.ClosestParameter(item.Mid, out var ux, out var uy); var depth = item.p.DepthGeometry as Curve; var w = depth?.PointAt(depth.Domain.Mid) ?? Point3d.Unset;
                var leaf = prepared.First(l => l.Identity == item.p.SourceIdentity);
                Console.WriteLine($"    UNRESOLVED {item.Length * 1000:F4} mm {(item.Linear ? "line" : "curve")} at ({ux:F3}, {uy:F3}) world {w} source {item.p.SourceIdentity[^12..]} {AnalyticVisibility.Recognise(leaf.Geometry, tol)?.GetType().Name}");
                // The engine's answer per solid at the exact point behind this piece: the point is put on the two own
                // surfaces it is nearest to, and every surface of any solid it then lies on (within 1e-7) is taken as known.
                if (AnalyticVisibility.Recognise(leaf.Geometry, tol) is AnalyticVisibility.CutTubeShape ownTube)
                {
                    var two = Enumerable.Range(0, ownTube.Real).OrderBy(i => Math.Abs(CutTubeRecognition.Distance(ownTube.Surfaces[i], w))).Take(2).ToArray();
                    var exactPoint = CutTubeRecognition.Onto(ownTube.Surfaces[two[0]], ownTube.Surfaces[two[1]], w) ?? w; var ve = AnalyticVisibility.Project(plane, exactPoint);
                    foreach (var other in prepared)
                    {
                        if (AnalyticVisibility.Recognise(other.Geometry, tol) is not { } otherShape) continue;
                        var solid = AnalyticVisibility.Solid(otherShape, plane); int bits = 0; string lies = "";
                        if (otherShape is AnalyticVisibility.CutTubeShape ct2)
                            for (int i = 0; i < ct2.Real; i++) if (Math.Abs(CutTubeRecognition.Distance(ct2.Surfaces[i], exactPoint)) < 1e-7) { bits |= 1 << i; lies += $" {i}"; }
                        if (otherShape is AnalyticVisibility.CylinderShape pc)
                        {
                            var d = exactPoint - pc.Origin; d -= pc.Axis * (d * pc.Axis);
                            if (Math.Abs(d.Length - pc.Radius) < 1e-7) { bits |= M2DPlus.Core.Visibility.AnalyticSolid.Outer; lies += " outer"; }
                            if (Math.Abs(d.Length - pc.InnerRadius) < 1e-7) { bits |= M2DPlus.Core.Visibility.AnalyticSolid.Inner; lies += " bore"; }
                        }
                        var answer = solid.Occlusion(ve, tol, bits);
                        if (answer != M2DPlus.Core.Visibility.VisibilityClass.Visible || other.Identity == item.p.SourceIdentity)
                            Console.WriteLine($"        exact point (moved {exactPoint.DistanceTo(w):E2}): {answer} by {other.Identity[^12..]}{(other.Identity == item.p.SourceIdentity ? " (own)" : "")} on its surfaces [{lies.Trim()}]; with no surface known {solid.Occlusion(ve, tol)}");
                    }
                }
                foreach (var other in prepared)
                    if (AnalyticVisibility.Recognise(other.Geometry, tol) is AnalyticVisibility.CutTubeShape nearTube)
                    {
                        var close = Enumerable.Range(0, nearTube.Surfaces.Count).Where(i => Math.Abs(CutTubeRecognition.Distance(nearTube.Surfaces[i], w)) < .2).ToArray();
                        if (close.Length != 0) Console.WriteLine($"        within 0.2 of {other.Identity[^12..]}{(other.Identity == item.p.SourceIdentity ? " (own)" : "")}: " + string.Join(", ", close.Select(i => $"{i}:{(nearTube.Surfaces[i].Cylinder ? $"cyl r={nearTube.Surfaces[i].Radius:F2}" : "plane")} {nearTube.Surfaces[i].Role}{(nearTube.Surfaces[i].Virtual ? "*" : "")} at {CutTubeRecognition.Distance(nearTube.Surfaces[i], w):E2}")));
                    }
                    else if (AnalyticVisibility.Recognise(other.Geometry, tol) is AnalyticVisibility.CylinderShape plain)
                    {
                        var d = w - plain.Origin; d -= plain.Axis * (d * plain.Axis);
                        if (Math.Abs(d.Length - plain.Radius) < .2 || Math.Abs(d.Length - plain.InnerRadius) < .2) Console.WriteLine($"        within 0.2 of {other.Identity[^12..]} (plain tube): outer at {d.Length - plain.Radius:E2}, bore at {d.Length - plain.InnerRadius:E2}, cap planes at {plain.Cap0.DistanceTo(w):E2} and {plain.Cap1.DistanceTo(w):E2}");
                    }
                foreach (var other in prepared)
                    if (AnalyticVisibility.Recognise(other.Geometry, tol) is { } shape && AnalyticVisibility.Solid(shape, plane).Occlusion(AnalyticVisibility.Project(plane, w), tol) is var k && k != M2DPlus.Core.Visibility.VisibilityClass.Visible)
                    {
                        Console.WriteLine($"        {k} by {other.Identity[^12..]}{(other.Identity == item.p.SourceIdentity ? " (own)" : "")} {shape.GetType().Name}");
                        if (shape is AnalyticVisibility.CutTubeShape cutTube)
                        {   // Along the ray: which cells of the cut tube it passes through, and whether each is known.
                            var csg = (M2DPlus.Core.Visibility.AnalyticCsg)AnalyticVisibility.Solid(cutTube, plane); var v0 = AnalyticVisibility.Project(plane, w);
                            var seen = new List<string>(); string last = "";
                            double reach = cutTube.Bounds.Diagonal.Length;
                            for (int march = 1; march <= 4000; march++)
                            {
                                double h = reach * march / 4000; var x = new M2DPlus.Core.Visibility.Vertex(v0.X, v0.Y, v0.Z + h);
                                int cell = csg.Cell(x, 0, out _, out bool inTube); bool? state = csg.Contains(x, 0);
                                string label = !inTube ? "outside tube" : $"cell {Convert.ToString(cell, 2)} {(state == true ? "solid" : state == false ? "void" : "UNKNOWN")}";
                                if (label != last) { seen.Add($"{h:F3}: {label}"); last = label; }
                            }
                            Console.WriteLine("            along the ray: " + string.Join(" | ", seen.Take(14)));
                            int near = Enumerable.Range(0, cutTube.Surfaces.Count).OrderBy(i => Math.Abs(CutTubeRecognition.Distance(cutTube.Surfaces[i], w))).First();
                            Console.WriteLine("            surfaces within 1 of the point: " + string.Join(", ", Enumerable.Range(0, cutTube.Surfaces.Count).Where(i => Math.Abs(CutTubeRecognition.Distance(cutTube.Surfaces[i], w)) < 1).Select(i => $"{i}:{(cutTube.Surfaces[i].Cylinder ? $"cyl r={cutTube.Surfaces[i].Radius:F2}" : "plane")} {cutTube.Surfaces[i].Role}{(cutTube.Surfaces[i].Virtual ? "*" : "")} slack {cutTube.Surfaces[i].Slack:G3} at {CutTubeRecognition.Distance(cutTube.Surfaces[i], w):E2}")));
                        }
                    }
            }
        // ---- Classed loci, both ways.
        static int Class(LineClass c) => c == LineClass.Hidden ? 1 : c == LineClass.Unresolved ? 2 : c is LineClass.Visible or LineClass.Profile or LineClass.Beyond ? 0 : 3;
        static int Drawn(int mask) => (mask & 4) != 0 ? 2 : (mask & 1) != 0 ? 0 : (mask & 2) != 0 ? 1 : (mask & 8) != 0 ? 3 : 4;
        (Locus Locus, List<(int Class, double[] P, DrawingPiece Piece)> Pieces) Index(ViewDrawing d)
        {
            var locus = new Locus(.05); var list = new List<(int, double[], DrawingPiece)>();
            foreach (var item in d.Items)
            {
                using var g = item.CopyGeometry(); if (g is not Curve c) continue;
                var flat = Flat(c, plane, tol); int cls = Class(item.Class);
                for (int k = 2; k < flat.Length; k += 2) locus.Add(flat[k - 2], flat[k - 1], flat[k], flat[k + 1], (byte)cls);
                list.Add((cls, flat, item));
            }
            return (locus, list);
        }
        var (beforeLocus, beforePieces) = Index(before); var (afterLocus, afterPieces) = Index(after);
        double strict = 10 * tol, step = .02;
        double[,] Matrix(List<(int Class, double[] P, DrawingPiece Piece)> from, Locus own, Locus other)
        {
            var m = new double[4, 5];
            foreach (var (cls, flat, _) in from)
                for (int k = 2; k < flat.Length; k += 2)
                {
                    double ax = flat[k - 2], ay = flat[k - 1], bx = flat[k], by = flat[k + 1], l = Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay));
                    int n = Math.Max(1, (int)Math.Ceiling(l / step));
                    for (int j = 0; j < n; j++)
                    {
                        double x = ax + (bx - ax) * (j + .5) / n, y = ay + (by - ay) * (j + .5) / n;
                        if (Drawn(own.Classes(x, y, tol)) != cls) continue;   // a coincident line of a higher class is what is drawn
                        m[cls, Drawn(other.Classes(x, y, strict))] += l / n;
                    }
                }
            return m;
        }
        var forward = Matrix(afterPieces, afterLocus, beforeLocus); var backward = Matrix(beforePieces, beforeLocus, afterLocus);
        string Row(double[,] m, int r) => $"{m[r, 0]:F2} / {m[r, 1]:F2} / {m[r, 2]:F2} / {m[r, 3]:F2} / {m[r, 4]:F2}";
        Console.WriteLine($"    after drawn -> before within {strict * 1000:G3} mm (Visible / Hidden / Unresolved / cut etc. / no line): V {Row(forward, 0)} | H {Row(forward, 1)} | U {Row(forward, 2)}");
        Console.WriteLine($"    before drawn -> after within {strict * 1000:G3} mm (Visible / Hidden / Unresolved / cut etc. / no line): V {Row(backward, 0)} | H {Row(backward, 1)} | U {Row(backward, 2)}");

        if (variants.Contains("why"))
        {
            int shown = 0;
            foreach (var (cls, flat, piece) in beforePieces)
            {
                if (shown >= 12) break;
                int mid = -1;
                for (int k = 2; k < flat.Length && mid < 0; k += 2)
                    if (Drawn(afterLocus.Classes((flat[k - 2] + flat[k]) / 2, (flat[k - 1] + flat[k + 1]) / 2, strict)) == 4 && Drawn(beforeLocus.Classes((flat[k - 2] + flat[k]) / 2, (flat[k - 1] + flat[k + 1]) / 2, tol)) == cls) mid = k;
                if (mid < 0) continue;
                var depth = piece.DepthGeometry as Curve; using var g = piece.CopyGeometry();
                var leaf = prepared.First(l => l.Identity == piece.SourceIdentity);
                Console.WriteLine($"    MISSING before {piece.Class} {(g as Curve)?.GetLength():F4} m {(g as Curve)?.GetType().Name} linear {(g as Curve)?.IsLinear(tol)} at ({flat[mid]:F4}, {flat[mid + 1]:F4}) world {depth?.PointAt(depth.Domain.Mid)} ends {depth?.PointAtStart} .. {depth?.PointAtEnd} source {piece.SourceIdentity[^12..]} {AnalyticVisibility.Recognise(leaf.Geometry, tol)}");
                shown++;
            }
        }
        // ---- Independent ray check on the original sources.
        var exact = new HashSet<string>(StringComparer.Ordinal); var bodies = new List<Body>(); var owned = new List<Brep>();
        bool clear = view.Settings?.Glass == GlassMode.Clear;
        foreach (var leaf in prepared)
        {
            bool recognised = AnalyticVisibility.Recognise(leaf.Geometry, tol) is not null; if (recognised) exact.Add(leaf.Identity);
            if (clear && leaf.Section.Transparent) continue;                       // clear glass covers nothing
            Brep? brep = leaf.Geometry switch { Brep b => b, Extrusion x => x.ToBrep(), Surface s => s.ToBrep(), SubD s => s.ToBrep(), _ => null };
            if (brep is not null && !ReferenceEquals(brep, leaf.Geometry)) owned.Add(brep);
            if (brep is null && leaf.Geometry is not Mesh) continue;
            bodies.Add(new(leaf.Geometry, brep, leaf.Geometry.GetBoundingBox(toView), leaf.Identity, recognised));
        }
        using var tree = new RTree();
        for (int i = 0; i < bodies.Count; i++) { var b = bodies[i].Box; tree.Insert(new BoundingBox(new Point3d(b.Min.X, b.Min.Y, 0), new Point3d(b.Max.X, b.Max.Y, 0)), i); }
        double top = bodies.Count == 0 ? 0 : bodies.Max(b => b.Box.Max.Z); long rays = 0;
        // Does anything lie more than `from` in front of the point? null = cannot say (a failed or grazing intersection).
        bool? Blocked(Point3d origin, double from, double ambiguous, string? only = null, string? except = null, double precision = 0)
        {
            var local = origin; local.Transform(toView); bool unsure = false; if (precision <= 0) precision = tol / 10;
            // In a section everything in front of the cut plane has been removed: the ray ends there.
            double end = cut ? Math.Min(top + 1, 0) : top + 1; if (end - local.Z <= from) return false;
            var near = new List<int>();
            tree.Search(new BoundingBox(new Point3d(local.X - 8 * tol, local.Y - 8 * tol, -1), new Point3d(local.X + 8 * tol, local.Y + 8 * tol, 1)), (_, a) => near.Add(a.Id));
            using var ray = new LineCurve(origin + e * from, origin + e * (end - local.Z));
            foreach (int bi in near)
            {
                var body = bodies[bi];
                if (only is not null && body.Id != only || body.Id == except || body.Box.Max.Z < local.Z + from) continue;
                rays++;
                if (body.Brep is null)
                {
                    double d = Intersection.MeshRay((Mesh)body.Geometry, new Ray3d(origin + e * from, e));
                    if (double.IsNaN(d) || double.IsInfinity(d)) { unsure = true; continue; }
                    if (d >= 0 && d + from <= end - local.Z) { if (d + from > ambiguous) return true; unsure = true; }
                    continue;
                }
                if (!Intersection.CurveBrep(ray, body.Brep, precision, out var overlaps, out var hits)) { unsure = true; continue; }
                double gap = double.NegativeInfinity; bool along = false;
                foreach (var h in hits) gap = Math.Max(gap, (h - origin) * e);
                // The ray running along a surface of this body is contact, a matter of the tie convention: this body is not judged.
                foreach (var o in overlaps) { double s0 = (o.PointAtStart - origin) * e, s1 = (o.PointAtEnd - origin) * e; o.Dispose(); if (Math.Abs(s1 - s0) > 4 * tol) along = true; else gap = Math.Max(gap, Math.Max(s0, s1)); }
                if (along) { unsure = true; continue; }
                if (gap > ambiguous) return true;
                if (gap > from) unsure = true;
                // In a section a solid that crosses the cut plane is capped there: a ray still inside it at the plane is covered.
                if (cut && body.Brep.IsSolid && body.Box.Max.Z > 0 && body.Box.Min.Z < 0)
                {
                    var atPlane = origin + e * (-local.Z - 4 * tol);
                    if (-local.Z - 4 * tol > ambiguous && body.Brep.IsPointInside(atPlane, tol / 10, true)) return true;
                }
            }
            return unsure ? null : false;
        }
        // 0 visible, 1 hidden, -1 not judged.
        int Verdict(Point3d p, Vector3d normal, string own, bool exactOwn)
        {
            // Other bodies. The ray itself decides that nothing is in front. Hidden needs the ray and one on each side of
            // the line blocked: with one side clear, a body's outline runs along this line (aligned members), a tie.
            var middle = Blocked(p, tol, 20 * tol, except: own);
            if (middle is null) return -1;
            if (middle.Value)
            {
                var left = Blocked(p + normal * (4 * tol), tol, 20 * tol, except: own); var right = Blocked(p - normal * (4 * tol), tol, 20 * tol, except: own);
                return left == true && right == true ? 1 : -1;
            }
            // Its own body: the ray itself; if that is blocked or grazing, step sideways both ways (a line on an outline
            // grazes its own surface). Visible when either side is clear, hidden when both are blocked; two steps agree.
            var centre = Blocked(p, tol, 20 * tol, only: own);
            // An exact solid's own material in front of the point, however thin sideways: a line on a bore (its outline,
            // an end cut's inner rim) has the wall just beside the ray, a few tolerances thick where the ray is about to
            // leave through an end cut, and a ray cast misses it. Rhino's point-in-solid test along the ray does not:
            // two consecutive steps strictly inside the body are cover beyond a tolerance, which is Hidden by the depth rule.
            // The body is only known to its stated bound (a cut tube's surfaces, with their stored tolerances), so the
            // material must be inside by more than that to count; material thinner than the bound is not judged
            // either way, as a ray that skims a surface a few degrees off it is in or out by less than the bound.
            bool thin = false;
            if (exactOwn && bodies.FirstOrDefault(b => b.Id == own) is { Brep: { IsSolid: true } material } mine)
            {
                // (For a plain shape the bound is what Rhino itself stores on the body's edges.)
                double bound = tol + Math.Max(material.Edges.Max(edge => edge.Tolerance), AnalyticVisibility.Recognise(mine.Geometry, tol) is AnalyticVisibility.CutTubeShape loose ? loose.SurfaceBound : 0);
                bool last = false;
                for (double h = 2 * tol; h < mine.Box.Diagonal.Length; h *= 1.15)
                {
                    bool inside = material.IsPointInside(p + e * h, bound, true);
                    if (inside && last) return 1;
                    last = inside;
                    if (!inside && !thin && h < 64 * bound + 64) thin = material.IsPointInside(p + e * h, tol / 10, true);
                }
            }
            // Material in front that is thinner than the body's bound: the real body may or may not cover the point.
            if (thin) return -1;
            if (centre == false) return 0;
            // The ray grazes its own body. An outline of a hole (a bore) has the hole on one side, so "one side clear"
            // says nothing there: first ask Rhino whether the ray itself runs through the body's material. A point
            // strictly inside by a tenth of a tolerance has more than the depth rule's cover around it.
            if (centre is null && bodies.FirstOrDefault(b => b.Id == own) is { Brep: { IsSolid: true } solid } body)
            {
                // No material found in front, but there is some on the same line behind the point: the line touches a
                // hole from inside the material, and whatever is in front is thinner sideways than this test resolves
                // (the ray leaves through an end cut within a fraction of a millimetre). Not judged.
                for (double h = 2 * tol; h < body.Box.Diagonal.Length; h *= 1.5)
                    if (solid.IsPointInside(p - e * h, tol / 10, true)) return -1;
            }
            int? verdict = null;
            foreach (double delta in exactOwn ? new[] { .2 * tol, .4 * tol } : new[] { 4 * tol, 8 * tol })
            {
                var a = Blocked(p + normal * delta, tol, tol, only: own, precision: exactOwn ? 1e-7 : 0); var b = Blocked(p - normal * delta, tol, tol, only: own, precision: exactOwn ? 1e-7 : 0);
                if (a is null || b is null) return -1;
                int v = a.Value && b.Value ? 1 : 0;
                if (verdict is { } old && old != v) return -1; verdict = v;
            }
            return verdict ?? -1;
        }
        // A cut tube's lines are its stored edges, which can be off the faces they join by the edge tolerance (0.03 mm on
        // the owner's pipes at a model tolerance of 0.001 mm). Where a rim meets its outline that offset decides whether
        // the ray grazes inside or outside the body, so the sample is judged at the faces' own intersection: the nearest
        // edge between two faces is found and the point is projected onto the two faces' surfaces in turn (Rhino's
        // closest point, nothing of the engine's). A point not near such an edge is left where it is.
        int moved = 0;
        Point3d OnFaces(Point3d world, string source)
        {
            var body = bodies.FirstOrDefault(b => b.Id == source); if (body is null || body.Brep is null || AnalyticVisibility.Recognise(body.Geometry, tol) is not AnalyticVisibility.CutTubeShape shape) return world;
            double reach = 4 * shape.EdgeBound + 4 * tol, best = reach; BrepEdge? nearest = null;
            foreach (var edge in body.Brep.Edges)
            {
                var faces = edge.AdjacentFaces(); if (faces.Length != 2 || shape.FaceSurface[faces[0]] == shape.FaceSurface[faces[1]]) continue;
                if (edge.ClosestPoint(world, out double t, best) && edge.PointAt(t).DistanceTo(world) < best) { best = edge.PointAt(t).DistanceTo(world); nearest = edge; }
            }
            if (nearest is null) return world;
            var pair = nearest.AdjacentFaces().Select(i => body.Brep.Faces[i]).ToArray(); var p = world;
            for (int round = 0; round < 40; round++)
                foreach (var face in pair) if (face.ClosestPoint(p, out double u, out double v)) p = face.PointAt(u, v);
            if (p.DistanceTo(world) > reach) return world;
            moved++; return p;
        }
        Console.WriteLine($"    after Unresolved by source kind: exact {after.Items.Where(p => p.Class == LineClass.Unresolved && exact.Contains(p.SourceIdentity)).Sum(p => { using var c = p.CopyGeometry(); return c is Curve k ? k.GetLength() : 0; }):F1}, other {after.Items.Where(p => p.Class == LineClass.Unresolved && !exact.Contains(p.SourceIdentity)).Sum(p => { using var c = p.CopyGeometry(); return c is Curve k ? k.GetLength() : 0; }):F1}");
        var random = new Random(20261116); var groups = new SortedDictionary<string, List<(DrawingPiece Piece, double T, int Class)>>(StringComparer.Ordinal);
        foreach (var (cls, flat, piece) in afterPieces)
        {
            if (cls > 1 || piece.DepthGeometry is not Curve || piece.Class is LineClass.Beyond) continue;
            using var g = piece.CopyGeometry(); var c = (Curve)g; double length = c.GetLength(); if (length < .005) continue;
            int n = Math.Max(1, (int)(length / .05));
            for (int k = 0; k < n; k++)
            {
                double t = c.Domain.ParameterAt((k + .5) / n); var q = c.PointAt(t); plane.ClosestParameter(q, out var x, out var y);
                if (Drawn(afterLocus.Classes(x, y, tol)) != cls) continue;
                int was = Drawn(beforeLocus.Classes(x, y, strict));
                string key = (exact.Contains(piece.SourceIdentity) ? "exact source" : "other source") + ", " + (was == cls ? "same class as before" : was == 2 ? "before Unresolved" : was == 4 ? "no line before" :
                    cls == 1 ? "now Hidden, before Visible" : "now Visible, before Hidden");
                if (!groups.TryGetValue(key, out var list)) groups.Add(key, list = new());
                list.Add((piece, t, cls));
            }
        }
        // `only:<text>` lists up to 40 flagged samples of the groups whose name contains the text.
        string? only = variants.FirstOrDefault(v => v.StartsWith("only:", StringComparison.Ordinal))?[5..];
        var adjudication = new SortedDictionary<string, int[]>(StringComparer.Ordinal); var flagged = new List<string>(); int flaggedTotal = 0;
        foreach (var (key, list) in groups)
        {
            var chosen = list.OrderBy(_ => random.Next()).Take(samples).ToArray(); var counts = new int[3]; // right, flagged, not judged
            foreach (var (piece, t, cls) in chosen)
            {
                using var g = piece.CopyGeometry(); var c = (Curve)g; var depth = (Curve)piece.DepthGeometry!;
                var tangent = c.TangentAt(t); var normal = Vector3d.CrossProduct(e, tangent); if (!normal.Unitize()) { counts[2]++; continue; }
                // The same parameter on the world-depth curve; the verdict must also hold 2 mm either way along the line.
                double span = c.Domain.Length, dt = Math.Min(.002 / Math.Max(c.GetLength(), 1e-9), .25) * span; int agreed = -2;
                foreach (double s in new[] { t, Math.Max(c.Domain.Min, t - dt), Math.Min(c.Domain.Max, t + dt) })
                {
                    var world = OnFaces(depth.PointAt(depth.Domain.ParameterAt(c.Domain.NormalizedParameterAt(s))), piece.SourceIdentity);
                    int v = Verdict(world, normal, piece.SourceIdentity, exact.Contains(piece.SourceIdentity));
                    if (agreed == -2) agreed = v; else if (agreed != v) agreed = -1;
                }
                if (agreed < 0) counts[2]++;
                else if (agreed == cls) counts[0]++;
                else
                {
                    counts[1]++; flaggedTotal++; var q = c.PointAt(t); plane.ClosestParameter(q, out var x, out var y);
                    if (flagged.Count < (only is null ? 12 : 40) && (only is null || key.Contains(only, StringComparison.Ordinal)))
                    {
                        var where = depth.PointAt(depth.Domain.ParameterAt(c.Domain.NormalizedParameterAt(t))); var note = new StringBuilder();
                        if (variants.Contains("why"))
                            foreach (var (label, offset) in new[] { ("left", normal * (4 * tol)), ("right", -normal * (4 * tol)), ("centre", Vector3d.Zero) })
                                foreach (var body in bodies)
                                    if (Blocked(where + offset, tol, 20 * tol, only: body.Id) != false)
                                        note.Append($" | {label}: {(Blocked(where + offset, tol, 20 * tol, only: body.Id) is null ? "unsure" : "blocked")} by {body.Id[^12..]}{(body.Id == piece.SourceIdentity ? " (own)" : "")} {(body.Exact ? AnalyticVisibility.Recognise(body.Geometry, tol)!.GetType().Name : body.Geometry.GetType().Name)} box z {body.Box.Min.Z:F3}..{body.Box.Max.Z:F3}");
                        if (variants.Contains("why") && bodies.FirstOrDefault(b => b.Id == piece.SourceIdentity) is { } mine && AnalyticVisibility.Recognise(mine.Geometry, tol) is AnalyticVisibility.CutTubeShape cutShape)
                        {   // What the engine's own solid says at this point, for each pair of its surfaces the point is near.
                            var solid = (M2DPlus.Core.Visibility.AnalyticCsg)AnalyticVisibility.Solid(cutShape, plane); var at = OnFaces(where, piece.SourceIdentity); var v = AnalyticVisibility.Project(plane, at);
                            var close = Enumerable.Range(0, cutShape.Surfaces.Count).Where(i => Math.Abs(CutTubeRecognition.Distance(cutShape.Surfaces[i], at)) < 1e-6).ToArray();
                            note.Append($" | engine: on surfaces [{string.Join(",", close.Select(i => $"{i}:{(cutShape.Surfaces[i].Cylinder ? $"cyl r={cutShape.Surfaces[i].Radius:F2}" : "plane")} {cutShape.Surfaces[i].Role}{(cutShape.Surfaces[i].Virtual ? "*" : "")}"))}]; moved {at.DistanceTo(where):E2}; no own bits {solid.Occlusion(v, tol)}; all near as own {solid.Occlusion(v, tol, close.Aggregate(0, (m, i) => m | 1 << i))}; tangent {solid.Occlusion(v, tol, close.Aggregate(0, (m, i) => m | 1 << i), true)}");
                            if (mine.Brep is not null) note.Append("; Rhino point-in-solid above the point at " + string.Join(", ", new[] { 10.0, 100, 1000, 3000 }.Select(k => $"{k * tol:G3}: {mine.Brep.IsPointInside(at + e * (k * tol), tol / 10, true)}")));
                            foreach (int i in close) note.Append($"; n{i}·e {(cutShape.Surfaces[i].Cylinder ? "(cyl)" : (cutShape.Surfaces[i].Direction * e).ToString("F4"))}");
                        }
                        if (variants.Contains("why"))
                        {   // Which exact solids the engine says hide this point (no own-surface rule), and Rhino's point-in-solid along the ray for each.
                            var at = OnFaces(where, piece.SourceIdentity); var v = AnalyticVisibility.Project(plane, at);
                            foreach (var body in bodies.Where(b => b.Exact))
                            {
                                var k = AnalyticVisibility.Solid(AnalyticVisibility.Recognise(body.Geometry, tol)!, plane).Occlusion(v, tol);
                                var inside = new List<double>();
                                if (body.Brep is { IsSolid: true } solidBrep) for (double h = 2 * tol; h < 4 * body.Box.Diagonal.Length; h *= 1.15) if (solidBrep.IsPointInside(at + e * h, tol / 10, true)) inside.Add(h);
                                if (k != M2DPlus.Core.Visibility.VisibilityClass.Visible || inside.Count != 0)
                                    note.Append($" | replay {body.Id[^12..]}{(body.Id == piece.SourceIdentity ? " (own)" : "")}: engine {k}; Rhino inside along the ray at {inside.Count} steps{(inside.Count == 0 ? "" : $" from {inside[0]:G4} to {inside[^1]:G4}")}");
                            }
                        }
                        if (variants.Contains("why"))
                            foreach (var h in heard)
                                if (h.World.ClosestPoint(where, out double ht, 20 * tol)) note.Append($" | HLD: {h.Id[^12..]}{(h.Id == piece.SourceIdentity ? " (own)" : "")} edge {h.Edge} {(h.Duplicate ? "Duplicate " : "")}{h.Kind} length {h.World.GetLength():F3} at {h.World.PointAt(ht).DistanceTo(where):E1}");
                        flagged.Add($"{key}: source {piece.SourceIdentity[^12..]} drawn {(cls == 0 ? "Visible" : "Hidden")} at ({x:F4}, {y:F4}) world {where}, check says {(agreed == 0 ? "Visible" : "Hidden")}{note}");
                    }
                }
            }
            adjudication[key] = new[] { list.Count, chosen.Length, counts[0], counts[1], counts[2] };
            Console.WriteLine($"    ray check [{key}]: {list.Count} samples, checked {chosen.Length}: right {counts[0]}, flagged {counts[1]}, not judged {counts[2]}");
        }
        Console.WriteLine($"    ray check: {moved} ray starts were moved from a stored edge onto its two faces' intersection");
        foreach (var line in flagged) Console.WriteLine("    FLAGGED " + line);
        Console.WriteLine($"    rays {rays}; bodies {bodies.Count}; recognised sources {exact.Count}");
        foreach (var b in owned) b.Dispose();

        double Share(Dictionary<string, double> l) => l.GetValueOrDefault("Unresolved") / Math.Max(1e-9, l.Values.Sum());
        File.WriteAllText(Path.Combine(output, stem + "-av.json"), JsonSerializer.Serialize(new
        {
            view = view.Name, stem, glass, cut, tolerance = tol, rhino = RhinoApp.Version.ToString(), runtime = Environment.Version.ToString(),
            sources = prepared.Count, recognised = exact.Count, analytic = Analytic(after),
            before = new { wall = beforeWall, nativeCalls = before.NativeCalls, nativeSolve = before.Phases.Seconds.GetValueOrDefault("native solve"), lengths = beforeLengths, unresolvedShare = Share(beforeLengths), route = before.Report.Route, busy = beforeBusy, error = before.Error, hash = hashBefore },
            after = new { wall = afterWall, firstWall = after1.Seconds, nativeCalls = after.NativeCalls, nativeSolve = after.Phases.Seconds.GetValueOrDefault("native solve"), lengths = afterLengths, unresolvedShare = Share(afterLengths), route = after.Report.Route, busy = afterBusy, error = after.Error, hash = hashAfter, repeatIdentical = hashAfter == hashAfter1, phases = after.Phases.Seconds },
            afterToBefore = Enumerable.Range(0, 4).Select(r => Enumerable.Range(0, 5).Select(c => forward[r, c]).ToArray()).ToArray(),
            beforeToAfter = Enumerable.Range(0, 4).Select(r => Enumerable.Range(0, 5).Select(c => backward[r, c]).ToArray()).ToArray(),
            adjudication, flagged, flaggedTotal, rays
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
