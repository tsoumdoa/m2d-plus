using System.Collections.Concurrent;
using M2DPlus.Core.Sections;
using Rhino.Geometry;

namespace M2DPlus.Rhino.Sections;

/// <summary>
/// The definition-local shape behind a source. Sources with the same <see cref="Key"/> are congruent: the same
/// definition content under a rigid transform (mirrors included), or under a similarity with the same scale.
/// <see cref="CopyLocal"/> returns an owned copy of the untransformed definition geometry and is safe to call
/// from any thread. Sources under a non-similar transform have no canonical shape and are never shared.
/// </summary>
internal sealed record SectionCanonical(string Key, double Scale, Func<GeometryBase> CopyLocal);

/// <summary>
/// Self-intersection verdicts per (definition content, scale, tolerance), owned by one prepared scene and cleared
/// with it. A verdict is computed once, on the canonical definition-local geometry, so it never depends on which
/// instance asked first. The section engine asks from one thread, before any parallel work starts.
/// "" means valid; anything else is the failure message.
/// </summary>
internal sealed class SectionVerdictCache : IDisposable
{
    private readonly ConcurrentDictionary<string, Lazy<string>> _verdicts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Lazy<string>> _localVerdicts = new(StringComparer.Ordinal);
    private int _computations;
    private int _localComputations;
    private volatile bool _disposed;
    internal int Count => _verdicts.Count;
    internal int Computations => Volatile.Read(ref _computations);
    internal int LocalComputations => Volatile.Read(ref _localComputations);
    internal bool IsDisposed => _disposed;

    internal string Verdict(SectionCanonical canonical, double tolerance)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SectionVerdictCache));
        var key = canonical.Key + "|tolerance=" + tolerance.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        return _verdicts.GetOrAdd(key, _ => new Lazy<string>(() =>
        {
            Interlocked.Increment(ref _computations);
            return SectionGeometry.CanonicalVerdict(canonical, tolerance, this);
        }, LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    // Exact complete archives only: translated copies of an exploded building element
    // can share validation without treating near-equal shapes as congruent.
    internal string LocalVerdict(Brep local, double tolerance, Func<string> compute)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SectionVerdictCache));
        var key = Services.GeometryFingerprint.Compute(local) + "|tolerance=" + tolerance.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        return _localVerdicts.GetOrAdd(key, _ => new Lazy<string>(() =>
        {
            Interlocked.Increment(ref _localComputations);
            return compute();
        }, LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    public void Dispose() { _disposed = true; _verdicts.Clear(); _localVerdicts.Clear(); }
}

/// <summary>
/// One conversion, component split and near-plane classification per source, shared by the visibility clip and
/// the region pass. Used by one thread at a time; owns everything it hands out except classification evidence copies.
/// </summary>
internal sealed class SharedSource : IDisposable
{
    internal SectionSource Source { get; }
    private readonly SectionRequest _request;
    private readonly string? _verdict;
    private Brep? _copy; private List<Brep>? _components; private Exception? _failure; private bool _built;
    private readonly Dictionary<int, (ComponentClassification Classification, StableSectionCertificate.Evidence? Evidence)> _near = new();
    /// <param name="verdict">The source's settled validation verdict ("" is valid); null validates its own geometry here.</param>
    internal SharedSource(SectionSource source, SectionRequest request, string? verdict)
    { Source = source; _request = request; _verdict = verdict; }

    /// <summary>Borrowed components. A conversion or validation failure is remembered and rethrown to every caller.</summary>
    internal IReadOnlyList<Brep> Components()
    {
        if (!_built)
        {
            _built = true;
            try { Build(); }
            catch (Exception ex) when (ex is SectionFailure or ArgumentException or InvalidOperationException) { _failure = ex; }
        }
        if (_failure is not null) throw _failure;
        return _components!;
    }

    private void Build()
    {
        var tolerance = _request.Tolerance;
        var copy = SectionGeometry.Convert(Source.Geometry, tolerance, validate: false);
        try
        {
            if (_verdict is null) SectionGeometry.ValidateFaceIntersections(copy, tolerance);
            else if (_verdict.Length != 0) throw new SectionFailure(_verdict);
            _components = SectionGeometry.MaterialComponents(copy, tolerance);
            _copy = copy;
        }
        catch { copy.Dispose(); throw; }
    }

    internal ComponentClassification ClassifyNear(string id, int index, Brep component, out StableSectionCertificate.Evidence? evidence)
    {
        if (!_near.TryGetValue(index, out var known))
        {
            var classification = SectionGeometry.Classify(id, index, component, _request, out var original);
            _near[index] = known = (classification, original);
        }
        evidence = known.Evidence is null ? null : new(known.Evidence.Loops.Select(l => l.DuplicateCurve()).ToArray());
        return known.Classification with { SourceId = id, Component = index };
    }

    public void Dispose()
    {
        foreach (var entry in _near.Values) entry.Evidence?.Dispose();
        if (_components is not null) foreach (var component in _components) component.Dispose();
        _copy?.Dispose();
    }
}
