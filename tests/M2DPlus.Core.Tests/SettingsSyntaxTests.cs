using Xunit;

namespace M2DPlus.Core.Tests;

public sealed class SettingsSyntaxTests
{
    private static ViewSettingsContext View(string name, string id) =>
        new(id, name, ViewSourceKind.Direction, false, 20, .001);

    [Theory]
    [InlineData("\"Level #1\"", "Level #1")]
    [InlineData("\"Level \\\"#1\\\"\"", "Level \"#1\"")]
    [InlineData("\"Level #1\\\\\"", "Level #1\\")]
    [InlineData("Level = 1", "Level = 1")]
    public void ParsePreservesRawNameValueWhileResolveDecodesIt(string value, string expectedName)
    {
        var text = $"name = {value} # comment with \"quotes\", ] and =";
        var assignment = Assert.Single(Settings.Parse([text]));
        Assert.Equal(new Settings.Assignment("*", "name", value, 1), assignment);

        var resolved = Settings.Resolve([View("Front", "a")], [[text]], DocumentUnits.Metres);
        Assert.Empty(resolved.Diagnostics);
        Assert.Equal(expectedName, Assert.Single(resolved.Views).Name);
    }

    [Theory]
    [InlineData("[\"Roof # [2]\"]", "Roof # [2]")]
    [InlineData("[\"Roof \\\"#2\\\" \\\\ west\"]", "Roof \"#2\" \\ west")]
    [InlineData("[\"Roof #2\\\\\"]", "Roof #2\\")]
    [InlineData("[\"Roof \\u0023\\u005B2\\u005D\"]", "Roof #[2]")]
    public void ParseAndResolveAddressTheSameDecodedViewName(string header, string viewName)
    {
        var items = new[] { header + " # comment with ] and \"", "scale = 50" };
        var assignment = Assert.Single(Settings.Parse(items));
        Assert.Equal(new Settings.Assignment(viewName, "scale", "50", 2), assignment);

        var resolved = Settings.Resolve([View("Other", "a"), View(viewName, "b")], [items], DocumentUnits.Metres);
        Assert.Empty(resolved.Diagnostics);
        Assert.Equal(100, resolved.Views[0].Scale);
        Assert.Equal(50, resolved.Views[1].Scale);
    }

    [Fact]
    public void LegacySyntaxKeepsCommentsUnitsOrderAndHeaderAddressingAcrossPanelItems()
    {
        var items = new[]
        {
            "# defaults\r\n HIDDEN = off # comment\r[#1] # index",
            "gap = 6\" # inches\n[Front]\rdepth = 10' # feet",
            "[*] # all views\n Min Visible = 2 mm # comment\n cut only = on"
        };
        var parsed = Settings.Parse(items);
        Assert.Equal(new Settings.Assignment[]
        {
            new("*", "hidden", "off", 2),
            new("#1", "gap", "6\"", 4),
            new("Front", "depth", "10'", 6),
            new("*", "min_visible", "2 mm", 8),
            new("*", "cutonly", "on", 9)
        }, parsed);

        var resolved = Settings.Resolve([View("Front", "a"), View("Back", "b")], [items], DocumentUnits.Metres);
        Assert.Empty(resolved.Diagnostics);
        Assert.All(resolved.Views, s => { Assert.False(s.Hidden); Assert.Equal(.002, s.MinVisible); Assert.True(s.CutOnly); });
        Assert.Equal(.1524, resolved.Views[1].Gap.Value, 10);
        Assert.Equal(3.048, resolved.Views[0].Depth, 10);
        Assert.Equal(20, resolved.Views[1].Depth);
    }

    [Theory]
    [InlineData("[Front")]
    [InlineData("[\"Front] # still inside string")]
    [InlineData("[\"Front\\q\"]")]
    [InlineData("[\"Front\" junk]")]
    [InlineData("[\"\"]")]
    public void InvalidHeadersKeepParseLineNumbersAndResolveDoesNotLeakAssignments(string header)
    {
        var items = new[] { "# first\r\n\r[Front]", header, "hidden = off\n[*]\nseams = on" };
        var error = Assert.Throws<FormatException>(() => Settings.Parse(items));
        Assert.StartsWith("Line 4:", error.Message);

        var resolved = Settings.Resolve([View("Front", "a")], [items], DocumentUnits.Metres);
        var diagnostic = Assert.Single(resolved.Diagnostics);
        Assert.Equal(0, diagnostic.Branch);
        Assert.Equal(4, diagnostic.Line);
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.True(resolved.Views[0].Hidden);
        Assert.True(resolved.Views[0].Seams);
    }

    [Fact]
    public void ParseRemainsSyntaxOnlyAndDoesNotBindViewsOrValidateTypedValues()
    {
        var parsed = Settings.Parse(["[Unknown view]\ncustom.key = arbitrary text\nhidden = maybe"]);
        Assert.Equal(new Settings.Assignment("Unknown view", "custom.key", "arbitrary text", 2), parsed[0]);
        Assert.Equal(new Settings.Assignment("Unknown view", "hidden", "maybe", 3), parsed[1]);
    }
}
