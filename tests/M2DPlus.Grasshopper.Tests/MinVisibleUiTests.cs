using M2DPlus.Core;
using M2DPlus.Grasshopper.UI.Model;
using Xunit;

namespace M2DPlus.Grasshopper.Tests;

/// <summary>min visible in the UI model: the appended Ignored output, old saved state, the menu item, and the
/// guarantee that a threshold change never reaches the visibility solve's cache key (so it makes no HLD call).</summary>
public sealed class MinVisibleUiTests
{
    [Fact]
    public void TenOutputsAndTheFirstNineNeverMove()
    {
        Assert.Equal(10, ComponentOutputs.All.Length);
        Assert.Equal(["Visible", "Hidden", "Beyond", "Far clip", "Cut", "Hatch", "Unresolved", "Frame", "Info", "Ignored"],
            ComponentOutputs.All.Select(o => o.Name));
        Assert.Equal(["V", "H", "B", "Fc", "C", "Ha", "?", "F", "i", "Ig"], ComponentOutputs.All.Select(o => o.NickName));
        Assert.All(ComponentOutputs.All, o => Assert.Equal(o.Index, ComponentOutputs.All.IndexOf(o)));
        Assert.Equal(9, ComponentOutputs.Ignored);
        Assert.Equal(ComponentOutputs.All.Length, ComponentOutputs.All.Select(o => o.NickName).Distinct().Count());
    }

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

    [Theory]
    [InlineData("min_visible", "5 mm")]
    [InlineData("min_visible", "off")]
    [InlineData("hidden", "off")]
    public void ThresholdAndHiddenChangesLeaveTheVisibilitySolveKeyUnchanged(string key, string value)
    {
        // DrawingCache keys the solve on Settings.VisibilityKeys only, so an unchanged key means zero HLD calls.
        var before = new SettingsPresentation(ComponentUiState.Default.With("min_visible", "2 mm"), PresentationTests.Context()).Resolution!;
        var after = new SettingsPresentation(ComponentUiState.Default.With("min_visible", "2 mm").With(key, value), PresentationTests.Context()).Resolution!;
        for (var i = 0; i < before.Views.Length; i++)
        {
            Assert.NotEqual(before.Views[i][key].Value, after.Views[i][key].Value);
            var solveBefore = before.Views[i].ForVisibilitySolve(); var solveAfter = after.Views[i].ForVisibilitySolve();
            Assert.All(Settings.VisibilityKeys, k => Assert.Equal(solveBefore[k].Value, solveAfter[k].Value));
            Assert.Equal(solveBefore.Hidden, solveAfter.Hidden);
        }
    }
}
