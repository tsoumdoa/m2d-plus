using System.Diagnostics;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;

namespace M2DPlus.Rhino.Sections;

/// <summary>Positive overlap evidence at the same expanded boundaries used for exclusion.
/// Empty native sections do not certify surface separation or native completeness.</summary>
internal class SlabOverlapGuard : IDisposable
{
    private readonly Plane[] _planes;
    private readonly double _tolerance;
    private readonly Dictionary<(Brep Brep, int Face, int Plane), FaceSection> _sections = new();
    internal SlabOverlapGuard(SectionValidation? validation, BoundingBox parent, double tolerance)
    {
        _planes = validation?.Domains(parent).SelectMany(s => new[] { s.Boundary(-1), s.Boundary(1) }).ToArray() ?? [];
        _tolerance = tolerance;
    }
    internal sealed record FaceSection(bool Success, Curve[] Curves, Point3d[] Points);

    // Virtual methods are internal pair-fixture seams, never application settings.
    internal virtual FaceSection Section(BrepFace face, Plane plane)
    {
        using var trimmed = face.DuplicateFace(false);
        var success = Intersection.BrepPlane(trimmed, plane, _tolerance, out var curves, out var points);
        return new(success, curves, points);
    }
    internal virtual Curve? Extract(Curve curve, Interval interval, Point3d start, Point3d end, Curve other)
    {
        if (!interval.IsValid || !curve.Domain.IncludesParameter(interval.T0) || !curve.Domain.IncludesParameter(interval.T1)) return null;
        var low = Math.Min(interval.T0, interval.T1);
        var high = Math.Max(interval.T0, interval.T1);
        var candidates = new List<Curve>();
        Curve? selected = null;
        try
        {
            if (high > low && curve.Trim(low, high) is { } direct) candidates.Add(direct);
            if (curve.IsClosed)
            {
                if (high == low)
                {
                    var full = curve.DuplicateCurve();
                    if (low == curve.Domain.T0 || full.ChangeClosedCurveSeam(low)) candidates.Add(full);
                    else full.Dispose();
                }
                else if (high - low < curve.Domain.Length)
                {
                    // Construct the other arc explicitly; a decreasing native interval
                    // alone does not distinguish reversed traversal from seam wrapping.
                    using var tail = high < curve.Domain.T1 ? curve.Trim(high, curve.Domain.T1) : null;
                    using var head = low > curve.Domain.T0 ? curve.Trim(curve.Domain.T0, low) : null;
                    if (tail is not null && head is not null)
                    {
                        var joined = Curve.JoinCurves([tail, head], _tolerance);
                        if (joined.Length == 1) candidates.Add(joined[0]);
                        else foreach (var c in joined) c.Dispose();
                    }
                    else if ((tail ?? head)?.DuplicateCurve() is { } arc) candidates.Add(arc);
                }
            }
            // A candidate matches within tol and is rejected only beyond 2 tol; anything
            // between (or an invalid candidate) is ambiguous, so the guard is unresolved.
            var matches = 0;
            Curve? match = null;
            foreach (var candidate in candidates)
            {
                if (!candidate.IsValid) return null;
                var p = candidate.PointAtStart; var q = candidate.PointAtEnd;
                var endpoints = Math.Min(Math.Max(p.DistanceTo(start), q.DistanceTo(end)),
                    Math.Max(p.DistanceTo(end), q.DistanceTo(start)));
                var middle = candidate.PointAt(candidate.Domain.Mid);
                if (!other.ClosestPoint(middle, out var t)) return null;
                var error = Math.Max(endpoints, other.PointAt(t).DistanceTo(middle));
                if (error > 2 * _tolerance) continue;
                if (error > _tolerance) return null;
                matches++; match = candidate;
            }
            if (matches == 1) selected = match;
            return selected;
        }
        finally { foreach (var candidate in candidates) if (candidate != selected) candidate.Dispose(); }
    }

    private FaceSection Get(BrepFace face, int plane)
    {
        var key = (face.Brep, face.FaceIndex, plane);
        if (_sections.TryGetValue(key, out var section)) return section;
        section = Section(face, _planes[plane]);
        _sections.Add(key, section);
        return section;
    }

    // False means unresolved: caller must repeat exactly #73's pair check with
    // no slab exclusion. A positive off-topology witness throws immediately.
    internal virtual bool Check(BrepFace a, BrepFace b, Curve[] shared, Point3d[] sharedPoints)
    {
        var timer = Stopwatch.StartNew();
        var outcome = "unresolved";
        try
        {
            var budget = 262144;
            bool OnShared(Point3d p) => sharedPoints.Any(q => p.DistanceTo(q) <= _tolerance) ||
                shared.Any(c => c.ClosestPoint(p, out var t) && c.PointAt(t).DistanceTo(p) <= _tolerance);
            void Refuse(string witness)
            {
                outcome = "refused";
                throw new SectionFailure($"Coincident Brep faces {a.FaceIndex}/{b.FaceIndex} at a material slab boundary away from shared topology ({a.UnderlyingSurface().GetType().Name}/{b.UnderlyingSurface().GetType().Name}; {witness}).");
            }
            bool Covered(Curve portion)
            {
                if (SectionGeometry.CoveredBySharedTopology(portion, shared, _tolerance, null, out _, out var remote)) return true;
                if (remote) Refuse("curve overlap");
                return false;
            }
            bool Points(FaceSection first, FaceSection second)
            {
                foreach (var p in first.Points)
                {
                    var coincides = false;
                    foreach (var q in second.Points)
                    {
                        if (--budget < 0) return false;
                        coincides |= p.DistanceTo(q) <= _tolerance;
                    }
                    foreach (var c in second.Curves)
                    {
                        if (--budget < 0 || !c.ClosestPoint(p, out var t)) return false;
                        coincides |= c.PointAt(t).DistanceTo(p) <= _tolerance;
                    }
                    if (coincides && !OnShared(p)) Refuse("isolated section point");
                }
                return true;
            }
            for (var i = 0; i < _planes.Length; i++)
            {
                var first = Get(a, i); var second = Get(b, i);
                if (!first.Success || !second.Success || first.Curves.Any(c => c is null || !c.IsValid) ||
                    second.Curves.Any(c => c is null || !c.IsValid) || first.Points.Any(p => !p.IsValid) || second.Points.Any(p => !p.IsValid)) return false;
                foreach (var ca in first.Curves)
                foreach (var cb in second.Curves)
                {
                    if (--budget < 0) return false;
                    lock (SectionGeometry.CurveIntersectionGate)
                    {
                        using var events = Intersection.CurveCurve(ca, cb, _tolerance, _tolerance);
                        if (events is null) return false;
                        foreach (var e in events)
                        {
                            if (--budget < 0 || (!e.IsPoint && !e.IsOverlap) || !e.PointA.IsValid || !e.PointB.IsValid ||
                                !double.IsFinite(e.ParameterA) || !double.IsFinite(e.ParameterB)) return false;
                            if (e.IsPoint)
                            {
                                if (!OnShared(e.PointA) || !OnShared(e.PointB)) Refuse("curve point event");
                                continue;
                            }
                            if (!e.PointA2.IsValid || !e.PointB2.IsValid) return false;
                            using var pa = Extract(ca, e.OverlapA, e.PointA, e.PointA2, cb);
                            using var pb = Extract(cb, e.OverlapB, e.PointB, e.PointB2, ca);
                            if (pa is null || pb is null || !pa.IsValid || !pb.IsValid) return false;
                            // Even a sub-tolerance overlap is positive evidence, but only an
                            // off-topology proof leaf refuses; an inconclusive proof falls back.
                            if (!Covered(pa) || !Covered(pb)) return false;
                        }
                    }
                }
                if (!Points(first, second) || !Points(second, first)) return false;
            }
            outcome = "passed";
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return false; }
        finally { SectionWork.Guard(timer.Elapsed.TotalSeconds, outcome); }
    }
    public void Dispose()
    {
        foreach (var section in _sections.Values)
            foreach (var curve in section.Curves) curve?.Dispose();
        _sections.Clear();
    }
}
