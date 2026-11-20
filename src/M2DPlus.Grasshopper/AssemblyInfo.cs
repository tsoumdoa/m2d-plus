using System.Drawing;
using Grasshopper.Kernel;

namespace M2DPlus.Grasshopper;

public sealed class AssemblyInfo : GH_AssemblyInfo
{
    // Stable plug-in GUID, generated once for M0; recorded in AGENTS.md and Yak manifest.
    public override Guid Id => new("acfcd722-881a-453e-a2f1-36a297b54c8d");
    public override string Name => "M2D+";
    public override string Description => "Make2D-style drawings for Rhino 8 (pre-alpha skeleton)";
    public override string AuthorName => "aectooling by tomoS";
    public override string AuthorContact => "https://github.com/tsoumdoa/m2d-plus";
    public override GH_LibraryLicense License => GH_LibraryLicense.opensource;
    public override Bitmap Icon => M2DIcons.Logo(24);
}

public sealed class M2DPlusPriority : GH_AssemblyPriority
{
    public override GH_LoadingInstruction PriorityLoad()
    {
        UI.MachineSettings.Initialize();
        // No category icon or symbol: the component lives in Params ▸ Util (owner decision 2026-11-15), so no M2D+ tab exists.
        // View box front-face fills (owner decision 2026-11-15). Never let the display stop the plug-in loading.
        try { M2DPlus.Rhino.Views.ViewBoxFaceConduit.Install(); }
        catch (Exception e) { global::Rhino.RhinoApp.WriteLine($"M2D+: view box face display is unavailable ({e.Message})."); }
        return GH_LoadingInstruction.Proceed;
    }
}
