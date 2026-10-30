#nullable enable

using TritRuntime.Kernel;
using Xunit;

namespace TritRuntime.Shim.Tests;

/// <summary>
/// 托管内核的资源预算记账集成测试：提交/完成计数、owner 隔离、会话复位保留跨存档累计。
/// 这是原生 Rust <c>budget.rs</c> 的同语义契约在 C# 回退内核上的镜像，parity 的关键面。
/// </summary>
public sealed class BudgetTests
{
    private static ScanRequest Histogram(string owner, byte[] trits) => new()
    {
        Owner = owner,
        Kind = ScanKind.Histogram,
        Trits = trits,
    };

    [Fact]
    public void SubmittedAndCompletedAreCounted()
    {
        using var kernel = new ManagedKernel();
        const int count = 20;
        for (var i = 0; i < count; i++)
        {
            kernel.SubmitScan(Histogram("alpha", new byte[] { 0, 1, 2, 1, 0 }));
        }

        // 同步执行：提交即完成，结果全量可取回。
        var results = kernel.PollJobs(owner: "alpha");
        Assert.Equal(count, results.Count);

        var totals = kernel.Budget().Totals;
        Assert.Equal((ulong)count, totals.Submitted);
        Assert.Equal((ulong)count, totals.Completed);
    }

    [Fact]
    public void OwnerLedgersAreIsolated()
    {
        using var kernel = new ManagedKernel();
        const int alphaCount = 10;
        const int betaCount = 15;
        for (var i = 0; i < alphaCount; i++)
        {
            kernel.SubmitScan(Histogram("alpha", new byte[] { 2, 2, 2 }));
        }

        for (var i = 0; i < betaCount; i++)
        {
            kernel.SubmitScan(Histogram("beta", new byte[] { 0, 0, 0 }));
        }

        var snapshot = kernel.Budget();
        Assert.Equal((ulong)(alphaCount + betaCount), snapshot.Totals.Submitted);
        Assert.Equal((ulong)(alphaCount + betaCount), snapshot.Totals.Completed);

        // 恰好两个 owner，各自提交数互不串味。
        Assert.Equal(2, snapshot.Mods.Count);
        var alpha = snapshot.Mods.Single(m => m.Owner == "alpha");
        var beta = snapshot.Mods.Single(m => m.Owner == "beta");
        Assert.Equal((ulong)alphaCount, alpha.Submitted);
        Assert.Equal((ulong)betaCount, beta.Submitted);
    }

    [Fact]
    public void TotalsEqualSumOfModLedgers()
    {
        using var kernel = new ManagedKernel();
        var owners = new[] { "a", "b", "c", "d" };
        foreach (var owner in owners)
        {
            for (var i = 0; i < owner.Length + 3; i++)
            {
                kernel.SubmitScan(Histogram(owner, new byte[] { 1 }));
            }
        }

        var snapshot = kernel.Budget();
        var sum = snapshot.Mods.Aggregate(0UL, (acc, m) => acc + m.Submitted);
        Assert.Equal(snapshot.Totals.Submitted, sum);
    }

    [Fact]
    public void ResetSessionPreservesCumulativeTotals()
    {
        using var kernel = new ManagedKernel();
        for (var i = 0; i < 5; i++)
        {
            kernel.SubmitScan(Histogram("alpha", new byte[] { 1, 2 }));
        }

        // 复位清空结果队列，但提交计数跨会话保留（设计约定）。
        kernel.ResetSession();
        Assert.Empty(kernel.PollJobs());
        Assert.Equal(5UL, kernel.Budget().Totals.Submitted);

        // 复位后仍可正常提交。
        kernel.SubmitScan(Histogram("alpha", new byte[] { 0 }));
        Assert.Equal(6UL, kernel.Budget().Totals.Submitted);
    }
}
