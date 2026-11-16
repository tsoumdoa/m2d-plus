using M2DPlus.Core.Visibility;
using Rhino.Geometry;

namespace M2DPlus.Rhino.Services;

/// <summary>
/// Recognition of cut tubes: a round member (solid or hollow) whose ends are cut by planes and by other cylinders
/// (a pipe coped to fit a node), described by its bounding surfaces and the cells of their sides that are material
/// (<see cref="AnalyticCsg"/>). Fails closed: a body that is not proved to be that set stays on native HLD.
///
/// What is established, and how:
///   faces     Every face lies on a plane or a circular cylinder within a bound that is computed, not sampled: the
///             face's NURBS surface is in the convex hull of its control points span by span, distance to a plane is
///             linear and distance to an axis is convex, so refined control points bound both from above and below.
///             The bound is the surface's slack in the exact predicate and is reported in Info.
///   the tube  One cylinder on the main axis holds the whole body (and one smaller, the bore, holds none of it); two
///             planes across the axis just beyond the body end it.
///   cells     Every other surface cuts the tube. The tube's wall is sampled on a grid; each sample is in one cell
///             (inside or outside each cutting surface). Rhino's point-in-solid test is asked at points spread
///             through each cell: all in, all out, or the body is refused (a stub beyond a cope or a hole through
///             one wall only has material in part of a cell). A cell no sample fell in stays unknown.
///   the set   The cells are then compared with the body again: on both sides of every face, and by volume.
///   edges     The lines drawn are the body's own edges. Each is measured against the two surfaces it joins; that
///             distance is the edge's sideways slack when it is classified.
/// The comparison with the body is by sampling and volume, as the plain shapes' check is by volume: it is evidence
/// that the description is the body, not a proof. The visibility decisions made from the description are exact.
/// </summary>
internal static class CutTubeRecognition
{
    /// <param name="Outward">For an edge's profile test: +1 when the solid's outward normal on this surface's faces is
    /// the surface's own (away from a cylinder's axis, along a plane's normal), -1 when it is the opposite.</param>
    internal sealed record Surface(bool Cylinder, Point3d Origin, Vector3d Direction, double Radius, CsgRole Role, double Slack, bool Virtual, int Outward);
    internal sealed record Edge(Curve World, int On, double Slack);

    /// <summary>Faces may be this far from their surface, and edges from the surfaces they join: 256 tolerances, and
    /// never more than a sixteenth of the wall thickness or radius. Beyond that the body is not a cut tube here.</summary>
    internal static double Limit(double outer, double inner, double tol) => Math.Min(256 * tol, Math.Min(outer, inner > 0 ? outer - inner : outer) / 16);

    internal static AnalyticVisibility.Shape? Recognise(Brep brep, double tol, Func<string, AnalyticVisibility.Shape?> no)
    {
        if (brep.Faces.Count > 48) return no("more than 48 faces");
        if (!brep.IsValid) return no("invalid Brep");
        if (!brep.IsSolid || !brep.IsManifold) return no("not a closed manifold solid");
        double volume = Math.Abs(brep.GetVolume()); if (!(volume > 0)) return no("no volume");
        var box = brep.GetBoundingBox(false); double span = box.IsValid ? box.Diagonal.Length : 0; if (!(span > 0)) return no("no extent");
        bool Along(Vector3d a, Vector3d b) => a.Unitize() && b.Unitize() && Vector3d.CrossProduct(a, b).Length * span <= tol;

        // ---- Faces to nominal surfaces. Planes and cylinders Rhino recognises at the model tolerance first; a NURBS
        // face that is nearly a cylinder gets its cylinder from its own shape, and its bound is certified below.
        var nominal = new (bool Cylinder, Point3d O, Vector3d D, double R)?[brep.Faces.Count];
        foreach (var face in brep.Faces)
        {
            if (face.TryGetPlane(out var plane, tol)) nominal[face.FaceIndex] = (false, plane.Origin, plane.Normal, 0);
            else if (face.TryGetCylinder(out var cylinder, tol))
            {
                var u = cylinder.Axis; if (!u.Unitize()) return no("degenerate cylinder axis");
                nominal[face.FaceIndex] = (true, cylinder.Center, u, cylinder.Radius);
            }
            else if (Ruled(face, tol) is { } fitted) nominal[face.FaceIndex] = fitted;
            else return no($"face {face.FaceIndex} is neither a plane nor near a circular cylinder");
        }
        // The tube itself: the axis that carries the most cylindrical area.
        var areas = brep.Faces.Select(f => AreaMassProperties.Compute(f, true, false, false, false)?.Area ?? 0).ToArray();
        var cylinders = Enumerable.Range(0, nominal.Length).Where(i => nominal[i]!.Value.Cylinder).ToArray();
        if (cylinders.Length == 0) return no(AnalyticVisibility.PlanarOnlyReason);
        int lead = cylinders.OrderByDescending(i => areas[i]).First(); var axisPoint = nominal[lead]!.Value.O; var axis = nominal[lead]!.Value.D; var axisLine = new Line(axisPoint, axisPoint + axis);
        bool OnAxis((bool Cylinder, Point3d O, Vector3d D, double R) s, double slack) => s.Cylinder && Vector3d.CrossProduct(s.D, axis).Length * span <= tol + slack && axisLine.DistanceTo(s.O, false) <= tol + slack;
        // A loosely fitted face on the tube's own axis takes the axis exactly; its radius stays the fitted one.
        for (int i = 0; i < nominal.Length; i++)
            if (nominal[i] is { Cylinder: true } s && !brep.Faces[i].TryGetCylinder(out _, tol) && OnAxis(s, Math.Max(s.R, 1) * 1e-3))
                nominal[i] = (true, axisPoint + axis * ((s.O - axisPoint) * axis), axis, s.R);

        // ---- Distinct surfaces.
        var surfaces = new List<Surface>(); var faceSurface = new int[brep.Faces.Count];
        var onAxis = cylinders.Where(i => OnAxis(nominal[i]!.Value, 0)).ToArray();
        double ro = onAxis.Max(i => nominal[i]!.Value.R), ri = onAxis.Min(i => nominal[i]!.Value.R); if (ro - ri <= tol) ri = 0;
        if (onAxis.Any(i => Math.Abs(nominal[i]!.Value.R - ro) > tol && Math.Abs(nominal[i]!.Value.R - ri) > tol)) return no("not one tube: more than two radii on its axis");
        double limit = Limit(ro, ri, tol); if (!(limit > 8 * tol)) return no("radius or wall below 128 tolerances");
        if (ri > 0 && ri >= ro - 8 * tol) return no("bore not inside the tube");
        foreach (var face in brep.Faces)
        {
            var s = nominal[face.FaceIndex]!.Value;
            // The outward normal relative to the surface's own is the same all over a plane or cylinder, so any point
            // tells it; a point inside the trims is preferred, and a sliver face with none uses the middle of its trims' box.
            if (!Interior(face, out var at, out var normal))
            {
                var uv = face.OuterLoop?.GetBoundingBox(true) ?? BoundingBox.Unset;
                double pu = uv.IsValid ? uv.Center.X : face.Domain(0).Mid, pv = uv.IsValid ? uv.Center.Y : face.Domain(1).Mid;
                at = face.PointAt(pu, pv); normal = face.NormalAt(pu, pv);
                if (!normal.Unitize()) return no($"face {face.FaceIndex} has no normal");
            }
            Vector3d natural;
            if (s.Cylinder) { natural = at - s.O; natural -= s.D * (natural * s.D); if (!natural.Unitize()) return no("a cylindrical face through its own axis"); }
            else natural = s.D;
            if (Math.Abs(natural * normal) < .5) return no($"face {face.FaceIndex}: its normal is not its surface's");
            int outward = natural * normal > 0 ? 1 : -1;
            int index = surfaces.FindIndex(k => k.Cylinder == s.Cylinder && (s.Cylinder
                ? Math.Abs(k.Radius - s.R) <= tol && Along(k.Direction, s.D) && new Line(k.Origin, k.Origin + k.Direction).DistanceTo(s.O, false) <= tol
                : Along(k.Direction, s.D) && Math.Abs(new Plane(k.Origin, k.Direction).DistanceTo(s.O)) <= tol));
            if (index < 0)
            {
                bool tube = OnAxis(s, 0);
                var role = tube ? (Math.Abs(s.R - ro) <= tol ? CsgRole.Inside : CsgRole.Outside) : CsgRole.Free;
                if (tube && outward != (role == CsgRole.Inside ? 1 : -1)) return no("a tube face turned the wrong way");
                index = surfaces.Count; surfaces.Add(new(s.Cylinder, s.O, s.D, s.R, role, 0, false, outward));
            }
            else
            {   // The same plane given with the opposite normal has the opposite sense.
                int sense = !s.Cylinder && surfaces[index].Direction * s.D < 0 ? -outward : outward;
                if (surfaces[index].Outward != sense) return no("a surface with the solid on both sides");
            }
            faceSurface[face.FaceIndex] = index;
        }
        if (surfaces.Count(k => k.Role == CsgRole.Inside) != 1) return no("not one tube");
        if (surfaces.Count(k => k.Role == CsgRole.Free) > 18) return no("more than 18 cutting surfaces");
        // The certified bound of every face against its surface.
        foreach (var face in brep.Faces)
        {
            int index = faceSurface[face.FaceIndex]; var s = surfaces[index];
            double bound = Deviation(face, s.Cylinder, s.Origin, s.Direction, s.Radius, tol / 8, limit);
            if (!(bound <= limit)) return no($"face {face.FaceIndex} is further than {limit:G3} from its plane or cylinder (certified bound {bound:G3})");
            if (bound > s.Slack) surfaces[index] = s with { Slack = bound };
        }
        double faceBound = surfaces.Max(s => s.Slack);

        // ---- Two planes across the axis just beyond the body: the tube ends there whatever its end cuts are.
        var frame = new Plane(axisPoint, axis); var local = brep.GetBoundingBox(frame);
        if (!local.IsValid) return no("no extent along the axis");
        double pad = 16 * tol + 4 * faceBound, h0 = local.Min.Z - pad, h1 = local.Max.Z + pad;
        int real = surfaces.Count;
        surfaces.Add(new(false, axisPoint + axis * h1, axis, 0, CsgRole.Inside, 0, true, 1)); surfaces.Add(new(false, axisPoint + axis * h0, -axis, 0, CsgRole.Inside, 0, true, 1));
        // Two cutting cylinders of one radius whose axes meet (two members mitred together, both cut into this tube)
        // cross each other in two planes. Neither plane need be a face here, yet material on one side of it and void on
        // the other can lie in the same cell of the cylinders: the planes are added so that the cells can tell them apart.
        for (int i = 0; i < real; i++)
            for (int j = i + 1; j < real; j++)
            {
                var a = surfaces[i]; var b = surfaces[j];
                if (!a.Cylinder || !b.Cylinder || a.Role != CsgRole.Free || b.Role != CsgRole.Free || Math.Abs(a.Radius - b.Radius) > tol) continue;
                var la = new Line(a.Origin, a.Origin + a.Direction); var lb = new Line(b.Origin, b.Origin + b.Direction);
                if (Vector3d.CrossProduct(a.Direction, b.Direction).Length < 1e-6 || !global::Rhino.Geometry.Intersect.Intersection.LineLine(la, lb, out double ta, out double tb, tol, false)) continue;
                var pa = la.PointAt(ta); var pb = lb.PointAt(tb); if (pa.DistanceTo(pb) > tol) continue;
                foreach (double sign in new[] { -1.0, 1.0 })
                {
                    var n = a.Direction + b.Direction * sign; if (!n.Unitize()) continue;
                    if (surfaces.Any(k => !k.Cylinder && Along(k.Direction, n) && Math.Abs(new Plane(k.Origin, k.Direction).DistanceTo(pa)) <= tol)) continue;   // already a face
                    surfaces.Add(new(false, pa, n, 0, CsgRole.Free, 0, true, 1));
                }
            }
        if (surfaces.Count(k => k.Role == CsgRole.Free) > 18 || surfaces.Count > 30) return no("more than 18 cutting surfaces");

        // ---- The cells of the cutting surfaces, from samples through the wall.
        static Vertex W(Point3d p) => new(p.X, p.Y, p.Z); static Vertex D(Vector3d d) => new(d.X, d.Y, d.Z);
        var primitives = surfaces.Select(s => new CsgPrimitive(s.Cylinder, W(s.Origin), D(s.Direction), s.Radius, s.Role)).ToArray();
        AnalyticCsg probe;
        try { probe = new AnalyticCsg(primitives, W(box.Min), W(box.Max)); }
        catch (ArgumentException) { return no("surfaces the exact solid refuses"); }
        // Points are compared with the body only where they are clear of every surface by more than the body's own
        // looseness: its faces' bound and the tolerances stored on its edges and vertices.
        double loose = Math.Max(brep.Edges.Max(e => e.Tolerance), brep.Vertices.Max(v => v.Tolerance)); if (!(loose >= 0) || loose > limit) return no("edge or vertex tolerances beyond the limit");
        double margin = Math.Max(16 * tol, 4 * Math.Max(faceBound, loose)), length = h1 - h0;
        int nTheta = 96, nH = (int)Math.Clamp(length / (2 * Math.PI * ro / nTheta), 8, 1500), nRho = ri > 0 ? 3 : 6;
        Point3d Sample(int ih, int it, int ir)
        {   // Equal volumes: rho² uniform between the radii.
            double rho = Math.Sqrt(ri * ri + (ro * ro - ri * ri) * (ir + .5) / nRho), theta = 2 * Math.PI * (it + .5) / nTheta, h = h0 + length * (ih + .5) / nH;
            return frame.PointAt(rho * Math.Cos(theta), rho * Math.Sin(theta), h);
        }
        var byCell = new Dictionary<int, List<int>>(); var count = new Dictionary<int, long>(); long cells = 0;
        for (int ih = 0; ih < nH; ih++)
            for (int it = 0; it < nTheta; it++)
                for (int ir = 0; ir < nRho; ir++)
                {
                    int cell = probe.Cell(W(Sample(ih, it, ir)), margin, out bool certain, out _); cells++;
                    count[cell] = count.GetValueOrDefault(cell) + 1;
                    if (!certain) continue;
                    if (!byCell.TryGetValue(cell, out var list)) byCell[cell] = list = [];
                    list.Add((ih * nTheta + it) * nRho + ir);
                }
        var inside = new List<int>(); var outside = new List<int>(); int asked = 0;
        foreach (var (cell, list) in byCell.OrderBy(k => k.Key))
        {   // Up to 24 points spread through the cell, its first and last along the axis among them.
            int take = Math.Min(24, list.Count); bool? solid = null;
            for (int k = 0; k < take; k++)
            {
                int id = list[take == 1 ? 0 : (int)((long)k * (list.Count - 1) / (take - 1))]; int ir = id % nRho, it = id / nRho % nTheta, ih = id / nRho / nTheta;
                bool here = brep.IsPointInside(Sample(ih, it, ir), tol, false); asked++;
                if (solid is { } before && before != here) return no("material in part of a cell only (a stub beyond a cut, a notch or a hole through one wall)");
                solid = here;
            }
            (solid == true ? inside : outside).Add(cell);
        }
        // A small face can bound a cell the grid has no sample in. The points just behind and in front of every face
        // are asked too; a cell they disagree about with the grid, or with each other, refuses the body.
        var known = inside.ToDictionary(c => c, _ => true); foreach (int c in outside) known[c] = false;
        foreach (var face in brep.Faces)
        {
            int own = faceSurface[face.FaceIndex];
            foreach (var (at, normal) in InteriorPoints(face, 9))
                foreach (double side in new[] { -2.0, 2.0 })
                {
                    var x = at + normal * (side * margin);
                    if (!Clear(surfaces, own, x, margin)) continue;
                    int cell = probe.Cell(W(x), margin, out bool certain, out bool inTube); if (!certain || !inTube) continue;
                    bool here = brep.IsPointInside(x, tol, false); asked++;
                    if (known.TryGetValue(cell, out bool before))
                    {
                        if (before != here)
                        {
                            var free = Enumerable.Range(0, surfaces.Count).Where(i => surfaces[i].Role == CsgRole.Free).ToArray();
                            string sides = string.Join(", ", free.Select((i, bit) => $"{((cell >> bit & 1) != 0 ? "in" : "out")} {i}:{(surfaces[i].Cylinder ? $"cyl r={surfaces[i].Radius:F2}" : "plane")}{(surfaces[i].Virtual ? "*" : "")}"));
                            var d = x - axisPoint; double hx = d * axis;
                            string grid = byCell.TryGetValue(cell, out var seen) ? $"grid samples {seen.Count}, heights {h0 + length * (seen.Min(id => id / nRho / nTheta) + .5) / nH:F1}..{h0 + length * (seen.Max(id => id / nRho / nTheta) + .5) / nH:F1}" : "no grid sample";
                            return no($"material in part of a cell only, seen at face {face.FaceIndex}: cell [{sides}] was {(before ? "solid" : "void")} ({grid}) but is {(here ? "solid" : "void")} at height {hx:F1}, point {x} (tube heights {h0:F1}..{h1:F1})");
                        }
                    }
                    else { known[cell] = here; (here ? inside : outside).Add(cell); }
                }
        }
        if (inside.Count == 0 || asked < 24) return no("too few points to compare with the body");
        AnalyticCsg world;
        try { world = new AnalyticCsg(primitives, W(box.Min), W(box.Max), inside, outside); }
        catch (ArgumentException) { return no("cells the exact solid refuses"); }

        // ---- The description against the body once more: both sides of every face, and the volume.
        foreach (var face in brep.Faces)
        {
            int checks = 0, own = faceSurface[face.FaceIndex], points = 0, clear = 0; string seen = "";
            foreach (var (at, normal) in InteriorPoints(face, 5))
            {
                var behind = at - normal * (2 * margin); var front = at + normal * (2 * margin); points++;
                // Only where no other surface is near enough to decide the point instead.
                if (!Clear(surfaces, own, behind, margin) || !Clear(surfaces, own, front, margin)) continue;
                clear++; seen += $" [{world.Contains(W(behind), margin)?.ToString() ?? "?"}/{world.Contains(W(front), margin)?.ToString() ?? "?"}]";
                if (world.Contains(W(behind), margin) == false) return no($"material missing behind face {face.FaceIndex}");
                if (world.Contains(W(front), margin) == true) return no($"material in front of face {face.FaceIndex}");
                if (world.Contains(W(behind), margin) == true && world.Contains(W(front), margin) == false) checks++;
            }
            // A face every point of which is within the margin of another surface (a sliver between two cutting
            // cylinders near where they cross) cannot be compared on its own; its cells are the other surface's, and
            // what the samples did not reach stays unknown. A sizeable face with no point found at all is refused.
            if (checks == 0 && clear == 0 && points > 0) continue;
            if (checks == 0 && areas[face.FaceIndex] > 4096 * margin * margin) return no($"face {face.FaceIndex} could not be checked against the cells (area {areas[face.FaceIndex]:G4}, margin {margin:G3}, interior points {points}, clear of other surfaces {clear}, behind/front{seen})");
        }
        long solidCells = count.Where(k => inside.Contains(k.Key)).Sum(k => k.Value), unknownCells = count.Where(k => !inside.Contains(k.Key) && !outside.Contains(k.Key)).Sum(k => k.Value);
        double cellVolume = Math.PI * (ro * ro - ri * ri) * length / cells, expected = cellVolume * solidCells;
        if (Math.Abs(volume - expected) > .01 * volume + cellVolume * unknownCells) return no($"volume differs from the cut tube's ({volume:G6} against {expected:G6})");
        if (cellVolume * unknownCells > .01 * volume) return no("too much of the tube in cells that could not be classified");

        // ---- Edges between different surfaces are the lines drawn.
        var edges = new List<Edge>(); double edgeBound = 0;
        foreach (var edge in brep.Edges)
        {
            var faces = edge.AdjacentFaces(); if (faces.Length != 2) { if (edge.Valence == EdgeAdjacency.Interior) continue; return no("an edge that is not shared by two faces"); }
            int a = faceSurface[faces[0]], b = faceSurface[faces[1]]; if (a == b) continue;
            double slack = 0;
            for (int k = 0; k <= 16; k++)
            {
                var q = edge.PointAt(edge.Domain.ParameterAt(k / 16.0));
                slack = Math.Max(slack, Math.Max(Math.Abs(Distance(surfaces[a], q)), Math.Abs(Distance(surfaces[b], q))));
            }
            if (slack > limit) return no($"an edge further than {limit:G3} from the surfaces it joins ({slack:G3})");
            edgeBound = Math.Max(edgeBound, slack);
            edges.Add(new(edge.DuplicateCurve(), (1 << a) | (1 << b), slack * 1.25));
        }
        return new AnalyticVisibility.CutTubeShape(surfaces, box, edges, faceSurface, real, faceBound, edgeBound, inside, outside);
    }

    /// <summary>Signed distance to a surface: negative inside a cylinder or behind a plane.</summary>
    internal static double Distance(Surface s, Point3d q)
    {
        var w = q - s.Origin;
        if (!s.Cylinder) return w * s.Direction;
        w -= s.Direction * (w * s.Direction); return w.Length - s.Radius;
    }

    /// <summary>
    /// A NURBS face Rhino does not call a cylinder: ruled (degree one) in one direction, with a circle at each end of
    /// the rulings. The axis is the line through the two centres. Only a nominal surface: the bound is certified later.
    /// </summary>
    private static (bool Cylinder, Point3d O, Vector3d D, double R)? Ruled(BrepFace face, double tol)
    {
        using var surface = face.UnderlyingSurface()?.ToNurbsSurface(); if (surface is null) return null;
        (Point3d C, double R)? Round(Curve curve)
        {
            // Loose here on purpose: an ellipse with nearly equal radii is a circle to the bound that follows.
            if (curve.TryGetCircle(out var circle, 64 * tol)) return (circle.Center, circle.Radius);
            if (curve.TryGetArc(out var arc, 64 * tol)) return (arc.Center, arc.Radius);
            if (curve.TryGetEllipse(out var ellipse, 64 * tol) && Math.Abs(ellipse.Radius1 - ellipse.Radius2) <= 64 * tol) return (ellipse.Plane.Origin, (ellipse.Radius1 + ellipse.Radius2) / 2);
            return null;
        }
        foreach (int ruled in new[] { 0, 1 })
        {
            if (surface.Degree(ruled) != 1) continue;
            var domain = surface.Domain(ruled);
            using var first = surface.IsoCurve(1 - ruled, domain.Min); using var last = surface.IsoCurve(1 - ruled, domain.Max);
            if (first is null || last is null || Round(first) is not { } a || Round(last) is not { } b) continue;
            var axis = b.C - a.C; if (!axis.Unitize() || Math.Abs(a.R - b.R) > 64 * tol) continue;
            return (true, a.C, axis, (a.R + b.R) / 2);
        }
        // Circles along one direction and a straight axis of centres, whatever the degrees.
        foreach (int along in new[] { 0, 1 })
        {
            var domain = surface.Domain(along); var centres = new List<Point3d>(); double radius = 0; bool round = true;
            for (int k = 0; k <= 2 && round; k++)
            {
                using var iso = surface.IsoCurve(1 - along, domain.ParameterAt(k / 2.0));
                if (iso is null || Round(iso) is not { } c) { round = false; break; }
                centres.Add(c.C); radius += c.R / 3;
            }
            if (!round) continue;
            var axis = centres[2] - centres[0]; if (!axis.Unitize()) continue;
            if (new Line(centres[0], centres[2]).DistanceTo(centres[1], false) > 64 * tol) continue;
            return (true, centres[0], axis, radius);
        }
        return null;
    }

    /// <summary>
    /// The point of the intersection of two surfaces nearest to q (the least move that puts q on both), or null when
    /// the surfaces do not cross cleanly near q (they touch, or q is on a cylinder's axis).
    /// </summary>
    internal static Point3d? Onto(Surface a, Surface b, Point3d q)
    {
        static bool Gradient(Surface s, Point3d x, out double f, out Vector3d n)
        {
            f = Distance(s, x);
            if (!s.Cylinder) { n = s.Direction; return true; }
            n = x - s.Origin; n -= s.Direction * (n * s.Direction); return n.Unitize();
        }
        var x = q; double scale = 1 + Math.Max(a.Radius, b.Radius);
        for (int step = 0; step < 12; step++)
        {
            if (!Gradient(a, x, out double fa, out var na) || !Gradient(b, x, out double fb, out var nb)) return null;
            if (Math.Abs(fa) + Math.Abs(fb) <= 1e-13 * scale) return x;
            // The least move that cancels both distances to first order.
            double ab = na * nb, det = 1 - ab * ab; if (det < 1e-4) return null;
            double la = (fa - ab * fb) / det, lb = (fb - ab * fa) / det;
            x -= na * la + nb * lb;
        }
        return Math.Abs(Distance(a, x)) + Math.Abs(Distance(b, x)) <= 1e-9 * scale ? x : null;
    }

    private static bool Clear(List<Surface> surfaces, int skip, Point3d q, double margin)
    {
        for (int i = 0; i < surfaces.Count; i++) if (i != skip && Math.Abs(Distance(surfaces[i], q)) < 2 * margin) return false;
        return true;
    }

    private static bool Interior(BrepFace face, out Point3d at, out Vector3d normal)
    {
        foreach (var hit in InteriorPoints(face, 1)) { (at, normal) = hit; return true; }
        at = Point3d.Unset; normal = Vector3d.Unset; return false;
    }

    /// <summary>Points inside the face's trims with the solid's outward normal there.</summary>
    private static IEnumerable<(Point3d At, Vector3d Normal)> InteriorPoints(BrepFace face, int wanted)
    {
        var du = face.Domain(0); var dv = face.Domain(1); int found = 0;
        // The trims' own box in the parameter plane first: a face is often a sliver of a large surface.
        var loop = face.OuterLoop; var uv = loop?.GetBoundingBox(true) ?? BoundingBox.Unset;
        if (uv.IsValid) { du = new Interval(Math.Max(du.Min, uv.Min.X), Math.Min(du.Max, uv.Max.X)); dv = new Interval(Math.Max(dv.Min, uv.Min.Y), Math.Min(dv.Max, uv.Max.Y)); }
        foreach (int n in new[] { 3, 7, 15, 31 })
        {
            for (int i = 0; i < n && found < wanted; i++)
                for (int j = 0; j < n && found < wanted; j++)
                {
                    if (n > 3 && i % 2 == 1 && j % 2 == 1) continue;       // seen at the coarser level
                    double u = du.ParameterAt((i + .5) / n), v = dv.ParameterAt((j + .5) / n);
                    if (face.IsPointOnFace(u, v) != PointFaceRelation.Interior) continue;
                    var normal = face.NormalAt(u, v); if (!normal.Unitize()) continue;
                        found++; yield return (face.PointAt(u, v), normal);
                }
            if (found >= wanted) yield break;
        }
        // A narrow or slanting face the grid misses: the middles of the largest triangles of its mesh, put back on the face.
        using var copy = face.DuplicateFace(false); var meshes = copy is null ? null : Mesh.CreateFromBrep(copy, MeshingParameters.Default);
        if (meshes is null) yield break;
        var centres = new List<(double Area, Point3d At)>();
        foreach (var mesh in meshes)
        {
            if (mesh is null) continue;
            mesh.Faces.ConvertQuadsToTriangles();
            foreach (var triangle in mesh.Faces)
            {
                Point3d a = mesh.Vertices[triangle.A], b = mesh.Vertices[triangle.B], c = mesh.Vertices[triangle.C];
                centres.Add((Vector3d.CrossProduct(b - a, c - a).Length / 2, new Point3d((a.X + b.X + c.X) / 3, (a.Y + b.Y + c.Y) / 3, (a.Z + b.Z + c.Z) / 3)));
            }
            mesh.Dispose();
        }
        foreach (var (_, centre) in centres.OrderByDescending(c => c.Area).Take(8 * wanted))
        {
            if (found >= wanted) yield break;
            if (!face.ClosestPoint(centre, out double u, out double v) || face.IsPointOnFace(u, v) != PointFaceRelation.Interior) continue;
            var normal = face.NormalAt(u, v); if (!normal.Unitize()) continue;
            found++; yield return (face.PointAt(u, v), normal);
        }
    }

    /// <summary>
    /// A certified bound on how far the face's surface is from a plane (origin, normal) or cylinder (axis, radius).
    /// Returns infinity when no bound can be given. Refinement stops at <paramref name="target"/>, or when the bound
    /// is clearly beyond <paramref name="limit"/>.
    /// </summary>
    internal static double Deviation(BrepFace face, bool cylinder, Point3d origin, Vector3d direction, double radius, double target, double limit)
    {
        using var copy = face.DuplicateFace(false); if (copy is null || copy.Faces.Count != 1) return double.PositiveInfinity;
        copy.Faces.ShrinkFaces();
        using var surface = copy.Faces[0].UnderlyingSurface()?.ToNurbsSurface(); if (surface is null) return double.PositiveInfinity;
        if (!direction.Unitize()) return double.PositiveInfinity;
        double bound = double.PositiveInfinity, previous = double.PositiveInfinity;
        for (int round = 0; round < 14; round++)
        {
            bound = Hull(surface, cylinder, origin, direction, radius);
            if (bound <= target || !double.IsFinite(bound)) break;
            // The control net closes on the surface four times per halving; stop when that cannot reach the limit.
            if (round >= 3 && bound > limit && bound > previous * .5) break;
            if ((long)surface.Points.CountU * surface.Points.CountV > 60000) break;
            previous = bound;
            // Halve every span in a direction of degree two or more (a ruled direction's control points are on the surface).
            for (int dir = 0; dir < 2; dir++)
            {
                if (surface.Degree(dir) < 2) continue;
                var knots = dir == 0 ? surface.KnotsU : surface.KnotsV; var values = new List<double>();
                for (int k = 0; k < knots.Count; k++) if (values.Count == 0 || knots[k] > values[^1]) values.Add(knots[k]);
                for (int k = 0; k + 1 < values.Count; k++) knots.InsertKnot((values[k] + values[k + 1]) / 2, 1);
            }
        }
        return bound;
    }

    private static double Hull(NurbsSurface surface, bool cylinder, Point3d origin, Vector3d direction, double radius)
    {
        int nu = surface.Points.CountU, nv = surface.Points.CountV, pu = surface.Degree(0), pv = surface.Degree(1);
        var points = new Vector3d[nu, nv];      // from the origin; for a cylinder, the part across the axis
        double worst = 0;
        for (int i = 0; i < nu; i++)
            for (int j = 0; j < nv; j++)
            {
                if (!surface.Points.GetPoint(i, j, out Point3d p) || !(surface.Points.GetWeight(i, j) > 0)) return double.PositiveInfinity;
                var w = p - origin;
                if (!cylinder) { worst = Math.Max(worst, Math.Abs(w * direction)); continue; }
                points[i, j] = w - direction * (w * direction);
            }
        if (!cylinder) return worst;
        // Span by span: each patch is in the hull of its (pu + 1)(pv + 1) control points.
        for (int i = 0; i + pu < nu; i++)
            for (int j = 0; j + pv < nv; j++)
            {
                var mean = Vector3d.Zero; double far = 0;
                for (int a = 0; a <= pu; a++) for (int b = 0; b <= pv; b++) { mean += points[i + a, j + b]; far = Math.Max(far, points[i + a, j + b].Length); }
                if (!mean.Unitize()) return double.PositiveInfinity;
                double near = double.PositiveInfinity;
                for (int a = 0; a <= pu; a++) for (int b = 0; b <= pv; b++) near = Math.Min(near, points[i + a, j + b] * mean);
                worst = Math.Max(worst, Math.Max(far - radius, radius - near));
            }
        return worst;
    }
}
