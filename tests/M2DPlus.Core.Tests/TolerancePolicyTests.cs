using Xunit;

namespace M2DPlus.Core.Tests;

public sealed class TolerancePolicyTests
{
    [Theory]
    [InlineData(1,.01)] [InlineData(5,.01)] [InlineData(10,.1)] [InlineData(20,.1)]
    [InlineData(49,.1)] [InlineData(50,1)] [InlineData(100,1)]
    public void ScaleAdviceUsesPhysicalMillimetres(double scale,double limit)
    {
        Assert.Null(TolerancePolicy.ScaleWarning(limit,scale,DocumentUnits.Millimetres));
        Assert.NotNull(TolerancePolicy.ScaleWarning(limit*1.01,scale,DocumentUnits.Millimetres));
        Assert.Null(TolerancePolicy.ScaleWarning(limit*.001,scale,DocumentUnits.Metres));
        Assert.NotNull(TolerancePolicy.ScaleWarning(limit*.00101,scale,DocumentUnits.Metres));
    }

    [Fact]
    public void OverrideControlsAdviceWithoutChangingDocumentTolerance()
    {
        var view=new ViewSettingsContext("a","Detail",ViewSourceKind.Direction,false,10,.1);
        var document=Settings.Resolve([view],[["scale=1:5"]],DocumentUnits.Millimetres);
        Assert.Contains(document.Diagnostics,d => d.Key=="tolerance" && d.Severity==DiagnosticSeverity.Warning);
        Assert.Equal(.1,document.Views[0].Tolerance);
        var overridden=Settings.Resolve([view],[["scale=1:5","tolerance=.001 mm"]],DocumentUnits.Millimetres);
        Assert.DoesNotContain(overridden.Diagnostics,d => d.Key=="tolerance");
        Assert.Equal(.001,overridden.Views[0].Tolerance);
        Assert.Equal(.1,view.DocumentTolerance);
    }
}
