using M2DPlus.Core;
using Xunit;
namespace M2DPlus.Core.Tests;

public sealed class WorkerMemoryPolicyTests
{
    private const long G=WorkerMemoryPolicy.GiB;
    private static SceneCost Large=>new(326053368,118772,0,20000);
    [Theory]
    [InlineData(1,1)] [InlineData(2,1)] [InlineData(8,6)] [InlineData(16,14)] [InlineData(32,30)]
    public void CpuCeilingLeavesTwoLogicalCores(int cores,int expected) => Assert.Equal(expected,WorkerMemoryPolicy.CpuLimit(cores));
    [Fact] public void Installed32GiBClassUsesLargerBudgetDespiteReservedHardwareRam()
    {
        Assert.Equal(31*G/100*75,WorkerMemoryPolicy.Budget(new(31*G,31*G)));
        Assert.Equal(24*G/100*60,WorkerMemoryPolicy.Budget(new(24*G,24*G)));
    }

    [Theory]
    [InlineData(16,8,2,1)] [InlineData(16,16,2,1)] [InlineData(16,32,2,1)]
    [InlineData(32,8,5,2)] [InlineData(32,16,5,2)] [InlineData(32,32,5,2)]
    [InlineData(64,8,6,6)] [InlineData(64,16,12,6)] [InlineData(64,32,12,6)]
    [InlineData(128,8,6,6)] [InlineData(128,16,14,12)] [InlineData(128,32,25,12)]
    public void PolicyTableAtThreeQuartersAvailable(int ram,int cores,int mediumCount,int largeCount)
    {
        var memory=new MachineMemory(ram*G,ram*G/4*3);
        var medium=WorkerMemoryPolicy.Size(new(81535672,29696,0,5000),32,WorkerMemoryPolicy.CpuLimit(cores),memory);
        var large=WorkerMemoryPolicy.Size(Large,32,WorkerMemoryPolicy.CpuLimit(cores),memory);
        Assert.Equal(mediumCount,medium.Count);Assert.Equal(largeCount,large.Count);
        foreach(var plan in new[]{medium,large})
        {
            Assert.True(plan.Count*plan.CapBytes+plan.HostReplyBytes<=plan.BudgetBytes);
            Assert.True(2*plan.ReplyExpandedLimit+WorkerMemoryPolicy.ReplyStreamAllowance<=plan.HostReplyBytes);
            Assert.InRange(plan.HostReplyBytes,1,4*G);
        }
    }
    [Fact] public void VeryLargeSceneHasNoSixteenGiBCapOrFourWorkerCeiling()
    {
        var campus=new SceneCost(2*G,600_001,4_000_001,60_000);
        Assert.False(campus.Admitted(1,long.MaxValue));
        var p=WorkerMemoryPolicy.Size(campus,16,30,new(128*G,100*G));
        Assert.True(p.CapBytes>16*G);Assert.True(p.Count>0);Assert.Null(p.Refusal);
        var small=WorkerMemoryPolicy.Size(default,16,30,new(128*G,100*G));
        Assert.Equal(16,small.Count);
    }
    [Fact] public void LargeSceneOn64GiBSelectsFour6GiBWorkers()
    {
        var p=WorkerMemoryPolicy.Size(Large,8,4,new(64*G,40*G));
        Assert.Equal(6*G,p.RequiredBytes);Assert.Equal(4,p.Count);Assert.Equal(6*G,p.CapBytes);
        Assert.True(p.Count*p.CapBytes<=p.BudgetBytes);Assert.Null(p.Refusal);
    }
    [Theory] [InlineData(12,1)] [InlineData(8,0)] [InlineData(6,0)]
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
        Assert.Equal(3*G,p.CapBytes);Assert.Equal(1,p.Count);
        Assert.Equal(1,WorkerMemoryPolicy.Size(Large,1,4,new(64*G,40*G)).Count);
        Assert.Equal(2,WorkerMemoryPolicy.Size(Large,8,2,new(64*G,40*G)).Count);
    }
    [Fact] public void RetryAloneCanGrowOnlyWithinFreshBudget()
    {
        Assert.Equal(7*G,WorkerMemoryPolicy.RetryCap(6*G,new(16*G,12*G)));
        Assert.Equal(0,WorkerMemoryPolicy.RetryCap(6*G,new(16*G,8*G)));
        Assert.Equal(12*G,WorkerMemoryPolicy.RetryCap(6*G,new(64*G,40*G)));
    }
    [Fact] public void UnknownOrInsufficientMemoryNeverAdmitsAndOverridesStillRespectBudget()
    {
        Assert.Equal(0,WorkerMemoryPolicy.Size(Large,8,4,new(0,0)).Count);
        var p=WorkerMemoryPolicy.Size(Large,8,4,new(16*G,12*G),4*G);
        Assert.Equal(1,p.Count);Assert.Equal(4*G,p.CapBytes);
        Assert.Equal(0,WorkerMemoryPolicy.Size(Large,8,4,new(16*G,12*G),10*G).Count);
    }
    [Theory] [InlineData(16,12)] [InlineData(32,24)] [InlineData(64,48)] [InlineData(128,96)]
    public void IsolatedRetryAlsoReservesHostReceiveBuffers(int ram,int available)
    {
        var memory=new MachineMemory(ram*G,available*G);var budget=WorkerMemoryPolicy.Budget(memory);
        var cap=WorkerMemoryPolicy.RetryCap(6*G,memory);
        Assert.True(cap==0 || cap+WorkerMemoryPolicy.HostReplyBytes(budget)<=budget);
    }
}
