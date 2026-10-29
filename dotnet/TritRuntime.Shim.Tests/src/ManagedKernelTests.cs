#nullable enable

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
    public void ZeroCapacitiesInConfigAreClampedLikeTheNativeKernel()
    {
        // Rust 侧 RuntimeConfig::sanitize 把容量下限夹到 1/1/1/4096；
        // 托管回退必须给出同样的结果，否则一份坏配置只在回退时才炸。
        var json = System.Text.Encoding.UTF8.GetBytes(
            "{\"max_subscribers\":0,\"max_open_txns\":0,\"max_snapshots\":0,\"max_log_entries\":0}");
        var config = ManagedConfig.FromJson(json);
        Assert.Equal(1, config.MaxSubscribers);
        Assert.Equal(1, config.MaxOpenTxns);
        Assert.Equal(1, config.MaxSnapshots);
        Assert.Equal(4096, config.MaxLogEntries);
    }

    [Fact]
    public void SessionResetClearsWorldLogAndSnapshotsButKeepsSubscribers()
    {
        // 对应原生内核 0x1B：退出存档时把这张存档的投影全部放掉，
        // 但订阅关系（属于模组装载期）必须原样保留，否则调用方手里的 sub id 全部失效、派发静默停止。
        kernel.SubmitIntent(0, 1, TritDelta.Pos);
        kernel.SubmitIntent(0, 2, TritDelta.Neg);
        var sub = kernel.Subscribe(1, 100, 0, Array.Empty<uint>());
        kernel.Publish(100, new[] { TritDelta.Pos });
        kernel.Snapshot();
        Assert.Equal(2u, QueryU32(QueryId.WorldSize));

        var response = kernel.Submit(Opcode.SessionReset, Array.Empty<byte>());
        Assert.Equal(Status.Ok, response.Status);
        Assert.Empty(response.Payload);

        Assert.Equal(0u, QueryU32(QueryId.WorldSize));
        Assert.Equal(0ul, QueryU64(QueryId.LogLen));
        Assert.Equal(0u, QueryU32(QueryId.SnapshotCount));

        // 订阅仍在：复位后发布的事件依然派发得到这个 sub。
        kernel.Publish(100, Array.Empty<TritDelta>());
        var dispatch = kernel.Step(1).Intents;
        Assert.Contains(dispatch, intent => intent.SubId == sub);
    }

    [Fact]
    public void SessionResetRejectsAPayload()
    {
        var response = kernel.Submit(Opcode.SessionReset, new byte[] { 1 });
        Assert.Equal(Status.Arg, response.Status);
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

    /// <summary>
    /// 批量载荷的 <c>count</c> 是 u32：坏载荷可以写 0xFFFF_FFFF。
    /// 托管侧若直接按 count 分配，一条 20 字节的坏载荷就能让进程去申请几十 GB；
    /// 必须先按剩余字节数夹一道，与原生侧「边读边解、读不动就报错」保持一致。
    /// </summary>
    [Theory]
    [InlineData(Opcode.TxnApply, 4)]
    [InlineData(Opcode.TxnSubmitBatch, 12)]
    public void BatchCountLargerThanThePayloadIsRejected(uint opcode, int headerBytes)
    {
        var writer = new WireWriter(headerBytes + 10);
        if (headerBytes == 12)
        {
            writer.U64(1);
        }

        writer.U32(1000); // 声称 1000 条，实际只给了 2 条（10 字节）
        writer.U32(1).U8((byte)TritDelta.Pos);
        writer.U32(2).U8((byte)TritDelta.Neg);

        var response = kernel.Submit(opcode, writer.ToArray());
        Assert.Equal(Status.Decode, response.Status);
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

    // ---- 回归：表与闸门不能随调用次数停摆 ----

    /// <summary>
    /// 事务表必须能被反复开闭，次数超过 <c>max_open_txns</c> 也不许停摆。
    /// </summary>
    /// <remarks>
    /// 原生侧早在 <c>txn.rs</c> 就修过一次（<c>CLOSED_TXN_RETAIN</c>）：事务表只增不减，
    /// 而容量闸门又拿<b>条目总数</b>当「并发数」，于是跑到 4096 tick 之后
    /// <c>begin</c> 永久失败、整个旁路写入路径停摆。托管回退内核当时漏掉了同一个修复。
    /// 这条把「闸门只数打开中的」和「已关闭的会被引退」两件事一起钉死。
    /// </remarks>
    [Fact]
    public void TransactionsKeepWorkingPastTheOpenLimit()
    {
        int limit = new ManagedConfig().MaxOpenTxns;
        var kernel = new ManagedKernel();

        for (int i = 0; i < limit + 64; i++)
        {
            ulong txn = kernel.BeginTransaction();
            kernel.SubmitIntent(txn, 1, TritDelta.Pos);
            kernel.Commit(txn);
        }

        // 全部已提交：打开中的必须是 0，下一笔事务必须还能开出来。
        Assert.Equal(0u, QueryU32Of(kernel, QueryId.OpenTxnCount));
        ulong after = kernel.BeginTransaction();
        Assert.True(after > 0);
    }

    private static uint QueryU32Of(ManagedKernel kernel, uint queryId)
    {
        var payload = kernel.Submit(Opcode.Query, Requests.Query(queryId)).Payload;
        return new WireReader(payload).U32();
    }

    /// <summary>
    /// 线格式里越界的 trit 字节必须收敛成「弃权」，而不是被当成一条真实驱动。
    /// </summary>
    /// <remarks>
    /// 原生侧是 <c>Ternary::from_index(byte)</c>：0/1/2 之外一律落回 <c>Unknown</c>
    /// （索引 1，也就是总线高阻），在仲裁里<b>不参与投票</b>。
    /// 托管侧若直接 <c>(TritDelta)7</c>，它就是一个不等于 <c>Zero</c> 的非法枚举值，
    /// 于是被算成一条驱动：多数模式下凭空多出一条车道并<b>真的写进世界</b>。
    /// 两条路径对同一份载荷必须给出同一个世界。
    /// </remarks>
    [Theory]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(255)]
    public void AnOutOfRangeDeltaByteAbstainsInsteadOfDrivingTheLane(byte raw)
    {
        using var kernel = new ManagedKernel();

        // 裸载荷：[u64 txn_id][u32 key][u8 delta]，txn_id=0 走自动提交。
        var payload = new WireWriter().U64(0).U32(42).U8(raw).ToArray();
        var response = kernel.Submit(Opcode.TxnSubmit, payload);

        Assert.Equal(Status.Ok, response.Status);
        var worldBytes = kernel.Submit(Opcode.Query, Requests.Query(QueryId.WorldSize)).Payload;
        Assert.Equal(0u, new WireReader(worldBytes).U32());
    }

    /// <summary>
    /// 载荷里声明的条数超过 <see cref="int"/> 可表示范围时，必须是「解码失败」，
    /// 不能让 <see cref="OverflowException"/> 冒出托管内核。
    /// </summary>
    /// <remarks>
    /// <c>WireReader</c> 原来写的是 <c>checked((int)U32())</c>。托管内核只捕
    /// <c>RtException</c>/<c>WireException</c>/<c>JsonException</c>，溢出异常会一路
    /// 穿透到调用方模组的 <c>Mod.Call</c>；而原生是 <c>u32 as usize</c> 再边读边解，
    /// 报的是 <c>Decode</c>。这条锁住「同一份坏载荷 → 同一个状态码」。
    /// </remarks>
    [Theory]
    [InlineData(0x8000_0000u)]
    [InlineData(0xFFFF_FFFFu)]
    public void ATritCountBeyondIntRangeIsReportedAsDecodeNotAnOverflow(uint count)
    {
        using var kernel = new ManagedKernel();

        // [u32 event_id][u32 count][打包体]
        var payload = new WireWriter().U32(1).U32(count).ToArray();
        Assert.Equal(Status.Decode, kernel.Submit(Opcode.Publish, payload).Status);
    }

    /// <summary>
    /// 重复发布同一个事件 id，不能把字节预算的计数器撑大，更不能因此淘汰别人的载荷。
    /// </summary>
    /// <remarks>
    /// 计数器只加不减时，同一 id 发布 N 次就报 N×size；而它正是字节闸门的判据，
    /// 虚增会让淘汰误判超预算，把<b>别人</b>的载荷当受害者提前淘汰——
    /// 表现为读别人的事件时拿到空数组。这是静默丢数据，比内存涨更难查。
    /// 这里让「正确计数 = 4.5M + 10」远低于闸门、「错误计数 = 9M」越过闸门，
    /// 用「事件 2 的载荷还在不在」来分辨两种实现。
    /// </remarks>
    [Fact]
    public void RepublishingAnEventDoesNotEvictSomeoneElsesPayload()
    {
        using var kernel = new ManagedKernel();

        var big = new TritDelta[4_500_000];
        var small = new TritDelta[10];
        kernel.Publish(1, big);
        kernel.Publish(2, small);
        // 覆盖式重发：内容相同，计数器应该原地不动。
        kernel.Publish(1, big);

        var payload = kernel.Submit(Opcode.Query, Requests.Query(QueryId.EventPayload, 2)).Payload;
        Assert.Equal(small.Length, new WireReader(payload).Trits().Length);
    }
}
