#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using TritRuntime.Kernel;
using TritRuntime.Wire;

namespace TritRuntimeMod;

/// <summary>
/// 给其他模组用的强类型门面：<b>所有方法都不抛异常</b>。
/// </summary>
/// <remarks>
/// <para>
/// 运行时不可用（模组没装、内核没装配、能力缺失）时一律返回默认值：
/// <c>0</c> / <see langword="null"/> / 空列表 / <see langword="false"/>。
/// 调用方不需要写 try/catch——这也是本类存在的唯一理由：
/// 旁路层挂了不允许把别的模组一起拖下水。
/// </para>
/// <para>
/// <b>其他模组怎么接（两条路）。</b>
/// </para>
/// <para>
/// 一、不想引用本程序集（推荐，本模组不存在也不会编译失败），用 <c>Mod.Call</c>，
/// 命令名大小写不敏感，参数按位置传：
/// <code>
/// Mod trit = ModLoader.GetMod("TritRuntimeMod");
/// if (trit is null) return;                       // 旁路层没装，直接跳过
///
/// // trits：每元素一个 trit，0=Neg(-1) 1=Zero(0) 2=Pos(+1)
/// // kind 是<b>字符串</b>，大小写不敏感：histogram | pattern | density | dot
/// ulong job = (ulong)trit.Call(
///     "TritRuntime.Scan", "MyMod", "pattern", trits, pattern, 3);
///
/// // 不需要 pattern 的用三参数简写
/// ulong h = (ulong)trit.Call("TritRuntime.Scan", "MyMod", "histogram", trits);
///
/// if (job == 0) return;                           // 提交失败（没能力 / 超预算 / 参数非法）
///
/// // <b>多模组共存时按 owner 取</b>：只取自己的结果，不会抢别人的
/// var jobs = (IReadOnlyList&lt;JobResult&gt;)trit.Call("TritRuntime.PollJobs", "MyMod", 16);
/// trit.Call("TritRuntime.CancelJob", job);        // 取消单个（id 必须是 ulong/int/long）
/// trit.Call("TritRuntime.CancelOwner", "MyMod");  // 取消该 owner 全部排队任务
///
/// var gpu = (GpuStatusReport)trit.Call("TritRuntime.GpuStatus", "auto", false);
/// var budget = (BudgetSnapshot)trit.Call("TritRuntime.Budget");
/// var caps = (CapabilityReport)trit.Call("TritRuntime.Capabilities");
/// </code>
/// 命令名以 <c>TritRuntime.</c> 开头且大小写不敏感，分支全在兼容壳里，本模组只转发。
/// 注意：<c>Mod.Call</c> 的参数个数与类型必须严格对上（见兼容壳里的签名表），
/// 对不上会抛 <see cref="ArgumentException"/>——那是兼容壳定的契约，不是本门面的行为。
/// </para>
/// <para>
/// 二、引用了本程序集，直接用本类的静态方法，编译期就有类型检查：
/// <code>
/// if (!TritApi.Supports(CapabilityBits.Parallel)) return;   // 没有任务池，别提交
///
/// ulong job = TritApi.Scan("MyMod", ScanKind.Histogram, trits);
/// if (job == 0) return;
///
/// // 同步短轮询：最多阻塞 100 ms，别在主线程上频繁调
/// if (TritApi.TryPollJob(job, "MyMod", out JobResult? r))
/// {
///     // r.ResultJson 是内核给的 JSON，例如 {"histogram":{"neg":1,"zero":2,"pos":3}}
/// }
///
/// // 每 tick 取一批（按 owner，只取自己那份）
/// IReadOnlyList&lt;JobResult&gt; batch = TritApi.PollJobs("MyMod", 16);
///
/// BudgetSnapshot? snap = TritApi.Budget();
/// </code>
/// </para>
/// <para>
/// 取出即出队：所有 <c>Poll</c> 类调用都会把结果从内核队列里拿走。
/// 本门面会把取到的结果顺手塞进模组的"最近结果"环形缓冲，
/// 所以 <c>trit jobs</c> 命令仍然看得到，不会凭空丢失。
/// </para>
/// </remarks>
public static class TritApi
{
    /// <summary>
    /// 默认短轮询超时（毫秒）。刻意压到 100：这是<b>同步阻塞主线程</b>的调用，
    /// 而 <see cref="Thread.Sleep(int)"/> 在 Windows 上的实际粒度约 15 ms，
    /// 200 ms 会变成十几段可见卡顿。要结果请用每 tick 一次的 <see cref="PollJobs"/>。
    /// </summary>
    public const int DefaultPollTimeoutMilliseconds = 100;

    /// <summary>短轮询超时上限（毫秒）：同步阻塞主线程，必须封顶。</summary>
    public const int MaxPollTimeoutMilliseconds = 2_000;

    /// <summary>同类失败日志的最小间隔（毫秒）：避免每次调用都刷日志。</summary>
    private const long WarnThrottleMilliseconds = 60_000L;

    private static long lastWarnAt = long.MinValue;

    /// <summary>运行时是否可用（模组已装载且内核已装配）。</summary>
    public static bool Available => TritRuntimeMod.Instance is { RuntimeAvailable: true };

    /// <summary>能力报告；拿不到时返回全 0 的安全默认（不返回 <see langword="null"/>）。</summary>
    public static CapabilityReport Capabilities()
    {
        if (!TryGetRuntime(out RuntimeShim? shim))
        {
            return SafeCapabilities();
        }

        try
        {
            return shim!.Capabilities() ?? SafeCapabilities();
        }
        catch (Exception ex)
        {
            WarnThrottled("Capabilities 调用失败：" + ex.Message);
            return SafeCapabilities();
        }
    }

    /// <summary>是否具备某能力。</summary>
    public static bool Supports(CapabilityBits bit)
    {
        if (!TryGetRuntime(out RuntimeShim? shim))
        {
            return false;
        }

        try
        {
            return shim!.Supports(bit);
        }
        catch (Exception ex)
        {
            WarnThrottled("Supports 调用失败：" + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// 提交一个扫描任务（立即返回，任务在旁路线程上跑，游戏主线程从不等待）。
    /// </summary>
    /// <param name="owner">归属模组名（计费/配额用）；空或空白一律归为 <c>unknown</c>。</param>
    /// <param name="kind">扫描种类。</param>
    /// <param name="trits">三值序列，每元素一个 trit：0=Neg、1=Zero、2=Pos；<see langword="null"/> 按空数组处理。</param>
    /// <param name="pattern">pattern / dot 用的窗口序列；其余种类忽略。</param>
    /// <param name="threshold">pattern 命中最低分；0 = 满分（逐元素全等）。</param>
    /// <param name="chunk">分步粒度；0 = 用配置里的 scan_chunk。</param>
    /// <param name="oneshot">true = 不分步、不可抢占。</param>
    /// <param name="priority">越大越优先。</param>
    /// <param name="top">density 返回的块数上限。</param>
    /// <returns>任务 id；<b>0 表示提交失败</b>（id 从 1 开始单调递增）。</returns>
    public static ulong Scan(
        string owner,
        ScanKind kind,
        byte[] trits,
        byte[]? pattern = null,
        int threshold = 0,
        int chunk = 0,
        bool oneshot = false,
        int priority = 0,
        int top = 16)
    {
        if (!TryGetRuntime(out RuntimeShim? shim))
        {
            return 0;
        }

        try
        {
            return shim!.SubmitScan(new ScanRequest
            {
                Owner = string.IsNullOrWhiteSpace(owner) ? "unknown" : owner.Trim(),
                Kind = kind,
                Trits = trits ?? Array.Empty<byte>(),
                Pattern = pattern,
                Threshold = threshold,
                Chunk = chunk,
                Oneshot = oneshot,
                Priority = priority,
                Top = top,
            });
        }
        catch (Exception ex)
        {
            WarnThrottled("Scan 提交失败：" + ex.Message);
            return 0;
        }
    }

    /// <summary>
    /// 取回已完成的任务结果（<b>取出即出队</b>）。<paramref name="limit"/> 为 0 表示全取。
    /// </summary>
    /// <remarks>
    /// <b>不区分归属</b>：会把<b>所有</b> owner 的结果都取走。
    /// 多个模组共存时应当改用 <see cref="PollJobs(string, int)"/>，
    /// 否则本调用会把别的模组的结果一起吃掉。
    /// </remarks>
    public static IReadOnlyList<JobResult> PollJobs(int limit = 0)
    {
        if (!TryGetRuntime(out RuntimeShim? shim))
        {
            return Array.Empty<JobResult>();
        }

        try
        {
            IReadOnlyList<JobResult> jobs = shim!.PollJobs(limit) ?? Array.Empty<JobResult>();
            PushRecent(jobs);
            return jobs;
        }
        catch (Exception ex)
        {
            WarnThrottled("PollJobs 调用失败：" + ex.Message);
            return Array.Empty<JobResult>();
        }
    }

    /// <summary>
    /// 取回<b>指定归属</b>的任务结果（取出即出队）。
    /// </summary>
    /// <param name="owner">归属模组名；空或空白按 <c>unknown</c> 处理。</param>
    /// <param name="limit">最多取几条；<c>0</c> 表示全取。</param>
    /// <remarks>
    /// <para>
    /// <b>多个模组共存时用这个重载</b>：结果按 owner 分桶投递，
    /// 各模组只取自己那一份，互不吞掉。
    /// </para>
    /// <para>
    /// 无参的 <see cref="PollJobs(int)"/> 会把所有 owner 的结果都取走——
    /// 在多模组环境下那等于抢别人的东西。
    /// </para>
    /// </remarks>
    public static IReadOnlyList<JobResult> PollJobs(string owner, int limit = 0)
    {
        TritRuntimeMod? mod = TritRuntimeMod.Instance;
        if (mod is null || !mod.RuntimeAvailable)
        {
            WarnThrottled("三进制运行时未装配，本次调用按默认值处理。");
            return Array.Empty<JobResult>();
        }

        try
        {
            // 结果已在每 tick 的自动轮询里按 owner 分好桶，这里只取自己那桶。
            IReadOnlyList<JobResult> jobs = mod.TakeOwnerJobs(owner, limit);
            if (jobs.Count > 0)
            {
                return jobs;
            }

            // 自己那桶是空的：可能自动轮询还没跑到这一批（刚提交就取）。
            // 直接问内核要本 owner 的结果——内核侧同样按 owner 过滤，
            // 不会把别人的结果带回来。
            if (!TryGetRuntime(out RuntimeShim? shim))
            {
                return Array.Empty<JobResult>();
            }

            IReadOnlyList<JobResult> direct = shim!.PollJobs(limit, owner) ?? Array.Empty<JobResult>();
            PushRecent(direct);
            return direct;
        }
        catch (Exception ex)
        {
            WarnThrottled("PollJobs(owner) 调用失败：" + ex.Message);
            return Array.Empty<JobResult>();
        }
    }

    /// <summary>
    /// 同步短轮询：直到拿到 <paramref name="id"/> 的结果或超时。
    /// </summary>
    /// <param name="id">要等的任务 id。</param>
    /// <param name="result">拿到的结果；没拿到为 <see langword="null"/>。</param>
    /// <param name="timeoutMs">超时上限（毫秒），夹到 0..<see cref="MaxPollTimeoutMilliseconds"/>。</param>
    /// <remarks>
    /// <b>会阻塞调用线程（默认最多 100 ms）</b>。它存在的意义是给"提交后立刻要结果"
    /// 的极短任务兜底，正常用法是每 tick 调 <see cref="PollJobs(string, int)"/> 拿批量结果——
    /// 后者从不等待。
    /// </remarks>
    /// <remarks>
    /// <b>归属不明的重载会取走所有 owner 的结果</b>（内核侧按 id 找不到时整批取回）。
    /// 多模组环境下请用 <see cref="TryPollJob(ulong, string, out JobResult?, int)"/>，
    /// 它只取本 owner 的结果。
    /// </remarks>
    public static bool TryPollJob(ulong id, out JobResult? result, int timeoutMs = DefaultPollTimeoutMilliseconds) =>
        TryPollJob(id, null, out result, timeoutMs);

    /// <summary>
    /// 同步短轮询（<b>按归属</b>）：直到拿到 <paramref name="id"/> 的结果或超时。
    /// </summary>
    /// <param name="id">要等的任务 id。</param>
    /// <param name="owner">归属模组名；空或空白按 <c>unknown</c> 处理。</param>
    /// <param name="result">拿到的结果；没拿到为 <see langword="null"/>。</param>
    /// <param name="timeoutMs">超时上限（毫秒），夹到 0..<see cref="MaxPollTimeoutMilliseconds"/>。</param>
    /// <remarks>
    /// <b>多模组环境请用这个重载</b>：轮询期间只取本 owner 的结果，
    /// 不会把别的模组的任务结果一起取走。
    /// </remarks>
    public static bool TryPollJob(
        ulong id,
        string? owner,
        out JobResult? result,
        int timeoutMs = DefaultPollTimeoutMilliseconds)
    {
        result = null;
        if (id == 0)
        {
            return false;
        }

        if (!TryGetRuntime(out RuntimeShim? shim))
        {
            return false;
        }

        try
        {
            // 先在环形缓冲里找：每 tick 的自动轮询很可能已经把它取回来了，
            // 命中就一次内核调用都不用。
            if (TryFindRecent(id, out JobResult? cached))
            {
                result = cached;
                return true;
            }

            long deadline = Environment.TickCount64
                + Math.Clamp(timeoutMs, 0, MaxPollTimeoutMilliseconds);

            do
            {
                // owner 为 null 时不限归属（老行为）；给了就只取该 owner 的，
                // 别的模组的任务结果不会被顺走。
                IReadOnlyList<JobResult> jobs = shim!.PollJobs(0, owner);
                if (jobs is { Count: > 0 })
                {
                    PushRecent(jobs);
                    if (TryFindRecent(id, out JobResult? hit))
                    {
                        result = hit;
                        return true;
                    }
                }

                if (Environment.TickCount64 >= deadline)
                {
                    break;
                }

                Thread.Sleep(1);
            }
            while (Environment.TickCount64 < deadline);

            return false;
        }
        catch (Exception ex)
        {
            WarnThrottled("TryPollJob 调用失败：" + ex.Message);
            return false;
        }
    }

    /// <summary>取消单个任务，返回被取消的任务数（0 = 没取消到）。</summary>
    public static int CancelJob(ulong id)
    {
        if (id == 0)
        {
            return 0;
        }

        if (!TryGetRuntime(out RuntimeShim? shim))
        {
            return 0;
        }

        try
        {
            return shim!.CancelJob(id);
        }
        catch (Exception ex)
        {
            WarnThrottled("CancelJob 调用失败：" + ex.Message);
            return 0;
        }
    }

    /// <summary>取消某个 owner 的全部排队任务，返回被取消的任务数。</summary>
    public static int CancelOwner(string owner)
    {
        if (string.IsNullOrWhiteSpace(owner))
        {
            return 0;
        }

        if (!TryGetRuntime(out RuntimeShim? shim))
        {
            return 0;
        }

        try
        {
            return shim!.CancelOwner(owner.Trim());
        }
        catch (Exception ex)
        {
            WarnThrottled("CancelOwner 调用失败：" + ex.Message);
            return 0;
        }
    }

    /// <summary>
    /// GPU 状态。<paramref name="policy"/> 给值即切换策略；<paramref name="probe"/> 为 true 强制重新探测（耗时，慎用）。
    /// </summary>
    public static GpuStatusReport? GpuStatus(GpuPolicy? policy = null, bool probe = false)
    {
        if (!TryGetRuntime(out RuntimeShim? shim))
        {
            return null;
        }

        try
        {
            return shim!.GpuStatus(policy, probe);
        }
        catch (Exception ex)
        {
            WarnThrottled("GpuStatus 调用失败：" + ex.Message);
            return null;
        }
    }

    /// <summary>全局资源预算快照（各模组的提交/完成/拒绝与排队情况）。</summary>
    public static BudgetSnapshot? Budget()
    {
        if (!TryGetRuntime(out RuntimeShim? shim))
        {
            return null;
        }

        try
        {
            return shim!.Budget();
        }
        catch (Exception ex)
        {
            WarnThrottled("Budget 调用失败：" + ex.Message);
            return null;
        }
    }

    /// <summary>最近一轮任务池指标的 Prometheus 文本；拿不到返回空串。</summary>
    public static string Metrics()
    {
        if (!TryGetRuntime(out RuntimeShim? shim))
        {
            return string.Empty;
        }

        try
        {
            return shim!.MetricsPrometheus() ?? string.Empty;
        }
        catch (Exception ex)
        {
            WarnThrottled("Metrics 调用失败：" + ex.Message);
            return string.Empty;
        }
    }

    /// <summary>内核统计计数器；拿不到返回 <see langword="null"/>。</summary>
    public static ulong[]? Stats()
    {
        if (!TryGetRuntime(out RuntimeShim? shim))
        {
            return null;
        }

        try
        {
            return shim!.Stats();
        }
        catch (Exception ex)
        {
            WarnThrottled("Stats 调用失败：" + ex.Message);
            return null;
        }
    }

    private static bool TryGetRuntime(out RuntimeShim? shim)
    {
        TritRuntimeMod? mod = TritRuntimeMod.Instance;
        if (mod is not null && mod.RuntimeAvailable)
        {
            shim = mod.Runtime;
            return shim is not null;
        }

        shim = null;
        WarnThrottled("三进制运行时未装配，本次调用按默认值处理。");
        return false;
    }

    private static bool TryFindRecent(ulong id, out JobResult? result)
    {
        IReadOnlyList<JobResult> recent = TritRuntimeMod.Instance?.RecentJobs ?? Array.Empty<JobResult>();
        for (int i = 0; i < recent.Count; i++)
        {
            if (recent[i].Id == id)
            {
                result = recent[i];
                return true;
            }
        }

        result = null;
        return false;
    }
    private static void PushRecent(IReadOnlyList<JobResult> jobs)
    {
        try
        {
            TritRuntimeMod.Instance?.PushRecentJobs(jobs);
        }
        catch
        {
            // 缓冲失败不影响返回结果：环形缓冲只是给命令回看用的
        }
    }

    private static void WarnThrottled(string message)
    {
        // 按时间节流：别的模组可能每 tick 调一次，逐条打日志会瞬间刷爆日志。
        long now = Environment.TickCount64;
        if (now - lastWarnAt < WarnThrottleMilliseconds)
        {
            return;
        }

        lastWarnAt = now;
        TritRuntimeMod.Instance?.Logger.Warn("[trit] TritApi." + message);
    }

    private static CapabilityReport SafeCapabilities() =>
        new(0, 0, CapabilityBits.None, KernelMode.Managed, ArbitrationMode.Majority);
}
