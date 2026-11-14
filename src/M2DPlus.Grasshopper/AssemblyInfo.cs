using System.Drawing;
using Grasshopper.Kernel;

namespace M2DPlus.Grasshopper;

public sealed class AssemblyInfo : GH_AssemblyInfo
{
    // Stable plug-in GUID, generated once for M0; recorded in AGENTS.md and Yak manifest.
    public override Guid Id => new("acfcd722-881a-453e-a2f1-36a297b54c8d");
    public override string Name => "M2D+";
    public override string Description => "Make2D-style drawings for Rhino 8 (pre-alpha skeleton)";
    public override string AuthorName => "Tomo S";
    public override string AuthorContact => "https://github.com/tsoumdoa/m2d-plus";
    public override GH_LibraryLicense License => GH_LibraryLicense.opensource;
    public override Bitmap Icon => M2DIcons.Logo(24);
}

public sealed class M2DPlusPriority : GH_AssemblyPriority
{
    public override GH_LoadingInstruction PriorityLoad()
    {
        global::Grasshopper.Instances.ComponentServer.AddCategoryIcon("M2D+", M2DIcons.Logo(16));
        global::Grasshopper.Instances.ComponentServer.AddCategorySymbolName("M2D+", 'M');
        return GH_LoadingInstruction.Proceed;
    }
}
