using System.Diagnostics;
using System.Globalization;
using M2DPlus.Core;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using Environment = System.Environment;

namespace M2DPlus.Rhino.Services;

public sealed record CaptureInput(object Value, int[] Path, string Identity)
{ public int SourceItem { get; init; } }

/// <summary>Host-only incremental capture. No document/table/reference survives into PreparedScene.</summary>
public sealed class SceneCapture : IDisposable
{
    private sealed record Pending(object Value, int[] Path, string Identity, Transform[] Chain, Guid[] Definitions, SourceSection? Section = null)
    {
        public int SourceItem { get; init; }
        public bool ScopeInside { get; set; }
        /// <summary>The top-level Rhino object (a block instance counts as one); null for Grasshopper geometry.</summary>
        public TopObject? Top { get; init; }
        /// <summary>The input text when the top-level item was a layer name.</summary>
        public string? LayerPattern { get; init; }
        /// <summary>The layer of the nearest Rhino object on the instance path: a leaf's own effective layer.</summary>
        public int LayerIndex { get; init; } = -1;
        public string LayerPath { get; init; } = "";
    }
    private sealed record TopObject(Guid Id, string? Name);
    private readonly Dictionary<string, M2DPlus.Core.Output.SourceGroupInfo> _groups = new(StringComparer.Ordinal);
    /// <summary>Output-tree values per captured leaf identity (UX §1.3): plain values read on this host thread, so
    /// presentation can group by layer or object without the document. Rebuilt by every capture, never cached.</summary>
    public IReadOnlyDictionary<string, M2DPlus.Core.Output.SourceGroupInfo> Groups => _groups;
    private readonly Dictionary<string, uint> _colours = new(StringComparer.Ordinal);
    /// <summary>Display colour (ARGB) per captured leaf identity of a Rhino object: <c>DrawColor(doc)</c>, with By Parent
    /// resolved through the block instance path (UX §3.7). Grasshopper geometry has none (preview ink). Plain values read
    /// on this host thread, like <see cref="Groups"/>; never part of the solve.</summary>
    public IReadOnlyDictionary<string, uint> Colours => _colours;
    private readonly WatchScope _scope;
    /// <summary>What this capture read, for <see cref="DocumentWatch"/> (set as the watch's scope on construction).</summary>
    public WatchScope Scope => _scope;
    private readonly int _thread = Environment.CurrentManagedThreadId;
    private readonly RhinoDoc _document;
    private readonly DocumentWatch _watch;
    private readonly long _generation;
    private readonly SceneCache _cache;
    private readonly Queue<Pending> _pending = new();
    private readonly List<SceneLeaf> _leaves = new();
    private readonly Dictionary<string, int> _unsupported = new(StringComparer.Ordinal);
    private readonly SectionMemo _sections;
    /// <summary>Objects the View input references (view boxes and not-yet-converted frame surfaces): never scene geometry.</summary>
    private readonly IReadOnlySet<Guid>? _viewObjects;
    /// <summary>Block definitions expanded once per capture: their sorted members and which are drawable.</summary>
    private readonly Dictionary<Guid, (RhinoObject Leaf, bool Drawable)[]> _definitions = new();
    private readonly double _tolerance;
    private readonly bool _millimetres;
    private readonly double _angleTolerance;
    private bool _accepted;
    private readonly CaptureVolumes? _volumes;
    public int ScopedOutCount { get; private set; }
    /// <summary>True when every requested view has a fixed volume, so capture skips objects wholly outside them.</summary>
    public bool Scoped => _volumes is { Enabled: true };
    public string CaptureStep => Scoped ? "scope and capture" : "capture";
    public static string ScopeKey(IReadOnlyList<ViewRequest> views, double tolerance) =>
        new CaptureVolumes(views, BoundingBox.Empty, tolerance).Revision;
    /// <summary>Leaves whose owned copy is fingerprinted and normalised in the background; joined by <see cref="AcceptAsync"/>.</summary>
    private readonly List<Task<SceneLeaf>> _inflight = new();
    public int CapturedCount => _leaves.Count + _inflight.Count;
    /// <summary>Objects still to read (an estimate: block instances expand as they are read).</summary>
    public int PendingCount => _pending.Count;
    /// <summary>Where capture time goes, on the host thread (Info's timing section and the profile tests).</summary>
    /// <remarks>Enumerate…Record run on the host (UI) thread; Fingerprint, Normalise, Sort, Signature and Scene run on
    /// background threads on owned copies (summed over threads).</remarks>
    public enum CapturePhase { Scoping, Enumerate, Verify, Objects, Sections, Blocks, Copy, Record, Fingerprint, Normalise, Sort, Signature, Scene }
    public static bool OnHostThread(CapturePhase phase) => phase <= CapturePhase.Record;
    private readonly long[] _ticks = new long[Enum.GetValues<CapturePhase>().Length];
    private long Mark() => Stopwatch.GetTimestamp();
    private void Add(CapturePhase phase, long since) => Interlocked.Add(ref _ticks[(int)phase], Stopwatch.GetTimestamp() - since);
    /// <summary>Milliseconds spent per phase so far.</summary>
    public IReadOnlyList<(CapturePhase Phase, double Milliseconds)> Profile =>
        Enum.GetValues<CapturePhase>().Select(p => (p, _ticks[(int)p] * 1000d / Stopwatch.Frequency)).ToArray();
    /// <summary>Capture slices run so far (each one blocks the UI thread for about the slice budget).</summary>
    public int Slices { get; private set; }
    public double MaximumSliceMilliseconds { get; private set; }
    public double InitialEnumerationMilliseconds { get; }
    public double ActiveSeconds { get; private set; }
    /// <param name="excluded">Objects never drawn in any mode: the view objects (view boxes and not-yet-converted frame
    /// surfaces) that the View input references.</param>
    /// <param name="scope">What the drawing will read; the capture fills it and sets it on the watch. Default: document
    /// mode without input, else referenced mode with the input's layer names.</param>
    public SceneCapture(RhinoDoc document, DocumentWatch watch, IEnumerable<CaptureInput>? input = null, SceneCache? cache = null,
        IReadOnlySet<Guid>? excluded = null, WatchScope? scope = null,
        Func<BoundingBox, IReadOnlyList<ViewRequest>>? scopeViews = null)
    {
        var timer = Stopwatch.StartNew();
        Interlocked.Increment(ref _captures);
        var items = input?.ToArray();
        _scope = scope ?? new WatchScope(items is null ? M2DPlus.Core.Watch.WatchMode.Document : M2DPlus.Core.Watch.WatchMode.Referenced,
            items?.Select(i => i.Value).OfType<string>(), excluded);
        watch.Scope = _scope;
        _document = document; _watch = watch; _generation = watch.Generation; _cache = cache ?? SceneCache.Shared;
        _sections = new SectionMemo(document);
        _viewObjects = excluded;
        _tolerance = document.ModelAbsoluteTolerance;
        _millimetres = document.ModelUnitSystem == UnitSystem.Millimeters;
        _angleTolerance = document.ModelAngleToleranceRadians;
        if (items is null)
        {
            var settings = new ObjectEnumeratorSettings { NormalObjects = true, LockedObjects = true, HiddenObjects = false,
                ReferenceObjects = true, IncludeLights = false, IncludeGrips = false };
            var ordinal = 0;
            foreach (var obj in document.Objects.GetObjectList(settings).OrderBy(o => o.Id))
                if (IsDrawable(document, obj) && excluded?.Contains(obj.Id) != true) _pending.Enqueue(new(obj.Id, new[] { 0 }, obj.Id.ToString(), Array.Empty<Transform>(), Array.Empty<Guid>()) { SourceItem = ordinal++ });
        }
        else foreach (var item in items) _pending.Enqueue(new(item.Value, (int[])item.Path.Clone(), item.Identity, Array.Empty<Transform>(), Array.Empty<Guid>()) { SourceItem = item.SourceItem });
        // Unbounded requests keep the original single-pass capture, including its full-scene bounds.
        if (scopeViews is not null) _volumes = new(scopeViews(BoundingBox.Empty), BoundingBox.Empty, _tolerance);
        InitialEnumerationMilliseconds = timer.Elapsed.TotalMilliseconds;
        _ticks[(int)CapturePhase.Enumerate] += (long)(timer.Elapsed.TotalSeconds * Stopwatch.Frequency);
        ActiveSeconds = timer.Elapsed.TotalSeconds;
    }
    /// <summary>
    /// What a capture would read, as one key (UX §5.1a): the document, its tolerance, the mode and every Geometry item
    /// (path, item, identity, and its value: an object id, a layer name, or the content fingerprint of Grasshopper
    /// geometry), plus the view objects that are never scene geometry. Two equal keys with no scene change in the
    /// document between them read the same scene, so a Settings, Depth, Target, menu or view-object change reuses the
    /// captured scene instead of reading the objects again.
    /// </summary>
    public static string InputKey(RhinoDoc document, IReadOnlyList<CaptureInput>? inputs, IEnumerable<Guid> viewObjects)
    {
        using var key = new SceneHash();
        key.Add("capture-v1"); key.Add((int)document.RuntimeSerialNumber); key.Add(document.ModelAbsoluteTolerance);
        key.Add((int)document.ModelUnitSystem); key.Add(document.ModelAngleToleranceRadians);
        key.Add(inputs is null);
        foreach (var input in inputs ?? [])
        {
            key.Add(input.Path); key.Add(input.SourceItem); key.Add(input.Identity);
            key.Add(input.Value switch
            {
                Guid id => "id:" + id.ToString("D"),
                string text => "layer:" + text,
                GeometryBase geometry => "geometry:" + GeometryFingerprint.Compute(geometry),
                null => "null",
                var other => other.GetType().FullName + ":" + Json(other),
            });
        }
        key.Add(viewObjects.Order().ToArray());
        return key.Finish();
        // Primitive values (Point3d, Line, Box…): their public state, else no reuse.
        static string Json(object value)
        {
            try { return System.Text.Json.JsonSerializer.Serialize(value, value.GetType()); }
            catch (Exception e) when (e is NotSupportedException or InvalidOperationException or ArgumentException) { return Guid.NewGuid().ToString(); }
        }
    }

    /// <summary>Document mode's scene rule (UX §3.1): visible (not hidden, layer and parents on) and selectable.</summary>
    internal static bool IsDrawable(RhinoDoc document, RhinoObject obj) => IsDrawable(document, obj, obj.Attributes);

    /// <inheritdoc cref="IsDrawable(RhinoDoc, RhinoObject)"/>
    /// <param name="attributes">The attributes to judge (a ModifyObjectAttributes event's new attributes).</param>
    internal static bool IsDrawable(RhinoDoc document, RhinoObject obj, ObjectAttributes attributes)
    {
        if (!attributes.Visible || attributes.Mode == ObjectMode.Hidden || !IsSelectable(document, obj, attributes)) return false;
        for (var layer = document.Layers[attributes.LayerIndex]; layer is not null;)
        {
            if (!layer.IsVisible) return false;
            if (layer.ParentLayerId == Guid.Empty) break;
            layer = document.Layers.FindId(layer.ParentLayerId);
            if (layer is null) return false;
        }
        return true;
    }

    /// <summary>Why an object is never scene geometry, whatever the mode or how it reached the input.</summary>
    public enum Exclusion { None, ViewBox, Baked }

    /// <summary>
    /// The hard rule (owner decision 2026-11-16, UX §3.1): view boxes and M2D+ bakes are never scene geometry, in document
    /// mode, referenced Geometry (select-all or a pick by mistake), layer-name items (<c>*</c>, <c>M2D+*</c>, parents) and
    /// block definitions. A view box is an object with <c>m2d.view</c> user text, or anything on the <c>$M2D Views</c>
    /// layer or a sublayer. A bake is an object with the bake markers every baked line, cut and hatch carries
    /// (<c>m2d.bake</c>, <c>m2d.class</c>; they stay when the user moves it to another layer), or anything on a layer
    /// under an <c>M2D+</c> bake layer. Markers are read on the object's attributes and its geometry.
    /// </summary>
    internal static Exclusion Excluded(RhinoDoc document, RhinoObject obj, ObjectAttributes attributes)
    {
        bool Has(string key) => attributes.GetUserString(key) is not null || obj.Geometry?.GetUserString(key) is not null;
        if (Has(M2DPlus.Rhino.Views.ViewBoxes.Marker)) return Exclusion.ViewBox;
        if (Has(M2DPlus.Core.Bake.BakeTags.Bake) || Has(M2DPlus.Core.Bake.BakeTags.Class)) return Exclusion.Baked;
        if (attributes.LayerIndex >= 0 && attributes.LayerIndex < document.Layers.Count)
        {
            var path = document.Layers[attributes.LayerIndex].FullPath.Split(new[] { "::" }, StringSplitOptions.None);
            if (path[0].Equals(M2DPlus.Rhino.Views.ViewBoxes.LayerName, StringComparison.OrdinalIgnoreCase)) return Exclusion.ViewBox;
            if (path.Contains("M2D+")) return Exclusion.Baked;
        }
        return Exclusion.None;
    }

    /// <summary>Whatever its visibility, an object M2D+ may draw: not deleted, not a clipping plane or light, not
    /// excluded by the hard rule (<see cref="Excluded"/>) and not tagged with any other m2d.* user text. Layer-name
    /// Geometry items select these (owner decision 2026-11-15): hidden objects and objects on layers that are off, or
    /// under a parent that is off, are drawn too, exactly like referenced objects.</summary>
    internal static bool IsSelectable(RhinoDoc document, RhinoObject obj, ObjectAttributes attributes)
    {
        if (obj.IsDeleted || obj.Geometry is ClippingPlaneSurface or Light) return false;
        if (attributes.LayerIndex < 0 || attributes.LayerIndex >= document.Layers.Count) return false;
        if (Excluded(document, obj, attributes) != Exclusion.None) return false;
        var strings = attributes.GetUserStrings();
        return strings.AllKeys.Concat(obj.Geometry.GetUserStrings().AllKeys)
            .All(key => key is null || !key.StartsWith("m2d.", StringComparison.OrdinalIgnoreCase));
    }

    private readonly List<(Exclusion Why, string Label)> _dropped = new();
    /// <summary>Objects the Geometry input referenced explicitly that the hard rule left out (Info reports them).</summary>
    public IReadOnlyList<(Exclusion Why, string Label)> Dropped => _dropped;
    /// <summary>One Info line for <see cref="Dropped"/>, or null.</summary>
    public string? DroppedSummary
    {
        get
        {
            if (_dropped.Count == 0) return null;
            string Part(Exclusion why, string one, string many)
            {
                var labels = _dropped.Where(d => d.Why == why).Select(d => d.Label).ToArray();
                if (labels.Length == 0) return "";
                var list = string.Join(", ", labels.Take(5)) + (labels.Length > 5 ? $", and {labels.Length - 5} more" : "");
                return $"{labels.Length} {(labels.Length == 1 ? one : many)} ({list})";
            }
            var parts = new[] { Part(Exclusion.ViewBox, "view box", "view boxes"), Part(Exclusion.Baked, "baked M2D+ object", "baked M2D+ objects") }
                .Where(p => p.Length > 0);
            return $"Left out of Geometry: {string.Join(" and ", parts)}. View boxes and M2D+ bakes are never drawn or used to hide anything.";
        }
    }

    /// <summary>A bare leaf name matches that layer's name; a full path or wildcard matches FullPath.</summary>
    internal static bool LayerMatches(Layer layer, string pattern)
    {
        if (layer.IsDeleted) return false;
        var expression = "^" + System.Text.RegularExpressions.Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
        const System.Text.RegularExpressions.RegexOptions ignore = System.Text.RegularExpressions.RegexOptions.IgnoreCase;
        return System.Text.RegularExpressions.Regex.IsMatch(layer.FullPath, expression, ignore) ||
            !pattern.Contains("::") && System.Text.RegularExpressions.Regex.IsMatch(layer.Name, expression, ignore);
    }
    private void Verify(bool final = false)
    {
        if (_thread != Environment.CurrentManagedThreadId) throw new InvalidOperationException("Capture must run on its host thread.");
        var since = Mark();
        try { VerifyCore(final); } finally { Add(CapturePhase.Verify, since); }
    }
    private void VerifyCore(bool final)
    {
        // The document can't change inside a slice (this is the UI thread): interactive Rhino reports changes between
        // slices as events, and the snapshot comparison (for headless documents) runs at most every 200 ms, plus at the end.
        _watch.VerifyHostState(throttle: !final);
        if (_watch.IsClosed || _watch.Generation != _generation) throw new OperationCanceledException("Document/definition changed during capture; retry.");
    }
    public bool Step(TimeSpan budget, CancellationToken token = default)
    {
        var timer = Stopwatch.StartNew(); Slices++;
        Verify(); token.ThrowIfCancellationRequested();
        if (budget <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(budget));
        do
        {
            token.ThrowIfCancellationRequested();
            if (_pending.TryDequeue(out var item)) Capture(item);
            else break;
        } while (timer.Elapsed < budget);
        Verify(); MaximumSliceMilliseconds = Math.Max(MaximumSliceMilliseconds, timer.Elapsed.TotalMilliseconds);
        ActiveSeconds += timer.Elapsed.TotalSeconds;
        return _pending.Count == 0;
    }
    private void Unsupported(string name)
    {
        _unsupported[name] = _unsupported.GetValueOrDefault(name) + 1;
    }
    private bool ScopedOut(Pending item, GeometryBase geometry, Guid? id = null, Guid? layer = null)
    {
        if (item.ScopeInside || _volumes is not { Enabled: true }) return false;
        var since = Mark();
        try
        {
            var instance = geometry is InstanceReferenceGeometry;
            var transform = PreparedScene.Combined(item.Chain);
            var box = instance ? InstanceBounds((InstanceReferenceGeometry)geometry, new()) : geometry.GetBoundingBox(false);
            box.Transform(transform);
            if (!box.IsValid) return false; // Invalid cheap bounds never exclude a source.
            if (!_volumes.Outside(box, item.Section, id, layer, instance))
            {
                if (instance && _volumes.Contains(box)) item.ScopeInside = true;
                return false;
            }
            ScopedOutCount++; return true;
        }
        finally { Add(CapturePhase.Scoping, since); }
    }

    // Definition-local conservative boxes are rebuilt per capture. Rhino's stored nested
    // instance boxes can be stale after editing an inner definition. No geometry is copied
    // or normalised here, and repeated instances share this cheap definition lookup.
    private readonly Dictionary<Guid, BoundingBox> _definitionBounds = new();
    private BoundingBox InstanceBounds(InstanceReferenceGeometry instance, HashSet<Guid> path)
    {
        var id = instance.ParentIdefId;
        if (!path.Add(id) || path.Count > 256) return BoundingBox.Unset;
        try
        {
            if (!_definitionBounds.TryGetValue(id, out var box))
            {
                var definition = _document.InstanceDefinitions.FindId(id);
                if (definition is null) return BoundingBox.Unset;
                _scope.Definitions.Add(id);
                box = BoundingBox.Empty;
                foreach (var (part, drawable) in Members(definition))
                {
                    _scope.Read(_document, part, gated: true);
                    if (!drawable) continue;
                    var child = part.Geometry is InstanceReferenceGeometry nested
                        ? InstanceBounds(nested, path) : part.Geometry.GetBoundingBox(false);
                    if (!child.IsValid) return BoundingBox.Unset;
                    box.Union(child);
                }
                _definitionBounds[id] = box;
            }
            box.Transform(instance.Xform);
            return box;
        }
        finally { path.Remove(id); }
    }

    private static string LocalIdentity(Pending item) => item.Definitions.Length == 0 ? item.Identity : item.Definitions[^1] + "/" + item.Identity.Split('/')[^1];
    private static string GeometryKey(uint document, string localId, string fingerprint, string type) =>
        $"{document}/{localId}/{fingerprint}/normalise-v2/{type}";

    private (RhinoObject Leaf, bool Drawable)[] Members(InstanceDefinition definition)
    {
        if (_definitions.TryGetValue(definition.Id, out var members)) return members;
        members = definition.GetObjects().OrderBy(o => o.Id).Select(o => (o, IsDrawable(_document, o))).ToArray();
        _definitions.Add(definition.Id, members);
        foreach (var (part, _) in members) { _scope.Gated.Add(part.Id); _scope.GateLayers.Add(part.Attributes.LayerIndex); }
        return members;
    }

    private static bool Supported(GeometryBase? geometry) =>
        geometry is Brep or Mesh or Curve or Surface or Extrusion or SubD or Point && geometry is not ClippingPlaneSurface;
    private void Capture(Pending item)
    {
        var value = item.Value;
        if (value is string layerPattern)
        {
            // A layer name selects every object on matching layers whatever its visibility, like referencing them
            // (owner decision 2026-11-15, UX §1.1): hiding objects or switching those layers never changes the input.
            var layers = _document.Layers.Where(l => LayerMatches(l, layerPattern)).Select(l => l.Index).ToHashSet();
            var matches = _document.Objects.GetObjectList(new ObjectEnumeratorSettings { NormalObjects = true, LockedObjects = true, HiddenObjects = true, ReferenceObjects = true })
                .Where(o => layers.Contains(o.Attributes.LayerIndex) && IsSelectable(_document, o, o.Attributes)).OrderBy(o => o.Id).ToArray();
            if (layers.Count == 0) Unsupported(PreparedScene.NonOccludingPrefix + $"Layer not found '{layerPattern}'");
            foreach (var match in matches) _pending.Enqueue(item with { Value = match.Id, Identity = item.Identity + "/" + match.Id, LayerPattern = item.LayerPattern ?? layerPattern });
            return;
        }
        var objectsSince = Mark();
        if (value is Guid id)
        {
            var obj = _document.Objects.FindId(id);
            if (obj is null) throw new OperationCanceledException("Source deleted during capture.");
            value = obj;
        }
        uint? sourceSerial = null;
        if (value is RhinoObject source)
        {
            // The hard rule also covers explicit references: report them, since the user picked them.
            var exclusion = _viewObjects?.Contains(source.Id) == true ? Exclusion.ViewBox : Excluded(_document, source, source.Attributes);
            if (exclusion != Exclusion.None)
            {
                if (item.Definitions.Length == 0 && item.LayerPattern is null)
                    _dropped.Add((exclusion, string.IsNullOrWhiteSpace(source.Attributes.Name) ? source.Id.ToString("D")[..8] : source.Attributes.Name));
                Add(CapturePhase.Objects, objectsSince);
                return;
            }
            Add(CapturePhase.Objects, objectsSince);
            // Even excluded references and definition members remain watch dependencies.
            _scope.Read(_document, source, gated: item.Definitions.Length > 0);
            if (ScopedOut(item, source.Geometry, source.Id, _document.Layers[source.Attributes.LayerIndex].Id)) return;
            objectsSince = Mark();
            sourceSerial = source.RuntimeSerialNumber;
            // A nested object keeps its own layer (Rhino has no layer By Parent).
            var layer = _document.Layers[source.Attributes.LayerIndex];
            Add(CapturePhase.Objects, objectsSince);
            var sectionsSince = Mark();
            var section = _sections.Capture(source, item.Section);
            Add(CapturePhase.Sections, sectionsSince);
            objectsSince = Mark();
            item = item with { Section = section, LayerIndex = layer.Index, LayerPath = layer.FullPath,
                // Only a top-level item is "the object"; parts of a Grasshopper-made instance stay Grasshopper sources.
                Top = item.Top ?? (item.Definitions.Length == 0 ? new(source.Id, string.IsNullOrWhiteSpace(source.Attributes.Name) ? null : source.Attributes.Name) : null) };
            value = source.Geometry;
        }
        Add(CapturePhase.Objects, objectsSince);
        if (sourceSerial is null && value is GeometryBase candidate && ScopedOut(item, candidate)) return;
        if (value is InstanceReferenceGeometry instance)
        {
            var blocksSince = Mark();
            try { Expand(item, instance); } finally { Add(CapturePhase.Blocks, blocksSince); }
            return;
        }
        Leaf(item, value, sourceSerial);
    }
    private void Expand(Pending item, InstanceReferenceGeometry instance)
    {
            var definition = _document.InstanceDefinitions.FindId(instance.ParentIdefId);
            if (definition is null || item.Definitions.Contains(instance.ParentIdefId) || item.Definitions.Length >= 256)
            { Unsupported("Invalid/cyclic block"); return; }
            _scope.Definitions.Add(definition.Id);
            var members = Members(definition);
            if (members.Length == 0) { Unsupported("Empty/unavailable block definition " + definition.Name); return; }
            foreach (var (leaf, drawable) in members)
            {
                if (!drawable) continue;
                _pending.Enqueue(new(leaf.Id, item.Path, item.Identity + "/" + definition.Id + "/" + leaf.Id,
                    item.Chain.Append(instance.Xform).ToArray(), item.Definitions.Append(definition.Id).ToArray(), item.Section)
                    { SourceItem = item.SourceItem, Top = item.Top, LayerPattern = item.LayerPattern, LayerIndex = item.LayerIndex, LayerPath = item.LayerPath, ScopeInside = item.ScopeInside });
            }
    }
    internal static BoundingBox PrimitiveBounds(object value)
    {
        static BoundingBox Union(BoundingBox a, BoundingBox b) { a.Union(b); return a; }
        return value switch
        {
            Point3d p => new(p, p), Line l => l.BoundingBox, Polyline p => p.BoundingBox,
            Arc a => a.BoundingBox(), Circle c => c.BoundingBox, Rectangle3d r => r.BoundingBox,
            Box b => b.BoundingBox, BoundingBox b => b, Sphere s => s.BoundingBox,
            Cylinder c => Union(c.CircleAt(c.Height1).BoundingBox, c.CircleAt(c.Height2).BoundingBox),
            Cone c => Union(new Circle(new Plane(c.BasePoint, c.Plane.XAxis, c.Plane.YAxis), Math.Abs(c.Radius)).BoundingBox,
                new BoundingBox(c.ApexPoint, c.ApexPoint)),
            Torus t => new Sphere(t.Plane.Origin, Math.Abs(t.MajorRadius) + Math.Abs(t.MinorRadius)).BoundingBox,
            _ => BoundingBox.Unset
        };
    }

    private static GeometryBase? AsGeometry(object value) => value switch
        {
            GeometryBase g => g,
            Point3d p => new Point(p), Line l => new LineCurve(l), Polyline p => new PolylineCurve(p),
            Arc a => new ArcCurve(a), Circle c => new ArcCurve(c), Rectangle3d r => r.ToNurbsCurve(),
            Box b => b.ToBrep(), BoundingBox b => b.ToBrep(), Sphere s => s.ToBrep(),
            Cylinder c => c.ToBrep(true, true), Cone c => c.ToBrep(true), Torus t => t.ToBrep(), _ => null
        };

    private void Leaf(Pending item, object value, uint? sourceSerial)
    {
        if (value is not GeometryBase && _volumes is { Enabled: true } && !item.ScopeInside)
        {
            var box = PrimitiveBounds(value); box.Transform(PreparedScene.Combined(item.Chain));
            if (_volumes.Outside(box, item.Section, null, null, false)) { ScopedOutCount++; return; }
        }
        GeometryBase? geometry = AsGeometry(value);
        var temporary = value is not GeometryBase && geometry is not null;
        try
        {
            // Flat annotation-like objects and point clouds can't hide anything: report them, don't quarantine the view.
            if (geometry is AnnotationBase or TextDot or Hatch or PointCloud)
            { Unsupported(PreparedScene.NonOccludingPrefix + geometry.GetType().Name); return; }
            if (!Supported(geometry))
            { Unsupported(geometry?.GetType().Name ?? value.GetType().Name); return; }
            var primitive = temporary ? value.GetType().Name : null;
            var normalisationType = primitive ?? geometry!.GetType().Name;
            var documentSerial = _watch.DocumentSerial;
            var localId = LocalIdentity(item);
            string Key(string fingerprint) => GeometryKey(documentSerial, localId, fingerprint, normalisationType);
            var section = item.Section ?? SourceSection.Plain;
            // Document objects are fingerprinted once per object instance (serial), across captures; a known object with
            // cached normalised geometry costs nothing more here.
            string? known = sourceSerial is { } serial && _cache.TryFingerprint(documentSerial, serial, out var remembered) ? remembered : null;
            if (known is not null && _cache.TryGeometry(Key(known)) is { } hit)
                _leaves.Add(new(item.Identity, Key(known), item.Path, item.Chain, hit, section) { SourceItem = item.SourceItem });
            else
            {
                // An owned copy now (the live object never leaves this thread); content fingerprint and normalisation on
                // a background thread, joined when the scene is accepted.
                var copySince = Mark();
                var copy = temporary ? geometry! : geometry!.Duplicate();
                if (temporary) temporary = false; // ownership moves to the background work
                copy.EnsurePrivateCopy();
                Add(CapturePhase.Copy, copySince);
                var cache = _cache; var identity = item.Identity; var path = item.Path; var chain = item.Chain; var sourceItem = item.SourceItem;
                _inflight.Add(Task.Run(() =>
                {
                    var owned = false;
                    try
                    {
                        var since = Mark();
                        var fingerprint = known ?? GeometryFingerprint.Compute(copy);
                        if (sourceSerial is { } s) cache.RememberFingerprint(documentSerial, s, fingerprint);
                        Add(CapturePhase.Fingerprint, since);
                        since = Mark();
                        var lease = cache.Geometry(Key(fingerprint), () => { owned = true; return NormaliseOwned(copy, primitive); });
                        Add(CapturePhase.Normalise, since);
                        return new SceneLeaf(identity, Key(fingerprint), path, chain, lease, section) { SourceItem = sourceItem };
                    }
                    finally { if (!owned) copy.Dispose(); }
                }));
            }
            var recordSince = Mark();
            if (item.Section is { } captured && sourceSerial is not null)
            {
                _colours[item.Identity] = captured.Style.ObjectColour;
                if (captured.Style.Pattern is not null) _scope.UsesHatchPatterns = true;
            }
            _groups[item.Identity] = item.Top is { } top
                ? new((int[])item.Path.Clone(), item.SourceItem, true, item.LayerIndex, item.LayerPath, top.Id, top.Name, item.LayerPattern)
                : new((int[])item.Path.Clone(), item.SourceItem, false, item.LayerIndex, item.LayerPath, Guid.Empty, null);
            Add(CapturePhase.Record, recordSince);
        }
        finally { if (temporary) geometry?.Dispose(); }
    }
    private static long _normalisations;
    /// <summary>Geometry normalised (not found in the cache) since the process started: the capture-count tests.</summary>
    public static long Normalisations => Interlocked.Read(ref _normalisations);
    /// <summary>Scene captures started since the process started.</summary>
    public static long Captures => Interlocked.Read(ref _captures);
    private static long _captures;

    /// <summary>Normalises an owned copy, taking ownership of it.</summary>
    private static OwnedGeometry NormaliseOwned(GeometryBase owned, string? primitive = null)
    {
        Interlocked.Increment(ref _normalisations);
        GeometryBase? converted = null;
        try
        {
            owned.EnsurePrivateCopy();
            converted = owned switch
            {
                SubD subd => subd.ToBrep() ?? throw new InvalidOperationException("SubD to Brep failed."),
                Extrusion extrusion => extrusion.ToBrep() ?? throw new InvalidOperationException("Extrusion to Brep failed."),
                Surface surface => surface.ToBrep() ?? throw new InvalidOperationException("Surface to Brep failed."),
                _ => owned
            };
            var conversion = primitive is not null ? $"{primitive} to {converted.GetType().Name}" :
                ReferenceEquals(owned, converted) ? null : $"{owned.GetType().Name} to Brep";
            converted.EnsurePrivateCopy();
            var result = new OwnedGeometry(converted, conversion);
            if (!ReferenceEquals(owned, converted)) owned.Dispose();
            return result;
        }
        catch { if (!ReferenceEquals(owned, converted)) converted?.Dispose(); owned.Dispose(); throw; }
    }
    /// <summary>Synchronous <see cref="AcceptAsync"/>, for tools and tests.</summary>
    public LeaseEntry<PreparedScene>.Lease Accept() => AcceptAsync().GetAwaiter().GetResult();

    /// <summary>
    /// Checks coherence on the host thread, then joins the background fingerprints and normalisations and builds the
    /// scene (sort, identity hash, bounds) on a background thread, so the UI thread is not blocked for it. The result
    /// was coherent with the document at the final check; the caller still rejects it if the watch generation moved.
    /// </summary>
    public Task<LeaseEntry<PreparedScene>.Lease> AcceptAsync()
    {
        Verify(final: true);
        if (_accepted || _pending.Count != 0) throw new InvalidOperationException("Capture is not ready for acceptance.");
        _accepted = true;
        var leaves = _leaves.ToList(); var inflight = _inflight.ToArray();
        var unsupported = new Dictionary<string, int>(_unsupported, StringComparer.Ordinal);
        var tolerance = _tolerance; var cache = _cache;
        return Task.Run(async () =>
        {
            try { await Task.WhenAll(inflight).ConfigureAwait(false); }
            catch
            {
                foreach (var leaf in leaves) leaf.Geometry.Dispose();
                foreach (var task in inflight) if (task.IsCompletedSuccessfully) task.Result.Geometry.Dispose();
                throw;
            }
            leaves.AddRange(inflight.Select(t => t.Result));
            try { return Build(leaves, unsupported, tolerance, cache); }
            catch { foreach (var leaf in leaves) leaf.Geometry.Dispose(); throw; }
        });
    }

    private LeaseEntry<PreparedScene>.Lease Build(List<SceneLeaf> leaves, Dictionary<string, int> unsupported, double tolerance, SceneCache cache)
    {
        var sortSince = Mark();
        // Native coincident-edge attribution depends on insertion order. Stabilise sources as
        // well as extracted pieces, and give a permuted but identical scene the same revision.
        leaves.Sort((a, b) =>
        {
            for (var i = 0; i < Math.Min(a.Path.Length, b.Path.Length); i++)
            { var order = a.Path[i].CompareTo(b.Path[i]); if (order != 0) return order; }
            var result = a.Path.Length.CompareTo(b.Path.Length);
            if (result == 0) result = StringComparer.Ordinal.Compare(a.Identity, b.Identity);
            if (result == 0) result = StringComparer.Ordinal.Compare(a.Key, b.Key);
            return result;
        });
        Add(CapturePhase.Sort, sortSince);
        var signatureSince = Mark();
        // The scene identity: every leaf's item, identity, geometry key, section revision, path and instance chain,
        // streamed into one hash (section revisions are shared between the instances of a block part).
        string hash;
        using (var signature = new SceneHash())
        {
            signature.Add("scene-v4");
            signature.Add(_volumes?.Revision ?? "unbounded");
            var revisions = new Dictionary<SourceSection, string>(ReferenceEqualityComparer.Instance);
            foreach (var l in leaves)
            {
                if (!revisions.TryGetValue(l.Section, out var revision)) revisions[l.Section] = revision = l.Section.Revision;
                signature.Add(l.SourceItem); signature.Add(l.Identity); signature.Add(l.Key); signature.Add(revision);
                signature.Add(l.Path); signature.Add(l.Chain);
            }
            foreach (var pair in unsupported.OrderBy(p => p.Key, StringComparer.Ordinal)) { signature.Add(pair.Key); signature.Add(pair.Value); }
            signature.Add(tolerance); signature.Add(_millimetres); signature.Add(_angleTolerance);
            hash = signature.Finish();
        }
        Add(CapturePhase.Signature, signatureSince);
        var sceneSince = Mark();
        var lease = cache.Scene(hash, leaves, unsupported, tolerance, _millimetres, _angleTolerance);
        Add(CapturePhase.Scene, sceneSince);
        return lease;
    }

    public void Dispose()
    {
        if (_accepted) return;
        _accepted = true;
        foreach (var leaf in _leaves) leaf.Geometry.Dispose();
        foreach (var task in _inflight)
            task.ContinueWith(t => { if (t.IsCompletedSuccessfully) t.Result.Geometry.Dispose(); }, TaskScheduler.Default);
    }
}
