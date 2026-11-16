using System.Drawing;
using M2DPlus.Core.Placement;
using M2DPlus.Core.Views;
using Rhino;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace M2DPlus.Rhino.Views;

/// <summary>One view box's front face as the display conduit draws it: the frame, its fill, and whether the view box
/// itself is visible (not hidden, layer and parent layers on).</summary>
public sealed record ViewBoxFace(Guid Id, string Name, Rectangle3d Frame, int LayerIndex, bool Visible, FillChoice Fill)
{
    public Color Colour => Color.FromArgb(Fill.Colour.A, Fill.Colour.R, Fill.Colour.G, Fill.Colour.B);
}

/// <summary>
/// The front-face fills of one document's view boxes (owner decision 2026-11-15, UX §2.2), kept up to date from
/// document events and recomputed only by <see cref="Refresh"/>, never per frame. It holds an <see cref="RTree"/> of
/// the bounding boxes of the scene objects the cut hint considers, and per view box the frame, the cached cut hint
/// (<see cref="CutHint"/>) and the resolved fill. Events only record what changed; <see cref="Refresh"/> applies it
/// incrementally (a moved object re-tests only the view boxes near its old and new bounds). A layer, block-definition,
/// tolerance or document change rebuilds everything. Host (UI) thread only; no geometry is drawn here.
/// </summary>
public sealed class ViewBoxFaceIndex : IDisposable
{
    private sealed class Box
    {
        public required ViewBoxFace Face;
        public required FrontFace Front;
        public required BoundingBox Search;
        public string? ColourText;
        public bool HintDirty = true;
        public Mesh? Mesh;
    }

    private readonly uint _serial;
    private readonly RTree _tree = new();
    private readonly Dictionary<Guid, (int Key, BoundingBox Box)> _scene = new();
    private readonly Dictionary<int, BoundingBox> _byKey = new();
    private readonly Dictionary<Guid, Box> _boxes = new();
    private readonly HashSet<Guid> _pending = new();
    private readonly Dictionary<int, (bool Scene, bool Visible)> _layers = new();
    private bool _full = true, _disposed;
    private int _nextKey;
    private double _tolerance = double.NaN;

    /// <summary>More than this many changed bounds in one refresh re-test every view box instead of the nearby ones.</summary>
    private const int NearbyLimit = 256;

    public ViewBoxFaceIndex(RhinoDoc document)
    {
        _serial = document.RuntimeSerialNumber;
        RhinoDoc.AddRhinoObject += ObjectEvent; RhinoDoc.DeleteRhinoObject += ObjectEvent; RhinoDoc.UndeleteRhinoObject += ObjectEvent;
        RhinoDoc.ReplaceRhinoObject += Replaced; RhinoDoc.ModifyObjectAttributes += Modified;
        RhinoDoc.LayerTableEvent += LayerEvent; RhinoDoc.InstanceDefinitionTableEvent += DefinitionEvent;
        RhinoDoc.DocumentPropertiesChanged += DocumentEvent;
    }

    public uint DocumentSerial => _serial;

    /// <summary>Raised (host thread) when a document event made the index dirty, so a caller can debounce a refresh.</summary>
    public event Action? Changed;

    /// <summary>Something changed since the last <see cref="Refresh"/>.</summary>
    public bool IsDirty => _full || _pending.Count > 0;

    /// <summary>Counters, for tests and diagnostics: full rebuilds and cut-hint evaluations (RTree searches) so far.</summary>
    public int Rebuilds { get; private set; }
    public int HintEvaluations { get; private set; }

    /// <summary>Scene objects in the RTree.</summary>
    public int SceneCount => _scene.Count;

    /// <summary>Every view box of the document as of the last <see cref="Refresh"/>.</summary>
    public IEnumerable<ViewBoxFace> Faces => _boxes.Values.Select(b => b.Face);

    public ViewBoxFace? Find(Guid id) => _boxes.TryGetValue(id, out var box) ? box.Face : null;

    /// <summary>The bounds of every visible front face (for the conduit's bounding box).</summary>
    public BoundingBox Bounds
    {
        get
        {
            var bounds = BoundingBox.Empty;
            foreach (var box in _boxes.Values) if (box.Face.Visible) bounds.Union(box.Face.Frame.BoundingBox);
            return bounds;
        }
    }

    /// <summary>Applies the recorded changes and recomputes the dirty cut hints and fills. Cheap when nothing changed.</summary>
    public void Refresh()
    {
        if (_disposed) return;
        var document = RhinoDoc.FromRuntimeSerialNumber(_serial);
        if (document is null) { Clear(); _pending.Clear(); _full = false; return; }
        var tolerance = document.ModelAbsoluteTolerance;
        if (_full || tolerance != _tolerance) Rebuild(document, tolerance);
        else if (_pending.Count > 0)
        {
            var changed = new List<BoundingBox>();
            foreach (var id in _pending) Update(document, id, changed);
            if (changed.Count > NearbyLimit) foreach (var box in _boxes.Values) box.HintDirty = true;
            else if (changed.Count > 0)
                foreach (var box in _boxes.Values)
                    if (!box.HintDirty && changed.Any(c => Intersects(c, box.Search))) box.HintDirty = true;
        }
        _pending.Clear(); _full = false;
        foreach (var box in _boxes.Values.Where(b => b.HintDirty)) Resolve(box);
    }

    /// <summary>The front-face mesh of a view box (one quad, cached until the box changes).</summary>
    public Mesh? MeshOf(Guid id)
    {
        if (!_boxes.TryGetValue(id, out var box)) return null;
        if (box.Mesh is null)
        {
            var frame = box.Face.Frame; var mesh = new Mesh();
            for (var i = 0; i < 4; i++) mesh.Vertices.Add(frame.Corner(i));
            mesh.Faces.AddFace(0, 1, 2, 3); mesh.Normals.ComputeNormals();
            box.Mesh = mesh;
        }
        return box.Mesh;
    }

    // ------------------------------------------------------------------ building

    private void Rebuild(RhinoDoc document, double tolerance)
    {
        Clear();
        _tolerance = tolerance; Rebuilds++;
        var settings = new ObjectEnumeratorSettings { NormalObjects = true, LockedObjects = true, HiddenObjects = true,
            ReferenceObjects = true, IncludeLights = false, IncludeGrips = false, IncludePhantoms = false };
        foreach (var obj in document.Objects.GetObjectList(settings)) Add(document, obj, null);
    }

    private void Clear()
    {
        _tree.Clear(); _scene.Clear(); _byKey.Clear(); _layers.Clear(); _nextKey = 0;
        foreach (var box in _boxes.Values) box.Mesh?.Dispose();
        _boxes.Clear();
    }

    private void Update(RhinoDoc document, Guid id, List<BoundingBox> changed)
    {
        if (_scene.Remove(id, out var old)) { _tree.Remove(old.Box, old.Key); _byKey.Remove(old.Key); changed.Add(old.Box); }
        if (_boxes.Remove(id, out var gone)) gone.Mesh?.Dispose();
        if (document.Objects.FindId(id) is { IsDeleted: false } obj) Add(document, obj, changed);
    }

    private void Add(RhinoDoc document, RhinoObject obj, List<BoundingBox>? changed)
    {
        if (obj.IsDeleted || obj.IsInstanceDefinitionGeometry) return;
        if (obj.Attributes.UserStringCount > 0 && ViewBoxes.IsViewBox(obj)) { AddBox(document, obj); return; }
        if (!IsSceneObject(document, obj)) return;
        var bounds = obj.Geometry.GetBoundingBox(false);
        if (!bounds.IsValid) return;
        var key = _nextKey++;
        _tree.Insert(bounds, key); _byKey[key] = bounds; _scene[obj.Id] = (key, bounds);
        changed?.Add(bounds);
    }

    private void AddBox(RhinoDoc document, RhinoObject obj)
    {
        if (obj.Geometry is not Extrusion extrusion) return;
        var check = ViewBoxes.ReadBox(extrusion, document.ModelAbsoluteTolerance, document.ModelAngleToleranceRadians);
        if (!check.IsValid) return;
        var frame = check.Frame!.Value;
        var front = Front(frame);
        var search = CutHint.SearchBounds(front, document.ModelAbsoluteTolerance);
        var visible = !obj.IsHidden && Layer(document, obj.Attributes.LayerIndex).Visible;
        var text = obj.Attributes.GetUserString(ViewBoxes.ColorKey);
        var name = ViewBoxes.Label(obj);
        _boxes[obj.Id] = new Box
        {
            Face = new(obj.Id, name, frame, obj.Attributes.LayerIndex, visible, ViewBoxFill.Choose(false, text, name)),
            Front = front, Search = new BoundingBox(Point(search.Min), Point(search.Max)), ColourText = text,
        };
    }

    private void Resolve(Box box)
    {
        var cuts = false;
        HintEvaluations++;
        _tree.Search(box.Search, (_, e) =>
        {
            if (_byKey.TryGetValue(e.Id, out var bounds) && CutHint.Cuts(box.Front, ToBounds(bounds), _tolerance)) { cuts = true; e.Cancel = true; }
        });
        box.Face = box.Face with { Fill = ViewBoxFill.Choose(cuts, box.ColourText, box.Face.Name) };
        box.HintDirty = false;
    }

    /// <summary>The objects the cut hint considers: what document mode would draw (as <c>SceneCapture.IsDrawable</c>:
    /// not hidden, layer and parents on, not on an <c>M2D+</c> bake layer, no <c>m2d.*</c> user text) and that can be
    /// cut: surfaces, polysurfaces, extrusions, SubDs, meshes and block instances. Curves, points and annotations are
    /// not cut by the component, so they never make a face red.</summary>
    private bool IsSceneObject(RhinoDoc document, RhinoObject obj)
    {
        if (obj.IsHidden || obj.Geometry is not (Brep or Extrusion or SubD or Mesh or Surface or InstanceReferenceGeometry)) return false;
        if (!Layer(document, obj.Attributes.LayerIndex).Scene) return false;
        if (obj.Attributes.UserStringCount > 0 && HasM2dKey(obj.Attributes.GetUserStrings())) return false;
        return obj.Geometry.UserStringCount == 0 || !HasM2dKey(obj.Geometry.GetUserStrings());
    }

    private static bool HasM2dKey(System.Collections.Specialized.NameValueCollection strings) =>
        strings.AllKeys.Any(k => k is not null && k.StartsWith(ViewBoxes.Prefix, StringComparison.OrdinalIgnoreCase));

    /// <summary>Per layer, cached until the next rebuild (layer events rebuild): visible with all parents, and
    /// whether its objects are scene (also not on an <c>M2D+</c> bake layer path).</summary>
    private (bool Scene, bool Visible) Layer(RhinoDoc document, int index)
    {
        if (_layers.TryGetValue(index, out var known)) return known;
        var layer = index >= 0 && index < document.Layers.Count ? document.Layers[index] : null;
        var visible = layer is { IsDeleted: false, IsVisible: true };
        for (var parentId = layer?.ParentLayerId ?? Guid.Empty; visible && parentId != Guid.Empty;)
        {
            var parent = document.Layers.FindId(parentId);
            visible = parent is { IsVisible: true };
            parentId = parent?.ParentLayerId ?? Guid.Empty;
        }
        var scene = visible && !layer!.FullPath.Split("::").Contains("M2D+");
        return _layers[index] = (scene, visible);
    }

    private static FrontFace Front(Rectangle3d frame) => ViewDepthScene.Front(frame);

    private static Bounds3 ToBounds(BoundingBox b) => new(new(b.Min.X, b.Min.Y, b.Min.Z), new(b.Max.X, b.Max.Y, b.Max.Z));
    private static Point3d Point(Vector3 v) => new(v.X, v.Y, v.Z);
    private static bool Intersects(BoundingBox a, BoundingBox b) =>
        a.Min.X <= b.Max.X && a.Max.X >= b.Min.X && a.Min.Y <= b.Max.Y && a.Max.Y >= b.Min.Y && a.Min.Z <= b.Max.Z && a.Max.Z >= b.Min.Z;

    // ------------------------------------------------------------------ events (record only; never compute here)

    /// <summary>Records that one object changed (what the object events do). Public because headless documents raise
    /// no object events: callers and tests that edit a headless document report their edits here.</summary>
    public void ObjectChanged(Guid id) => Mark(null, id, false);

    /// <summary>Records that everything must be rebuilt (what layer, block-definition and document events do).</summary>
    public void Invalidate() => MarkAll(null, false);

    /// <summary>How many Rhino document events for this document reached the index (diagnostics and tests).</summary>
    public int EventsSeen { get; private set; }

    private void Mark(RhinoDoc? document, Guid id, bool fromEvent = true)
    {
        // A deleted object's document wrapper may be gone: record it anyway (re-reading an unknown id is harmless).
        if (_disposed || document is not null && document.RuntimeSerialNumber != _serial) return;
        if (fromEvent) EventsSeen++;
        if (!_full) _pending.Add(id);
        Changed?.Invoke();
    }
    private void MarkAll(RhinoDoc? document, bool fromEvent = true)
    {
        if (_disposed || document is not null && document.RuntimeSerialNumber != _serial) return;
        if (fromEvent) EventsSeen++;
        _full = true; _pending.Clear();
        Changed?.Invoke();
    }
    private void ObjectEvent(object? sender, RhinoObjectEventArgs e) => Mark(e.TheObject.Document, e.ObjectId);
    private void Replaced(object? sender, RhinoReplaceObjectEventArgs e) => Mark(e.Document, e.ObjectId);
    private void Modified(object? sender, RhinoModifyObjectAttributesEventArgs e) => Mark(e.Document, e.RhinoObject.Id);
    private void LayerEvent(object? sender, global::Rhino.DocObjects.Tables.LayerTableEventArgs e) { if (e.Document is { } d) MarkAll(d); }
    private void DefinitionEvent(object? sender, global::Rhino.DocObjects.Tables.InstanceDefinitionTableEventArgs e) { if (e.Document is { } d) MarkAll(d); }
    private void DocumentEvent(object? sender, DocumentEventArgs e) { if (e.Document is { } d) MarkAll(d); }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        RhinoDoc.AddRhinoObject -= ObjectEvent; RhinoDoc.DeleteRhinoObject -= ObjectEvent; RhinoDoc.UndeleteRhinoObject -= ObjectEvent;
        RhinoDoc.ReplaceRhinoObject -= Replaced; RhinoDoc.ModifyObjectAttributes -= Modified;
        RhinoDoc.LayerTableEvent -= LayerEvent; RhinoDoc.InstanceDefinitionTableEvent -= DefinitionEvent;
        RhinoDoc.DocumentPropertiesChanged -= DocumentEvent;
        Clear(); _tree.Dispose();
    }
}

/// <summary>
/// Draws the see-through front-face fill of every visible view box in the active document, in every display mode
/// (owner decision 2026-11-15). Registered once when the GHA loads (<see cref="Install"/>); not in headless Rhino.
/// The fill is an unlit transparent quad drawn after the objects without writing depth, so it tints but never hides
/// the model's wires, and conduits take no part in picking, so selection is unchanged. Rhino's own selection
/// highlight and the component's preview are drawn as before. The fills are recomputed only after document changes,
/// debounced (<see cref="Debounce"/>), through <see cref="ViewBoxFaceIndex"/>.
/// </summary>
public sealed class ViewBoxFaceConduit : DisplayConduit
{
    public static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(200);
    private static ViewBoxFaceConduit? s_instance;
    private ViewBoxFaceIndex? _index;
    private DateTime _lastChange;
    private bool _scheduled;
    private readonly Dictionary<int, DisplayMaterial> _materials = new();

    /// <summary>The installed conduit, if any.</summary>
    public static ViewBoxFaceConduit? Instance => s_instance;

    /// <summary>The active document's face index (null before the first document or when headless).</summary>
    public ViewBoxFaceIndex? Index => _index;

    /// <summary>Enables the conduit once per process. Returns false (and does nothing) in headless Rhino, where there
    /// is no display; workers and Rhino.Inside tests use <see cref="ViewBoxFaceIndex"/> directly.</summary>
    public static bool Install()
    {
        if (s_instance is not null) return true;
        if (RhinoApp.IsRunningHeadless) return false;
        var conduit = new ViewBoxFaceConduit();
        RhinoDoc.ActiveDocumentChanged += conduit.DocumentChanged; RhinoDoc.EndOpenDocument += conduit.DocumentChanged;
        RhinoDoc.NewDocument += conduit.DocumentChanged; RhinoDoc.CloseDocument += conduit.DocumentClosed;
        s_instance = conduit;
        conduit.Attach(RhinoDoc.ActiveDoc);
        conduit.Enabled = true;
        return true;
    }

    private void DocumentChanged(object? sender, DocumentEventArgs e) => Attach(RhinoDoc.ActiveDoc);
    private void DocumentClosed(object? sender, DocumentEventArgs e)
    {
        if (_index is not null && e.Document?.RuntimeSerialNumber == _index.DocumentSerial) { _index.Dispose(); _index = null; }
    }

    private void Attach(RhinoDoc? document)
    {
        if (document is null || _index?.DocumentSerial == document.RuntimeSerialNumber) return;
        _index?.Dispose();
        _index = new ViewBoxFaceIndex(document);
        _index.Changed += Touched;
        Touched();
    }

    // Debounce: every change pushes the deadline back; one refresh runs on the UI thread once changes stop.
    private void Touched()
    {
        _lastChange = DateTime.UtcNow;
        if (_scheduled) return;
        _scheduled = true;
        Schedule(Debounce);
    }

    private void Schedule(TimeSpan delay) =>
        Task.Delay(delay).ContinueWith(_ => RhinoApp.InvokeOnUiThread(new Action(Flush)), TaskScheduler.Default);

    private void Flush()
    {
        var wait = _lastChange + Debounce - DateTime.UtcNow;
        if (wait > TimeSpan.FromMilliseconds(5)) { Schedule(wait); return; }
        _scheduled = false;
        if (_index is not { IsDirty: true } index) return;
        try { index.Refresh(); }
        catch (Exception e) { RhinoApp.WriteLine($"M2D+ view box faces: {e.Message}"); return; }
        RhinoDoc.FromRuntimeSerialNumber(index.DocumentSerial)?.Views.Redraw();
    }

    protected override void CalculateBoundingBox(CalculateBoundingBoxEventArgs e)
    {
        if (_index is { } index && index.Bounds is { IsValid: true } bounds) e.IncludeBoundingBox(bounds);
    }

    protected override void PostDrawObjects(DrawEventArgs e)
    {
        if (_index is not { } index || e.RhinoDoc?.RuntimeSerialNumber != index.DocumentSerial) return;
        var viewportId = e.Viewport.Id;
        var drawn = false;
        foreach (var face in index.Faces)
        {
            if (!face.Visible) continue;
            var layer = face.LayerIndex >= 0 && face.LayerIndex < e.RhinoDoc.Layers.Count ? e.RhinoDoc.Layers[face.LayerIndex] : null;
            if (layer is not null && !layer.PerViewportIsVisible(viewportId)) continue;
            if (index.MeshOf(face.Id) is not { } mesh) continue;
            if (!drawn) { e.Display.EnableLighting(false); e.Display.PushDepthWriting(false); drawn = true; }
            e.Display.DrawMeshShaded(mesh, Material(face.Colour));
        }
        if (drawn) { e.Display.PopDepthWriting(); e.Display.EnableLighting(true); }
    }

    private DisplayMaterial Material(Color colour)
    {
        var key = colour.ToArgb();
        if (_materials.TryGetValue(key, out var material)) return material;
        var solid = Color.FromArgb(colour.R, colour.G, colour.B);
        var transparency = 1 - colour.A / 255d;
        material = new DisplayMaterial(solid, transparency)
        {
            BackDiffuse = solid, BackTransparency = transparency, Specular = Color.Black, BackSpecular = Color.Black,
            Emission = Color.Black, BackEmission = Color.Black, Shine = 0, BackShine = 0,
        };
        return _materials[key] = material;
    }
}
