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
        Assert.False(read.HasMenuValues);
        Assert.True(read.Preview.SetEquals(PreviewLayers.Default));
        Assert.Contains(PreviewLayers.Views, read.Preview);
    }

    [Fact]
    public void EverythingRoundTripsIncludingUnknownFutureData()
    {
        var state = new ComponentUiState(Row(("scale", "1:200"), ("hidden", "off"), ("future.key", "x")),
            autoRun: false, preview: ["hidden", "frame", "contours"],
            extras: [KeyValuePair.Create("layout", "{\"columns\":[1,2],\"on\":true}")]);
        var text = UiStateSerializer.Write(state);
        var read = UiStateSerializer.Read(text, out var problem);
        Assert.Null(problem);
        Assert.Equal(text, UiStateSerializer.Write(read));
        Assert.False(read.AutoRun);
        Assert.Equal("x", read.Menu.Values["future.key"]);
        Assert.Contains("contours", read.Preview);
        Assert.Contains("\"layout\":{\"columns\":[1,2],\"on\":true}", text);
    }

    [Fact]
    public void RemovedEditorRowsFromPreReleaseFilesAreKeptVerbatimButUnused()
    {
        const string old = "{\"v\":1,\"all\":{\"hidden\":\"off\"},\"views\":{\"text:section a\":{\"depth\":\"40 m\"}}}";
        var read = UiStateSerializer.Read(old, out var problem);
        Assert.Null(problem);
        Assert.Equal("off", read.Menu.Values["hidden"]);
        Assert.Contains("\"views\":{\"text:section a\":{\"depth\":\"40 m\"}}", UiStateSerializer.Write(read));
        var editor = read.ToEditorSettings();
        Assert.Empty(editor.Views);
    }

    [Fact]
    public void UnknownKeysAreKeptButNeverResolved()
    {
        var state = ComponentUiState.Default.With("future.key", "x").With("hidden", "off");
        var editor = state.ToEditorSettings();
        Assert.False(editor.AllViews.Values.ContainsKey("future.key"));
        var view = new ViewSettingsContext("v", "V", ViewSourceKind.Plane, false, 10, .001);
        var resolution = Settings.Resolve([view], [], DocumentUnits.Metres, editor);
        Assert.Empty(resolution.Diagnostics);
        Assert.False(resolution.Views[0].Hidden);
        Assert.Equal("x", state.Menu.Values["future.key"]);
    }

    [Fact]
    public void ViewObjectRowsJoinTheMenuRowForCore()
    {
        var rows = new Dictionary<string, SettingsRow> { ["object:1"] = Row(("hidden", "on")) };
        var editor = ComponentUiState.Default.With("hidden", "off").ToEditorSettings(rows);
        Assert.Equal("off", editor.AllViews.Values["hidden"]);
        Assert.Equal("on", editor.Views["object:1"].Values["hidden"]);
    }

    [Fact]
    public void NewerVersionIsReadWithANoteAndKeepsItsExtras()
    {
        var read = UiStateSerializer.Read("{\"v\":7,\"all\":{\"hidden\":\"off\"},\"auto\":false,\"v7only\":[\"a\"]}", out var problem);
        Assert.Contains("newer M2D+", problem);
        Assert.Equal("off", read.Menu.Values["hidden"]);
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
    public void ClearingAKeyAndResetMenuRestoreDefaults()
    {
        var state = ComponentUiState.Default.With("hidden", "off").With("hidden", null);
        Assert.False(state.HasMenuValues);
        state = state.With("scale", "1:50").WithAutoRun(false).ResetMenu();
        Assert.False(state.HasMenuValues);
        Assert.False(state.AutoRun);
    }
}
