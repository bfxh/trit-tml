using TritRuntime.Wire;

namespace TritRuntime.Kernel;

/// <summary>
/// 请求体构造器：<see cref="NativeKernel"/> 与 <see cref="ManagedKernel"/> 共用，
/// 保证两条路径对同一操作发出**逐字节相同**的请求——这是影子差分能逐字节
/// 对拍的前提（差异只能来自被调用方，不能来自调用方）。
/// </summary>
public static class Requests
{
    /// <summary>订阅：<c>mod_id u32, event_id u32, priority i32, dep_count u32, deps u32 * n</c>。</summary>
    public static byte[] Subscribe(uint modId, uint eventId, int priority, uint[] deps)
    {
        var writer = new WireWriter()
            .U32(modId)
            .U32(eventId)
            .I32(priority)
            .U32((uint)deps.Length);
        foreach (var dep in deps)
        {
            writer.U32(dep);
        }

        return writer.ToArray();
    }

    /// <summary>退订：<c>sub_id u64</c>。</summary>
    public static byte[] Unsubscribe(ulong subId) => new WireWriter().U64(subId).ToArray();

    /// <summary>发布：<c>event_id u32, trits</c>。</summary>
    public static byte[] Publish(uint eventId, TritDelta[] payload) =>
        new WireWriter().U32(eventId).Trits(payload).ToArray();

    /// <summary>开事务：<c>flags u32</c>。</summary>
    public static byte[] TxnBegin() => new WireWriter().U32(0).ToArray();

    /// <summary>提交事务：<c>txn_id u64</c>。</summary>
    public static byte[] TxnCommit(ulong txnId) => new WireWriter().U64(txnId).ToArray();

    /// <summary>回滚事务：<c>txn_id u64</c>。</summary>
    public static byte[] TxnRollback(ulong txnId) => new WireWriter().U64(txnId).ToArray();

    /// <summary>事务内提交意图：<c>txn_id u64, key u32, delta u8</c>（txn_id == 0 = 自动提交）。</summary>
    public static byte[] TxnSubmit(ulong txnId, uint key, TritDelta delta) =>
        new WireWriter().U64(txnId).U32(key).U8((byte)delta).ToArray();

    /// <summary>仲裁：<c>count u32, [txn_id u64, key u32, delta u8] * n</c>。</summary>
    public static byte[] Arbitrate(IReadOnlyList<WriteIntent> intents)
    {
        var writer = new WireWriter().U32((uint)intents.Count);
        foreach (var intent in intents)
        {
            writer.U64(intent.TxnId).U32(intent.Key).U8((byte)intent.Delta);
        }

        return writer.ToArray();
    }

    /// <summary>查询：<c>query_id u32</c>；事件载荷查询额外带 <c>event_id u32</c>。</summary>
    public static byte[] Query(uint queryId, uint eventId = 0) =>
        queryId == QueryId.EventPayload
            ? new WireWriter().U32(queryId).U32(eventId).ToArray()
            : new WireWriter().U32(queryId).ToArray();

    /// <summary>推进一个 tick：<c>tick u64</c>。</summary>
    public static byte[] Step(ulong tick) => new WireWriter().U64(tick).ToArray();

    /// <summary>打快照：<c>flags u32</c>。</summary>
    public static byte[] Snapshot() => new WireWriter().U32(0).ToArray();

    /// <summary>差分：<c>snap_a u64, snap_b u64</c>。</summary>
    public static byte[] Diff(ulong a, ulong b) => new WireWriter().U64(a).U64(b).ToArray();

    /// <summary>回滚到快照：<c>snap_id u64</c>。</summary>
    public static byte[] RollbackTo(ulong snapshotId) => new WireWriter().U64(snapshotId).ToArray();

    /// <summary>抽随机数：<c>count u32</c>。</summary>
    public static byte[] PrngNext(int count) => new WireWriter().U32((uint)count).ToArray();

    /// <summary>影子签名：<c>trits</c>。</summary>
    public static byte[] ShadowRecord(TritDelta[] signature) => new WireWriter().Trits(signature).ToArray();

    /// <summary>能力查询：空载荷。</summary>
    public static byte[] Capabilities() => Array.Empty<byte>();

    /// <summary>统计查询：空载荷。</summary>
    public static byte[] Stats() => Array.Empty<byte>();
}
