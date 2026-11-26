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
    internal virtual Curve? Extract(Curve curve, Interval interval) =>
        interval.IsValid && interval.Length > 0 && curve.Domain.IncludesInterval(interval) ? curve.Trim(interval) : null;

    private FaceSection Get(BrepFace face, int plane)
    {
        var key = (face.Brep, face.FaceIndex, plane);
        if (_sections.TryGetValue(key, out var section)) return section;
        section = Section(face, _planes[plane]);
        _sections.Add(key, section);
        return section;
    }

    // False means unresolved: caller must repeat the old whole-pair check with
    // no slab exclusion. A positive off-topology witness throws immediately.
    internal virtual bool Check(BrepFace a, BrepFace b, Curve[] shared, Point3d[] sharedPoints)
    {
        var timer = Stopwatch.StartNew();
        try
        {
            var budget = 262144;
            bool OnShared(Point3d p) => sharedPoints.Any(q => p.DistanceTo(q) <= _tolerance) ||
                shared.Any(c => c.ClosestPoint(p, out var t) && c.PointAt(t).DistanceTo(p) <= _tolerance);
            void Refuse() => throw new SectionFailure($"Coincident Brep faces {a.FaceIndex}/{b.FaceIndex} at a material slab boundary away from shared topology ({a.UnderlyingSurface().GetType().Name}/{b.UnderlyingSurface().GetType().Name}).");
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
                    if (coincides && !OnShared(p)) Refuse();
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
                            if (!e.IsOverlap) continue;
                            if (!e.PointA2.IsValid || !e.PointB2.IsValid) return false;
                            using var pa = Extract(ca, e.OverlapA);
                            using var pb = Extract(cb, e.OverlapB);
                            if (pa is null || pb is null || !pa.IsValid || !pb.IsValid) return false;
                            // Even a sub-tolerance overlap is positive evidence.
                            if (!SectionGeometry.CoveredBySharedTopology(pa, shared, _tolerance) ||
                                !SectionGeometry.CoveredBySharedTopology(pb, shared, _tolerance)) Refuse();
                        }
                    }
                }
                if (!Points(first, second) || !Points(second, first)) return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return false; }
        finally { SectionWork.Guard(timer.Elapsed.TotalSeconds); }
    }
    public void Dispose()
    {
        foreach (var section in _sections.Values)
            foreach (var curve in section.Curves) curve?.Dispose();
        _sections.Clear();
    }
}
