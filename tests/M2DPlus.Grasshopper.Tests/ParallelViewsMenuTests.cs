using M2DPlus.Core;
using M2DPlus.Grasshopper.UI.Model;
using Xunit;

namespace M2DPlus.Grasshopper.Tests;

public sealed class ParallelViewsMenuTests
{
    [Theory]
    [InlineData(1, "Auto (1),1,Custom…")]
    [InlineData(4, "Auto (1),1,2,Custom…")]
    [InlineData(8, "Auto (1),1,2,4,6,Custom…")]
    [InlineData(10, "Auto (1),1,2,4,6,8,Custom…")]
    [InlineData(16, "Auto (2),1,2,4,6,8,Custom…")]
    [InlineData(32, "Auto (4),1,2,4,6,8,Custom…")]
    public void AutoAndPresetsAreFilteredByCpuLimit(int cores, string labels)
    {
        var item = ParallelViewsMenu.Build(ParallelViewsPolicy.Resolve(cores, null, null));
        Assert.Equal("Parallel views", item.Label);
        Assert.Equal(labels.Split(','), item.Children.Select(n => n.Label));
        Assert.Equal(item.Children[0], Assert.Single(item.Children.Where(n => n.Checked)));
        Assert.All(item.Children, n => Assert.True(n.Enabled));
        Assert.Equal("parallel-views:auto", item.Children[0].Command);
        Assert.Equal("machine setting", item.Hint);
        Assert.Contains("2+ views", item.Tooltip);
        Assert.Contains("full copy of the scene", item.Tooltip);
        Assert.Contains("memory check may start fewer", item.Tooltip);
        Assert.Contains("217 s", item.Tooltip); Assert.Contains("131 s", item.Tooltip);
    }

    [Theory]
    [InlineData(4, "4")] [InlineData(8, "8")] [InlineData(3, "Custom…")] [InlineData(30, "Custom…")]
    public void CurrentPresetOrCustomIsCheckedAndEntryIsPrefilled(int value, string label)
    {
        var item = ParallelViewsMenu.Build(ParallelViewsPolicy.Resolve(32, null, value));
        Assert.Equal(label, Assert.Single(item.Children.Where(n => n.Checked)).Label);
        var entry = Assert.IsType<ParallelViewsTextEntry>(item.Children.Last().TextEntry);
        Assert.Equal(value.ToString(), entry.Text);
        Assert.Contains("whole number", entry.PromptSuffix);
        Assert.True(entry.TryAccept(" 6 ", out var command, out var error));
        Assert.Equal("parallel-views:6", command); Assert.Null(error);
        Assert.False(MenuCommands.TryParseSet(command!, out _, out _));
    }

    [Theory]
    [InlineData("0")] [InlineData("31")] [InlineData("1.5")] [InlineData("auto")]
    [InlineData("")] [InlineData("-2")] [InlineData("2\nhidden=off")]
    public void InvalidCustomEntryReturnsOneLineErrorAndNoCommand(string text)
    {
        var entry = new ParallelViewsTextEntry("4", 30);
        Assert.False(entry.TryAccept(text, out var command, out var error));
        Assert.Null(command);
        Assert.Equal("Enter a whole number from 1 to 30.", error);
    }

    [Theory]
    [InlineData("6", "6")] [InlineData("3", "3")] [InlineData("999", "30")]
    [InlineData("bad", "4")] [InlineData("", "4")] [InlineData("0", "1")]
    public void EnvironmentLocksEveryEntryAndShowsResolvedChoice(string environment, string selected)
    {
        var item = ParallelViewsMenu.Build(ParallelViewsPolicy.Resolve(32, environment, 8));
        Assert.Equal("Parallel views (locked by env)", item.Label);
        Assert.True(item.Enabled); // Open to inspect the locked choices and explanation.
        Assert.Contains("Locked by environment variable M2DPLUS_HLD_WORKERS", item.Tooltip);
        Assert.All(item.Children, n => Assert.False(n.Enabled));
        Assert.Equal(selected, Assert.Single(item.Children.Where(n => n.Checked)).Label);
        Assert.Equal(selected, item.Children.Last().TextEntry!.Text);
    }

    [Fact]
    public void MachineChoiceDoesNotEnterComponentStateSettingsTextOrDrawingIdentity()
    {
        var state = ComponentUiState.Default.With("hidden", "off");
        var context = new UiContext([new("a", "A", ViewSourceKind.Plane, false, 20, .001)], [], DocumentUnits.Millimetres);
        var presentation = new SettingsPresentation(state, context);
        var json = UiStateSerializer.Write(state);
        var text = Settings.CopyEditorAsText(state.ToEditorSettings(), context.Views);
        var key = presentation.Resolution!.Views[0].VisibilityKey();
        foreach (var count in new[] { 0, 2, 8 })
        {
            var menu = ComponentMenu.Build(new(presentation, new(DrawingPhase.Done), true,
                ParallelViews: ParallelViewsPolicy.Resolve(32, null, count)));
            Assert.Contains(menu, n => n.Label == "Parallel views");
            Assert.Equal(json, UiStateSerializer.Write(state));
            Assert.Equal(text, Settings.CopyEditorAsText(state.ToEditorSettings(), context.Views));
            Assert.Equal(key, presentation.Resolution.Views[0].VisibilityKey());
        }
        Assert.DoesNotContain("ParallelViews", json);
        Assert.DoesNotContain("parallel", text);
        Assert.DoesNotContain(Settings.Keys, k => k.Contains("parallel", StringComparison.OrdinalIgnoreCase));
    }
}
