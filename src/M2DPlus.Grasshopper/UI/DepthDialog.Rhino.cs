using Eto.Forms;
using M2DPlus.Core.Views;
using Rhino;
using Window = Eto.Forms.Window;

namespace M2DPlus.Grasshopper.UI;

/// <summary>The Rhino side of the "View box depth" dialog: the menus' prompt, owned by the Rhino main window.</summary>
public sealed partial class DepthDialog
{
    /// <summary>The menus' <see cref="DepthPrompt"/>: shows the dialog modally over the Rhino main window and returns the
    /// choice, or null for Cancel. Never blocks a non-interactive host: headless Rhino, a call off the UI thread, or no
    /// visible Rhino main window answer Auto without showing anything.</summary>
    public static DepthChoice? Prompt(ViewDepthPrompt request)
    {
        if (OwnerWindow() is not { } owner) return DepthChoice.Auto;
        using var dialog = new DepthDialog(request);
        dialog.UseRhinoStyle();
        return dialog.ShowModal(owner);
    }

    private static Window? OwnerWindow()
    {
        try
        {
            if (RhinoApp.IsRunningHeadless || RhinoApp.InvokeRequired) return null;
            return global::Rhino.UI.RhinoEtoApp.MainWindow is { Visible: true } main ? main : null;
        }
        catch (Exception e) when (e is InvalidOperationException or NullReferenceException or NotSupportedException or DllNotFoundException) { return null; }
    }
}

/// <summary>Rhino's dialog style for an Eto window, when the Rhino UI is there.</summary>
internal static class RhinoStyle
{
    public static void UseRhinoStyle(this Window window)
    {
        try { global::Rhino.UI.EtoExtensions.UseRhinoStyle(window); }
        catch (Exception e) when (e is InvalidOperationException or NullReferenceException or NotSupportedException or MissingMethodException) { }
    }
}
