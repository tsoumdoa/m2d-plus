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
    internal OwnedGeometry(GeometryBase geometry, string? conversion)
    { _geometry = geometry; Conversion = conversion; Bytes = (long)geometry.MemoryEstimate(); }
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
    Transform[] Chain, LeaseEntry<OwnedGeometry>.Lease Geometry);
internal sealed record PreparedLeaf(string Identity, int[] Path, GeometryBase Geometry);

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
    internal IReadOnlyDictionary<string, int> Unsupported { get; }
    internal IReadOnlyList<string> Conversions { get; }
    public string Revision { get; }
    public BoundingBox Bounds { get; }
    public double Tolerance { get; }
    public long EstimatedBytes { get; }
    public int SourceCount => _leaves.Count;
    public int PreparationCount { get; private set; }
    internal PreparedScene(string revision, List<SceneLeaf> leaves, Dictionary<string, int> unsupported, double tolerance)
    {
        Revision = revision; _leaves = leaves; Unsupported = new ReadOnlyDictionary<string, int>(unsupported); Tolerance = tolerance;
        Conversions = Array.AsReadOnly(leaves.Where(l => l.Geometry.Value.Conversion is not null)
            .Select(l => $"{l.Identity}: {l.Geometry.Value.Conversion}").ToArray());
        var bounds = BoundingBox.Empty;
        long bytes = 0;
        foreach (var leaf in leaves)
        {
            var transform = Combined(leaf.Chain);
            bounds.Union(leaf.Geometry.Value.Bounds(transform));
            bytes += leaf.Geometry.Value.Bytes;
        }
        Bounds = bounds;
        // Admission estimate, not a hard native allocation cap. Large/complex scenes are refused in M1a.
        EstimatedBytes = checked(bytes * 8 + leaves.Count * 65536L);
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
                    if (!geometry.Transform(Combined(leaf.Chain))) throw new InvalidOperationException("Block transform failed.");
                    // Transform reverses closed-solid orientation for odd reflections. Normalize after the whole chain.
                    if (geometry is Brep { SolidOrientation: BrepSolidOrientation.Inward } brep) brep.Flip();
                    if (geometry is Mesh { IsClosed: true } mesh && mesh.SolidOrientation() < 0) mesh.Flip(true, true, true);
                    prepared.Add(new(leaf.Identity, leaf.Path, geometry));
                }
                catch { geometry.Dispose(); throw; }
            }
            _prepared = prepared; PreparationCount++; Interlocked.Increment(ref _totalPreparations);
            return prepared;
        }
        catch { foreach (var leaf in prepared) leaf.Geometry.Dispose(); throw; }
    }
    public void Dispose()
    {
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
    private long _geometryBytes;
    private long _sceneBytes;
    private const long RetainedBudget = 128L * 1024 * 1024;
    public int GeometryHits { get; private set; }
    public int SceneHits { get; private set; }
    internal static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    internal LeaseEntry<OwnedGeometry>.Lease Geometry(string key, Func<OwnedGeometry> create)
    {
        lock (_sync)
        {
            if (_geometry.TryGetValue(key, out var found)) { GeometryHits++; return found.Acquire(); }
            var value = create();
            var bytes = value.Bytes;
            if (_geometry.Count >= 512 || _geometryBytes + _sceneBytes + bytes > RetainedBudget) Clear();
            var entry = new LeaseEntry<OwnedGeometry>(value); var lease = entry.Acquire();
            if (bytes <= RetainedBudget) { _geometry.Add(key, entry); _geometryBytes += bytes; }
            else entry.Dispose();
            return lease;
        }
    }
    internal LeaseEntry<PreparedScene>.Lease Scene(string key, List<SceneLeaf> leaves, Dictionary<string, int> unsupported, double tolerance)
    {
        lock (_sync)
        {
            if (_scenes.TryGetValue(key, out var found))
            {
                SceneHits++; foreach (var leaf in leaves) leaf.Geometry.Dispose(); return found.Acquire();
            }
            var scene = new PreparedScene(key, leaves, unsupported, tolerance);
            if (_scenes.Count >= 8 || _geometryBytes + _sceneBytes + scene.EstimatedBytes > RetainedBudget) Clear();
            var added = new LeaseEntry<PreparedScene>(scene); var lease = added.Acquire();
            if (scene.EstimatedBytes <= RetainedBudget) { _scenes.Add(key, added); _sceneBytes += scene.EstimatedBytes; }
            else added.Dispose();
            return lease;
        }
    }
    private void ClearGeometry() { foreach (var entry in _geometry.Values) entry.Dispose(); _geometry.Clear(); _geometryBytes = 0; }
    public void Clear()
    {
        lock (_sync)
        {
            foreach (var entry in _scenes.Values) entry.Dispose(); _scenes.Clear(); _sceneBytes = 0; ClearGeometry();
        }
    }
    public void Dispose() => Clear();
}
