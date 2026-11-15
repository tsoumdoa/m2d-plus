using M2DPlus.Core;
using M2DPlus.Grasshopper.UI.Model;
using Xunit;

namespace M2DPlus.Grasshopper.Tests;

public sealed class PersistenceTests
{
    private static SettingsRow Row(params (string Key, string Value)[] values) => new(values.Select(v => KeyValuePair.Create(v.Key, v.Value)));

    [Fact]
    public void DefaultStateIsCompactAndReadsBack()
    {
        var text = UiStateSerializer.Write(ComponentUiState.Default);
        Assert.Equal("{\"v\":1}", text);
        var read = UiStateSerializer.Read(text, out var problem);
        Assert.Null(problem);
        Assert.True(read.AutoRun);
        Assert.False(read.HasEditorValues);
        Assert.True(read.Preview.SetEquals(PreviewLayers.Default));
    }

    [Fact]
    public void EverythingRoundTripsIncludingUnknownFutureData()
    {
        var state = new ComponentUiState(Row(("scale", "1:200"), ("hidden", "off"), ("future.key", "x")),
            [KeyValuePair.Create("text:section a", Row(("depth", "40 m"), ("name", "Sección \"A\" #1"))),
             KeyValuePair.Create("index:2", Row(("farclip", "line")))],
            autoRun: false, preview: ["hidden", "frame", "contours"],
            extras: [KeyValuePair.Create("layout", "{\"columns\":[1,2],\"on\":true}")]);
        var text = UiStateSerializer.Write(state);
        var read = UiStateSerializer.Read(text, out var problem);
        Assert.Null(problem);
        Assert.Equal(text, UiStateSerializer.Write(read));
        Assert.False(read.AutoRun);
        Assert.Equal("x", read.AllViews.Values["future.key"]);
        Assert.Contains("contours", read.Preview);
        Assert.Equal("Sección \"A\" #1", read.Row("text:section a").Values["name"]);
        Assert.Contains("\"layout\":{\"columns\":[1,2],\"on\":true}", text);
    }

    [Fact]
    public void UnknownKeysAreKeptButNeverResolved()
    {
        var state = ComponentUiState.Default.With("*", "future.key", "x").With("*", "hidden", "off");
        var editor = state.ToEditorSettings();
        Assert.False(editor.AllViews.Values.ContainsKey("future.key"));
        var view = new ViewSettingsContext("v", "V", ViewSourceKind.Plane, false, 10, .001);
        var resolution = Settings.Resolve([view], [], DocumentUnits.Metres, editor);
        Assert.Empty(resolution.Diagnostics);
        Assert.False(resolution.Views[0].Hidden);
        Assert.Equal("x", state.AllViews.Values["future.key"]);
    }

    [Fact]
    public void NewerVersionIsReadWithANoteAndKeepsItsExtras()
    {
        var read = UiStateSerializer.Read("{\"v\":7,\"all\":{\"hidden\":\"off\"},\"auto\":false,\"v7only\":[\"a\"]}", out var problem);
        Assert.Contains("newer M2D+", problem);
        Assert.Equal("off", read.AllViews.Values["hidden"]);
        Assert.False(read.AutoRun);
        Assert.Contains("\"v7only\":[\"a\"]", UiStateSerializer.Write(read));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("{\"all\":{}}")]
    [InlineData("{\"v\":1,\"all\":{\"Depth\":\"1\",\"depth\":\"2\"}}")]
    public void UnreadableStateFallsBackToDefaultsWithoutThrowing(string text)
    {
        var read = UiStateSerializer.Read(text, out var problem);
        Assert.NotNull(problem);
        Assert.Equal(UiStateSerializer.Write(ComponentUiState.Default), UiStateSerializer.Write(read));
    }

    [Fact]
    public void EmptyRowsAreDroppedAndClearRowRestoresDefaults()
    {
        var state = ComponentUiState.Default.With("text:a", "depth", "4 m").With("text:a", "depth", null);
        Assert.Empty(state.Views);
        state = state.With("*", "scale", "1:50").ClearRow("*");
        Assert.False(state.HasEditorValues);
    }
}
