using M2DPlus.Core;
using M2DPlus.Grasshopper.UI.Model;
using Xunit;

namespace M2DPlus.Grasshopper.Tests;

public sealed class PipeVisibilityUiTests
{
    [Fact] public void ChoiceDefaultsToHldPersistsPipesAndHonoursInputLocks()
    {
        var context = PresentationTests.Context();
        var defaults = ComponentMenu.Choice(new(ComponentUiState.Default, context), "Visibility engine", "visibility");
        Assert.Equal(["Rhino HLD", "Pipes (experimental)"], defaults.Children.Select(n => n.Label));
        Assert.Equal([true, false], defaults.Children.Select(n => n.Checked));
        var state = ComponentUiState.Default.With("visibility", "pipes");
        var restored = UiStateSerializer.Read(UiStateSerializer.Write(state), out var error); Assert.Null(error);
        var presentation = new SettingsPresentation(restored, context);
        Assert.Equal(VisibilityMode.Pipes, presentation.Resolution!.Views[0].Visibility);
        Assert.True(ComponentMenu.Choice(presentation, "Visibility engine", "visibility").Children[1].Checked);
        var locked = ComponentMenu.Choice(new(restored, PresentationTests.Context(["visibility=hld"])), "Visibility engine", "visibility");
        Assert.False(locked.Enabled); Assert.True(locked.Children[0].Checked);
        Assert.All(locked.Children, item => Assert.False(item.Enabled));
    }
}
