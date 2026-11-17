using Xunit;

namespace M2DPlus.Core.Tests;

/// <summary>UX rev. 4: Depth input &gt; Settings input &gt; view object &gt; component menu &gt; auto,
/// and a Box (GH Box or M2D+ view box) always takes its depth from its geometry.</summary>
public sealed class DepthPrecedenceTests
{
    private static ViewSettingsContext Plane(string id = "a", string name = "Plane A") => new(id, name, ViewSourceKind.Plane, true, 20, .001);
    private static ViewSettingsContext Box(string id = "b", double height = 12) => new(id, "View box B", ViewSourceKind.Box, true, 20, .001, height);
    private static SettingsRow Row(params (string Key, string Value)[] values) => new(values.Select(p => KeyValuePair.Create(p.Key, p.Value)));

    [Fact]
    public void DepthInputBeatsSettingsTextWhichBeatsAuto()
    {
        var auto = Settings.Resolve([Plane()], [], DocumentUnits.Metres).Views[0];
        Assert.Equal(20, auto.Depth); Assert.Equal(SettingSource.Auto, auto["depth"].Source);

        var text = Settings.Resolve([Plane()], [["depth = 8 m"]], DocumentUnits.Metres).Views[0];
        Assert.Equal(8, text.Depth); Assert.Equal(SettingSource.Input, text["depth"].Source);

        var both = Settings.Resolve([Plane()], [["depth = 8 m"]], DocumentUnits.Millimetres, depth: ["4 m"]);
        Assert.Empty(both.Diagnostics);
        Assert.Equal(4000, both.Views[0].Depth);
        Assert.Equal(SettingSource.DepthInput, both.Views[0]["depth"].Source);
        Assert.True(both.Views[0]["depth"].IsSetByInput);
        Assert.Equal("set by Depth input", both.Views[0]["depth"].SourceDescription);
        // A bare number is in document units, like every other distance.
        Assert.Equal(2.5, Settings.Resolve([Plane()], [], DocumentUnits.Metres, depth: ["2.5"]).Views[0].Depth);
        // Depth input "auto" restores auto even over Settings text, and still reads as set by the Depth input.
        var reset = Settings.Resolve([Plane()], [["depth = 8 m"]], DocumentUnits.Metres, depth: ["auto"]).Views[0];
        Assert.Equal(20, reset.Depth); Assert.Equal("auto (set by Depth input)", reset["depth"].SourceDescription);
    }

    [Fact]
    public void DepthInputIsOneForAllOrOnePerViewAndBlankItemsLeaveAViewUnset()
    {
        var views = new[] { Plane("a", "A"), Plane("b", "B"), Plane("c", "C") };
        Assert.All(Settings.Resolve(views, [], DocumentUnits.Metres, depth: ["5"]).Views, v => Assert.Equal(5, v.Depth));
        var each = Settings.Resolve(views, [["depth = 9"]], DocumentUnits.Metres, depth: ["1", null, "3 m"]);
        Assert.Equal([1d, 9d, 3d], each.Views.Select(v => v.Depth));
        Assert.Equal(SettingSource.Input, each.Views[1]["depth"].Source);
        var wrong = Settings.Resolve(views, [], DocumentUnits.Metres, depth: ["1", "2"]);
        Assert.Contains(wrong.Diagnostics, d => d.Severity == DiagnosticSeverity.Error && d.Message == "Give one Depth, or one per view");
        Assert.All(wrong.Views, v => Assert.Equal(20, v.Depth));
        var bad = Settings.Resolve(views, [], DocumentUnits.Metres, depth: ["1", "deep", "-2"]);
        Assert.Equal(2, bad.Diagnostics.Count(d => d.Severity == DiagnosticSeverity.Warning && d.Message.StartsWith("Invalid Depth input", StringComparison.Ordinal)));
        Assert.Equal([1d, 20d, 20d], bad.Views.Select(v => v.Depth));
        Assert.Equal(1, bad.Diagnostics.First().ViewIndex);
    }

    [Fact]
    public void BoxGeometryWinsOverEveryDepthInstructionWithOneRemark()
    {
        var editor = new EditorSettings(Row(("depth", "30")), [KeyValuePair.Create("b", Row(("depth", "25")))]);
        var result = Settings.Resolve([Plane(), Box()], [["depth = 8 m"]], DocumentUnits.Metres, editor, ["4"]);
        Assert.Equal(4, result.Views[0].Depth);
        var box = result.Views[1];
        Assert.Equal(12, box.Depth);
        Assert.Equal(SettingSource.Auto, box["depth"].Source);
        Assert.False(box["depth"].IsSetByInput);
        var remark = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticSeverity.Remark, remark.Severity);
        Assert.Equal(1, remark.ViewIndex);
        Assert.Contains("geometry sets the depth (12)", remark.Message);
        Assert.Contains("Depth input", remark.Message); Assert.Contains("Settings input", remark.Message);
        Assert.Contains("view object", remark.Message); Assert.Contains("component menu", remark.Message);
        // No instruction, no remark; auto/explicit "auto" is not an instruction worth a remark either.
        Assert.Empty(Settings.Resolve([Box()], [["depth = auto"]], DocumentUnits.Metres).Diagnostics);
    }
}
