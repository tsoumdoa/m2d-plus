using System.Collections.Immutable;
using M2DPlus.Core;
using M2DPlus.Grasshopper.UI.Model;
using Xunit;
using static M2DPlus.Grasshopper.Tests.PresentationTests;

namespace M2DPlus.Grasshopper.Tests;

/// <summary>macOS has no drawing workers (docs/design/macos.md): no Parallel views; a platform self-test instead.
/// The default context is the Windows menu, unchanged.</summary>
public sealed class PlatformMenuTests
{
    private static ImmutableArray<MenuNode> Menu(bool workers, DrawingPhase phase = DrawingPhase.Done) =>
        ComponentMenu.Build(new(new SettingsPresentation(ComponentUiState.Default, Context()), new(phase), HasInfo: true,
            Views: new(2, 1, false), IsolatedWorkers: workers));
    private static string[] Labels(ImmutableArray<MenuNode> menu) => menu.Where(n => !n.IsSeparator).Select(n => n.Label).ToArray();

    [Fact]
    public void WindowsMenuIsUnchangedByDefault()
    {
        var defaults = ComponentMenu.Build(new(new SettingsPresentation(ComponentUiState.Default, Context()), new(DrawingPhase.Done), HasInfo: true, Views: new(2, 1, false)));
        Assert.Equal(Labels(defaults), Labels(Menu(workers: true)));
        Assert.Contains("Parallel views", Labels(defaults));
        Assert.DoesNotContain(defaults, n => n.Command == MenuCommands.SelfTest);
    }

    [Fact]
    public void MacMenuHidesParallelViewsAndAddsTheSelfTestLast()
    {
        var mac = Labels(Menu(workers: false));
        var windows = Labels(Menu(workers: true));
        Assert.DoesNotContain("Parallel views", mac);
        Assert.Equal(windows.Where(l => l != "Parallel views").Append("Platform self-test…"), mac);
        var item = Menu(workers: false).Last();
        Assert.Equal(MenuCommands.SelfTest, item.Command);
        Assert.True(item.Enabled);
        Assert.Contains("Windows baseline", item.Tooltip);
        Assert.False(Menu(workers: false, DrawingPhase.Running).Last().Enabled);
    }
}
