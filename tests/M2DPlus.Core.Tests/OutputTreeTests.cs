using M2DPlus.Core.Output;
using Xunit;

namespace M2DPlus.Core.Tests;

/// <summary>tree = auto | layer | object (owner decision 2026-11-15, UX §1.3): the auto rule, layer and object paths,
/// block parts by their own layer, Cut/Hatch region branches and precedence. Paths exclude the leading view index.</summary>
public sealed class OutputTreeTests
{
    private static readonly Guid Wall = new("11111111-1111-1111-1111-111111111111"), Block = new("22222222-2222-2222-2222-222222222222");

    private static SourceGroupInfo Referenced(int item, int layer, string path, Guid id, string? name = null, int[]? input = null) =>
        new(input ?? [0], item, true, layer, path, id, name);

    [Theory]
    [InlineData(true, new bool[0], GeometryInputKind.Document, OutputTreeMode.Layer)]
    [InlineData(false, new[] { true, true }, GeometryInputKind.Referenced, OutputTreeMode.Layer)]
    [InlineData(false, new[] { false, false }, GeometryInputKind.Grasshopper, OutputTreeMode.Object)]
    [InlineData(false, new[] { true, false }, GeometryInputKind.Grasshopper, OutputTreeMode.Object)] // mixed: by object
    public void AutoIsByLayerForDocumentOrReferencedInputAndByObjectOtherwise(bool document, bool[] referenced, GeometryInputKind kind, OutputTreeMode auto)
    {
        Assert.Equal(kind, OutputTree.Classify(document, referenced));
        Assert.Equal(auto, OutputTree.Effective(OutputTreeMode.Auto, kind));
        // An explicit choice always wins over the input's kind.
        Assert.Equal(OutputTreeMode.Layer, OutputTree.Effective(OutputTreeMode.Layer, kind));
        Assert.Equal(OutputTreeMode.Object, OutputTree.Effective(OutputTreeMode.Object, kind));
        Assert.StartsWith(auto == OutputTreeMode.Layer ? "by layer (auto: " : "by object (auto: ", OutputTree.Describe(OutputTreeMode.Auto, kind));
        Assert.Equal("by object", OutputTree.Describe(OutputTreeMode.Object, kind));
    }

    [Fact]
    public void LayerPathsUseTheLayerTableIndexSoTheyStayStableWhenLayersAreAdded()
    {
        // Layer 7 "Arch" sorts before layer 2 "Walls::Exterior" by name; the path is the table index, not the order.
        var sources = new Dictionary<string, SourceGroupInfo>
        {
            ["a"] = Referenced(0, 2, "Walls::Exterior", Wall), ["b"] = Referenced(1, 7, "Arch", Guid.NewGuid()),
            ["c"] = Referenced(2, 2, "Walls::Exterior", Guid.NewGuid()),
        };
        var layout = new OutputTreeLayout(OutputTreeMode.Layer, sources);
        Assert.Equal([2], layout.Line("a")); Assert.Equal([7], layout.Line("b")); Assert.Equal([2], layout.Line("c"));
        Assert.Equal([([2], "Walls::Exterior"), ([7], "Arch")], layout.Keys.Select(k => (k.Path.ToArray(), k.Key)));

        // A new layer (index 8) and a new object on it leave every existing path unchanged: sparse paths are fine.
        sources["d"] = Referenced(3, 8, "Aaa first by name", Guid.NewGuid());
        var later = new OutputTreeLayout(OutputTreeMode.Layer, sources);
        Assert.Equal([2], later.Line("a")); Assert.Equal([7], later.Line("b")); Assert.Equal([8], later.Line("d"));
    }

    [Fact]
    public void BlockPartsGoToTheirOwnLayerAndKeepTheInstanceAsTheirObject()
    {
        // One referenced instance (item 0) whose parts sit on Frames (4) and Glass (5); a wall (item 1) on Walls (2).
        var sources = new Dictionary<string, SourceGroupInfo>
        {
            ["block/def/frame"] = Referenced(0, 4, "Frames", Block, "Window W1"),
            ["block/def/pane"] = Referenced(0, 5, "Glass", Block, "Window W1"),
            ["block/def/nested/def2/sill"] = Referenced(0, 4, "Frames", Block, "Window W1"),
            ["wall"] = Referenced(1, 2, "Walls", Wall),
        };
        var byLayer = new OutputTreeLayout(OutputTreeMode.Layer, sources);
        Assert.Equal([4], byLayer.Line("block/def/frame")); Assert.Equal([5], byLayer.Line("block/def/pane"));
        Assert.Equal([4], byLayer.Line("block/def/nested/def2/sill")); Assert.Equal([2], byLayer.Line("wall"));
        Assert.Equal(["Walls", "Frames", "Glass"], byLayer.Keys.Select(k => k.Key));

        // By object, the instance is one object: all its parts share its branch and label.
        var byObject = new OutputTreeLayout(OutputTreeMode.Object, sources);
        Assert.All(sources.Keys.Where(k => k.StartsWith("block", StringComparison.Ordinal)), k => Assert.Equal([0, 0], byObject.Line(k)));
        Assert.Equal([0, 1], byObject.Line("wall"));
        Assert.Equal([$"{Block} Window W1", Wall.ToString()], byObject.Keys.Select(k => k.Key));
    }

    [Fact]
    public void ByObjectKeepsTheGeometryInputTreeAndLabelsGrasshopperItemsByPathAndItem()
    {
        var sources = new Dictionary<string, SourceGroupInfo>();
        foreach (var (path, count) in new[] { (new[] { 0, 1 }, 2), (new[] { 0, 0 }, 3), (new[] { 3 }, 1) })
            for (var item = 0; item < count; item++) sources[$"input/{OutputTree.FormatPath(path)}/{item}"] = SourceGroupInfo.Grasshopper(path, item);
        sources["ref"] = Referenced(1, 0, "Default", Wall, input: [3]); // a referenced item in a mixed input
        var layout = new OutputTreeLayout(OutputTreeMode.Object, sources);
        foreach (var (identity, source) in sources) Assert.Equal(source.InputPath.Append(source.InputItem), layout.Line(identity));
        // Path order, with GH-style labels; the referenced item is labelled by its object id.
        Assert.Equal(["{0;0} 0", "{0;0} 1", "{0;0} 2", "{0;1} 0", "{0;1} 1", "{3} 0", Wall.ToString()], layout.Keys.Select(k => k.Key));
        Assert.Equal([[0, 0, 0], [0, 0, 1], [0, 0, 2], [0, 1, 0], [0, 1, 1], [3, 0], [3, 1]], layout.Keys.Select(k => k.Path.ToArray()));
    }

    [Fact]
    public void LayerNameItemsByObjectShareTheirItemBranchAndGrasshopperGeometryByLayerHasItsOwnBranch()
    {
        var sources = new Dictionary<string, SourceGroupInfo>
        {
            ["walls/1"] = new([0], 0, true, 2, "Walls", Guid.NewGuid(), null, "Walls*"),
            ["walls/2"] = new([0], 0, true, 3, "Walls2", Guid.NewGuid(), null, "Walls*"),
            ["gh"] = SourceGroupInfo.Grasshopper([0], 1),
        };
        var byObject = new OutputTreeLayout(OutputTreeMode.Object, sources);
        Assert.Equal(byObject.Line("walls/1"), byObject.Line("walls/2"));
        Assert.Equal([0, 1], byObject.Line("gh"));
        Assert.Equal(["layer Walls*", "{0} 1"], byObject.Keys.Select(k => k.Key));
        var byLayer = new OutputTreeLayout(OutputTreeMode.Layer, sources);
        Assert.Equal([OutputTree.NoLayer], byLayer.Line("gh"));
        Assert.Equal(OutputTree.NoLayerKey, byLayer.Keys[^1].Key);
    }

    [Fact]
    public void CutRegionsGetOneTrailingRegionLevelAndMergedCutsGoToTheLowestGroup()
    {
        var sources = new Dictionary<string, SourceGroupInfo>
        {
            ["col"] = Referenced(0, 6, "Columns", Guid.NewGuid()), ["rhs"] = Referenced(1, 6, "Columns", Guid.NewGuid()),
            ["slab"] = Referenced(2, 3, "Slabs", Guid.NewGuid()), ["open"] = Referenced(3, 9, "Surfaces", Guid.NewGuid()),
        };
        var layout = new OutputTreeLayout(OutputTreeMode.Layer, sources);
        // Regions are numbered per group in order; every region has its own branch, so its loops (outer + holes)
        // and its one hatch item (or null for the open outline) share exactly one path.
        var paths = new[]
        {
            layout.Region(["col"]), layout.Region(["rhs"]), layout.Region(["slab"]), layout.Region(["open"]),
            layout.Region(["col", "slab"]), // cuts = merge: the lowest group that took part (Slabs, index 3)
            layout.Region(["slab", "col"]), // the same, whatever the order of its sources
        }.Select(p => p.ToArray()).ToArray();
        Assert.Equal([[6, 0], [6, 1], [3, 0], [9, 0], [3, 1], [3, 2]], paths);
        Assert.Equal(paths.Length, paths.Select(OutputTree.FormatPath).Distinct().Count());
        // Lines of the same sources sit one level up, at their group; Key labels each group once.
        Assert.Equal([6], layout.Line("rhs"));
        Assert.Equal([([3], "Slabs"), ([6], "Columns"), ([9], "Surfaces")], layout.Keys.Select(k => (k.Path.ToArray(), k.Key)));

        var byObject = new OutputTreeLayout(OutputTreeMode.Object, sources);
        Assert.Equal([0, 0, 0], byObject.Region(["slab", "col"])); // merged: object 0 is the lowest group
        Assert.Equal([0, 2, 0], byObject.Region(["slab"]));
        Assert.Throws<ArgumentException>(() => byObject.Region([]));
    }

    [Fact]
    public void LayoutNeedsAResolvedModeAndCapturedSources()
    {
        Assert.Throws<ArgumentException>(() => new OutputTreeLayout(OutputTreeMode.Auto, new Dictionary<string, SourceGroupInfo>()));
        Assert.Throws<InvalidOperationException>(() => new OutputTreeLayout(OutputTreeMode.Layer, new Dictionary<string, SourceGroupInfo>()).Line("missing"));
    }

    private static ViewSettingsContext View(string id) => new(id, id, ViewSourceKind.Plane, true, 20, .001);

    [Fact]
    public void TreeFollowsNormalPrecedenceAndTheOldBranchValueReadsAsAuto()
    {
        var views = new[] { View("a"), View("b"), View("c") };
        SettingsRow Row(string value) => new([KeyValuePair.Create("tree", value)]);
        var editor = new EditorSettings(Row("layer"), [KeyValuePair.Create("b", Row("object")), KeyValuePair.Create("c", Row("object"))]);
        // Settings input (view c only) > view object (b, c) > menu (all) > default auto.
        var resolved = Settings.Resolve(views, [new[] { "[c]", "tree = layer" }], DocumentUnits.Metres, editor);
        Assert.Empty(resolved.Diagnostics);
        Assert.Equal([OutputTreeMode.Layer, OutputTreeMode.Object, OutputTreeMode.Layer], resolved.Views.Select(v => v.Tree));
        Assert.Equal([SettingSource.EditorAll, SettingSource.EditorView, SettingSource.Input], resolved.Views.Select(v => v["tree"].AssignedBy));
        Assert.All(Settings.Resolve(views, [], DocumentUnits.Metres).Views, v => Assert.Equal(OutputTreeMode.Auto, v.Tree));
        Assert.Contains("tree = auto (default)", Settings.Resolve(views, [], DocumentUnits.Metres).Views[0].Describe());

        // "branch" was the old default ("Per source branch") and now means auto, in text, user text and the menu.
        Assert.Equal(OutputTreeMode.Auto, Settings.Resolve([View("a")], [new[] { "tree = branch" }], DocumentUnits.Metres).Views[0].Tree);
        Assert.Equal(OutputTreeMode.Auto, Settings.Resolve([View("a")], [], DocumentUnits.Metres, new(Row("branch"))).Views[0].Tree);
        var typo = Settings.Resolve([View("a")], [new[] { "tree = layers" }], DocumentUnits.Metres);
        Assert.Contains(typo.Diagnostics, d => d.Message.Contains("'layer'", StringComparison.Ordinal));
    }

    [Fact]
    public void TreeIsPresentationAndNeverKeysTheVisibilitySolve() => Assert.DoesNotContain("tree", Settings.VisibilityKeys);
}
