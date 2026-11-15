using System.Collections.Immutable;
using System.Globalization;

namespace M2DPlus.Core.Output;

/// <summary>What the Geometry input holds, for <c>tree = auto</c> (owner decision 2026-11-15, UX §1.3).</summary>
public enum GeometryInputKind
{
    /// <summary>Geometry is empty: the visible Rhino document is drawn.</summary>
    Document,
    /// <summary>Every item is a referenced Rhino object (an id, a referenced or model object, or a layer name).</summary>
    Referenced,
    /// <summary>At least one item is geometry made in Grasshopper (not a document object).</summary>
    Grasshopper,
}

/// <summary>
/// Plain values captured on the host for one drawn source leaf: a Rhino object, one part of a block instance (through
/// its instance path) or one Grasshopper item. Grouping is presentation over these; it never changes the solve.
/// </summary>
/// <param name="InputPath">The Geometry input branch path; document mode uses <c>{0}</c>.</param>
/// <param name="InputItem">The item index in that branch; document mode uses the top-level object's ordinal.</param>
/// <param name="Referenced">The top-level source is a Rhino document object (or a layer name selecting them).</param>
/// <param name="LayerIndex">The leaf's own Rhino layer-table index (a block part's own layer); -1 for GH geometry.</param>
/// <param name="LayerPath">That layer's full path, for example <c>Walls::Exterior</c>; empty for GH geometry.</param>
/// <param name="ObjectId">The top-level Rhino object (a block instance is one object); empty for GH geometry.</param>
/// <param name="ObjectName">That object's Rhino name, if it has one.</param>
/// <param name="LayerPattern">The input text when the top-level item is a layer name such as <c>Walls::*</c>.</param>
public sealed record SourceGroupInfo(int[] InputPath, int InputItem, bool Referenced, int LayerIndex, string LayerPath,
    Guid ObjectId, string? ObjectName, string? LayerPattern = null)
{
    public static SourceGroupInfo Grasshopper(int[] path, int item) => new(path, item, false, -1, "", Guid.Empty, null);
}

/// <summary>One output branch below the view index, and its label for the Key output.</summary>
public sealed record OutputGroup(ImmutableArray<int> Path, string Key);

/// <summary>Branch rules for the line, Cut, Hatch and Key outputs (UX §1.3). Paths here exclude the leading view index.</summary>
public static class OutputTree
{
    /// <summary>The by-layer branch of sources with no Rhino layer (GH geometry under an explicit <c>tree = layer</c>).
    /// GH paths can't be negative, and a reserved maximum never collides with a real layer index.</summary>
    public const int NoLayer = int.MaxValue;
    public const string NoLayerKey = "(no layer)";

    public static GeometryInputKind Classify(bool documentMode, IEnumerable<bool> referencedItems)
    {
        if (documentMode) return GeometryInputKind.Document;
        return referencedItems.All(r => r) ? GeometryInputKind.Referenced : GeometryInputKind.Grasshopper;
    }

    /// <summary><c>auto</c> is by layer for document mode or an all-referenced input, and by object as soon as one item is
    /// Grasshopper geometry (so mixed input is by object). An explicit layer/object is kept.</summary>
    public static OutputTreeMode Effective(OutputTreeMode setting, GeometryInputKind input) => setting switch
    {
        OutputTreeMode.Auto => input == GeometryInputKind.Grasshopper ? OutputTreeMode.Object : OutputTreeMode.Layer,
        _ => setting,
    };

    public static string Describe(OutputTreeMode setting, GeometryInputKind input)
    {
        var effective = Effective(setting, input) == OutputTreeMode.Layer ? "by layer" : "by object";
        if (setting != OutputTreeMode.Auto) return effective;
        var why = input switch
        {
            GeometryInputKind.Document => "document mode", GeometryInputKind.Referenced => "every Geometry item is a referenced Rhino object",
            _ => "Geometry holds Grasshopper geometry",
        };
        return $"{effective} (auto: {why})";
    }

    /// <summary>The group of one source under an effective (never Auto) mode.</summary>
    public static OutputGroup Group(SourceGroupInfo source, OutputTreeMode effective)
    {
        ArgumentNullException.ThrowIfNull(source);
        switch (effective)
        {
            case OutputTreeMode.Layer:
                return source.LayerIndex >= 0 ? new([source.LayerIndex], source.LayerPath) : new([NoLayer], NoLayerKey);
            case OutputTreeMode.Object:
                var path = source.InputPath.Append(source.InputItem).ToImmutableArray();
                if (source.LayerPattern is { } pattern) return new(path, "layer " + pattern);
                if (source.Referenced && source.ObjectId != Guid.Empty)
                    return new(path, string.IsNullOrEmpty(source.ObjectName) ? source.ObjectId.ToString() : source.ObjectId + " " + source.ObjectName);
                return new(path, FormatPath(source.InputPath) + " " + source.InputItem.ToString(CultureInfo.InvariantCulture));
            default: throw new ArgumentException("Resolve tree = auto with OutputTree.Effective first.", nameof(effective));
        }
    }

    /// <summary>A GH-style path text, for example <c>{0;2}</c>.</summary>
    public static string FormatPath(IEnumerable<int> path) => "{" + string.Join(";", path.Select(i => i.ToString(CultureInfo.InvariantCulture))) + "}";

    /// <summary>Lexicographic order of branch paths, shorter first on a common prefix (Grasshopper's order).</summary>
    public static IComparer<IReadOnlyList<int>> PathOrder { get; } = Comparer<IReadOnlyList<int>>.Create((a, b) =>
    {
        for (var i = 0; i < Math.Min(a.Count, b.Count); i++)
        {
            var order = a[i].CompareTo(b[i]);
            if (order != 0) return order;
        }
        return a.Count.CompareTo(b.Count);
    });
}

/// <summary>
/// The branch layout of one view's outputs. Lines go to <c>{group}</c>. A cut region goes to <c>{group; region}</c>, where
/// a merged region takes the lowest group of the sources that took part and <c>region</c> counts that group's regions
/// in order, so each region's loops and its single hatch item share one path (PLAN contract 5). The Key output gets
/// one label per group that holds anything, at <c>{group}</c>.
/// </summary>
public sealed class OutputTreeLayout
{
    private readonly IReadOnlyDictionary<string, SourceGroupInfo> _sources;
    private readonly Dictionary<string, OutputGroup> _groups = new(StringComparer.Ordinal);
    private readonly SortedDictionary<IReadOnlyList<int>, OutputGroup> _used = new(OutputTree.PathOrder);
    private readonly Dictionary<string, int> _regions = new(StringComparer.Ordinal);
    public OutputTreeMode Mode { get; }

    /// <param name="mode">The effective mode (resolve Auto with <see cref="OutputTree.Effective"/>).</param>
    /// <param name="sources">Group info per source identity, captured on the host.</param>
    public OutputTreeLayout(OutputTreeMode mode, IReadOnlyDictionary<string, SourceGroupInfo> sources)
    {
        if (mode == OutputTreeMode.Auto) throw new ArgumentException("Resolve tree = auto with OutputTree.Effective first.", nameof(mode));
        Mode = mode; _sources = sources ?? throw new ArgumentNullException(nameof(sources));
    }

    private OutputGroup GroupOf(string identity)
    {
        if (_groups.TryGetValue(identity, out var group)) return group;
        if (!_sources.TryGetValue(identity, out var source))
            throw new InvalidOperationException($"No output-tree information was captured for source '{identity}'.");
        return _groups[identity] = OutputTree.Group(source, Mode);
    }

    /// <summary>The branch (below the view) of a drawn line or point of this source; marks its group as used.</summary>
    public int[] Line(string identity)
    {
        var group = GroupOf(identity); _used.TryAdd(group.Path, group);
        return group.Path.ToArray();
    }

    /// <summary>The branch of the next cut region of these sources, <c>{group; region}</c>; call once per region, in order.</summary>
    public int[] Region(IEnumerable<string> identities)
    {
        var group = identities.Select(GroupOf).OrderBy(g => (IReadOnlyList<int>)g.Path, OutputTree.PathOrder).FirstOrDefault()
            ?? throw new ArgumentException("A cut region needs at least one source.", nameof(identities));
        _used.TryAdd(group.Path, group);
        var key = string.Join(",", group.Path);
        var ordinal = _regions.GetValueOrDefault(key); _regions[key] = ordinal + 1;
        return group.Path.Add(ordinal).ToArray();
    }

    /// <summary>One label per used group, in path order: the Key output's branches.</summary>
    public IReadOnlyList<OutputGroup> Keys => _used.Values.ToArray();
}
