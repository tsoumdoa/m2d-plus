using M2DPlus.Core;
using M2DPlus.Grasshopper.UI.Model;
using Xunit;
using static M2DPlus.Grasshopper.Tests.PresentationTests;

namespace M2DPlus.Grasshopper.Tests;

/// <summary>tree = auto | layer | object in the UI model (owner decision 2026-11-15): the appended Key output, the
/// Output tree menu and old saved menu state.</summary>
public sealed class OutputTreeUiTests
{
    [Fact]
    public void ElevenOutputsWithKeyAppendedAndTheFirstTenUnchanged()
    {
        Assert.Equal(11, ComponentOutputs.All.Length);
        Assert.Equal(["Visible", "Hidden", "Beyond", "Far clip", "Cut", "Hatch", "Unresolved", "Frame", "Info", "Ignored", "Key"],
            ComponentOutputs.All.Select(o => o.Name));
        Assert.Equal(["V", "H", "B", "Fc", "C", "Ha", "?", "F", "i", "Ig", "K"], ComponentOutputs.All.Select(o => o.NickName));
        Assert.Equal([0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10], new[] { ComponentOutputs.Visible, ComponentOutputs.Hidden, ComponentOutputs.Beyond,
            ComponentOutputs.FarClip, ComponentOutputs.Cut, ComponentOutputs.Hatch, ComponentOutputs.Unresolved, ComponentOutputs.Frame,
            ComponentOutputs.Info, ComponentOutputs.Ignored, ComponentOutputs.Key });
        Assert.All(ComponentOutputs.All, o => Assert.Equal(o.Index, ComponentOutputs.All.IndexOf(o)));
        Assert.Equal(ComponentOutputs.All.Length, ComponentOutputs.All.Select(o => o.NickName).Distinct().Count());
        Assert.Contains("tree = auto | layer | object", ComponentOutputs.All[ComponentOutputs.Key].Description);
    }

    [Fact]
    public void MenuOffersAutoByLayerByObjectWithAutoAsDefault()
    {
        var item = ComponentMenu.Choice(new SettingsPresentation(ComponentUiState.Default, Context()), "Output tree", "tree");
        Assert.Equal(["Auto", "By layer", "By object"], item.Children.Select(c => c.Label));
        Assert.Equal([true, false, false], item.Children.Select(c => c.Checked));
        Assert.Equal([MenuCommands.SetValue("tree", "auto"), MenuCommands.SetValue("tree", "layer"), MenuCommands.SetValue("tree", "object")],
            item.Children.Select(c => c.Command));
        Assert.Equal("tree = auto", item.Hint);
        var menu = ComponentMenu.Build(new(new SettingsPresentation(ComponentUiState.Default, Context()), new(DrawingPhase.Done), HasInfo: true));
        Assert.Contains("tree = auto | layer | object", menu.Single(n => n.Label == "Output tree").Tooltip);
        var layer = ComponentMenu.Choice(new SettingsPresentation(ComponentUiState.Default.With("tree", "layer"), Context()), "Output tree", "tree");
        Assert.Equal([false, true, false], layer.Children.Select(c => c.Checked));
        var input = ComponentMenu.Choice(new SettingsPresentation(ComponentUiState.Default, Context(["tree = object"])), "Output tree", "tree");
        Assert.False(input.Enabled); Assert.Equal("Output tree (set by input)", input.Label);
    }

    [Theory]
    // "Per source branch" (the old default) maps to Auto; "Branch per object" stays By object. Nothing is rewritten.
    [InlineData("{\"v\":1,\"all\":{\"tree\":\"branch\"}}", "Auto", OutputTreeMode.Auto)]
    [InlineData("{\"v\":1,\"all\":{\"tree\":\"object\"}}", "By object", OutputTreeMode.Object)]
    [InlineData("{\"v\":1,\"all\":{\"hidden\":\"off\"}}", "Auto", OutputTreeMode.Auto)]
    public void MenuStateSavedWithTheOldBranchItemsLoadsAndMapsSensibly(string old, string label, OutputTreeMode mode)
    {
        var read = UiStateSerializer.Read(old, out var problem);
        Assert.Null(problem);
        Assert.Equal(old, UiStateSerializer.Write(read));
        var presentation = new SettingsPresentation(read, Context());
        var item = ComponentMenu.Choice(presentation, "Output tree", "tree");
        Assert.Equal(label, Assert.Single(item.Children, c => c.Checked).Label);
        Assert.Equal(3, item.Children.Length); // no stray "branch" entry
        Assert.All(presentation.Resolution!.Views, v => Assert.Equal(mode, v.Tree));
        Assert.Empty(presentation.Resolution.Diagnostics);
    }

    [Fact]
    public void ViewObjectsCanSetTreeAndTheMenuReportsIt()
    {
        var objects = new SettingsPresentation(ComponentUiState.Default, Context() with
        { ViewRows = new Dictionary<string, SettingsRow> { [A.Identity] = new([KeyValuePair.Create("tree", "object")]) } });
        Assert.Equal("set on 1 of 2 view objects", objects.Field("tree").Note);
        Assert.Equal([OutputTreeMode.Object, OutputTreeMode.Auto], objects.Resolution!.Views.Select(v => v.Tree));
    }
}
