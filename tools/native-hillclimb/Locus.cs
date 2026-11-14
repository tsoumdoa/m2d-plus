using System.Diagnostics;
using System.Text.Json;
using M2DPlus.Rhino.Services;
using Rhino.FileIO;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;
using Point = Rhino.Geometry.Point;

namespace NativeHillclimb;
internal static class Locus
{
    internal sealed record Difference(string Kind, string Class, string Source, int Index, double UncoveredLength, string Detail);
    internal sealed record Verdict(bool Passed, int BaselineCount, int CandidateCount, int AttributionChanges, IReadOnlyList<Difference> Differences, string Method);
    internal static void Save(ViewDrawing view, string path)
    {
        using var archive = new File3dm();
        foreach (var item in view.Items)
        {
            using var geo = item.CopyGeometry(); using var attr = new Rhino.DocObjects.ObjectAttributes();
            attr.Name = item.SourceIdentity; attr.SetUserString("class", item.Class.ToString()); attr.SetUserString("profile", item.Profile.ToString());
            archive.Objects.Add(geo, attr);
        }
        foreach (var region in view.Regions) foreach (var loop in region.Region.Loops)
        { using var attr = new Rhino.DocObjects.ObjectAttributes { Name = "region:" + string.Join(',', region.SourcePath) }; archive.Objects.AddCurve(loop.Curve, attr); }
        foreach (var region in view.Regions) M2DPlus.Rhino.Sections.SectionHatches.AddToFile(archive, region.Region);
        if (!archive.Write(path, 8)) throw new IOException("Could not preserve lossless result");
    }
    internal static void Roundtrip(ViewDrawing view, string path)
    {
        Save(view, path);
        var copies=new List<GeometryBase>();
        try
        {
            using(var received=File3dm.Read(path) ?? throw new IOException("Result read failed"))
            {
                foreach(var obj in received.Objects)
                {
                    var copy=obj.Geometry.Duplicate(); copies.Add(copy); copy.EnsurePrivateCopy();
                    if(copy is Hatch hatch && received.AllHatchPatterns.FindIndex(hatch.PatternIndex) is null) throw new IOException("Missing result hatch pattern");
                }
            }
            // A receiving host retains the complete result after closing the archive.
            foreach(var copy in copies) if(!copy.IsValid) throw new IOException("Invalid detached result geometry");
        }
        finally {foreach(var copy in copies) copy.Dispose();}
    }
    internal static double Canonicalize(ViewDrawing drawing)
    {
        var timer = Stopwatch.StartNew();
        // Stable total ordering and removal of byte-identical class/source copies only.
        // A geometric union cannot reconstruct missing loci or repair changed visibility.
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var piece in drawing.Pieces.ToArray())
        {
            var key = piece.Class + "/" + piece.Profile + "/" + piece.SourceIdentity + "/" + piece.GeometricKey;
            if (keys.Add(key)) continue;
            drawing.Pieces.Remove(piece); piece.Dispose();
        }
        drawing.Pieces.Sort(NativeDrawing.ComparePieces); return timer.Elapsed.TotalSeconds;
    }
    internal static Verdict Compare(ViewDrawing baseline, ViewDrawing candidate, double tolerance)
    {
        var differences = new List<Difference>();
        if (baseline.Error is not null || candidate.Error is not null) differences.Add(new("execution", "", "", -1, 0, $"{baseline.Error} / {candidate.Error}"));
        // Existing declared uncertainty is preserved and compared; it is never treated as visible.
        if (baseline.Report.InvalidExtractionCount != candidate.Report.InvalidExtractionCount || baseline.Report.Quarantined != candidate.Report.Quarantined)
            differences.Add(new("quality", "", "", -1, 0, "Changed extraction/quarantine status"));
        var a = baseline.Items.Select(p => new Item(p, p.CopyGeometry())).ToArray();
        var b = candidate.Items.Select(p => new Item(p, p.CopyGeometry())).ToArray();
        var attribution = 0;
        try
        {
            Check(a, b, "missing", tolerance, differences, ref attribution);
            Check(b, a, "extra", tolerance, differences, ref attribution);
            // Multiplicity is checked independently of set coverage. Splits and joins do not change it.
            Multiplicity(a, b, tolerance, differences);
            if (baseline.Regions.Count != candidate.Regions.Count) differences.Add(new("regions", "", "", -1, 0, "Region count changed"));
            else for (var i = 0; i < baseline.Regions.Count; i++)
            {
                var x = baseline.Regions[i].Region; var y = candidate.Regions[i].Region;
                var areaBudget = x.Loops.Sum(l => l.Curve.GetLength()) * tolerance + Math.PI * tolerance * tolerance * x.Loops.Count;
                var loopsMatch = x.Loops.Count == y.Loops.Count;
                var used = new bool[y.Loops.Count];
                if (loopsMatch) foreach (var loop in x.Loops)
                {
                    var match = Enumerable.Range(0,y.Loops.Count).FirstOrDefault(j => !used[j] && loop.IsHole == y.Loops[j].IsHole && Equivalent(loop.Curve,y.Loops[j].Curve,tolerance), -1);
                    if (match < 0) { loopsMatch=false; break; } used[match]=true;
                }
                if (x.Status != y.Status || (x.Hatch is null) != (y.Hatch is null) || Math.Abs(x.Area - y.Area) > areaBudget || JsonSerializer.Serialize(x.Style) != JsonSerializer.Serialize(y.Style) || !loopsMatch ||
                    !baseline.Regions[i].SourcePath.SequenceEqual(candidate.Regions[i].SourcePath) || !x.SourceIds.Order(StringComparer.Ordinal).SequenceEqual(y.SourceIds.Order(StringComparer.Ordinal)))
                    differences.Add(new("region-topology-or-style", "", "", i, 0, "Region source/branch, status, style, area, hatch presence, or complete hole-aware loop coverage differs"));
            }
            if (attribution != 0) differences.Add(new("attribution-only", "all", "", -1, 0, $"{attribution} source-attribution disagreements; this candidate does not certify deterministic metadata"));
            return new(differences.Count == 0, a.Length, b.Length, attribution, differences, "Bidirectional full CurveCurve overlap interval coverage including profile flags, parameter unions, point matching, class/profile equality and pointwise signed overlap multiplicity; no tessellation or sample signatures. Overlap certificates use half model tolerance. Parameter-roundoff gaps are ignored only when their arc length is at most one millionth of model tolerance; an entirely absent curve always fails. Attribution differences reported separately. Existing baseline uncertainty stays uncertainty.");
        }
        finally { foreach (var item in a.Concat(b)) item.Geometry.Dispose(); }
    }
    private sealed record Item(DrawingPiece Piece, GeometryBase Geometry);
    private static void Check(Item[] from, Item[] to, string kind, double tol, List<Difference> differences, ref int attribution)
    {
        using var tree = new RTree();
        var exact = to.GroupBy(t => t.Piece.Class + "/" + t.Piece.Profile + "/" + t.Piece.GeometricKey).ToDictionary(g => g.Key, g => g.Select(t => t.Piece.SourceIdentity).ToHashSet(StringComparer.Ordinal));
        for (var i = 0; i < to.Length; i++) { var bounds = to[i].Geometry.GetBoundingBox(true); bounds.Inflate(tol); if (bounds.IsValid) tree.Insert(bounds, i); }
        for (var i = 0; i < from.Length; i++)
        {
            var item = from[i];
            if (exact.TryGetValue(item.Piece.Class + "/" + item.Piece.Profile + "/" + item.Piece.GeometricKey, out var owners))
            { if (!owners.Contains(item.Piece.SourceIdentity)) attribution++; continue; }
            var nearby = new List<int>(); var bounds = item.Geometry.GetBoundingBox(true); bounds.Inflate(tol);
            tree.Search(bounds, (_, e) => nearby.Add(e.Id));
            var possible = nearby.Where(j => to[j].Piece.Class == item.Piece.Class && to[j].Piece.Profile == item.Piece.Profile).ToList();
            if (item.Geometry is Point point)
            {
                if (!possible.Any(j => to[j].Geometry is Point p && p.Location.DistanceTo(point.Location) <= tol)) differences.Add(new(kind + "-point", item.Piece.Class.ToString(), item.Piece.SourceIdentity, i, 0, point.Location.ToString()));
                continue;
            }
            if (item.Geometry is not Curve curve) { differences.Add(new("unsupported-comparison", item.Piece.Class.ToString(), item.Piece.SourceIdentity, i, 0, item.Geometry.GetType().Name)); continue; }
            var spans = new List<Interval>();
            foreach (var j in possible)
            {
                if (to[j].Geometry is not Curve other) continue;
                using var hits = Intersection.CurveCurve(curve, other, tol * .5, tol * .5);
                if (hits is null) continue;
                foreach (var hit in hits.Where(h => h.IsOverlap)) spans.Add(hit.OverlapA);
            }
            var uncovered = Gaps(curve, spans, tol);
            if (uncovered.Count != 0)
            {
                var otherSpans = new List<Interval>();
                foreach (var j in nearby.Where(j => to[j].Piece.Class != item.Piece.Class || to[j].Piece.Profile != item.Piece.Profile))
                {
                    if (to[j].Geometry is not Curve other) continue;
                    using var hits = Intersection.CurveCurve(curve, other, tol * .5, tol * .5);
                    if (hits is not null) otherSpans.AddRange(hits.Where(h => h.IsOverlap).Select(h => h.OverlapA));
                }
                foreach (var gap in uncovered)
                {
                    using var part = curve.Trim(gap);
                    if (part is null) { differences.Add(new("comparison-failure", item.Piece.Class.ToString(), item.Piece.SourceIdentity, i, 0, "Unmatched interval trim failed")); continue; }
                    var position = gap.Min; var inOtherClass = true;
                    foreach (var span in otherSpans.OrderBy(s => s.Min))
                    { if (span.Max <= position) continue; if (span.Min > position) { inOtherClass = false; break; } position = Math.Max(position, span.Max); if (position >= gap.Max) break; }
                    inOtherClass &= position >= gap.Max;
                    differences.Add(new(inOtherClass ? "visibility-or-profile-change" : kind + "-locus", item.Piece.Class.ToString(), item.Piece.SourceIdentity, i, curve.GetLength(gap), JsonSerializer.Serialize(new[] { gap.Min, gap.Max })));
                }
            }
            else if (possible.All(j => to[j].Piece.SourceIdentity != item.Piece.SourceIdentity)) attribution++;
        }
    }
    private static bool Equivalent(Curve a, Curve b, double tol)
    {
        if (GeometryFingerprint.Compute(a)==GeometryFingerprint.Compute(b)) return true;
        using var hits=Intersection.CurveCurve(a,b,tol * .5,tol * .5);
        if(hits is null) return false;
        return Gaps(a,hits.Where(h=>h.IsOverlap).Select(h=>h.OverlapA).ToList(),tol).Count==0 && Gaps(b,hits.Where(h=>h.IsOverlap).Select(h=>h.OverlapB).ToList(),tol).Count==0;
    }
    private static List<Interval> Gaps(Curve curve, List<Interval> intervals, double tolerance)
    {
        var result = new List<Interval>(); var t = curve.Domain.Min;
        // Parameter precision only; never absorb a tolerance-sized missing geometric interval.
        var eps = Math.Abs(curve.Domain.Length) * 1e-12;
        bool Significant(double from,double to) => to > from && (to-from > eps || !double.IsFinite(curve.GetLength(new Interval(from,to))) || curve.GetLength(new Interval(from,to)) > tolerance * 1e-6);
        foreach (var span in intervals.OrderBy(s => s.Min)) { if (Significant(t,span.Min)) result.Add(new(t, span.Min)); t = Math.Max(t, span.Max); }
        if (intervals.Count==0 || Significant(t,curve.Domain.Max)) result.Add(new(t, curve.Domain.Max)); return result;
    }
    private static void Multiplicity(Item[] a, Item[] b, double tol, List<Difference> differences)
    {
        static string Key(Item i) => i.Piece.Class + "/" + i.Piece.GeometricKey;
        var countsA = a.GroupBy(Key).ToDictionary(g => g.Key, g => g.Count());
        var countsB = b.GroupBy(Key).ToDictionary(g => g.Key, g => g.Count());
        var changed = a.Concat(b).GroupBy(Key).Where(g => countsA.GetValueOrDefault(g.Key) != countsB.GetValueOrDefault(g.Key)).Select(g => g.First()).ToArray();
        if (changed.Length == 0) return;
        using var tree = new RTree(); var all = a.Concat(b).ToArray();
        for (var i = 0; i < all.Length; i++) { var bounds = all[i].Geometry.GetBoundingBox(true); bounds.Inflate(tol); if (bounds.IsValid) tree.Insert(bounds, i); }
        foreach (var carrier in changed)
        {
            var bounds = carrier.Geometry.GetBoundingBox(true); bounds.Inflate(tol); var nearby = new List<int>();
            tree.Search(bounds, (_, e) => { if (all[e.Id].Piece.Class == carrier.Piece.Class) nearby.Add(e.Id); });
            if (carrier.Geometry is Point p)
            {
                var delta = nearby.Where(i => all[i].Geometry is Point q && q.Location.DistanceTo(p.Location) <= tol).Sum(i => i < a.Length ? -1 : 1);
                if (delta != 0) differences.Add(new("point-multiplicity", carrier.Piece.Class.ToString(), carrier.Piece.SourceIdentity, -1, 0, $"signed count={delta}"));
                continue;
            }
            if (carrier.Geometry is not Curve curve) continue;
            var events = new List<(double T, int Delta)>();
            foreach (var i in nearby)
            {
                if (all[i].Geometry is not Curve other) continue;
                using var hits = Intersection.CurveCurve(curve, other, tol * .5, tol * .5);
                if (hits is null) continue;
                foreach (var hit in hits.Where(h => h.IsOverlap))
                { var sign = i < a.Length ? -1 : 1; events.Add((hit.OverlapA.Min, sign)); events.Add((hit.OverlapA.Max, -sign)); }
            }
            var groups = events.GroupBy(e => e.T).OrderBy(g => g.Key).ToArray(); var count = 0;
            for (var j = 0; j + 1 < groups.Length; j++)
            {
                count += groups[j].Sum(e => e.Delta);
                if (count == 0 || groups[j+1].Key<=groups[j].Key) continue;
                var length=curve.GetLength(new Interval(groups[j].Key,groups[j+1].Key));
                if(groups[j+1].Key-groups[j].Key<=Math.Abs(curve.Domain.Length)*1e-12 && double.IsFinite(length) && length<=tol*1e-6) continue;
                differences.Add(new("locus-multiplicity", carrier.Piece.Class.ToString(), carrier.Piece.SourceIdentity, -1, length, $"signed multiplicity={count}; parameters={groups[j].Key:R},{groups[j + 1].Key:R}"));
            }
        }
    }
}
