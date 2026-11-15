using System.Diagnostics;
using System.Globalization;
using M2DPlus.Core;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using Environment = System.Environment;

namespace M2DPlus.Rhino.Services;

/// <summary>All members except Generation/IsClosed run on the constructing host thread.</summary>
public sealed class DocumentWatch : IDisposable
{
    private long _generation;
    private int _closed;
    private string _hostStamp;
    private readonly int _hostThread = Environment.CurrentManagedThreadId;
    public uint DocumentSerial { get; }
    public long Generation => Interlocked.Read(ref _generation);
    public bool IsClosed => Volatile.Read(ref _closed) != 0;
    public event Action? Changed;
    public DocumentWatch(RhinoDoc document)
    {
        DocumentSerial = document.RuntimeSerialNumber;
        _hostStamp = Stamp(document);
        RhinoDoc.AddRhinoObject += ObjectChanged; RhinoDoc.DeleteRhinoObject += ObjectChanged;
        RhinoDoc.UndeleteRhinoObject += ObjectChanged;
        RhinoDoc.ModifyObjectAttributes += AttributesChanged;
        RhinoDoc.LayerTableEvent += LayerChanged; RhinoDoc.HatchPatternTableEvent += HatchChanged;
        RhinoDoc.InstanceDefinitionTableEvent += DefinitionChanged;
        RhinoDoc.CloseDocument += Closed;
    }
    // Headless documents can omit table notifications. Verify serial/membership witnesses as well as events.
    // This is host-only; content hashes on captured geometry remain the cache identity.
    private static string Stamp(RhinoDoc document)
    {
        var objects = document.Objects.GetObjectList(new ObjectEnumeratorSettings
            { NormalObjects = true, LockedObjects = true, HiddenObjects = true, IdefObjects = true, ReferenceObjects = true });
        return string.Join("|", objects.Select(o => $"{o.Id}:{o.RuntimeSerialNumber}:{o.Attributes.ToJSON(new global::Rhino.FileIO.SerializationOptions())}")) +
            string.Join("|", document.InstanceDefinitions.Where(d => !d.IsDeleted).Select(d => $"{d.Id}:{string.Join(',', d.GetObjects().Select(o => o.RuntimeSerialNumber))}")) +
            string.Join("|", document.Layers.Where(l => !l.IsDeleted).Select(l => $"{l.Id}:{l.IsVisible}:{l.FullPath}:{l.ToJSON(new global::Rhino.FileIO.SerializationOptions())}")) +
            document.ModelAbsoluteTolerance.ToString("R", CultureInfo.InvariantCulture) + document.ModelUnitSystem +
            string.Join("|", document.NamedViews.Select(v => ViewRequest.Parse(v).Revision)) +
            string.Join("|", document.HatchPatterns.Where(p => !p.IsDeleted).Select(p => p.ToJSON(new global::Rhino.FileIO.SerializationOptions())));
    }
    public void VerifyHostState()
    {
        if (_hostThread != Environment.CurrentManagedThreadId) throw new InvalidOperationException("Document verification must run on the host thread.");
        var document = RhinoDoc.FromRuntimeSerialNumber(DocumentSerial);
        if (document is null || !document.IsAvailable)
        {
            if (Interlocked.Exchange(ref _closed, 1) == 0) Touch(DocumentSerial);
            return;
        }
        var stamp = Stamp(document);
        if (_hostStamp == stamp) return;
        _hostStamp = stamp; Touch(DocumentSerial);
    }
    private void Touch(uint serial) { if (serial == DocumentSerial) { Interlocked.Increment(ref _generation); Changed?.Invoke(); } }
    private void ObjectChanged(object? sender, RhinoObjectEventArgs e)
    {
        // A deleted object's document wrapper may already be unavailable. Conservatively
        // invalidate this watcher in that case; final host witnesses still verify coherence.
        Touch(e.TheObject.Document?.RuntimeSerialNumber ?? DocumentSerial);
    }
    private void AttributesChanged(object? sender, RhinoModifyObjectAttributesEventArgs e) => Touch(e.Document.RuntimeSerialNumber);
    private void LayerChanged(object? sender, global::Rhino.DocObjects.Tables.LayerTableEventArgs e) => Touch(e.Document.RuntimeSerialNumber);
    private void HatchChanged(object? sender, global::Rhino.DocObjects.Tables.HatchPatternTableEventArgs e) => Touch(e.Document.RuntimeSerialNumber);
    private void DefinitionChanged(object? sender, global::Rhino.DocObjects.Tables.InstanceDefinitionTableEventArgs e) => Touch(e.Document.RuntimeSerialNumber);
    private void Closed(object? sender, DocumentEventArgs e)
    {
        if (e.Document.RuntimeSerialNumber != DocumentSerial) return;
        Interlocked.Exchange(ref _closed, 1); Touch(DocumentSerial);
    }
    public void Dispose()
    {
        RhinoDoc.AddRhinoObject -= ObjectChanged; RhinoDoc.DeleteRhinoObject -= ObjectChanged;
        RhinoDoc.UndeleteRhinoObject -= ObjectChanged;
        RhinoDoc.ModifyObjectAttributes -= AttributesChanged; RhinoDoc.LayerTableEvent -= LayerChanged; RhinoDoc.HatchPatternTableEvent -= HatchChanged;
        RhinoDoc.InstanceDefinitionTableEvent -= DefinitionChanged; RhinoDoc.CloseDocument -= Closed;
    }
}

public sealed record CaptureInput(object Value, int[] Path, string Identity);

/// <summary>Host-only incremental capture. No document/table/reference survives into PreparedScene.</summary>
public sealed class SceneCapture : IDisposable
{
    private sealed record Pending(object Value, int[] Path, string Identity, Transform[] Chain, Guid[] Definitions, SourceSection? Section = null);
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
    /// <param name="excluded">Document mode only: objects never drawn, such as the view objects (view boxes and
    /// not-yet-validated frame surfaces) that the View input references.</param>
    public SceneCapture(RhinoDoc document, DocumentWatch watch, IEnumerable<CaptureInput>? input = null, SceneCache? cache = null,
        IReadOnlySet<Guid>? excluded = null)
    {
        var timer = Stopwatch.StartNew();
        _document = document; _watch = watch; _generation = watch.Generation; _cache = cache ?? SceneCache.Shared;
        _tolerance = document.ModelAbsoluteTolerance;
        if (input is null)
        {
            var settings = new ObjectEnumeratorSettings { NormalObjects = true, LockedObjects = true, HiddenObjects = false,
                ReferenceObjects = true, IncludeLights = false, IncludeGrips = false };
            foreach (var obj in document.Objects.GetObjectList(settings))
                if (IsDrawable(document, obj) && excluded?.Contains(obj.Id) != true) _pending.Enqueue(new(obj.Id, new[] { 0 }, obj.Id.ToString(), Array.Empty<Transform>(), Array.Empty<Guid>()));
        }
        else foreach (var item in input) _pending.Enqueue(new(item.Value, (int[])item.Path.Clone(), item.Identity, Array.Empty<Transform>(), Array.Empty<Guid>()));
        InitialEnumerationMilliseconds = timer.Elapsed.TotalMilliseconds;
    }
    /// <summary>Document mode's scene rule (UX §3.1): visible, not a clipping plane or light, not on an M2D+ layer and
    /// not tagged with m2d.* user text (bakes and view boxes).</summary>
    internal static bool IsDrawable(RhinoDoc document, RhinoObject obj)
    {
        if (obj.IsHidden || obj.IsDeleted || obj.Geometry is ClippingPlaneSurface or Light) return false;
        var layer = document.Layers[obj.Attributes.LayerIndex];
        if (!layer.IsVisible || layer.FullPath.Split(new[] { "::" }, StringSplitOptions.None).Contains("M2D+")) return false;
        for (var parentId = layer.ParentLayerId; parentId != Guid.Empty;)
        {
            var parent = document.Layers.FindId(parentId);
            if (parent is null || !parent.IsVisible) return false;
            parentId = parent.ParentLayerId;
        }
        var strings = obj.Attributes.GetUserStrings();
        return strings.AllKeys.Concat(obj.Geometry.GetUserStrings().AllKeys)
            .All(key => key is null || !key.StartsWith("m2d.", StringComparison.OrdinalIgnoreCase));
    }
    private void Verify()
    {
        if (_thread != Environment.CurrentManagedThreadId) throw new InvalidOperationException("Capture must run on its host thread.");
        _watch.VerifyHostState();
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
        return _pending.Count == 0;
    }
    private void Unsupported(string name) => _unsupported[name] = _unsupported.GetValueOrDefault(name) + 1;
    private void Capture(Pending item)
    {
        var value = item.Value;
        if (value is string layerPattern)
        {
            // A bare leaf name matches that layer; a full path or wildcard matches FullPath.
            var expression = "^" + System.Text.RegularExpressions.Regex.Escape(layerPattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
            var layers = _document.Layers.Where(l => !l.IsDeleted &&
                (System.Text.RegularExpressions.Regex.IsMatch(l.FullPath, expression, System.Text.RegularExpressions.RegexOptions.IgnoreCase) ||
                 (!layerPattern.Contains("::") && System.Text.RegularExpressions.Regex.IsMatch(l.Name, expression, System.Text.RegularExpressions.RegexOptions.IgnoreCase))))
                .Select(l => l.Index).ToHashSet();
            var matches = _document.Objects.GetObjectList(new ObjectEnumeratorSettings { NormalObjects = true, LockedObjects = true, ReferenceObjects = true })
                .Where(o => layers.Contains(o.Attributes.LayerIndex) && IsDrawable(_document, o)).OrderBy(o => o.Id).ToArray();
            if (layers.Count == 0) Unsupported(PreparedScene.NonOccludingPrefix + $"Layer not found '{layerPattern}'");
            foreach (var match in matches) _pending.Enqueue(item with { Value = match.Id, Identity = item.Identity + "/" + match.Id });
            return;
        }
        if (value is Guid id)
        {
            var obj = _document.Objects.FindId(id);
            if (obj is null) throw new OperationCanceledException("Source deleted during capture.");
            value = obj;
        }
        uint? sourceSerial = null;
        if (value is RhinoObject source) { sourceSerial = source.RuntimeSerialNumber; item = item with { Section = SectionCapture.Capture(_document, source, item.Section) }; value = source.Geometry; }
        if (value is InstanceReferenceGeometry instance)
        {
            var definition = _document.InstanceDefinitions.FindId(instance.ParentIdefId);
            if (definition is null || item.Definitions.Contains(instance.ParentIdefId) || item.Definitions.Length >= 256)
            { Unsupported("Invalid/cyclic block"); return; }
            var members = definition.GetObjects();
            if (members.Length == 0) { Unsupported("Empty/unavailable block definition " + definition.Name); return; }
            foreach (var leaf in members.OrderBy(o => o.Id))
            {
                if (!IsDrawable(_document, leaf)) continue;
                _pending.Enqueue(new(leaf.Id, item.Path, item.Identity + "/" + definition.Id + "/" + leaf.Id,
                    item.Chain.Append(instance.Xform).ToArray(), item.Definitions.Append(definition.Id).ToArray(), item.Section));
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
            _leaves.Add(new(item.Identity, key, item.Path, item.Chain, lease, item.Section ?? SourceSection.Plain));
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
        Verify();
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
        var signature = string.Join("|", _leaves.Select(l => l.Identity + ":" + l.Key + ":" + l.Section.Revision + ":" + string.Join(",", l.Path) + ":" + string.Join(";", l.Chain.Select(Matrix))));
        signature += string.Join("|", _unsupported.OrderBy(p => p.Key)) + _tolerance.ToString("R", CultureInfo.InvariantCulture);
        var lease = _cache.Scene(SceneCache.Hash(signature), _leaves, _unsupported, _tolerance);
        _accepted = true; return lease;
    }
    public void Dispose() { if (!_accepted) foreach (var leaf in _leaves) leaf.Geometry.Dispose(); }
}
