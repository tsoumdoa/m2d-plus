using M2DPlus.Core;
using M2DPlus.Core.Styling;
using Xunit;

namespace M2DPlus.Core.Tests;

/// <summary>The owner's min visible rule (REQUIREMENTS, "Ignoring objects seen only through small gaps").</summary>
public sealed class MinVisibleTests
{
    private const double Tol = 1e-6;
    private static MinVisiblePiece Line(string source, MinVisibleRole role, double x0, double y0, double x1, double y1) =>
        new(source, role, [new(x0, y0), new(x1, y1)], Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0)));
    private static MinVisiblePiece Drawn(string s, double x0, double y0, double x1, double y1) => Line(s, MinVisibleRole.Drawn, x0, y0, x1, y1);
    private static MinVisiblePiece Hidden(string s, double x0, double y0, double x1, double y1) => Line(s, MinVisibleRole.Hidden, x0, y0, x1, y1);
    private static IReadOnlySet<string> Ignored(double threshold, params MinVisiblePiece[] pieces) =>
        MinVisibleRule.Evaluate(pieces, threshold, Tol).Ignored;

    /// <summary>A 1 x 2 column behind a wall, seen only through a 0.003 wide vertical gap at x = 0.5: its top and
    /// bottom edges show 0.003 each, the rest is hidden.</summary>
    private static MinVisiblePiece[] ColumnThroughGap(string id = "column") =>
    [
        Drawn(id, .5, 2, .503, 2), Drawn(id, .5, 0, .503, 0),
        Hidden(id, 0, 2, .5, 2), Hidden(id, .503, 2, 1, 2), Hidden(id, 0, 0, .5, 0), Hidden(id, .503, 0, 1, 0),
        Hidden(id, 0, 0, 0, 2), Hidden(id, 1, 0, 1, 2),
    ];

    [Fact]
    public void PartlyHiddenSmallObjectIsIgnored()
    {
        Assert.Equal(["column"], Ignored(.005, ColumnThroughGap()));
        Assert.Empty(Ignored(.002, ColumnThroughGap())); // each glimpse is 3 mm, longer than 2 mm
    }

    [Fact]
    public void FullyVisibleSmallObjectIsKeptEvenWithSelfHiddenBackEdges()
    {
        // A 1 mm box in an axonometric: its outline is drawn; its back edges are hidden by the box itself, inside the outline.
        var bolt = new[]
        {
            Drawn("bolt", 0, 0, .001, 0), Drawn("bolt", .001, 0, .0015, .0005), Drawn("bolt", .0015, .0005, .0015, .0015),
            Drawn("bolt", .0015, .0015, .0005, .0015), Drawn("bolt", .0005, .0015, 0, .001), Drawn("bolt", 0, .001, 0, 0),
            Hidden("bolt", .0005, .0005, .0015, .0005), Hidden("bolt", .0005, .0005, .0005, .0015), Hidden("bolt", 0, 0, .0005, .0005),
        };
        Assert.Empty(Ignored(.005, bolt));
        // Nothing hidden at all: never ignored, however small.
        Assert.Empty(Ignored(1, Drawn("pin", 0, 0, .0001, 0)));
    }

    [Fact]
    public void FragmentedLongEdgeIsJoinedAndKept()
    {
        // One 9 mm edge split by the solve into nine adjacent 1 mm fragments, the rest of the object hidden.
        var pieces = Enumerable.Range(0, 9).Select(i => Drawn("beam", i * .001, 0, (i + 1) * .001, 0))
            .Append(Hidden("beam", .009, 0, 1, 0)).Append(Hidden("beam", 0, -.2, 1, -.2)).ToArray();
        Assert.Empty(Ignored(.005, pieces));
        Assert.Equal(["beam"], Ignored(.01, pieces)); // 9 mm joined is still shorter than 10 mm
    }

    [Fact]
    public void GlimpsesWithGapsBetweenThemAreNotJoined()
    {
        // A post behind a fence: three collinear 2 mm glimpses with gaps in between total 6 mm, but none is continuous.
        var pieces = new[] { Drawn("post", 0, 0, .002, 0), Drawn("post", .01, 0, .012, 0), Drawn("post", .02, 0, .022, 0), Hidden("post", -1, 0, 1, 0) };
        Assert.Equal(["post"], Ignored(.005, pieces));
    }

    [Fact]
    public void PiecesTouchingEndToInteriorAndAroundACornerJoin()
    {
        // An L of two 3 mm edges, and a 3 mm edge whose end touches the middle of another (T): both reach 6 mm.
        var corner = new[] { Drawn("l", 0, 0, .003, 0), Drawn("l", .003, 0, .003, .003), Hidden("l", 0, 0, 0, 1) };
        var tee = new[] { Drawn("t", 0, 0, .004, 0), Drawn("t", .002, 0, .002, .003), Hidden("t", 0, 0, 0, 1) };
        Assert.Empty(Ignored(.005, [.. corner, .. tee]));
        Assert.Equal(["l", "t"], Ignored(.0075, [.. corner, .. tee]).Order()); // 6 and 7 mm joined
    }

    [Fact]
    public void ArcAndPolylinePiecesJoinThroughTheirOwnPoints()
    {
        var arc = new MinVisiblePiece("arc", MinVisibleRole.Drawn, [new(0, 0), new(.001, .001), new(.002, 0)], .0031);
        var tail = Drawn("arc", .001, .001, .001, .004); // starts on the polyline's first segment end
        Assert.Empty(Ignored(.005, arc, tail, Hidden("arc", 0, 0, 0, 1)));
    }

    [Fact]
    public void ThresholdZeroOrOffIgnoresNothing()
    {
        Assert.Empty(Ignored(0, ColumnThroughGap()));
        Assert.Empty(Ignored(-1, ColumnThroughGap()));
        Assert.Empty(Ignored(double.NaN, ColumnThroughGap()));
    }

    [Fact]
    public void UnresolvedCutAndUndrawnObjectsAreNeverIgnored()
    {
        var uncertain = ColumnThroughGap("u").Append(Line("u", MinVisibleRole.Unresolved, 0, 1, 1, 1)).ToArray();
        Assert.Empty(Ignored(.005, uncertain));
        Assert.Empty(MinVisibleRule.Evaluate(ColumnThroughGap("cut"), .005, Tol, new HashSet<string> { "cut" }).Ignored);
        Assert.Empty(Ignored(.005, Hidden("behind", 0, 0, 1, 0)));
    }

    [Fact]
    public void EachInstanceIsItsOwnObject()
    {
        // Two instances of one definition: one seen through the gap, one fully in view.
        var visible = new[] { Drawn("def/instance-2", 5, 0, 6, 0), Drawn("def/instance-2", 6, 0, 6, 2), Hidden("def/instance-2", 5.5, .5, 5.6, .5) };
        Assert.Equal(["def/instance-1"], Ignored(.005, [.. ColumnThroughGap("def/instance-1"), .. visible]));
    }

    [Theory]
    [InlineData(DocumentUnits.Metres, "2 mm", .002)]
    [InlineData(DocumentUnits.Metres, "0.002", .002)]
    [InlineData(DocumentUnits.Millimetres, "2 mm", 2)]
    [InlineData(DocumentUnits.Millimetres, ".5 cm", 5)]
    [InlineData(DocumentUnits.Feet, "6\"", .5)]
    [InlineData(DocumentUnits.Metres, "off", 0)]
    [InlineData(DocumentUnits.Metres, "0", 0)]
    public void ThresholdParsesLikeOtherLengths(DocumentUnits units, string text, double expected)
    {
        var view = new ViewSettingsContext("v", "V", ViewSourceKind.Plane, false, 10, .00001);
        foreach (var spelling in new[] { "min visible", "min_visible", "Min  Visible" })
        {
            var result = Settings.Resolve([view], [[$"{spelling} = {text}"]], units);
            Assert.Empty(result.Diagnostics);
            Assert.Equal(expected, result.Views[0].MinVisible, 12);
            Assert.Equal(SettingSource.Input, result.Views[0]["min_visible"].Source);
        }
    }

    [Fact]
    public void DefaultIsOffAndBadValuesWarnWithAHint()
    {
        var view = new ViewSettingsContext("v", "V", ViewSourceKind.Plane, false, 10, .001);
        var defaults = Settings.Resolve([view], [], DocumentUnits.Metres).Views[0];
        Assert.Equal(0, defaults.MinVisible);
        Assert.Contains("min_visible = off (default)", defaults.Describe());
        var bad = Settings.Resolve([view], [["min visible = big", "min visible = -2 mm"]], DocumentUnits.Metres);
        Assert.Equal(2, bad.Diagnostics.Count(d => d.Key == "min_visible" && d.Message.Contains("Use off or a nonnegative distance")));
        Assert.Equal(0, bad.Views[0].MinVisible);
    }

    [Fact]
    public void PrecedenceIsInputThenViewObjectThenMenu()
    {
        var a = new ViewSettingsContext("object:a", "A", ViewSourceKind.Box, true, 10, .001, 5);
        var b = new ViewSettingsContext("object:b", "B", ViewSourceKind.Box, true, 10, .001, 5);
        static SettingsRow Row(string key, string value) => new([KeyValuePair.Create(key, value)]);
        // View-object user text m2d.min_visible arrives as the row key min_visible.
        var editor = new EditorSettings(Row("min_visible", "1 mm"), [KeyValuePair.Create("object:a", Row("min_visible", "3 mm"))]);
        var objects = Settings.Resolve([a, b], [], DocumentUnits.Metres, editor);
        Assert.Equal((.003, SettingSource.EditorView), (objects.Views[0].MinVisible, objects.Views[0]["min_visible"].Source));
        Assert.Equal((.001, SettingSource.EditorAll), (objects.Views[1].MinVisible, objects.Views[1]["min_visible"].Source));
        var input = Settings.Resolve([a, b], [["min visible = 5 mm"]], DocumentUnits.Metres, editor);
        Assert.All(input.Views, v => Assert.Equal((.005, SettingSource.Input), (v.MinVisible, v["min_visible"].Source)));
    }

    [Fact]
    public void MinVisibleIsPresentationAndHiddenKeysTheVisibilitySolve()
    {
        Assert.DoesNotContain("min_visible", Settings.VisibilityKeys);
        Assert.Contains("hidden", Settings.VisibilityKeys);
        Assert.All(Settings.VisibilityKeys, k => Assert.Contains(k, Settings.Keys));
        var view = new ViewSettingsContext("v", "V", ViewSourceKind.Plane, false, 10, .001);
        var off = Settings.Resolve([view], [["hidden = off", "min visible = 2 mm"]], DocumentUnits.Metres).Views[0];
        var solve = off;
        Assert.False(solve.Hidden); Assert.False(off.Hidden);
        Assert.Equal(off.MinVisible, solve.MinVisible);
        Assert.All(Settings.VisibilityKeys, k => Assert.Equal(off[k].Value, solve[k].Value));
    }

    [Fact]
    public void SyntaxOnlyParserAcceptsTheSpacedKey() =>
        Assert.Equal("min_visible", Assert.Single(Settings.Parse(["min visible = 2 mm"])).Key);
}
