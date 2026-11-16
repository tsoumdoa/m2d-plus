using System.Diagnostics;
using System.Globalization;
using M2DPlus.Core;
using M2DPlus.Core.Watch;
using Rhino;
using Rhino.DocObjects;
using Environment = System.Environment;

namespace M2DPlus.Rhino.Services;

/// <summary>
/// What the current drawing reads from the Rhino document: filled by <see cref="SceneCapture"/> as it reads (objects,
/// block parts, layers, definitions) and by the component (view objects). <see cref="DocumentWatch"/> re-runs the
/// drawing only for changes to these (UX §5.1). Host thread only.
/// </summary>
public sealed class WatchScope
{
    public WatchScope(WatchMode mode, IEnumerable<string>? layerPatterns = null, IEnumerable<Guid>? views = null, bool textViews = false)
    {
        Mode = mode; LayerPatterns = (layerPatterns ?? []).ToArray();
        _views = (views ?? []).Where(id => id != Guid.Empty).ToHashSet(); TextViews = textViews;
    }
    public WatchMode Mode { get; }
    /// <summary>Layer-name Geometry items (<c>Walls</c>, <c>Structure::*</c>). They select every object on matching layers,
    /// whatever its or the layers' visibility, so only layer names and membership matter.</summary>
    public IReadOnlyList<string> LayerPatterns { get; }
    /// <summary>View objects the View input references (view boxes, frame surfaces, clipping planes).</summary>
    public IReadOnlySet<Guid> Views => _views;
    private HashSet<Guid> _views;
    /// <summary>The View input changed while the captured scene is reused: watch the new view objects.</summary>
    public void SetViews(IEnumerable<Guid> views, bool textViews)
    { _views = views.Where(id => id != Guid.Empty).ToHashSet(); TextViews = textViews; }
    /// <summary>A View item is text (a named view, clipping plane name or direction): clipping planes and named views matter.</summary>
    public bool TextViews { get; private set; }
    internal HashSet<Guid> Objects { get; } = new();
    /// <summary>Drawn only while visible: block parts and objects a layer name selects.</summary>
    internal HashSet<Guid> Gated { get; } = new();
    internal HashSet<int> UsedLayers { get; } = new();
    internal HashSet<int> GateLayers { get; } = new();
    internal HashSet<Guid> Definitions { get; } = new();
    internal HashSet<int> Materials { get; } = new();
    internal bool UsesHatchPatterns { get; set; }
    /// <summary>Rhino objects read as drawn sources (top-level and block parts), for tests and diagnostics.</summary>
    public int ObjectCount => Objects.Count;

    internal void Read(RhinoDoc document, RhinoObject obj, bool gated)
    {
        Objects.Add(obj.Id);
        if (gated) Gated.Add(obj.Id);
        UsedLayers.Add(obj.Attributes.LayerIndex);
        if (obj.Attributes.MaterialIndex >= 0) Materials.Add(obj.Attributes.MaterialIndex);
        var layer = document.Layers[obj.Attributes.LayerIndex];
        if (layer?.RenderMaterialIndex >= 0) Materials.Add(layer.RenderMaterialIndex);
    }

    internal bool MatchesPattern(RhinoDoc document, int layerIndex)
    {
        if (LayerPatterns.Count == 0 || layerIndex < 0 || layerIndex >= document.Layers.Count) return false;
        var layer = document.Layers[layerIndex];
        return LayerPatterns.Any(p => SceneCapture.LayerMatches(layer, p));
    }
}

/// <summary>
/// Watches the Rhino document for changes that affect the drawing, and only those (owner request 2026-11-15, UX §5.1).
/// Each event is classified against the <see cref="Scope"/> by <see cref="RerunPolicy"/>: with Geometry given, hiding,
/// showing, layer on/off and per-viewport visibility never re-run; document mode follows model visibility; viewport-only
/// changes (camera, display mode) raise no document event at all. Headless documents may raise no events, so
/// <see cref="VerifyHostState"/> also compares a facet snapshot with the same policy.
/// All members except <see cref="Generation"/> and <see cref="IsClosed"/> run on the constructing host thread.
/// </summary>
public sealed class DocumentWatch : IDisposable
{
    private long _generation;
    private int _closed;
    private readonly int _hostThread = Environment.CurrentManagedThreadId;
    private Snapshot _snapshot;
    private bool _rebase;
    private long _verifiedAt;
    private WatchScope? _scope;
    public uint DocumentSerial { get; }
    public long Generation => Interlocked.Read(ref _generation);
    public bool IsClosed => Volatile.Read(ref _closed) != 0;
    /// <summary>Raised (host thread) for a change that must re-run the drawing.</summary>
    public event Action? Changed;
    /// <summary>Raised (host thread) for a change to view objects only: re-run the drawing, keep the captured scene.</summary>
    public event Action? ViewsChanged;
    private long _viewGeneration;
    /// <summary>Counts view-only changes; <see cref="Generation"/> counts changes to the scene.</summary>
    public long ViewGeneration => Interlocked.Read(ref _viewGeneration);
    public int ViewReruns { get; private set; }
    /// <summary>Changes that re-ran / were ignored, and the last re-run's reason (diagnostics and tests).</summary>
    public int Reruns { get; private set; }
    public int Ignored { get; private set; }
    public string? LastReason { get; private set; }

    /// <summary>What the current drawing reads. Null (before the first capture) re-runs on every change.</summary>
    public WatchScope? Scope
    {
        get => _scope;
        set { _scope = value; _rebase = true; }
    }

    /// <summary>Host-thread time spent taking document snapshots (construction and verification), for profiling.</summary>
    public double SnapshotMilliseconds { get; private set; }
    public int Snapshots { get; private set; }
    private Snapshot Take(RhinoDoc document, WatchScope? scope)
    {
        var since = Stopwatch.GetTimestamp();
        try { return Snapshot.Take(document, scope); }
        finally { SnapshotMilliseconds += Stopwatch.GetElapsedTime(since).TotalMilliseconds; Snapshots++; }
    }

    /// <summary>Interactive Rhino raises an event for every change the policy reads, so there the watch compares only
    /// a light snapshot (tolerance, units, named views: changes with no event of their own). Headless documents can omit
    /// notifications, so they also compare every object's and layer's facets. Tests may force the full comparison.</summary>
    internal static bool ForceSnapshots { get; set; }
    /// <summary>Profiling only: treat a headless document as interactive (events authoritative).</summary>
    internal static bool SimulateInteractive { get; set; }
    public bool UsesSnapshots { get; }

    public DocumentWatch(RhinoDoc document)
    {
        DocumentSerial = document.RuntimeSerialNumber;
        UsesSnapshots = ForceSnapshots || document.IsHeadless && !SimulateInteractive;
        _snapshot = UsesSnapshots ? Take(document, null) : Snapshot.Light(document);
        RhinoDoc.AddRhinoObject += Added; RhinoDoc.DeleteRhinoObject += Deleted; RhinoDoc.UndeleteRhinoObject += Added;
        RhinoDoc.ModifyObjectAttributes += AttributesChanged;
        RhinoDoc.LayerTableEvent += LayerChanged; RhinoDoc.HatchPatternTableEvent += HatchChanged;
        RhinoDoc.InstanceDefinitionTableEvent += DefinitionChanged; RhinoDoc.MaterialTableEvent += MaterialChanged;
        RhinoDoc.DocumentPropertiesChanged += PropertiesChanged;
        RhinoDoc.CloseDocument += Closed;
    }

    // ------------------------------------------------------------------ classification

    /// <summary>The role of an object (with the given, possibly new, attributes) for the current scope.</summary>
    internal ObjectRole Role(RhinoDoc document, RhinoObject obj, ObjectAttributes attributes)
    {
        if (_scope is not { } scope) return ObjectRole.Candidate;
        if (scope.Views.Contains(obj.Id) || scope.TextViews && obj is ClippingPlaneObject) return ObjectRole.View;
        if (scope.Gated.Contains(obj.Id)) return ObjectRole.Gated;
        if (scope.Objects.Contains(obj.Id)) return ObjectRole.Scene;
        if (obj.IsDeleted || obj.IsInstanceDefinitionGeometry) return ObjectRole.Unrelated;
        if (scope.Mode == WatchMode.Document) return SceneCapture.IsDrawable(document, obj, attributes) ? ObjectRole.Candidate : ObjectRole.Unrelated;
        // A layer name selects every selectable object on its layers, hidden or not: a new object there, or one moved
        // onto such a layer, is missing from the drawing.
        return SceneCapture.IsSelectable(document, obj, attributes) && scope.MatchesPattern(document, attributes.LayerIndex)
            ? ObjectRole.Candidate : ObjectRole.Unrelated;
    }

    /// <summary>The role of a deleted or purged object: only scope membership counts (it was drawn or it wasn't).</summary>
    private ObjectRole RoleOf(Guid id) => _scope is not { } scope ? ObjectRole.Candidate
        : scope.Views.Contains(id) ? ObjectRole.View : scope.Gated.Contains(id) ? ObjectRole.Gated
        : scope.Objects.Contains(id) ? ObjectRole.Scene : ObjectRole.Unrelated;

    internal LayerRole LayerRole(RhinoDoc document, Layer layer)
    {
        if (_scope is not { } scope) return Core.Watch.LayerRole.Used | Core.Watch.LayerRole.Gate | Core.Watch.LayerRole.Pattern;
        var role = Core.Watch.LayerRole.None;
        if (scope.LayerPatterns.Count > 0) role |= Core.Watch.LayerRole.Pattern;
        if (AncestorOrSelf(document, layer.Id, scope.UsedLayers)) role |= Core.Watch.LayerRole.Used;
        if (AncestorOrSelf(document, layer.Id, scope.GateLayers) ||
            scope.Mode == WatchMode.Document && ((role & Core.Watch.LayerRole.Used) != 0 || HoldsDrawable(document, layer)))
            role |= Core.Watch.LayerRole.Gate;
        return role;
    }

    private static bool AncestorOrSelf(RhinoDoc document, Guid layerId, IEnumerable<int> layers)
    {
        foreach (var index in layers)
        {
            if (index < 0 || index >= document.Layers.Count) continue;
            for (var current = document.Layers[index]; current is not null;)
            {
                if (current.Id == layerId) return true;
                if (current.ParentLayerId == Guid.Empty) break;
                current = document.Layers.FindId(current.ParentLayerId);
            }
        }
        return false;
    }

    /// <summary>Document mode: the layer or a sublayer holds an object that is drawn whenever its layer is on.</summary>
    private static bool HoldsDrawable(RhinoDoc document, Layer layer)
    {
        var subtree = new HashSet<Guid> { layer.Id };
        for (var grew = true; grew;)
        {
            grew = false;
            foreach (var other in document.Layers)
                if (!other.IsDeleted && subtree.Contains(other.ParentLayerId) && subtree.Add(other.Id)) grew = true;
        }
        foreach (var id in subtree)
        {
            var index = document.Layers.FindId(id)?.Index ?? -1;
            if (index < 0) continue;
            // Objects on an off layer count as hidden to the enumerator: list them all and test their own visibility.
            var settings = new ObjectEnumeratorSettings { NormalObjects = true, LockedObjects = true, HiddenObjects = true,
                ReferenceObjects = true, IncludeLights = false, IncludeGrips = false, LayerIndexFilter = index };
            if (document.Objects.GetObjectList(settings).Any(o => o.Attributes.Visible && o.Attributes.Mode != ObjectMode.Hidden
                && SceneCapture.IsSelectable(document, o, o.Attributes))) return true;
        }
        return false;
    }

    private void Decide(bool rerun, string reason)
    {
        if (!rerun) { Ignored++; return; }
        _rebase = true;
        Touch(reason);
    }

    // ------------------------------------------------------------------ events

    private RhinoDoc? Mine(RhinoDoc? document) => document is not null && document.RuntimeSerialNumber == DocumentSerial ? document : null;

    private void Added(object? sender, RhinoObjectEventArgs e)
    {
        // A deleted object's document wrapper may be unavailable; fall back to scope membership.
        var document = Mine(e.TheObject.Document);
        if (document is null && e.TheObject.Document is not null) return;
        var role = document is null ? RoleOf(e.ObjectId) : Role(document, e.TheObject, e.TheObject.Attributes);
        Decide(RerunPolicy.Object(_scope?.Mode ?? WatchMode.Document, role, ObjectChange.Added), $"object {e.ObjectId} added ({role})");
    }

    private void Deleted(object? sender, RhinoObjectEventArgs e)
    {
        if (e.TheObject.Document is { } d && d.RuntimeSerialNumber != DocumentSerial) return;
        var role = RoleOf(e.ObjectId);
        Decide(RerunPolicy.Object(_scope?.Mode ?? WatchMode.Document, role, ObjectChange.Deleted), $"object {e.ObjectId} deleted ({role})");
    }

    private void AttributesChanged(object? sender, RhinoModifyObjectAttributesEventArgs e)
    {
        if (Mine(e.Document) is not { } document) return;
        var change = ObjectFacets.Of(document, e.OldAttributes, 0).Diff(ObjectFacets.Of(document, e.NewAttributes, 0));
        var role = Role(document, e.RhinoObject, e.NewAttributes);
        Decide(RerunPolicy.Object(_scope?.Mode ?? WatchMode.Document, role, change), $"object {e.RhinoObject.Id} {change} ({role})");
    }

    private void LayerChanged(object? sender, global::Rhino.DocObjects.Tables.LayerTableEventArgs e)
    {
        if (Mine(e.Document) is not { } document) return;
        var change = e.EventType switch
        {
            global::Rhino.DocObjects.Tables.LayerTableEventType.Added => LayerChange.Added,
            global::Rhino.DocObjects.Tables.LayerTableEventType.Deleted => LayerChange.Deleted,
            global::Rhino.DocObjects.Tables.LayerTableEventType.Undeleted => LayerChange.Added | LayerChange.Visibility,
            global::Rhino.DocObjects.Tables.LayerTableEventType.Modified when e.OldState is { } old && e.NewState is { } now =>
                LayerFacets.Of(old, own: true).Diff(LayerFacets.Of(now, own: true)),
            global::Rhino.DocObjects.Tables.LayerTableEventType.Modified => LayerChange.Other | LayerChange.Visibility | LayerChange.Colour,
            _ => LayerChange.Other, // sorted, made current
        };
        var layer = e.NewState ?? e.OldState;
        var role = layer is null ? Core.Watch.LayerRole.Used | Core.Watch.LayerRole.Gate : LayerRole(document, layer);
        Decide(RerunPolicy.Layer(role, change), $"layer {layer?.FullPath} {change} ({role})");
    }

    private void HatchChanged(object? sender, global::Rhino.DocObjects.Tables.HatchPatternTableEventArgs e)
    { if (Mine(e.Document) is not null) Decide(_scope is null || _scope.UsesHatchPatterns, "hatch pattern table"); }

    private void DefinitionChanged(object? sender, global::Rhino.DocObjects.Tables.InstanceDefinitionTableEventArgs e)
    {
        if (Mine(e.Document) is null) return;
        var id = e.NewState?.Id ?? e.OldState?.Id ?? Guid.Empty;
        Decide(_scope is null || _scope.Definitions.Contains(id), $"block definition {id}");
    }

    private void MaterialChanged(object? sender, global::Rhino.DocObjects.Tables.MaterialTableEventArgs e)
    { if (Mine(e.Document) is not null) Decide(_scope is null || _scope.Materials.Contains(e.Index), $"material {e.Index}"); }

    private void PropertiesChanged(object? sender, DocumentEventArgs e)
    {
        if (Mine(e.Document) is not { } document) return;
        var now = Snapshot.Properties(document);
        Decide(now != _snapshot.DocumentProperties, "tolerance or units");
    }

    private void Closed(object? sender, DocumentEventArgs e)
    {
        if (e.Document.RuntimeSerialNumber != DocumentSerial) return;
        Interlocked.Exchange(ref _closed, 1); Touch("document closed");
    }

    /// <summary>A change to view objects only (their geometry, name or m2d.* text; clipping planes and named views for
    /// text views). It re-runs the drawing but never touches the captured scene: view objects are never scene geometry.</summary>
    private static bool ViewsOnly(string reason) => reason.EndsWith("(View)", StringComparison.Ordinal) || reason == "named views";

    private void Touch(string reason)
    {
        Reruns++; LastReason = reason;
        if (ViewsOnly(reason)) { ViewReruns++; Interlocked.Increment(ref _viewGeneration); ViewsChanged?.Invoke(); return; }
        Interlocked.Increment(ref _generation); Changed?.Invoke();
    }

    // ------------------------------------------------------------------ snapshot verification (headless documents)

    /// <summary>Compares the document with the last snapshot under the same policy. <paramref name="throttle"/> skips the
    /// comparison when one ran in the last 200 ms (the capture's per-slice check; the final check is never throttled).</summary>
    public void VerifyHostState(bool throttle = false)
    {
        if (_hostThread != Environment.CurrentManagedThreadId) throw new InvalidOperationException("Document verification must run on the host thread.");
        var document = RhinoDoc.FromRuntimeSerialNumber(DocumentSerial);
        if (document is null || !document.IsAvailable)
        {
            if (Interlocked.Exchange(ref _closed, 1) == 0) Touch("document closed");
            return;
        }
        var now = Stopwatch.GetTimestamp();
        if (throttle && !_rebase && Stopwatch.GetElapsedTime(_verifiedAt, now) < TimeSpan.FromMilliseconds(200)) return;
        _verifiedAt = now;
        var current = UsesSnapshots ? Take(document, _scope) : Snapshot.Light(document);
        if (_rebase) { _snapshot = current; _rebase = false; return; }
        var reason = Compare(document, _snapshot, current);
        _snapshot = current;
        if (reason is not null) Touch(reason);
    }

    private string? Compare(RhinoDoc document, Snapshot before, Snapshot after)
    {
        var mode = _scope?.Mode ?? WatchMode.Document;
        if (before.DocumentProperties != after.DocumentProperties) return "tolerance or units";
        if (before.NamedViews != after.NamedViews && (_scope is null || _scope.TextViews)) return "named views";
        if (before.HatchPatterns != after.HatchPatterns && (_scope is null || _scope.UsesHatchPatterns)) return "hatch patterns";
        foreach (var (id, members) in after.Definitions)
            if ((!before.Definitions.TryGetValue(id, out var old) || old != members) && (_scope is null || _scope.Definitions.Contains(id)))
                return $"block definition {id}";
        foreach (var (index, now) in after.Layers)
        {
            var change = before.Layers.TryGetValue(index, out var old) ? old.Diff(now) : LayerChange.Added;
            if (change == LayerChange.None) continue;
            var layer = document.Layers[index];
            if (layer is not null && RerunPolicy.Layer(LayerRole(document, layer), change)) return $"layer {layer.FullPath} {change}";
        }
        foreach (var (id, old) in before.Objects)
        {
            if (after.Objects.TryGetValue(id, out var now))
            {
                var change = old.Diff(now);
                if (change == ObjectChange.None) continue;
                var obj = document.Objects.FindId(id);
                var role = obj is null ? RoleOf(id) : Role(document, obj, obj.Attributes);
                if (RerunPolicy.Object(mode, role, change)) return $"object {id} {change} ({role})";
                Ignored++;
            }
            else if (RoleOf(id) is var gone && RerunPolicy.Object(mode, gone, ObjectChange.Deleted)) return $"object {id} deleted ({gone})";
        }
        foreach (var (id, _) in after.Objects)
        {
            if (before.Objects.ContainsKey(id)) continue;
            // Newly tracked: an added object, or one the capture has just read (unchanged by definition).
            var obj = document.Objects.FindId(id);
            if (obj is not null && Role(document, obj, obj.Attributes) == ObjectRole.Candidate) return $"object {id} added";
        }
        return null;
    }

    public void Dispose()
    {
        RhinoDoc.AddRhinoObject -= Added; RhinoDoc.DeleteRhinoObject -= Deleted; RhinoDoc.UndeleteRhinoObject -= Added;
        RhinoDoc.ModifyObjectAttributes -= AttributesChanged; RhinoDoc.LayerTableEvent -= LayerChanged; RhinoDoc.HatchPatternTableEvent -= HatchChanged;
        RhinoDoc.InstanceDefinitionTableEvent -= DefinitionChanged; RhinoDoc.MaterialTableEvent -= MaterialChanged;
        RhinoDoc.DocumentPropertiesChanged -= PropertiesChanged; RhinoDoc.CloseDocument -= Closed;
    }

    // ------------------------------------------------------------------ facets

    /// <summary>The parts of an object's state the policy distinguishes. Serial changes when the geometry is replaced.</summary>
    internal readonly record struct ObjectFacets(uint Serial, bool Hidden, int Layer, string Colour, string Section, string Material,
        string Name, string M2D, string Viewport, bool Deleted)
    {
        internal static ObjectFacets Of(RhinoDoc document, ObjectAttributes a, uint serial, bool deleted = false)
        {
            var strings = a.GetUserStrings();
            var m2d = string.Join("\n", strings.AllKeys.Where(k => k is not null && k.StartsWith("m2d.", StringComparison.OrdinalIgnoreCase))
                .Order(StringComparer.Ordinal).Select(k => k + "=" + strings[k]));
            var section = ((int)a.SectionAttributesSource).ToString(CultureInfo.InvariantCulture);
            if (a.SectionAttributesSource == ObjectSectionAttributesSource.FromObject)
            {
                using var style = a.GetCustomSectionStyle();
                section += ":" + SectionKey(style);
            }
            return new(serial, !a.Visible || a.Mode == ObjectMode.Hidden, a.LayerIndex,
                FormattableString.Invariant($"{(int)a.ColorSource}:{a.ObjectColor.ToArgb()}:{(int)a.PlotColorSource}:{a.PlotColor.ToArgb()}:{a.PlotWeight}:{(int)a.PlotWeightSource}"),
                section, FormattableString.Invariant($"{(int)a.MaterialSource}:{a.MaterialIndex}"), a.Name ?? "", m2d,
                FormattableString.Invariant($"{a.ViewportId}:{string.Join(",", a.GetHideInDetailOverrides() ?? [])}"), deleted);
        }

        internal ObjectChange Diff(ObjectFacets now)
        {
            var change = ObjectChange.None;
            if (Serial != now.Serial) change |= ObjectChange.Geometry;
            if (Deleted != now.Deleted) change |= now.Deleted ? ObjectChange.Deleted : ObjectChange.Added;
            if (Hidden != now.Hidden) change |= ObjectChange.Visibility;
            if (Layer != now.Layer) change |= ObjectChange.Layer;
            if (Colour != now.Colour) change |= ObjectChange.Colour;
            if (Section != now.Section) change |= ObjectChange.Section;
            if (Material != now.Material) change |= ObjectChange.Material;
            if (Name != now.Name) change |= ObjectChange.Name;
            if (M2D != now.M2D) change |= ObjectChange.M2DText;
            if (Viewport != now.Viewport) change |= ObjectChange.ViewportVisibility;
            return change;
        }
    }

    internal readonly record struct LayerFacets(bool Visible, string Colour, string Name, string Section, string Print, int Material, bool Deleted)
    {
        /// <param name="own">The layer's own on/off (event states have no parent context); else the effective model visibility.</param>
        internal static LayerFacets Of(Layer layer, bool own, RhinoDoc? document = null)
        {
            var visible = layer.IsVisible;
            if (!own && document is not null)
                for (var parentId = layer.ParentLayerId; visible && parentId != Guid.Empty;)
                {
                    var parent = document.Layers.FindId(parentId);
                    visible = parent?.IsVisible ?? false; parentId = parent?.ParentLayerId ?? Guid.Empty;
                }
            using var style = layer.GetCustomSectionStyle();
            return new(visible, layer.Color.ToArgb().ToString(CultureInfo.InvariantCulture), layer.FullPath + "|" + layer.Name, SectionKey(style),
                FormattableString.Invariant($"{layer.PlotWeight}:{layer.PlotColor.ToArgb()}"), layer.RenderMaterialIndex, layer.IsDeleted);
        }

        internal LayerChange Diff(LayerFacets now)
        {
            var change = LayerChange.None;
            if (Visible != now.Visible) change |= LayerChange.Visibility;
            if (Colour != now.Colour) change |= LayerChange.Colour;
            if (Name != now.Name) change |= LayerChange.Name;
            if (Section != now.Section) change |= LayerChange.Section;
            if (Print != now.Print) change |= LayerChange.Print;
            if (Material != now.Material) change |= LayerChange.Material;
            if (Deleted != now.Deleted) change |= now.Deleted ? LayerChange.Deleted : LayerChange.Added;
            return change;
        }
    }

    private static string SectionKey(SectionStyle? style) => style is null ? "-" : string.Join(":", style.HatchIndex, style.HatchScale.ToString("R", CultureInfo.InvariantCulture),
        style.HatchRotationRadians.ToString("R", CultureInfo.InvariantCulture), style.HatchPatternColor.ToArgb(), style.HatchPatternPrintColor.ToArgb(),
        (int)style.BackgroundFillMode, style.BackgroundFillColor.ToArgb(), style.BackgroundFillPrintColor.ToArgb(), style.BoundaryVisible,
        style.BoundaryWidthScale.ToString("R", CultureInfo.InvariantCulture), style.BoundaryColor.ToArgb(), style.BoundaryPrintColor.ToArgb(), (int)style.SectionFillRule);

    private sealed record Snapshot(IReadOnlyDictionary<Guid, ObjectFacets> Objects, IReadOnlyDictionary<int, LayerFacets> Layers,
        IReadOnlyDictionary<Guid, string> Definitions, string DocumentProperties, string NamedViews, string HatchPatterns)
    {
        /// <summary>Only what changes without an event of its own: tolerance, units and named views.</summary>
        internal static Snapshot Light(RhinoDoc document) => new(new Dictionary<Guid, ObjectFacets>(), new Dictionary<int, LayerFacets>(),
            new Dictionary<Guid, string>(), Properties(document), string.Join("|", document.NamedViews.Select(v => ViewRequest.Parse(v).Revision)), "");

        internal static string Properties(RhinoDoc document) => FormattableString.Invariant(
            $"{document.ModelAbsoluteTolerance:R}:{document.ModelAngleToleranceRadians:R}:{document.ModelUnitSystem}");

        /// <summary>Document mode (or no scope yet): every object, hidden ones included, so a shown object is noticed.
        /// Referenced mode: only the objects the drawing read, the view objects and objects on layer-name layers.</summary>
        internal static Snapshot Take(RhinoDoc document, WatchScope? scope)
        {
            var objects = new Dictionary<Guid, ObjectFacets>();
            void Add(RhinoObject? obj) { if (obj is not null) objects[obj.Id] = ObjectFacets.Of(document, obj.Attributes, obj.RuntimeSerialNumber, obj.IsDeleted); }
            if (scope is null || scope.Mode == WatchMode.Document || scope.LayerPatterns.Count > 0 || scope.TextViews)
            {
                // Model objects only (IdefObjects would return block-definition geometry instead); block parts below.
                var all = document.Objects.GetObjectList(new ObjectEnumeratorSettings
                    { NormalObjects = true, LockedObjects = true, HiddenObjects = true, ReferenceObjects = true, IncludeLights = true });
                foreach (var obj in all)
                    if (scope is null || scope.Mode == WatchMode.Document || scope.Objects.Contains(obj.Id) || scope.Views.Contains(obj.Id)
                        || scope.TextViews && obj is ClippingPlaneObject || scope.MatchesPattern(document, obj.Attributes.LayerIndex))
                        Add(obj);
            }
            else
                foreach (var id in scope.Objects.Concat(scope.Views)) Add(document.Objects.FindId(id));
            var layers = new Dictionary<int, LayerFacets>();
            foreach (var layer in document.Layers) layers[layer.Index] = LayerFacets.Of(layer, own: false, document);
            var definitions = new Dictionary<Guid, string>();
            foreach (var definition in document.InstanceDefinitions)
                if (definition is not null && !definition.IsDeleted)
                {
                    var members = definition.GetObjects();
                    definitions[definition.Id] = string.Join(",", members.Select(o => o.RuntimeSerialNumber));
                    // Parts of drawn blocks: their own visibility, layer, colour and style matter (Gated).
                    if (scope is null || scope.Definitions.Contains(definition.Id)) foreach (var member in members) Add(member);
                }
            var named = string.Join("|", document.NamedViews.Select(v => ViewRequest.Parse(v).Revision));
            var hatches = string.Join("|", document.HatchPatterns.Where(p => !p.IsDeleted).Select(p => p.ToJSON(new global::Rhino.FileIO.SerializationOptions())));
            return new(objects, layers, definitions, Properties(document), named, hatches);
        }
    }
}
