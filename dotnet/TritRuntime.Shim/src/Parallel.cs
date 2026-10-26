using System.Text.Json;

namespace TritRuntime.Kernel;

/// <summary>扫描任务种类，与 Rust 侧 JSON 契约逐项对齐。</summary>
public enum ScanKind
{
    /// <summary>
    /// 未知种类：请求里的种类名无法识别。
    /// </summary>
    /// <remarks>
    /// 原生内核与托管回退内核都必须把「无法识别的种类」收敛到这一个取值，
    /// 并在任务记录的 <c>kind</c> 字段里写 <c>"unknown"</c>——
    /// 否则同一份坏请求在两条路径上会给出不同的机器可读结果，影子差分一直报分歧。
    /// 用户传入的<b>原文</b>只出现在错误文案里（<c>未知扫描 kind：xxx</c>）。
    /// </remarks>
    Unknown = -1,

    /// <summary>三值直方图。</summary>
    Histogram = 0,

    /// <summary>滑动窗口模式匹配。</summary>
    Pattern = 1,

    /// <summary>分块非零密度。</summary>
    Density = 2,

    /// <summary>三值点积。</summary>
    Dot = 3,
}

/// <summary>扫描任务终态。</summary>
public enum JobState
{
    /// <summary>已完成。</summary>
    Done = 0,

    /// <summary>已取消。</summary>
    Cancelled = 1,

    /// <summary>执行失败。</summary>
    Failed = 2,
}

/// <summary>GPU 使用策略。</summary>
public enum GpuPolicy
{
    /// <summary>由运行时自动选择。</summary>
    Auto = 0,

    /// <summary>关闭 GPU。</summary>
    Off = 1,

    /// <summary>启用 GPU；不可用时仍安全回退。</summary>
    On = 2,
}

/// <summary>扫描任务请求。</summary>
public sealed class ScanRequest
{
    /// <summary>资源计费归属；空串由内核归为 <c>unknown</c>。</summary>
    public string Owner { get; init; } = "unknown";

    /// <summary>扫描种类。</summary>
    public ScanKind Kind { get; init; }

    /// <summary>
    /// 用户传入的种类<b>原文</b>（未经解析）。
    /// </summary>
    /// <remarks>
    /// 枚举装不下「无法识别的种类」，失败文案却要回显用户到底写了什么。
    /// 这个字段就是给文案用的：原生内核与托管回退内核都从 JSON 原文取它，
    /// 保证错误文案逐字相同。
    /// 为空串时表示「调用方直接给了枚举」，此时错误文案里的种类名由
    /// <see cref="Requests.ScanKindName"/> 给出。
    /// </remarks>
    public string KindText { get; init; } = string.Empty;

    /// <summary>输入三值数组，每个元素必须是 0、1 或 2。</summary>
    public byte[] Trits { get; init; } = [];

    /// <summary>模式或点积的右侧三值数组。</summary>
    public byte[]? Pattern { get; init; }

    /// <summary>模式命中阈值；非正数表示使用满分阈值。</summary>
    public int Threshold { get; init; }

    /// <summary>分步或密度分块大小；非正数使用内核默认值。</summary>
    public int Chunk { get; init; }

    /// <summary>是否一次执行且不可抢占。</summary>
    public bool Oneshot { get; init; }

    /// <summary>调度优先级，数值越大越优先。</summary>
    public int Priority { get; init; }

    /// <summary>密度结果最多返回的块数。</summary>
    public int Top { get; init; } = 16;
}

/// <summary>一个扫描任务的终态结果；<paramref name="ResultJson"/> 保留内核结果对象原文。</summary>
public sealed record JobResult(
    ulong Id,
    string Owner,
    ScanKind Kind,
    JobState State,
    string ResultJson,
    string Error,
    ulong Steps,
    string Backend);

/// <summary>GPU 后端状态与自检结果。</summary>
public sealed record GpuStatusReport(
    bool Available,
    bool Enabled,
    GpuPolicy Policy,
    string Adapter,
    string Backend,
    bool BuiltWithGpu,
    string Reason,
    long CpuDot,
    long GpuDot,
    bool Matches,
    long MinTrits,
    double NsPerTrit,
    ulong Jobs,
    ulong GpuJobs,
    ulong Fallbacks);

/// <summary>单个模组的资源预算累计账本。</summary>
public sealed record ModBudget(
    string Owner,
    int Inflight,
    int Queued,
    ulong Submitted,
    ulong Completed,
    ulong Cancelled,
    ulong Rejected,
    ulong Steps);

/// <summary>所有模组的资源预算累计值。</summary>
public sealed record BudgetTotals(
    ulong Submitted,
    ulong Completed,
    ulong Cancelled,
    ulong Rejected,
    ulong Steps);

/// <summary>最近一轮任务池调度指标。</summary>
public sealed record PoolMetrics(
    ulong Started,
    ulong Completed,
    ulong Failed,
    ulong Cancelled,
    ulong Deferred,
    ulong Preemptions,
    ulong Steps,
    int PeakCpu,
    int PeakMemory);

/// <summary>全局资源预算快照。</summary>
public sealed record BudgetSnapshot(
    int Workers,
    int CpuCapacity,
    long MemoryCapacity,
    int PerModInflight,
    int Inflight,
    int Queued,
    IReadOnlyList<ModBudget> Mods,
    BudgetTotals Totals,
    PoolMetrics? LastMetrics);

/// <summary>扫描结果 JSON 的只读、无异常便利解析器。</summary>
public static class ScanResultJson
{
    /// <summary>尝试读取三值直方图。</summary>
    public static bool TryGetHistogram(string json, out long neg, out long zero, out long pos)
    {
        neg = 0;
        zero = 0;
        pos = 0;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("histogram", out var histogram)
                || !TryInt64(histogram, "neg", out neg)
                || !TryInt64(histogram, "zero", out zero)
                || !TryInt64(histogram, "pos", out pos))
            {
                neg = 0;
                zero = 0;
                pos = 0;
                return false;
            }

            return true;
        }
        catch (Exception)
        {
            neg = 0;
            zero = 0;
            pos = 0;
            return false;
        }
    }

    /// <summary>尝试读取点积值。</summary>
    public static bool TryGetDot(string json, out long dot)
    {
        dot = 0;
        try
        {
            using var document = JsonDocument.Parse(json);
            return TryInt64(document.RootElement, "dot", out dot);
        }
        catch (Exception)
        {
            dot = 0;
            return false;
        }
    }

    /// <summary>读取模式命中项；JSON 非法时返回空列表。</summary>
    public static IReadOnlyList<(long Index, long Score)> TryGetMatches(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("matches", out var matches)
                || matches.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<(long Index, long Score)>();
            }

            var result = new List<(long Index, long Score)>();
            foreach (var item in matches.EnumerateArray())
            {
                if (TryInt64(item, "index", out var index) && TryInt64(item, "score", out var score))
                {
                    result.Add((index, score));
                }
            }

            return result;
        }
        catch (Exception)
        {
            return Array.Empty<(long Index, long Score)>();
        }
    }

    /// <summary>读取密度块；JSON 非法时返回空列表。</summary>
    public static IReadOnlyList<(long Block, long Index, long NonZero, double Density)> TryGetBlocks(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("blocks", out var blocks)
                || blocks.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<(long Block, long Index, long NonZero, double Density)>();
            }

            var result = new List<(long Block, long Index, long NonZero, double Density)>();
            foreach (var item in blocks.EnumerateArray())
            {
                if (TryInt64(item, "block", out var block)
                    && TryInt64(item, "index", out var index)
                    && TryInt64(item, "nonzero", out var nonZero)
                    && TryDouble(item, "density", out var density))
                {
                    result.Add((block, index, nonZero, density));
                }
            }

            return result;
        }
        catch (Exception)
        {
            return Array.Empty<(long Block, long Index, long NonZero, double Density)>();
        }
    }

    /// <summary>尝试读取模式扫描窗口数。</summary>
    public static bool TryGetScanned(string json, out long scanned)
    {
        scanned = 0;
        try
        {
            using var document = JsonDocument.Parse(json);
            return TryInt64(document.RootElement, "scanned", out scanned);
        }
        catch (Exception)
        {
            scanned = 0;
            return false;
        }
    }

    /// <summary>读取扫描后端；JSON 非法或字段缺失时返回空字符串。</summary>
    public static string TryGetBackend(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty("backend", out var backend)
                   && backend.ValueKind == JsonValueKind.String
                ? backend.GetString() ?? string.Empty
                : string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static bool TryInt64(JsonElement parent, string name, out long value)
    {
        value = 0;
        return parent.ValueKind == JsonValueKind.Object
               && parent.TryGetProperty(name, out var element)
               && element.ValueKind == JsonValueKind.Number
               && element.TryGetInt64(out value);
    }

    private static bool TryDouble(JsonElement parent, string name, out double value)
    {
        value = 0;
        return parent.ValueKind == JsonValueKind.Object
               && parent.TryGetProperty(name, out var element)
               && element.ValueKind == JsonValueKind.Number
               && element.TryGetDouble(out value);
    }
}
