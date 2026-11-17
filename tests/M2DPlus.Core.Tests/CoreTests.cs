using M2DPlus.Core;
using Xunit;

namespace M2DPlus.Core.Tests;

public sealed class CoreTests
{
    [Fact]
    public void CoreHasNoRhinoOrGrasshopperDependency()
    {
        Assert.DoesNotContain(typeof(Settings).Assembly.GetReferencedAssemblies(),
            a => a.Name!.StartsWith("Rhino", StringComparison.OrdinalIgnoreCase) || a.Name.StartsWith("Grasshopper", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BranchJoinsPanelItemsAndPreservesHeadersCommentsAndAssignmentOrder()
    {
        var parsed = Settings.Parse(new[] { "# Defaults\n HIDDEN = off # comment", "[Section A]\ndepth = 8 m", "[#2] # index\ndepth=12'", "[*]\nHidden = on" });
        Assert.Equal(4, parsed.Count);
        Assert.Equal(new Settings.Assignment("*", "hidden", "off", 2), parsed[0]);
        Assert.Equal(new Settings.Assignment("Section A", "depth", "8 m", 4), parsed[1]);
        Assert.Equal("#2", parsed[2].View);
        Assert.Equal("*", parsed[3].View);
        Assert.Equal("on", parsed[3].Value);
    }

    [Theory]
    [InlineData("depth")]
    [InlineData("= 8 m")]
    [InlineData("depth = # missing")]
    [InlineData("[]")]
    [InlineData("[ ]")]
    [InlineData("[Front] junk")]
    public void MalformedSettingsReportLine(string text)
    {
        var error = Assert.Throws<FormatException>(() => Settings.Parse(new[] { "", text }));
        Assert.Contains("Line 2:", error.Message);
    }
}
