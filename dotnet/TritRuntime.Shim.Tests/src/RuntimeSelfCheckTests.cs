using TritRuntime.Kernel;
using Xunit;

namespace TritRuntime.Shim.Tests;

/// <summary>
/// 端到端自检（<see cref="RuntimeSelfCheck"/>）测试。
/// </summary>
/// <remarks>
/// tModLoader 模组装载时会调用同一个方法，把结果写日志。这里必须证明：
/// 托管与原生两种内核上闭环都真的走通，而不是"报告看起来像通过"。
/// </remarks>
public sealed class RuntimeSelfCheckTests : NativeKernelTestBase
{
    [Fact]
    public void ManagedKernelPassesFullLoop()
    {
        using var shim = RuntimeShim.Attach(new RuntimeHostOptions { ForceManaged = true });
        Assert.Equal(KernelMode.Managed, shim.Mode);

        SelfCheckReport report = RuntimeSelfCheck.Run(shim);

        Assert.True(report.Passed, report.Message);
        Assert.Contains("commit(applied=1", report.Message);
        Assert.Contains("rollback=ok", report.Message);
        Assert.EndsWith("=> PASS", report.Message);
    }

    [Fact]
    public void NativeKernelPassesFullLoop()
    {
        using var shim = RuntimeShim.Attach(new RuntimeHostOptions
        {
            LibraryPath = LibraryPath,
            AllowDefaultProbing = false,
        });

        Assert.Equal(KernelMode.Native, shim.Mode);

        SelfCheckReport report = RuntimeSelfCheck.Run(shim);

        Assert.True(report.Passed, report.Message);
        Assert.Contains("0xF0FF0FFF=", report.Message);
        Assert.EndsWith("=> PASS", report.Message);
    }

    [Fact]
    public void SelfCheckLeavesWorldAtBaseline()
    {
        using var shim = RuntimeShim.Attach(new RuntimeHostOptions { ForceManaged = true });

        ulong before = shim.Snapshot();
        SelfCheckReport report = RuntimeSelfCheck.Run(shim);
        Assert.True(report.Passed, report.Message);

        // 自检的探针写入必须已被回滚：基线快照与当前世界不应再有任何差异。
        ulong after = shim.Snapshot();
        DiffResult diff = shim.Diff(before, after);
        Assert.Empty(diff.Entries);
    }

    [Fact]
    public void SelfCheckIsRepeatable()
    {
        using var shim = RuntimeShim.Attach(new RuntimeHostOptions { ForceManaged = true });

        // 事务日志只追加：重复自检会继续写日志条目，但闭环本身必须一直可通过。
        for (var round = 0; round < 3; round++)
        {
            SelfCheckReport report = RuntimeSelfCheck.Run(shim);
            Assert.True(report.Passed, $"第 {round + 1} 轮：" + report.Message);
        }
    }

    [Fact]
    public void SelfCheckRejectsNullShim()
    {
        Assert.Throws<ArgumentNullException>(() => RuntimeSelfCheck.Run(null!));
    }

    [Fact]
    public void ProbeLaneDoesNotCollideWithWorldKinds()
    {
        // 探针车道高位种类固定为 0xF，模组约定世界种类只用到 1..3。
        Assert.Equal(0xFu, (RuntimeSelfCheck.ProbeLane >> 28) & 0xFu);
    }
}
