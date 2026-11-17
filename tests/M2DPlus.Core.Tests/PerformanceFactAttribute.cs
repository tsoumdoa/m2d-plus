using Xunit;

namespace M2DPlus.Core.Tests;

public sealed class PerformanceFactAttribute : FactAttribute
{
    public PerformanceFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("M2D_PERFORMANCE_TESTS") != "1")
            Skip = "Timing benchmark: set M2D_PERFORMANCE_TESTS=1 explicitly.";
    }
}
