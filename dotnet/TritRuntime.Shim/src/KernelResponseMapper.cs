using System.Text.Json;
using TritRuntime.Wire;

namespace TritRuntime.Kernel;

/// <summary>
/// 把 <see cref="KernelResponse"/> 的载荷解码成强类型结果，字段布局与 Rust 侧
/// <c>trit_rt::wire</c> 的响应编码逐项对齐。原生内核与托管回退内核共用本类——
/// 同一份解码逻辑跑在两种内核上，正是「语义等价」的可执行证明。
/// </summary>
public static class KernelResponseMapper
{
    /// <summary>订阅 → <c>sub_id u64</c>。</summary>
    public static ulong ToSubId(KernelResponse response) => Reader(response, out var reader).U64();

    /// <summary>发布 → <c>dirty_count u32</c>。</summary>
    public static uint ToDirtyCount(KernelResponse response) => Reader(response, out var reader).U32();

    /// <summary>开事务 → <c>txn_id u64</c>。</summary>
    public static ulong ToTxnId(KernelResponse response) => Reader(response, out var reader).U64();

    /// <summary>通用 u64（订阅号 / 事务号 / 快照号 / 快照字节）。</summary>
    public static ulong ToU64(KernelResponse response, string? detail = null) =>
        Reader(response, detail, out var reader).U64();

    /// <summary>通用 u32。</summary>
    public static uint ToU32(KernelResponse response, string? detail = null) =>
        Reader(response, detail, out var reader).U32();

    /// <summary>提交事务 → <c>applied u32, log_seq u64</c>。</summary>
    public static TxnCommitResult ToCommitResult(KernelResponse response, string? detail = null)
    {
        var reader = Reader(response, detail, out _);
        var applied = reader.U32();
        var logSeq = reader.U64();
        return new TxnCommitResult(applied, logSeq);
    }

    /// <summary>仲裁 → <c>status u8, resolved_count u32, [key u32, delta u8] * n, dispute_count u32, disputes</c>。</summary>
    public static ArbitrateResult ToArbitrateResult(KernelResponse response, string? detail = null)
    {
        var reader = Reader(response, detail, out _);
        var hasDisputes = reader.U8() != 0;
        var resolvedCount = checked((int)reader.U32());
        var resolved = new List<(uint Key, TritDelta Delta)>(Math.Min(resolvedCount, 4096));
        for (var i = 0; i < resolvedCount; i++)
        {
            var key = reader.U32();
            var delta = Trit(reader.U8());
            resolved.Add((key, delta));
        }

        var disputeCount = checked((int)reader.U32());
        var disputes = new List<LaneDispute>(Math.Min(disputeCount, 4096));
        for (var i = 0; i < disputeCount; i++)
        {
            var key = reader.U32();
            var driverCount = checked((int)reader.U32());
            var drivers = new List<(ulong TxnId, TritDelta Delta)>(Math.Min(driverCount, 4096));
            for (var d = 0; d < driverCount; d++)
            {
                var txnId = reader.U64();
                var delta = Trit(reader.U8());
                drivers.Add((txnId, delta));
            }

            disputes.Add(new LaneDispute(key, drivers));
        }

        return new ArbitrateResult(hasDisputes, resolved, disputes);
    }

    /// <summary>step → <c>intent_count u32, [sub_id u64, event_id u32, mod_id u32] * n</c>。</summary>
    public static StepResult ToStepResult(KernelResponse response, string? detail = null)
    {
        var reader = Reader(response, detail, out _);
        var count = checked((int)reader.U32());
        var intents = new List<ActionIntent>(Math.Min(count, 4096));
        for (var i = 0; i < count; i++)
        {
            var subId = reader.U64();
            var eventId = reader.U32();
            var modId = reader.U32();
            intents.Add(new ActionIntent(subId, eventId, modId));
        }

        return new StepResult(intents);
    }

    /// <summary>差分 → <c>entry_count u32, [key u32, old u8, new u8] * n</c>。</summary>
    public static DiffResult ToDiffResult(KernelResponse response, string? detail = null)
    {
        var reader = Reader(response, detail, out _);
        var count = checked((int)reader.U32());
        var entries = new List<DiffEntry>(Math.Min(count, 4096));
        for (var i = 0; i < count; i++)
        {
            var key = reader.U32();
            var old = Trit(reader.U8());
            var updated = Trit(reader.U8());
            entries.Add(new DiffEntry(key, old, updated));
        }

        return new DiffResult(entries);
    }

    /// <summary>trit 序列（PRNG 抽样 / 事件载荷查询）。</summary>
    public static TritDelta[] ToTrits(KernelResponse response, string? detail = null)
    {
        var reader = Reader(response, detail, out _);
        return reader.Trits();
    }

    /// <summary>统计 → 10 个 u64（顺序见 docs/ABI.md）。</summary>
    public static ulong[] ToStats(KernelResponse response, string? detail = null)
    {
        var reader = Reader(response, detail, out _);
        var values = new ulong[10];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = reader.U64();
        }

        return values;
    }

    /// <summary>能力报告 → <c>abi u32, version u32, caps u64, mode u8</c>。</summary>
    /// <param name="response">内核响应。</param>
    /// <param name="mode">运行模式由调用方给出（载荷里没有这一字节）。</param>
    /// <param name="detail">可选的原生/托管侧错误细节。</param>
    public static CapabilityReport ToCapabilityReport(KernelResponse response, KernelMode mode, string? detail = null)
    {
        var reader = Reader(response, detail, out _);
        var abi = reader.U32();
        var version = reader.U32();
        var caps = (CapabilityBits)reader.U64();
        var arbitration = (ArbitrationMode)reader.U8();
        return new CapabilityReport(abi, version, caps, mode, arbitration);
    }

    /// <summary>只解冲突诊断（<c>dispute_count u32</c> + 争议车道），不抛异常。</summary>
    public static IReadOnlyList<LaneDispute> TryReadDisputes(KernelResponse response)
    {
        if (response.Status != Status.Conflict)
        {
            return Array.Empty<LaneDispute>();
        }

        try
        {
            var reader = new WireReader(response.Payload);
            var count = checked((int)reader.U32());
            var disputes = new List<LaneDispute>(Math.Min(count, 4096));
            for (var i = 0; i < count; i++)
            {
                var key = reader.U32();
                var driverCount = checked((int)reader.U32());
                var drivers = new List<(ulong TxnId, TritDelta Delta)>(Math.Min(driverCount, 4096));
                for (var d = 0; d < driverCount; d++)
                {
                    drivers.Add((reader.U64(), Trit(reader.U8())));
                }

                disputes.Add(new LaneDispute(key, drivers));
            }

            return disputes;
        }
        catch (WireException)
        {
            // 诊断字节不完整时不该掩盖原始错误：交还空清单，由状态码说话。
            return Array.Empty<LaneDispute>();
        }
    }

    /// <summary>解码扫描任务号；错误或非法 JSON 返回 0。</summary>
    public static ulong ToJobId(KernelResponse response) =>
        FromJson(response, 0UL, root => UInt64(root, "job"));

    /// <summary>解码已完成任务；错误或非法 JSON 返回空列表。</summary>
    public static IReadOnlyList<JobResult> ToJobResults(KernelResponse response) =>
        FromJson<IReadOnlyList<JobResult>>(response, Array.Empty<JobResult>(), root =>
        {
            if (!root.TryGetProperty("jobs", out var jobs) || jobs.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<JobResult>();
            }

            var result = new List<JobResult>();
            foreach (var job in jobs.EnumerateArray())
            {
                var resultJson = job.TryGetProperty("result", out var value)
                    ? value.GetRawText()
                    : "{}";
                result.Add(new JobResult(
                    UInt64(job, "id"),
                    String(job, "owner"),
                    ParseScanKind(String(job, "kind")),
                    ParseJobState(String(job, "state")),
                    resultJson,
                    String(job, "error"),
                    UInt64(job, "steps"),
                    String(job, "backend")));
            }

            return result;
        });

    /// <summary>解码取消数量；错误或非法 JSON 返回 0。</summary>
    public static int ToCancelled(KernelResponse response) =>
        FromJson(response, 0, root =>
        {
            var cancelled = UInt64(root, "cancelled");
            return cancelled > int.MaxValue ? int.MaxValue : (int)cancelled;
        });

    /// <summary>解码 GPU 状态；错误或非法 JSON 返回安全的不可用报告。</summary>
    public static GpuStatusReport ToGpuStatus(KernelResponse response) =>
        FromJson(response, EmptyGpuStatus(), root => new GpuStatusReport(
            Boolean(root, "available"),
            Boolean(root, "enabled"),
            ParseGpuPolicy(String(root, "policy")),
            String(root, "adapter"),
            String(root, "backend"),
            Boolean(root, "built_with_gpu"),
            String(root, "reason"),
            Int64(root, "cpu_dot"),
            Int64(root, "gpu_dot"),
            Boolean(root, "matches"),
            Int64(root, "min_trits"),
            Double(root, "ns_per_trit"),
            UInt64(root, "jobs"),
            UInt64(root, "gpu_jobs"),
            UInt64(root, "fallbacks")));

    /// <summary>解码全局预算；错误或非法 JSON 返回空快照。</summary>
    public static BudgetSnapshot ToBudget(KernelResponse response) =>
        FromJson(response, EmptyBudget(), root =>
        {
            var mods = new List<ModBudget>();
            if (root.TryGetProperty("mods", out var modList) && modList.ValueKind == JsonValueKind.Array)
            {
                foreach (var mod in modList.EnumerateArray())
                {
                    mods.Add(new ModBudget(
                        String(mod, "owner"),
                        Int32(mod, "inflight"),
                        Int32(mod, "queued"),
                        UInt64(mod, "submitted"),
                        UInt64(mod, "completed"),
                        UInt64(mod, "cancelled"),
                        UInt64(mod, "rejected"),
                        UInt64(mod, "steps")));
                }
            }

            var totals = root.TryGetProperty("totals", out var totalsElement)
                ? new BudgetTotals(
                    UInt64(totalsElement, "submitted"),
                    UInt64(totalsElement, "completed"),
                    UInt64(totalsElement, "cancelled"),
                    UInt64(totalsElement, "rejected"),
                    UInt64(totalsElement, "steps"))
                : new BudgetTotals(0, 0, 0, 0, 0);

            PoolMetrics? metrics = null;
            if (root.TryGetProperty("last_metrics", out var metricsElement)
                && metricsElement.ValueKind == JsonValueKind.Object)
            {
                metrics = new PoolMetrics(
                    UInt64(metricsElement, "started"),
                    UInt64(metricsElement, "completed"),
                    UInt64(metricsElement, "failed"),
                    UInt64(metricsElement, "cancelled"),
                    UInt64(metricsElement, "deferred"),
                    UInt64(metricsElement, "preemptions"),
                    UInt64(metricsElement, "steps"),
                    Int32(metricsElement, "peak_cpu"),
                    Int32(metricsElement, "peak_memory"));
            }

            return new BudgetSnapshot(
                Int32(root, "workers"),
                Int32(root, "cpu_capacity"),
                Int64(root, "memory_capacity"),
                Int32(root, "per_mod_inflight"),
                Int32(root, "inflight"),
                Int32(root, "queued"),
                mods,
                totals,
                metrics);
        });

    /// <summary>解码 Prometheus 文本；错误或非法 JSON 返回空字符串。</summary>
    public static string ToPrometheus(KernelResponse response) =>
        FromJson(response, string.Empty, root => String(root, "prometheus"));

    private static T FromJson<T>(KernelResponse response, T fallback, Func<JsonElement, T> mapper)
    {
        if (response.Status != Status.Ok)
        {
            return fallback;
        }

        try
        {
            using var document = JsonDocument.Parse(response.Payload);
            return document.RootElement.ValueKind == JsonValueKind.Object
                ? mapper(document.RootElement)
                : fallback;
        }
        catch (Exception)
        {
            return fallback;
        }
    }

    /// <summary>
    /// 解析内核返回的种类名。
    /// </summary>
    /// <remarks>
    /// 无法识别时给 <see cref="ScanKind.Unknown"/>，**不兜底成 Histogram**：
    /// 兜底会把「种类未知」这条失败信息悄悄改写成「直方图成功」，
    /// 调用方再也分不清自己是跑了个直方图还是踩了个未知种类。
    /// </remarks>
    private static ScanKind ParseScanKind(string value) => value.Trim().ToLowerInvariant() switch
    {
        "histogram" => ScanKind.Histogram,
        "pattern" => ScanKind.Pattern,
        "density" => ScanKind.Density,
        "dot" => ScanKind.Dot,
        _ => ScanKind.Unknown,
    };

    private static JobState ParseJobState(string value) => value.ToLowerInvariant() switch
    {
        "done" => JobState.Done,
        "cancelled" => JobState.Cancelled,
        "failed" => JobState.Failed,
        _ => JobState.Failed,
    };

    private static GpuPolicy ParseGpuPolicy(string value) => value.ToLowerInvariant() switch
    {
        "off" => GpuPolicy.Off,
        "on" => GpuPolicy.On,
        _ => GpuPolicy.Auto,
    };

    private static string String(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object
        && parent.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static bool Boolean(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object
        && parent.TryGetProperty(name, out var value)
        && (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False)
        && value.GetBoolean();

    private static int Int32(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object
        && parent.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var result)
            ? result
            : 0;

    private static long Int64(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object
        && parent.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt64(out var result)
            ? result
            : 0;

    private static ulong UInt64(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object
        && parent.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetUInt64(out var result)
            ? result
            : 0;

    private static double Double(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object
        && parent.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetDouble(out var result)
            ? result
            : 0;

    private static GpuStatusReport EmptyGpuStatus() => new(
        false,
        false,
        GpuPolicy.Auto,
        string.Empty,
        string.Empty,
        false,
        string.Empty,
        0,
        0,
        false,
        0,
        0,
        0,
        0,
        0);

    private static BudgetSnapshot EmptyBudget() => new(
        0,
        0,
        0,
        0,
        0,
        0,
        Array.Empty<ModBudget>(),
        new BudgetTotals(0, 0, 0, 0, 0),
        null);

    private static WireReader Reader(KernelResponse response, out WireReader reader) =>
        Reader(response, null, out reader);

    private static WireReader Reader(KernelResponse response, string? detail, out WireReader reader)
    {
        if (response.IsOk)
        {
            reader = new WireReader(response.Payload);
            return reader;
        }

        var disputes = TryReadDisputes(response);
        var suffix = string.IsNullOrEmpty(detail) ? string.Empty : $"：{detail}";
        throw new KernelException(
            response.Status,
            $"三进制内核返回 {response.Status}（{StatusNames.Describe(response.Status)}）{suffix}",
            disputes);
    }

    private static TritDelta Trit(byte value) => (TritDelta)value;
}
