using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

namespace M2DPlus.Grasshopper.Tests;

internal static class AssemblyLoad
{
    [ModuleInitializer]
    internal static void Initialize() => AssemblyLoadContext.Default.Resolving += (context, name) =>
        name.Name == "M2DPlus.Grasshopper" ? context.LoadFromAssemblyPath(Path.Combine(AppContext.BaseDirectory, "M2DPlus.Grasshopper.gha")) : null;
}
