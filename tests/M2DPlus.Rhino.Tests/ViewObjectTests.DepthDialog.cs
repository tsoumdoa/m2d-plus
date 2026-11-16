using M2DPlus.Core.Views;
using Xunit;

namespace M2DPlus.Rhino.Tests;

/// <summary>The real "View box depth" Eto dialog from the GHA, built in Rhino's own Eto platform, checked without showing
/// it. Its PNGs come from tools/dialog-render (see its README).</summary>
public sealed partial class ViewObjectTests
{
    private static object NewDepthDialog(ViewDepthPrompt model) =>
        Activator.CreateInstance(Gha.GetType("M2DPlus.Grasshopper.UI.DepthDialog", true)!, model)!;

    [RhinoFact] public void TheDepthDialogFillsAutoAndKeepsItselfOpenOnInvalidInput() => host.Run(() =>
    {
        Assert.NotNull(Eto.Forms.Application.Instance); // Rhino's own Eto platform
        var single = new ViewDepthPrompt([new AutoDepth(3000, ViewBoxKind.Plan, AutoDepthRule.Storey, 4)], .001, Tol);
        using var dialog = (Eto.Forms.Dialog<DepthChoice?>)NewDepthDialog(single);
        dynamic d = dialog;
        Assert.Equal("View box depth", dialog.Title);
        Assert.Equal("3000 mm", (string)d.DepthText);
        d.DepthText = "8 parsecs";
        Assert.False((bool)d.Accept());
        Assert.Contains("Unknown distance unit: parsecs", (string)d.Error);
        d.DepthText = "8 m";             // typing clears the error
        Assert.Null((string?)d.Error);
    });
}
