using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true, MaxParallelThreads = 1)]

namespace M2DPlus.Rhino.Tests;

/// <summary>Opt-in at discovery time: ordinary dotnet test never initializes Rhino or its resolver.</summary>
public class RhinoFactAttribute : FactAttribute
{
    public RhinoFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("M2D_RHINO_TESTS") != "1")
            Skip = "Licensed Rhino.Inside integration: set M2D_RHINO_TESTS=1 explicitly (see README.md).";
    }
}

public sealed class RhinoPerformanceFactAttribute : RhinoFactAttribute
{
    public RhinoPerformanceFactAttribute()
    {
        if (Skip is null && Environment.GetEnvironmentVariable("M2D_PERFORMANCE_TESTS") != "1")
            Skip = "Timing benchmark: also set M2D_PERFORMANCE_TESTS=1 explicitly.";
    }
}

public sealed class RhinoScreenshotFactAttribute : RhinoFactAttribute
{
    public RhinoScreenshotFactAttribute()
    {
        if (Skip is null && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("M2D_UI_SCREENSHOTS")))
            Skip = "Screenshot generation: also set M2D_UI_SCREENSHOTS to an output folder.";
    }
}

public sealed class RhinoResearchFactAttribute : RhinoFactAttribute
{
    public RhinoResearchFactAttribute()
    {
        if (Skip is null && Environment.GetEnvironmentVariable("M2D_RESEARCH_TESTS") != "1")
            Skip = "Raw Rhino research: also set M2D_RESEARCH_TESTS=1 explicitly.";
    }
}
