using System.Collections.Immutable;
using M2DPlus.Core;
using M2DPlus.Grasshopper.UI.Model;
using Xunit;
using static M2DPlus.Grasshopper.Tests.PresentationTests;

namespace M2DPlus.Grasshopper.Tests;

public sealed class MenuTests
{
    private static ImmutableArray<MenuNode> Menu(ComponentUiState state, DrawingPhase phase = DrawingPhase.Done, params string[][] input) =>
        ComponentMenu.Build(new(new SettingsPresentation(state, Context(input)), new(phase), HasInfo: true));
    private static MenuNode Item(ImmutableArray<MenuNode> menu, string label) => menu.First(n => n.Label.StartsWith(label, StringComparison.Ordinal));

    [Fact]
    public void ItemsFollowUxSectionSevenOrder()
    {
        var labels = Menu(ComponentUiState.Default, DrawingPhase.Running).Where(n => !n.IsSeparator).Select(n => n.Label).ToArray();
        Assert.Equal(["View range & settings…", "Bake drawing", "Bake one", "Run automatically", "Run now", "Cancel",
            "Hidden lines", "Tangent edges", "Seams", "Beyond", "Far clip", "Depth cueing", "Cuts", "Hatch", "Scale", "Line weights",
            "Preview", "Output tree", "Copy info", "Copy settings as text", "Clear drawing cache"], labels);
        Assert.DoesNotContain(Menu(ComponentUiState.Default, DrawingPhase.Done), n => n.Label == "Cancel");
    }

    [Fact]
    public void TogglesEditAllViewsAndShowDefaults()
    {
        var menu = Menu(ComponentUiState.Default);
        var hidden = Item(menu, "Hidden lines");
        Assert.True(hidden.Checked);
        Assert.Equal(MenuCommands.SetValue("hidden", "off"), hidden.Command);
        Assert.Equal("hidden = on", hidden.Hint);
        Assert.False(Item(menu, "Seams").Checked);
        Assert.Equal(["Auto"], Item(menu, "Beyond").Children.Where(c => c.Checked).Select(c => c.Label));
        Assert.Equal(["Clip without line"], Item(menu, "Far clip").Children.Where(c => c.Checked).Select(c => c.Label));
        Assert.Equal(["1:100"], Item(menu, "Scale").Children.Where(c => c.Checked).Select(c => c.Label));
        Assert.Equal(MenuCommands.Editor, Item(menu, "Scale").Children[^1].Command);
        Assert.True(MenuCommands.TryParseSet(hidden.Command!, out var key, out var value));
        Assert.Equal(("hidden", "off"), (key, value));
    }

    [Fact]
    public void TypedValuesShowAsACheckedExtraItem()
    {
        var beyond = Item(Menu(ComponentUiState.Default.With("*", "beyond", "2 m")), "Beyond");
        Assert.Equal("2 m", beyond.Children[0].Label);
        Assert.True(beyond.Children[0].Checked);
        Assert.False(beyond.Children[0].Enabled);
        Assert.DoesNotContain(beyond.Children.Skip(1), c => c.Checked);
    }

    [Fact]
    public void InputSetKeysAreGreyedWithSetByInput()
    {
        var menu = Menu(ComponentUiState.Default, DrawingPhase.Done, ["hidden = off", "farclip = line"]);
        var hidden = Item(menu, "Hidden lines");
        Assert.Equal("Hidden lines (set by input)", hidden.Label);
        Assert.False(hidden.Enabled);
        Assert.False(hidden.Checked);
        var far = Item(menu, "Far clip");
        Assert.False(far.Enabled);
        Assert.All(far.Children, c => Assert.False(c.Enabled));
        Assert.True(Item(menu, "Seams").Enabled);

        var partial = Item(Menu(ComponentUiState.Default, DrawingPhase.Done, ["[Section B]", "seams = on"]), "Seams");
        Assert.True(partial.Enabled);
        Assert.Equal("Seams (set by input for 1 of 2 views)", partial.Label);
    }

    [Fact]
    public void BakeNeedsACompleteDrawing()
    {
        Assert.False(Item(Menu(ComponentUiState.Default, DrawingPhase.Running), "Bake drawing").Enabled);
        var done = Menu(ComponentUiState.Default);
        Assert.True(Item(done, "Bake drawing").Enabled);
        Assert.Equal(["Section A", "Section B"], Item(done, "Bake one").Children.Select(c => c.Label));
        Assert.Equal(MenuCommands.BakeOne + "1", Item(done, "Bake one").Children[1].Command);
    }

    [Fact]
    public void RunAutomaticallyAndPreviewReflectState()
    {
        var state = ComponentUiState.Default.WithAutoRun(false).WithPreview(PreviewLayers.Frame, true).WithPreview(PreviewLayers.Hatch, false);
        var menu = Menu(state);
        Assert.False(Item(menu, "Run automatically").Checked);
        var preview = Item(menu, "Preview").Children.ToDictionary(c => c.Label, c => c.Checked);
        Assert.Equal(new Dictionary<string, bool> { ["Hidden"] = true, ["Beyond"] = true, ["Far clip"] = true, ["Hatch"] = false, ["Frame"] = true }, preview);
    }

    [Theory]
    [InlineData("Running", "Cancel")]
    [InlineData("Done", "Bake")]
    [InlineData("Paused", "Run")]
    [InlineData("Cancelled", "Run")]
    [InlineData("Idle", "None")]
    [InlineData("Error", "None")]
    public void TabButtonFollowsTheJob(string phase, string expected) =>
        Assert.Equal(Enum.Parse<FooterButton>(expected), RuntimeModel.Button(new(Enum.Parse<DrawingPhase>(phase))));

    [Fact]
    public void ProgressFractionSplitsPreparationAndDrawing()
    {
        Assert.Null(RuntimeModel.Fraction(new(JobPhase.Reading, 0, 0)));
        Assert.Null(RuntimeModel.Fraction(new(JobPhase.Queued, 0, 3)));
        Assert.Equal(.2, RuntimeModel.Fraction(new(JobPhase.Preparing, 1, 2))!.Value, 6);
        Assert.Equal(.4, RuntimeModel.Fraction(new(JobPhase.Drawing, 0, 3))!.Value, 6);
        Assert.Equal(.8, RuntimeModel.Fraction(new(JobPhase.Drawing, 2, 3))!.Value, 6);
        Assert.Equal(1, RuntimeModel.Fraction(new(JobPhase.Drawing, 9, 3))!.Value, 6);
    }

    [Fact]
    public void ViewMenuChoosesTextValuesAndGreysWhenWired()
    {
        var menu = ViewMenu.Build(["Level 1", "Roof"], [], ["level 1"], wired: false);
        var named = menu.First(n => n.Label == "Named views");
        Assert.True(named.Children.First(c => c.Label == "Level 1").Checked);
        Assert.Equal(ViewMenu.Toggle + "Roof", named.Children[1].Command);
        var clipping = menu.First(n => n.Label == "Clipping planes");
        Assert.False(clipping.Children.Single().Enabled);
        Assert.False(menu.First(n => n.Label == "All clipping planes").Enabled);
        var directions = menu.First(n => n.Label == "Directions").Children;
        Assert.Equal(11, directions.Length);
        Assert.True(directions[6].IsSeparator);

        var wired = ViewMenu.Build(["Level 1"], ["Section A"], [], wired: true);
        Assert.All(wired.Where(n => !n.IsSeparator), n => Assert.False(n.Enabled));
        Assert.Equal("Disconnect the wire to choose views here.", wired[0].Tooltip);
    }

    [Fact]
    public void ViewMenuEditsKeepOrderAndIgnoreCase()
    {
        Assert.Equal(["Top", "SW"], ViewMenu.ToggleValue(["Top"], "SW").ToArray());
        Assert.Equal(["SW"], ViewMenu.ToggleValue(["Top", "SW"], "top").ToArray());
        Assert.Equal(["Top", "level 1", "Roof"], ViewMenu.AddAll(["Top", "level 1"], ["Level 1", "Roof"]).ToArray());
        Assert.Equal("M2D 03", ViewMenu.NextCaptureName(["m2d 01", "M2D 02", "Level 1"]));
    }
}
