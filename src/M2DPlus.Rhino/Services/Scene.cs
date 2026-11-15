using System.Security.Cryptography;
using System.Text;
using System.Collections.ObjectModel;
using M2DPlus.Core;
using Rhino.Geometry;

namespace M2DPlus.Rhino.Services;

internal sealed class OwnedGeometry : IDisposable
{
    private readonly GeometryBase _geometry;
    private readonly object _sync = new();
    internal long Bytes { get; }
    internal string? Conversion { get; }
    internal long BrepFaces { get; }
    internal long MeshTriangles { get; }
    internal OwnedGeometry(GeometryBase geometry, string? conversion)
    {
        _geometry = geometry; Conversion = conversion; Bytes = (long)geometry.MemoryEstimate();
        BrepFaces = geometry is Brep brep ? brep.Faces.Count : 0;
        MeshTriangles = geometry is Mesh mesh ? mesh.Faces.TriangleCount + 2L * mesh.Faces.QuadCount : 0;
    }
    internal GeometryBase Copy()
    {
        lock (_sync)
        {
            var copy = _geometry.Duplicate();
            try { copy.EnsurePrivateCopy(); return copy; }
            catch { copy.Dispose(); throw; }
        }
    }
    internal BoundingBox Bounds(Transform transform) { lock (_sync) return _geometry.GetBoundingBox(transform); }
    public void Dispose() => _geometry.Dispose();
}

internal sealed record SceneLeaf(string Identity, string Key, int[] Path,
    Transform[] Chain, LeaseEntry<OwnedGeometry>.Lease Geometry, SourceSection Section)
{ internal int SourceItem { get; init; } }
internal sealed record PreparedLeaf(string Identity, int[] Path, GeometryBase Geometry)
{
    internal GeometryBase? KernelClassifiedGeometry { get; init; }
    internal double KernelTolerance { get; init; }
    internal bool KernelEligible { get; init; }
    internal int SourceItem { get; init; }
    internal SourceSection Section { get; init; } = SourceSection.Plain;
    /// <summary>Definition-local shape for work shared between congruent instances; null under a non-similar transform.</summary>
    internal Sections.SectionCanonical? Canonical { get; init; }
}

/// <summary>Owns definition-local leases and lazily prepared world copies. Access serialized by the global scheduler.</summary>
public sealed class PreparedScene : IDisposable
{
    /// <summary>Unsupported-type keys with this prefix are reported in Info but can't occlude, so they never quarantine a view.</summary>
    internal const string NonOccludingPrefix = "Not drawn (non-occluding) ";
    internal bool HasPotentialOccluderUnsupported => Unsupported.Keys.Any(k => !k.StartsWith(NonOccludingPrefix, StringComparison.Ordinal));
    private static long _totalPreparations;
    public static long TotalPreparations => Interlocked.Read(ref _totalPreparations);
    private readonly List<SceneLeaf> _leaves;
    private List<PreparedLeaf>? _prepared;
    /// <summary>Section validation verdicts, reused by every view of this scene and cleared with it.</summary>
    internal Sections.SectionVerdictCache SectionVerdicts { get; } = new();
    internal IReadOnlyDictionary<string, int> Unsupported { get; }
    internal IReadOnlyList<string> Conversions { get; }
    public string Revision { get; }
    public BoundingBox Bounds { get; }
    public double Tolerance { get; }
    public long EstimatedBytes { get; }
    public SceneCost Cost { get; }
    public int SourceCount => _leaves.Count;
    public int PreparationCount { get; private set; }
    public string VisibilityRevision { get; }
    internal IReadOnlyDictionary<string, SourceSection> SourceMetadata { get; }
    internal PreparedScene(string revision, List<SceneLeaf> leaves, Dictionary<string, int> unsupported, double tolerance)
    {
        SourceMetadata = new ReadOnlyDictionary<string, SourceSection>(leaves.ToDictionary(l => l.Identity, l => l.Section, StringComparer.Ordinal));
        VisibilityRevision = SceneCache.Hash(System.Text.Json.JsonSerializer.Serialize(new {
            leaves = leaves.Select(l => new { l.Identity, l.Key, l.Path, l.SourceItem,
                chain = l.Chain.Select(t => new[] { t.M00,t.M01,t.M02,t.M03,t.M10,t.M11,t.M12,t.M13,t.M20,t.M21,t.M22,t.M23,t.M30,t.M31,t.M32,t.M33 }),
                l.Section.Transparent, l.Section.ObjectIds, l.Section.LayerIds }),
            unsupported = unsupported.OrderBy(p => p.Key, StringComparer.Ordinal), tolerance }));
        Revision = revision; _leaves = leaves; Unsupported = new ReadOnlyDictionary<string, int>(unsupported); Tolerance = tolerance;
        Conversions = Array.AsReadOnly(leaves.Where(l => l.Geometry.Value.Conversion is not null)
            .Select(l => $"{l.Identity}: {l.Geometry.Value.Conversion}")
            .Concat(leaves.Where(l => Combined(l.Chain).SimilarityType == TransformSimilarityType.NotSimilarity)
                .Select(l => $"{l.Identity}: nonuniform transform; deformable geometry (curves to NURBS where required)")).ToArray());
        var bounds = BoundingBox.Empty;
        long bytes = 0, faces = 0, triangles = 0;
        foreach (var leaf in leaves)
        {
            var transform = Combined(leaf.Chain);
            bounds.Union(leaf.Geometry.Value.Bounds(transform));
            bytes += leaf.Geometry.Value.Bytes;
            faces += leaf.Geometry.Value.BrepFaces; triangles += leaf.Geometry.Value.MeshTriangles;
        }
        Bounds = bounds;
        Cost = new(bytes, faces, triangles, leaves.Count);
        EstimatedBytes = Cost.Estimate(1);
    }
    internal static Transform Combined(IEnumerable<Transform> chain)
    {
        var result = Transform.Identity;
        foreach (var transform in chain) result *= transform;
        return result;
    }
    internal IReadOnlyList<PreparedLeaf> Prepare(CancellationToken token)
    {
        if (_prepared is not null) return _prepared;
        var prepared = new List<PreparedLeaf>();
        try
        {
            foreach (var leaf in _leaves)
            {
                token.ThrowIfCancellationRequested();
                var geometry = leaf.Geometry.Value.Copy();
                try
                {
                    geometry.EnsurePrivateCopy();
                    var transform = Combined(leaf.Chain);
                    if (transform.SimilarityType == TransformSimilarityType.NotSimilarity && !geometry.IsDeformable)
                    {
                        if (geometry is Curve curve)
                        { var deformable = curve.ToNurbsCurve(); geometry.Dispose(); geometry = deformable; }
                        else if (!geometry.MakeDeformable()) throw new InvalidOperationException($"Nonuniform block transform unsupported for {geometry.GetType().Name} ({leaf.Identity}).");
                    }
                    if (!geometry.Transform(transform)) throw new InvalidOperationException($"Block transform failed ({leaf.Identity}); geometry was not drawn.");
                    // Transform reverses closed-solid orientation for odd reflections. Normalize after the whole chain.
                    if (geometry is Brep { SolidOrientation: BrepSolidOrientation.Inward } brep) brep.Flip();
                    if (geometry is Mesh { IsClosed: true } mesh && mesh.SolidOrientation() < 0) mesh.Flip(true, true, true);
                    prepared.Add(new(leaf.Identity, leaf.Path, geometry) { Section = leaf.Section, SourceItem = leaf.SourceItem, KernelClassifiedGeometry = geometry, KernelTolerance = Tolerance, KernelEligible = PlanarExtraction.MayProduceKernelCandidates(geometry,Tolerance), Canonical = Canonical(leaf, transform) });
                }
                catch { geometry.Dispose(); throw; }
            }
            _prepared = prepared; PreparationCount++; Interlocked.Increment(ref _totalPreparations);
            return prepared;
        }
        catch { foreach (var leaf in prepared) leaf.Geometry.Dispose(); throw; }
    }
    /// <summary>
    /// Rigid transforms (mirrors included) share the definition's key; a similarity adds its scale; anything else
    /// shares nothing. The scale is rounded to 12 significant digits, so the key and the canonical shape depend only
    /// on the definition and that number, never on a particular instance's rotation round-off.
    /// </summary>
    internal static Sections.SectionCanonical? Canonical(SceneLeaf leaf, Transform transform)
    {
        if (transform.SimilarityType == TransformSimilarityType.NotSimilarity) return null;
        var invariant = System.Globalization.CultureInfo.InvariantCulture;
        var scale = double.Parse(Math.Cbrt(Math.Abs(transform.Determinant)).ToString("G12", invariant), invariant);
        if (!double.IsFinite(scale) || scale <= 0) return null;
        var definition = leaf.Geometry.Value;
        return scale == 1 ? new(leaf.Key, 1, definition.Copy)
            : new(leaf.Key + "|scale=" + scale.ToString("R", invariant), scale, definition.Copy);
    }
    public void Dispose()
    {
        SectionVerdicts.Dispose();
        if (_prepared is not null) foreach (var leaf in _prepared) leaf.Geometry.Dispose();
        foreach (var leaf in _leaves) leaf.Geometry.Dispose();
    }
}

/// <summary>Bounded process cache; clear/eviction retires owners, never live jobs' leases.</summary>
public sealed class SceneCache : IDisposable
{
    public static SceneCache Shared { get; } = new();
    private readonly Dictionary<string, LeaseEntry<OwnedGeometry>> _geometry = new();
    private readonly Dictionary<string, LeaseEntry<PreparedScene>> _scenes = new();
    private readonly object _sync = new();
    private readonly ByteLru<string> _retention;
    public SceneCache(long retainedBudget = 128L * 1024 * 1024) => _retention = new(retainedBudget);
    public long RetainedBytes { get { lock (_sync) return _retention.RetainedBytes; } }
    public int GeometryHits { get; private set; }
    public int SceneHits { get; private set; }
    internal static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    internal LeaseEntry<OwnedGeometry>.Lease Geometry(string key, Func<OwnedGeometry> create)
    {
        lock (_sync)
        {
            if (_geometry.TryGetValue(key, out var found)) { GeometryHits++; _retention.Touch("g/" + key); return found.Acquire(); }
            var value = create();
            var bytes = value.Bytes;
            var entry = new LeaseEntry<OwnedGeometry>(value); var lease = entry.Acquire();
            _geometry.Add(key, entry);
            _retention.Add("g/" + key, bytes, () => { _geometry.Remove(key); entry.Dispose(); });
            return lease;
        }
    }
    internal LeaseEntry<PreparedScene>.Lease Scene(string key, List<SceneLeaf> leaves, Dictionary<string, int> unsupported, double tolerance)
    {
        lock (_sync)
        {
            if (_scenes.TryGetValue(key, out var found))
            {
                SceneHits++; _retention.Touch("s/" + key); foreach (var leaf in leaves) leaf.Geometry.Dispose(); return found.Acquire();
            }
            var scene = new PreparedScene(key, leaves, unsupported, tolerance);
            var added = new LeaseEntry<PreparedScene>(scene); var lease = added.Acquire();
            _scenes.Add(key, added);
            _retention.Add("s/" + key, scene.EstimatedBytes, () => { _scenes.Remove(key); added.Dispose(); });
            return lease;
        }
    }
    public void Clear()
    {
        lock (_sync)
        {
            _retention.Dispose();
        }
    }
    public void Dispose() => Clear();
}
