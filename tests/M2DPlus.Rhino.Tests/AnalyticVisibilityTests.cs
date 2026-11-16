using System.Drawing;
using M2DPlus.Core;
using M2DPlus.Rhino.Services;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using Xunit;
using Xunit.Abstractions;

namespace M2DPlus.Rhino.Tests;

/// <summary>Exact analytic solids: strict recognition, and their composition with the planar kernel and native HLD.</summary>
[Collection("Rhino")]
[Trait("Category", "RhinoIntegration")]
public sealed class AnalyticVisibilityTests(RhinoHost host, ITestOutputHelper output)
{
    private const double Tol = 1e-6;

    private static Brep Column(Point3d foot, double radius, double height) =>
        new Cylinder(new Circle(new Plane(foot, Vector3d.ZAxis), radius), height).ToBrep(true, true);
    private static Brep Tube(Point3d foot, double radius, double inner, double height)
    {
        using var outer = Column(foot, radius, height); using var bore = Column(foot - Vector3d.ZAxis, inner, height + 2);
        return Assert.Single(Brep.CreateBooleanDifference(outer, bore, Tol));
    }
    private static Brep Plate(double x0, double y0, double z0, double x1, double y1, double z1) => Brep.CreateFromBox(new BoundingBox(new Point3d(x0, y0, z0), new Point3d(x1, y1, z1)));
    private static Brep Ring(Point3d centre, double major, double minor) => new Torus(new Plane(centre, Vector3d.ZAxis), major, minor).ToRevSurface().ToBrep();

    private static ViewDrawing Route(GeometryBase[] geometry, NativeSettings? settings = null, ViewRequest? view = null)
    {
        var result = new ViewDrawing { Frame = new Rectangle3d(Plane.WorldXY, new Interval(-10, 10), new Interval(-10, 10)), Tolerance = Tol };
        var input = geometry.Select((g, i) => new VisibilityInput(g, new PreparedLeaf(i.ToString(), [i], g))).ToArray();
        string route = ComposedVisibility.Solve(input, result, view ?? ViewRequest.Parse("Top"), settings ?? new(), default);
        result.Report = new(route, result.Items.Count(p => p.Class == LineClass.Unresolved), result.RoutingInvalid, new Dictionary<string, int>(), [], result.RoutingQuarantine);
        return result;
    }
    private static double Length(ViewDrawing view, LineClass kind, string? source = null) => view.Items.Where(p => p.Class == kind && (source is null || p.SourceIdentity == source))
        .Sum(p => { using var g = p.CopyGeometry(); return g is Curve c ? c.GetLength() : 0; });
    private static ViewRequest SettingsFor(ViewRequest view, params string[] lines)
    {
        var context = new ViewSettingsContext("view", view.Name, view.Kind, view.Cut is not null, 10, .00001, null);
        var settings = Settings.Resolve(new[] { context }, new[] { new[] { "depth=10", "beyond=off", "farclip=none" }.Concat(lines).ToArray() }, DocumentUnits.Metres);
        Assert.False(settings.HasErrors, string.Join(";", settings.Diagnostics));
        return view with { Settings = settings.Views[0] };
    }

    // ---------------------------------------------------------------- recognition

    [RhinoFact] public void RecognisesSphereCylinderTubeAndExtrudedArcWithTheirDimensions() => host.Run(() =>
    {
        using var ball = new Sphere(new Point3d(1, 2, 3), 2).ToBrep();
        var sphere = Assert.IsType<AnalyticVisibility.SphereShape>(AnalyticVisibility.Recognise(ball, Tol));
        Assert.Equal(2, sphere.Radius, 9); Assert.True(sphere.Centre.DistanceTo(new Point3d(1, 2, 3)) < 1e-9);

        using var column = Column(new Point3d(0, 0, 1), .5, 3);
        var solid = Assert.IsType<AnalyticVisibility.CylinderShape>(AnalyticVisibility.Recognise(column, Tol));
        Assert.Equal(.5, solid.Radius, 9); Assert.Equal(0, solid.InnerRadius); Assert.Equal(1, Math.Abs(solid.Axis * Vector3d.ZAxis), 9);
        Assert.Equal(new[] { 1.0, 4.0 }, new[] { solid.Cap0.Origin.Z, solid.Cap1.Origin.Z }.Order().Select(z => Math.Round(z, 9)));
        // The cap normals point out of the solid.
        Assert.True((solid.Cap1.Origin - solid.Cap0.Origin) * solid.Cap1.Normal > 0 && (solid.Cap0.Origin - solid.Cap1.Origin) * solid.Cap0.Normal > 0);

        using var tube = Tube(Point3d.Origin, 1, .7, 2);
        var hollow = Assert.IsType<AnalyticVisibility.CylinderShape>(AnalyticVisibility.Recognise(tube, Tol));
        Assert.Equal(1, hollow.Radius, 9); Assert.Equal(.7, hollow.InnerRadius, 9);

        using var extrusion = Extrusion.CreateCylinderExtrusion(new Cylinder(new Circle(Plane.WorldXY, .4), 2), true, true);
        Assert.IsType<AnalyticVisibility.CylinderShape>(AnalyticVisibility.Recognise(extrusion, Tol));

        using var arc = new ArcCurve(new Arc(Plane.WorldXY, 3, Math.PI / 3)); using var wall = Surface.CreateExtrusion(arc, Vector3d.ZAxis * 2).ToBrep();
        var sheet = Assert.IsType<AnalyticVisibility.SheetShape>(AnalyticVisibility.Recognise(wall, Tol));
        Assert.Equal(3, sheet.Radius, 9); Assert.Equal(Math.PI / 3, sheet.Sweep, 7); Assert.Equal(2, sheet.Height1 - sheet.Height0, 9);

        // A cylinder with an oblique end: still one cylinder between two planes.
        using var longColumn = Column(Point3d.Origin, .5, 6);
        var cutter = new Plane(new Point3d(0, 0, 3), new Vector3d(.3, 0, 1));
        using var trimmed = longColumn.Trim(cutter, Tol).OrderByDescending(b => b.GetBoundingBox(true).Min.Z).Last();
        using var capped = trimmed.CapPlanarHoles(Tol);
        var mitred = Assert.IsType<AnalyticVisibility.CylinderShape>(AnalyticVisibility.Recognise(capped, Tol));
        Assert.Contains(new[] { mitred.Cap0, mitred.Cap1 }, c => c.Normal.IsParallelTo(cutter.Normal, 1e-9) != 0);
    });

    [RhinoFact] public void RecognitionSurvivesRigidMirroredAndAxialTransformsAndRefusesShear() => host.Run(() =>
    {
        foreach (var (name, transform, expected) in new (string, Transform, bool)[]
        {
            ("moved and rotated", Transform.Translation(3, -2, 5) * Transform.Rotation(1.1, new Vector3d(1, 2, 3), Point3d.Origin), true),
            ("mirrored", Transform.Mirror(new Plane(new Point3d(.3, 0, 0), new Vector3d(1, .2, .1))), true),
            ("scaled uniformly", Transform.Scale(new Point3d(1, 1, 1), 2.5), true),
            ("stretched along the axis", Transform.Scale(Plane.WorldXY, 1, 1, 3), true),
            ("squashed across the axis", Transform.Scale(Plane.WorldXY, 1, 2, 1), false),
            ("sheared", Transform.Shear(Plane.WorldXY, Vector3d.XAxis, Vector3d.YAxis + Vector3d.XAxis * .3, Vector3d.ZAxis), false),
        })
        {
            // As NURBS surfaces, so that Rhino really applies a non-similar transform instead of keeping the revolved form.
            using var tube = Tube(new Point3d(.2, .1, 0), 1, .7, 2); Assert.True(tube.MakeDeformable(), name); Assert.True(tube.Transform(transform), name);
            var shape = AnalyticVisibility.Recognise(tube, Tol);
            Assert.True(expected == shape is AnalyticVisibility.CylinderShape, name + ": " + shape);
        }
        using var ball = new Sphere(Point3d.Origin, 1).ToBrep(); Assert.True(ball.MakeDeformable()); Assert.True(ball.Transform(Transform.Scale(Plane.WorldXY, 1, 1, 1.5)));
        Assert.Null(AnalyticVisibility.Recognise(ball, Tol));
        using var mirroredBall = new Sphere(new Point3d(2, 0, 0), 1).ToBrep(); Assert.True(mirroredBall.Transform(Transform.Mirror(Plane.WorldYZ)));
        var mirrored = Assert.IsType<AnalyticVisibility.SphereShape>(AnalyticVisibility.Recognise(mirroredBall, Tol));
        Assert.True(mirrored.Centre.DistanceTo(new Point3d(-2, 0, 0)) < 1e-9);
    });

    [RhinoFact] public void RefusesEverythingThatIsNotExactlyOneOfTheShapes() => host.Run(() =>
    {
        var refused = new Dictionary<string, Func<GeometryBase>>
        {
            ["box"] = () => Plate(0, 0, 0, 1, 1, 1),
            ["cone"] = () => new Cone(Plane.WorldXY, 2, 1).ToBrep(true),
            ["torus"] = () => Ring(Point3d.Origin, 2, .5),
            ["cylinder with one cap missing"] = () => new Cylinder(new Circle(Plane.WorldXY, 1), 2).ToBrep(true, false),
            ["half a cylinder, cut along its axis"] = () =>
            {
                using var column = Column(Point3d.Origin, 1, 2); using var half = column.Trim(new Plane(Point3d.Origin, Vector3d.XAxis), Tol)[0];
                return half.CapPlanarHoles(Tol);
            },
            ["cylinder with a cross hole"] = () =>
            {
                using var column = Column(Point3d.Origin, 1, 2);
                using var drill = new Cylinder(new Circle(new Plane(new Point3d(-3, 0, 1), Vector3d.XAxis), .2), 6).ToBrep(true, true);
                return Assert.Single(Brep.CreateBooleanDifference(column, drill, Tol));
            },
            ["two cylinders joined end to end with different radii"] = () =>
            {
                using var a = Column(Point3d.Origin, 1, 1); using var b = Column(new Point3d(0, 0, 1), .5, 1);
                return Assert.Single(Brep.CreateBooleanUnion(new[] { a, b }, Tol));
            },
            ["hemisphere"] = () =>
            {
                using var ball = new Sphere(Point3d.Origin, 1).ToBrep(); using var half = ball.Trim(Plane.WorldXY, Tol)[0];
                return half.CapPlanarHoles(Tol);
            },
            ["planar arc wall with thickness"] = () =>
            {
                using var outer = new ArcCurve(new Arc(Plane.WorldXY, 3, 1)); using var surface = Surface.CreateExtrusion(outer, Vector3d.ZAxis * 2);
                return Brep.CreateFromOffsetFace(surface.ToBrep().Faces[0], .2, Tol, false, true);
            },
            ["mesh"] = () => Mesh.CreateFromCylinder(new Cylinder(new Circle(Plane.WorldXY, 1), 2), 4, 16),
            ["curve"] = () => new ArcCurve(new Circle(Plane.WorldXY, 1)),
        };
        foreach (var (name, make) in refused)
        {
            using var geometry = make();
            Assert.True(AnalyticVisibility.Recognise(geometry, Tol) is null, name);
        }
    });

    [RhinoFact] public void RefusalsThatLookLikePipesAndTheReasonGiven() => host.Run(() =>
    {
        const double tol = 1e-4;
        Brep Drilled(Brep body, Point3d at, Vector3d along, double radius, double length)
        {
            using var drill = new Cylinder(new Circle(new Plane(at, along), radius), length).ToBrep(true, true);
            return Assert.Single(Brep.CreateBooleanDifference(body, drill, tol));
        }
        Brep Blended(BlendType type)
        {
            using var column = Column(Point3d.Origin, 1, 3);
            var rim = column.Edges.Where(e => e.AdjacentFaces().Length == 2 && e.PointAtStart.Z > 2.9).Select(e => e.EdgeIndex).ToArray();
            Assert.NotEmpty(rim);
            return Assert.Single(Brep.CreateFilletEdges(column, rim, rim.Select(_ => .2).ToArray(), rim.Select(_ => .2).ToArray(), type, RailType.RollingBall, tol));
        }
        Brep Dented(double depth)
        {   // A NURBS cylinder with one interior control point pushed in by `depth`, capped.
            using var surface = NurbsSurface.CreateFromCylinder(new Cylinder(new Circle(Plane.WorldXY, 1), 3));
            bool heightIsV = surface.IsClosed(0);
            Assert.True(heightIsV ? surface.IncreaseDegreeV(3) : surface.IncreaseDegreeU(3));
            var (u, v) = heightIsV ? (0, 1) : (1, 0); var cv = surface.Points.GetControlPoint(u, v);
            var at = cv.Location; var inward = new Vector3d(-at.X, -at.Y, 0); Assert.True(inward.Unitize());
            Assert.True(surface.Points.SetControlPoint(u, v, new ControlPoint(at + inward * depth, cv.Weight)));
            using var open = surface.ToBrep(); return open.CapPlanarHoles(tol);
        }
        var refused = new Dictionary<string, (Func<Brep> Make, string Why)>
        {
            ["tube with an off-centre bore"] = (() => { using var c = Column(Point3d.Origin, 1, 3); return Drilled(c, new Point3d(.2, 0, -1), Vector3d.ZAxis, .5, 5); }, "different axes"),
            ["two coaxial cylinders with a gap, one Brep"] = (() =>
            {
                var a = Column(Point3d.Origin, 1, 1); using var b = Column(new Point3d(0, 0, 2), 1, 1); a.Append(b); return a;
            }, ""),
            ["filleted end"] = (() => Blended(BlendType.Fillet), "not a plane, cylinder or sphere"),
            ["chamfered end"] = (() => Blended(BlendType.Chamfer), "not a plane, cylinder or sphere"),
            ["NURBS cylinder dented by five tolerances"] = (() => Dented(5 * tol * 4), ""),
            ["open tube of two cylindrical faces"] = (() =>
            {
                using var tube = Tube(Point3d.Origin, 1, .7, 2);
                var open = tube.DuplicateSubBrep(tube.Faces.Where(f => !f.IsPlanar(tol)).Select(f => f.FaceIndex));
                Assert.False(open.IsSolid); Assert.Equal(2, open.Faces.Count); return open;
            }, "open Brep"),
            ["arc wall with a hole"] = (() =>
            {
                using var arc = new ArcCurve(new Arc(Plane.WorldXY, 3, 1)); using var wall = Surface.CreateExtrusion(arc, Vector3d.ZAxis * 2).ToBrep();
                var mid = arc.PointAt(arc.Domain.Mid); var outward = new Vector3d(mid.X, mid.Y, 0); outward.Unitize();
                using var drill = new Cylinder(new Circle(new Plane(new Point3d(mid.X, mid.Y, 1) - outward, outward), .2), 2).ToBrep(true, true);
                var pieces = wall.Split(drill, tol); Assert.True(pieces.Length >= 2);
                var kept = pieces.OrderByDescending(b => b.GetArea()).First(); foreach (var other in pieces) if (!ReferenceEquals(other, kept)) other.Dispose();
                return kept;
            }, "extruded arc"),
            ["end planes meeting inside the cylinder"] = (() =>
            {
                using var column = Column(Point3d.Origin, .5, 4);
                // Keep the part under an oblique plane that runs out through the bottom cap inside the cylinder.
                var plane = new Plane(new Point3d(0, 0, .1), new Vector3d(1, 0, .3));
                using var kept = column.Trim(plane, tol).OrderBy(b => b.GetArea()).First();
                var solid = kept.CapPlanarHoles(tol); Assert.Equal(2, solid.Faces.Count(f => f.IsPlanar(tol))); return solid;
            }, "meet inside"),
            ["cap with bolt holes"] = (() => { using var c = Column(Point3d.Origin, 1, 1); return Drilled(c, new Point3d(.6, 0, .7), Vector3d.ZAxis, .1, 1); }, ""),
            ["cylinder with a hole right through, parallel to the axis"] = (() => { using var c = Column(Point3d.Origin, 1, 1); return Drilled(c, new Point3d(.6, 0, -1), Vector3d.ZAxis, .1, 3); }, "different axes"),
        };
        foreach (var (name, (make, why)) in refused)
        {
            using var brep = make(); Assert.True(brep.IsValid, name);
            var (shape, refusal) = AnalyticVisibility.Examine(brep, tol);
            output.WriteLine($"{name}: {refusal}");
            Assert.True(shape is null, name); Assert.False(string.IsNullOrEmpty(refusal), name); Assert.Contains(why, refusal);
        }

        using (var dented = Dented(.2 * tol))
        {   // A dent of a fifth of a tolerance: within tolerance of the cylinder, but Rhino no longer calls the face a
            // cylinder, so it is refused as well. Stricter than the contract needs, and on the safe side.
            var (found, refusal) = AnalyticVisibility.Examine(dented, tol); output.WriteLine($"dent of a fifth of a tolerance: {found?.ToString() ?? refusal}");
            Assert.True(found is null || found is AnalyticVisibility.CylinderShape { Radius: > .999 and < 1.001 });
        }

        // Accepted: the same solids, described differently.
        using (var column = Column(Point3d.Origin, 1, 3))
        {   // The side and both caps split in two by a plane through the axis, then joined again.
            using var cutter = new PlaneSurface(new Plane(Point3d.Origin, Vector3d.XAxis), new Interval(-5, 5), new Interval(-5, 5)).ToBrep();
            var pieces = column.Split(cutter, tol); Assert.Equal(2, pieces.Length);
            using var joined = Assert.Single(Brep.JoinBreps(pieces, tol)); foreach (var piece in pieces) piece.Dispose();
            Assert.True(joined.IsSolid); Assert.True(joined.Faces.Count >= 6);
            var shape = Assert.IsType<AnalyticVisibility.CylinderShape>(AnalyticVisibility.Recognise(joined, tol)); Assert.Equal(1, shape.Radius, 9);
        }
        using (var inside = Column(Point3d.Origin, 1, 3))
        {   // An inside-out solid (as a mirrored instance is) is the same set of points: same shape.
            inside.Flip(); Assert.Equal(BrepSolidOrientation.Inward, inside.SolidOrientation);
            Assert.IsType<AnalyticVisibility.CylinderShape>(AnalyticVisibility.Recognise(inside, tol));
        }
        using (var pipe = Tube(new Point3d(65000, 40000, 15000), 108.15, 102.15, 2500))
        {   // An exported pipe: the bore's axis differs from the wall's by 3e-7 rad (0.75 micrometres over its length).
            // Compared as an angle that was refused; as a distance over the body it is the same axis.
            using var surface = NurbsSurface.CreateFromCylinder(new Cylinder(new Circle(new Plane(new Point3d(65000, 40000, 15000), Vector3d.ZAxis), 102.15), 2500));
            Assert.True(surface.Rotate(3e-7, Vector3d.XAxis, new Point3d(65000, 40000, 16250)));
            using var outer = Column(new Point3d(65000, 40000, 15000), 108.15, 2500);
            using var bore = surface.ToBrep().CapPlanarHoles(1e-3);
            using var long_ = Column(new Point3d(65000, 40000, 14000), 102.15, 4500); long_.Rotate(3e-7, Vector3d.XAxis, new Point3d(65000, 40000, 16250));
            using var exported = Assert.Single(Brep.CreateBooleanDifference(outer, long_, 1e-3));
            var shape = Assert.IsType<AnalyticVisibility.CylinderShape>(AnalyticVisibility.Recognise(exported, 1e-3));
            Assert.Equal(108.15, shape.Radius, 6); Assert.Equal(102.15, shape.InnerRadius, 6);
            // A real kink of the same kind, a hundred times larger, is refused.
            using var bent = Column(new Point3d(65000, 40000, 14000), 102.15, 4500); bent.Rotate(3e-5, Vector3d.XAxis, new Point3d(65000, 40000, 16250));
            using var kinked = Assert.Single(Brep.CreateBooleanDifference(outer, bent, 1e-3));
            Assert.Null(AnalyticVisibility.Recognise(kinked, 1e-3));
        }
    });

    // ---------------------------------------------------------------- pipe nodes

    private static Brep Pipe(Point3d from, Vector3d along, double radius, double inner, double length)
    {
        Assert.True(along.Unitize());
        using var outer = new Cylinder(new Circle(new Plane(from, along), radius), length).ToBrep(true, true);
        using var bore = new Cylinder(new Circle(new Plane(from - along, along), inner), length + 2).ToBrep(true, true);
        return Assert.Single(Brep.CreateBooleanDifference(outer, bore, Tol));
    }

    /// <summary>
    /// An independent check of a Top view: from points of each drawn curve, march straight up and ask Rhino whether the
    /// point is inside any solid. A Visible point must have no cover; a Hidden one must have some. Points whose ray
    /// only grazes something are not judged. Returns (judged, wrong).
    /// </summary>
    private (int Judged, int Wrong) Witness(ViewDrawing view, IReadOnlyList<Brep> solids, double top)
    {
        int judged = 0, wrong = 0; const double step = 4e-3;
        foreach (var piece in view.Items.Where(p => p.Class is LineClass.Visible or LineClass.Hidden))
        {
            if (piece.DepthGeometry is not Curve world || world.GetLength() < 5e-2) continue;
            foreach (double f in new[] { .3, .7 })
            {
                var w = world.PointAt(world.Domain.ParameterAt(f)); int run = 0, longest = 0, grazed = 0;
                for (double z = w.Z + step; z < top; z += step)
                {
                    bool inside = solids.Any(b => b.IsPointInside(new Point3d(w.X, w.Y, z), Tol, true));
                    if (inside) longest = Math.Max(longest, ++run); else { if (run is > 0 and < 3) grazed++; run = 0; }
                }
                if (longest >= 3) { judged++; if (piece.Class != LineClass.Hidden) { wrong++; output.WriteLine($"WRONG Visible at {w}: {longest * step * 1000:F0} mm of cover; source {piece.SourceIdentity}"); } }
                else if (longest == 0 && grazed == 0) { judged++; if (piece.Class != LineClass.Visible) { wrong++; output.WriteLine($"WRONG Hidden at {w}: no cover; source {piece.SourceIdentity}"); } }
            }
        }
        return (judged, wrong);
    }

    [RhinoFact] public void FivePipesMeetingAtANodeAreExactWithAndWithoutANodeSphere() => host.Run(() =>
    {
        // Five hollow sections run into one node and through each other, as modelled steel often does. Nothing is
        // trimmed to fit: every pipe is still a tube between two planes. No native call, nothing Unresolved, and the
        // independent witness agrees with every drawn class.
        var node = new Point3d(.3, -.2, 1);
        var directions = new[] { new Vector3d(1, .1, .1), new Vector3d(-.8, .5, .2), new Vector3d(-.3, -1, -.1), new Vector3d(.4, .9, .3), new Vector3d(.2, -.3, 1) };
        var pipes = directions.Select(d => { var u = d; u.Unitize(); return Pipe(node - u * .15, d, .2, .17, 2.5); }).ToList();
        using var ball = new Sphere(node, .35).ToBrep();
        try
        {
            foreach (bool sphere in new[] { false, true })
            {
                var bodies = sphere ? pipes.Append(ball).ToList() : pipes;
                Assert.All(bodies, b => Assert.NotNull(AnalyticVisibility.Recognise(b, Tol)));
                using var view = Route(bodies.Cast<GeometryBase>().ToArray());
                output.WriteLine($"{(sphere ? "with" : "without")} node sphere: {view.Report.Route}; pieces {view.Items.Count}; Visible {Length(view, LineClass.Visible):F4}, Hidden {Length(view, LineClass.Hidden):F4}, Unresolved {Length(view, LineClass.Unresolved):F6}");
                Assert.Equal(0, view.NativeCalls); Assert.Contains($"exact analytic solids={bodies.Count}", view.Report.Route);
                Assert.Equal(0, Length(view, LineClass.Unresolved), 6);
                var (judged, wrong) = Witness(view, bodies, 5);
                output.WriteLine($"witness: judged {judged}, wrong {wrong}");
                Assert.True(judged > 40, $"judged {judged}"); Assert.Equal(0, wrong);
            }
        }
        finally { foreach (var p in pipes) p.Dispose(); }
    });

    [RhinoFact] public void InsulationOverAPipeSharesTheBoreSurfaceWithoutShowingThrough() => host.Run(() =>
    {
        // The insulation's bore is the pipe's outer surface. Under the insulation the pipe's outline is Hidden; past
        // its ends it is Visible. Nothing is Unresolved and no native call is made.
        using var pipe = Pipe(new Point3d(-2, 0, 0), Vector3d.XAxis, .2, .17, 4); using var insulation = Pipe(new Point3d(-1, 0, 0), Vector3d.XAxis, .3, .2, 2);
        using var view = Route([pipe, insulation]);
        output.WriteLine($"{view.Report.Route}; Visible {Length(view, LineClass.Visible):F4}, Hidden {Length(view, LineClass.Hidden):F4}, Unresolved {Length(view, LineClass.Unresolved):F6}");
        Assert.Equal(0, view.NativeCalls); Assert.Contains("exact analytic solids=2", view.Report.Route);
        Assert.Equal(0, Length(view, LineClass.Unresolved), 6);
        // The pipe's two outlines: 4 long each, of which the middle 2 lie under the insulation.
        double Straight(LineClass kind, string source) => view.Items.Where(p => p.Class == kind && p.SourceIdentity == source)
            .Sum(p => { using var g = p.CopyGeometry(); return g is Curve c && c.IsLinear(1e-9) && Math.Abs(c.PointAtStart.Y - c.PointAtEnd.Y) < 1e-9 && Math.Abs(Math.Abs(c.PointAtStart.Y) - .2) < 1e-6 ? c.GetLength() : 0; });
        Assert.Equal(4, Straight(LineClass.Visible, "0"), 4);
        var (judged, wrong) = Witness(view, [pipe, insulation], 2);
        output.WriteLine($"witness: judged {judged}, wrong {wrong}"); Assert.True(judged > 8); Assert.Equal(0, wrong);
    });

    [RhinoFact] public void APipeWithATorusElbowStaysOnNativeAndInfoNamesTheElbow() => host.Run(() =>
    {
        using var pipe = Pipe(new Point3d(-3, 1, 0), Vector3d.XAxis, .2, .17, 3); using var elbow = Ring(new Point3d(0, 0, 0), 1, .2);
        Assert.NotNull(AnalyticVisibility.Recognise(pipe, Tol)); Assert.Null(AnalyticVisibility.Recognise(elbow, Tol));
        using var view = Route([pipe, elbow]);
        foreach (var d in view.Diagnostics.Where(d => d.Contains("nalytic"))) output.WriteLine(d);
        Assert.Equal(1, view.NativeCalls); Assert.DoesNotContain("exact analytic solids", view.Report.Route);
        Assert.Contains(view.Diagnostics, d => d == "Analytic solids kept on native HLD: 0 (may be hidden by 1).");
        Assert.Contains(view.Diagnostics, d => d == "Not exact analytic solids (native HLD): 1 x a face that is not a plane, cylinder or sphere (1).");
    });

    // ---------------------------------------------------------------- composition with the planar kernel

    [RhinoFact] public void ColumnAndPlatesNeedNoNativeCallAndMatchClosedFormLengths() => host.Run(() =>
    {
        // Top view. A column of radius 1 from z = 0 to 2; a plate under it; a plate over half of it.
        using var column = Column(Point3d.Origin, 1, 2); using var under = Plate(-3, 0, -1.2, 3, 3, -1); using var over = Plate(0, -3, 3, 3, 3, 3.2);
        using var alone = Route([under, column]);
        output.WriteLine(alone.Report.Route);
        Assert.Equal(0, alone.NativeCalls); Assert.Contains("exact analytic solids=1", alone.Report.Route); Assert.Equal(0, alone.Report.UncertainCount);
        // Seen along its axis the column's two rims fall on one circle: the top one is Visible, and the bottom one,
        // behind the body, is not drawn a second time under it.
        Assert.Equal(2 * Math.PI, Length(alone, LineClass.Visible, "1"), 5); Assert.Equal(0, Length(alone, LineClass.Hidden, "1"), 5);
        // The plate's edge along y = 0 runs under the column for |x| < 1: 2 of its 18 m of top outline are hidden.
        Assert.Equal(16, Length(alone, LineClass.Visible, "0"), 5);

        using var covered = Route([over, column]);
        Assert.Equal(0, covered.NativeCalls);
        // The plate above hides the half of the top rim with x > 0. The bottom rim lies under the top rim throughout,
        // so the circle is drawn once: half Visible, half Hidden.
        Assert.Equal(Math.PI, Length(covered, LineClass.Visible, "1"), 5); Assert.Equal(Math.PI, Length(covered, LineClass.Hidden, "1"), 5);
        Assert.Equal(0, covered.Report.UncertainCount);
    });

    [RhinoFact] public void StackedColumnsInPlanDrawOneCircleInAFewPieces() => host.Run(() =>
    {
        // Three storeys of one column, away from the origin. In plan every rim lies exactly on the outline of the
        // other two columns: the boundary rule must see the whole rim on that boundary, not chords dipping in and out
        // of it (which once gave about a hundred alternating pieces per rim).
        var foot = new Point3d(37.3, 52.1, 3.2);
        var columns = Enumerable.Range(0, 3).Select(i => (GeometryBase)Column(foot + Vector3d.ZAxis * (3.6 * i), .2, 3.6)).ToArray();
        try
        {
            var result = new ViewDrawing { Frame = new Rectangle3d(Plane.WorldXY, new Interval(30, 45), new Interval(45, 60)), Tolerance = Tol };
            using (result)
            {
                var input = columns.Select((g, i) => new VisibilityInput(g, new PreparedLeaf(i.ToString(), [i], g))).ToArray();
                output.WriteLine(ComposedVisibility.Solve(input, result, ViewRequest.Parse("Top"), new(), default));
                Assert.Equal(0, result.NativeCalls); Assert.DoesNotContain(result.Items, p => p.Class == LineClass.Unresolved);
                Assert.Equal(2 * Math.PI * .2, Length(result, LineClass.Visible), 6); Assert.Equal(0, Length(result, LineClass.Hidden), 6);
                Assert.InRange(result.Items.Count, 1, 4);
            }
        }
        finally { foreach (var c in columns) c.Dispose(); }
    });

    [RhinoFact] public void TubeSphereAndArcWallHideAndAreHiddenExactly() => host.Run(() =>
    {
        // A probe line under each shape, at z = -1, crossing its whole footprint in the Top view.
        using var tube = Tube(new Point3d(-6, 0, 0), 1, .5, 1); using var ball = new Sphere(new Point3d(0, 0, 2), 1).ToBrep();
        using var arc = new ArcCurve(new Arc(new Plane(new Point3d(6, 0, 0), Vector3d.XAxis, Vector3d.ZAxis), 1, Math.PI));   // a vault: half a cylinder along Y
        using var vault = Surface.CreateExtrusion(arc, Vector3d.YAxis * 2).ToBrep(); vault.Translate(0, -1, 0);
        using var probe = Plate(-9, 0, -1.5, 9, 5, -1);   // its edge y = 0, z = -1 is the probe
        using var view = Route([probe, tube, ball, vault]);
        output.WriteLine(view.Report.Route); foreach (var d in view.Diagnostics.Where(d => d.StartsWith("Analytic"))) output.WriteLine(d);
        Assert.Equal(0, view.NativeCalls); Assert.Contains("exact analytic solids=3", view.Report.Route); Assert.Equal(0, view.Report.UncertainCount);
        // Under the tube the probe is hidden by the two walls (2 x 0.5) and seen through the bore (1); under the ball 2; under the vault 2.
        Assert.Equal(18 + 5 + 18 + 5 - (1 + 2 + 2), Length(view, LineClass.Visible, "0"), 4);
        // The ball draws its outline, a full circle, Visible.
        Assert.Equal(2 * Math.PI, Length(view, LineClass.Visible, "2"), 5); Assert.Equal(0, Length(view, LineClass.Hidden, "2"));
        // The tube seen along its axis: top rims Visible (outer and bore), bottom rims behind the wall or seen through the bore.
        Assert.Equal(2 * Math.PI * 1.5, Length(view, LineClass.Visible, "1"), 5);
        // The vault: its two straight springing edges and two outline rulings coincide in pairs... its arcs project to lines.
        Assert.True(Length(view, LineClass.Visible, "3") > 0);
    });

    [RhinoFact] public void PointsAreHiddenByExactSolids() => host.Run(() =>
    {
        using var column = Column(Point3d.Origin, 1, 2); using var plate = Plate(5, 5, 0, 6, 6, 1);
        using var under = new global::Rhino.Geometry.Point(new Point3d(.3, 0, -1)); using var beside = new global::Rhino.Geometry.Point(new Point3d(2, 0, -1));
        using var view = Route([plate, column, under, beside]);
        Assert.Equal(0, view.NativeCalls); Assert.Equal(1, view.HiddenPoints);
        Assert.Single(view.Items, p => p.SourceIdentity == "3"); Assert.DoesNotContain(view.Items, p => p.SourceIdentity == "2");
    });

    // ---------------------------------------------------------------- composition with native HLD

    [RhinoFact] public void ANativeBodyElsewhereLeavesTheColumnExactAndOneBehindItIsCutExactly() => host.Run(() =>
    {
        using var column = Column(Point3d.Origin, 1, 2); using var away = Ring(new Point3d(6, 0, 0), 1, .3);
        using var apart = Route([column, away]);
        output.WriteLine(apart.Report.Route);
        Assert.Equal(1, apart.NativeCalls); Assert.Contains("exact analytic solids=1", apart.Report.Route);
        Assert.Equal(2 * Math.PI, Length(apart, LineClass.Visible, "0"), 5);
        Assert.Equal(2 * Math.PI * (1.3 + .7), Length(apart, LineClass.Visible, "1"), 3);

        // A ring wholly below the column: the column still leaves HLD, and hides the ring's inner outline (radius 0.7)
        // while its outer outline (radius 1.3) stays Visible.
        using var below = Ring(new Point3d(0, 0, -3), 1, .3);
        using var stacked = Route([column, below]);
        output.WriteLine(stacked.Report.Route);
        Assert.Contains("exact analytic solids=1", stacked.Report.Route);
        Assert.Equal(2 * Math.PI * 1.3, Length(stacked, LineClass.Visible, "1"), 3); Assert.Equal(2 * Math.PI * .7, Length(stacked, LineClass.Hidden, "1"), 3);
        Assert.Equal(2 * Math.PI, Length(stacked, LineClass.Visible, "0"), 5);
    });

    [RhinoFact] public void ANativeCurveBehindASheetSliverIsNotMergedIntoVisible() => host.Run(() =>
    {
        // Top view, tolerance 0.1 mm. A curved upstand 80 mm high leans by about a milliradian, so its drawing is a thin strip. A native
        // curve passes under it at right angles. A native curve is only known to half a tolerance sideways. Behind a
        // strip 0.8 tolerances wide nothing can be decided: that stretch must stay Unresolved, not be given to its
        // Visible neighbours (it once was). Behind a strip 2.5 tolerances wide the middle is provably Hidden.
        const double tol = 1e-4;
        using var arc = new ArcCurve(new Arc(new Circle(new Plane(Point3d.Origin, Vector3d.XAxis, Vector3d.YAxis), 3), new Interval(-Math.PI / 6, Math.PI / 6)));
        foreach (double width in new[] { .8, 2.5 })
        {
        using var wall = Surface.CreateExtrusion(arc, Vector3d.ZAxis * .08).ToBrep();
        Assert.True(wall.Rotate(width * tol / .08, Vector3d.YAxis, new Point3d(3, 0, 0)));
        Assert.IsType<AnalyticVisibility.SheetShape>(AnalyticVisibility.Recognise(wall, tol));
        using var under = new ArcCurve(new Arc(new Circle(new Plane(new Point3d(3, -5, -1), Vector3d.XAxis, Vector3d.YAxis), 5), new Interval(Math.PI / 2 - .2, Math.PI / 2 + .2)));
        using var view = new ViewDrawing { Frame = new Rectangle3d(Plane.WorldXY, new Interval(-10, 10), new Interval(-10, 10)), Tolerance = tol };
        var input = new GeometryBase[] { wall, under }.Select((g, i) => new VisibilityInput(g, new PreparedLeaf(i.ToString(), [i], g))).ToArray();
        string route = ComposedVisibility.Solve(input, view, ViewRequest.Parse("Top"), new(), default);
        output.WriteLine(route); Assert.Contains("exact analytic solids=1", route);
        output.WriteLine($"strip {width} tol; curve: Visible {Length(view, LineClass.Visible, "1"):R}, Hidden {Length(view, LineClass.Hidden, "1"):R}, Unresolved {Length(view, LineClass.Unresolved, "1"):R}");
        var behind = new Point3d(3 + width / 2 * tol, 0, 0);   // the middle of the strip, in the drawing
        foreach (var piece in view.Items.Where(p => p.SourceIdentity == "1" && p.Class == LineClass.Visible))
        {
            using var g = piece.CopyGeometry(); var curve = Assert.IsAssignableFrom<Curve>(g);
            Assert.True(!curve.ClosestPoint(behind, out double t) || curve.PointAt(t).DistanceTo(behind) > tol / 4, "a Visible piece covers the stretch behind the sliver");
        }
        double undecided = Length(view, LineClass.Unresolved, "1") + Length(view, LineClass.Hidden, "1");
        Assert.InRange(undecided, width * tol / 2, 8 * tol);   // that stretch, and only that stretch
        Assert.Equal(under.GetLength(), undecided + Length(view, LineClass.Visible, "1"), 6);
        if (width < 1) Assert.True(Length(view, LineClass.Unresolved, "1") > 0); else Assert.True(Length(view, LineClass.Hidden, "1") > tol);
        }
    });

    [RhinoFact] public void AColumnThatANativeBodyMayHideStaysOnNative() => host.Run(() =>
    {
        using var column = Column(Point3d.Origin, 1, 2); using var above = Ring(new Point3d(0, 0, 4), 1, .3);
        using var view = Route([column, above]);
        output.WriteLine(view.Report.Route); foreach (var d in view.Diagnostics.Where(d => d.StartsWith("Analytic"))) output.WriteLine(d);
        Assert.DoesNotContain("exact analytic solids", view.Report.Route);
        Assert.Contains(view.Diagnostics, d => d.StartsWith("Analytic solids: recognised=1; exact=0; kept on native HLD because an unrecognised body may hide them=1"));
        Assert.Equal(1, view.NativeCalls);
        // Native decides: the ring's tube covers the rim between radii 0.7 and 1.3, so the whole top rim is hidden.
        Assert.Equal(0, Length(view, LineClass.Visible, "0"), 3);
    });

    [RhinoFact] public void TangentAndSeamSettingsAndTheSwitchKeepEverythingOnNative() => host.Run(() =>
    {
        using var column = Column(Point3d.Origin, 1, 2); using var plate = Plate(-3, 0, -1.2, 3, 3, -1);
        using var off = Route([plate, column], new NativeSettings { Analytic = false });
        Assert.DoesNotContain("exact analytic solids", off.Report.Route); Assert.Equal(1, off.NativeCalls);
        foreach (var line in new[] { "tangents=on", "seams=on" })
        {
            using var view = Route([plate, column], view: SettingsFor(ViewRequest.Parse("Top"), line));
            Assert.DoesNotContain("exact analytic solids", view.Report.Route); Assert.Equal(1, view.NativeCalls);
        }
    });

    // ---------------------------------------------------------------- whole pipeline: uncut, sections, crop, glass

    private static double Total(ViewDrawing view, params LineClass[] kinds) => kinds.Sum(k => Length(view, k));

    [RhinoFact] public void WholePipelineAgreesWithNativeOnScenesWithoutTies() => host.Run(() =>
    {
        // Nothing touches anything, so the tie convention cannot differ; lengths must agree with the native route.
        using var doc = RhinoDoc.CreateHeadless(null); doc.ModelAbsoluteTolerance = 1e-5;
        using var column = Column(new Point3d(0, 0, .5), .6, 3); using var tube = Tube(new Point3d(3, 1, .2), .8, .6, 2);
        using var ball = new Sphere(new Point3d(-2.5, 1.5, 2), .9).ToBrep(); using var slab = Plate(-5, -4, -.5, 6, 5, -.2); using var wall = Plate(-4, 3, 0, 2, 3.3, 2.5);
        foreach (var b in new[] { column, tube, ball, slab, wall }) doc.Objects.AddBrep(b);
        using var cache = new SceneCache();
        var views = new[] { ViewRequest.Parse("Top"), ViewRequest.Parse("Front"), ViewRequest.Parse("SW"),
            ViewRequest.Parse(new Plane(new Point3d(0, 0, 1.5), Vector3d.XAxis, Vector3d.YAxis)) with { Name = "Plan" },
            ViewRequest.Parse(new Plane(new Point3d(0, .9, 0), Vector3d.XAxis, Vector3d.ZAxis)) with { Name = "Section" } };
        using var exact = NativeDrawing.SolveAsync(BaselineTests.Capture(doc, cache), new("s", "v", "exact", 1), views, new(), default).GetAwaiter().GetResult();
        using var native = NativeDrawing.SolveAsync(BaselineTests.Capture(doc, cache), new("s", "v", "native", 1), views, new NativeSettings { Analytic = false }, default).GetAwaiter().GetResult();
        for (int i = 0; i < views.Length; i++)
        {
            var a = exact.Views[i]; var b = native.Views[i]; output.WriteLine($"{views[i].Name}: {a.Report.Route}");
            Assert.Null(a.Error); Assert.Null(b.Error); Assert.False(a.Report.Quarantined, a.Info);
            // Uncut views are all exact. A cut can leave pieces that are none of the shapes (half a ball): those stay native.
            Assert.True(b.NativeCalls > 0, views[i].Name); Assert.True(i < 3 ? a.NativeCalls == 0 : a.NativeCalls <= b.NativeCalls, views[i].Name);
            Assert.True(i >= 3 || a.Report.UncertainCount == 0, views[i].Name + ": " + a.Info);   // cut views may keep native pieces
            Assert.Equal(Total(b, LineClass.Visible), Total(a, LineClass.Visible), 2);
            Assert.Equal(b.Regions.Count, a.Regions.Count); Assert.Equal(b.Regions.Sum(r => r.Region.Area), a.Regions.Sum(r => r.Region.Area), 6);
            // Native merges coincident hidden lines; where it reports nothing Unresolved the hidden lengths agree too.
            if (b.Report.UncertainCount == 0) Assert.Equal(Total(b, LineClass.Hidden), Total(a, LineClass.Hidden), 2);
        }
    });

    [RhinoFact] public void SectionPiecesAreExactWhenTheCutLeavesACylinderAndNativeOtherwise() => host.Run(() =>
    {
        using var doc = RhinoDoc.CreateHeadless(null); doc.ModelAbsoluteTolerance = 1e-5;
        using var column = Column(Point3d.Origin, 1, 4); using var slab = Plate(-3, -3, -.5, 3, 3, -.2);
        doc.Objects.AddBrep(column); doc.Objects.AddBrep(slab);
        using var cache = new SceneCache();
        // A plan through the column leaves a shorter cylinder: exact, no native call, one round cut region.
        var plan = ViewRequest.Parse(new Plane(new Point3d(0, 0, 2), Vector3d.XAxis, Vector3d.YAxis)) with { Name = "Plan" };
        // A vertical section through its axis leaves half a cylinder: not one of the shapes, so it stays on native.
        var section = ViewRequest.Parse(new Plane(new Point3d(0, 0, 0), Vector3d.XAxis, Vector3d.ZAxis)) with { Name = "Section" };
        // An oblique cut leaves a cylinder with a mitred end: exact again.
        var oblique = ViewRequest.Parse(new Plane(new Point3d(0, 0, 2), Vector3d.XAxis, new Vector3d(0, 1, .3))) with { Name = "Oblique" };
        using var batch = BaselineTests.Solve(BaselineTests.Capture(doc, cache), plan, section, oblique);
        foreach (var v in batch.Views) { output.WriteLine($"{v.Name}: {v.Report.Route}"); Assert.Null(v.Error); Assert.False(v.Report.Quarantined, v.Info); }
        Assert.Equal(0, batch.Views[0].NativeCalls); Assert.Contains("exact analytic solids=1", batch.Views[0].Report.Route);
        Assert.Equal(Math.PI, Assert.Single(batch.Views[0].Regions).Region.Area, 4);
        Assert.True(batch.Views[1].NativeCalls > 0); Assert.DoesNotContain("exact analytic solids", batch.Views[1].Report.Route);
        Assert.Equal(0, batch.Views[2].NativeCalls); Assert.Contains("exact analytic solids=1", batch.Views[2].Report.Route);
        Assert.Single(batch.Views[2].Regions);
    });

    [RhinoFact] public void SectionFarFromTheOriginIsExactInItsLocalFrame() => host.Run(() =>
    {
        // Sections trim and validate in a local frame (far-from-origin models). The clipped piece comes back in world
        // coordinates and must still be recognised there, and drawn without a native call.
        var at = new Point3d(212345.6, 398765.4, 0);
        using var doc = RhinoDoc.CreateHeadless(null); doc.ModelAbsoluteTolerance = 1e-4;
        using var column = Column(at, 1, 4); using var slab = Plate(at.X - 3, at.Y - 3, -.5, at.X + 3, at.Y + 3, -.2);
        doc.Objects.AddBrep(column); doc.Objects.AddBrep(slab);
        using var cache = new SceneCache();
        var plan = ViewRequest.Parse(new Plane(new Point3d(at.X, at.Y, 2), Vector3d.XAxis, Vector3d.YAxis)) with { Name = "Plan" };
        var oblique = ViewRequest.Parse(new Plane(new Point3d(at.X, at.Y, 2), Vector3d.XAxis, new Vector3d(0, 1, .3))) with { Name = "Oblique" };
        using var batch = BaselineTests.Solve(BaselineTests.Capture(doc, cache), plan, oblique);
        foreach (var v in batch.Views)
        {
            output.WriteLine($"{v.Name}: {v.Report.Route}"); Assert.Null(v.Error); Assert.False(v.Report.Quarantined, v.Info);
            Assert.Equal(0, v.NativeCalls); Assert.Contains("exact analytic solids=1", v.Report.Route); Assert.Equal(0, v.Report.UncertainCount);
        }
        Assert.Equal(Math.PI, Assert.Single(batch.Views[0].Regions).Region.Area, 3);
    });

    private sealed class Sink(List<JobProgress> values) : IProgress<JobProgress>
    { public void Report(JobProgress value) { lock (values) values.Add(value); } }

    [RhinoFact] public void FastEngineLeavesExactSolidsExactAndReportsTheirStep() => host.Run(() =>
    {
        // engine = fast only changes what still goes to native. A column and a plate never go there: the drawing is
        // the exact one, no source is counted as fast, and the view reports the "exact curved" step.
        using var doc = RhinoDoc.CreateHeadless(null); doc.ModelAbsoluteTolerance = 1e-4;
        using var column = Column(Point3d.Origin, 1, 2); using var under = Plate(-3, 0, -1.2, 3, 3, -1);
        doc.Objects.AddBrep(under); doc.Objects.AddBrep(column);
        using var cache = new SceneCache(); using var scene = BaselineTests.Capture(doc, cache);
        ViewDrawing Draw(string engine, List<JobProgress> reports)
        {
            var view = ViewRequest.Parse("Top");
            var setting = Settings.Resolve([new("v", view.Name, view.Kind, false, 20, 1e-6)], [[$"engine={engine}"]], DocumentUnits.Metres).Views[0];
            var batch = NativeDrawing.SolveBorrowedAsync(scene.Value, new("s", "v", engine, 1), [view with { Settings = setting.ForVisibilitySolve() }],
                new(), default, new Sink(reports)).GetAwaiter().GetResult();
            return Assert.Single(batch.Views);
        }
        var fastReports = new List<JobProgress>(); var exactReports = new List<JobProgress>();
        using var fast = Draw("fast", fastReports); using var exact = Draw("exact", exactReports);
        Assert.Null(fast.Error); Assert.Null(exact.Error);
        Assert.Equal(0, fast.NativeCalls); Assert.Equal(0, fast.FastNativeSources); Assert.Contains("exact analytic solids=1", fast.Report.Route);
        foreach (var kind in new[] { LineClass.Visible, LineClass.Hidden, LineClass.Unresolved })
            Assert.Equal(Length(exact, kind), Length(fast, kind), 9);
        Assert.Equal(2 * Math.PI, fast.Items.Where(p => p.Class == LineClass.Visible).Sum(p => { using var g = p.CopyGeometry(); return g is Curve c && !c.IsLinear() ? c.GetLength() : 0; }), 5);
        Assert.Contains(fastReports, p => p.Step == ViewSteps.ExactCurved); Assert.Contains(exactReports, p => p.Step == ViewSteps.ExactCurved);
        Assert.DoesNotContain(fastReports, p => p.Step == ViewSteps.Fast);
    });

    [RhinoFact] public void CutOnlyViewsAreTheSameWithAndWithoutTheClass() => host.Run(() =>
    {
        // cutonly = on returns after the cut regions, before any projection: the class is never asked. A plan through a
        // column and a wall gives the same cuts, no line beyond them and no native call, with the class on or off.
        using var doc = RhinoDoc.CreateHeadless(null); doc.ModelAbsoluteTolerance = 1e-5;
        using var column = Column(Point3d.Origin, 1, 4); using var wall = Plate(2, -2, 0, 2.3, 2, 4); using var slab = Plate(-3, -3, -.5, 3, 3, -.2);
        foreach (var b in new[] { column, wall, slab }) doc.Objects.AddBrep(b);
        using var cache = new SceneCache();
        var plan = ViewRequest.Parse(new Plane(new Point3d(0, 0, 2), Vector3d.XAxis, Vector3d.YAxis)) with { Name = "Plan" };
        ViewDrawing Draw(string cutOnly, bool analytic)
        {
            var context = new ViewSettingsContext("view", plan.Name, plan.Kind, true, 10, .00001, null);
            var settings = Settings.Resolve(new[] { context }, new[] { new[] { "cutonly=" + cutOnly, "hidden=on", "depth=5" } }, DocumentUnits.Metres);
            Assert.False(settings.HasErrors, string.Join(";", settings.Diagnostics));
            var batch = NativeDrawing.SolveAsync(BaselineTests.Capture(doc, cache), new("s", "v", cutOnly + analytic, 1), [plan with { Settings = settings.Views[0].ForVisibilitySolve() }],
                new NativeSettings { Analytic = analytic }, default).GetAwaiter().GetResult();
            return Assert.Single(batch.Views);
        }
        using var on = Draw("on", true); using var off = Draw("on", false); using var full = Draw("off", true);
        foreach (var v in new[] { on, off }) { Assert.Null(v.Error); Assert.Equal(0, v.NativeCalls); Assert.DoesNotContain("exact analytic solids", v.Report.Route); }
        Assert.Equal(2, on.Regions.Count); Assert.Equal(off.Regions.Count, on.Regions.Count);
        Assert.Equal(off.Regions.Sum(r => r.Region.Area), on.Regions.Sum(r => r.Region.Area), 9); Assert.Equal(Math.PI + .3 * 4, on.Regions.Sum(r => r.Region.Area), 4);
        Assert.Equal(off.Items.Count, on.Items.Count);
        foreach (var kind in new[] { LineClass.Visible, LineClass.Hidden, LineClass.Unresolved }) Assert.Equal(Length(off, kind), Length(on, kind), 9);
        Assert.Equal(off.Diagnostics, on.Diagnostics);
        // The same view without cutonly does use the class (the column below the cut is exact) and draws the slab beyond.
        Assert.Contains("exact analytic solids=1", full.Report.Route); Assert.True(full.Items.Count > on.Items.Count);
    });

    [RhinoFact] public void ClearGlassCoversNothingAndOpaqueGlassHidesExactSolids() => host.Run(() =>
    {
        using var doc = RhinoDoc.CreateHeadless(null); doc.ModelAbsoluteTolerance = 1e-5;
        var material = doc.Materials.Add(); var edited = doc.Materials[material]; edited.Transparency = .7; edited.CommitChanges();
        using var attributes = new ObjectAttributes { MaterialSource = ObjectMaterialSource.MaterialFromObject, MaterialIndex = material };
        using var pane = Plate(-2, -2, 3, 2, 2, 3.1); doc.Objects.AddBrep(pane, attributes);
        using var column = Column(Point3d.Origin, 1, 2); var id = doc.Objects.AddBrep(column).ToString();
        using var scene = new SceneCache(); using var cache = new DrawingCache();
        DrawingBatch Draw(params string[] lines)
        {
            var lease = BaselineTests.Capture(doc, scene);
            return cache.SolveAsync(lease, new(lease.Value.Revision, "glass", string.Join(",", lines), 1), new[] { SettingsFor(ViewRequest.Parse("Top"), lines) }, new(), [], default).GetAwaiter().GetResult();
        }
        using var opaque = Draw(); using var clear = Draw("glass=clear");
        foreach (var v in new[] { opaque.Views[0], clear.Views[0] }) { output.WriteLine(v.Report.Route); Assert.Null(v.Error); Assert.Equal(0, v.NativeCalls); Assert.Contains("exact analytic solids=1", v.Report.Route); }
        Assert.Equal(0, Length(opaque.Views[0], LineClass.Visible, id), 5); Assert.Equal(2 * Math.PI, Length(opaque.Views[0], LineClass.Hidden, id), 4);
        Assert.Equal(2 * Math.PI, Length(clear.Views[0], LineClass.Visible, id), 5); Assert.Equal(0, Length(clear.Views[0], LineClass.Hidden, id), 5);
    });

    [RhinoFact] public void CropTrimsExactCurves() => host.Run(() =>
    {
        using var doc = RhinoDoc.CreateHeadless(null); doc.ModelAbsoluteTolerance = 1e-6;
        using var column = Column(Point3d.Origin, 1, 2); var id = doc.Objects.AddBrep(column).ToString();
        using var plate = Plate(5, 5, 0, 6, 6, 1); doc.Objects.AddBrep(plate);
        using var viewport = new ViewportInfo(); viewport.IsParallelProjection = true;
        viewport.SetCameraLocation(new Point3d(0, 0, 10)); viewport.SetCameraDirection(-Vector3d.ZAxis);
        viewport.SetCameraUp(Vector3d.YAxis); viewport.SetFrustum(0, 3, -3, 3, .1, 100);   // the half x > 0
        viewport.SetScreenPort(new Rectangle(0, 0, 300, 600), 0, 1);
        using var source = new global::Rhino.Display.RhinoViewport(); Assert.True(source.SetViewProjection(viewport, false));
        using var named = new ViewInfo(source) { Name = "Half" }; doc.NamedViews.Add(named);
        var request = ViewRequest.Parse("Half", doc); Assert.True(request.Named is not null, request.Error);
        using var cache = new SceneCache();
        using var batch = BaselineTests.Solve(BaselineTests.Capture(doc, cache), request with { Named = request.Named with { Crop = true } }, request with { Named = request.Named with { Crop = false } });
        foreach (var v in batch.Views) { output.WriteLine(v.Report.Route); Assert.Null(v.Error); Assert.Equal(0, v.NativeCalls); }
        // Half of the rim circle is inside the crop.
        Assert.Equal(Math.PI, Length(batch.Views[0], LineClass.Visible, id), 4); Assert.Equal(0, Length(batch.Views[0], LineClass.Hidden, id), 4);
        Assert.Equal(2 * Math.PI, Length(batch.Views[1], LineClass.Visible, id), 5);
    });

    [RhinoFact] public void MirroredBlockInstancesAreRecognisedAndDrawnLikeTheirOriginals() => host.Run(() =>
    {
        using var doc = RhinoDoc.CreateHeadless(null); doc.ModelAbsoluteTolerance = 1e-5;
        using var tube = Tube(new Point3d(1, 0, 0), .5, .3, 2);
        int definition = doc.InstanceDefinitions.Add("tube", "", Point3d.Origin, new GeometryBase[] { tube }, new[] { new ObjectAttributes() });
        doc.Objects.AddInstanceObject(definition, Transform.Identity);
        doc.Objects.AddInstanceObject(definition, Transform.Mirror(Plane.WorldYZ));                                  // mirrored
        doc.Objects.AddInstanceObject(definition, Transform.Translation(0, 4, 0) * Transform.Scale(Plane.WorldXY, 1, 2, 1)); // squashed: refused
        using var slab = Plate(-4, -2, -.6, 4, 6, -.3); doc.Objects.AddBrep(slab);
        using var cache = new SceneCache();
        using var batch = BaselineTests.Solve(BaselineTests.Capture(doc, cache), ViewRequest.Parse("Top"), ViewRequest.Parse("SW"));
        foreach (var v in batch.Views)
        {
            output.WriteLine(v.Report.Route); foreach (var d in v.Diagnostics.Where(d => d.StartsWith("Analytic"))) output.WriteLine(d);
            Assert.Null(v.Error); Assert.False(v.Report.Quarantined, v.Info);
            Assert.Contains(v.Diagnostics, d => d.StartsWith("Analytic solids: recognised=2; exact=2;"));
            Assert.Equal(1, v.NativeCalls);   // the squashed instance is an elliptic tube: native
        }
        // Top view: each exact tube shows its two top rims; the mirrored one exactly like the original.
        var top = batch.Views[0]; var bySource = top.Items.Where(p => p.Class == LineClass.Visible).GroupBy(p => p.SourceIdentity)
            .ToDictionary(g => g.Key, g => g.Sum(p => { using var c = p.CopyGeometry(); return c is Curve k ? k.GetLength() : 0; }));
        Assert.Equal(2, bySource.Values.Count(l => Math.Abs(l - 2 * Math.PI * .8) < 1e-4));
    });
}
