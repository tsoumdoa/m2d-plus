using M2DPlus.Grasshopper.UI.Model;
using Xunit;

namespace M2DPlus.Grasshopper.Tests;

public sealed class SourceInputTests
{
    [Fact]
    public void RegistrationAppendsOptionalSourcePlaneAfterTheFiveExistingInputs()
    {
        Assert.Equal(["Geometry", "View", "Target", "Settings", "Depth", "Source"], ComponentInputs.All.Select(p => p.Name));
        Assert.Equal(["G", "V", "T", "S", "D", "Src"], ComponentInputs.All.Select(p => p.NickName));
        Assert.Equal(["tree", "list", "list", "tree", "list", "list"], ComponentInputs.All.Select(p => p.Access));
        var source = ComponentInputs.All[ComponentInputs.Source];
        Assert.Equal("Plane", source.Type); Assert.True(source.Optional);
        Assert.Contains("Orient's A", source.Description); Assert.Contains("Target (required)", source.Description);
        Assert.Equal([false, true, false, false, false, false], ComponentInputs.All.Select(p => !p.Optional));
    }

    [Fact]
    public void SourceAndTargetArePlacementOnlyAndKeepTheVisibilitySettingsUnchanged()
    {
        Assert.Equal(["Target", "Source"], ComponentInputs.All.Where(p => p.PlacementOnly).Select(p => p.Name));
        // Source/Target have no settings row or visibility key: the real cache/call-counter gate lives in
        // SurfaceSourceTests and RerunTests under licensed Rhino, where native geometry can be constructed.
        var context = PresentationTests.Context();
        var settings = new SettingsPresentation(ComponentUiState.Default, context).Resolution!;
        Assert.DoesNotContain("source", M2DPlus.Core.Settings.VisibilityKeys);
        Assert.DoesNotContain("target", M2DPlus.Core.Settings.VisibilityKeys);
        Assert.False(settings.HasErrors);
    }
}
