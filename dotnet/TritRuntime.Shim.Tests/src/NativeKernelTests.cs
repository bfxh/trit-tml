using System.Runtime.InteropServices;
using TritRuntime.Kernel;
using TritRuntime.Wire;
using Xunit;
using WireFormat = TritRuntime.Wire.Wire;

namespace TritRuntime.Shim.Tests;

/// <summary>
/// 原生内核定位：测试进程的工作目录不固定，按候选路径逐个探测。
/// </summary>
internal static class NativeLibraryLocator
{
    /// <summary>找 trit_rt.dll；找不到返回 null。</summary>
    public static string? Find()
    {
        var candidates = new List<string>();
        void Add(string? path)
        {
            if (!string.IsNullOrEmpty(path) && !candidates.Contains(path))
            {
                candidates.Add(path);
            }
        }

        // 仓库约定：target/release/trit_rt.dll（相对测试程序目录逐级上溯）。
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; depth < 8 && directory is not null; depth++)
        {
            Add(Path.Combine(directory.FullName, "target", "release", "trit_rt.dll"));
            directory = directory.Parent;
        }

        Add(Path.Combine(AppContext.BaseDirectory, "trit_rt.dll"));
        Add(Environment.GetEnvironmentVariable(RuntimeHost.LibraryPathEnvironmentVariable));

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}

/// <summary>原生内核测试基类：库缺失时跳过并说明原因。</summary>
public abstract class NativeKernelTestBase
{
    /// <summary>原生库路径；null 表示本机没有构建产物。</summary>
    protected static readonly string? LibraryPath = NativeLibraryLocator.Find();

    /// <summary>原生库缺失时的提示语。</summary>
    protected const string MissingLibraryHint =
        "本机没有 target/release/trit_rt.dll；先在仓库根目录执行 cargo build --release -p trit-rt";

    /// <summary>装载原生内核；库缺失时失败并给出可操作提示（不静默跳过）。</summary>
    protected static NativeKernel LoadNative(string configJson = "")
    {
        if (LibraryPath is null)
        {
            throw new InvalidOperationException(MissingLibraryHint);
        }

        return NativeKernel.Load(LibraryPath, configJson);
    }
}

/// <summary>原生内核（Rust trit_rt.dll）测试；本机没有构建产物时自动跳过。</summary>
public sealed class NativeKernelTests : NativeKernelTestBase
{
    [Fact]
    public void AbiAndVersionAreAccepted()
    {
        using var kernel = LoadNative();
        Assert.Equal(NativeKernel.ExpectedAbiVersion, NativeBindings.AbiVersion());
        Assert.True(NativeBindings.Version() >= NativeKernel.MinimumRuntimeVersion);
    }

    [Fact]
    public void CapabilitiesReportNativeMode()
    {
        using var kernel = LoadNative();
        var report = kernel.Capabilities();
        Assert.Equal(KernelMode.Native, report.Mode);
        Assert.Equal(NativeKernel.ExpectedAbiVersion, report.AbiVersion);
        Assert.True(report.Has(CapabilityBits.Router));
        Assert.True(report.Has(CapabilityBits.TxnLog));
        Assert.True(report.Has(CapabilityBits.Snapshot));
        Assert.True(report.Has(CapabilityBits.Diff));
        Assert.True(report.Has(CapabilityBits.Arbitrate));
        Assert.True(report.Has(CapabilityBits.Prng));
        Assert.True(report.Has(CapabilityBits.Codec));
        Assert.True(report.Has(CapabilityBits.Shadow));
        Assert.True(report.Has(CapabilityBits.Ai), "默认特性应启用 AI 投票");
    }

    [Fact]
    public void SubscribePublishStepRoundTrip()
    {
        using var kernel = LoadNative();
        var a = kernel.Subscribe(1, 100, 0, Array.Empty<uint>());
        var b = kernel.Subscribe(2, 101, 0, new uint[] { 100 });
        Assert.Equal(2u, kernel.Publish(100, new[] { TritDelta.Pos, TritDelta.Neg, TritDelta.Pos }));
        var dispatch = kernel.Step(1).Intents;
        Assert.Equal(new[] { a, b }, dispatch.Select(intent => intent.SubId).ToArray());
    }

    [Fact]
    public void TransactionAndSnapshotRoundTrip()
    {
        using var kernel = LoadNative();
        var txn = kernel.BeginTransaction();
        kernel.SubmitIntent(txn, 5, TritDelta.Pos);
        var commit = kernel.Commit(txn);
        Assert.Equal(1u, commit.Applied);

        var snapshot = kernel.Snapshot();
        Assert.True(snapshot > 0);
        kernel.SubmitIntent(0, 5, TritDelta.Neg);
        var diff = kernel.Diff(snapshot, kernel.Snapshot());
        var entry = Assert.Single(diff.Entries);
        Assert.Equal(5u, entry.Key);
        Assert.Equal(TritDelta.Pos, entry.Old);
        Assert.Equal(TritDelta.Zero, entry.New);
    }

    [Fact]
    public void SnapshotBytesCarryIdPrefixAndWorld()
    {
        using var kernel = LoadNative();
        kernel.Step(4);
        kernel.SubmitIntent(0, 4242, TritDelta.Pos);
        var bytes = kernel.SnapshotBytes();

        // 布局：[u64 id][世界编码]，不装帧（与 ManagedKernel 逐字节一致）。
        var reader = new WireReader(bytes);
        var id = reader.U64();
        var (tick, _, world) = WorldCodec.Decode(reader.Bytes(reader.Remaining));
        Assert.True(id > 0);
        Assert.Equal(4ul, tick);
        Assert.Equal(TritDelta.Pos, world[4242]);
    }

    [Fact]
    public void BadLibraryPathThrowsInsteadOfSilentlyFallingBack()
    {
        if (LibraryPath is null)
        {
            throw new InvalidOperationException(MissingLibraryHint);
        }

        var missing = Path.Combine(Path.GetTempPath(), "definitely-not-trit-rt.dll");
        var error = Assert.ThrowsAny<Exception>(() => NativeKernel.Load(missing));
        Assert.Contains("definitely-not-trit-rt", error.Message);
    }

    [Fact]
    public void BadHandleIsReportedAsBadHandle()
    {
        if (LibraryPath is null)
        {
            throw new InvalidOperationException(MissingLibraryHint);
        }

        // 句柄 0 永远非法（不依赖具体库实例）。
        var status = NativeBindings.Submit(0, Opcode.Capabilities, null, 0, new byte[256], 256);
        Assert.Equal(Status.BadHandle, status);
    }
}
