using M2DPlus.Core.Experimental.RasterVisibility;
using M2DPlus.Rhino.Services;

namespace M2DPlus.Rhino.Experimental.RasterVisibility;

/// <summary>Only opt-in Raster run/progress history keys add this policy and exact process/assembly identity.</summary>
public static class RasterEngineIdentity
{
    public static string Value => RasterPolicy.Identity + "/" + WorkerCodec.Fingerprint();
}
