using System.Text.Json;
using Xunit;

namespace M2DPlus.Core.Tests;

public sealed class PipeVisibilityTests
{
    [Theory]
    [InlineData(-5, -2, .1, -1)]
    [InlineData(2, 5, .1, 1)]
    [InlineData(-5, 2, .1, 0)]
    [InlineData(-5, -.1, .1, 0)]
    [InlineData(.1, 5, .1, 0)]
    [InlineData(0, 0, .1, 0)]
    [InlineData(2, 1, .1, 0)]
    [InlineData(double.NaN, 1, .1, 0)]
    [InlineData(0, double.PositiveInfinity, .1, 0)]
    [InlineData(1, 2, double.NaN, 0)]
    [InlineData(1, 2, -1, 0)]
    public void PlaneEnclosureOnlyCertifiesStrictSeparation(double min, double max, double margin, int expected) =>
        Assert.Equal(expected, PipeEligibility.PlaneSide(min, max, margin));

    private static EffectiveSettings Resolve(params string[] lines) => Settings.Resolve(
        [new("v", "Top", ViewSourceKind.Direction, false, 100, .001)], [lines], DocumentUnits.Millimetres).Views[0];

    [Fact] public void DefaultHldKeepsOriginalProjectionKeyAndPipesKeysRunAndCompute()
    {
        var defaults = Resolve();
        Assert.Equal(VisibilityMode.Hld, defaults.Visibility);
        Assert.Equal(defaults.VisibilityKey(), Resolve("visibility=hld").VisibilityKey());
        Assert.DoesNotContain("visibility=", defaults.VisibilityKey());
        var pipes = Resolve("visibility=PIPES");
        Assert.Equal(VisibilityMode.Pipes, pipes.Visibility);
        Assert.NotEqual(defaults.VisibilityKey(), pipes.VisibilityKey());
        Assert.Contains("visibility=pipes", pipes.VisibilityKey());
        Assert.Contains("visibility=pipes", Settings.RunText(["visibility=pipes"]));
        Assert.NotEqual(Settings.RunText(new SettingsRow([KeyValuePair.Create("visibility", "hld")])),
            Settings.RunText(new SettingsRow([KeyValuePair.Create("visibility", "pipes")])));
        var options = new JsonSerializerOptions(); options.Converters.Add(new WorkerSettingsConverter());
        var restored = JsonSerializer.Deserialize<EffectiveSettings>(JsonSerializer.Serialize(pipes, options), options)!;
        Assert.Equal(pipes.VisibilityKey(), restored.VisibilityKey());
        Assert.Equal(VisibilityMode.Pipes, restored.Visibility);
    }

    [Fact] public void InvalidEngineValueReportsWarningAndUsesDefault()
    {
        var result = Settings.Resolve([new("v", "Top", ViewSourceKind.Direction, false, 100, .001)],
            [["visibility=pipe"]], DocumentUnits.Millimetres);
        Assert.Equal(VisibilityMode.Hld, result.Views[0].Visibility);
        Assert.Contains(result.Diagnostics, d => d.Key == "visibility" && d.Message.Contains("pipes", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true, true, false, false, false, null, null)]
    [InlineData(false, true, false, false, false, null, "model units are not millimetres")]
    [InlineData(true, false, false, false, false, null, "view is not parallel")]
    [InlineData(true, true, true, false, false, null, "view has a section")]
    [InlineData(true, true, false, true, false, null, "view has a clip")]
    [InlineData(true, true, false, false, true, null, "scene has block instances")]
    [InlineData(true, true, false, false, false, "Mesh", "scene has Mesh")]
    public void AdmissionExplainsEachRefusal(bool mm, bool parallel, bool section, bool clip, bool blocks,
        string? unsupported, string? expected) =>
        Assert.Equal(expected, PipeEligibility.Reason(mm, parallel, section, clip, blocks, unsupported));
}
