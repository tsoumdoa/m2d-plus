using System.Reflection;
using M2DPlus.Core;
using Xunit;

namespace M2DPlus.Core.Tests;

public sealed class CoreTests
{
    [Fact]
    public void IdentityUsesBuiltAssemblyAndReportsRuntime()
    {
        var identity = BuildIdentity.Current;
        Assert.Equal("M2D+", identity.Product);
        Assert.Equal(typeof(BuildIdentity).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
            .InformationalVersion, identity.Version);
        Assert.Equal(0, identity.ProtocolSchema);
        Assert.StartsWith(".NET ", identity.Runtime);
        Assert.NotEqual(Guid.Empty, Guid.Parse(identity.CoreModuleId));
    }

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

    [Theory]
    [InlineData("300 cm", 1, 3)]
    [InlineData("8 m", 0.001, 8000)]
    [InlineData("2 km", 1, 2000)]
    [InlineData("5mm", 0.01, 0.5)]
    [InlineData("10'", 1, 3.048)]
    [InlineData("12\"", 1, 0.3048)]
    [InlineData("12 in", 1, 0.3048)]
    [InlineData("1 FT", 1, 0.3048)]
    [InlineData("12.5", 0.001, 12.5)]
    [InlineData("1e2 mm", 1, 0.1)]
    public void DistancesConvertToDocumentUnits(string text, double metresPerUnit, double expected)
        => Assert.Equal(expected, Settings.ParseDistance(text, metresPerUnit), 10);

    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity m")]
    [InlineData("1e999 m")]
    [InlineData("3 yards")]
    [InlineData("3,5 m")]
    public void InvalidDistancesAreRejected(string text)
        => Assert.Throws<FormatException>(() => Settings.ParseDistance(text, 1));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    public void InvalidDocumentUnitIsRejected(double metresPerUnit)
        => Assert.Throws<ArgumentOutOfRangeException>(() => Settings.ParseDistance("3 m", metresPerUnit));
}
