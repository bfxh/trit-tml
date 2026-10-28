#nullable enable

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

            // 唯一允许的分歧：能力报告。托管回退没有 C ABI、AI、并行线程池或 GPU，
            // 但仍暴露同步扫描兼容语义与预算账本。除此之外必须逐字节一致。
            var divergence = Assert.Single(differ.Recent);
            Assert.Equal(Opcode.Capabilities, divergence.Opcode);
            Assert.Equal(1ul, differ.Divergences);

            var nativeCaps = new WireReader(divergence.Primary.Payload);
            Assert.Equal(NativeKernel.ExpectedAbiVersion, nativeCaps.U32());
            Assert.Equal(NativeKernel.MinimumRuntimeVersion, nativeCaps.U32());
            var nativeBits = (CapabilityBits)nativeCaps.U64();
            Assert.True((nativeBits & CapabilityBits.Ai) != 0);
            Assert.True((nativeBits & CapabilityBits.Parallel) != 0);
            Assert.True((nativeBits & CapabilityBits.Budget) != 0);

            var managedCaps = new WireReader(divergence.Shadow.Payload);
            Assert.Equal(0u, managedCaps.U32());
            Assert.Equal(NativeKernel.MinimumRuntimeVersion, managedCaps.U32());
            var managedBits = (CapabilityBits)managedCaps.U64();
            Assert.False((managedBits & CapabilityBits.Ai) != 0);
            Assert.False((managedBits & CapabilityBits.Parallel) != 0);
            Assert.False((managedBits & CapabilityBits.Gpu) != 0);
            Assert.True((managedBits & CapabilityBits.Budget) != 0);

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
    public void BatchSubmitLandsExactlyLikeOneByOneSubmit()
    {
        var pair = Pair();
        if (pair is null)
        {
            return;
        }

        var (native, managed, _) = pair.Value;
        try
        {
            // 同一批车道：一边逐条提交，一边批量提交；最终世界必须逐字节相同。
            var keys = new uint[64];
            var deltas = new TritDelta[64];
            for (int i = 0; i < keys.Length; i++)
            {
                keys[i] = (uint)(500 + i);
                deltas[i] = i % 3 == 0 ? TritDelta.Neg : i % 3 == 1 ? TritDelta.Zero : TritDelta.Pos;
            }

            ulong txn = native.BeginTransaction();
            for (int i = 0; i < keys.Length; i++)
            {
                native.SubmitIntent(txn, keys[i], deltas[i]);
            }

            native.Commit(txn);

            ulong batchTxn = managed.BeginTransaction();
            managed.SubmitIntents(batchTxn, keys, deltas);
            managed.Commit(batchTxn);

            Assert.Equal(native.SnapshotBytes(), managed.SnapshotBytes());
        }
        finally
        {
            native.Dispose();
            managed.Dispose();
        }
    }

    [Fact]
    public void SessionResetLeavesBothKernelsInTheSameEmptyState()
    {
        var pair = Pair();
        if (pair is null)
        {
            return;
        }

        var (native, managed, _) = pair.Value;
        try
        {
            // 先让两个内核都攒出真实状态（世界车道 / 日志 / 快照 / 事件），
            // 再各自复位，然后逐项对拍：复位后的状态必须完全一致，
            // 否则"退出存档"在原生内核和托管回退上会留下不同的起点。
            BuildComparableWorld(native);
            BuildComparableWorld(managed);
            native.Snapshot();
            managed.Snapshot();

            var nativeResponse = native.Submit(Opcode.SessionReset, Array.Empty<byte>());
            var managedResponse = managed.Submit(Opcode.SessionReset, Array.Empty<byte>());
            Assert.Equal(nativeResponse.Status, managedResponse.Status);
            Assert.Equal(nativeResponse.Payload, managedResponse.Payload);

            foreach (var query in new[]
                     {
                         QueryId.WorldSize, QueryId.DirtyCount, QueryId.LogLen, QueryId.SnapshotCount,
                     })
            {
                Assert.Equal(
                    native.Submit(Opcode.Query, Requests.Query(query)).Payload,
                    managed.Submit(Opcode.Query, Requests.Query(query)).Payload);
            }

            Assert.Equal(native.SnapshotBytes(), managed.SnapshotBytes());
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

    /// <summary>
    /// <c>ApplyBatch</c>（一步提交）必须与 开事务 + 批量提交 + 提交 三步等价，
    /// 且原生内核与托管回退内核给出完全相同的 applied / log_seq / 世界投影。
    /// </summary>
    [Fact]
    public void ApplyBatchMatchesTheThreeStepFlow()
    {
        var pair = Pair();
        if (pair is null)
        {
            return;
        }

        var (native, managed, _) = pair.Value;
        uint[] keys = { 7, 11, 19, 23 };
        TritDelta[] deltas = { TritDelta.Pos, TritDelta.Pos, TritDelta.Neg, TritDelta.Pos };
        try
        {
            native.Step(1);
            managed.Step(1);

            var threeNative = CommitThreeStep(native, keys, deltas);
            var threeManaged = CommitThreeStep(managed, keys, deltas);
            Assert.Equal(threeNative, threeManaged);

            var oneNative = native.ApplyBatch(keys, deltas);
            var oneManaged = managed.ApplyBatch(keys, deltas);
            Assert.Equal(oneManaged.Applied, oneNative.Applied);
            Assert.Equal(oneManaged.LogSeq, oneNative.LogSeq);
            Assert.Equal(keys.Length, (int)oneNative.Applied);

            Assert.Equal(
                native.Submit(Opcode.Query, Requests.Query(QueryId.WorldSize)).Payload,
                managed.Submit(Opcode.Query, Requests.Query(QueryId.WorldSize)).Payload);
            Assert.Equal(native.SnapshotBytes(), managed.SnapshotBytes());
        }
        finally
        {
            native.Dispose();
            managed.Dispose();
        }
    }

    /// <summary>
    /// 坏载荷必须在两条路径上给出<b>同一个</b>状态码：否则「托管回退」不是回退，
    /// 而是换了一套故障表现（原生报 ARG、托管却 OOM 或静默成功）。
    /// </summary>
    [Fact]
    public void BothKernelsRejectABatchCountLargerThanThePayload()
    {
        var pair = Pair();
        if (pair is null)
        {
            return;
        }

        var (native, managed, _) = pair.Value;
        try
        {
            var writer = new WireWriter(14);
            writer.U32(1000); // 声称 1000 条，实际只给 2 条
            writer.U32(1).U8((byte)TritDelta.Pos);
            writer.U32(2).U8((byte)TritDelta.Neg);

            var bad = writer.ToArray();
            Assert.Equal(
                native.Submit(Opcode.TxnApply, bad).Status,
                managed.Submit(Opcode.TxnApply, bad).Status);
            Assert.Equal(Status.Decode, managed.Submit(Opcode.TxnApply, bad).Status);
        }
        finally
        {
            native.Dispose();
            managed.Dispose();
        }
    }

    private static TxnCommitResult CommitThreeStep(
        ITritKernel kernel,
        IReadOnlyList<uint> keys,
        IReadOnlyList<TritDelta> deltas)
    {
        var txn = kernel.BeginTransaction();
        kernel.SubmitIntents(txn, keys, deltas);
        return kernel.Commit(txn);
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
