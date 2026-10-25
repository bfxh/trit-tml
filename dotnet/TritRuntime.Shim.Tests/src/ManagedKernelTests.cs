using TritRuntime.Kernel;
using TritRuntime.Wire;
using Xunit;
using WireFormat = TritRuntime.Wire.Wire;

namespace TritRuntime.Shim.Tests;

/// <summary>
/// 托管回退内核的语义测试：覆盖路由器、事务、仲裁、快照、PRNG、影子、统计。
/// 这些用例与 Rust 侧 <c>crates/trit-rt</c> 的单测一一对应——
/// 两边都过，才敢说「语义等价」。
/// </summary>
public sealed class ManagedKernelTests : IDisposable
{
    private readonly ManagedKernel kernel = new();

    public void Dispose() => kernel.Dispose();

    // ---- 路由器 ----

    [Fact]
    public void PublishMarksDependencyClosureDirtyAndDispatchesInOrder()
    {
        var a = kernel.Subscribe(1, 100, 0, Array.Empty<uint>());
        var b = kernel.Subscribe(2, 101, 0, new uint[] { 100 });
        var c = kernel.Subscribe(3, 102, 0, new uint[] { 101 });
        var unrelated = kernel.Subscribe(4, 200, 0, Array.Empty<uint>());

        Assert.Equal(3u, kernel.Publish(100, Array.Empty<TritDelta>()));

        var dispatch = kernel.Step(1).Intents;
        Assert.Equal(3, dispatch.Count);
        Assert.Equal(new[] { a, b, c }, dispatch.Select(intent => intent.SubId).ToArray());
        Assert.DoesNotContain(unrelated, dispatch.Select(intent => intent.SubId));
    }

    [Fact]
    public void StepCarriesModAndEventIds()
    {
        kernel.Subscribe(7, 42, 0, Array.Empty<uint>());
        kernel.Publish(42, Array.Empty<TritDelta>());
        var dispatch = kernel.Step(9).Intents;
        var intent = Assert.Single(dispatch);
        Assert.Equal(7u, intent.ModId);
        Assert.Equal(42u, intent.EventId);
    }

    [Fact]
    public void PriorityOrdersIndependentSubscribers()
    {
        var low = kernel.Subscribe(1, 100, -5, Array.Empty<uint>());
        var high = kernel.Subscribe(2, 101, 5, Array.Empty<uint>());
        kernel.Publish(100, Array.Empty<TritDelta>());
        kernel.Publish(101, Array.Empty<TritDelta>());

        var dispatch = kernel.Step(1).Intents;
        var order = dispatch.Select(intent => intent.SubId).ToArray();
        Assert.True(Array.IndexOf(order, high) < Array.IndexOf(order, low), $"高优先级应先派发：{string.Join(",", order)}");
    }

    [Fact]
    public void UnknownEventIsNoOp()
    {
        kernel.Subscribe(1, 100, 0, Array.Empty<uint>());
        Assert.Equal(0u, kernel.Publish(999, Array.Empty<TritDelta>()));
        Assert.Empty(kernel.Step(1).Intents);
    }

    [Fact]
    public void UnsubscribeRemovesFromDispatch()
    {
        var a = kernel.Subscribe(1, 100, 0, Array.Empty<uint>());
        kernel.Publish(100, Array.Empty<TritDelta>());
        kernel.Unsubscribe(a);
        Assert.Empty(kernel.Step(1).Intents);
    }

    [Fact]
    public void UnsubscribeUnknownThrowsNotFound()
    {
        var error = Assert.Throws<KernelException>(() => kernel.Unsubscribe(4242));
        Assert.Equal(Status.NotFound, error.Status);
    }

    [Fact]
    public void SelfDependencyIsRejected()
    {
        var error = Assert.Throws<KernelException>(() => kernel.Subscribe(1, 100, 0, new uint[] { 100 }));
        Assert.Equal(Status.Arg, error.Status);
        Assert.Contains("不能依赖自己", kernel.LastError);
    }

    [Fact]
    public void CycleIsRejectedWithoutSideEffects()
    {
        var a = kernel.Subscribe(1, 100, 0, new uint[] { 101 });
        var error = Assert.Throws<KernelException>(() => kernel.Subscribe(2, 101, 0, new uint[] { 100 }));
        Assert.Equal(Status.Arg, error.Status);
        Assert.Contains("存在环", kernel.LastError);

        // 坏订阅不留痕迹：a 仍在，且可以正常退订后重新订阅。
        kernel.Unsubscribe(a);
        var b = kernel.Subscribe(2, 101, 0, Array.Empty<uint>());
        Assert.Equal(a + 1, b);
    }

    [Fact]
    public void PublishCountsOnlyNewlyDirty()
    {
        kernel.Subscribe(1, 100, 0, Array.Empty<uint>());
        kernel.Subscribe(2, 100, 0, Array.Empty<uint>());
        Assert.Equal(2u, kernel.Publish(100, Array.Empty<TritDelta>()));
        // 未 step ⇒ 脏集合还在，再次发布不增加新标脏。
        Assert.Equal(0u, kernel.Publish(100, Array.Empty<TritDelta>()));
    }

    // ---- 事务 ----

    [Fact]
    public void TransactionCommitAppliesIntents()
    {
        var txn = kernel.BeginTransaction();
        kernel.SubmitIntent(txn, 42, TritDelta.Pos);
        var result = kernel.Commit(txn);
        Assert.Equal(1u, result.Applied);
        Assert.Equal(1ul, result.LogSeq);
        Assert.Equal(TritDelta.Pos, kernel.World[42]);
    }

    [Fact]
    public void AutoCommitAppliesImmediately()
    {
        kernel.SubmitIntent(0, 7, TritDelta.Pos);
        Assert.Equal(TritDelta.Pos, kernel.World[7]);
    }

    [Fact]
    public void RollbackDiscardsIntents()
    {
        var txn = kernel.BeginTransaction();
        kernel.SubmitIntent(txn, 1, TritDelta.Neg);
        kernel.RollbackTransaction(txn);
        Assert.False(kernel.World.ContainsKey(1));
    }

    [Fact]
    public void DoubleCommitIsRejected()
    {
        var txn = kernel.BeginTransaction();
        kernel.Commit(txn);
        var error = Assert.Throws<KernelException>(() => kernel.Commit(txn));
        Assert.Equal(Status.State, error.Status);
    }

    [Fact]
    public void CommitConflictKeepsTransactionOpen()
    {
        // 严格模式：同车道两个驱动即冲突。
        using var strict = new ManagedKernel(
            new ManagedConfig { Arbitration = ArbitrationMode.Strict });
        var a = strict.BeginTransaction();
        var b = strict.BeginTransaction();
        strict.SubmitIntent(a, 7, TritDelta.Neg);
        strict.SubmitIntent(b, 7, TritDelta.Pos);

        var error = Assert.Throws<KernelException>(() => strict.Commit(a));
        Assert.True(error.IsConflict);
        Assert.True(error.ShouldFallBack is false);

        // 冲突 ⇒ 什么都不落地，且事务仍打开（可以回滚）。
        Assert.Empty(strict.World);
        strict.RollbackTransaction(a);
        strict.RollbackTransaction(b);
    }

    [Fact]
    public void ConflictDiagnosticsListEveryDriver()
    {
        using var strict = new ManagedKernel(
            new ManagedConfig { Arbitration = ArbitrationMode.Strict });
        var a = strict.BeginTransaction();
        var b = strict.BeginTransaction();
        strict.SubmitIntent(a, 10, TritDelta.Neg);
        strict.SubmitIntent(b, 10, TritDelta.Pos);

        var error = Assert.Throws<KernelException>(() => strict.Commit(a));
        var dispute = Assert.Single(error.Disputes);
        Assert.Equal(10u, dispute.Key);
        Assert.Equal(new[] { (a, TritDelta.Neg), (b, TritDelta.Pos) }, dispute.Drivers);
    }

    [Fact]
    public void DeltaAccumulationClampsAtBounds()
    {
        Assert.Equal(TritDelta.Pos, ManagedKernel.ApplyDelta(TritDelta.Zero, TritDelta.Pos));
        Assert.Equal(TritDelta.Pos, ManagedKernel.ApplyDelta(TritDelta.Pos, TritDelta.Pos));
        Assert.Equal(TritDelta.Neg, ManagedKernel.ApplyDelta(TritDelta.Neg, TritDelta.Neg));
        Assert.Equal(TritDelta.Zero, ManagedKernel.ApplyDelta(TritDelta.Pos, TritDelta.Neg));
        Assert.Equal(TritDelta.Zero, ManagedKernel.ApplyDelta(TritDelta.Neg, TritDelta.Pos));
    }

    // ---- 仲裁 ----

    [Fact]
    public void StrictModeFlagsSameDirectionDrivers()
    {
        using var strict = new ManagedKernel(new ManagedConfig { Arbitration = ArbitrationMode.Strict });
        var result = strict.Arbitrate(new[]
        {
            new WriteIntent(1, 10, TritDelta.Pos),
            new WriteIntent(2, 10, TritDelta.Pos),
            new WriteIntent(3, 11, TritDelta.Neg),
        });

        Assert.True(result.HasDisputes);
        var dispute = Assert.Single(result.Disputes);
        Assert.Equal(10u, dispute.Key);
        Assert.Equal(2, dispute.Drivers.Count);
    }

    [Fact]
    public void MajorityModeMergesSameDirectionDrivers()
    {
        var result = kernel.Arbitrate(new[]
        {
            new WriteIntent(1, 10, TritDelta.Pos),
            new WriteIntent(2, 10, TritDelta.Pos),
            new WriteIntent(3, 11, TritDelta.Neg),
        });

        Assert.False(result.HasDisputes);
        Assert.Equal(
            new[] { (10u, TritDelta.Pos), (11u, TritDelta.Neg) },
            result.Resolved);
    }

    [Fact]
    public void MajorityModeResolvesByVoteAndTiesDispute()
    {
        var resolved = kernel.Arbitrate(new[]
        {
            new WriteIntent(1, 10, TritDelta.Pos),
            new WriteIntent(2, 10, TritDelta.Pos),
            new WriteIntent(3, 10, TritDelta.Neg),
            new WriteIntent(4, 11, TritDelta.Neg),
            new WriteIntent(5, 11, TritDelta.Neg),
            new WriteIntent(6, 11, TritDelta.Pos),
        });

        Assert.False(resolved.HasDisputes);
        Assert.Equal(
            new[] { (10u, TritDelta.Pos), (11u, TritDelta.Neg) },
            resolved.Resolved);

        var tie = kernel.Arbitrate(new[]
        {
            new WriteIntent(1, 20, TritDelta.Pos),
            new WriteIntent(2, 20, TritDelta.Neg),
        });
        Assert.True(tie.HasDisputes);
        Assert.Equal(20u, Assert.Single(tie.Disputes).Key);
    }

    [Fact]
    public void AbstainNeverDrives()
    {
        var result = kernel.Arbitrate(new[]
        {
            new WriteIntent(1, 10, TritDelta.Zero),
            new WriteIntent(2, 10, TritDelta.Zero),
        });

        Assert.False(result.HasDisputes);
        Assert.Empty(result.Resolved);
    }

    [Fact]
    public void ArbitrateDoesNotMutateWorld()
    {
        kernel.Arbitrate(new[] { new WriteIntent(1, 5, TritDelta.Pos) });
        Assert.Empty(kernel.World);
    }

    // ---- 快照 / 差分 / 回滚 ----

    [Fact]
    public void SnapshotDiffRollbackAndReplay()
    {
        kernel.SubmitIntent(0, 1, TritDelta.Pos);
        kernel.SubmitIntent(0, 2, TritDelta.Pos);
        var snapshot = kernel.Snapshot();

        kernel.SubmitIntent(0, 1, TritDelta.Neg); // 撤掉 key 1
        kernel.SubmitIntent(0, 3, TritDelta.Pos);
        Assert.Equal(3, kernel.World.Count);

        kernel.RollbackTo(snapshot);
        Assert.Equal(2, kernel.World.Count);
        Assert.Equal(TritDelta.Pos, kernel.World[1]);

        // 重放快照之后的日志，回到回滚前状态。
        Assert.Equal(2, kernel.ReplayFrom(2));
        Assert.Equal(3, kernel.World.Count);
        Assert.Equal(TritDelta.Zero, kernel.World[1]);
    }

    [Fact]
    public void LogIsAppendOnlyAfterRollback()
    {
        kernel.SubmitIntent(0, 1, TritDelta.Pos); // 日志序号 1
        var snapshot = kernel.Snapshot();
        kernel.SubmitIntent(0, 2, TritDelta.Pos); // 日志序号 2
        kernel.RollbackTo(snapshot);

        // 回滚只还原世界投影，日志保持只追加。
        Assert.Equal(2ul, QueryU64(QueryId.LogLen));
        Assert.False(kernel.World.ContainsKey(2));
        // 仍可从快照序列号向前重放。
        Assert.Equal(1, kernel.ReplayFrom(1));
        Assert.True(kernel.World.ContainsKey(2));
    }

    [Fact]
    public void DiffReportsChangedAddedAndRemovedLanes()
    {
        kernel.SubmitIntent(0, 1, TritDelta.Pos);
        kernel.SubmitIntent(0, 2, TritDelta.Pos);
        var first = kernel.Snapshot();

        kernel.SubmitIntent(0, 1, TritDelta.Neg); // 1: Pos -> Zero
        kernel.SubmitIntent(0, 3, TritDelta.Pos); // 3: 新增
        var second = kernel.Snapshot();

        Assert.Equal(
            new[]
            {
                new DiffEntry(1, TritDelta.Pos, TritDelta.Zero),
                new DiffEntry(3, TritDelta.Zero, TritDelta.Pos),
            },
            kernel.Diff(first, second).Entries);

        // 反向差分：3 相对 second 就是「被删除」。
        Assert.Equal(
            new[]
            {
                new DiffEntry(1, TritDelta.Zero, TritDelta.Pos),
                new DiffEntry(3, TritDelta.Pos, TritDelta.Zero),
            },
            kernel.Diff(second, first).Entries);
    }

    [Fact]
    public void SnapshotBytesRoundTrip()
    {
        kernel.SubmitIntent(0, 900, TritDelta.Pos);
        kernel.SubmitIntent(0, 4242, TritDelta.Neg);
        kernel.Step(5);
        var bytes = kernel.SnapshotBytes();

        // 布局：[u64 id][世界编码]，不装帧。
        var reader = new WireReader(bytes);
        var id = reader.U64();
        var (tick, logSeq, world) = WorldCodec.Decode(reader.Bytes(reader.Remaining));
        Assert.Equal(5ul, tick);
        Assert.Equal(2ul, logSeq);
        Assert.Equal(TritDelta.Pos, world[900]);
        Assert.Equal(TritDelta.Neg, world[4242]);
        Assert.True(id > 0);
    }

    [Fact]
    public void RollbackToUnknownSnapshotThrows()
    {
        var error = Assert.Throws<KernelException>(() => kernel.RollbackTo(999));
        Assert.Equal(Status.NotFound, error.Status);
    }

    // ---- PRNG ----

    [Fact]
    public void PrngMatchesIndependentSplitmix64Vector()
    {
        // 参考向量由标准 splitmix64 独立算出（reseed(0x5EED1234, 7) 后连续 6 次取模 3）。
        kernel.Step(7);
        var trits = kernel.NextRandom(6);
        Assert.Equal(
            new[] { TritDelta.Pos, TritDelta.Pos, TritDelta.Zero, TritDelta.Pos, TritDelta.Pos, TritDelta.Neg },
            trits);
    }

    [Fact]
    public void PrngIsDeterministicPerTick()
    {
        kernel.Step(3);
        var first = kernel.NextRandom(32);
        kernel.Step(3);
        var second = kernel.NextRandom(32);
        Assert.Equal(first, second);
    }

    [Fact]
    public void DifferentTicksDiverge()
    {
        kernel.Step(3);
        var first = kernel.NextRandom(16);
        kernel.Step(4);
        var second = kernel.NextRandom(16);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void PrngRejectsOversizedDraw()
    {
        var error = Assert.Throws<KernelException>(() => kernel.NextRandom(ManagedKernel.MaxPrngCount + 1));
        Assert.Equal(Status.Arg, error.Status);
    }

    // ---- 影子 ----

    [Fact]
    public void ShadowRecordCountsDivergencesOnly()
    {
        Assert.Equal(0ul, kernel.ShadowRecord(Array.Empty<byte>()));
        Assert.Equal(1ul, kernel.ShadowRecord(new byte[] { 1, 2 }));
        Assert.Equal(2ul, kernel.ShadowRecord(new byte[] { 0 }));
        Assert.Equal(3ul, kernel.ShadowRecord(new byte[] { 2 }));
    }

    [Fact]
    public void ShadowQueryReportsComparedAndDivergences()
    {
        kernel.ShadowRecord(Array.Empty<byte>());
        kernel.ShadowRecord(new byte[] { 1 });
        var payload = kernel.Submit(Opcode.Query, Requests.Query(QueryId.Shadow)).Payload;
        var reader = new WireReader(payload);
        Assert.Equal(2ul, reader.U64());
        Assert.Equal(1ul, reader.U64());
    }

    [Fact]
    public void ShadowRecordDoesNotChangeWorld()
    {
        kernel.SubmitIntent(0, 1, TritDelta.Pos);
        var before = WorldOf(kernel.SnapshotBytes());
        kernel.ShadowRecord(new byte[] { 1, 1, 1 });
        Assert.Equal(before, WorldOf(kernel.SnapshotBytes()));
    }

    // ---- 统计 / 查询 ----

    [Fact]
    public void StatsCountersInFixedOrder()
    {
        kernel.Subscribe(1, 100, 0, Array.Empty<uint>());
        kernel.Publish(100, Array.Empty<TritDelta>());
        kernel.Step(1);
        var txn = kernel.BeginTransaction();
        kernel.SubmitIntent(txn, 1, TritDelta.Pos);
        kernel.Commit(txn);
        kernel.Snapshot();
        kernel.Diff(kernel.Snapshot(), kernel.Snapshot());
        kernel.Arbitrate(new[] { new WriteIntent(1, 9, TritDelta.Pos) });
        kernel.NextRandom(4);

        var stats = kernel.Stats();
        Assert.Equal(10, stats.Length);
        Assert.Equal(1ul, stats[0]); // events_published
        Assert.Equal(1ul, stats[1]); // events_dispatched
        Assert.Equal(1ul, stats[2]); // txns_committed
        Assert.Equal(0ul, stats[3]); // txns_rolled_back
        Assert.Equal(3ul, stats[4]); // snapshots_taken
        Assert.Equal(1ul, stats[5]); // diffs
        Assert.Equal(1ul, stats[6]); // arbitrations
        Assert.Equal(0ul, stats[7]); // conflicts
        Assert.Equal(1ul, stats[8]); // intents_applied
        Assert.Equal(4ul, stats[9]); // prng_draws
    }

    [Fact]
    public void QueriesReturnExpectedValues()
    {
        kernel.Subscribe(1, 100, 0, Array.Empty<uint>());
        kernel.Publish(100, new[] { TritDelta.Pos, TritDelta.Neg });
        kernel.SubmitIntent(0, 3, TritDelta.Pos);
        var txn = kernel.BeginTransaction();

        Assert.Equal(1u, QueryU32(QueryId.DirtyCount));
        Assert.Equal(1u, QueryU32(QueryId.WorldSize));
        Assert.Equal(1ul, QueryU64(QueryId.LogLen));
        Assert.Equal(0u, QueryU32(QueryId.SnapshotCount));
        Assert.Equal(1u, QueryU32(QueryId.OpenTxnCount));

        var payload = kernel.Submit(Opcode.Query, Requests.Query(QueryId.EventPayload, 100)).Payload;
        Assert.Equal(new[] { TritDelta.Pos, TritDelta.Neg }, new WireReader(payload).Trits());
    }

    [Fact]
    public void UnknownQueryIsRejected()
    {
        var response = kernel.Query(new WireWriter().U32(99).ToArray());
        Assert.Equal(Status.Arg, response.Status);
    }

    // ---- 配置 / 杂项 ----

    [Fact]
    public void ConfigParsesFromJsonWithDefaults()
    {
        var config = ManagedConfig.FromJson(System.Text.Encoding.UTF8.GetBytes("{\"seed\":7,\"arbitration\":\"strict\"}"));
        Assert.Equal(7ul, config.Seed);
        Assert.Equal(ArbitrationMode.Strict, config.Arbitration);
        Assert.Equal(64, config.MaxSnapshots);

        var defaults = ManagedConfig.FromJson(Array.Empty<byte>());
        Assert.Equal(0x5EED_1234ul, defaults.Seed);
        Assert.Equal(ArbitrationMode.Majority, defaults.Arbitration);
    }

    [Fact]
    public void InvalidConfigIsRejected()
    {
        Assert.Throws<WireException>(() => ManagedConfig.FromJson(System.Text.Encoding.UTF8.GetBytes("not json")));
        Assert.Throws<WireException>(
            () => ManagedConfig.FromJson(System.Text.Encoding.UTF8.GetBytes("{\"arbitration\":\"weird\"}")));
    }

    [Fact]
    public void UnknownOpcodeIsRejected()
    {
        var response = kernel.Submit(0xFF, Array.Empty<byte>());
        Assert.Equal(Status.Arg, response.Status);
    }

    [Fact]
    public void ZeroOpcodeIsBadOp()
    {
        var response = kernel.Submit(0, Array.Empty<byte>());
        Assert.Equal(Status.BadOp, response.Status);
    }

    [Fact]
    public void LogCapacityLimitReportsCapacity()
    {
        using var tiny = new ManagedKernel(new ManagedConfig { MaxLogEntries = 2 });
        tiny.SubmitIntent(0, 1, TritDelta.Pos);
        tiny.SubmitIntent(0, 2, TritDelta.Pos);
        var error = Assert.Throws<KernelException>(() => tiny.SubmitIntent(0, 3, TritDelta.Pos));
        Assert.Equal(Status.Capacity, error.Status);
        Assert.True(error.ShouldFallBack);
    }

    [Fact]
    public void CapabilityReportMarksManagedWithoutAi()
    {
        var report = kernel.Capabilities();
        Assert.Equal(KernelMode.Managed, report.Mode);
        Assert.Equal(0u, report.AbiVersion);
        Assert.True(report.Has(CapabilityBits.Router));
        Assert.True(report.Has(CapabilityBits.TxnLog));
        Assert.True(report.Has(CapabilityBits.Shadow));
        Assert.False(report.Has(CapabilityBits.Ai), "托管回退不声明 AI 能力");
    }

    [Fact]
    public void AiVoteIsRejectedInManagedKernel()
    {
        var response = kernel.Submit(Opcode.AiVote, new WireWriter().Trits(new[] { TritDelta.Pos }).ToArray());
        Assert.Equal(Status.Arg, response.Status);
    }

    /// <summary>从快照字节里取出世界投影（剥掉 u64 ID 前缀与帧）。</summary>
    private static SortedDictionary<uint, TritDelta> WorldOf(byte[] snapshotBytes)
    {
        var reader = new WireReader(snapshotBytes);
        _ = reader.U64();
        var (_, _, world) = WorldCodec.Decode(reader.Bytes(reader.Remaining));
        return world;
    }

    private uint QueryU32(uint queryId)
    {
        var payload = kernel.Submit(Opcode.Query, Requests.Query(queryId)).Payload;
        return new WireReader(payload).U32();
    }

    private ulong QueryU64(uint queryId)
    {
        var payload = kernel.Submit(Opcode.Query, Requests.Query(queryId)).Payload;
        return new WireReader(payload).U64();
    }
}
