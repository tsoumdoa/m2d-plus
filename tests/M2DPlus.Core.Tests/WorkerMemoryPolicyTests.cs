using M2DPlus.Core;
using Xunit;
namespace M2DPlus.Core.Tests;

public sealed class WorkerMemoryPolicyTests
{
    private const long G=WorkerMemoryPolicy.GiB;
    private static SceneCost Large=>new(326053368,118772,0,20000);
    [Fact] public void LargeSceneOn64GiBSelectsFour6GiBWorkers()
    {
        var p=WorkerMemoryPolicy.Size(Large,8,4,new(64*G,40*G));
        Assert.Equal(6*G,p.RequiredBytes);Assert.Equal(4,p.Count);Assert.Equal(6*G,p.CapBytes);
        Assert.True(p.Count*p.CapBytes<=p.BudgetBytes);Assert.Null(p.Refusal);
    }
    [Theory] [InlineData(12,1)] [InlineData(8,1)] [InlineData(6,0)]
    public void LaptopReducesCountBeforeReducingSceneCap(int available,int count)
    {
        var p=WorkerMemoryPolicy.Size(Large,8,4,new(16*G,available*G));
        Assert.Equal(count,p.Count);Assert.Equal(6*G,p.CapBytes);
        Assert.True(p.Count*p.CapBytes<=p.BudgetBytes);
        if(count==0)Assert.Contains("Close other applications",p.Refusal);
    }
    [Fact] public void AvailableMemoryAndCpuLimitBothConstrainSmallScene()
    {
        var p=WorkerMemoryPolicy.Size(new(81535672,29696,0,5000),8,4,new(16*G,8*G));
        Assert.Equal(3*G,p.CapBytes);Assert.Equal(2,p.Count);
        Assert.Equal(1,WorkerMemoryPolicy.Size(Large,1,4,new(64*G,40*G)).Count);
        Assert.Equal(2,WorkerMemoryPolicy.Size(Large,8,2,new(64*G,40*G)).Count);
    }
    [Fact] public void RetryAloneCanGrowOnlyWithinFreshBudget()
    {
        Assert.Equal(9*G,WorkerMemoryPolicy.RetryCap(6*G,new(16*G,12*G)));
        Assert.Equal(0,WorkerMemoryPolicy.RetryCap(6*G,new(16*G,8*G)));
        Assert.Equal(12*G,WorkerMemoryPolicy.RetryCap(6*G,new(64*G,40*G)));
    }
    [Fact] public void UnknownOrInsufficientMemoryNeverAdmitsAndOverridesStillRespectBudget()
    {
        Assert.Equal(0,WorkerMemoryPolicy.Size(Large,8,4,new(0,0)).Count);
        var p=WorkerMemoryPolicy.Size(Large,8,4,new(16*G,12*G),4*G);
        Assert.Equal(2,p.Count);Assert.Equal(4*G,p.CapBytes);
        Assert.Equal(0,WorkerMemoryPolicy.Size(Large,8,4,new(16*G,12*G),10*G).Count);
    }
}
