using System.Reflection;
using System.Runtime.InteropServices;

namespace M2DPlus.Core;

/// <summary>M0 identification only; this is not yet the production worker protocol.</summary>
public sealed record BuildIdentity(string Product, string Version, int ProtocolSchema,
    string Runtime, string Architecture, string CoreModuleId)
{
    public static BuildIdentity Current { get; } = new(
        "M2D+",
        typeof(BuildIdentity).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
            .InformationalVersion,
        0,
        RuntimeInformation.FrameworkDescription,
        RuntimeInformation.ProcessArchitecture.ToString(),
        typeof(BuildIdentity).Module.ModuleVersionId.ToString("D"));
}
