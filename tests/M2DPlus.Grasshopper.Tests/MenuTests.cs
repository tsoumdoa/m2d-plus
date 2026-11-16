using System.Collections.Immutable;
using M2DPlus.Core;
using M2DPlus.Grasshopper.UI.Model;
using Xunit;
using static M2DPlus.Grasshopper.Tests.PresentationTests;

namespace M2DPlus.Grasshopper.Tests;

public sealed class MenuTests
{
    private static ImmutableArray<MenuNode> Menu(ComponentUiState state, DrawingPhase phase = DrawingPhase.Done, params string[][] input) =>
        ComponentMenu.Build(new(new SettingsPresentation(state, Context(input)), new(phase), HasInfo: true, Views: new(2, 1, false)));
    private static MenuNode Item(ImmutableArray<MenuNode> menu, string label) => menu.First(n => n.Label.StartsWith(label, StringComparison.Ordinal));

    [Fact]
    public void ItemsFollowUxRevisionFourSectionSevenOrderAndTheEditorIsGone()
    {
        var labels = Menu(ComponentUiState.Default, DrawingPhase.Running).Where(n => !n.IsSeparator).Select(n => n.Label).ToArray();
        Assert.Equal(["Convert to view boxes", "Flip view direction", "Create view from current viewport", "Select view objects",
            "Bake drawing", "Bake one", "Run automatically", "Run now", "Cancel",
            "Hidden lines", "Tangent edges", "Seams", "Min visible", "Beyond", "Far clip", "Depth cueing", "Cuts", "Hatch", "Scale", "Line weights",
            "Reset menu defaults", "Preview", "Output tree", "Copy info", "Copy settings as text", "Clear drawing cache"], labels);
        Assert.DoesNotContain(Menu(ComponentUiState.Default, DrawingPhase.Done), n => n.Label == "Cancel");
        var all = Flatten(Menu(ComponentUiState.Default)).ToArray();
        Assert.DoesNotContain(all, n => n.Label.Contains("editor", StringComparison.OrdinalIgnoreCase) || n.Label.Contains("settings…", StringComparison.Ordinal));
        Assert.Equal("Double-click", Item(Menu(ComponentUiState.Default), "Select view objects").Hint);
    }

    private static IEnumerable<MenuNode> Flatten(IEnumerable<MenuNode> nodes) =>
        nodes.SelectMany(n => n.IsSubmenu ? Flatten(n.Children).Prepend(n) : [n]);

    [Fact]
    public void ViewCommandsAreEnabledByWhatVReferences()
    {
        var none = ViewCommands.Build(new(0, 0, false), withSelect: true);
        Assert.Equal([false, false, true, false], none.Select(n => n.Enabled));
        Assert.Contains("Pick views in Rhino", none[0].Tooltip);
        var surfaces = ViewCommands.Build(new(2, 0, false), withSelect: true);
        Assert.Equal([true, false, true, true], surfaces.Select(n => n.Enabled));
        Assert.Contains("Convert to view boxes first", surfaces[1].Tooltip);
        var boxes = ViewCommands.Build(new(2, 2, true), withSelect: false);
        Assert.Equal([ViewCommands.Validate, ViewCommands.Flip, ViewCommands.Create], boxes.Select(n => n.Command));
        Assert.All(boxes, n => Assert.True(n.Enabled));
        Assert.Contains("wire the new box in yourself", boxes[2].Tooltip);
    }

    [Fact]
    public void ResetMenuDefaultsNeedsMenuValues()
    {
        Assert.False(Item(Menu(ComponentUiState.Default), "Reset menu defaults").Enabled);
        var reset = Item(Menu(ComponentUiState.Default.With("hidden", "off")), "Reset menu defaults");
        Assert.True(reset.Enabled);
        Assert.Equal(MenuCommands.Reset, reset.Command);
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
        Assert.Equal(["1:20", "1:50", "1:100", "1:200", "1:500"], Item(menu, "Scale").Children.Select(c => c.Label));
        Assert.Contains("0.50", Item(menu, "Scale").Tooltip);
        Assert.True(MenuCommands.TryParseSet(hidden.Command!, out var key, out var value));
        Assert.Equal(("hidden", "off"), (key, value));
    }

    [Fact]
    public void TypedValuesShowAsACheckedExtraItem()
    {
        var beyond = Item(Menu(ComponentUiState.Default.With("beyond", "2 m")), "Beyond");
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
        var preview = Item(menu, "Preview").Children.Where(c => !c.IsSeparator).ToDictionary(c => c.Label, c => c.Checked);
        Assert.Equal(new Dictionary<string, bool> { ["View frames"] = true, ["Hidden"] = true, ["Beyond"] = true, ["Far clip"] = true, ["Hatch"] = false, ["Frame"] = true, ["Ignored"] = false,
            ["Object colours"] = true, ["Ink"] = false }, preview);
    }

    [Fact]
    public void PreviewColoursAreObjectColoursByDefaultWithAnInkChoice()
    {
        var colours = Item(Menu(ComponentUiState.Default), "Preview").Children.SkipWhile(c => !c.IsSeparator).Skip(1).ToArray();
        Assert.Equal(["Object colours", "Ink"], colours.Select(c => c.Label));
        Assert.Equal([MenuCommands.PreviewObjectColours, MenuCommands.PreviewInk], colours.Select(c => c.Command));
        Assert.Equal([true, false], colours.Select(c => c.Checked));
        var ink = ComponentUiState.Default.WithPreview(PreviewLayers.Ink, true);
        Assert.Equal([false, true], Item(Menu(ink), "Preview").Children.SkipWhile(c => !c.IsSeparator).Skip(1).Select(c => c.Checked));
        // Stored as the exception: a preview set saved before the toggle existed reads as object colours.
        Assert.DoesNotContain(PreviewLayers.Ink, PreviewLayers.Default);
        var old = UiStateSerializer.Read("{\"v\":1,\"preview\":[\"views\",\"hidden\"]}", out _);
        Assert.DoesNotContain(PreviewLayers.Ink, old.Preview);
        Assert.Contains(PreviewLayers.Ink, UiStateSerializer.Read(UiStateSerializer.Write(ink), out _).Preview);
    }

    [Fact]
    public void TheComponentLivesInParamsUtil()
    {
        Assert.Equal("Params", ComponentPlacement.Tab);
        Assert.Equal("Util", ComponentPlacement.Panel);
        Assert.Equal("quarternary", ComponentPlacement.Exposure);
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

        Assert.Equal(["Pick views in Rhino…", "Convert to view boxes", "Flip view direction", "Create view from current viewport"],
            menu.TakeWhile(n => !n.IsSeparator).Select(n => n.Label));
        Assert.DoesNotContain(menu, n => n.Label.StartsWith("Capture", StringComparison.Ordinal));

        // Wired: choosing is greyed, but the view-object commands still act on the wired references.
        var wired = ViewMenu.Build(["Level 1"], ["Section A"], [], wired: true, new(1, 1, true));
        var commands = new[] { ViewCommands.Validate, ViewCommands.Flip, ViewCommands.Create };
        Assert.All(wired.Where(n => !n.IsSeparator && !commands.Contains(n.Command)), n => Assert.False(n.Enabled));
        Assert.All(wired.Where(n => commands.Contains(n.Command)), n => Assert.True(n.Enabled));
        Assert.Equal("Disconnect the wire to choose views here.", wired[0].Tooltip);
        Assert.True(ViewMenu.Build([], [], [], wired: false, storedObjects: 1).First(n => n.Label == "Clear").Enabled);
        Assert.False(ViewMenu.Build([], [], [], wired: false).First(n => n.Label == "Clear").Enabled);
    }

    [Fact]
    public void ViewMenuEditsKeepOrderAndIgnoreCase()
    {
        Assert.Equal(["Top", "SW"], ViewMenu.ToggleValue(["Top"], "SW").ToArray());
        Assert.Equal(["SW"], ViewMenu.ToggleValue(["Top", "SW"], "top").ToArray());
        Assert.Equal(["Top", "level 1", "Roof"], ViewMenu.AddAll(["Top", "level 1"], ["Level 1", "Roof"]).ToArray());
    }
}
