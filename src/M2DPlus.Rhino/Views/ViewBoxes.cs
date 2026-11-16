using System.Collections.Immutable;
using M2DPlus.Core;
using M2DPlus.Core.Views;
using M2DPlus.Rhino.Services;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace M2DPlus.Rhino.Views;

/// <summary>A view frame established from geometry. <see cref="Frame"/>'s plane Z points toward the viewer, so the
/// view looks along −Z (UX §2). Its origin is the frame's lower-left corner. <see cref="Depth"/> is set for a box.</summary>
public sealed record ViewFrameCheck(Rectangle3d? Frame, double? Depth, string? Reason)
{
    public bool IsValid => Frame is not null && Reason is null;
    public static ViewFrameCheck Fail(string reason) => new(null, null, reason);
}

/// <summary>One object's outcome for Convert to view boxes, Flip view direction or Create view. <see cref="Cancelled"/>:
/// the user cancelled the "View box depth" dialog, so the whole run changed nothing.</summary>
public sealed record ViewObjectResult(Guid Id, string Label, bool Ok, bool Changed, string Message, bool Cancelled = false)
{
    public override string ToString() => $"{Label}: {Message}";
}

/// <summary>M2D+ view boxes (UX rev. 4 §2.2): editable Rhino objects that define a view. A view box is an
/// <see cref="Extrusion"/> whose profile is the view frame (its front face, and the cut plane when it passes through
/// the model) and whose path runs into the model along the look direction; the path length is the depth. Per-view
/// settings are Attribute User Text keys <c>m2d.&lt;setting&gt;</c>; <c>m2d.view = 1</c> marks the object.
/// Host-thread only. No document reference is kept.</summary>
public static class ViewBoxes
{
    public const string Marker = "m2d.view", MarkerVersion = "1", Prefix = "m2d.", Inherit = "default";
    /// <summary>The single top-level layer view boxes live on (owner decision 2026-11-15). Older files may still have
    /// an <c>M2D+::Views</c> layer; it is left alone.</summary>
    public const string LayerName = "$M2D Views";
    public static readonly System.Drawing.Color Cobalt = System.Drawing.Color.FromArgb(0x1F, 0x6F, 0xD1);

    /// <summary>The front-face fill colour key (<c>m2d.color</c>). Display only: not a drawing setting, so
    /// <see cref="ReadRow"/> skips it.</summary>
    public const string ColorKey = M2DPlus.Core.Views.ViewBoxFill.Key;

    /// <summary>Per-view settings written as user text, in Settings-key order (UX §6.3). Depth and crop come from the
    /// geometry; tree, gap and tolerance are rarely per-view, so they are not written (but m2d.tree, m2d.gap and
    /// m2d.tolerance are read).</summary>
    public static ImmutableArray<string> Keys { get; } = ["name", "farclip", "cutonly", "beyond", "fade", "fade.start", "fade.end", "fade.limit",
        "hidden", "engine", "tangents", "seams", "glass", "scale", "weights", "hatch", "cuts", "min_visible"];

    public static bool IsViewBox(RhinoObject? obj) => obj?.Attributes.GetUserString(Marker) is not null;

    // ---------------------------------------------------------------- frames

    /// <summary>Checks that geometry can establish a view frame: one planar surface (trimmed is fine) or a closed
    /// planar curve whose outline is a rectangle (4 straight edges at right angles, not degenerate), or an
    /// extrusion read as a view box. The frame's normal is the surface normal or the curve's plane normal;
    /// <see cref="Face"/> then points it at the model.</summary>
    public static ViewFrameCheck Establish(GeometryBase? geometry, double tolerance, double angleTolerance)
    {
        switch (geometry)
        {
            case Extrusion extrusion: return ReadBox(extrusion, tolerance, angleTolerance);
            case Brep brep:
                if (brep.Faces.Count != 1)
                    return ViewFrameCheck.Fail($"it is a polysurface with {brep.Faces.Count} faces. Reference one planar surface.");
                return FromFace(brep.Faces[0], tolerance, angleTolerance);
            case Surface surface:
                using (var single = surface.ToBrep()) return single is null ? ViewFrameCheck.Fail("the surface is invalid.") : FromFace(single.Faces[0], tolerance, angleTolerance);
            case Curve curve:
                if (!curve.IsClosed) return ViewFrameCheck.Fail("the curve is not closed. Use a closed rectangle.");
                if (!curve.TryGetPlane(out var plane, tolerance)) return ViewFrameCheck.Fail("the curve is not planar.");
                if (curve.ClosedCurveOrientation(plane) == CurveOrientation.Clockwise) plane.Flip();
                return FromOutline(curve, plane.ZAxis, tolerance, angleTolerance);
            case null: return ViewFrameCheck.Fail("there is no geometry.");
            default: return ViewFrameCheck.Fail($"a {Describe(geometry)} can't frame a view. Use a planar surface or a closed rectangle curve.");
        }
    }

    private static ViewFrameCheck FromFace(BrepFace face, double tolerance, double angleTolerance)
    {
        if (!face.IsPlanar(tolerance) || !face.TryGetPlane(out _, tolerance)) return ViewFrameCheck.Fail("the surface is not planar.");
        var u = face.Domain(0).Mid; var v = face.Domain(1).Mid;
        var normal = face.NormalAt(u, v);
        if (face.OrientationIsReversed) normal.Reverse();
        using var outline = face.OuterLoop.To3dCurve();
        return outline is null ? ViewFrameCheck.Fail("the surface has no outer boundary.") : FromOutline(outline, normal, tolerance, angleTolerance);
    }

    private static ViewFrameCheck FromOutline(Curve outline, Vector3d normal, double tolerance, double angleTolerance)
    {
        var corners = Corners(outline, tolerance, angleTolerance, out var reason);
        if (corners is null) return ViewFrameCheck.Fail(reason!);
        if (!normal.Unitize()) return ViewFrameCheck.Fail("the surface normal is degenerate.");
        return new(Orient(corners, normal), null, null);
    }

    /// <summary>The four corners of a rectangular closed outline, or null with the reason.</summary>
    internal static Point3d[]? Corners(Curve outline, double tolerance, double angleTolerance, out string? reason)
    {
        reason = null;
        var points = new List<Point3d>();
        if (outline.TryGetPolyline(out var polyline)) points.AddRange(polyline);
        else
        {
            var segments = outline is PolyCurve polycurve ? polycurve.Explode() : outline.DuplicateSegments();
            if (segments.Length == 0) segments = [outline];
            foreach (var segment in segments)
            {
                if (!segment.IsLinear(tolerance)) { reason = "its edges are not all straight. The outline must be a rectangle."; return null; }
                points.Add(segment.PointAtStart);
            }
            points.Add(segments[^1].PointAtEnd);
        }
        // Closed loop without the repeated end point and without zero-length edges.
        var loop = new List<Point3d>();
        foreach (var p in points) if (loop.Count == 0 || loop[^1].DistanceTo(p) > tolerance) loop.Add(p);
        if (loop.Count > 1 && loop[0].DistanceTo(loop[^1]) <= tolerance) loop.RemoveAt(loop.Count - 1);
        // Merge collinear runs (a split edge is still one edge).
        for (var changed = true; changed && loop.Count > 2;)
        {
            changed = false;
            for (var i = 0; i < loop.Count; i++)
            {
                var a = loop[(i + loop.Count - 1) % loop.Count] ; var b = loop[i]; var c = loop[(i + 1) % loop.Count];
                var ab = b - a; var bc = c - b;
                if (ab.Unitize() && bc.Unitize() && ab * bc >= Math.Cos(angleTolerance)) { loop.RemoveAt(i); changed = true; break; }
            }
        }
        if (loop.Count != 4) { reason = $"its outline has {loop.Count} corners, not 4. The outline must be a rectangle."; return null; }
        for (var i = 0; i < 4; i++)
        {
            var a = loop[(i + 3) % 4]; var b = loop[i]; var c = loop[(i + 1) % 4];
            if (a.DistanceTo(b) <= tolerance * 10) { reason = "it is degenerate (an edge is shorter than 10 × the document tolerance)."; return null; }
            var ba = a - b; var bc = c - b; ba.Unitize(); bc.Unitize();
            if (Math.Abs(ba * bc) > Math.Sin(angleTolerance))
            {
                reason = $"its corners are not right angles ({RhinoMath.ToDegrees(Vector3d.VectorAngle(ba, bc)):0.#}°). The outline must be a rectangle.";
                return null;
            }
        }
        return [.. loop];
    }

    /// <summary>Right-handed frame on the rectangle with Z = normal: Y is the edge nearest world Z (a vertical or
    /// inclined frame stands upright), or nearest world Y for a horizontal frame (north up). Origin = lower-left.</summary>
    private static Rectangle3d Orient(IReadOnlyList<Point3d> corners, Vector3d normal)
    {
        var e0 = corners[1] - corners[0]; var e1 = corners[2] - corners[1]; e0.Unitize(); e1.Unitize();
        var reference = Math.Abs(normal * Vector3d.ZAxis) < .9 ? Vector3d.ZAxis : Vector3d.YAxis;
        var y = new[] { e0, -e0, e1, -e1 }.OrderByDescending(d => d * reference).First();
        var x = Vector3d.CrossProduct(y, normal); x.Unitize();
        y = Vector3d.CrossProduct(normal, x); y.Unitize();
        return Anchor(new Plane(corners[0], x, y), corners);
    }

    private static Rectangle3d Anchor(Plane plane, IEnumerable<Point3d> corners)
    {
        var uv = corners.Select(p => { plane.ClosestParameter(p, out var u, out var v); return (u, v); }).ToArray();
        var (u0, v0) = (uv.Min(p => p.u), uv.Min(p => p.v));
        plane.Origin = plane.PointAt(u0, v0);
        return new(plane, new Interval(0, uv.Max(p => p.u) - u0), new Interval(0, uv.Max(p => p.v) - v0));
    }

    /// <summary>Points a new frame at the model: a horizontal frame looks down (a plan); any other frame looks toward
    /// the middle of the scene. <see cref="Flip"/> reverses either choice.</summary>
    public static Rectangle3d Face(Rectangle3d frame, BoundingBox scene, double tolerance)
    {
        var normal = frame.Plane.ZAxis;
        if (Math.Abs(normal * Vector3d.ZAxis) >= .9) return normal.Z < 0 ? Flip(frame) : frame;
        if (!scene.IsValid) return frame;
        return frame.Plane.DistanceTo(scene.Center) > tolerance ? Flip(frame) : frame;
    }

    /// <summary>The same rectangle looking the other way: Z and X reverse, Y stays up, origin stays lower-left.</summary>
    public static Rectangle3d Flip(Rectangle3d frame)
    {
        var plane = frame.Plane;
        var flipped = new Plane(plane.PointAt(frame.X.Max, frame.Y.Min), -plane.XAxis, plane.YAxis);
        return new(flipped, new Interval(0, frame.X.Length), new Interval(0, frame.Y.Length));
    }

    /// <summary>Metres per document model unit (any Rhino unit system).</summary>
    public static double MetresPerUnit(RhinoDoc document) => RhinoMath.UnitScale(document.ModelUnitSystem, UnitSystem.Meters);

    /// <summary>Reads depth text in this document's units (<c>8 m</c>, <c>3000 mm</c>, <c>10'</c>, a bare number, or
    /// <c>auto</c>) with the settings unit parser; false with the error the dialog shows.</summary>
    public static bool TryParseDepth(RhinoDoc document, string? text, out DepthChoice choice, out string? error) =>
        DepthChoice.TryParse(text, MetresPerUnit(document), document.ModelAbsoluteTolerance, out choice, out error);

    // ---------------------------------------------------------------- the box

    /// <summary>The view box: profile = frame at the path start, path along the look direction (−Z), length = depth.</summary>
    public static Extrusion Build(Rectangle3d frame, double depth)
    {
        if (!(depth > 0) || !double.IsFinite(depth)) throw new ArgumentOutOfRangeException(nameof(depth), "Depth must be positive.");
        var plane = frame.Plane;
        var extrusion = new Extrusion();
        if (!extrusion.SetPathAndUp(plane.Origin, plane.Origin - plane.ZAxis * depth, plane.YAxis))
            throw new InvalidOperationException("Could not set the view box path.");
        // Profile coordinates: profile X = −frame X, profile Y = frame Y (up), profile Z = the look direction.
        var (x0, x1, y0, y1) = (frame.X.Min, frame.X.Max, frame.Y.Min, frame.Y.Max);
        var profile = new PolylineCurve([new(-x0, y0, 0), new(-x1, y0, 0), new(-x1, y1, 0), new(-x0, y1, 0), new(-x0, y0, 0)]);
        if (!extrusion.SetOuterProfile(profile, true) || !extrusion.IsValid) throw new InvalidOperationException("Could not build the view box.");
        return extrusion;
    }

    /// <summary>Reads a view box: frame from the profile at the path start, depth = path length.</summary>
    public static ViewFrameCheck ReadBox(Extrusion extrusion, double tolerance, double angleTolerance)
    {
        if (!extrusion.IsValid) return ViewFrameCheck.Fail("the view box geometry is invalid.");
        if (extrusion.ProfileCount != 1) return ViewFrameCheck.Fail("the view box has holes. Its profile must be one rectangle.");
        var depth = extrusion.PathStart.DistanceTo(extrusion.PathEnd);
        if (depth <= tolerance) return ViewFrameCheck.Fail("the view box has no depth.");
        var profilePlane = extrusion.GetProfilePlane(0);
        var plane = new Plane(extrusion.PathStart, -profilePlane.XAxis, profilePlane.YAxis);
        var tangent = extrusion.PathTangent;
        if (!tangent.Unitize() || plane.ZAxis * tangent > -Math.Cos(angleTolerance))
            return ViewFrameCheck.Fail("the view box's ends are not square to its depth.");
        using var profile = extrusion.Profile3d(0, 0);
        var corners = profile is null ? null : Corners(profile, tolerance, angleTolerance, out var reason);
        if (corners is null) return ViewFrameCheck.Fail("the view box's profile is not a rectangle.");
        return new(Anchor(plane, corners), depth, null);
    }

    // ---------------------------------------------------------------- user text

    /// <summary>Per-view settings from <c>m2d.*</c> user text (blank and <c>default</c> mean "not set here").</summary>
    public static SettingsRow ReadRow(ObjectAttributes attributes)
    {
        var strings = attributes.GetUserStrings();
        var values = new List<KeyValuePair<string, string>>();
        foreach (var key in strings.AllKeys)
        {
            if (key is null || !key.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) || key.Equals(Marker, StringComparison.OrdinalIgnoreCase)
                || key.Equals(ColorKey, StringComparison.OrdinalIgnoreCase)) continue;
            var value = strings[key]?.Trim() ?? "";
            if (value.Length == 0 || value.Equals(Inherit, StringComparison.OrdinalIgnoreCase)) continue;
            values.Add(KeyValuePair.Create(key[Prefix.Length..].ToLowerInvariant(), value));
        }
        return new(values);
    }

    /// <summary>Adds the marker and <c>default</c> for every missing key (the settings, then <c>m2d.color</c>); never
    /// overwrites a value. Returns how many.</summary>
    public static int WriteDefaults(ObjectAttributes attributes)
    {
        var added = 0;
        if (attributes.GetUserString(Marker) is null) { attributes.SetUserString(Marker, MarkerVersion); added++; }
        foreach (var key in Keys)
            if (attributes.GetUserString(Prefix + key) is null) { attributes.SetUserString(Prefix + key, Inherit); added++; }
        if (attributes.GetUserString(ColorKey) is null) { attributes.SetUserString(ColorKey, Inherit); added++; }
        return added;
    }

    // ---------------------------------------------------------------- document commands (one undo record each)

    /// <summary>Convert to view boxes: converts each referenced planar rectangle (surface or curve) in place into a view box,
    /// keeping its id, writes default user text to every view box and moves every view box onto <see cref="LayerName"/>.
    /// One undo record for the whole call. Non-interactive: every new box gets <paramref name="depth"/> (Auto by default,
    /// each box its own <see cref="ViewDepthScene.Auto"/>). Existing view boxes keep their depth.</summary>
    public static IReadOnlyList<ViewObjectResult> Validate(RhinoDoc document, IEnumerable<Guid> ids, DepthChoice depth = default, BoundingBox? scene = null) =>
        Validate(document, ids, _ => depth, scene);

    /// <summary>Convert to view boxes, asking for the depth once for the whole run (the "View box depth" dialog) when it
    /// converts at least one new object. Everything is checked first and nothing changes before the answer; a null
    /// answer (Cancel) returns a single <see cref="ViewObjectResult.Cancelled"/> result with no change and no undo
    /// record. Re-converting existing view boxes never asks.</summary>
    public static IReadOnlyList<ViewObjectResult> Validate(RhinoDoc document, IEnumerable<Guid> ids, DepthPrompt prompt, BoundingBox? scene = null)
    {
        var list = ids.Distinct().ToArray();
        using var depthScene = ViewDepthScene.Collect(document, list);
        var bounds = scene ?? depthScene.Bounds;
        var planned = new Dictionary<Guid, (Rectangle3d Frame, AutoDepth Auto)>();
        foreach (var id in list)
            if (Plan(document, id, bounds) is { } frame) planned[id] = (frame, depthScene.Auto(frame));
        var choice = DepthChoice.Auto;
        if (planned.Count > 0)
        {
            var answer = prompt(new ViewDepthPrompt([.. list.Where(planned.ContainsKey).Select(id => planned[id].Auto)],
                depthScene.MetresPerUnit, depthScene.Tolerance));
            if (answer is null) return [Cancelled("Convert to view boxes")];
            choice = answer.Value;
        }
        var results = new List<ViewObjectResult>();
        var record = document.BeginUndoRecord("M2D+ Convert to view boxes");
        try
        {
            foreach (var id in list)
                results.Add(planned.TryGetValue(id, out var plan) ? ConvertOne(document, id, plan.Frame, plan.Auto, choice) : ValidateOne(document, id));
        }
        finally { if (record != 0) document.EndUndoRecord(record); }
        return results;
    }

    /// <summary>The result for a run the user cancelled in the depth dialog.</summary>
    public static ViewObjectResult Cancelled(string title) => new(Guid.Empty, title, false, false, "cancelled; nothing changed.", true);

    /// <summary>The faced frame a new conversion would get, or null when the object is not converted (a view box, a
    /// clipping plane, a refusal: <see cref="ValidateOne"/> reports those).</summary>
    private static Rectangle3d? Plan(RhinoDoc document, Guid id, BoundingBox scene)
    {
        var obj = document.Objects.FindId(id);
        if (obj is null || obj is ClippingPlaneObject || IsViewBox(obj) || obj.Geometry is Extrusion) return null;
        var frame = Establish(obj.Geometry, document.ModelAbsoluteTolerance, document.ModelAngleToleranceRadians);
        return frame.IsValid ? Face(frame.Frame!.Value, scene, document.ModelAbsoluteTolerance) : null;
    }

    /// <summary>Everything Convert to view boxes does except a new conversion: not found, clipping planes, existing view
    /// boxes (re-adds missing keys, moves them back onto the views layer; never changes their depth) and refusals.</summary>
    private static ViewObjectResult ValidateOne(RhinoDoc document, Guid id)
    {
        var obj = document.Objects.FindId(id);
        if (obj is null) return new(id, id.ToString("D"), false, false, "not found in the Rhino document.");
        var label = Label(obj);
        var tolerance = document.ModelAbsoluteTolerance; var angle = document.ModelAngleToleranceRadians;
        if (obj is ClippingPlaneObject) return new(id, label, true, false, "a clipping plane is already a view.");
        if (IsViewBox(obj))
        {
            if (obj.Geometry is not Extrusion box)
                return new(id, label, false, false, $"is no longer a view box: Rhino turned it into a {Describe(obj.Geometry)}. Undo that edit, or reference a planar surface and convert it again.");
            var check = ReadBox(box, tolerance, angle);
            if (!check.IsValid) return new(id, label, false, false, check.Reason!);
            var attributes = obj.Attributes.Duplicate();
            var added = WriteDefaults(attributes);
            var layer = ViewsLayer(document);
            var moved = layer >= 0 && attributes.LayerIndex != layer;
            if (moved) attributes.LayerIndex = layer;
            if (added > 0 || moved) document.Objects.ModifyAttributes(obj, attributes, true);
            var notes = new List<string>();
            if (added > 0) notes.Add($"added {added} user-text keys");
            if (moved) notes.Add($"moved to layer {LayerName}");
            return new(id, label, true, notes.Count > 0, notes.Count > 0 ? $"view box; {string.Join("; ", notes)}." : $"view box, depth {Format(check.Depth!.Value)}.");
        }
        if (obj.Geometry is Extrusion)
            return new(id, label, false, false, "it is an extrusion or box, not a planar surface or rectangle. Reference one planar surface or a closed rectangle curve.");
        var frame = Establish(obj.Geometry, tolerance, angle);
        return new(id, label, false, false, frame.Reason ?? "it can't frame a view.");
    }

    private static ViewObjectResult ConvertOne(RhinoDoc document, Guid id, Rectangle3d faced, AutoDepth auto, DepthChoice choice)
    {
        var obj = document.Objects.FindId(id)!;
        var label = Label(obj);
        var depth = choice.Resolve(auto);
        using var extrusion = Build(faced, depth);
        if (!document.Objects.Replace(id, extrusion)) return new(id, label, false, false, "Rhino refused to replace it (is it locked or in a block?).");
        var replaced = document.Objects.FindId(id)!;
        var next = replaced.Attributes.Duplicate();
        WriteDefaults(next);
        if (string.IsNullOrWhiteSpace(next.Name)) next.Name = NextName(document);
        if (ViewsLayer(document) is var layerIndex and >= 0) next.LayerIndex = layerIndex;
        Wireframe(next);
        document.Objects.ModifyAttributes(replaced, next, true);
        return new(id, next.Name!, true, true, $"converted to a view box on layer {LayerName}, looking {Direction(-faced.Plane.ZAxis)}, depth {Format(depth)} ({DepthSource(choice, auto)}).");
    }

    private static string DepthSource(DepthChoice choice, AutoDepth auto) => choice.IsAuto ? "auto: " + auto.Describe() : "given";

    /// <summary>Flip view direction: the front face stays, the box extends to the other side with the same depth.</summary>
    public static IReadOnlyList<ViewObjectResult> Flip(RhinoDoc document, IEnumerable<Guid> ids)
    {
        var results = new List<ViewObjectResult>();
        var record = document.BeginUndoRecord("M2D+ Flip view direction");
        try
        {
            foreach (var id in ids.Distinct())
            {
                var obj = document.Objects.FindId(id);
                if (obj is null) { results.Add(new(id, id.ToString("D"), false, false, "not found in the Rhino document.")); continue; }
                if (!IsViewBox(obj) || obj.Geometry is not Extrusion box)
                {
                    results.Add(new(id, Label(obj), false, false, obj is ClippingPlaneObject
                        ? "flip a clipping plane in Rhino (Flip command)." : "not a view box yet. Convert to view boxes first."));
                    continue;
                }
                var check = ReadBox(box, document.ModelAbsoluteTolerance, document.ModelAngleToleranceRadians);
                if (!check.IsValid) { results.Add(new(id, Label(obj), false, false, check.Reason!)); continue; }
                var flipped = Flip(check.Frame!.Value);
                using var extrusion = Build(flipped, check.Depth!.Value);
                results.Add(document.Objects.Replace(id, extrusion)
                    ? new(id, Label(obj), true, true, $"now looking {Direction(-flipped.Plane.ZAxis)}.")
                    : new(id, Label(obj), false, false, "Rhino refused to replace it (is it locked?)."));
            }
        }
        finally { if (record != 0) document.EndUndoRecord(record); }
        return results;
    }

    /// <summary>The frame for a parallel viewport's visible rectangle, with its front face just in front of the scene;
    /// <see cref="ViewFrameCheck.Depth"/> runs to just behind the whole scene (Create uses Auto depth instead). An
    /// empty scene uses the camera target and the frame's longer side.</summary>
    public static ViewFrameCheck FrameFromViewport(ViewportInfo viewport, BoundingBox scene, double tolerance)
    {
        if (!viewport.IsParallelProjection)
            return ViewFrameCheck.Fail("M2D+ draws parallel views only. Switch the viewport to Parallel and try again.");
        var camera = new Plane(viewport.CameraLocation, viewport.CameraX, viewport.CameraY);
        if (!viewport.GetFrustum(out var left, out var right, out var bottom, out var top, out _, out _) || !camera.IsValid ||
            !(right > left) || !(top > bottom)) return ViewFrameCheck.Fail("the viewport has an invalid frame.");
        double front, depth;
        if (scene.IsValid)
        {
            var distances = scene.GetCorners().Select(p => (p - camera.Origin) * camera.ZAxis).ToArray();
            var margin = Math.Max(tolerance * 10, scene.Diagonal.Length * .01);
            front = distances.Max() + margin; depth = distances.Max() - distances.Min() + 2 * margin;
        }
        else { front = (viewport.TargetPoint - camera.Origin) * camera.ZAxis; depth = Math.Max(right - left, top - bottom); }
        var plane = new Plane(camera.Origin + camera.ZAxis * front + camera.XAxis * left + camera.YAxis * bottom, camera.XAxis, camera.YAxis);
        return new(new Rectangle3d(plane, new Interval(0, right - left), new Interval(0, top - bottom)), depth, null);
    }

    /// <summary>Create view from current viewport, non-interactive: bakes a named view box on the <see cref="LayerName"/>
    /// layer (one undo record), front face just in front of the scene, depth <paramref name="depth"/> (Auto: to the back
    /// of what is inside the frame). <paramref name="excluded"/>: view objects that are not scene.</summary>
    public static ViewObjectResult Create(RhinoDoc document, ViewportInfo viewport, IReadOnlyCollection<Guid>? excluded = null, DepthChoice depth = default) =>
        Create(document, viewport, excluded, _ => depth);

    /// <summary>Create view from current viewport, asking for the depth first (the "View box depth" dialog). A null
    /// answer (Cancel) adds nothing and leaves no undo record.</summary>
    public static ViewObjectResult Create(RhinoDoc document, ViewportInfo viewport, IReadOnlyCollection<Guid>? excluded, DepthPrompt prompt)
    {
        using var depthScene = ViewDepthScene.Collect(document, excluded ?? []);
        var frame = FrameFromViewport(viewport, depthScene.Bounds, document.ModelAbsoluteTolerance);
        if (!frame.IsValid) return new(Guid.Empty, "Create view", false, false, frame.Reason!);
        var auto = depthScene.Auto(frame.Frame!.Value);
        var answer = prompt(new ViewDepthPrompt([auto], depthScene.MetresPerUnit, depthScene.Tolerance));
        if (answer is not { } choice) return Cancelled("Create view");
        var value = choice.Resolve(auto);
        var record = document.BeginUndoRecord("M2D+ Create view from viewport");
        try
        {
            using var extrusion = Build(frame.Frame!.Value, value);
            var attributes = new ObjectAttributes { Name = NextName(document) };
            if (ViewsLayer(document) is var layerIndex and >= 0) attributes.LayerIndex = layerIndex;
            WriteDefaults(attributes);
            Wireframe(attributes);
            var id = document.Objects.AddExtrusion(extrusion, attributes);
            return id == Guid.Empty ? new(id, attributes.Name, false, false, "Rhino could not add the view box.")
                : new(id, attributes.Name, true, true, $"created on layer {LayerName}, depth {Format(value)} ({DepthSource(choice, auto)}).");
        }
        finally { if (record != 0) document.EndUndoRecord(record); }
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>Bounding box of what document mode would draw, excluding the given view objects. Fast (not tight).</summary>
    public static BoundingBox SceneBounds(RhinoDoc document, IReadOnlyCollection<Guid> excluded)
    {
        var bounds = BoundingBox.Empty;
        var settings = new ObjectEnumeratorSettings { NormalObjects = true, LockedObjects = true, HiddenObjects = false,
            ReferenceObjects = true, IncludeLights = false, IncludeGrips = false };
        foreach (var obj in document.Objects.GetObjectList(settings))
            if (!excluded.Contains(obj.Id) && SceneCapture.IsDrawable(document, obj)) bounds.Union(obj.Geometry.GetBoundingBox(false));
        return bounds;
    }

    /// <summary>The next free "M2D View 01" style object name.</summary>
    public static string NextName(RhinoDoc document)
    {
        var names = new HashSet<string>(document.Objects.GetObjectList(new ObjectEnumeratorSettings
            { NormalObjects = true, LockedObjects = true, HiddenObjects = true, ReferenceObjects = true })
            .Select(o => o.Attributes.Name ?? ""), StringComparer.OrdinalIgnoreCase);
        for (var i = 1; ; i++) if (!names.Contains($"M2D View {i:00}")) return $"M2D View {i:00}";
    }

    /// <summary>Index of the top-level <see cref="LayerName"/> layer. Created only when missing, cobalt and No Print
    /// (plot weight −1); an existing layer is used as it is, so the user's colour and print edits survive. −1 if Rhino
    /// refuses to add it (the object then keeps its layer).</summary>
    public static int ViewsLayer(RhinoDoc document)
    {
        var index = document.Layers.FindByFullPath(LayerName, -1);
        return index >= 0 ? index : document.Layers.Add(new Layer { Name = LayerName, Color = Cobalt, PlotWeight = -1 });
    }

    // A view box encloses the model it draws: show it as wires so it never hides the model in shaded modes.
    private static void Wireframe(ObjectAttributes attributes)
    {
        try
        {
            if (global::Rhino.Display.DisplayModeDescription.FindByName("Wireframe") is { } wireframe) attributes.SetDisplayModeOverride(wireframe);
        }
        catch (Exception e) when (e is InvalidOperationException or NullReferenceException or DllNotFoundException) { }
    }

    public static string Label(RhinoObject obj) => string.IsNullOrWhiteSpace(obj.Attributes.Name)
        ? $"{(IsViewBox(obj) ? "View box" : Describe(obj.Geometry))} {obj.Id.ToString("D")[..8]}" : obj.Attributes.Name;

    private static string Describe(GeometryBase? geometry) => geometry switch
    {
        Brep { Faces.Count: 1 } => "surface", Brep => "polysurface", Extrusion => "extrusion", Curve => "curve",
        Mesh => "mesh", SubD => "SubD", null => "nothing", _ => geometry.ObjectType.ToString().ToLowerInvariant()
    };

    private static string Format(double value) => value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>"down", "north", "south-west" etc., for messages.</summary>
    public static string Direction(Vector3d look)
    {
        look.Unitize();
        if (look.Z <= -.9) return "down";
        if (look.Z >= .9) return "up";
        var angle = Math.Atan2(look.Y, look.X) * 180 / Math.PI;
        var names = new[] { "east", "north-east", "north", "north-west", "west", "south-west", "south", "south-east" };
        return names[(int)Math.Round((angle + 360) % 360 / 45) % 8];
    }
}
