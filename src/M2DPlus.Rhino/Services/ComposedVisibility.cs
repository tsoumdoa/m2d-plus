using M2DPlus.Core;
using M2DPlus.Core.Visibility;
using M2DPlus.Core.Sections;
using M2DPlus.Rhino.Sections;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;
using Point = Rhino.Geometry.Point;

namespace M2DPlus.Rhino.Services;

internal sealed record VisibilityInput(GeometryBase Geometry, PreparedLeaf Source, bool Far = false, bool KernelCandidate = false);

/// <summary>Union over occluders, with separate ownership of planar and native candidates.</summary>
internal static class ComposedVisibility
{
    internal sealed record Tag(VisibilityInput Input, int Edge = -1);
    internal sealed record NativeRecord(Curve World, Tag Tag, LineClass Class, bool Profile, bool Duplicate);
    private sealed record NativeSpan(double A, double B, LineClass Class);
    internal sealed record HiddenSupport(int Edge, Vertex A, Vertex B);

    // Exact projected endpoint equality only. An everywhere-closer Hidden witness
    // proves every overlap that the dominated witness could prove. Crossing depth
    // graphs both survive; source/depth always come from an actual witness.
    internal static void RetainFrontHidden(Dictionary<(double,double,double,double),List<HiddenSupport>> groups, HiddenSupport support)
    {
        var a=support.A;var b=support.B;
        if (a.X>b.X || a.X==b.X && a.Y>b.Y) (a,b)=(b,a);
        support=support with {A=a,B=b};var key=(a.X,a.Y,b.X,b.Y);
        if (!groups.TryGetValue(key,out var front)) groups.Add(key,front=[]);
        if (front.Any(s=>s.A.Z>=a.Z && s.B.Z>=b.Z)) return;
        front.RemoveAll(s=>a.Z>=s.A.Z && b.Z>=s.B.Z);front.Add(support);
    }

    internal static string Solve(IReadOnlyList<VisibilityInput> inputs, ViewDrawing output, ViewRequest view,
        NativeSettings settings, CancellationToken token, BoundingBox? sceneBounds = null)
    {
        using var phase = output.Phases.Start("planar extraction");
        var plane = output.Frame.Plane; double tol = output.Tolerance;
        var extraction = new PlanarExtraction();
        var edgeInputs = new List<VisibilityInput>(); var curved = new List<VisibilityInput>();
        var points = new List<VisibilityInput>(); var owned = new List<GeometryBase>();
        var nativeEdges = new Dictionary<int, List<NativeSpan>>();
        var querySupports = new Dictionary<(Vertex A,Vertex B),int>();
        var aliases = new Dictionary<int,(int Edge,bool Reverse)>();
        var nativeCurves = new List<(Curve Curve, Tag Tag, LineClass Class, bool Profile)>();
        var records = new List<NativeRecord>();
        var localUnsetSources = new HashSet<string>(StringComparer.Ordinal);
        var localUnsetRegions = new List<BoundingBox>();
        Vertex Project(Point3d p) { var d = p - plane.Origin; return new(d * plane.XAxis, d * plane.YAxis, d * plane.ZAxis); }
        Point3d World(Vertex p) => plane.PointAt(p.X, p.Y) + plane.ZAxis * p.Z;
        try
        {
            foreach (var input in VisibilityInputOrder.Canonical(inputs, i => i))
            {
                token.ThrowIfCancellationRequested();
                if (input.Geometry is Point) { points.Add(input); continue; }
                int start = extraction.Edges.Count;
                if (extraction.Add(input.Geometry, Transform.Identity, plane, tol, input.Source.Identity, token, nativeMeshCandidates: true, tangents: view.Settings?.Tangents ?? false))
                    for (int i = start; i < extraction.Edges.Count; i++) edgeInputs.Add(input);
                else curved.Add(input);
            }
            settings.ObservePlanarScene?.Invoke(extraction, plane);
            settings.ObserveKernelSources?.Invoke(edgeInputs.Select(i => i.Source.Identity).Distinct(StringComparer.Ordinal).ToArray());
            phase.Next("kernel solve");
            var kernel = EdgeFaceKernel.Solve(extraction.Faces, extraction.Edges, new(tol, Math.Min(System.Environment.ProcessorCount, 8))
            {
                Progress = (done, total) => NativeDrawing.ReportStep(ViewSteps.ExactLines, done, total),
                ObserveWork = work => {
                    output.Phases.Add("kernel exact fallback CPU work",work.ExactSeconds);
                    output._diagnostics.Add($"Kernel certificates: exact prepared faces={work.ExactFaces}; budget-limited candidates={work.BudgetEdges}.");
                }
            }, token);
            phase.Next("composition setup");
            output._diagnostics.Add($"Kernel predicates: face tests={kernel.FaceTests}; exact fallbacks={kernel.ExactFallbacks}.");
            output._diagnostics.AddRange(kernel.Diagnostics);
            EdgeFaceKernel.PointQuery? query = null;
            try { if (curved.Count != 0 || points.Count != 0) query = new(extraction.Faces, tol, token); }
            catch (ArithmeticException ex) { output._diagnostics.Add(ex.Message); }
            var kernelByEdge = kernel.Intervals.GroupBy(i => i.Edge).ToDictionary(g => g.Key, g => g.ToArray());
            var native = curved.Select(i => new Tag(i)).ToList();
            var curvedBounds = curved.Where(i => i.Geometry is Brep or Mesh or Surface or Extrusion)
                .Select(i => i.Geometry.GetBoundingBox(Transform.PlaneToPlane(plane, Plane.WorldXY))).ToArray();
            bool Overlaps(BoundingBox a) => curvedBounds.Any(b => a.Min.X <= b.Max.X + tol && a.Max.X >= b.Min.X - tol &&
                a.Min.Y <= b.Max.Y + tol && a.Max.Y >= b.Min.Y - tol);
            // Curves cannot occlude. No curved occluder means no planar candidates enter HLD.
            for (int i = 0; i < extraction.Edges.Count; i++)
            {
                token.ThrowIfCancellationRequested(); var e = extraction.Edges[i];
                // H(planar) union H(curved) is already Hidden when planar coverage
                // is complete. These non-occluding curves need no foreign query.
                if (!kernelByEdge.TryGetValue(i, out var known) || known.All(k => k.Class == VisibilityClass.Hidden)) continue;
                if (!Overlaps(new BoundingBox(new[] { new Point3d(e.A.X, e.A.Y, e.A.Z), new Point3d(e.B.X, e.B.Y, e.B.Z) }))) continue;
                nativeEdges.Add(i, []);
                // Exact 3D endpoint equality, including reverse orientation. One
                // non-occluding query covers all identical owned candidates; HLD
                // must not be asked to choose an owner among those aliases.
                if (querySupports.TryGetValue((e.A,e.B),out var forward)) { aliases.Add(i,(forward,false)); continue; }
                if (querySupports.TryGetValue((e.B,e.A),out var reverse)) { aliases.Add(i,(reverse,true)); continue; }
                querySupports.Add((e.A,e.B),i);
                var curve = new LineCurve(World(e.A), World(e.B)); owned.Add(curve);
                native.Add(new(new(curve, edgeInputs[i].Source, edgeInputs[i].Far), i));
            }
            foreach (var p in points)
                if (Overlaps(p.Geometry.GetBoundingBox(Transform.PlaneToPlane(plane, Plane.WorldXY)))) native.Add(new(p));
            int missingCoverage = 0, duplicateUnresolved = 0;
            var handledPoints = new HashSet<VisibilityInput>();
            if (native.Count != 0)
            {
                using var viewport = new ViewportInfo();
                var bounds = sceneBounds ?? BoundingBox.Empty;
                if (sceneBounds is null) foreach (var i in inputs) bounds.Union(i.Geometry.GetBoundingBox(true));
                NativeViewport.Configure(viewport,output.Frame,view.Direction,bounds,tol);
                var nativeOptions = NativeHldOptions.For(view, settings);
                var parameters = new HiddenLineDrawingParameters { AbsoluteTolerance = tol, Flatten = false, IncludeHiddenCurves = nativeOptions.IncludeHiddenCurves,
                    IncludeTangentEdges = view.Settings?.Tangents ?? false, IncludeTangentSeams = view.Settings?.Seams ?? false };
                parameters.SetViewport(viewport);
                foreach (var tag in VisibilityInputOrder.Native(native, t => t.Input with { KernelCandidate = t.Edge >= 0 }, settings))
                {
                    token.ThrowIfCancellationRequested();
                    if (!parameters.AddGeometry(tag.Input.Geometry, tag)) throw new InvalidOperationException("Native rejected composition candidate");
                }
                NativeDrawing.CountHld(); output.NativeCalls++;
                phase.Next("native solve");
                nativeOptions.Begin(output, native.Select(t => t.Input), settings);
                using var drawing = HiddenLineDrawing.Compute(parameters, nativeOptions.MultipleThreads, NativeDrawing.HldProgress, token);
                phase.Next("native extraction and duplicates");
                token.ThrowIfCancellationRequested();
                if (drawing is null || !drawing.WorldToHiddenLine.TryGetInverse(out var inverse)) throw new InvalidOperationException("Composition HLD failed");
                var nativeParents = new HashSet<int>();
                foreach (var segment in drawing.Segments)
                {
                    token.ThrowIfCancellationRequested(); var visibility = segment.SegmentVisibility;
                    if (visibility == HiddenLineDrawingSegment.Visibility.Unset)
                    {
                        var evidence = NativeUncertainty.Inspect(segment, t => (t as Tag)?.Input.Source, Transform.PlanarProjection(plane) * inverse);
                        if (evidence.Kind == UnsetKind.EmptySlot) { output.EmptyNativeSlots++; continue; }
                        if (evidence.Kind == UnsetKind.ZeroLength) { output.ZeroLengthNativeSegments++; continue; }
                        if (segment.ParentCurve?.SourceObject?.Tag is Tag localTag && NativeUncertainty.TryDetach(segment, inverse, out var localWorld))
                        {
                            owned.Add(localWorld!);
                            localUnsetSources.Add(localTag.Input.Source.Identity);
                            localUnsetRegions.Add(localWorld!.GetBoundingBox(Transform.PlanarProjection(plane)));
                            records.Add(new(localWorld!, localTag, LineClass.Unresolved, segment.IsSceneSilhouette, false));
                            output.RoutingInvalid++;
                            output._diagnostics.Add($"Local composition Unset retained: source={localTag.Input.Source.Identity}; interval remains Unresolved unless planar-hidden proof covers it.");
                            continue;
                        }
                        // A real Unset can be the only native candidate. Its source is quarantined below.
                        if (evidence.Source is { } source) output.UncertainRoutingSources.Add(source.Identity);
                        else output.RoutingQuarantine = true;
                        output.RoutingInvalid++;
                        output._diagnostics.Add($"Composition Unset {segment.Index}: {evidence.Kind}; source={evidence.Source?.Identity}; {evidence.Detail}");
                        continue;
                    }
                    if (visibility == HiddenLineDrawingSegment.Visibility.Projecting) { output.ProjectingSegments++; continue; }
                    if (segment.ParentCurve?.SourceObject?.Tag is not Tag tag || segment.CurveGeometry is not { } proxy)
                        throw new InvalidOperationException("Unattributed composition candidate");
                    if (tag.Edge < 0) nativeParents.Add(segment.ParentCurve.Index);
                    var kind = visibility == HiddenLineDrawingSegment.Visibility.Visible ? LineClass.Visible :
                        visibility == HiddenLineDrawingSegment.Visibility.Hidden ? LineClass.Hidden : LineClass.Unresolved;
                    if (visibility == HiddenLineDrawingSegment.Visibility.Duplicate)
                    {
                        output.DuplicateSegments++;
                        if (extraction.Faces.Count == 0 && tag.Edge < 0) continue;
                    }
                    var world = proxy.DuplicateCurve(); world.EnsurePrivateCopy(); world.Transform(inverse); owned.Add(world);
                    records.Add(new(world, tag, kind, segment.IsSceneSilhouette, visibility == HiddenLineDrawingSegment.Visibility.Duplicate));
                    settings.ObserveCompositionRecord?.Invoke(tag.Input.Source.Identity,tag.Edge,visibility == HiddenLineDrawingSegment.Visibility.Duplicate,kind,world);
                }
                output.CurvedCandidateEdges = nativeParents.Count;
                phase.Next("native ray verification");
                // HLD sees curved occluders only. The exact planar union below is
                // a proof, not a native contradiction (including at its boundaries).
                if (settings.Verification.MaxSamples != 0)
                using (var verifier = new NativeVisibilityVerifier(curved, output, settings.Verification, token, new PlanarExtraction()))
                {
                    var verified = new List<NativeRecord>(); var checkedRecords = 0;
                    foreach (var record in records)
                    {
                        NativeDrawing.ReportStep(ViewSteps.Checking, checkedRecords++, records.Count);
                        var spans = verifier.Verify(record.World, record.Class, record.Tag.Input.Source.Identity, token);
                        if (spans.Length == 1 && spans[0].Class == record.Class) { verified.Add(record); continue; }
                        foreach (var span in spans)
                        {
                            var part = record.World.Trim(span.A,span.B) ?? throw new InvalidOperationException("Native verifier span trim failed");
                            owned.Add(part); verified.Add(record with { World = part, Class = span.Class });
                        }
                    }
                    records = verified; verifier.Report();
                }
                else output._diagnostics.Add("Native ray verifier disabled by sample budget; native confidence unverified.");
                phase.Next("native extraction and duplicates");
                var retained = new List<NativeRecord>();
                if (records.Any(r => r.Duplicate) && kernel.Intervals.Any(i => i.Class == VisibilityClass.Hidden))
                {
                    // Fully planar-hidden candidates were deliberately omitted from
                    // HLD. They remain independent, owned overlap witnesses.
                    using var duplicates = new RTree();
                    var toXY = Transform.PlaneToPlane(plane,Plane.WorldXY);
                    foreach (var duplicate in records.Where(r => r.Duplicate))
                    {
                        var box = duplicate.World.GetBoundingBox(toXY);
                        var projected = new BoundingBox(new Point3d(box.Min.X,box.Min.Y,-tol),new Point3d(box.Max.X,box.Max.Y,tol));
                        projected.Inflate(tol); duplicates.Insert(projected,0);
                    }
                    var frontHidden = new Dictionary<(double,double,double,double),List<HiddenSupport>>();
                    foreach (var span in kernel.Intervals.Where(i => i.Class == VisibilityClass.Hidden))
                    {
                        token.ThrowIfCancellationRequested(); var edge = extraction.Edges[span.Edge];
                        var a = new Vertex(edge.A.X+(edge.B.X-edge.A.X)*span.Start,edge.A.Y+(edge.B.Y-edge.A.Y)*span.Start,edge.A.Z+(edge.B.Z-edge.A.Z)*span.Start);
                        var b = new Vertex(edge.A.X+(edge.B.X-edge.A.X)*span.End,edge.A.Y+(edge.B.Y-edge.A.Y)*span.End,edge.A.Z+(edge.B.Z-edge.A.Z)*span.End);
                        bool overlaps = false;
                        duplicates.Search(new BoundingBox(new Point3d(Math.Min(a.X,b.X),Math.Min(a.Y,b.Y),-tol),new Point3d(Math.Max(a.X,b.X),Math.Max(a.Y,b.Y),tol)),(_,_) => overlaps=true);
                        if (!overlaps) continue;
                        RetainFrontHidden(frontHidden,new(span.Edge,a,b));
                    }
                    foreach (var support in frontHidden.Values.SelectMany(s=>s))
                    {
                        var world = new LineCurve(World(support.A),World(support.B)); owned.Add(world);
                        retained.Add(new(world,new(edgeInputs[support.Edge],support.Edge),LineClass.Hidden,extraction.Profiles[support.Edge],false));
                    }
                }
                foreach (var record in extraction.Edges.Count == 0 && extraction.Faces.Count == 0 ? records : ResolveDuplicates(records, plane, tol, owned, token,retained,
                    () => output._diagnostics.Add("Duplicate certificate witness budget exceeded; candidate retained as Unresolved.")))
                {
                    var tag = record.Tag; var world = record.World;
                    if (tag.Edge >= 0)
                    {
                        var e = extraction.Edges[tag.Edge]; var a = World(e.A); var b = World(e.B); var delta = b - a;
                        double Param(Point3d p) => Math.Clamp(((p - a) * delta) / delta.SquareLength, 0, 1);
                        double Snap(double t)
                        {
                            double roundoff = Math.Min(tol/64,128 * 2.220446049250313e-16 * Math.Max(1, Math.Max(a.DistanceTo(Point3d.Origin), b.DistanceTo(Point3d.Origin)))) / delta.Length;
                            return t <= roundoff ? 0 : t >= 1 - roundoff ? 1 : t;
                        }
                        double u = Snap(Param(world.PointAtStart)), v = Snap(Param(world.PointAtEnd));
                        nativeEdges[tag.Edge].Add(new(Math.Min(u, v), Math.Max(u, v), record.Class));
                    }
                    else if (!record.Duplicate || record.Class == LineClass.Unresolved)
                    {
                        if (record.Duplicate) duplicateUnresolved++;
                        nativeCurves.Add((world, tag, record.Class, record.Profile));
                    }
                }

                foreach (var point in drawing.Points)
                {
                    token.ThrowIfCancellationRequested();
                    if (point.SourceObject?.Tag is not Tag tag) { output.RoutingQuarantine = true; output.RoutingInvalid++; continue; }
                    handledPoints.Add(tag.Input);
                    var world = point.Location; world.Transform(inverse);
                    var k = query?.Classify(Project(world), token) ?? VisibilityClass.Unresolved;
                    if (point.PointVisibility == HiddenLineDrawingPoint.Visibility.Hidden || k == VisibilityClass.Hidden) { output.HiddenPoints++; continue; }
                    if (point.PointVisibility != HiddenLineDrawingPoint.Visibility.Visible || k == VisibilityClass.Unresolved)
                    { output.RoutingInvalid++; output._diagnostics.Add($"Uncertain point suppressed: {tag.Input.Source.Identity}"); continue; }
                    output.Pieces.Add(new(new Point(plane.ClosestPoint(world)), tag.Input.Source, LineClass.Visible, new Point(world)));
                }
            }
            foreach (var (edge,alias) in aliases)
                nativeEdges[edge].AddRange(nativeEdges[alias.Edge].Select(s => alias.Reverse ? new NativeSpan(1-s.B,1-s.A,s.Class) : s));
            output._diagnostics.Add($"Native query reuse: canonical={querySupports.Count}; aliases={aliases.Count}; owned={nativeEdges.Count}.");
            phase.Next("output construction");
            output.KernelEdges = kernelByEdge.Count; output.PlanarNativeEdges = nativeEdges.Count;
            output.NativeEdges = nativeEdges.Count + output.CurvedCandidateEdges; output.NativeFragments = nativeCurves.Count;
            foreach (var interval in kernel.Intervals)
            {
                token.ThrowIfCancellationRequested(); var e = extraction.Edges[interval.Edge];
                var input = edgeInputs[interval.Edge]; var kind = PlanarExtraction.OutputClass(interval.Class);
                void Emit(double a, double b, LineClass c)
                {
                    if (b <= a) return;
                    double edgeLength = Math.Sqrt(Math.Pow(e.B.X-e.A.X,2)+Math.Pow(e.B.Y-e.A.Y,2)+Math.Pow(e.B.Z-e.A.Z,2));
                    double roundoff = 128 * 2.220446049250313e-16 * Math.Max(1, Math.Max(Math.Abs(e.A.X)+Math.Abs(e.A.Y)+Math.Abs(e.A.Z),Math.Abs(e.B.X)+Math.Abs(e.B.Y)+Math.Abs(e.B.Z)));
                    if ((a > 0 || b < 1) && edgeLength > tol && (b-a)*edgeLength <= Math.Min(roundoff,tol/64))
                    { output.ProjectingSegments++; return; }
                    var wa = World(e.A); var wb = World(e.B); var delta = wb - wa;
                    Add(new LineCurve(wa + delta * a, wa + delta * b), input, c, extraction.Profiles[interval.Edge], output, kernelOwned: true);
                }
                if (kind == LineClass.Hidden || !nativeEdges.TryGetValue(interval.Edge, out var spans)) { Emit(interval.Start, interval.End, kind); continue; }
                var ends = spans.SelectMany(s => new[] { Math.Max(interval.Start, s.A), Math.Min(interval.End, s.B) })
                    .Where(t => t > interval.Start && t < interval.End).Append(interval.Start).Append(interval.End).Distinct().Order().ToArray();
                for (int j = 1; j < ends.Length; j++)
                {
                    double mid = (ends[j - 1] + ends[j]) / 2;
                    var hits = spans.Where(s => s.A <= mid && s.B >= mid).ToArray();
                    if (hits.Length == 0)
                    {
                        if (missingCoverage++ < 8) output._diagnostics.Add($"Missing native coverage: edge={interval.Edge}; source={input.Source.Identity}; t={ends[j-1]:R}..{ends[j]:R}; A={e.A}; B={e.B}");
                    }
                    var c = hits.Length == 0 || hits.Any(s => s.Class == LineClass.Unresolved) || kind == LineClass.Unresolved ? LineClass.Unresolved :
                        kind == LineClass.Hidden || hits.Any(s => s.Class == LineClass.Hidden) ? LineClass.Hidden : LineClass.Visible;
                    Emit(ends[j - 1], ends[j], c);
                }
            }
            foreach (var c in nativeCurves)
            {
                token.ThrowIfCancellationRequested();
                if (c.Class == LineClass.Hidden || extraction.Faces.Count == 0)
                { Add(c.Curve.DuplicateCurve(), c.Tag.Input, c.Class, c.Profile, output); continue; }
                phase.Next("curve-plane events");
                var parts = ClipCurve(c.Curve, extraction.Faces, query, plane, tol, token,
                    () => output._diagnostics.Add("Curve/plane certificate work budget exceeded; candidate retained as Unresolved.")).ToArray();
                phase.Next("output construction");
                foreach (var part in parts)
                    // A complete planar-hidden interval decides the union even when
                    // native duplicate attribution cannot decide foreign visibility.
                    Add(part.Curve, c.Tag.Input, part.Class == LineClass.Hidden ? LineClass.Hidden :
                        c.Class == LineClass.Unresolved ? LineClass.Unresolved : part.Class, c.Profile, output);
            }
            foreach (var p in points.Where(p => !handledPoints.Contains(p)))
            {
                token.ThrowIfCancellationRequested();
                // Missing records from a submitted native point are uncertainty, not visible.
                if (native.Any(t => t.Input == p)) { output.RoutingInvalid++; continue; }
                var world = ((Point)p.Geometry).Location;
                var k = query?.Classify(Project(world), token) ?? VisibilityClass.Unresolved;
                if (k == VisibilityClass.Hidden) output.HiddenPoints++;
                else if (k == VisibilityClass.Visible) output.Pieces.Add(new(new Point(plane.ClosestPoint(world)), p.Source, LineClass.Visible, new Point(world)));
                else { output.RoutingInvalid++; output._diagnostics.Add($"Kernel cannot decide point {p.Source.Identity}"); }
            }
            output._diagnostics.Add($"Composition coverage: missing={missingCoverage}; unresolved curved duplicate intervals={duplicateUnresolved}.");
            foreach (var piece in output.Pieces)
                if (output.RoutingQuarantine || output.UncertainRoutingSources.Contains(piece.SourceIdentity)) piece.Class = LineClass.Unresolved;
            if (localUnsetSources.Count != 0)
                NativeUncertainty.ApplyFootprints(output, inputs.Select(i => i.Source).ToArray(), false, [], localUnsetRegions, tol, token,
                    localSources: localUnsetSources, verificationInputs: inputs);
            phase.Next("interval union");
            if (extraction.Edges.Count != 0 || extraction.Faces.Count != 0) Reconcile(output, inputs, token);
            return $"{(curved.Count == 0 ? "Planar kernel" : "Planar kernel + curved native HLD")}; kernel edges={output.KernelEdges}; native candidates={output.NativeEdges}; planar edges submitted={nativeEdges.Count}; curved candidate edges={output.CurvedCandidateEdges}; native fragments={output.NativeFragments}; native calls={output.NativeCalls}";
        }
        finally { foreach (var g in owned) g.Dispose(); }
    }

    internal static IEnumerable<NativeRecord> ResolveDuplicates(List<NativeRecord> records, Plane plane, double tolerance,
        List<GeometryBase> owned, CancellationToken token, IReadOnlyList<NativeRecord>? independent = null, Action? budgetExceeded = null)
    {
        int candidateCount = records.Count;
        if (independent is not null) records = records.Concat(independent).ToList();
        var projection = Transform.PlanarProjection(plane); var toXY = Transform.PlaneToPlane(plane, Plane.WorldXY);
        using var tree = new RTree();
        var projected = new Curve[records.Count]; var depths = new BoundingBox[records.Count];
        try
        {
            for (int i = 0; i < records.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                projected[i] = records[i].World.DuplicateCurve(); projected[i].Transform(projection);
                depths[i] = records[i].World.GetBoundingBox(toXY);
                if (!records[i].Duplicate && records[i].Class != LineClass.Unresolved)
                { var bounds = projected[i].GetBoundingBox(true); bounds.Inflate(tolerance); tree.Insert(bounds, i); }
            }
            for (int i = 0; i < candidateCount; i++)
            {
                token.ThrowIfCancellationRequested(); var record = records[i];
                if (!record.Duplicate) { yield return record; continue; }
                var nearby = new List<int>(); tree.Search(projected[i].GetBoundingBox(true), (_, a) => nearby.Add(a.Id));
                if (nearby.Count > 4096)
                { budgetExceeded?.Invoke(); yield return record with { Class = LineClass.Unresolved }; continue; }
                var references = new List<Curve>(); var certified = new List<Interval>();
                try
                {
                    foreach (int j in nearby)
                    {
                        token.ThrowIfCancellationRequested();
                        // No complete-overlap front proof can use an everywhere-
                        // behind witness. Use the maximum budget allowed by any
                        // downstream roundoff certificate, preserving all ties.
                        if (depths[j].Max.Z < depths[i].Min.Z - tolerance/64) continue;
                        if (TryLinearOverlap(record.World,records[j].World,plane,tolerance,out var ca,out var cb))
                        {
                            using var candidate = CurveIntervals.Trim(record.World,ca);
                            using var witness = CurveIntervals.Trim(records[j].World,cb);
                            if (candidate is not null && witness is not null && FrontWitness(candidate,witness,plane,tolerance,token)) certified.Add(ca);
                            continue;
                        }
                        // A complete closer-depth envelope proves that a retained native
                        // witness owns this projected overlap, even after planar clipping.
                        double roundoff = Math.Min(tolerance / 64, 128 * 2.220446049250313e-16 * Math.Max(1,
                            Math.Max(Math.Abs(depths[j].Min.Z),Math.Abs(depths[i].Max.Z))));
                        if (depths[j].Min.Z >= depths[i].Max.Z - roundoff)
                        { references.Add(projected[j].DuplicateCurve()); continue; }
                        using var projectedEvents = Intersection.CurveCurve(projected[i], projected[j], tolerance, tolerance);
                        if (projectedEvents is not null)
                            foreach (var overlap in projectedEvents)
                            {
                                if (!overlap.IsOverlap) continue;
                                using var a = CurveIntervals.Trim(record.World, overlap.OverlapA);
                                using var b = CurveIntervals.Trim(records[j].World, overlap.OverlapB);
                                if (a is null || b is null || !FrontWitness(a, b, plane, tolerance,token)) continue;
                                var reference = CurveIntervals.Trim(projected[j], overlap.OverlapB);
                                if (reference is not null) references.Add(reference);
                            }
                        using var events = Intersection.CurveCurve(record.World, records[j].World, tolerance, tolerance);
                        if (events is null) continue;
                        foreach (var hit in events)
                        {
                            if (!hit.IsOverlap) continue;
                            using var candidate = CurveIntervals.Trim(record.World,hit.OverlapA);
                            using var witness = CurveIntervals.Trim(records[j].World,hit.OverlapB);
                            if (candidate is null || witness is null || !FrontWitness(candidate,witness,plane,tolerance,token)) continue;
                            var reference = witness.DuplicateCurve();
                            reference.Transform(projection); references.Add(reference);
                        }
                    }
                    var parts = EdgeProvenance.Split(projected[i], references.Select(c => (c, EdgeOrigin.Original)), tolerance);
                    foreach (var part in parts)
                    {
                        using (part)
                        {
                            var domain = part.Curve.Domain;
                            var ends = certified.SelectMany(i => new[] {i.Min,i.Max}).Where(t => domain.IncludesParameter(t))
                                .Append(domain.Min).Append(domain.Max).Distinct().Order().ToArray();
                            for (int k=1;k<ends.Length;k++)
                            {
                                var interval = new Interval(ends[k-1],ends[k]);
                                var world = CurveIntervals.Trim(record.World, interval);
                                if (world is null) continue; // exactly point-projecting alias
                                owned.Add(world);
                                bool covered = part.Origin == EdgeOrigin.Original || certified.Any(i => i.IncludesParameter(interval.Mid));
                                yield return record with { World = world, Class = covered ? LineClass.Hidden : LineClass.Unresolved };
                            }
                        }
                    }
                }
                finally { foreach (var c in references) c.Dispose(); }
            }
        }
        finally { foreach (var c in projected) c?.Dispose(); }
    }

    internal static bool TryLinearOverlap(Curve candidate, Curve witness, Plane plane, double tolerance, out Interval onCandidate, out Interval onWitness)
    {
        onCandidate = onWitness = Interval.Unset;
        if (candidate is not (LineCurve or NurbsCurve) || witness is not (LineCurve or NurbsCurve)) return false;
        using var a = candidate.ToNurbsCurve(); using var b = witness.ToNurbsCurve();
        bool Affine(NurbsCurve c) => c.Degree == 1 && c.Points.Count == 2 && c.Points[0].Weight > 0 &&
            double.IsFinite(c.Points[0].Weight) && c.Points[0].Weight == c.Points[1].Weight;
        if (!Affine(a) || !Affine(b)) return false;
        double scale = Math.Max(1,new[] {a.PointAtStart,a.PointAtEnd,b.PointAtStart,b.PointAtEnd}.Max(p => p.DistanceTo(Point3d.Origin)));
        double roundoff = Math.Min(tolerance/64,128*2.220446049250313e-16*scale);
        var toXY = Transform.PlaneToPlane(plane,Plane.WorldXY); a.Transform(toXY); b.Transform(toXY);
        var p = a.PointAtStart; var q = a.PointAtEnd; var u = b.PointAtStart; var v = b.PointAtEnd;
        double dx=q.X-p.X, dy=q.Y-p.Y, length=Math.Sqrt(dx*dx+dy*dy);
        if (length <= roundoff) return false;
        double Distance(Point3d t) => Math.Abs((t.X-p.X)*dy-(t.Y-p.Y)*dx)/length;
        bool x = Math.Abs(dx)>=Math.Abs(dy); double Axis(Point3d t) => x ? t.X : t.Y;
        double da=Axis(q)-Axis(p), db=Axis(v)-Axis(u);
        if (db == 0) return false;
        double t0=(Axis(u)-Axis(p))/da, t1=(Axis(v)-Axis(p))/da;
        double lo=Math.Max(0,Math.Min(t0,t1)), hi=Math.Min(1,Math.Max(t0,t1));
        if (hi<=lo) return false;
        double s0=Math.Clamp((Axis(p)+da*lo-Axis(u))/db,0,1), s1=Math.Clamp((Axis(p)+da*hi-Axis(u))/db,0,1);
        Point3d At(double t) => u+(v-u)*t;
        // Test the actual overlap. Extrapolating the short proxy's tiny native
        // skew across a long witness amplifies inverse-frame error needlessly.
        if (Distance(At(s0))>roundoff || Distance(At(s1))>roundoff) return false;
        onCandidate = new(candidate.Domain.ParameterAt(lo),candidate.Domain.ParameterAt(hi));
        onWitness = new(witness.Domain.ParameterAt(Math.Min(s0,s1)),witness.Domain.ParameterAt(Math.Max(s0,s1)));
        return true;
    }

    internal static bool FrontWitness(Curve candidate, Curve witness, Plane plane, double tolerance, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var transform = Transform.PlaneToPlane(plane, Plane.WorldXY);
        using var a = candidate.ToNurbsCurve(); using var b = witness.ToNurbsCurve();
        if (a.Points.Count > 4096 || b.Points.Count > 4096) return false;
        a.Transform(transform); b.Transform(transform);
        double DistanceXY(Point3d p, Point3d q) => Math.Sqrt((p.X-q.X)*(p.X-q.X)+(p.Y-q.Y)*(p.Y-q.Y));
        if (DistanceXY(a.PointAtStart, b.PointAtEnd) < DistanceXY(a.PointAtStart, b.PointAtStart)) b.Reverse();
        // Native can represent the same straight support with different degrees,
        // knots and weights. Positive rational basis functions keep each curve in
        // its control hull. A linear depth graph therefore certifies the entire
        // overlap without requiring an identical parameterization.
        var start = a.PointAtStart; var end = a.PointAtEnd;
        double dx = end.X-start.X, dy = end.Y-start.Y, square = dx*dx+dy*dy;
        double scale = Math.Max(1, a.Points.Select(p => p.Location.DistanceTo(Point3d.Origin)).Concat(b.Points.Select(p => p.Location.DistanceTo(Point3d.Origin))).Max());
        double roundoff = Math.Min(tolerance/64,128*2.220446049250313e-16*scale);
        if (square > roundoff*roundoff && DistanceXY(start,b.PointAtStart) <= tolerance/16 && DistanceXY(end,b.PointAtEnd) <= tolerance/16)
        {
            bool OnSupport(ControlPoint p, bool witness)
            {
                if (!double.IsFinite(p.Weight) || p.Weight <= 0) return false;
                var q = p.Location; double t = ((q.X-start.X)*dx+(q.Y-start.Y)*dy)/square;
                double perpendicular = Math.Abs((q.X-start.X)*dy-(q.Y-start.Y)*dx)/Math.Sqrt(square);
                double depth = q.Z-(start.Z+t*(end.Z-start.Z));
                return perpendicular <= roundoff && (witness ? depth >= -roundoff : Math.Abs(depth) <= roundoff);
            }
            if (a.Points.All(p => OnSupport(p,false)) && b.Points.All(p => OnSupport(p,true))) return true;
        }
        if (a.Degree != b.Degree || a.Points.Count != b.Points.Count || a.Knots.Count != b.Knots.Count)
            return RationalDepthCertificate.Front(a, b, tolerance, roundoff,token);
        a.Domain = new(0,1); b.Domain = new(0,1);
        for (int i=0; i<a.Knots.Count; i++) if (a.Knots[i] != b.Knots[i]) return RationalDepthCertificate.Front(a, b, tolerance, roundoff,token);
        double ratio = a.Points[0].Weight / b.Points[0].Weight;
        for (int i=0; i<a.Points.Count; i++)
        {
            if ((i & 63) == 0) token.ThrowIfCancellationRequested();
            var p = a.Points[i]; var q = b.Points[i];
            if (p.Weight <= 0 || q.Weight <= 0 || p.Weight != q.Weight * ratio ||
                DistanceXY(p.Location,q.Location) > tolerance / 16 || q.Location.Z < p.Location.Z -
                    Math.Min(tolerance / 64, 128 * 2.220446049250313e-16 * Math.Max(1,Math.Max(Math.Abs(p.Location.Z),Math.Abs(q.Location.Z)))))
                return RationalDepthCertificate.Front(a, b, tolerance, roundoff,token);
        }
        // Matching rational basis with positive weights: the depth difference is
        // bounded by its control coefficients throughout the complete overlap.
        return true;
    }

    private static void Reconcile(ViewDrawing output, IReadOnlyList<VisibilityInput> inputs, CancellationToken token)
    {
        var lines = new List<(DrawingPiece Piece, Curve Curve)>(); var loci = new List<Locus>();
        var plane = output.Frame.Plane;
        try
        {
            foreach (var piece in output.Items)
            {
                token.ThrowIfCancellationRequested(); var g = piece.CopyGeometry();
                if (g is not Curve c || !c.IsLinear(output.Tolerance) || piece.DepthGeometry is not Curve wc || !wc.IsLinear(output.Tolerance)) { g.Dispose(); continue; }
                plane.ClosestParameter(c.PointAtStart, out var ax, out var ay); plane.ClosestParameter(c.PointAtEnd, out var bx, out var by);
                int index = lines.Count; lines.Add((piece, c));
                loci.Add(new(ax, ay, bx, by, piece.Class == LineClass.Visible ? VisibilityClass.Visible : piece.Class == LineClass.Hidden ? VisibilityClass.Hidden : VisibilityClass.Unresolved,
                    index.ToString("D10", System.Globalization.CultureInfo.InvariantCulture)));
            }
            var union = VisibilityLoci.Union(loci, output.Tolerance, preserveOwnership: true, token);
            var byId = inputs.Select(i => i.Source).DistinctBy(s => s.Identity).ToDictionary(s => s.Identity, StringComparer.Ordinal);
            var replaced = lines.Select(l => l.Piece).ToHashSet();
            output.Pieces.RemoveAll(replaced.Contains);
            foreach (var l in union)
            {
                token.ThrowIfCancellationRequested(); var (piece, curve) = lines[int.Parse(l.Source, System.Globalization.CultureInfo.InvariantCulture)];
                var a = plane.PointAt(l.X0, l.Y0); var b = plane.PointAt(l.X1, l.Y1);
                double roundoff = Math.Min(output.Tolerance/64,128*2.220446049250313e-16*Math.Max(1,Math.Max(a.DistanceTo(Point3d.Origin),b.DistanceTo(Point3d.Origin))));
                if (a == b || a.DistanceTo(b) <= roundoff && curve.GetBoundingBox(true).Diagonal.Length > output.Tolerance)
                { output.ProjectingSegments++; continue; }
                curve.ClosestPoint(a, out var ta); curve.ClosestPoint(b, out var tb);
                var world = piece.DepthGeometry as Curve;
                if (world is null) throw new InvalidOperationException("Missing world support during locus reconciliation");
                var projected = new LineCurve(a, b);
                var depth = new LineCurve(world.PointAt(ta), world.PointAt(tb)) { Domain = projected.Domain };
                output.Pieces.Add(new(projected, byId[piece.SourceIdentity], PlanarExtraction.OutputClass(l.Class), depth)
                    { Profile = piece.Profile, FarCandidate = piece.FarCandidate, KernelOwned = piece.KernelOwned });
            }
        }
        finally { foreach (var line in lines) { line.Curve.Dispose(); line.Piece.Dispose(); } }
    }

    internal static bool ProjectionResidue(Curve world, Curve projected, double tolerance)
    {
        var extent = projected.GetBoundingBox(true).Diagonal.Length;
        if (extent == 0) return true;
        // Only discard an unresolvable projection of a real world edge. Isolated
        // short world features survive. The bound is roundoff, capped by model tolerance.
        double scale = Math.Max(1, Math.Max(world.PointAtStart.DistanceTo(Point3d.Origin), world.PointAtEnd.DistanceTo(Point3d.Origin)));
        double roundoff = Math.Min(tolerance / 64, 128 * 2.220446049250313e-16 * scale);
        return extent <= roundoff && world.GetBoundingBox(true).Diagonal.Length > tolerance;
    }

    private static void Add(Curve world, VisibilityInput input, LineClass kind, bool profile, ViewDrawing output, bool kernelOwned = false)
    {
        Curve? projected = null;
        try
        {
            projected = world.DuplicateCurve();
            if (!projected.Transform(Transform.PlanarProjection(output.Frame.Plane))) throw new InvalidOperationException("Composition projection failed");
            if (ProjectionResidue(world, projected, output.Tolerance))
            { output.ProjectingSegments++; projected.Dispose(); world.Dispose(); return; }
            output.Pieces.Add(new(projected, input.Source, kind, world) { Profile = profile, FarCandidate = input.Far, KernelOwned = kernelOwned });
        }
        catch { world.Dispose(); projected?.Dispose(); throw; }
    }

    private static IEnumerable<(Curve Curve, LineClass Class)> ClipCurve(Curve world, IReadOnlyList<PlanarFace> faces,
        EdgeFaceKernel.PointQuery? query, Plane plane, double tolerance, CancellationToken token, Action? budgetExceeded = null)
    {
        var ends = new SortedSet<double> { world.Domain.Min, world.Domain.Max };
        var transform = Transform.PlaneToPlane(plane, Plane.WorldXY);
        var bounds = world.GetBoundingBox(transform); bool failed = query is null;
        using var local = world.DuplicateCurve(); local.Transform(transform);
        var ids = query?.OverlappingFaces(bounds.Min.X, bounds.Min.Y, bounds.Max.X, bounds.Max.Y) ?? Enumerable.Range(0, faces.Count).ToArray();
        if (ids.Count > 4096) { budgetExceeded?.Invoke(); yield return (world.DuplicateCurve(),LineClass.Unresolved); yield break; }
        int work = 0; bool exhausted = false;
        foreach (int fi in ids)
        {
            var face = faces[fi];
            token.ThrowIfCancellationRequested(); var all = face.Loops.SelectMany(l => l).ToArray();
            if (bounds.Min.X > all.Max(p => p.X) + tolerance || bounds.Max.X < all.Min(p => p.X) - tolerance ||
                bounds.Min.Y > all.Max(p => p.Y) + tolerance || bounds.Max.Y < all.Min(p => p.Y) - tolerance || bounds.Min.Z > all.Max(p => p.Z)) continue;
            var a = all[0]; Vector3d normal = Vector3d.Zero;
            for (int i = 1; i + 1 < all.Length && normal.IsZero; i++)
                normal = Vector3d.CrossProduct(new Vector3d(all[i].X - a.X, all[i].Y - a.Y, all[i].Z - a.Z),
                    new Vector3d(all[i + 1].X - a.X, all[i + 1].Y - a.Y, all[i + 1].Z - a.Z));
            if (normal.Z == 0) continue;
            void Events(Plane boundary)
            {
                token.ThrowIfCancellationRequested();
                if (++work > 8192 || ends.Count > 16384) { exhausted = true; return; }
                if (!boundary.IsValid) { failed = true; return; }
                using var hits = Intersection.CurvePlane(local, boundary, tolerance);
                if (hits is null) return; // RhinoCommon contract: no intersections recorded.
                foreach (var hit in hits)
                    if (hit.IsOverlap) { ends.Add(hit.OverlapA.Min); ends.Add(hit.OverlapA.Max); }
                    else ends.Add(hit.ParameterA);
            }
            Events(new Plane(new Point3d(a.X, a.Y, a.Z - tolerance), normal));
            foreach (var loop in face.Loops)
                for (int i = 0; i < loop.Length; i++)
                {
                    if (exhausted) break;
                    var p = loop[i]; var q = loop[(i + 1) % loop.Length];
                    var n = new Vector3d(q.Y - p.Y, p.X - q.X, 0);
                    if (!n.IsZero) Events(new Plane(new Point3d(p.X, p.Y, 0), n));
                }
            if (exhausted) break;
        }
        if (exhausted) { budgetExceeded?.Invoke(); yield return (world.DuplicateCurve(),LineClass.Unresolved); yield break; }
        var cuts = ends.Where(t => t >= world.Domain.Min && t <= world.Domain.Max).ToArray();
        double start = cuts[0]; LineClass? previous = null;
        for (int i = 1; i < cuts.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            var mid = local.PointAt((cuts[i - 1] + cuts[i]) / 2);
            var kind = PlanarExtraction.OutputClass(failed ? VisibilityClass.Unresolved : query!.Classify(new(mid.X, mid.Y, mid.Z), token));
            if (previous is { } old && old != kind)
            {
                var part = CurveIntervals.Trim(world, new(start, cuts[i - 1])) ?? throw new InvalidOperationException("Composition curve trim failed");
                yield return (part, old); start = cuts[i - 1];
            }
            previous = kind;
        }
        if (previous is { } last)
            yield return (CurveIntervals.Trim(world, new(start, cuts[^1])) ?? throw new InvalidOperationException("Composition curve trim failed"), last);
    }
}
