using TritRuntime.Kernel;
using TritRuntime.Wire;
using Xunit;

namespace TritRuntime.Shim.Tests;

/// <summary>
/// 原生 vs 托管逐字节对拍：同一份请求序列喂给 Rust 内核与 C# 托管内核，
/// 每一次响应都必须字节相同。这是「影子差分」的可执行证明——
/// 只有对拍全绿，才允许把热点从 C# 路径切到 Rust 旁路。
/// </summary>
public sealed class ParityTests
{
    /// <summary>构造一对内核（原生 + 托管），原生库缺失时跳过。</summary>
    private static (NativeKernel Native, ManagedKernel Managed, ShadowDiffer Differ)? Pair()
    {
        var path = NativeLibraryLocator.Find();
        if (path is null)
        {
            return null;
        }

        var native = NativeKernel.Load(path);
        var managed = new ManagedKernel();
        return (native, managed, new ShadowDiffer(native, managed));
    }

    [Fact]
    public void EveryResponseIsByteForByteIdentical()
    {
        var pair = Pair();
        if (pair is null)
        {
            return;
        }

        var (native, managed, differ) = pair.Value;
        using var _ = differ;
        try
        {
            RunScript(differ);

            Assert.True(differ.Compared > 40, $"对拍次数太少：{differ.Compared}");

            // 唯一允许的分歧：能力报告。托管回退没有 C ABI（abi=0），也不声明
            // AI 位——它做不了三值权重网络推理。除此之外必须逐字节一致。
            var divergence = Assert.Single(differ.Recent);
            Assert.Equal(Opcode.Capabilities, divergence.Opcode);
            Assert.Equal(1ul, differ.Divergences);
            Assert.Equal(
                new byte[] { 1, 0, 0, 0, 0, 0, 1, 0, 0xFF, 1, 0, 0, 0, 0, 0, 0, 1 },
                divergence.Primary.Payload);
            Assert.Equal(
                new byte[] { 0, 0, 0, 0, 0, 0, 1, 0, 0xFF, 0, 0, 0, 0, 0, 0, 0, 1 },
                divergence.Shadow.Payload);

            // 主内核的影子日志（Rust 侧 SHADOW_RECORD）：脚本里两次显式记录，
            // 加上差分器为那一处能力报告分歧注入的一次签名 ⇒ compared = 3；
            // 分歧 = 真实的那一处 + 注入的签名 ⇒ 2。
            var nativeShadow = native.Submit(Opcode.Query, Requests.Query(QueryId.Shadow)).Payload;
            var reader = new WireReader(nativeShadow);
            Assert.Equal(3ul, reader.U64());
            Assert.Equal(2ul, reader.U64());
        }
        finally
        {
            native.Dispose();
            managed.Dispose();
        }
    }

    [Fact]
    public void SnapshotBytesAreIdentical()
    {
        var pair = Pair();
        if (pair is null)
        {
            return;
        }

        var (native, managed, _) = pair.Value;
        try
        {
            BuildComparableWorld(native);
            BuildComparableWorld(managed);

            Assert.Equal(native.SnapshotBytes(), managed.SnapshotBytes());
        }
        finally
        {
            native.Dispose();
            managed.Dispose();
        }
    }

    [Fact]
    public void StatisticsAreIdentical()
    {
        var pair = Pair();
        if (pair is null)
        {
            return;
        }

        var (native, managed, _) = pair.Value;
        try
        {
            RunScriptOn(native);
            RunScriptOn(managed);
            Assert.Equal(native.Stats(), managed.Stats());
        }
        finally
        {
            native.Dispose();
            managed.Dispose();
        }
    }

    [Fact]
    public void PrngStreamsAreIdentical()
    {
        var pair = Pair();
        if (pair is null)
        {
            return;
        }

        var (native, managed, _) = pair.Value;
        try
        {
            native.Step(77);
            managed.Step(77);
            for (var tick = 0; tick < 8; tick++)
            {
                Assert.Equal(managed.NextRandom(17), native.NextRandom(17));
            }
        }
        finally
        {
            native.Dispose();
            managed.Dispose();
        }
    }

    [Fact]
    public void ConflictDiagnosticsAreIdentical()
    {
        var pair = Pair();
        if (pair is null)
        {
            return;
        }

        var (native, managed, _) = pair.Value;
        try
        {
            const string config = "{\"arbitration\":\"strict\"}";
            using var strictNative = NativeKernel.Load(NativeLibraryLocator.Find()!, config);
            using var strictManaged = new ManagedKernel(
                new ManagedConfig { Arbitration = ArbitrationMode.Strict });

            var a = strictNative.BeginTransaction();
            var b = strictNative.BeginTransaction();
            strictNative.SubmitIntent(a, 10, TritDelta.Neg);
            strictNative.SubmitIntent(b, 10, TritDelta.Pos);

            var managedA = strictManaged.BeginTransaction();
            var managedB = strictManaged.BeginTransaction();
            strictManaged.SubmitIntent(managedA, 10, TritDelta.Neg);
            strictManaged.SubmitIntent(managedB, 10, TritDelta.Pos);

            var nativeError = Assert.Throws<KernelException>(() => strictNative.Commit(a));
            var managedError = Assert.Throws<KernelException>(() => strictManaged.Commit(managedA));

            Assert.Equal(nativeError.Status, managedError.Status);
            Assert.Equal(nativeError.Disputes.Count, managedError.Disputes.Count);
            Assert.Equal(nativeError.Disputes[0].Key, managedError.Disputes[0].Key);
            Assert.Equal(nativeError.Disputes[0].Drivers.Count, managedError.Disputes[0].Drivers.Count);
            for (var i = 0; i < nativeError.Disputes[0].Drivers.Count; i++)
            {
                Assert.Equal(nativeError.Disputes[0].Drivers[i].Delta, managedError.Disputes[0].Drivers[i].Delta);
            }
        }
        finally
        {
            native.Dispose();
            managed.Dispose();
        }
    }

    /// <summary>完整的对拍脚本：覆盖全部 opcode 的成功与失败路径。</summary>
    private static void RunScript(ShadowDiffer differ)
    {
        var native = (NativeKernel)differ.Primary;
        var managed = (ManagedKernel)differ.Shadow;

        // 路由器：订阅（含依赖/优先级）、发布、step。
        differ.Run(Opcode.Subscribe, Requests.Subscribe(1, 100, 0, Array.Empty<uint>()));
        differ.Run(Opcode.Subscribe, Requests.Subscribe(2, 101, 3, new uint[] { 100 }));
        differ.Run(Opcode.Subscribe, Requests.Subscribe(3, 102, -2, new uint[] { 100, 101 }));
        differ.Run(Opcode.Subscribe, Requests.Subscribe(4, 200, 7, Array.Empty<uint>()));
        differ.Run(Opcode.Subscribe, Requests.Subscribe(5, 100, 0, Array.Empty<uint>()));
        differ.Run(Opcode.Publish, Requests.Publish(100, new[] { TritDelta.Pos, TritDelta.Neg, TritDelta.Zero }));
        differ.Run(Opcode.Publish, Requests.Publish(200, new[] { TritDelta.Pos }));
        differ.Run(Opcode.Step, Requests.Step(1));
        differ.Run(Opcode.Step, Requests.Step(2));
        differ.Run(Opcode.Publish, Requests.Publish(101, Array.Empty<TritDelta>()));
        differ.Run(Opcode.Step, Requests.Step(3));

        // 自依赖 / 成环：两侧都应拒绝。
        differ.Run(Opcode.Subscribe, Requests.Subscribe(9, 900, 0, new uint[] { 900 }));

        // 查询。
        differ.Run(Opcode.Query, Requests.Query(QueryId.DirtyCount));
        differ.Run(Opcode.Query, Requests.Query(QueryId.WorldSize));
        differ.Run(Opcode.Query, Requests.Query(QueryId.LogLen));
        differ.Run(Opcode.Query, Requests.Query(QueryId.SnapshotCount));
        differ.Run(Opcode.Query, Requests.Query(QueryId.OpenTxnCount));
        differ.Run(Opcode.Query, Requests.Query(QueryId.Shadow));
        differ.Run(Opcode.Query, Requests.Query(QueryId.EventPayload, 100));
        differ.Run(Opcode.Query, Requests.Query(QueryId.EventPayload, 200));
        differ.Run(Opcode.Query, Requests.Query(77)); // 未知查询：两侧都报参数非法

        // 事务：自动提交 + 显式事务 + 回滚 + 重复提交。
        differ.Run(Opcode.TxnSubmit, Requests.TxnSubmit(0, 5, TritDelta.Pos));
        differ.Run(Opcode.TxnSubmit, Requests.TxnSubmit(0, 6, TritDelta.Neg));
        differ.Run(Opcode.TxnSubmit, Requests.TxnSubmit(0, 7, TritDelta.Zero)); // 弃权：不落地
        differ.Run(Opcode.TxnBegin, Requests.TxnBegin());
        differ.Run(Opcode.TxnSubmit, Requests.TxnSubmit(1, 8, TritDelta.Pos));
        differ.Run(Opcode.TxnSubmit, Requests.TxnSubmit(1, 9, TritDelta.Pos));
        differ.Run(Opcode.TxnCommit, Requests.TxnCommit(1));
        differ.Run(Opcode.TxnCommit, Requests.TxnCommit(1)); // 重复提交：状态非法
        differ.Run(Opcode.TxnBegin, Requests.TxnBegin());
        differ.Run(Opcode.TxnRollback, Requests.TxnRollback(2));
        differ.Run(Opcode.TxnRollback, Requests.TxnRollback(2)); // 重复回滚

        // 仲裁：干净、争议、弃权。
        differ.Run(Opcode.Arbitrate, Requests.Arbitrate(new[]
        {
            new WriteIntent(10, 20, TritDelta.Pos),
            new WriteIntent(11, 21, TritDelta.Neg),
        }));
        differ.Run(Opcode.Arbitrate, Requests.Arbitrate(new[]
        {
            new WriteIntent(12, 22, TritDelta.Pos),
            new WriteIntent(13, 22, TritDelta.Neg),
        }));
        differ.Run(Opcode.Arbitrate, Requests.Arbitrate(new[]
        {
            new WriteIntent(14, 23, TritDelta.Zero),
            new WriteIntent(15, 23, TritDelta.Zero),
        }));

        // PRNG。
        differ.Run(Opcode.PrngNext, Requests.PrngNext(13));
        differ.Run(Opcode.PrngNext, Requests.PrngNext(0));

        // 快照 / 差分 / 回滚。
        differ.Run(Opcode.Snapshot, Requests.Snapshot());
        differ.Run(Opcode.TxnSubmit, Requests.TxnSubmit(0, 5, TritDelta.Neg));
        differ.Run(Opcode.Snapshot, Requests.Snapshot());
        differ.Run(Opcode.Diff, Requests.Diff(1, 2));
        differ.Run(Opcode.Diff, Requests.Diff(2, 1));
        differ.Run(Opcode.Diff, Requests.Diff(1, 99)); // 快照不存在
        differ.Run(Opcode.RollbackTo, Requests.RollbackTo(1));
        differ.Run(Opcode.RollbackTo, Requests.RollbackTo(1)); // 幂等
        differ.Run(Opcode.RollbackTo, Requests.RollbackTo(42)); // 不存在

        // 影子 / 统计 / 能力。
        differ.Run(Opcode.ShadowRecord, Requests.ShadowRecord(Array.Empty<TritDelta>()));
        differ.Run(Opcode.ShadowRecord, Requests.ShadowRecord(new[] { TritDelta.Pos, TritDelta.Neg }));
        differ.Run(Opcode.Stats, Requests.Stats());
        differ.Run(Opcode.Capabilities, Requests.Capabilities());

        // 未知 opcode。
        differ.Run(0x7E, Array.Empty<byte>());

        // 快照字节单独对拍。
        Assert.Equal(managed.SnapshotBytes(), native.SnapshotBytes());
    }

    /// <summary>把一份脚本单独跑在某个内核上（统计对拍用）。</summary>
    private static void RunScriptOn(ITritKernel kernel)
    {
        kernel.Subscribe(1, 100, 0, Array.Empty<uint>());
        kernel.Subscribe(2, 101, 3, new uint[] { 100 });
        kernel.Publish(100, new[] { TritDelta.Pos, TritDelta.Neg });
        kernel.Step(1);
        kernel.SubmitIntent(0, 5, TritDelta.Pos);
        var txn = kernel.BeginTransaction();
        kernel.SubmitIntent(txn, 6, TritDelta.Neg);
        kernel.Commit(txn);
        var snapshot = kernel.Snapshot();
        kernel.SubmitIntent(0, 7, TritDelta.Pos);
        kernel.Diff(snapshot, kernel.Snapshot());
        kernel.NextRandom(9);
        kernel.Arbitrate(new[] { new WriteIntent(1, 8, TritDelta.Pos) });
    }

    /// <summary>在两个内核上构造完全相同的世界投影。</summary>
    private static void BuildComparableWorld(ITritKernel kernel)
    {
        kernel.Step(11);
        kernel.SubmitIntent(0, 1, TritDelta.Pos);
        kernel.SubmitIntent(0, 900, TritDelta.Neg);
        kernel.SubmitIntent(0, 4242, TritDelta.Pos);
        kernel.SubmitIntent(0, 7, TritDelta.Neg);
        kernel.Step(12);
        kernel.SubmitIntent(0, 900, TritDelta.Pos);
    }
}
