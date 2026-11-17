using M2DPlus.Grasshopper.UI.Model;
using Xunit;

namespace M2DPlus.Grasshopper.Tests;

public sealed class VisibilityKeyUiTests
{
    [Theory]
    [InlineData("exact", "tree", "auto", "layer", false)]
    [InlineData("exact", "tree", "layer", "object", false)]
    [InlineData("fast", "tree", "auto", "object", false)]
    [InlineData("exact", "min_visible", "2 mm", "5 mm", false)]
    [InlineData("exact", "min_visible", "2 mm", "off", false)]
    [InlineData("fast", "min_visible", "2 mm", "5 mm", false)]
    [InlineData("exact", "hidden", "on", "off", false)]
    [InlineData("fast", "hidden", "on", "off", true)]
    public void MenuChangesUseTheActualVisibilityKey(string engine, string key, string from, string to, bool changesSolve)
    {
        var state = ComponentUiState.Default.With("engine", engine).With(key, from);
        var before = new SettingsPresentation(state, PresentationTests.Context()).Resolution!;
        var after = new SettingsPresentation(state.With(key, to), PresentationTests.Context()).Resolution!;
        for (var i = 0; i < before.Views.Length; i++)
        {
            var a = before.Views[i]; var b = after.Views[i];
            Assert.NotEqual(a[key].Value, b[key].Value);
            Assert.Equal(changesSolve, a.VisibilityKey() != b.VisibilityKey());
            Assert.Equal(a.VisibilityKey(), a.ForVisibilitySolve().VisibilityKey());
            Assert.Equal(b.VisibilityKey(), b.ForVisibilitySolve().VisibilityKey());
        }
    }
}
