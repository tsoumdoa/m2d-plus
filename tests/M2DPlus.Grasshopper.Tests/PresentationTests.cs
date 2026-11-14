using M2DPlus.Core;
using M2DPlus.Grasshopper.UI.Model;
using Xunit;

namespace M2DPlus.Grasshopper.Tests;

public sealed class PresentationTests
{
    internal static readonly ViewSettingsContext A = new("text:section a", "Section A", ViewSourceKind.Rectangle, true, 50, .001);
    internal static readonly ViewSettingsContext B = new("text:section b", "Section B", ViewSourceKind.Rectangle, true, 50, .001);
    internal static UiContext Context(params string[][] branches) => new([A, B], branches, DocumentUnits.Metres);

    [Fact]
    public void PrecedenceIsInputThenViewRowThenAllViewsThenDefault()
    {
        var state = ComponentUiState.Default.With("*", "hidden", "off").With(A.Identity, "depth", "40 m");
        var view = new SettingsPresentation(state, Context());
        var depthA = view.Field(A.Identity, "depth");
        Assert.Equal(("40 m", FieldOrigin.Row, ""), (depthA.Text, depthA.Origin, depthA.Note));
        var depthB = view.Field(B.Identity, "depth");
        Assert.Equal(("auto", FieldOrigin.Default, ""), (depthB.Text, depthB.Origin, depthB.Note));
        var hiddenB = view.Field(B.Identity, "hidden");
        Assert.Equal(("off", FieldOrigin.AllViews, "as All views"), (hiddenB.Text, hiddenB.Origin, hiddenB.Note));
        Assert.True(hiddenB.IsInherited);

        var wired = new SettingsPresentation(state, Context(["depth = 8 m"]));
        var locked = wired.Field(A.Identity, "depth");
        Assert.True(locked.Locked);
        Assert.Equal("8", locked.Text); // Core's authored text, in document units
        Assert.Equal("set by input", locked.Note);
        Assert.Equal("40 m", locked.RowText); // the editor value is kept, only overridden
        Assert.True(wired.Field("*", "depth").Locked);
    }

    [Fact]
    public void AllViewsRowLocksOnlyWhenEveryViewIsSetByInput()
    {
        var partial = new SettingsPresentation(ComponentUiState.Default, Context(["[#1]", "hidden = off"]));
        var all = partial.Field("*", "hidden");
        Assert.False(all.Locked);
        Assert.Equal("set by input for 1 of 2 views", all.Note);
        Assert.False(partial.Field(A.Identity, "hidden").Locked);
        Assert.True(partial.Field(B.Identity, "hidden").Locked);

        var perView = new SettingsPresentation(ComponentUiState.Default, Context(["hidden = off"], ["hidden = on"]));
        var varies = perView.Field("*", "hidden");
        Assert.True(varies.Locked);
        Assert.Equal("Varies by view", varies.Display);
    }

    [Fact]
    public void TableShowsDotsForInheritanceAndMarksInput()
    {
        var state = ComponentUiState.Default.With(A.Identity, "farclip", "line").With("*", "scale", "1:200");
        var view = new SettingsPresentation(state, Context(["fade = on"]));
        Assert.Equal("Clip with line", view.Cell(A.Identity, "farclip"));
        Assert.Equal("·", view.Cell(B.Identity, "farclip"));
        Assert.Equal("Clip without line", view.Cell("*", "farclip"));
        Assert.Equal("1:200", view.Cell("*", "scale"));
        Assert.Equal("On (input)", view.Cell(B.Identity, "fade"));
    }

    [Fact]
    public void RowsAreAllViewsThenViewsThenUnused()
    {
        var state = ComponentUiState.Default.With("text:gone", "depth", "3 m");
        var rows = new SettingsPresentation(state, Context()).Rows;
        Assert.Equal(["*", A.Identity, B.Identity, "text:gone"], rows.Select(r => r.Identity));
        Assert.Equal([RowKind.AllViews, RowKind.View, RowKind.View, RowKind.Unused], rows.Select(r => r.Kind));
        Assert.Equal("Rectangle · cuts", rows[1].Source);
    }

    [Fact]
    public void BadInputBranchCountLocksNothing()
    {
        var view = new SettingsPresentation(ComponentUiState.Default, Context(["hidden = off"], ["hidden = off"], ["hidden = off"]));
        Assert.False(view.Field(A.Identity, "hidden").Locked);
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
        var session = new EditorSession(ComponentUiState.Default, Context());
        foreach (var field in SettingsCatalog.Fields)
        {
            Assert.Contains(field.Key, Settings.Keys);
            foreach (var option in field.Options) Assert.Null(session.Validate(field.Key, option.Value));
            Assert.NotNull(SettingsPresentation.DefaultText(field.Key));
        }
    }
}
