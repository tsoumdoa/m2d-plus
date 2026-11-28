using Rhino.Geometry;

namespace M2DPlus.Rhino.Sections;

/// <summary>One native result, with independent owned curves. No source faces or slab verdict are retained.</summary>
internal sealed class FacePairIntersections(bool success, Curve[] curves, Point3d[] points) : IDisposable
{
    internal bool Success { get; } = success;
    internal Curve[] Curves { get; } = curves;
    internal Point3d[] Points { get; } = points;

    internal FacePairIntersections Copy()
    {
        var copies = new List<Curve>();
        try
        {
            foreach (var curve in Curves)
            {
                var copy = curve.DuplicateCurve(); copies.Add(copy); copy.EnsurePrivateCopy();
            }
            return new(Success, copies.ToArray(), (Point3d[])Points.Clone());
        }
        catch { foreach (var copy in copies) copy.Dispose(); throw; }
    }
    public void Dispose() { foreach (var curve in Curves) curve.Dispose(); }
}

/// <summary>
/// Exact ordered face archives and tolerance. Different local cut planes can reuse
/// the native intersection, but every caller still checks its own slabs, shared topology and boundary guard.
/// Owns private curves for one validation pass; callers always receive independent owned results. Bounded, in-memory only.
/// </summary>
internal sealed class SectionIntersectionCache(long maximumBytes = 8L * 1024 * 1024, int maximumEntries = 4096) : IDisposable
{
    private readonly object _sync = new();
    private readonly Dictionary<string, FacePairIntersections> _pairs = new(StringComparer.Ordinal);
    private bool _disposed;
    internal int Hits { get; private set; }
    internal int Count { get { lock (_sync) return _pairs.Count; } }
    internal long RetainedBytes { get; private set; }

    internal FacePairIntersections Get(Brep first, Brep second, double tolerance, Func<FacePairIntersections> compute)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            string key;
            try
            {
                key = tolerance.ToString("R", System.Globalization.CultureInfo.InvariantCulture) +
                        "|" + Services.GeometryFingerprint.Compute(first) + "|" + Services.GeometryFingerprint.Compute(second);
            }
            catch (Exception ex) when (OptionalFailure(ex)) { return compute(); }
            if (_pairs.TryGetValue(key, out var known))
            {
                try
                {
                    var copy = known.Copy(); Hits++;
                    return copy;
                }
                catch (Exception ex) when (OptionalFailure(ex)) { return compute(); }
            }
            var result = compute();
            try
            {
                // Failed or invalid native results retain their existing per-call handling and are never cached.
                if (result.Success && _pairs.Count < maximumEntries && result.Curves.All(c => c.IsValid) && result.Points.All(p => p.IsValid))
                {
                    var bytes = 128 + key.Length * 2L + result.Points.LongLength * 24 + result.Curves.Sum(c => (long)c.MemoryEstimate());
                    if (bytes <= maximumBytes - RetainedBytes)
                    {
                        FacePairIntersections? retained = null;
                        try
                        {
                            retained = result.Copy();
                            _pairs.Add(key, retained); RetainedBytes += bytes; retained = null;
                        }
                        finally { retained?.Dispose(); }
                    }
                }
            }
            catch (Exception ex) when (OptionalFailure(ex)) { /* Optional retention must not change the native result. */ }
            catch { result.Dispose(); throw; }
            return result;
        }
    }

    // Native computation failures still propagate. Only fingerprinting/copying/retention is optional.
    private static bool OptionalFailure(Exception error) => error is not (OperationCanceledException or
        OutOfMemoryException or StackOverflowException or AccessViolationException or AppDomainUnloadedException or
        System.Threading.ThreadAbortException or System.Runtime.InteropServices.SEHException);

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var pair in _pairs.Values) pair.Dispose();
            _pairs.Clear(); RetainedBytes = 0;
        }
    }
}
