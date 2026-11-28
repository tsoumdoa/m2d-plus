using M2DPlus.Core;
using M2DPlus.Grasshopper.UI.Model;
using Xunit;

namespace M2DPlus.Grasshopper.Tests;

public sealed class PipeVisibilityUiTests
{
    [Fact] public void RasterChoicePersistsAndDisclosesApproximation()
    {
        var state = ComponentUiState.Default.With("visibility", "raster");
        var restored = UiStateSerializer.Read(UiStateSerializer.Write(state), out var error); Assert.Null(error);
        var presentation = new SettingsPresentation(restored, PresentationTests.Context());
        Assert.Equal(VisibilityMode.Raster, presentation.Resolution!.Views[0].Visibility);
        Assert.True(ComponentMenu.Choice(presentation, "Visibility engine", "visibility").Children[2].Checked);
        Assert.Contains("pixel-limited", SettingsCatalog.Fields.Single(f => f.Key == "visibility").Tooltip);
    }
    [Fact] public void ChoiceDefaultsToHldPersistsPipesAndHonoursInputLocks()
    {
        var context = PresentationTests.Context();
        var defaults = ComponentMenu.Choice(new(ComponentUiState.Default, context), "Visibility engine", "visibility");
        Assert.Equal(["Rhino HLD", "Pipes (experimental)", "Raster (experimental)"], defaults.Children.Select(n => n.Label));
        Assert.Equal([true, false, false], defaults.Children.Select(n => n.Checked));
        var state = ComponentUiState.Default.With("visibility", "pipes");
        var restored = UiStateSerializer.Read(UiStateSerializer.Write(state), out var error); Assert.Null(error);
        var presentation = new SettingsPresentation(restored, context);
        Assert.Equal(VisibilityMode.Pipes, presentation.Resolution!.Views[0].Visibility);
        Assert.True(ComponentMenu.Choice(presentation, "Visibility engine", "visibility").Children[1].Checked);
        var locked = ComponentMenu.Choice(new(restored, PresentationTests.Context(["visibility=hld"])), "Visibility engine", "visibility");
        Assert.False(locked.Enabled); Assert.True(locked.Children[0].Checked);
        Assert.All(locked.Children, item => Assert.False(item.Enabled));
    }
    [Fact] public void RasterResolutionUsesInputAndViewObjectOverridesForRunIdentity()
    {
        var input = new SettingsPresentation(ComponentUiState.Default, PresentationTests.Context(["visibility=raster"]));
        Assert.Contains(input.Resolution!.Views, v => v.Visibility == VisibilityMode.Raster);
        var context = PresentationTests.Context();
        context = context with { ViewRows = context.Views.ToDictionary(v => v.Identity,
            _ => new SettingsRow([KeyValuePair.Create("visibility", "raster")])) };
        Assert.Contains(new SettingsPresentation(ComponentUiState.Default, context).Resolution!.Views, v => v.Visibility == VisibilityMode.Raster);
        Assert.DoesNotContain(new SettingsPresentation(ComponentUiState.Default.With("visibility", "raster"),
            PresentationTests.Context(["visibility=hld"])).Resolution!.Views, v => v.Visibility == VisibilityMode.Raster);
    }

}
