using System.Collections.Immutable;
using M2DPlus.Core.Styling;

namespace M2DPlus.Core.Bake;

public static class BakeTags
{
    public const string Bake = "m2d.bake";
    public const string Source = "m2d.source";
    public const string Class = "m2d.class";
    public const string Hash = "m2d.hash";
    public const string Weights = "m2d.weights";
    // Additional provenance required to detect a user move after save/reopen.
    public const string Layer = "m2d.layer";
    public static string Owner(Guid component, string drawing)
    {
        if (component == Guid.Empty) throw new ArgumentException("Component identity is required.", nameof(component));
        BakeNames.ValidateSegment(drawing);
        return $"{component:D}/{drawing}";
    }
    /// <summary>Reads a baked object's <see cref="Bake"/> marker (<c>component/drawing</c>). Any other <c>m2d.bake</c> text, such as
    /// a view object's layout setting (<c>m2d.bake = layer</c>), is not an owner.</summary>
    public static bool TryParseOwner(string? value, out Guid component, out string drawing)
    {
        component = Guid.Empty; drawing = "";
        if (value is null || value.Length < 38 || value[36] != '/' || !Guid.TryParseExact(value[..36], "D", out component) || component == Guid.Empty)
            return false;
        drawing = value[37..];
        return !string.IsNullOrWhiteSpace(drawing);
    }
    public static ImmutableDictionary<string, string> Create(Guid component, string drawing, string source,
        LineClass lineClass, string geometryHash, string layerPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(geometryHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(layerPath);
        var category = lineClass == LineClass.Profile ? "visible" : BakeNames.Category(lineClass).Replace(" ", "").ToLowerInvariant();
        return new Dictionary<string, string> { [Bake] = Owner(component, drawing), [Source] = source,
            [Class] = category, [Hash] = geometryHash, [Layer] = layerPath }.ToImmutableDictionary();
    }
}

/// <summary>Host recomputes CurrentHash and CurrentLayer. OriginalHash/OriginalLayer come from saved bake provenance.</summary>
public sealed record BakedRecord(string Id, string Owner, string OriginalHash, string CurrentHash,
    string OriginalLayer, string CurrentLayer);
public sealed record NewBakeRecord(string Key, string Layer, string Hash, string Source, LineClass Class);
public sealed record BakeReplacementPlan(ImmutableArray<string> Delete, ImmutableArray<string> Keep,
    ImmutableArray<NewBakeRecord> Add, string Summary);

public static class BakePlan
{
    /// <summary>Replace only unchanged records owned by this component/drawing. Every new piece is added,
    /// including a fresh piece corresponding to an old user-edited piece; edited originals stay intact.</summary>
    public static BakeReplacementPlan Replace(Guid component, string drawing, IEnumerable<BakedRecord> previous,
        IEnumerable<NewBakeRecord> records)
    {
        var owner = BakeTags.Owner(component, drawing);
        var delete = ImmutableArray.CreateBuilder<string>();
        var keep = ImmutableArray.CreateBuilder<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var record in previous)
        {
            if (!seen.Add(record.Id)) throw new ArgumentException("Duplicate previously baked id.", nameof(previous));
            if (record.Owner != owner) continue;
            if (string.IsNullOrWhiteSpace(record.OriginalHash) || string.IsNullOrWhiteSpace(record.OriginalLayer) ||
                record.OriginalHash != record.CurrentHash || record.OriginalLayer != record.CurrentLayer) keep.Add(record.Id);
            else delete.Add(record.Id);
        }
        var add = records.ToImmutableArray();
        if (add.Select(r => r.Key).Distinct(StringComparer.Ordinal).Count() != add.Length)
            throw new ArgumentException("New bake keys must be unique.", nameof(records));
        foreach (var record in add)
            if (string.IsNullOrWhiteSpace(record.Key) || string.IsNullOrWhiteSpace(record.Hash) || string.IsNullOrWhiteSpace(record.Layer))
                throw new ArgumentException("New bake records need a key, hash and layer.", nameof(records));
        var summary = string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"Baked {drawing}: {add.Length:N0} objects (replaced {delete.Count:N0} · kept {keep.Count:N0} you edited)");
        return new(delete.ToImmutable(), keep.ToImmutable(), add, summary);
    }
}

/// <summary>The editable style of a bake layer, as the host reads it. M2D+ stores its <see cref="Signature"/> on each layer it
/// creates (<see cref="Tag"/>) and updates it only with its own preset-width changes, so any user edit makes them differ.</summary>
public sealed record BakeLayerStyle(int Colour, int PlotColour, double PlotWeight, int Linetype, int Material)
{
    public const string Tag = "m2d.style";
    public const string Created = "m2d.created";
    public string Signature => string.Create(System.Globalization.CultureInfo.InvariantCulture,
        $"v1|{Colour:X8}|{PlotColour:X8}|{PlotWeight:R}|{Linetype}|{Material}");
}

/// <summary>A layer that held objects a re-bake replaced. <paramref name="StyleTag"/> is the stored <see cref="BakeLayerStyle.Tag"/>
/// (null on layers from beta.3 and earlier); <paramref name="ClassicSignature"/> is then the style M2D+ would have created that
/// classic layer with, or null when it cannot tell.</summary>
public sealed record BakeLayerState(string FullPath, bool CreatedByM2D, bool Empty, bool HasChildren, bool Visible, bool Locked,
    string? StyleTag, string Signature, string? ClassicSignature = null);

public static class BakeLayerCleanup
{
    /// <summary>After a re-bake, possibly with another layout (UX §8.2), an emptied layer is removed only when M2D+ created it,
    /// it holds no objects and no sublayers, and the user has not changed it: same style as created, still on and unlocked.
    /// The <c>M2D+</c> root and every layer the user created or changed are kept.</summary>
    public static bool Removable(BakeLayerState layer)
    {
        if (!layer.CreatedByM2D || !layer.Empty || layer.HasChildren || !layer.Visible || layer.Locked) return false;
        if (!layer.FullPath.StartsWith(BakeNames.Root + "::", StringComparison.Ordinal)) return false;
        return layer.StyleTag is not null ? layer.StyleTag == layer.Signature : layer.ClassicSignature == layer.Signature;
    }
}

public sealed record LayerWidthDecision(bool Update, double Width, string? WeightsTag, string Message);

public static class BakeLayerWidths
{
    /// <summary>Exact comparison is deliberate (Q18). Compare each actual category/source layer independently.
    /// A missing/invalid prior tag preserves the user's existing layer.</summary>
    public static LayerWidthDecision Decide(LineClass category, double currentWidth, string? previousWeightsTag,
        LineWeightPreset next, bool isNew = false)
    {
        if (!double.IsFinite(currentWidth)) throw new ArgumentOutOfRangeException(nameof(currentWidth));
        var width = next[category].PrintWidthMillimetres;
        var tag = $"1:{next.Scale.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}";
        if (next.UseSourceLayers) return new(false, currentWidth, previousWeightsTag, "Widths from source layers");
        if (isNew) return new(true, width, tag, "Created preset width");
        var scaleText = previousWeightsTag?.StartsWith("1:", StringComparison.Ordinal) == true ? previousWeightsTag[2..] : null;
        if (double.TryParse(scaleText, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var previousScale) &&
            double.IsFinite(previousScale) && previousScale > 0 && currentWidth == new LineWeightPreset(previousScale)[category].PrintWidthMillimetres)
            return new(currentWidth != width || previousWeightsTag != tag, width, tag, "Updated M2D+ preset width");
        return new(false, currentWidth, previousWeightsTag, "Kept layer width you edited");
    }
}
