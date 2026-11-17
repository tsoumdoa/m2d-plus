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
    /// <param name="Exact">The stored edge stands for the exact intersection of its two surfaces: every point of it
    /// snaps onto that curve (<see cref="Onto"/>), which is what is classified. Otherwise (where the two surfaces meet
    /// at too flat an angle to bound reliably) it is Unresolved.</param>
    /// <param name="Gap">How far the stored curve, which is what is drawn, may be from the curve that is classified.</param>
    internal sealed record Edge(Curve World, int On, double Slack, bool Exact, double Gap)
    {
        internal string Diagnostic { get; init; } = "";
    }

    /// <summary>Faces may be this far from their surface, and edges from the surfaces they join: 256 tolerances, and
    /// never more than a sixteenth of the wall thickness or radius. Beyond that the body is not a cut tube here.</summary>
    internal static double Limit(double outer, double inner, double tol) => Math.Min(256 * tol, Math.Min(outer, inner > 0 ? outer - inner : outer) / 16);

    /// <summary>The cheap part of recognition: could this body be a cut tube at all? A closed solid of at most 48
    /// faces, each a plane or near a circular cylinder, at least one of them a cylinder. The costly part (thousands of
    /// point-in-solid questions) is only spent on bodies that pass, and only on a bounded number of them per view.</summary>
    internal static bool Plausible(Brep brep, double tol)
    {
        if (brep.Faces.Count > 48 || !brep.IsSolid) return false;
        bool round = false;
        foreach (var face in brep.Faces)
        {
            if (face.TryGetPlane(out _, tol)) continue;
            if (!face.TryGetCylinder(out _, tol) && Ruled(face, tol) is null) return false;
            round = true;
        }
        return round;
    }

    internal static AnalyticVisibility.Shape? Recognise(Brep brep, double tol, Func<string, AnalyticVisibility.Shape?> no, CancellationToken token = default)
    {
        if (brep.Faces.Count > 48) return no("more than 48 faces");
        if (!brep.IsValid) return no("invalid Brep");
        if (!brep.IsSolid || !brep.IsManifold) return no("not a closed manifold solid");
        double volume = Math.Abs(brep.GetVolume()); if (!(volume > 0)) return no("no volume");
        var box = brep.GetBoundingBox(false); double span = box.IsValid ? box.Diagonal.Length : 0; if (!(span > 0)) return no("no extent");
        bool Along(Vector3d a, Vector3d b) => a.Unitize() && b.Unitize() && Vector3d.CrossProduct(a, b).Length * span <= tol;

        // ---- Faces to nominal surfaces. Planes and cylinders Rhino recognises at the model tolerance first; a NURBS
        // face that is nearly a cylinder gets its cylinder from its own shape, and its bound is certified below.
        var nominal = new (bool Cylinder, Point3d O, Vector3d D, double R)?[brep.Faces.Count]; var fitted = new bool[brep.Faces.Count];
        foreach (var face in brep.Faces)
        {
            if (face.TryGetPlane(out var plane, tol)) nominal[face.FaceIndex] = (false, plane.Origin, plane.Normal, 0);
            else if (face.TryGetCylinder(out var cylinder, tol))
            {
                var u = cylinder.Axis; if (!u.Unitize()) return no("degenerate cylinder axis");
                nominal[face.FaceIndex] = (true, cylinder.Center, u, cylinder.Radius);
            }
            else if (Ruled(face, tol) is { } near) { nominal[face.FaceIndex] = near; fitted[face.FaceIndex] = true; }
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
        // The body's own looseness: the tolerances stored on its edges and vertices.
        double loose = Math.Max(brep.Edges.Max(e => e.Tolerance), brep.Vertices.Max(v => v.Tolerance)); if (!(loose >= 0) || loose > limit) return no("edge or vertex tolerances beyond the limit");
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
            // A face fitted loosely (a NURBS near-cylinder, often one of several pieces of the same surface) joins an
            // existing cylinder it agrees with to a quarter of the limit; its bound is then taken against that one.
            // So do faces whose surfaces agree to within the body's own looseness: a body whose edges are stored to
            // 0.03 cannot hold two different surfaces 0.008 apart (two pieces of one cope, fitted separately), and a
            // slab that thin between them would be a cell nothing could be learned about.
            double same = Math.Max(fitted[face.FaceIndex] ? limit / 4 : tol, Math.Min(loose, limit / 4));
            int index = surfaces.FindIndex(k => k.Cylinder == s.Cylinder && (s.Cylinder
                ? Math.Abs(k.Radius - s.R) <= same && Vector3d.CrossProduct(k.Direction, s.D).Length * span <= same && new Line(k.Origin, k.Origin + k.Direction).DistanceTo(s.O, false) <= same
                : Vector3d.CrossProduct(k.Direction, s.D).Length * span <= same && Math.Abs(new Plane(k.Origin, k.Direction).DistanceTo(s.O)) <= same));
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
            token.ThrowIfCancellationRequested();
            int index = faceSurface[face.FaceIndex]; var s = surfaces[index];
            double bound = Deviation(face, s.Cylinder, s.Origin, s.Direction, s.Radius, tol / 8, limit);
            if (!(bound <= limit)) return no($"face {face.FaceIndex} is further than {limit:G3} from its plane or cylinder (certified bound {bound:G3})");
            if (bound > s.Slack) surfaces[index] = s with { Slack = bound };
        }
        double faceBound = surfaces.Max(s => s.Slack);
        // Nothing of the body may lie beyond the tube's outer cylinder (a branch stub, a lug): the exact solid is
        // inside it by construction. Every edge is bounded through its control points; a planar face lies within its
        // edges' hull and so inside the cylinder with them. A cylindrical face can bulge out between edges that do
        // not (a blister): those are looked at where points on them can be found, which is evidence, not a bound.
        foreach (var edge in brep.Edges)
        {
            token.ThrowIfCancellationRequested();
            using var curve = edge.DuplicateCurve(); double reach = Reach(curve, axisPoint, axis, ro + limit);
            if (!(reach <= ro + limit)) return no($"material beyond the tube's outer cylinder (edge {edge.EdgeIndex} reaches {reach - ro:G3} outside it)");
        }
        foreach (var face in brep.Faces)
        {
            if (!surfaces[faceSurface[face.FaceIndex]].Cylinder || surfaces[faceSurface[face.FaceIndex]].Role != CsgRole.Free) continue;
            foreach (var (at, _) in InteriorPoints(face, 9))
                if (axisLine.DistanceTo(at, false) > ro + limit) return no($"material beyond the tube's outer cylinder (face {face.FaceIndex})");
        }

        // ---- Two planes across the axis just beyond the body: the tube ends there whatever its end cuts are.
        var frame = new Plane(axisPoint, axis); var local = brep.GetBoundingBox(frame);
        if (!local.IsValid) return no("no extent along the axis");
        // Points are compared with the body only where they are clear of every surface by more than the body's own
        // looseness: its faces' bound and the tolerances stored on its edges and vertices.
        double margin = Math.Max(16 * tol, 4 * Math.Max(faceBound, loose));
        // At least six margins beyond the body, so that a point two margins in front of an end face is still clear of
        // them; and a whole number of the sampling grid's steps, so that the grid's layers start and end with the body
        // (square ends then fall between layers, and the volume the layers add up to is not a layer out).
        int nTheta = 96, nBody = (int)Math.Clamp((local.Max.Z - local.Min.Z) / (2 * Math.PI * ro / nTheta), 8, 1500);
        double layer = (local.Max.Z - local.Min.Z) / nBody; if (!(layer > 0)) return no("no extent along the axis");
        int padLayers = (int)Math.Clamp(Math.Ceiling(6 * margin / layer), 1, 64);
        double pad = Math.Max(6 * margin, padLayers * layer), h0 = local.Min.Z - pad, h1 = local.Max.Z + pad;
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
        // "Outside a cutting cylinder" has two sides along the tube: before the cutter and beyond it. A tip of material
        // left beyond a cutter and the void in front of it would share a cell; a plane through the cutter's axis, across
        // the tube's direction, tells them apart.
        for (int i = 0; i < real; i++)
        {
            var c = surfaces[i]; if (!c.Cylinder || c.Role != CsgRole.Free) continue;
            var n = axis - c.Direction * (axis * c.Direction); if (!n.Unitize()) continue;           // parallel to the tube: no before or beyond
            if (surfaces.Any(k => !k.Cylinder && Along(k.Direction, n) && Math.Abs(new Plane(k.Origin, k.Direction).DistanceTo(c.Origin)) <= tol)) continue;
            surfaces.Add(new(false, c.Origin, n, 0, CsgRole.Free, 0, true, 1));
        }
        // The wall is a ring. A region bounded by planes alone can hold two pieces of it, on different sides of the
        // bore (a slab along the tube narrower than the bore), one material and one void. Six planes through the tube's
        // axis divide the wall into 30° sectors, in each of which such a region is one piece.
        if (surfaces.Any(k => k.Role == CsgRole.Free))
            for (int k = 0; k < 6; k++)
            {
                var n = frame.XAxis * Math.Cos(k * Math.PI / 6) + frame.YAxis * Math.Sin(k * Math.PI / 6);
                if (surfaces.Any(f => !f.Cylinder && Along(f.Direction, n) && Math.Abs(new Plane(f.Origin, f.Direction).DistanceTo(axisPoint)) <= tol)) continue;   // already a surface
                surfaces.Add(new(false, axisPoint, n, 0, CsgRole.Free, 0, true, 1));
            }
        if (surfaces.Count(k => k.Role == CsgRole.Free) > 26 || surfaces.Count > 30) return no("more than 26 cutting and separating surfaces");

        // ---- The cells of the cutting surfaces, from samples through the wall.
        static Vertex W(Point3d p) => new(p.X, p.Y, p.Z); static Vertex D(Vector3d d) => new(d.X, d.Y, d.Z);
        var primitives = surfaces.Select(s => new CsgPrimitive(s.Cylinder, W(s.Origin), D(s.Direction), s.Radius, s.Role)).ToArray();
        AnalyticCsg probe;
        try { probe = new AnalyticCsg(primitives, W(box.Min), W(box.Max)); }
        catch (ArgumentException) { return no("surfaces the exact solid refuses"); }
        double length = h1 - h0;
        int nH = pad == padLayers * layer ? nBody + 2 * padLayers : (int)Math.Clamp(length / (2 * Math.PI * ro / nTheta), 8, 1700), nRho = ri > 0 ? 3 : 12;
        Point3d Sample(int ih, int it, int ir)
        {   // Equal volumes: rho² uniform between the radii.
            double rho = Math.Sqrt(ri * ri + (ro * ro - ri * ri) * (ir + .5) / nRho), theta = 2 * Math.PI * (it + .5) / nTheta, h = h0 + length * (ih + .5) / nH;
            return frame.PointAt(rho * Math.Cos(theta), rho * Math.Sin(theta), h);
        }
        var byCell = new Dictionary<int, List<int>>(); var count = new Dictionary<int, long>(); long cells = 0;
        var best = new Dictionary<int, (double Clearance, int Id)>();     // per cell, the grid sample furthest from every surface
        double Clear0(Point3d x) { double least = double.MaxValue; foreach (var k in surfaces) least = Math.Min(least, Math.Abs(Distance(k, x))); return least; }
        for (int ih = 0; ih < nH; ih++)
            for (int it = 0; it < nTheta; it++)
                for (int ir = 0; ir < nRho; ir++)
                {
                    var sample = Sample(ih, it, ir);
                    int cell = probe.Cell(W(sample), margin, out bool certain, out _); cells++;
                    count[cell] = count.GetValueOrDefault(cell) + 1;
                    double clearance = Clear0(sample);
                    if (!best.TryGetValue(cell, out var held) || clearance > held.Clearance) best[cell] = (clearance, (ih * nTheta + it) * nRho + ir);
                    if (!certain) continue;
                    if (!byCell.TryGetValue(cell, out var list)) byCell[cell] = list = [];
                    list.Add((ih * nTheta + it) * nRho + ir);
                }
        // ---- Learning. Rhino's point-in-solid test is asked at points that are clear of every surface. A point clear
        // by four times the body's looseness (its faces' bound, its stored edge and vertex tolerances) is strong
        // evidence; one clear by only twice that is weak: next to a ragged tip with loose edges the test itself wobbles
        // there. Two strong answers that disagree in one cell refuse the body (a stub beyond a cut, a notch, a hole
        // through one wall: material in part of a cell). A strong answer overrules a weak one. Two weak answers that
        // disagree leave the cell unknown, and so does a cell never reached.
        double low = Math.Max(8 * tol, 2 * Math.Max(faceBound, loose));
        var label = new Dictionary<int, (bool Solid, bool Strong, Point3d At)>(); var mixed = new HashSet<int>(); var askedOf = new Dictionary<int, int>();
        int asked = 0; string? refusal = null;
        string Tell(int cell, Point3d x, bool here, Point3d before)
        {
            var free = Enumerable.Range(0, surfaces.Count).Where(k => surfaces[k].Role == CsgRole.Free && !surfaces[k].Virtual).ToArray();
            int Bit(int k) => surfaces.Take(k).Count(f => f.Role == CsgRole.Free);
            string sides = string.Join(", ", free.Select(k => $"{((cell >> Bit(k) & 1) != 0 ? "in" : "out")} {(surfaces[k].Cylinder ? $"cylinder r={surfaces[k].Radius:F2}" : "plane")} {k}"));
            (double H, double Angle, double Rho) Polar(Point3d q) { var d = q - axisPoint; double px = d * frame.XAxis, py = d * frame.YAxis; return (d * axis, Math.Atan2(py, px) * 180 / Math.PI, Math.Sqrt(px * px + py * py)); }
            var a = Polar(x); var b = Polar(before);
            return $"[{sides}] is {(here ? "solid" : "void")} at height {a.H:F2}, angle {a.Angle:F1}, radius {a.Rho:F2} and {(here ? "void" : "solid")} at height {b.H:F2}, angle {b.Angle:F1}, radius {b.Rho:F2}";
        }
        bool Learn(int cell, Point3d x, bool strong, string where, int most)
        {
            if ((asked & 63) == 0) token.ThrowIfCancellationRequested();
            if (mixed.Contains(cell) || askedOf.GetValueOrDefault(cell) >= most && label.TryGetValue(cell, out var seen) && (seen.Strong || !strong)) return true;
            askedOf[cell] = askedOf.GetValueOrDefault(cell) + 1;
            bool here = brep.IsPointInside(x, tol, false); asked++;
            if (!label.TryGetValue(cell, out var held)) { label[cell] = (here, strong, x); return true; }
            if (held.Solid == here) { if (strong && !held.Strong) label[cell] = (here, true, x); return true; }
            if (held.Strong && strong) { refusal = $"material in part of a cell only, seen {where}: the cell {Tell(cell, x, here, held.At)}"; return false; }
            if (strong) label[cell] = (here, true, x);
            else if (!held.Strong) { label.Remove(cell); mixed.Add(cell); }
            return true;
        }
        double Clearance(Point3d x) => Clear0(x);
        // The grid: up to 24 points spread through each cell, its first and last along the axis among them.
        foreach (var (cell, list) in byCell.OrderBy(k => k.Key))
        {
            int take = Math.Min(24, list.Count);
            for (int k = 0; k < take; k++)
            {
                int id = list[take == 1 ? 0 : (int)((long)k * (list.Count - 1) / (take - 1))]; int ir = id % nRho, it = id / nRho % nTheta, ih = id / nRho / nTheta;
                if (!Learn(cell, Sample(ih, it, ir), true, "on the grid", 24)) return no(refusal!);
            }
        }
        // Behind and in front of every face, from just clear of it to well beyond the margin: a small face can bound a
        // cell the grid has no sample in, and a thin cell between the face and the next surface is met at one of the steps.
        foreach (var face in brep.Faces)
        {
            foreach (var (at, normal) in InteriorPoints(face, 9))
                foreach (double side in new[] { -1.0, 1.0 })
                {
                    var steps = new List<double> { 2 * margin };
                    for (double d = 1.25 * low; d < 8 * margin; d *= 1.25) steps.Add(d);
                    foreach (double d in steps)
                    {
                        var x = at + normal * (side * d); double clearance = Clearance(x);
                        if (clearance < low) continue;
                        int cell = probe.Cell(W(x), low, out bool certain, out bool inTube); if (!certain || !inTube) continue;
                        if (!Learn(cell, x, clearance >= margin, $"at face {face.FaceIndex}", 8)) return no(refusal!);
                    }
                }
        }
        // Cells the grid passes through without a certain sample: the sample deepest in each is moved away from its
        // nearest surface a few times, staying in the cell.
        foreach (var (cell, (_, id)) in best.OrderBy(k => k.Key))
        {
            if (label.ContainsKey(cell) || mixed.Contains(cell)) continue;
            int ir = id % nRho, it = id / nRho % nTheta, ih = id / nRho / nTheta; var x = Sample(ih, it, ir);
            for (int round = 0; round < 40 && Clearance(x) < 2 * low; round++)
            {
                int nearest = Enumerable.Range(0, surfaces.Count).OrderBy(k => Math.Abs(Distance(surfaces[k], x))).First(); var f = surfaces[nearest];
                Vector3d away;
                if (f.Cylinder) { away = x - f.Origin; away -= f.Direction * (away * f.Direction); if (!away.Unitize()) break; } else away = f.Direction;
                double d = Distance(f, x); if (d < 0) away = -away;
                var moved = x + away * Math.Max(low / 4, (2 * low - Math.Abs(d)) / 2);
                if (probe.Cell(W(moved), 0, out _, out bool stillIn) != cell || !stillIn) break;
                x = moved;
            }
            double clearance = Clearance(x);
            if (clearance < low || probe.Cell(W(x), low, out bool sure, out bool inTube) != cell || !sure || !inTube) continue;
            if (!Learn(cell, x, clearance >= margin, "on the grid", 2)) return no(refusal!);
        }
        // Cells that meet the body at an edge: rings of points about every edge, at a few radii.
        foreach (var edge in brep.Edges)
        {
            if (edge.AdjacentFaces().Length != 2) continue;
            for (int k = 0; k <= 24; k++)
            {
                double t = edge.Domain.ParameterAt((k + .5) / 25.5); var centre = edge.PointAt(t); var tangent = edge.TangentAt(t);
                if (!tangent.Unitize()) continue; var ring = new Plane(centre, tangent);
                foreach (double radius in new[] { 2 * low, 3 * low, 5 * low, 8 * low, 13 * low })
                    for (int a = 0; a < 16; a++)
                    {
                        var x = ring.PointAt(radius * Math.Cos(a * Math.PI / 8 + .1), radius * Math.Sin(a * Math.PI / 8 + .1)); double clearance = Clearance(x);
                        if (clearance < low) continue;
                        int cell = probe.Cell(W(x), low, out bool certain, out bool inTube); if (!certain || !inTube) continue;
                        if (!Learn(cell, x, clearance >= margin, $"beside edge {edge.EdgeIndex}", 4)) return no(refusal!);
                    }
            }
        }
        // Cells smaller than the grid: along lines through the wall in random directions, every stretch between two
        // consecutive surface crossings is one cell, and its middle is as deep in that cell as the line gets.
        {
            var lines = new Random(20261118); var crossings = new List<double>(); int before = asked;
            for (int n = 0; n < 6000 && asked - before < 600; n++)
            {
                var origin = Sample(lines.Next(nH), lines.Next(nTheta), lines.Next(nRho));
                var d = new Vector3d(lines.NextDouble() * 2 - 1, lines.NextDouble() * 2 - 1, lines.NextDouble() * 2 - 1); if (!d.Unitize()) continue;
                double far = 3 * ro; crossings.Clear(); crossings.Add(-far); crossings.Add(far);
                foreach (var f in surfaces)
                {
                    var w = origin - f.Origin;
                    if (!f.Cylinder) { double dn = d * f.Direction; if (Math.Abs(dn) > 1e-12) crossings.Add(-(w * f.Direction) / dn); continue; }
                    var wa = w - f.Direction * (w * f.Direction); var da = d - f.Direction * (d * f.Direction);
                    double qa = da * da, qb = wa * da, qc = wa * wa - f.Radius * f.Radius, disc = qb * qb - qa * qc;
                    if (qa < 1e-12 || disc <= 0) continue;
                    double root = Math.Sqrt(disc); crossings.Add((-qb - root) / qa); crossings.Add((-qb + root) / qa);
                }
                crossings.Sort();
                for (int k = 0; k + 1 < crossings.Count; k++)
                {
                    double a0 = crossings[k], a1 = crossings[k + 1]; if (a1 <= a0 || a0 < -far || a1 > far || a1 - a0 < 2 * low) continue;
                    var x = origin + d * ((a0 + a1) / 2); double clearance = Clearance(x);
                    int cell = probe.Cell(W(x), low, out bool certain, out bool inTube); if (!certain || !inTube) continue;
                    if (!Learn(cell, x, clearance >= margin, "on a line through the wall", 2)) return no(refusal!);
                }
            }
        }
        var inside = label.Where(k => k.Value.Solid).Select(k => k.Key).OrderBy(c => c).ToList(); var outside = label.Where(k => !k.Value.Solid).Select(k => k.Key).OrderBy(c => c).ToList();
        if (inside.Count == 0 || asked < 24) return no("too few points to compare with the body");
        AnalyticCsg world;
        try { world = new AnalyticCsg(primitives, W(box.Min), W(box.Max), inside, outside); }
        catch (ArgumentException) { return no("cells the exact solid refuses"); }

        // ---- The description against the body once more: both sides of every face, and the volume.
        int uncompared = 0; double uncomparedArea = 0; var slivers = new List<int>();
        foreach (var face in brep.Faces)
        {
            token.ThrowIfCancellationRequested();
            int checks = 0, own = faceSurface[face.FaceIndex], points = 0, clear = 0; string seen = "";
            // Five points first; a face none of them could be compared at gets sixty more before it is given up on.
            foreach (int wanted in new[] { 5, 65, 600 })
            {
                if (checks != 0) break;
                foreach (var (at, normal) in InteriorPoints(face, wanted).Skip(points))
                {
                    var behind = at - normal * (2 * margin); var front = at + normal * (2 * margin); points++;
                    // Only where no other surface is near enough to decide the point instead.
                    if (!Clear(surfaces, own, behind, margin) || !Clear(surfaces, own, front, margin)) continue;
                    clear++; if (clear <= 5) seen += $" [{world.Contains(W(behind), margin)?.ToString() ?? "?"}/{world.Contains(W(front), margin)?.ToString() ?? "?"}]";
                    if (world.Contains(W(behind), margin) == false) return no($"material missing behind face {face.FaceIndex}");
                    if (world.Contains(W(front), margin) == true) return no($"material in front of face {face.FaceIndex}");
                    if (world.Contains(W(behind), margin) == true && world.Contains(W(front), margin) == false) checks++;
                }
            }
            if (checks != 0) continue;
            // No comparison succeeded. Two cases.
            // Every one of up to 600 points of the face is within two margins of another surface: a sliver that hugs
            // that surface (the cut face where two equal pipes pinch out against each other). Point-in-solid answers
            // cannot be trusted that close to two surfaces, so the face cannot be compared by this method whatever
            // its area. Its cells are learned where they are wider, or stay unknown; the face is counted and Info
            // gives the count and area.
            // Or some points are clear but the cells beside them are unknown: let through only when the face is small
            // (no more than sixteen margins square), refused otherwise.
            slivers.Add(face.FaceIndex);
            if (clear != 0 && areas[face.FaceIndex] > 256 * margin * margin)
                return no($"face {face.FaceIndex} could not be checked against the cells (area {areas[face.FaceIndex]:G4}, margin {margin:G3}, interior points {points}, clear of other surfaces {clear}, behind/front{seen})");
            uncompared++; uncomparedArea += areas[face.FaceIndex];
        }
        long solidCells = count.Where(k => inside.Contains(k.Key)).Sum(k => k.Value), unknownCells = count.Where(k => !inside.Contains(k.Key) && !outside.Contains(k.Key)).Sum(k => k.Value);
        double cellVolume = Math.PI * (ro * ro - ri * ri) * length / cells, expected = cellVolume * solidCells;
        if (Math.Abs(volume - expected) > .01 * volume + cellVolume * unknownCells) return no($"volume differs from the cut tube's ({volume:G6} against {expected:G6})");
        if (cellVolume * unknownCells > .01 * volume) return no("too much of the tube in cells that could not be classified");

        // ---- Edges between different surfaces are the lines drawn.
        // Where two faces meet, the real faces do not end on the exact intersection of their surfaces: each runs on to
        // the stored edge (and to its own trim, within the edge's stored tolerance of that). So the real body can
        // differ from the exact one, next to a surface, by the edge's distance from that surface plus the stored
        // tolerances: that is added to the surface's slack, beside its faces' own bound.
        var edges = new List<Edge>(); double edgeBound = 0; var grow = new double[surfaces.Count]; bool transferred = false;
        try
        {
            foreach (var edge in brep.Edges)
            {
                token.ThrowIfCancellationRequested();
                var faces = edge.AdjacentFaces(); if (faces.Length != 2) { if (edge.Valence == EdgeAdjacency.Interior) continue; return no("an edge that is not shared by two faces"); }
                int a = faceSurface[faces[0]], b = faceSurface[faces[1]]; if (a == b) continue;
                using var whole = edge.DuplicateCurve();
                // The edge's distance from each of its two surfaces, bounded through its control points like a face's.
                // (No finer than a sixty-fourth of the body's own looseness: the stored tolerances are added to it anyway.)
                double fine = Math.Max(tol / 8, loose / 64);
                double da = Deviation(whole, surfaces[a], fine, limit), db = Deviation(whole, surfaces[b], fine, limit), slack = Math.Max(da, db);
                if (!(slack <= limit)) return no($"an edge further than {limit:G3} from the surfaces it joins (certified bound {slack:G3})");
                double stored = edge.Tolerance + Math.Max(edge.StartVertex?.Tolerance ?? 0, edge.EndVertex?.Tolerance ?? 0);
                if (!(stored >= 0) || slack + stored > limit) return no($"an edge whose distance from its surfaces and stored tolerance together exceed {limit:G3}");
                grow[a] = Math.Max(grow[a], slack + stored); grow[b] = Math.Max(grow[b], slack + stored);
                // A smooth join between two faces is not a line of the drawing (tangent edges are off whenever this class runs).
                if (edge.IsSmoothManifoldEdge(Math.PI / 180)) continue;
                edgeBound = Math.Max(edgeBound, slack);
                // Where the two surfaces cross cleanly the edge is exact: its points snap onto their intersection, moving
                // by no more than the limit. Runs of the edge where they do not (two cylinders touching where a cope
                // pinches out) stay as stored. The edge is cut into such runs.
                const int parts = 64; var clean = new bool[parts + 1];
                for (int k = 0; k <= parts; k++)
                {
                    var q = whole.PointAt(whole.Domain.ParameterAt((double)k / parts));
                    clean[k] = Onto(surfaces[a], surfaces[b], q, limit) is { } on && on.DistanceTo(q) <= limit;
                }
                for (int from = 0; from < parts;)
                {
                    bool exact = clean[from] && clean[from + 1]; int to = from + 1;
                    while (to < parts && (clean[to] && clean[to + 1]) == exact) to++;
                    Emit(from, to, exact); from = to;
                }
                void Emit(int from, int to, bool exact)
                {
                    var piece = from == 0 && to == parts ? whole.DuplicateCurve() : whole.Trim(whole.Domain.ParameterAt((double)from / parts), whole.Domain.ParameterAt((double)to / parts));
                    if (piece is null) return;
                    // Surface residuals alone do not bound distance to their intersection: divide their SUM by a lower
                    // bound on the crossing sine over the whole piece and its gap neighbourhood. Pinch-outs refuse this
                    // certificate; sampled snap distances are never a substitute for it.
                    double lowerSine = 0;
                    double gap = exact ? CylinderGap(piece, surfaces[a], surfaces[b], da, db, limit) : double.PositiveInfinity;
                    bool closedForm = double.IsFinite(gap);
                    if (exact && !closedForm) gap = IntersectionGap(piece, surfaces[a], surfaces[b], da, db, limit, out lowerSine, token);
                    if (exact && !double.IsFinite(gap) && to - from > 1)
                    {
                        // A pinch-out may be between the initial samples. Localise the failure instead of withholding
                        // the entire otherwise clean rim; even the smallest piece still needs the whole-piece bound.
                        piece.Dispose(); int mid = (from + to) / 2; Emit(from, mid, true); Emit(mid, to, true); return;
                    }
                    exact &= double.IsFinite(gap);
                    if (exact) edgeBound = Math.Max(edgeBound, gap);
                    // An exact piece can also lie in one of the separating planes (the rim where two mitred cutters meet lies
                    // in their mitre plane). The exact curve is then on that plane by construction, like on its own two
                    // surfaces, and which side of it a ray leaves on is a matter of the ray's direction, not of a distance.
                    int on = (1 << a) | (1 << b);
                    if (exact)
                        for (int k = real; k < surfaces.Count; k++)
                        {
                            if (surfaces[k].Cylinder || surfaces[k].Role != CsgRole.Free) continue;
                            bool lies = true;
                            for (int m = from; m <= to && lies; m++)
                                lies = Onto(surfaces[a], surfaces[b], whole.PointAt(whole.Domain.ParameterAt((double)m / parts))) is { } x && Math.Abs(Distance(surfaces[k], x)) <= 1e-9 * (1 + span);
                            if (lies) on |= 1 << k;
                        }
                    edges.Add(new(piece, on, 0, exact, exact ? gap : 0) { Diagnostic = FormattableString.Invariant(
                        $"edge {edge.EdgeIndex}, parts {from}..{to}/64, surfaces {a}/{b}, da {da:R}, db {db:R}, sin lower {lowerSine:R}, gap {gap:R}, exact {exact}, closed form {closedForm}") });
                }
            }
            for (int i = 0; i < real; i++) if (grow[i] > surfaces[i].Slack) surfaces[i] = surfaces[i] with { Slack = grow[i] };

            // ---- Faces the point-in-solid comparison could not reach are compared with the body's own orientation instead.
            // A valid oriented solid has material immediately behind each face and none immediately in front: that is a
            // fact of the Brep, not an answer near two surfaces. Just clear of the face's own slack on each side, the
            // description (every surface known to its slack, so a surface that close may have the point on either side)
            // must allow material behind and must allow void in front. If it says "void behind" or "solid in front" for
            // certain, it is not this body. What the description can then still have wrong beside such a face lies within
            // the slack of the surface the face hugs, where every ray is undecided anyway.
            var uncertainFaces = new List<(int Surface, BoundingBox Bounds, double Slack)>(); var localNotes = new List<string>();
            if (slivers.Count != 0)
            {
                AnalyticCsg stated;
                try { stated = new AnalyticCsg(surfaces.Select(k => new CsgPrimitive(k.Cylinder, W(k.Origin), D(k.Direction), k.Radius, k.Role, k.Slack)).ToArray(), W(box.Min), W(box.Max), inside, outside); }
                catch (ArgumentException) { return no("cells the exact solid refuses"); }
                foreach (int index in slivers)
                {
                    token.ThrowIfCancellationRequested();
                    if (SliverFailure(InteriorPoints(brep.Faces[index], 65), faceSurface[index], surfaces, stated, tol) is not { } failure) continue;
                    // No evidence for this face is no evidence for this face, not a reason to discard the other
                    // faces and their learned cells. Quarantine its complete positive-NURBS control hull, inflated
                    // for stored face/edge slack. The CSG predicate treats the surface band there as unknown and
                    // never uses an OnSurface/contact exemption there. Sure material elsewhere can still hide it.
                    int own = faceSurface[index]; var footprint = FaceHull(brep.Faces[index]);
                    if (!footprint.IsValid) footprint = box;
                    footprint.Inflate(surfaces.Max(s => s.Slack) + 4 * tol);
                    uncertainFaces.Add((own, footprint, surfaces[own].Slack + 4 * tol));
                    localNotes.Add($"face {index} locally uncertain: {failure}");
                }
            }
            int unknownKinds = count.Keys.Count(k => !inside.Contains(k) && !outside.Contains(k)) + mixed.Count(k => !count.ContainsKey(k));
            var result = new AnalyticVisibility.CutTubeShape(surfaces, box, edges, faceSurface, real, faceBound, edgeBound, inside, outside)
                { Note = $"cells: {inside.Count} solid, {outside.Count} void, {unknownKinds} unknown holding {100.0 * unknownCells / cells:F3} % of the tube's wall; {asked} points asked; {mixed.Count} cells left unknown because weak answers disagreed; {uncompared} faces ({uncomparedArea:G3} in area) too close to another surface for point-in-solid answers, orientation evidence only, not proof; surface slack up to {surfaces.Max(k => k.Slack):G3}",
                  Unchecked = uncompared, SurfaceBound = surfaces.Max(k => k.Slack), UncertainFaces = uncertainFaces };
            if (localNotes.Count != 0) result = result with { Note = result.Note + "; " + string.Join("; ", localNotes) };
            transferred = true; return result;
        }
        finally { if (!transferred) foreach (var edge in edges) edge.World.Dispose(); }
    }

    private static BoundingBox FaceHull(BrepFace face)
    {
        using var copy = face.DuplicateFace(false); if (copy is null) return BoundingBox.Unset;
        copy.Faces.ShrinkFaces(); using var n = copy.Faces[0].UnderlyingSurface()?.ToNurbsSurface();
        if (n is null) return BoundingBox.Unset;
        var points = new List<Point3d>();
        for (int i = 0; i < n.Points.CountU; i++)
            for (int j = 0; j < n.Points.CountV; j++)
            {
                var point = n.Points.GetControlPoint(i, j); if (!(point.Weight > 0) || !double.IsFinite(point.Weight)) return BoundingBox.Unset;
                points.Add(point.Location);
            }
        return new BoundingBox(points);
    }

    private static (Vector3d Side, Vector3d Normal, double Along)? CylinderFrame(Surface a, Surface b)
    {
        if (!a.Cylinder || !b.Cylinder) return null;
        var side = Vector3d.CrossProduct(a.Direction, b.Direction); if (!side.Unitize()) return null;
        var normal = Vector3d.CrossProduct(side, b.Direction); if (!normal.Unitize()) return null;
        double along = normal * a.Direction; if (Math.Abs(along) < 1e-6) return null;
        return (side, normal, along);
    }

    /// <summary>Closed-form cylinder/cylinder certificate, including tangent branch endpoints. First put q on A
    /// radially (move at most da), then move along A's axis. In B's perpendicular frame this changes only x:
    /// x' = +/-sqrt(rb^2-y^2). With e=da+db, |x^2-x'^2| <= e(2rb+e), hence the correction is bounded by both
    /// N/(|x|+|x'|) and sqrt(N). The latter remains finite at a pinch-out; there is no inverse crossing-angle
    /// assumption. Positive NURBS control hulls bound y and x over the complete piece. Both axes are tried.</summary>
    internal static double CylinderGap(Curve curve, Surface a, Surface b, double da, double db, double limit)
    {
        double One(Surface first, Surface second, double residual)
        {
            if (CylinderFrame(first, second) is not { } frame) return double.PositiveInfinity;
            using var n = curve.ToNurbsCurve();
            if (n is null || n.Points.Any(p => !(p.Weight > 0) || !double.IsFinite(p.Weight))) return double.PositiveInfinity;
            double y0 = double.PositiveInfinity, y1 = double.NegativeInfinity, x0 = y0, x1 = y1;
            foreach (var p in n.Points)
            {
                var w = p.Location - second.Origin; double y = w * frame.Side, x = w * frame.Normal;
                y0 = Math.Min(y0, y); y1 = Math.Max(y1, y); x0 = Math.Min(x0, x); x1 = Math.Max(x1, x);
            }
            double extent = n.Points.Max(p => p.Location.DistanceTo(first.Origin) + p.Location.DistanceTo(second.Origin));
            double guard = 1e-12 * (1 + first.Origin.DistanceTo(Point3d.Origin) + second.Origin.DistanceTo(Point3d.Origin) + first.Radius + second.Radius + extent);
            double shift = (first.Origin - second.Origin) * frame.Side;
            // A's radial projection is exactly on its circle; intersect that range with the moved hull's range.
            y0 = Math.Max(y0 - residual - guard, shift - first.Radius - guard);
            y1 = Math.Min(y1 + residual + guard, shift + first.Radius + guard);
            if (!(y0 <= y1)) return double.PositiveInfinity;
            double far = Math.Max(Math.Abs(y0), Math.Abs(y1));
            // Coincident exact side extents need no floating padding beyond B's radius: the circle itself proves
            // this domain when both axes have the identical stored origin and the first radius is no larger.
            // A rounded zero dot product of two different origins is not an incidence certificate.
            if (first.Origin == second.Origin && first.Radius <= second.Radius) far = Math.Min(far, first.Radius);
            if (!(far <= second.Radius)) return double.PositiveInfinity;
            double root = Math.Sqrt(Math.Max(0, (second.Radius - far) * (second.Radius + far)));
            x0 -= residual + guard; x1 += residual + guard;
            double near = x0 > 0 ? x0 : x1 < 0 ? -x1 : 0;
            double e = da + db + guard, numerator = e * (2 * second.Radius + e);
            double delta = Math.Min(Math.Sqrt(numerator), near + root > 0 ? numerator / (near + root) : double.PositiveInfinity);
            return residual + delta / Math.Abs(frame.Along) + guard;
        }
        if (!(da >= 0 && db >= 0)) return double.PositiveInfinity;
        double gap = Math.Min(One(a, b, da), One(b, a, db));
        return gap <= limit ? gap : double.PositiveInfinity;
    }

    private static Point3d? CylinderPoint(Surface a, Surface b, Point3d q)
    {
        if (CylinderFrame(a, b) is not { } frame) return null;
        var radial = q - a.Origin; radial -= a.Direction * (radial * a.Direction); if (!radial.Unitize()) return null;
        var on = q + radial * -Distance(a, q); var w = on - b.Origin;
        double y = w * frame.Side, x = w * frame.Normal, square = (b.Radius - Math.Abs(y)) * (b.Radius + Math.Abs(y));
        if (square < 0) return null;
        double root = Math.CopySign(Math.Sqrt(square), x);
        var p = on + a.Direction * ((root - x) / frame.Along);
        return Math.Abs(Distance(a, p)) + Math.Abs(Distance(b, p)) <= 1e-10 * (1 + a.Radius + b.Radius) ? p : null;
    }

    /// <summary>No interior point, or a probe that can still be in another surface's slack band, supplies no
    /// orientation evidence. In particular May=(true,true) in that band must not let an unchecked sliver through.</summary>
    internal static bool CheckSliver(IEnumerable<(Point3d At, Vector3d Normal)> points, int own,
        IReadOnlyList<Surface> surfaces, AnalyticCsg stated, double tol) => SliverFailure(points, own, surfaces, stated, tol) is null;

    internal static string? SliverFailure(IEnumerable<(Point3d At, Vector3d Normal)> points, int own,
        IReadOnlyList<Surface> surfaces, AnalyticCsg stated, double tol)
    {
        int found = 0; double off = surfaces[own].Slack + 4 * tol;
        foreach (var (at, normal) in points)
        {
            found++;
            var behind = at - normal * off; var front = at + normal * off;
            for (int i = 0; i < surfaces.Count; i++)
            {
                if (i == own || surfaces[i].Virtual) continue;
                double db = Math.Abs(Distance(surfaces[i], behind)), df = Math.Abs(Distance(surfaces[i], front));
                double guard = 1e-12 * (1 + at.DistanceTo(surfaces[i].Origin) + surfaces[i].Radius);
                // Test the actual probes against the other surface's certified band. The scalar sum-of-slacks
                // test is only a broad phase: at an oblique crossing it can reject probes that are both clear.
                if (Math.Min(db, df) <= surfaces[i].Slack + guard)
                    return FormattableString.Invariant($"overlapping slack bands at sample {found}, surfaces {own}/{i}, distance {Math.Abs(Distance(surfaces[i], at)):R}, own slack {surfaces[own].Slack:R}, other slack {surfaces[i].Slack:R}, threshold {off + surfaces[i].Slack:R}, probe distances {db:R}/{df:R}");
            }
            if (!stated.May(new(behind.X, behind.Y, behind.Z)).Solid || !stated.May(new(front.X, front.Y, front.Z)).Void) return "orientation contradicts cells";
        }
        return found == 0 ? "no interior sample (found=0)" : null;
    }

    /// <summary>Certified edge-to-intersection distance. For unit signed-distance gradients, the least correction
    /// has speed at most (|da|+|db|)/sin(theta). Plane curvature is zero, cylinder curvature is 1/rho. Bound the
    /// closed-form gradient cross product between samples by that curvature and the positive NURBS control hull.
    /// The same bound holds in the returned distance's neighbourhood, so integrating the correction to zero residual
    /// cannot escape that neighbourhood. An angle below asin(.01), an axis, or a work-cap hit fails closed.</summary>
    internal static double IntersectionGap(Curve curve, Surface a, Surface b, double da, double db, double limit,
        CancellationToken token = default) => IntersectionGap(curve, a, b, da, db, limit, out _, token);

    private static double IntersectionGap(Curve curve, Surface a, Surface b, double da, double db, double limit,
        out double lowerSine, CancellationToken token)
    {
        lowerSine = 0;
        const double threshold = .01;
        if (!(da >= 0 && db >= 0 && double.IsFinite(da + db))) return double.PositiveInfinity;
        static Vector3d Normal(Surface s, Point3d q)
        {
            if (!s.Cylinder) return s.Direction;
            var n = q - s.Origin; n -= s.Direction * (n * s.Direction); n.Unitize(); return n;
        }
        double Sine(double neighbourhood)
        {
            double minimum = 1; int samples = 0;
            var stack = new Stack<Interval>(); stack.Push(curve.Domain);
            while (stack.TryPop(out var interval))
            {
                token.ThrowIfCancellationRequested(); if (++samples > 16384) return 0;
                var at = curve.PointAt(interval.Mid); var na = Normal(a, at); var nb = Normal(b, at);
                double sine = Vector3d.CrossProduct(na, nb).Length;
                if (!(na.SquareLength > .5 && nb.SquareLength > .5 && sine >= threshold)) return 0;
                using var part = curve.Trim(interval); using var n = part?.ToNurbsCurve();
                if (n is null || n.Points.Any(p => !(p.Weight > 0) || !double.IsFinite(p.Weight))) return 0;
                double travel = n.Points.Max(p => p.Location.DistanceTo(at));
                double rhoA = a.Radius - da - travel - neighbourhood, rhoB = b.Radius - db - travel - neighbourhood;
                double curvature = (a.Cylinder ? rhoA > 0 ? 1 / rhoA : double.PositiveInfinity : 0) +
                    (b.Cylinder ? rhoB > 0 ? 1 / rhoB : double.PositiveInfinity : 0);
                double variation = curvature * (travel + neighbourhood), lower = sine - variation - 1e-12;
                // Refine the along-curve part; subdivision cannot reduce the neighbourhood's contribution.
                if (lower >= threshold && curvature * travel <= .01 * sine)
                { minimum = Math.Min(minimum, lower); continue; }
                if (double.IsFinite(curvature) && curvature * neighbourhood > sine - threshold) return 0;
                double mid = interval.Mid; if (!(mid > interval.Min && mid < interval.Max)) return 0;
                stack.Push(new(mid, interval.Max)); stack.Push(new(interval.Min, mid));
            }
            return minimum;
        }
        double lower = Sine(0); lowerSine = lower; if (!(lower >= threshold)) return double.PositiveInfinity;
        double gap = (da + db) / lower;
        for (int round = 0; round < 12; round++)
        {
            if (!(gap <= limit)) return double.PositiveInfinity;
            lower = Sine(gap); lowerSine = lower; if (!(lower >= threshold)) return double.PositiveInfinity;
            double floor = (da + db) / lower;
            if (floor <= gap) return gap;
            gap = floor * 1.0625;
        }
        return double.PositiveInfinity;
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
                if (k > 0 && Math.Abs(c.R - radius / k) > 64 * tol) { round = false; break; }      // a cone is not a cylinder
                centres.Add(c.C); radius += c.R;
            }
            if (!round) continue; radius /= 3;
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
    internal static Point3d? Onto(Surface a, Surface b, Point3d q, double maxMove = double.PositiveInfinity)
    {
        if (a.Cylinder && b.Cylinder)
        {
            var ca = CylinderPoint(a, b, q); var cb = CylinderPoint(b, a, q);
            if (ca is { } pa && pa.DistanceTo(q) <= maxMove && (cb is not { } pb || pa.DistanceTo(q) <= pb.DistanceTo(q))) return pa;
            if (cb is { } second && second.DistanceTo(q) <= maxMove) return second;
        }
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
            // A face that really is that far from its surface (a NURBS near-cylinder): the bound settles on the true
            // distance and further halving only confirms it.
            if (round >= 2 && bound > previous * .97) break;
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

    /// <summary>
    /// A certified bound on how far a curve is from a surface, by the same argument as for a face: the curve is in the
    /// hull of its control points span by span, distance to a plane is linear and distance to an axis is convex.
    /// Infinity when no bound can be given.
    /// </summary>
    internal static double Deviation(Curve curve, Surface s, double target, double limit)
    {
        using var nurbs = curve.ToNurbsCurve(); if (nurbs is null) return double.PositiveInfinity;
        var direction = s.Direction; if (!direction.Unitize()) return double.PositiveInfinity;
        double bound = double.PositiveInfinity, previous = double.PositiveInfinity;
        for (int round = 0; round < 14; round++)
        {
            int n = nurbs.Points.Count, degree = nurbs.Degree; var across = new Vector3d[n]; bound = 0;
            for (int i = 0; i < n; i++)
            {
                var point = nurbs.Points[i]; if (!(point.Weight > 0) || !double.IsFinite(point.Weight)) return double.PositiveInfinity;
                var w = point.Location - s.Origin;
                if (!s.Cylinder) bound = Math.Max(bound, Math.Abs(w * direction)); else across[i] = w - direction * (w * direction);
            }
            if (s.Cylinder)
                for (int i = 0; i + degree < n; i++)
                {
                    var mean = Vector3d.Zero; double far = 0;
                    for (int k = 0; k <= degree; k++) { mean += across[i + k]; far = Math.Max(far, across[i + k].Length); }
                    if (!mean.Unitize()) return double.PositiveInfinity;
                    double near = double.PositiveInfinity;
                    for (int k = 0; k <= degree; k++) near = Math.Min(near, across[i + k] * mean);
                    bound = Math.Max(bound, Math.Max(far - s.Radius, s.Radius - near));
                }
            if (bound <= target || !s.Cylinder && nurbs.Degree == 1) break;          // against a plane a polyline's bound is exact
            if (round >= 3 && bound > limit && bound > previous * .5) break;
            if (round >= 2 && bound > previous * .97) break;
            if (n > 4096) break;
            previous = bound;
            var values = new List<double>();
            for (int k = 0; k < nurbs.Knots.Count; k++) if (values.Count == 0 || nurbs.Knots[k] > values[^1]) values.Add(nurbs.Knots[k]);
            for (int k = 0; k + 1 < values.Count; k++) nurbs.Knots.InsertKnot((values[k] + values[k + 1]) / 2, 1);
        }
        return bound;
    }

    /// <summary>A certified upper bound on how far from an axis a curve gets (its control points, refined until the
    /// bound is under <paramref name="enough"/> or no longer improves).</summary>
    private static double Reach(Curve curve, Point3d origin, Vector3d axis, double enough)
    {
        using var nurbs = curve.ToNurbsCurve(); if (nurbs is null || !axis.Unitize()) return double.PositiveInfinity;
        double bound = double.PositiveInfinity, previous = double.PositiveInfinity;
        for (int round = 0; round < 12; round++)
        {
            bound = 0;
            foreach (var point in nurbs.Points)
            {
                if (!(point.Weight > 0) || !double.IsFinite(point.Weight)) return double.PositiveInfinity;
                var w = point.Location - origin; bound = Math.Max(bound, (w - axis * (w * axis)).Length);
            }
            if (bound <= enough || nurbs.Degree == 1 || round >= 2 && bound > previous - (previous - enough) * .25 || nurbs.Points.Count > 20000) break;
            previous = bound;
            var values = new List<double>();
            for (int k = 0; k < nurbs.Knots.Count; k++) if (values.Count == 0 || nurbs.Knots[k] > values[^1]) values.Add(nurbs.Knots[k]);
            for (int k = 0; k + 1 < values.Count; k++) nurbs.Knots.InsertKnot((values[k] + values[k + 1]) / 2, 1);
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
