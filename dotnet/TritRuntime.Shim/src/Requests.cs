#nullable enable

using System.Buffers;
using System.Text.Json;
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

    /// <summary>批量提交：<c>txn_id u64, count u32, [key u32, delta u8] * n</c>。</summary>
    public static byte[] TxnSubmitBatch(ulong txnId, IReadOnlyList<uint> keys, IReadOnlyList<TritDelta> deltas)
    {
        if (keys.Count != deltas.Count)
        {
            throw new ArgumentException("批量提交的 keys 与 deltas 条数必须一致", nameof(deltas));
        }

        // 载荷长度可精确算出：u64 txn_id + u32 count + 每条 (u32 key + u8 delta)。
        var writer = new WireWriter(12 + keys.Count * 5).U64(txnId).U32((uint)keys.Count);
        for (int i = 0; i < keys.Count; i++)
        {
            writer.U32(keys[i]).U8((byte)deltas[i]);
        }

        return writer.ToArray();
    }

    /// <summary>一步提交整批：<c>count u32, [key u32, delta u8] * n</c>（无事务号）。</summary>
    public static byte[] TxnApply(IReadOnlyList<uint> keys, IReadOnlyList<TritDelta> deltas)
    {
        if (keys.Count != deltas.Count)
        {
            throw new ArgumentException("批量提交的 keys 与 deltas 条数必须一致", nameof(deltas));
        }

        // u32 count + 每条 (u32 key + u8 delta)。
        var writer = new WireWriter(4 + keys.Count * 5).U32((uint)keys.Count);
        for (int i = 0; i < keys.Count; i++)
        {
            writer.U32(keys[i]).U8((byte)deltas[i]);
        }

        return writer.ToArray();
    }

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

    /// <summary>提交扫描任务：UTF-8 JSON；大三值数组直接流式写入缓冲区。</summary>
    public static byte[] ScanSubmit(ScanRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = JsonEncoder }))
        {
            writer.WriteStartObject();
            writer.WriteString("owner", NormalizeOwner(request.Owner));
            // 种类：优先写调用方给的**原文**（失败文案要回显它），
            // 没给原文就写规范名。两条路径（原生 / 托管回退）读的是同一份 JSON。
            writer.WriteString(
                "kind",
                string.IsNullOrWhiteSpace(request.KindText)
                    ? ScanKindName(request.Kind)
                    : request.KindText);
            writer.WritePropertyName("trits");
            WriteTrits(writer, request.Trits ?? Array.Empty<byte>());
            writer.WritePropertyName("pattern");
            if (request.Pattern is null)
            {
                writer.WriteNullValue();
            }
            else
            {
                WriteTrits(writer, request.Pattern);
            }

            writer.WriteNumber("threshold", request.Threshold);
            writer.WriteNumber("chunk", request.Chunk);
            writer.WriteBoolean("oneshot", request.Oneshot);
            writer.WriteNumber("priority", request.Priority);
            writer.WriteNumber("top", request.Top);
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>取出已完成任务：<c>{"limit":n}</c>；带 owner 过滤时多一个 <c>{"owner":"..."}</c>。</summary>
    /// <param name="limit">最多取几条；<c>&lt;= 0</c> 表示全部。</param>
    /// <param name="owner">
    /// 归属过滤；<see langword="null"/> 表示不限归属。
    /// 空串会被内核归一化为 <c>unknown</c>（与提交侧同一口径），
    /// 所以「以空 owner 提交」的任务要用 <c>""</c> 或 <c>"unknown"</c> 过滤才能取到。
    /// </param>
    public static byte[] JobPoll(int limit, string? owner = null) =>
        Json(writer =>
        {
            writer.WriteNumber("limit", limit);
            if (owner is not null)
            {
                writer.WriteString("owner", NormalizeOwner(owner));
            }
        });

    /// <summary>按任务号协作式取消：<c>{"job":id}</c>。</summary>
    public static byte[] JobCancelJob(ulong id) => Json(writer => writer.WriteNumber("job", id));

    /// <summary>按 owner 协作式取消：<c>{"owner":"..."}</c>。</summary>
    public static byte[] JobCancelOwner(string owner) =>
        Json(writer => writer.WriteString("owner", owner ?? string.Empty));

    /// <summary>查询或切换 GPU 状态。</summary>
    public static byte[] GpuStatus(GpuPolicy? policy, bool probe, long trits)
    {
        return Json(writer =>
        {
            if (policy.HasValue)
            {
                writer.WriteString("policy", GpuPolicyName(policy.Value));
            }

            writer.WriteBoolean("probe", probe);
            writer.WriteNumber("trits", trits);
        });
    }

    /// <summary>查询全局预算。</summary>
    public static byte[] Budget() => Json(_ => { });

    /// <summary>查询 Prometheus 指标。</summary>
    public static byte[] Metrics() => Json(_ => { });

    /// <summary>
    /// JSON 请求体的编码器：不转义非 ASCII 字符（与 Rust 侧 serde_json 对齐）。
    /// </summary>
    /// <remarks>
    /// 默认编码器会把中文写成 <c>é</c> 这样的转义序列，
    /// 与原生内核产出的原始 UTF-8 字节不同；请求体是要送进原生内核的，
    /// 两边看到的字节必须一致。
    /// </remarks>
    private static readonly System.Text.Encodings.Web.JavaScriptEncoder JsonEncoder =
        System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping;

    private static byte[] Json(Action<Utf8JsonWriter> writeProperties)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = JsonEncoder }))
        {
            writer.WriteStartObject();
            writeProperties(writer);
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    private static void WriteTrits(Utf8JsonWriter writer, byte[] trits)
    {
        writer.WriteStartArray();
        foreach (var trit in trits)
        {
            writer.WriteNumberValue(trit);
        }

        writer.WriteEndArray();
    }

    /// <summary>
    /// 归属名归一化：**去首尾空白 → 空则归为 <c>unknown</c>**。
    /// </summary>
    /// <remarks>
    /// 必须与 Rust 侧 <c>normalize_owner</c> <b>逐字对齐</b>。
    /// 这里是两条路径（原生 / 托管回退）共用的请求构造器：
    /// 归一化规则一旦分叉，同一份请求在两个内核上就会算到不同的 owner 上，
    /// 影子差分会永远报不一致，而且这种不一致极难从日志里看出来。
    /// </remarks>
    public static string NormalizeOwner(string? owner)
    {
        var trimmed = owner?.Trim() ?? string.Empty;
        return trimmed.Length == 0 ? "unknown" : trimmed;
    }

    /// <summary>
    /// 种类名 → JSON 契约里的规范名。
    /// </summary>
    /// <remarks>
    /// 未知种类（含 <see cref="ScanKind.Unknown"/> 与任何非法枚举值）一律给
    /// <c>"unknown"</c>，与 Rust 侧 <c>ScanKind::canonical_text</c> 逐字对齐。
    /// 用户传入的原文不在这里体现——它只出现在失败文案里。
    /// </remarks>
    public static string ScanKindName(ScanKind kind) => kind switch
    {
        ScanKind.Histogram => "histogram",
        ScanKind.Pattern => "pattern",
        ScanKind.Density => "density",
        ScanKind.Dot => "dot",
        _ => "unknown",
    };

    private static string GpuPolicyName(GpuPolicy policy) => policy switch
    {
        GpuPolicy.Auto => "auto",
        GpuPolicy.Off => "off",
        GpuPolicy.On => "on",
        _ => policy.ToString().ToLowerInvariant(),
    };
}
