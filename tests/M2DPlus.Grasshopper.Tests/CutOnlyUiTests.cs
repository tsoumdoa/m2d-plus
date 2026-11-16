using M2DPlus.Grasshopper.UI.Model;
using Xunit;

namespace M2DPlus.Grasshopper.Tests;

public sealed class CutOnlyUiTests
{
    [Fact] public void ToggleDefaultsOffPersistsAndReflectsInputOverrides()
    {
        var context = PresentationTests.Context();
        var defaults = new SettingsPresentation(ComponentUiState.Default, context);
        var item = ComponentMenu.Toggle(defaults, "Cut only", "cutonly");
        Assert.False(item.Checked); Assert.True(item.Enabled);
        var state = ComponentUiState.Default.With("cutonly", "on");
        var copy = UiStateSerializer.Read(UiStateSerializer.Write(state), out var problem); Assert.Null(problem);
        Assert.True(ComponentMenu.Toggle(new(copy, context), "Cut only", "cutonly").Checked);
        var input = new SettingsPresentation(copy, PresentationTests.Context(["cut only=off"]));
        Assert.False(ComponentMenu.Toggle(input, "Cut only", "cutonly").Enabled);
        Assert.All(input.Resolution!.Views, v => Assert.False(v.CutOnly));
    }
}
