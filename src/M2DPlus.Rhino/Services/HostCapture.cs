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
    private readonly Dictionary<uint, string> _fingerprints = new();
    private readonly double _tolerance;
    private bool _accepted;
    public int CapturedCount => _leaves.Count;
    public double MaximumSliceMilliseconds { get; private set; }
    public double InitialEnumerationMilliseconds { get; }
    public double ActiveSeconds { get; private set; }
    /// <param name="excluded">Document mode only: objects never drawn, such as the view objects (view boxes and
    /// not-yet-validated frame surfaces) that the View input references.</param>
    /// <param name="scope">What the drawing will read; the capture fills it and sets it on the watch. Default: document
    /// mode without input, else referenced mode with the input's layer names.</param>
    public SceneCapture(RhinoDoc document, DocumentWatch watch, IEnumerable<CaptureInput>? input = null, SceneCache? cache = null,
        IReadOnlySet<Guid>? excluded = null, WatchScope? scope = null)
    {
        var timer = Stopwatch.StartNew();
        var items = input?.ToArray();
        _scope = scope ?? new WatchScope(items is null ? M2DPlus.Core.Watch.WatchMode.Document : M2DPlus.Core.Watch.WatchMode.Referenced,
            items?.Select(i => i.Value).OfType<string>(), excluded);
        watch.Scope = _scope;
        _document = document; _watch = watch; _generation = watch.Generation; _cache = cache ?? SceneCache.Shared;
        _tolerance = document.ModelAbsoluteTolerance;
        if (items is null)
        {
            var settings = new ObjectEnumeratorSettings { NormalObjects = true, LockedObjects = true, HiddenObjects = false,
                ReferenceObjects = true, IncludeLights = false, IncludeGrips = false };
            var ordinal = 0;
            foreach (var obj in document.Objects.GetObjectList(settings).OrderBy(o => o.Id))
                if (IsDrawable(document, obj) && excluded?.Contains(obj.Id) != true) _pending.Enqueue(new(obj.Id, new[] { 0 }, obj.Id.ToString(), Array.Empty<Transform>(), Array.Empty<Guid>()) { SourceItem = ordinal++ });
        }
        else foreach (var item in items) _pending.Enqueue(new(item.Value, (int[])item.Path.Clone(), item.Identity, Array.Empty<Transform>(), Array.Empty<Guid>()) { SourceItem = item.SourceItem });
        InitialEnumerationMilliseconds = timer.Elapsed.TotalMilliseconds;
        ActiveSeconds = timer.Elapsed.TotalSeconds;
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

    /// <summary>Whatever its visibility, an object M2D+ may draw: not deleted, not a clipping plane or light, not on an
    /// M2D+ bake layer and not tagged with m2d.* user text (bakes and view boxes).</summary>
    internal static bool IsSelectable(RhinoDoc document, RhinoObject obj, ObjectAttributes attributes)
    {
        if (obj.IsDeleted || obj.Geometry is ClippingPlaneSurface or Light) return false;
        if (attributes.LayerIndex < 0 || attributes.LayerIndex >= document.Layers.Count) return false;
        var layer = document.Layers[attributes.LayerIndex];
        if (layer.FullPath.Split(new[] { "::" }, StringSplitOptions.None).Contains("M2D+")) return false;
        var strings = attributes.GetUserStrings();
        return strings.AllKeys.Concat(obj.Geometry.GetUserStrings().AllKeys)
            .All(key => key is null || !key.StartsWith("m2d.", StringComparison.OrdinalIgnoreCase));
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
        // The document can't change inside a slice (this is the UI thread): interactive Rhino reports changes between
        // slices as events, and the snapshot comparison (for headless documents) runs at most every 200 ms, plus at the end.
        _watch.VerifyHostState(throttle: !final);
        if (_watch.IsClosed || _watch.Generation != _generation) throw new OperationCanceledException("Document/definition changed during capture; retry.");
    }
    public bool Step(TimeSpan budget, CancellationToken token = default)
    {
        var timer = Stopwatch.StartNew();
        Verify(); token.ThrowIfCancellationRequested();
        if (budget <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(budget));
        do
        {
            if (!_pending.TryDequeue(out var item)) break;
            token.ThrowIfCancellationRequested(); Capture(item);
        } while (timer.Elapsed < budget);
        Verify(); MaximumSliceMilliseconds = Math.Max(MaximumSliceMilliseconds, timer.Elapsed.TotalMilliseconds);
        ActiveSeconds += timer.Elapsed.TotalSeconds;
        return _pending.Count == 0;
    }
    private void Unsupported(string name) => _unsupported[name] = _unsupported.GetValueOrDefault(name) + 1;
    private void Capture(Pending item)
    {
        var value = item.Value;
        if (value is string layerPattern)
        {
            // A layer name selects the visible objects on matching layers (UX §1.1), so their visibility is watched.
            var layers = _document.Layers.Where(l => LayerMatches(l, layerPattern)).Select(l => l.Index).ToHashSet();
            var matches = _document.Objects.GetObjectList(new ObjectEnumeratorSettings { NormalObjects = true, LockedObjects = true, ReferenceObjects = true })
                .Where(o => layers.Contains(o.Attributes.LayerIndex) && IsDrawable(_document, o)).OrderBy(o => o.Id).ToArray();
            _scope.GateLayers.UnionWith(layers);
            if (layers.Count == 0) Unsupported(PreparedScene.NonOccludingPrefix + $"Layer not found '{layerPattern}'");
            foreach (var match in matches) _pending.Enqueue(item with { Value = match.Id, Identity = item.Identity + "/" + match.Id, LayerPattern = item.LayerPattern ?? layerPattern });
            return;
        }
        if (value is Guid id)
        {
            var obj = _document.Objects.FindId(id);
            if (obj is null) throw new OperationCanceledException("Source deleted during capture.");
            value = obj;
        }
        uint? sourceSerial = null;
        if (value is RhinoObject source)
        {
            sourceSerial = source.RuntimeSerialNumber;
            _scope.Read(_document, source, gated: item.Definitions.Length > 0 || item.LayerPattern is not null);
            // A nested object keeps its own layer (Rhino has no layer By Parent).
            var layer = _document.Layers[source.Attributes.LayerIndex];
            item = item with { Section = SectionCapture.Capture(_document, source, item.Section), LayerIndex = layer.Index, LayerPath = layer.FullPath,
                // Only a top-level item is "the object"; parts of a Grasshopper-made instance stay Grasshopper sources.
                Top = item.Top ?? (item.Definitions.Length == 0 ? new(source.Id, string.IsNullOrWhiteSpace(source.Attributes.Name) ? null : source.Attributes.Name) : null) };
            value = source.Geometry;
        }
        if (value is InstanceReferenceGeometry instance)
        {
            var definition = _document.InstanceDefinitions.FindId(instance.ParentIdefId);
            if (definition is null || item.Definitions.Contains(instance.ParentIdefId) || item.Definitions.Length >= 256)
            { Unsupported("Invalid/cyclic block"); return; }
            _scope.Definitions.Add(definition.Id);
            var members = definition.GetObjects();
            if (members.Length == 0) { Unsupported("Empty/unavailable block definition " + definition.Name); return; }
            foreach (var leaf in members.OrderBy(o => o.Id))
            {
                // Parts are drawn as Rhino shows them: their own and their layers' visibility decide, so both are watched.
                _scope.Gated.Add(leaf.Id); _scope.GateLayers.Add(leaf.Attributes.LayerIndex);
                if (!IsDrawable(_document, leaf)) continue;
                _pending.Enqueue(new(leaf.Id, item.Path, item.Identity + "/" + definition.Id + "/" + leaf.Id,
                    item.Chain.Append(instance.Xform).ToArray(), item.Definitions.Append(definition.Id).ToArray(), item.Section)
                    { SourceItem = item.SourceItem, Top = item.Top, LayerPattern = item.LayerPattern, LayerIndex = item.LayerIndex, LayerPath = item.LayerPath });
            }
            return;
        }
        GeometryBase? geometry = value switch
        {
            GeometryBase g => g,
            Point3d p => new Point(p), Line l => new LineCurve(l), Polyline p => new PolylineCurve(p),
            Arc a => new ArcCurve(a), Circle c => new ArcCurve(c), Rectangle3d r => r.ToNurbsCurve(),
            Box b => b.ToBrep(), BoundingBox b => b.ToBrep(), Sphere s => s.ToBrep(),
            Cylinder c => c.ToBrep(true, true), Cone c => c.ToBrep(true), Torus t => t.ToBrep(), _ => null
        };
        var temporary = value is not GeometryBase && geometry is not null;
        try
        {
            // Flat annotation-like objects and point clouds can't hide anything: report them, don't quarantine the view.
            if (geometry is AnnotationBase or TextDot or Hatch or PointCloud)
            { Unsupported(PreparedScene.NonOccludingPrefix + geometry.GetType().Name); return; }
            if (geometry is not (Brep or Mesh or Curve or Surface or Extrusion or SubD or Point) || geometry is ClippingPlaneSurface)
            { Unsupported(geometry?.GetType().Name ?? value.GetType().Name); return; }
            // Each document leaf is content-hashed once per coherent capture, including repeated definitions.
            var fingerprint = sourceSerial is { } serial && _fingerprints.TryGetValue(serial, out var reused)
                ? reused : GeometryFingerprint.Compute(geometry);
            if (sourceSerial is { } capturedSerial) _fingerprints[capturedSerial] = fingerprint;
            var localId = item.Definitions.Length == 0 ? item.Identity : item.Definitions[^1] + "/" + item.Identity.Split('/')[^1];
            var normalisationType = temporary ? value.GetType().Name : geometry.GetType().Name;
            var key = $"{_watch.DocumentSerial}/{localId}/{fingerprint}/normalise-v2/{normalisationType}";
            var lease = _cache.Geometry(key, () => Normalise(geometry, temporary ? value.GetType().Name : null));
            _leaves.Add(new(item.Identity, key, item.Path, item.Chain, lease, item.Section ?? SourceSection.Plain) { SourceItem = item.SourceItem });
            if (item.Section is { } section && sourceSerial is not null)
            {
                _colours[item.Identity] = section.Style.ObjectColour;
                if (section.Style.Pattern is not null) _scope.UsesHatchPatterns = true;
            }
            _groups[item.Identity] = item.Top is { } top
                ? new((int[])item.Path.Clone(), item.SourceItem, true, item.LayerIndex, item.LayerPath, top.Id, top.Name, item.LayerPattern)
                : new((int[])item.Path.Clone(), item.SourceItem, false, item.LayerIndex, item.LayerPath, Guid.Empty, null);
        }
        finally { if (temporary) geometry?.Dispose(); }
    }
    private static OwnedGeometry Normalise(GeometryBase geometry, string? primitive = null)
    {
        var owned = geometry.Duplicate();
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
    public LeaseEntry<PreparedScene>.Lease Accept()
    {
        var timer = Stopwatch.StartNew();
        Verify(final: true);
        if (_accepted || _pending.Count != 0) throw new InvalidOperationException("Capture is not ready for acceptance.");
        // Native coincident-edge attribution depends on insertion order. Stabilise sources as
        // well as extracted pieces, and give a permuted but identical scene the same revision.
        _leaves.Sort((a, b) =>
        {
            for (var i = 0; i < Math.Min(a.Path.Length, b.Path.Length); i++)
            { var order = a.Path[i].CompareTo(b.Path[i]); if (order != 0) return order; }
            var result = a.Path.Length.CompareTo(b.Path.Length);
            if (result == 0) result = StringComparer.Ordinal.Compare(a.Identity, b.Identity);
            if (result == 0) result = StringComparer.Ordinal.Compare(a.Key, b.Key);
            return result;
        });
        static string Matrix(Transform t) => string.Join(",", Enumerable.Range(0, 4)
            .SelectMany(r => Enumerable.Range(0, 4).Select(c => t[r, c].ToString("R", CultureInfo.InvariantCulture))));
        var signature = string.Join("|", _leaves.Select(l => l.SourceItem + ":" + l.Identity + ":" + l.Key + ":" + l.Section.Revision + ":" + string.Join(",", l.Path) + ":" + string.Join(";", l.Chain.Select(Matrix))));
        signature += string.Join("|", _unsupported.OrderBy(p => p.Key)) + _tolerance.ToString("R", CultureInfo.InvariantCulture);
        var lease = _cache.Scene(SceneCache.Hash(signature), _leaves, _unsupported, _tolerance);
        ActiveSeconds += timer.Elapsed.TotalSeconds;
        _accepted = true; return lease;
    }
    public void Dispose() { if (!_accepted) foreach (var leaf in _leaves) leaf.Geometry.Dispose(); }
}
