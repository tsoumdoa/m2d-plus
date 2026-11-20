using M2DPlus.Core;
using Xunit;

namespace M2DPlus.Core.Tests;

public sealed class ParallelViewsPolicyTests
{
    [Theory]
    [InlineData(8, null, null, 1)] [InlineData(16, null, null, 2)] [InlineData(32, null, null, 4)]
    [InlineData(32, null, 8, 8)] [InlineData(32, "6", 8, 6)]
    [InlineData(8, "999", 4, 6)] [InlineData(32, "0", 8, 1)] [InlineData(32, "-2", 8, 1)]
    [InlineData(32, "bad", 8, 4)] [InlineData(32, "", 8, 4)] [InlineData(32, "2147483648", 8, 4)]
    [InlineData(32, null, 0, 4)] [InlineData(32, null, -1, 4)] [InlineData(32, null, 31, 4)]
    [InlineData(8, null, 8, 1)] [InlineData(1, "8", 1, 1)]
    public void EnvironmentThenValidPersistedThenAutoWithCpuCeiling(int cores, string? environment, int? persisted, int expected)
    {
        var choice = ParallelViewsPolicy.Resolve(cores, environment, persisted);
        Assert.Equal(expected, choice.Limit);
        Assert.Equal(environment is not null, choice.EnvironmentLocked);
        Assert.Equal(WorkerMemoryPolicy.CpuLimit(cores), choice.CpuLimit);
        Assert.Equal(WorkerMemoryPolicy.ThreadedHldLimit(cores), choice.AutoLimit);
        Assert.InRange(choice.Limit, 1, choice.CpuLimit);
        Assert.Equal(persisted is >= 1 && persisted <= choice.CpuLimit ? persisted : null, choice.PersistedValue);
    }

    [Theory]
    [InlineData("1", true)] [InlineData("30", true)] [InlineData(" 6 ", true)]
    [InlineData("0", false)] [InlineData("31", false)] [InlineData("-1", false)]
    [InlineData("1.5", false)] [InlineData("1e1", false)] [InlineData("+2", false)]
    [InlineData("", false)] [InlineData("auto", false)] [InlineData("2147483648", false)]
    [InlineData("2\nhidden=off", false)]
    public void CustomRequiresAnIntegerWithinCpuCeiling(string text, bool expected)
    {
        Assert.Equal(expected, ParallelViewsPolicy.TryValidate(text, 30, out var value, out var error));
        if (expected) { Assert.Null(error); Assert.InRange(value, 1, 30); }
        else Assert.Equal("Enter a whole number from 1 to 30.", error);
    }
}
