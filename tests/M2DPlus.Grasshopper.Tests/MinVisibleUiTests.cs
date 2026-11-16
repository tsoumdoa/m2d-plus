using M2DPlus.Core;
using M2DPlus.Grasshopper.UI.Model;
using Xunit;

namespace M2DPlus.Grasshopper.Tests;

/// <summary>min visible in the UI model: old saved state and the menu item.</summary>
public sealed class MinVisibleUiTests
{
    [Theory]
    [InlineData("{\"v\":1}")]
    [InlineData("{\"v\":1,\"all\":{\"hidden\":\"off\",\"scale\":\"1:200\"},\"auto\":false,\"preview\":[\"beyond\",\"farclip\",\"hidden\",\"views\"]}")]
    public void StateSavedBeforeMinVisibleReadsUnchangedWithIgnoredOff(string old)
    {
        var read = UiStateSerializer.Read(old, out var problem);
        Assert.Null(problem);
        Assert.Equal(old, UiStateSerializer.Write(read));
        Assert.DoesNotContain(PreviewLayers.Ignored, read.Preview);
        Assert.DoesNotContain(PreviewLayers.Ignored, PreviewLayers.Default);
        var presentation = new SettingsPresentation(read, PresentationTests.Context());
        Assert.Equal(("off", FieldOrigin.Default), (presentation.Field("min_visible").Text, presentation.Field("min_visible").Origin));
        Assert.All(presentation.Resolution!.Views, v => Assert.Equal(0, v.MinVisible));
    }

    [Fact]
    public void MenuItemOffersOffAndLengthsAndReflectsOverrides()
    {
        var item = ComponentMenu.Choice(new SettingsPresentation(ComponentUiState.Default, PresentationTests.Context()), "Min visible", "min_visible");
        Assert.Equal(["Off", "1 mm", "2 mm", "5 mm", "10 mm"], item.Children.Select(c => c.Label));
        Assert.True(item.Children[0].Checked);
        Assert.Equal(MenuCommands.SetValue("min_visible", "5 mm"), item.Children[3].Command);
        Assert.Equal("min_visible = off", item.Hint);

        var set = ComponentMenu.Choice(new SettingsPresentation(ComponentUiState.Default.With("min_visible", "5 mm"), PresentationTests.Context()), "Min visible", "min_visible");
        Assert.True(set.Children.Single(c => c.Label == "5 mm").Checked);
        var typed = ComponentMenu.Choice(new SettingsPresentation(ComponentUiState.Default, PresentationTests.Context(["min visible = 3 mm"])), "Min visible", "min_visible");
        Assert.False(typed.Enabled); // set by input for every view
        Assert.Equal("0.003", typed.Children[0].Label); // document units (metres here)
        var objects = new SettingsPresentation(ComponentUiState.Default, PresentationTests.Context() with
        { ViewRows = new Dictionary<string, SettingsRow> { [PresentationTests.A.Identity] = new([KeyValuePair.Create("min_visible", "2 mm")]) } });
        Assert.Equal("set on 1 of 2 view objects", objects.Field("min_visible").Note);
        Assert.Equal(.002, objects.Resolution!.Views[0].MinVisible, 12);
    }
}
