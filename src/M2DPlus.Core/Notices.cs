using System.Globalization;

namespace M2DPlus.Core;

/// <summary>GH balloon levels, without GH types: Remark (grey), Warning (orange), Error (red).</summary>
public enum NoticeLevel { Remark, Warning, Error }

public sealed record Notice(NoticeLevel Level, string Text);

/// <summary>What one finished view reports, as plain values (UX §5.2 severity policy).</summary>
/// <param name="Unresolved">Pieces in the Unresolved output (drawn orange in the preview, own bake layer).</param>
/// <param name="Quarantined">The whole view's visibility could not be trusted (an unsupported source that could hide
/// others, a native extraction failure): Visible / Hidden may be incomplete.</param>
/// <param name="OccludingUnsupported">Sources not drawn that could hide other geometry, by type.</param>
/// <param name="NonOccludingUnsupported">Sources not drawn that can't hide anything (text, dots, point clouds), by type.</param>
/// <param name="MissingLayers">Layer-name Geometry items that matched no layer.</param>
/// <param name="ToleranceRisk">The tolerance detail-risk census text (short edges, close gaps), if any.</param>
public sealed record ViewOutcome(string Name, string? Error, int Unresolved, bool Quarantined,
    IReadOnlyDictionary<string, int> OccludingUnsupported, IReadOnlyDictionary<string, int> NonOccludingUnsupported,
    IReadOnlyList<string> MissingLayers, string? ToleranceRisk);

/// <summary>
/// The severity policy (owner decision 2026-11-15, UX §5.2), following the GH convention: a <b>warning means the user has
/// to act</b>, because the drawing may be wrong or an input is wrong. What a normal run reports goes to remarks and Info.
/// "Never wrong without saying so" still holds: unresolved pieces are in the Unresolved output, orange in the preview,
/// counted in Info and named in a remark.
/// </summary>
public static class NoticePolicy
{
    public static IReadOnlyList<Notice> For(ViewOutcome view)
    {
        ArgumentNullException.ThrowIfNull(view);
        var notices = new List<Notice>();
        if (view.Error is not null) { notices.Add(new(NoticeLevel.Error, $"{view.Name}: {view.Error}")); return notices; }
        if (view.Quarantined)
            notices.Add(new(NoticeLevel.Warning, $"{view.Name}: visibility could not be trusted for the whole view, so its lines are Unresolved (orange). Check Info for the cause."));
        if (view.OccludingUnsupported.Count > 0)
            notices.Add(new(NoticeLevel.Warning, $"{view.Name}: {Count(view.OccludingUnsupported)} not drawn ({List(view.OccludingUnsupported)}). They could hide other objects. See Info."));
        foreach (var layer in view.MissingLayers)
            notices.Add(new(NoticeLevel.Warning, $"{view.Name}: no layer matches '{layer}' in Geometry."));
        if (view.Unresolved > 0 && !view.Quarantined)
            notices.Add(new(NoticeLevel.Remark, $"{view.Name}: {view.Unresolved.ToString(CultureInfo.InvariantCulture)} unresolved piece{(view.Unresolved == 1 ? "" : "s")} in the Unresolved output (orange in the preview, own layer when baked). Check them before issuing."));
        if (view.NonOccludingUnsupported.Count > 0)
            notices.Add(new(NoticeLevel.Remark, $"{view.Name}: {Count(view.NonOccludingUnsupported)} not drawn ({List(view.NonOccludingUnsupported)}). They hide nothing. See Info."));
        if (view.ToleranceRisk is { } risk)
            notices.Add(new(NoticeLevel.Remark, $"{view.Name}: {risk}"));
        return notices;
    }

    /// <summary>The worst level among notices, or null.</summary>
    public static NoticeLevel? Worst(IEnumerable<Notice> notices) => notices.Select(n => (NoticeLevel?)n.Level).DefaultIfEmpty(null).Max();

    private static string Count(IReadOnlyDictionary<string, int> types)
    {
        var n = types.Values.Sum();
        return n.ToString(CultureInfo.InvariantCulture) + (n == 1 ? " object" : " objects");
    }

    private static string List(IReadOnlyDictionary<string, int> types) =>
        string.Join(", ", types.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => $"{p.Value.ToString(CultureInfo.InvariantCulture)} {p.Key}"));
}
