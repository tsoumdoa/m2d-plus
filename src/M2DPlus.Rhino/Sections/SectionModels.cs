using M2DPlus.Core.Sections;
using Rhino.Geometry;

namespace M2DPlus.Rhino.Sections;

/// <summary>Borrowed for the duration of Compute; caller owns and must keep geometry alive and unchanged.</summary>
public sealed record SectionSource(string Id, GeometryBase Geometry, SectionStyleSnapshot Style)
{
    /// <summary>Definition-local shape shared with congruent sources; null validates this source's own geometry.</summary>
    internal SectionCanonical? Canonical { get; init; }
}
public sealed record SectionRequest(Plane Plane, double Tolerance, double AngleTolerance,
    Rectangle3d? Crop = null, CutMode Cuts = CutMode.Separate, HatchMode Hatch = HatchMode.Styles)
{
    // Geometry may be centred independently of the user's plane origin. Styles
    // remain relative to that original plane, including through presentation.
    internal Plane? PatternPlane { get; init; }
}
public sealed record SectionDiagnostic(string SourceId, SectionStatus Status, string Message);
public sealed record ComponentClassification(string SourceId, int Component, SectionStatus Status,
    double Minimum, double Maximum, string Certificate);
public sealed record SectionEdge(Curve Curve, EdgeOrigin Origin) : IDisposable
{
    public void Dispose() => Curve.Dispose();
}
public sealed record SectionLoop(Curve Curve, bool IsHole, IReadOnlyList<SectionEdge> Edges) : IDisposable
{
    public void Dispose() { Curve.Dispose(); foreach (var edge in Edges) edge.Dispose(); }
}
/// <summary>One Cut branch (all loops) and exactly one matching Hatch slot. Owns all geometry.</summary>
public sealed record SectionRegion(string Id, IReadOnlyList<SectionLoop> Loops, Hatch? Hatch,
    IReadOnlyList<string> SourceIds, SectionStyleSnapshot Style, SectionStatus Status, double Area,
    string? Uncertainty) : IDisposable
{
    public void Dispose() { foreach (var loop in Loops) loop.Dispose(); Hatch?.Dispose(); }
}
public sealed class SectionResult : IDisposable
{
    public List<SectionRegion> Regions { get; } = new();
    public Dictionary<string, PhysicalClipResult> Visibility { get; } = new(StringComparer.Ordinal);
    public List<ComponentClassification> Components { get; } = new();
    public List<SectionDiagnostic> Diagnostics { get; } = new();
    internal List<SectionRefusal> Refusals { get; } = new();
    public bool RequiresQuarantine => Diagnostics.Any(d => d.Status is SectionStatus.Uncertain or SectionStatus.Unsupported);
    public void Dispose() { foreach (var region in Regions) region.Dispose(); foreach (var clip in Visibility.Values) clip.Dispose(); foreach (var refusal in Refusals) refusal.Dispose(); }
}
// Transient compute evidence, consumed before drawing/worker serialization.
// Only components without published cut topology are represented here.
internal sealed record SectionRefusal(string SourceId, GeometryBase Geometry) : IDisposable
{
    public void Dispose() => Geometry.Dispose();
}
public sealed record VisibilityPiece(string SourceId, Brep Geometry, IReadOnlyList<SectionEdge> Edges) : IDisposable
{
    public void Dispose() { Geometry.Dispose(); foreach (var edge in Edges) edge.Dispose(); }
}
public sealed record SectionContactFeature(string SourceId, GeometryBase Geometry) : IDisposable
{
    public EdgeOrigin Origin => EdgeOrigin.Original;
    public void Dispose() => Geometry.Dispose();
}
public sealed class PhysicalClipResult : IDisposable
{
    public List<VisibilityPiece> Pieces { get; } = new();
    public List<SectionContactFeature> ContactFeatures { get; } = new();
    public List<SectionDiagnostic> Diagnostics { get; } = new();
    public bool Succeeded => !Diagnostics.Any(d => d.Status is SectionStatus.Uncertain or SectionStatus.Unsupported);
    public bool SectionOnly { get; internal set; }
    public void Dispose() { foreach (var piece in Pieces) piece.Dispose(); foreach (var feature in ContactFeatures) feature.Dispose(); }
}
