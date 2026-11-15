using M2DPlus.Core.Sections;
using Xunit;

namespace M2DPlus.Core.Tests;

public sealed class SettingsTests
{
    private static ViewSettingsContext View(string name = "Section A", string id = "a", bool cut = true,
        ViewSourceKind source = ViewSourceKind.Rectangle) => new(id, name, source, cut, 20, .001);
    private static SettingsRow Row(params (string Key, string Value)[] values) => new(values.Select(p => KeyValuePair.Create(p.Key, p.Value)));
    private static SettingsResolution Resolve(string text, params ViewSettingsContext[] views) =>
        Settings.Resolve(views.Length == 0 ? [View()] : views, [new[] { text }], DocumentUnits.Metres);

    [Theory]
    [InlineData("3 m", DocumentUnits.Millimetres, 3000)]
    [InlineData("300 cm", DocumentUnits.Feet, 9.84251968503937)]
    [InlineData("10'", DocumentUnits.Inches, 120)]
    [InlineData("6\"", DocumentUnits.Metres, .1524)]
    [InlineData(".25", DocumentUnits.Feet, .25)]
    [InlineData("+1.2e3 MM", DocumentUnits.Metres, 1.2)]
    [InlineData("-3 m", DocumentUnits.Metres, -3)]
    [InlineData("0", DocumentUnits.Kilometres, 0)]
    public void UnitEnumConvertsDistances(string text, DocumentUnits units, double expected) =>
        Assert.Equal(expected, Settings.ParseDistance(text, units), 10);

    [Theory]
    [InlineData("2..3 m")]
    [InlineData("1/2 ft")]
    [InlineData("5'6\"")]
    [InlineData("1e309")]
    [InlineData("1e308 km")]
    [InlineData("")]
    [InlineData("1 m junk")]
    public void MalformedAndOverflowingDistancesAreRejected(string text) =>
        Assert.Throws<FormatException>(() => Settings.ParseDistance(text, DocumentUnits.Metres));

    [Fact]
    public void AllTwentyKeysParseWithTypedValues()
    {
        var result = Resolve("""
            depth = 8 m
            farclip = line
            beyond = 300 cm
            fade = yes
            fade.start = 25 %
            fade.end = 90
            fade.limit = 10 %
            crop = off
            hidden = false
            tangents = 1
            seams = true
            glass = clear
            scale = 1:50
            weights = layers
            hatch = colour
            cuts = merge
            gap = 6"
            name = Section C
            tree = object
            tolerance = 0.1 mm
            """, View(source: ViewSourceKind.NamedView));
        Assert.Empty(result.Diagnostics);
        var s = Assert.Single(result.Views);
        Assert.Equal(20, s.Values.Count);
        Assert.Equal(8, s.Depth); Assert.Equal(3, s.Beyond); Assert.Equal(FarClipMode.Line, s.FarClip);
        Assert.True(s.Fade); Assert.Equal(25, s.FadeStart); Assert.Equal(90, s.FadeEnd); Assert.Equal(10, s.FadeLimit);
        Assert.False(s.Crop); Assert.False(s.Hidden); Assert.True(s.Tangents); Assert.True(s.Seams);
        Assert.Equal(GlassMode.Clear, s.Glass); Assert.Equal(50, s.Scale); Assert.Equal(WeightMode.Layers, s.Weights);
        Assert.Equal(HatchMode.Colour, s.Hatch); Assert.Equal(CutMode.Merge, s.Cuts); Assert.Equal(.1524, s.Gap.Value, 10);
        Assert.Equal("Section C", s.Name); Assert.Equal(OutputTreeMode.Object, s.Tree); Assert.Equal(.0001, s.Tolerance, 10);
        Assert.All(s.Values.Values, v => Assert.True(v.IsSetByInput));
    }

    [Theory]
    [InlineData("ON", true)] [InlineData("True", true)] [InlineData("YES", true)] [InlineData("1", true)]
    [InlineData("off", false)] [InlineData("false", false)] [InlineData("no", false)] [InlineData("0", false)]
    public void BooleanAliases(string text, bool expected) => Assert.Equal(expected, Resolve($"hidden = {text}").Views[0].Hidden);

    [Theory]
    [InlineData("hatch", "black", HatchMode.Black)]
    [InlineData("hatch", "styles", HatchMode.Styles)]
    [InlineData("hatch", "none", HatchMode.None)]
    public void RemainingHatchModes(string key, string text, HatchMode expected) => Assert.Equal(expected, Resolve($"{key} = {text}").Views[0].Hatch);

    [Fact]
    public void DefaultsAndAutomaticValuesUseViewAndDocumentUnits()
    {
        var contexts = new[] { View(), View("Elevation", "b", false, ViewSourceKind.Plane),
            View("Box", "c", source: ViewSourceKind.Box) with { SourceDepth = 42, SceneDepth = 100 } };
        var result = Settings.Resolve(contexts, [], DocumentUnits.Millimetres);
        Assert.Equal(2, result.Diagnostics.Length);
        Assert.All(result.Diagnostics, d => Assert.Equal(DiagnosticSeverity.Remark, d.Severity));
        var s = result.Views[0];
        // 3m is outside this 20mm finite view and is ignored.
        Assert.Null(s.Beyond); Assert.Equal(20, s.Depth); Assert.Equal(42, result.Views[2].Depth);
        Assert.Null(result.Views[1].Beyond); Assert.True(s.Crop); Assert.False(result.Views[1].Crop);
        Assert.True(s.Hidden); Assert.False(s.Fade); Assert.Equal(100, s.Scale); Assert.Equal(30, s.FadeLimit);
        Assert.Equal(SettingSource.Auto, s["depth"].Source); Assert.Equal(SettingSource.Default, s["hidden"].Source);
        Assert.Equal(SettingSource.Auto, s["tolerance"].Source); Assert.Equal(.001, s.Tolerance);
        var wide = Settings.Resolve([View() with { SceneDepth = 10000 }], [], DocumentUnits.Millimetres).Views[0];
        Assert.Equal(3000, wide.Beyond);
    }

    [Fact]
    public void PrecedenceIsPerKeyAndInputAutoRetainsInputLock()
    {
        var editor = new EditorSettings(Row(("depth", "12 m"), ("hidden", "off"), ("scale", "50")),
            new[] { KeyValuePair.Create("a", Row(("depth", "9 m"), ("scale", "200"))) });
        var context = View();
        var result = Settings.Resolve([context], [new[] { "depth = auto", "seams = on" }], DocumentUnits.Metres, editor);
        var s = result.Views[0];
        Assert.Equal(20, s.Depth); Assert.Equal(SettingSource.Auto, s["depth"].Source);
        Assert.Equal(SettingSource.Input, s["depth"].AssignedBy); Assert.True(s["depth"].IsSetByInput);
        Assert.Contains("set by input", s["depth"].SourceDescription);
        Assert.Equal(200, s.Scale); Assert.Equal(SettingSource.EditorView, s["scale"].Source);
        Assert.False(s.Hidden); Assert.Equal(SettingSource.EditorAll, s["hidden"].Source);
        Assert.Equal(9, Settings.Resolve([context], [], DocumentUnits.Metres, editor).Views[0].Depth);
        Assert.Equal("9 m", editor.Views["a"].Values["depth"]);
    }

    [Fact]
    public void EditorRowsFollowStableIdentityWhenViewsReorder()
    {
        var editor = new EditorSettings(SettingsRow.Empty, new[] { KeyValuePair.Create("b", Row(("scale", "200"))) });
        var result = Settings.Resolve([View("B", "b"), View()], [], DocumentUnits.Metres, editor);
        Assert.Equal(200, result.Views[0].Scale); Assert.Equal(100, result.Views[1].Scale);
    }

    [Fact]
    public void BranchItemsAreJoinedAndHeadersOverrideBranchMatchingInAnyBranch()
    {
        var result = Settings.Resolve([View(), View("B", "b")],
            [new[] { "depth = 8", "[B]\nscale = 50" }, new[] { "hidden = off", "[#0]", "beyond = off", "[*]\nseams = on" }], DocumentUnits.Metres);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(8, result.Views[0].Depth); Assert.Equal(20, result.Views[1].Depth);
        Assert.Equal(50, result.Views[1].Scale); Assert.False(result.Views[1].Hidden);
        Assert.True(result.Views[0].Hidden); Assert.Null(result.Views[0].Beyond);
        Assert.All(result.Views, s => Assert.True(s.Seams));
    }

    [Fact]
    public void SingleBranchAppliesToEveryViewAndLastValidAssignmentWins()
    {
        var result = Resolve("hidden=off\n[B]\nhidden=on\n[*]\nhidden=off\nhidden=oops", View(), View("B", "b"));
        Assert.All(result.Views, s => Assert.False(s.Hidden));
        Assert.Single(result.Diagnostics);
    }

    [Fact]
    public void BadBranchesReportErrorWithoutDiscardingEditorValues()
    {
        var result = Settings.Resolve([View(), View("B", "b")], [new[] { "hidden=off" }, [], []], DocumentUnits.Metres);
        Assert.True(result.HasErrors); Assert.Contains("Give one Settings branch, or one per view", result.Diagnostics[0].Message);
        Assert.All(result.Views, s => Assert.True(s.Hidden));
    }

    [Fact]
    public void DiagnosticsSuggestKeysValuesViewsAndDoNotLeakUnknownHeaderBlocks()
    {
        var result = Resolve("""
            farclp = line
            farclip = nolin
            depth=-1
            hidden = off
            [Secton A]
            scale=50
            [*]
            seams=on
            broken
            """);
        Assert.False(result.Views[0].Hidden); Assert.True(result.Views[0].Seams); Assert.Equal(100, result.Views[0].Scale);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("Did you mean 'farclip'"));
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("Did you mean 'noline'"));
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("Did you mean 'Section A'"));
        Assert.Contains(result.Diagnostics, d => d.Line == 3 && d.Key == "depth");
        Assert.Contains(result.Diagnostics, d => d.Line == 9 && d.Branch == 0);
    }

    [Theory]
    [InlineData("depth=-.1")]
    [InlineData("beyond=-3 m")]
    [InlineData("gap=-1")]
    [InlineData("tolerance=0")]
    [InlineData("tolerance=auto")]
    [InlineData("fade.start=-1")]
    [InlineData("fade.end=101 %")]
    [InlineData("fade.limit=NaN")]
    [InlineData("scale=0")]
    [InlineData("scale=2:100")]
    [InlineData("scale=auto")]
    [InlineData("name=::bad")]
    public void InvalidValuesAreWarningsAndOtherKeysSurvive(string invalid)
    {
        var result = Resolve($"{invalid}\nhidden=off");
        Assert.Single(result.Diagnostics); Assert.False(result.HasErrors); Assert.False(result.Views[0].Hidden);
    }

    [Fact]
    public void FadeContradictionFallsBackThroughStartPrecedenceOnly()
    {
        var editor = new EditorSettings(Row(("fade.start", "20")));
        var result = Settings.Resolve([View()], [new[] { "fade.start=80\nfade.end=40\nfade=on" }], DocumentUnits.Metres, editor);
        Assert.Equal(20, result.Views[0].FadeStart); Assert.Equal(40, result.Views[0].FadeEnd);
        Assert.Equal(SettingSource.EditorAll, result.Views[0]["fade.start"].Source);
        Assert.True(result.Views[0].Fade); Assert.Single(result.Diagnostics);
    }

    [Fact]
    public void DuplicateViewNamesNeedIndexAndInvalidHeaderCannotApplyToPreviousView()
    {
        var result = Resolve("[A]\nscale=20\n[#1]\nscale=50\n[]\nhidden=off", View("A"), View("A", "b"));
        Assert.Equal(100, result.Views[0].Scale); Assert.Equal(50, result.Views[1].Scale);
        Assert.All(result.Views, s => Assert.True(s.Hidden)); Assert.Equal(2, result.Diagnostics.Length);
    }

    [Fact]
    public void CopyEffectiveInstructionsRoundTripsAllKeysIncludingQuotesFeetAndComments()
    {
        var contexts = new[] { View(), View("Roof # [2]", "b", false, ViewSourceKind.NamedView) };
        var original = Resolve("depth=10'\ngap=6\"\nname=\"Drawing # \\\"A\\\" \\\\ west\"\n[BAD]\nscale=20\n[#1]\nscale=200\ncrop=off", contexts);
        var copied = Settings.CopyAsText(original.Views);
        var roundtrip = Settings.Resolve(contexts, [new[] { copied }], DocumentUnits.Metres);
        Assert.Empty(roundtrip.Diagnostics);
        for (var i = 0; i < contexts.Length; i++)
            foreach (var key in Settings.Keys) Assert.Equal(original.Views[i][key].Value, roundtrip.Views[i][key].Value);
        Assert.Equal("Drawing # \"A\" \\ west", roundtrip.Views[0].Name);
    }

    [Fact]
    public void CopyEditorPreservesInheritanceAndEscapedViewNames()
    {
        var contexts = new[] { View("Roof # [2]", "a"), View("B", "b") };
        var editor = new EditorSettings(Row(("hidden", "off")), new[] { KeyValuePair.Create("a", Row(("name", "A #1"), ("scale", "50"))) });
        var text = Settings.CopyEditorAsText(editor, contexts);
        var result = Settings.Resolve(contexts, [new[] { text }], DocumentUnits.Metres);
        Assert.Empty(result.Diagnostics); Assert.Equal("A #1", result.Views[0].Name);
        Assert.Equal(50, result.Views[0].Scale); Assert.Equal(100, result.Views[1].Scale);
        Assert.All(result.Views, s => Assert.False(s.Hidden));
    }

    [Fact]
    public void HeadersAllowCommentsContainingBracketsAndWhitespaceAndCrLineEndings()
    {
        var result = Resolve("[Section A] # comment with ]\rhidden=off\r[#0]\rseams=on");
        Assert.Empty(result.Diagnostics); Assert.False(result.Views[0].Hidden); Assert.True(result.Views[0].Seams);
    }

    [Fact]
    public void UnknownUnitsSuggestClosestSupportedSuffix()
    {
        var result = Resolve("depth=3 mt\nhidden=off");
        Assert.Contains("Did you mean", Assert.Single(result.Diagnostics).Message);
        Assert.False(result.Views[0].Hidden);
    }

    [Fact]
    public void MisspelledAutomaticDistanceInstructionsHaveEditDistanceSuggestions()
    {
        var result = Resolve("depth=atuo\nbeyond=of\ngap=ato");
        Assert.Equal(3, result.Diagnostics.Length);
        Assert.Contains(result.Diagnostics, d => d.Key == "depth" && d.Message.Contains("Did you mean 'auto'"));
        Assert.Contains(result.Diagnostics, d => d.Key == "beyond" && d.Message.Contains("Did you mean 'off'"));
        Assert.Contains(result.Diagnostics, d => d.Key == "gap" && d.Message.Contains("Did you mean 'auto'"));
    }

    [Fact]
    public void AutomaticSourceDepthAppliesOnlyToBoxAndClippingPlane()
    {
        var contexts = new[] { View(source: ViewSourceKind.Plane) with { SourceDepth = 8 },
            View("CP", "cp", source: ViewSourceKind.ClippingPlane) with { SourceDepth = 8 } };
        var result = Settings.Resolve(contexts, [], DocumentUnits.Metres);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(20, result.Views[0].Depth); Assert.Equal(8, result.Views[1].Depth);
    }

    [Fact]
    public void BeyondAtDepthIsRemarkUnlessNoFarClipAndRectangleCannotDisableCrop()
    {
        var clipped = Resolve("depth=3\nbeyond=3\ncrop=off");
        Assert.Null(clipped.Views[0].Beyond); Assert.True(clipped.Views[0].Crop);
        Assert.Equal(2, clipped.Diagnostics.Length);
        var unclipped = Resolve("depth=3\nbeyond=4\nfarclip=none");
        Assert.Empty(unclipped.Diagnostics); Assert.Equal(4, unclipped.Views[0].Beyond);
    }

    [Fact]
    public void ZeroAndAutoDepthAreValidButInvalidHostMetadataIsRejected()
    {
        Assert.Equal(0, Resolve("depth=0").Views[0].Depth);
        Assert.Equal(0, Settings.Resolve([View(source: ViewSourceKind.ClippingPlane) with { SourceDepth = 0 }], [], DocumentUnits.Metres).Views[0].Depth);
        Assert.Throws<ArgumentException>(() => Settings.Resolve([View() with { SourceDepth = -1 }], [], DocumentUnits.Metres));
    }
}
