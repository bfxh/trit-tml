using TritRuntime.Kernel;
using TritRuntime.Wire;
using Xunit;

namespace TritRuntime.Shim.Tests;

/// <summary>
/// 宿主与门面测试：能力协商、失败回退、影子模式、Mod.Call 契约。
/// 全部强制走托管内核，保证在没装 Rust 工具链的机器上也能跑。
/// </summary>
public sealed class RuntimeHostTests
{
    [Fact]
    public void ForceManagedNeverThrowsAndReportsReason()
    {
        using var host = RuntimeHost.Create(new RuntimeHostOptions { ForceManaged = true });
        Assert.Equal(KernelMode.Managed, host.Mode);
        Assert.NotNull(host.FallbackReason);
        Assert.Contains("托管", host.FallbackReason);
        Assert.True(host.Supports(CapabilityBits.Router));
        Assert.False(host.Supports(CapabilityBits.Ai));
    }

    [Fact]
    public void MissingNativeLibraryFallsBackInsteadOfThrowing()
    {
        using var host = RuntimeHost.Create(new RuntimeHostOptions
        {
            LibraryPath = Path.Combine(Path.GetTempPath(), "no-such-trit-rt.dll"),
            AllowDefaultProbing = false,
        });

        Assert.Equal(KernelMode.Managed, host.Mode);
        Assert.False(host.NativeLoad.Loaded);
        Assert.Contains("不可用", host.FallbackReason);
    }

    [Fact]
    public void ExplicitNativeLibraryIsUsedWhenPresent()
    {
        var path = NativeLibraryLocator.Find();
        if (path is null)
        {
            throw new InvalidOperationException(
                "本机没有 target/release/trit_rt.dll；先在仓库根目录执行 cargo build --release -p trit-rt");
        }

        using var host = RuntimeHost.Create(new RuntimeHostOptions { LibraryPath = path });
        Assert.Equal(KernelMode.Native, host.Mode);
        Assert.Null(host.FallbackReason);
        Assert.True(host.Supports(CapabilityBits.Ai));
    }

    [Fact]
    public void ShadowModeCreatesDifferAndFindsNoDivergence()
    {
        using var host = RuntimeHost.Create(new RuntimeHostOptions { ForceManaged = true, ShadowMode = true });
        Assert.NotNull(host.Differ);
        Assert.NotNull(host.ShadowKernel);

        foreach (var kernel in new[] { host.Kernel, host.ShadowKernel! })
        {
            kernel.Subscribe(1, 100, 0, Array.Empty<uint>());
            kernel.Publish(100, new[] { TritDelta.Pos });
            kernel.Step(1);
            kernel.SubmitIntent(0, 3, TritDelta.Pos);
        }

        var signature = host.Differ!.CompareWorlds();
        Assert.Empty(signature);
        Assert.Equal(0ul, host.Differ.Divergences);
        Assert.True(host.Differ.Compared > 0);
    }

    [Fact]
    public void ShadowDifferDetectsInjectedDivergence()
    {
        using var primary = new ManagedKernel();
        using var shadow = new ManagedKernel();
        using var differ = new ShadowDiffer(primary, shadow);

        primary.Subscribe(1, 100, 0, Array.Empty<uint>());
        shadow.Subscribe(1, 100, 0, Array.Empty<uint>());
        primary.Publish(100, Array.Empty<TritDelta>());
        shadow.Publish(100, Array.Empty<TritDelta>());

        differ.Run(Opcode.Step, Requests.Step(1));
        Assert.Equal(0ul, differ.Divergences);

        // 只在主内核侧发布 ⇒ 两侧状态分叉，对拍必须发现。
        primary.Publish(100, Array.Empty<TritDelta>());
        differ.Run(Opcode.Step, Requests.Step(2));
        Assert.Equal(1ul, differ.Divergences);
        var divergence = Assert.Single(differ.Recent);
        Assert.Equal(Opcode.Step, divergence.Opcode);
    }

    [Fact]
    public void ShadowDifferRecordsSignatureIntoKernelLog()
    {
        using var primary = new ManagedKernel();
        using var shadow = new ManagedKernel();
        using var differ = new ShadowDiffer(primary, shadow);

        primary.Subscribe(1, 100, 0, Array.Empty<uint>());
        primary.Publish(100, Array.Empty<TritDelta>());
        shadow.Subscribe(1, 100, 0, Array.Empty<uint>());

        differ.Run(Opcode.Step, Requests.Step(1));
        var payload = primary.Submit(Opcode.Query, Requests.Query(QueryId.Shadow)).Payload;
        var reader = new WireReader(payload);
        Assert.Equal(1ul, reader.U64()); // compared
        Assert.Equal(1ul, reader.U64()); // divergences
    }

    [Fact]
    public void LibraryFileNameIsPlatformSpecific()
    {
        var name = RuntimeHost.LibraryFileName;
        Assert.True(
            name is "trit_rt.dll" or "libtrit_rt.so" or "libtrit_rt.dylib",
            $"意外的库文件名：{name}");
    }
}

/// <summary>Mod.Call 契约测试：其他模组唯一可见的入口。</summary>
public sealed class ModCallTests : IDisposable
{
    private readonly RuntimeShim shim = RuntimeShim.Attach(new RuntimeHostOptions { ForceManaged = true });

    public void Dispose() => shim.Dispose();

    [Fact]
    public void CapabilitiesCommandReturnsReport()
    {
        var report = Assert.IsType<CapabilityReport>(shim.Call("TritRuntime.Capabilities"));
        Assert.Equal(KernelMode.Managed, report.Mode);
    }

    [Fact]
    public void CommandPrefixIsOptional()
    {
        var report = Assert.IsType<CapabilityReport>(shim.Call("Capabilities"));
        Assert.Equal(KernelMode.Managed, report.Mode);
    }

    [Fact]
    public void CommandMatchIsCaseInsensitive()
    {
        var report = Assert.IsType<CapabilityReport>(shim.Call("tritruntime.capabilities"));
        Assert.Equal(KernelMode.Managed, report.Mode);
    }

    [Fact]
    public void ModeAndFallbackReasonCommands()
    {
        Assert.Equal("Managed", shim.Call("TritRuntime.Mode"));
        var reason = Assert.IsType<string>(shim.Call("TritRuntime.FallbackReason"));
        Assert.False(string.IsNullOrEmpty(reason));
    }

    [Fact]
    public void RouterCommandsRoundTrip()
    {
        var sub = Assert.IsType<ulong>(shim.Call(
            "TritRuntime.Subscribe", 1, 100, 0, Array.Empty<uint>()));
        Assert.True(sub > 0);

        Assert.Equal(1u, shim.Call("TritRuntime.Publish", 100, new byte[] { 2, 0 }));

        var step = Assert.IsType<StepResult>(shim.Call("TritRuntime.Step", 5UL));
        var intent = Assert.Single(step.Intents);
        Assert.Equal(100u, intent.EventId);
        Assert.Equal(1u, intent.ModId);
    }

    [Fact]
    public void TransactionCommandsRoundTrip()
    {
        var txn = Assert.IsType<ulong>(shim.Call("TritRuntime.BeginTransaction"));
        shim.Call("TritRuntime.SubmitIntent", txn, 7, TritDelta.Pos);
        var commit = Assert.IsType<TxnCommitResult>(shim.Call("TritRuntime.Commit", txn));
        Assert.Equal(1u, commit.Applied);
        Assert.Throws<KernelException>(() => shim.Call("TritRuntime.RollbackTransaction", txn));
    }

    [Fact]
    public void SnapshotDiffRollbackCommandsRoundTrip()
    {
        shim.Call("TritRuntime.SubmitIntent", 0UL, 1, TritDelta.Pos);
        var snapshot = Assert.IsType<ulong>(shim.Call("TritRuntime.Snapshot"));
        shim.Call("TritRuntime.SubmitIntent", 0UL, 2, TritDelta.Pos);
        var second = Assert.IsType<ulong>(shim.Call("TritRuntime.Snapshot"));

        var diff = Assert.IsType<DiffResult>(shim.Call("TritRuntime.Diff", snapshot, second));
        var entry = Assert.Single(diff.Entries);
        Assert.Equal(2u, entry.Key);

        shim.Call("TritRuntime.Rollback", snapshot);
        var worldSize = Assert.IsType<byte[]>(shim.Call("TritRuntime.Query", QueryId.WorldSize));
        Assert.Equal(1u, new WireReader(worldSize).U32());
    }

    [Fact]
    public void ArbitrateAcceptsFlatTuplesAndIntentArray()
    {
        var flat = Assert.IsType<ArbitrateResult>(shim.Call(
            "TritRuntime.Arbitrate", 1UL, 10u, TritDelta.Pos, 2UL, 10u, TritDelta.Pos));
        Assert.False(flat.HasDisputes);

        var typed = Assert.IsType<ArbitrateResult>(shim.Call(
            "TritRuntime.Arbitrate",
            new[] { new WriteIntent(1, 10, TritDelta.Pos), new WriteIntent(2, 10, TritDelta.Pos) }));
        Assert.False(typed.HasDisputes);
    }

    [Fact]
    public void NextRandomReturnsTritBytes()
    {
        var trits = Assert.IsType<TritDelta[]>(shim.Call("TritRuntime.NextRandom", 16));
        Assert.Equal(16, trits.Length);
        Assert.All(trits, value => Assert.True((int)value <= 2));
    }

    [Fact]
    public void StatsCommandReturnsCounters()
    {
        var stats = Assert.IsType<ulong[]>(shim.Call("TritRuntime.Stats"));
        Assert.Equal(10, stats.Length);
    }

    [Fact]
    public void SubmitCommandPassesThrough()
    {
        var payload = Assert.IsType<byte[]>(shim.Call(
            "TritRuntime.Submit", Opcode.Capabilities, Array.Empty<byte>()));
        Assert.True(payload.Length >= 17);
    }

    [Fact]
    public void UnknownCommandListsValidCommands()
    {
        var error = Assert.Throws<ArgumentException>(() => shim.Call("TritRuntime.Nope"));
        Assert.Contains("TritRuntime.Capabilities", error.Message);
        Assert.Contains("TritRuntime.Rollback", error.Message);
    }

    [Fact]
    public void WrongArityIsRejected()
    {
        Assert.Throws<ArgumentException>(() => shim.Call("TritRuntime.Step"));
        Assert.Throws<ArgumentException>(() => shim.Call("TritRuntime.Diff", 1UL));
    }

    [Fact]
    public void WrongArgumentTypeIsRejected()
    {
        var error = Assert.Throws<ArgumentException>(() => shim.Call("TritRuntime.Step", "not-a-number"));
        Assert.Contains("tick", error.Message);
    }

    [Fact]
    public void StronglyTypedMethodsAgreeWithCall()
    {
        shim.Subscribe(1, 100, 0, Array.Empty<uint>());
        Assert.Equal(1u, shim.Publish(100, new[] { TritDelta.Pos }));
        var viaCall = Assert.IsType<StepResult>(shim.Call("TritRuntime.Step", 1UL));
        Assert.Equal(1u, shim.Publish(100, new[] { TritDelta.Pos }));
        var viaMethod = shim.Step(1);
        Assert.Single(viaCall.Intents);
        Assert.Equal(viaMethod.Intents.Count, viaCall.Intents.Count);
    }
}
