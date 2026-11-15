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
        RhinoDoc.LayerTableEvent += LayerChanged;
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
            string.Join("|", document.Layers.Where(l => !l.IsDeleted).Select(l => $"{l.Id}:{l.IsVisible}:{l.FullPath}")) +
            document.ModelAbsoluteTolerance.ToString("R", CultureInfo.InvariantCulture) + document.ModelUnitSystem;
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
        RhinoDoc.ModifyObjectAttributes -= AttributesChanged; RhinoDoc.LayerTableEvent -= LayerChanged;
        RhinoDoc.InstanceDefinitionTableEvent -= DefinitionChanged; RhinoDoc.CloseDocument -= Closed;
    }
}

public sealed record CaptureInput(object Value, int[] Path, string Identity);

/// <summary>Host-only incremental capture. No document/table/reference survives into PreparedScene.</summary>
public sealed class SceneCapture : IDisposable
{
    private sealed record Pending(object Value, int[] Path, string Identity, Transform[] Chain, Guid[] Definitions);
    private readonly int _thread = Environment.CurrentManagedThreadId;
    private readonly RhinoDoc _document;
    private readonly DocumentWatch _watch;
    private readonly long _generation;
    private readonly SceneCache _cache;
    private readonly Queue<Pending> _pending = new();
    private readonly List<SceneLeaf> _leaves = new();
    private readonly Dictionary<string, int> _unsupported = new(StringComparer.Ordinal);
    private readonly double _tolerance;
    private bool _accepted;
    public int CapturedCount => _leaves.Count;
    public double MaximumSliceMilliseconds { get; private set; }
    public double InitialEnumerationMilliseconds { get; }
    public SceneCapture(RhinoDoc document, DocumentWatch watch, IEnumerable<CaptureInput>? input = null, SceneCache? cache = null)
    {
        var timer = Stopwatch.StartNew();
        _document = document; _watch = watch; _generation = watch.Generation; _cache = cache ?? SceneCache.Shared;
        _tolerance = document.ModelAbsoluteTolerance;
        if (input is null)
        {
            var settings = new ObjectEnumeratorSettings { NormalObjects = true, LockedObjects = true, HiddenObjects = false,
                ReferenceObjects = true, IncludeLights = false, IncludeGrips = false };
            foreach (var obj in document.Objects.GetObjectList(settings))
                if (Included(obj)) _pending.Enqueue(new(obj.Id, new[] { 0 }, obj.Id.ToString(), Array.Empty<Transform>(), Array.Empty<Guid>()));
        }
        else foreach (var item in input) _pending.Enqueue(new(item.Value, (int[])item.Path.Clone(), item.Identity, Array.Empty<Transform>(), Array.Empty<Guid>()));
        InitialEnumerationMilliseconds = timer.Elapsed.TotalMilliseconds;
    }
    private bool Included(RhinoObject obj)
    {
        if (obj.IsHidden || obj.IsDeleted || obj.Geometry is ClippingPlaneSurface or Light) return false;
        var layer = _document.Layers[obj.Attributes.LayerIndex];
        if (!layer.IsVisible || layer.FullPath.Split(new[] { "::" }, StringSplitOptions.None).Contains("M2D+")) return false;
        for (var parentId = layer.ParentLayerId; parentId != Guid.Empty;)
        {
            var parent = _document.Layers.FindId(parentId);
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
        if (value is Guid id)
        {
            var obj = _document.Objects.FindId(id);
            if (obj is null) throw new OperationCanceledException("Source deleted during capture.");
            value = obj;
        }
        if (value is RhinoObject source) value = source.Geometry;
        if (value is InstanceReferenceGeometry instance)
        {
            var definition = _document.InstanceDefinitions.FindId(instance.ParentIdefId);
            if (definition is null || item.Definitions.Contains(instance.ParentIdefId) || item.Definitions.Length >= 64)
            { Unsupported("Invalid/cyclic block"); return; }
            foreach (var leaf in definition.GetObjects().OrderBy(o => o.Id))
            {
                if (!Included(leaf)) continue;
                _pending.Enqueue(new(leaf.Id, item.Path, item.Identity + "/" + definition.Id + "/" + leaf.Id,
                    item.Chain.Append(instance.Xform).ToArray(), item.Definitions.Append(definition.Id).ToArray()));
            }
            return;
        }
        GeometryBase? geometry = value switch
        {
            GeometryBase g => g,
            Point3d p => new Point(p), Line l => new LineCurve(l), Polyline p => new PolylineCurve(p),
            Arc a => new ArcCurve(a), Circle c => new ArcCurve(c), _ => null
        };
        var temporary = value is not GeometryBase && geometry is not null;
        try
        {
            // Flat annotation-like objects and point clouds can't hide anything: report them, don't quarantine the view.
            if (geometry is AnnotationBase or TextDot or Hatch or PointCloud)
            { Unsupported(PreparedScene.NonOccludingPrefix + geometry.GetType().Name); return; }
            if (geometry is not (Brep or Mesh or Curve or Surface or Extrusion or SubD or Point) || geometry is ClippingPlaneSurface)
            { Unsupported(geometry?.GetType().Name ?? value.GetType().Name); return; }
            // Hash the complete serialized content, never a hash of DataCRC. Definition-local identity enables repeat reuse.
            var fingerprint = SceneCache.Hash(geometry.ToJSON(new global::Rhino.FileIO.SerializationOptions()));
            var localId = item.Definitions.Length == 0 ? item.Identity : item.Definitions[^1] + "/" + item.Identity.Split('/')[^1];
            var key = $"{_watch.DocumentSerial}/{localId}/{fingerprint}/normalise-v1";
            var lease = _cache.Geometry(key, () => Normalise(geometry));
            _leaves.Add(new(item.Identity, key, item.Path, item.Chain, lease));
        }
        finally { if (temporary) geometry?.Dispose(); }
    }
    private static OwnedGeometry Normalise(GeometryBase geometry)
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
            var conversion = ReferenceEquals(owned, converted) ? null : $"{owned.GetType().Name} to Brep";
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
        static string Matrix(Transform t) => string.Join(",", Enumerable.Range(0, 4)
            .SelectMany(r => Enumerable.Range(0, 4).Select(c => t[r, c].ToString("R", CultureInfo.InvariantCulture))));
        var signature = string.Join("|", _leaves.Select(l => l.Identity + ":" + l.Key + ":" + string.Join(",", l.Path) + ":" + string.Join(";", l.Chain.Select(Matrix))));
        signature += string.Join("|", _unsupported.OrderBy(p => p.Key)) + _tolerance.ToString("R", CultureInfo.InvariantCulture);
        var lease = _cache.Scene(SceneCache.Hash(signature), _leaves, _unsupported, _tolerance);
        _accepted = true; return lease;
    }
    public void Dispose() { if (!_accepted) foreach (var leaf in _leaves) leaf.Geometry.Dispose(); }
}
