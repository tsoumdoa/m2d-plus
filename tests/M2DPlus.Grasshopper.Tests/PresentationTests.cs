using M2DPlus.Core;
using M2DPlus.Grasshopper.UI.Model;
using Xunit;

namespace M2DPlus.Grasshopper.Tests;

public sealed class PresentationTests
{
    internal static readonly ViewSettingsContext A = new("object:a", "Section A", ViewSourceKind.Box, true, 50, .001, 12);
    internal static readonly ViewSettingsContext B = new("text:section b", "Section B", ViewSourceKind.Rectangle, true, 50, .001);
    internal static UiContext Context(params string[][] branches) => new([A, B], branches, DocumentUnits.Metres);
    private static SettingsRow Row(params (string Key, string Value)[] values) => new(values.Select(v => KeyValuePair.Create(v.Key, v.Value)));

    [Fact]
    public void MenuShowsItsOwnValueOrTheDefault()
    {
        var view = new SettingsPresentation(ComponentUiState.Default.With("hidden", "off"), Context());
        var hidden = view.Field("hidden");
        Assert.Equal(("off", FieldOrigin.Menu, "off", ""), (hidden.Text, hidden.Origin, hidden.MenuText, hidden.Note));
        var seams = view.Field("seams");
        Assert.Equal(("off", FieldOrigin.Default, null), (seams.Text, seams.Origin, seams.MenuText));
    }

    [Fact]
    public void InputLocksOnlyWhenEveryViewIsSetByInput()
    {
        var partial = new SettingsPresentation(ComponentUiState.Default, Context(["[#1]", "hidden = off"]));
        var some = partial.Field("hidden");
        Assert.False(some.Locked);
        Assert.Equal("set by input for 1 of 2 views", some.Note);

        var all = new SettingsPresentation(ComponentUiState.Default.With("hidden", "on"), Context(["hidden = off"]));
        var locked = all.Field("hidden");
        Assert.True(locked.Locked);
        Assert.Equal("off", locked.Text);
        Assert.Equal("on", locked.MenuText); // the menu value is kept, only overridden

        var perView = new SettingsPresentation(ComponentUiState.Default, Context(["hidden = off"], ["hidden = on"]));
        var varies = perView.Field("hidden");
        Assert.True(varies.Locked);
        Assert.Equal("Varies by view", varies.Display);
    }

    [Fact]
    public void ViewObjectOverridesAreReportedButNeverLock()
    {
        var context = Context() with { ViewRows = new Dictionary<string, SettingsRow> { [A.Identity] = Row(("fade", "on")) } };
        var view = new SettingsPresentation(ComponentUiState.Default, context);
        var fade = view.Field("fade");
        Assert.False(fade.Locked);
        Assert.Equal("set on 1 of 2 view objects", fade.Note);
        Assert.Equal("Depth cueing (set on 1 of 2 view objects)", ComponentMenu.Toggle(view, "Depth cueing", "fade").Label);
        Assert.True(view.Resolution!.Views[0].Fade);
        Assert.False(view.Resolution.Views[1].Fade);

        var everywhere = new SettingsPresentation(ComponentUiState.Default, Context() with
        { ViewRows = new Dictionary<string, SettingsRow> { [A.Identity] = Row(("cuts", "merge")), [B.Identity] = Row(("cuts", "merge")) } });
        Assert.Equal(FieldOrigin.ViewObjects, everywhere.Field("cuts").Origin);
        Assert.Equal("set on every view object", everywhere.Field("cuts").Note);
    }

    [Fact]
    public void DepthInputReachesTheResolutionAndBoxesKeepTheirDepth()
    {
        var view = new SettingsPresentation(ComponentUiState.Default, Context() with { Depth = ["8 m"] });
        Assert.Equal(12, view.Resolution!.Views[0].Depth);
        Assert.Equal(8, view.Resolution.Views[1].Depth);
        Assert.Contains(view.Resolution.Diagnostics, d => d.Severity == DiagnosticSeverity.Remark && d.ViewIndex == 0);
    }

    [Fact]
    public void BadInputBranchCountLocksNothing()
    {
        var view = new SettingsPresentation(ComponentUiState.Default, Context(["hidden = off"], ["hidden = off"], ["hidden = off"]));
        Assert.False(view.Field("hidden").Locked);
    }

    [Theory]
    [InlineData("hidden", "TRUE", "on")]
    [InlineData("scale", "100", "1:100")]
    [InlineData("fade.limit", "25", "25 %")]
    [InlineData("farclip", "Line", "line")]
    [InlineData("name", "\"A #1\"", "A #1")]
    public void CanonicalTextMatchesCoreSyntax(string key, string text, string expected) =>
        Assert.Equal(expected, SettingsCatalog.Canonical(key, text));

    [Fact]
    public void WeightStripFollowsTheLineWeightPreset()
    {
        Assert.Equal("Cut 0.50 · Profile 0.35 · Visible 0.18 · Hidden 0.13 · Beyond 0.09 mm (1:100 preset)", SettingsCatalog.WeightStrip("1:100", "auto"));
        Assert.EndsWith("(1:100 preset)", SettingsCatalog.WeightStrip("1:75", "auto"));
        Assert.StartsWith("Cut 0.35", SettingsCatalog.WeightStrip("1:150", "auto"));
        Assert.StartsWith("Cut 0.70", SettingsCatalog.WeightStrip("20", "auto"));
        Assert.StartsWith("Line weights from source layers", SettingsCatalog.WeightStrip("1:100", "layers"));
    }

    [Fact]
    public void EveryCatalogFieldIsACoreKeyAndEveryOptionParses()
    {
        var probe = new ViewSettingsContext("p", "P", ViewSourceKind.Plane, true, 1, .001);
        foreach (var field in SettingsCatalog.Fields)
        {
            Assert.Contains(field.Key, Settings.Keys);
            foreach (var option in field.Options)
                Assert.Empty(Settings.Resolve([probe], [[$"{field.Key} = {option.Value}"]], DocumentUnits.Metres).Diagnostics
                    .Where(d => d.Severity != DiagnosticSeverity.Remark));
            Assert.NotNull(SettingsPresentation.DefaultText(field.Key));
        }
    }
}
