using System.Text.Json;
using System.Text.Json.Serialization;

namespace M2DPlus.Core.Diagnostics;

/// <summary>One line class of a drawn view, in the view frame's plane coordinates (document units).</summary>
public sealed record ClassSignature(int Pieces, int Points, double Length, double MinX, double MinY, double MaxX, double MaxY);

/// <summary>A platform-comparable summary of one drawn view: what a Windows and a Mac solve of the same fixture must
/// agree on within tolerance. Piece counts are reported but not gated, because HLD may split the same locus into a
/// different number of segments on another build; lengths, extents, regions and errors are gated.</summary>
public sealed record DrawingSignature(string View, string? Error, double FrameWidth, double FrameHeight,
    IReadOnlyDictionary<string, ClassSignature> Classes, int Regions, int Hatches, double RegionArea);

/// <summary>A named set of view signatures for one fixture scene, and where it was produced.</summary>
public sealed record SignatureBaseline(int Schema, string Fixture, string Producer, IReadOnlyList<DrawingSignature> Views)
{
    public const int CurrentSchema = 1;
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.Never };
    public string ToJson() => JsonSerializer.Serialize(this, Options);
    public static SignatureBaseline FromJson(string json) =>
        JsonSerializer.Deserialize<SignatureBaseline>(json, Options) ?? throw new InvalidDataException("Empty signature baseline.");
}

/// <summary>The outcome of comparing one view: failures break the self-test; notes are informational.</summary>
public sealed record SignatureComparison(string View, IReadOnlyList<string> Failures, IReadOnlyList<string> Notes)
{
    public bool Passed => Failures.Count == 0;

    /// <summary>Lengths and region areas may differ by <paramref name="relative"/> of the expected value (and by
    /// <paramref name="absolute"/> for small values); extents by <paramref name="absolute"/> plus
    /// <paramref name="relative"/> of the frame size.</summary>
    public static SignatureComparison Compare(DrawingSignature expected, DrawingSignature actual, double relative = 0.005, double absolute = 0.01)
    {
        var failures = new List<string>(); var notes = new List<string>();
        var size = Math.Max(Math.Max(expected.FrameWidth, expected.FrameHeight), 1e-9);
        bool Close(double a, double b, double scale) => Math.Abs(a - b) <= absolute + relative * Math.Abs(scale);
        if (!string.Equals(expected.Error, actual.Error, StringComparison.Ordinal))
            failures.Add($"error: expected '{expected.Error ?? "none"}', got '{actual.Error ?? "none"}'");
        if (!Close(expected.FrameWidth, actual.FrameWidth, size) || !Close(expected.FrameHeight, actual.FrameHeight, size))
            failures.Add(FormattableString.Invariant($"frame: expected {expected.FrameWidth:G6} x {expected.FrameHeight:G6}, got {actual.FrameWidth:G6} x {actual.FrameHeight:G6}"));
        foreach (var name in expected.Classes.Keys.Union(actual.Classes.Keys).Order(StringComparer.Ordinal))
        {
            var empty = new ClassSignature(0, 0, 0, 0, 0, 0, 0);
            var e = expected.Classes.GetValueOrDefault(name, empty); var a = actual.Classes.GetValueOrDefault(name, empty);
            if (!Close(e.Length, a.Length, e.Length))
                failures.Add(FormattableString.Invariant($"{name} length: expected {e.Length:G6}, got {a.Length:G6}"));
            if (e.Points != a.Points) failures.Add($"{name} points: expected {e.Points}, got {a.Points}");
            if (e.Pieces + e.Points > 0 && a.Pieces + a.Points > 0 &&
                !(Close(e.MinX, a.MinX, size) && Close(e.MinY, a.MinY, size) && Close(e.MaxX, a.MaxX, size) && Close(e.MaxY, a.MaxY, size)))
                failures.Add(FormattableString.Invariant($"{name} extents: expected [{e.MinX:G6},{e.MinY:G6}]-[{e.MaxX:G6},{e.MaxY:G6}], got [{a.MinX:G6},{a.MinY:G6}]-[{a.MaxX:G6},{a.MaxY:G6}]"));
            if (e.Pieces != a.Pieces) notes.Add($"{name} pieces: expected {e.Pieces}, got {a.Pieces} (segmentation may differ; not gated)");
        }
        if (expected.Regions != actual.Regions) failures.Add($"cut regions: expected {expected.Regions}, got {actual.Regions}");
        if (expected.Hatches != actual.Hatches) failures.Add($"hatches: expected {expected.Hatches}, got {actual.Hatches}");
        if (!Close(expected.RegionArea, actual.RegionArea, expected.RegionArea))
            failures.Add(FormattableString.Invariant($"cut area: expected {expected.RegionArea:G6}, got {actual.RegionArea:G6}"));
        return new(expected.View, failures, notes);
    }

    /// <summary>Every expected view must be present in <paramref name="actual"/> by name, and no extra view may appear.</summary>
    public static IReadOnlyList<SignatureComparison> Compare(SignatureBaseline expected, IReadOnlyList<DrawingSignature> actual, double relative = 0.005, double absolute = 0.01)
    {
        var results = new List<SignatureComparison>();
        var byName = actual.GroupBy(v => v.View, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        foreach (var view in expected.Views)
            results.Add(byName.TryGetValue(view.View, out var got) ? Compare(view, got, relative, absolute)
                : new(view.View, ["view missing from this run"], []));
        foreach (var extra in actual.Where(v => expected.Views.All(e => e.View != v.View)))
            results.Add(new(extra.View, ["view not in the baseline"], []));
        return results;
    }
}
