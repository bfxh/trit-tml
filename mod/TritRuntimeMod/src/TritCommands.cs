using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Xna.Framework;
using Terraria.ModLoader;
using TritRuntime.Kernel;
using TritRuntime.Wire;

namespace TritRuntimeMod;

/// <summary>
/// 旁路三进制运行时的玩家命令（聊天框与服务器控制台都能用）。
/// </summary>
/// <remarks>
/// <para>
/// 全部成员签名都用反射从本机 <c>tModLoader.dll</c>（1.4.4.9）逐项核对过：
/// <c>ModCommand.Command</c> / <c>Type</c> / <c>Usage</c> / <c>Description</c> /
/// <c>Action(CommandCaller, string, string[])</c>。
/// 特别注意 <c>Action</c> 是三参数（旧的 1.3 只有 <c>caller</c> 一个），
/// 且 <c>CommandCaller.Reply</c> 只有 <c>(string, Color)</c> 一个重载——没有单参数版本。
/// </para>
/// <para>
/// <c>CommandType.World | CommandType.Console</c>：<c>World</c> 表示单人可直接在聊天框用、
/// 联机时由服务器执行；<c>Console</c> 表示专用服务器控制台也能用。
/// （<c>Chat</c> 会盖掉 <c>Server</c> 的语义，所以这里不写 <c>Chat</c>。）
/// </para>
/// <para>
/// 铁律：任何一个子命令都不允许把异常冒给 tML。外层统一 try/catch，
/// 每个子命令内部再各自兜一层——命令只是诊断工具，绝不能因为查个状态把服务器搞崩。
/// </para>
/// </remarks>
public sealed class TritCommand : ModCommand
{
    /// <summary>Prometheus 文本最多打印多少行（其余报个总数，别刷屏）。</summary>
    private const int MaxMetricLines = 12;

    /// <summary>budget / jobs 每个列表最多打印多少行。</summary>
    private const int MaxListRows = 10;

    public override string Command => "trit";

    public override CommandType Type => CommandType.World | CommandType.Console;

    public override string Usage => "trit <status|gpu|mem|budget|metrics|jobs|cancel|scan|help> [参数]";

    public override string Description => "旁路三进制运行时：总览、GPU、内存、预算、任务与扫描。";

    public override void Action(CommandCaller caller, string input, string[] args)
    {
        try
        {
            string sub = (args is { Length: > 0 } ? args[0] : string.Empty).Trim().ToLowerInvariant();
            switch (sub)
            {
                case "":
                case "status":
                    Status(caller);
                    break;

                case "gpu":
                    Gpu(caller, args);
                    break;

                case "budget":
                    Budget(caller);
                    break;

                case "metrics":
                    Metrics(caller);
                    break;

                case "mem":
                case "memory":
                    Mem(caller);
                    break;

                case "jobs":
                    Jobs(caller);
                    break;

                case "cancel":
                    Cancel(caller, args);
                    break;

                case "scan":
                    Scan(caller, args);
                    break;

                case "help":
                    Help(caller);
                    break;

                default:
                    Help(caller);
                    Error(caller, "未知子命令：" + sub);
                    break;
            }
        }
        catch (Exception ex)
        {
            // 最后一道闸：任何漏网的异常都变成一行红字，绝不冒泡。
            Error(caller, "命令失败：" + ex.Message);
        }
    }

    // ------------------------------------------------------------------ 子命令

    /// <summary>一行总览：模式 / 能力位 / 任务池 / GPU。</summary>
    private static void Status(CommandCaller caller)
    {
        try
        {
            if (!Require(caller, out RuntimeShim? shim))
            {
                return;
            }

            CapabilityReport caps = shim!.Capabilities();
            Say(caller,
                $"旁路三进制运行时 mode={shim.Mode} abi={caps.AbiVersion} " +
                $"caps=0x{(ulong)caps.Capabilities:X} " +
                $"并行={OnOff(shim.Supports(CapabilityBits.Parallel))} " +
                $"GPU={OnOff(shim.Supports(CapabilityBits.Gpu))} " +
                $"预算={OnOff(shim.Supports(CapabilityBits.Budget))}");

            BudgetSnapshot? snapshot = SafeBudget(shim);
            if (snapshot is null)
            {
                Say(caller, "  任务池：内核未提供预算快照（BUDGET 能力位未置位）", Color.Gray);
            }
            else
            {
                Say(caller,
                    $"  任务池 workers={snapshot.Workers} inflight={snapshot.Inflight} " +
                    $"queued={snapshot.Queued} 单模组上限={snapshot.PerModInflight} " +
                    $"内存={snapshot.MemoryCapacity}MiB", Color.Gray);
            }

            GpuStatusReport? gpu = SafeGpuStatus(shim);
            if (gpu is null)
            {
                Say(caller, "  GPU：内核未提供状态", Color.Gray);
                return;
            }

            if (gpu.Available)
            {
                Say(caller,
                    $"  GPU 可用：adapter={gpu.Adapter} backend={gpu.Backend} " +
                    $"policy={PolicyName(gpu.Policy)} 启用={OnOff(gpu.Enabled)} " +
                    $"min_trits={gpu.MinTrits}", Color.Gray);
            }
            else
            {
                Say(caller, "  GPU 不可用：" + GpuUnavailableReason(gpu) +
                            $"（当前 policy={PolicyName(gpu.Policy)}）", Color.Yellow);
            }
        }
        catch (Exception ex)
        {
            Error(caller, "命令失败：" + ex.Message);
        }
    }

    /// <summary>GPU 状态查看 / 切策略 / 强制探测。</summary>
    private static void Gpu(CommandCaller caller, string[] args)
    {
        try
        {
            if (!Require(caller, out TritRuntimeMod? mod, out RuntimeShim? shim))
            {
                return;
            }

            string arg = args.Length > 1 ? args[1].Trim().ToLowerInvariant() : string.Empty;
            switch (arg)
            {
                case "":
                    PrintGpu(caller, SafeGpuStatus(shim!));
                    return;

                case "probe":
                    // 强制探测要真跑一次 GPU 对拍，会卡住主线程——必须先告诉玩家。
                    Say(caller, "正在强制重新探测 GPU（会卡一下，几百毫秒）…", Color.Yellow);
                    PrintGpu(caller, SafeGpuStatus(shim!, null, true));
                    return;

                case "on":
                case "off":
                case "auto":
                    ApplyGpuPolicy(caller, mod!, shim!, arg);
                    return;

                default:
                    Say(caller, "GPU 子命令只接受：on / off / auto / probe（不带参数 = 看状态）。", Color.Yellow);
                    return;
            }
        }
        catch (Exception ex)
        {
            Error(caller, "命令失败：" + ex.Message);
        }
    }

    /// <summary>全局资源预算：容量 + 各模组账本。</summary>
    private static void Budget(CommandCaller caller)
    {
        try
        {
            if (!Require(caller, out RuntimeShim? shim))
            {
                return;
            }

            BudgetSnapshot? snapshot = SafeBudget(shim!);
            if (snapshot is null)
            {
                Say(caller, "内核未提供预算快照（BUDGET 能力位未置位，通常是托管回退内核）。", Color.Yellow);
                return;
            }

            Say(caller,
                $"全局预算 workers={snapshot.Workers}/{snapshot.CpuCapacity}(cpu) " +
                $"内存={snapshot.MemoryCapacity}MiB 单模组上限={snapshot.PerModInflight} " +
                $"inflight={snapshot.Inflight} queued={snapshot.Queued}");

            BudgetTotals? totals = snapshot.Totals;
            if (totals is not null)
            {
                Say(caller,
                    $"  累计 提交={totals.Submitted} 完成={totals.Completed} " +
                    $"取消={totals.Cancelled} 拒绝={totals.Rejected} 步数={totals.Steps}", Color.Gray);
            }

            IReadOnlyList<ModBudget> mods = snapshot.Mods ?? Array.Empty<ModBudget>();
            if (mods.Count == 0)
            {
                Say(caller, "  还没有任何模组提交过任务。", Color.Gray);
                return;
            }

            var sorted = new List<ModBudget>(mods);
            sorted.Sort((a, b) => b.Submitted.CompareTo(a.Submitted));

            int rows = Math.Min(sorted.Count, MaxListRows);
            Say(caller, $"  各模组（按提交降序，{rows}/{sorted.Count} 行）：", Color.Gray);
            for (int i = 0; i < rows; i++)
            {
                ModBudget entry = sorted[i];
                Say(caller,
                    $"    {entry.Owner} inflight={entry.Inflight} queued={entry.Queued} " +
                    $"提交={entry.Submitted} 完成={entry.Completed} 取消={entry.Cancelled} " +
                    $"拒绝={entry.Rejected} 步数={entry.Steps}", Color.Gray);
            }
        }
        catch (Exception ex)
        {
            Error(caller, "命令失败：" + ex.Message);
        }
    }

    /// <summary>最近一轮任务池指标（Prometheus 文本，截断打印）。</summary>
    private static void Metrics(CommandCaller caller)
    {
        try
        {
            if (!Require(caller, out RuntimeShim? shim))
            {
                return;
            }

            string text = SafeMetrics(shim!) ?? string.Empty;
            if (text.Length == 0)
            {
                Say(caller, "内核没有提供任务池指标（PARALLEL 能力位未置位）。", Color.Yellow);
                return;
            }

            string[] lines = text.Split('\n');
            int shown = Math.Min(lines.Length, MaxMetricLines);
            for (int i = 0; i < shown; i++)
            {
                string line = lines[i].Trim();
                if (line.Length > 0)
                {
                    Say(caller, "  " + line, Color.Gray);
                }
            }

            if (lines.Length > shown)
            {
                Say(caller, $"  …共 {lines.Length} 行，已省略 {lines.Length - shown} 行。", Color.Gray);
            }
        }
        catch (Exception ex)
        {
            Error(caller, "命令失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 整进程内存：工作集 / 专用字节 / GC 堆 / 内核常驻，以及本模组的预算旋钮。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 为什么要看**进程**而不是本模组：tModLoader 里所有模组共用一个进程，
    /// 本模组真正让玩家感受到的是"这游戏占了多少内存"，而不是我自己分配了多少。
    /// 所以这里报的是 <see cref="Process.WorkingSet64"/> 与
    /// <see cref="Process.PrivateMemorySize64"/>，再加上托管堆（<see cref="GC"/> 视角）
    /// 与内核自己的账（世界车道 / 日志 / 快照）。
    /// </para>
    /// <para>
    /// 日志与快照的字节数是**按已知单条大小推算**的，不是分配器实测值——
    /// 运行时没有为它们维护字节计数（那本身也是每 tick 的开销）。
    /// 实测值请看 <c>crates/trit-rt/examples/mem_bench.rs</c>。
    /// </para>
    /// </remarks>
    private static void Mem(CommandCaller caller)
    {
        try
        {
            if (!Require(caller, out TritRuntimeMod? mod, out RuntimeShim? shim))
            {
                return;
            }

            Process process = Process.GetCurrentProcess();
            Say(caller,
                $"进程工作集={Mb(process.WorkingSet64)} 专用={Mb(process.PrivateMemorySize64)} " +
                $"峰值工作集={Mb(process.PeakWorkingSet64)}", Color.Gray);
            Say(caller,
                $"托管堆={Mb(GC.GetTotalMemory(false))} " +
                $"（GC 已提交={Mb(GC.GetGCMemoryInfo().TotalCommittedBytes)} 上限={Mb(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes)}）",
                Color.Gray);

            TritSettings settings = mod!.Settings;
            uint world = SafeQueryU32(() => shim!.WorldSize());
            ulong logLen = SafeQueryU64(() => shim!.LogLen());
            uint snaps = SafeQueryU32(() => shim!.SnapshotCount());
            Say(caller,
                $"内核：世界车道={world} 日志={logLen} 条（约 {Mb((long)logLen * 5)}） 快照={snaps} 张",
                Color.Gray);
            Say(caller,
                $"预算：maxLogEntries={settings.MaxLogEntries}（约 {Mb(settings.MaxLogEntries * 5L)} 上限）" +
                $" 任务池内存={settings.MemoryCapacityMb}MiB workers={settings.WorkerCount}",
                Color.Gray);
            Say(caller,
                "提示：退出存档会自动复位内核状态；日志预算越大，撤销窗口越长、常驻越多。", Color.Gray);
        }
        catch (Exception ex)
        {
            Error(caller, "命令失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 列出最近完成的任务。
    /// 刻意<b>不</b>直接调 <c>PollJobs</c>：取出即出队，命令这样做会把每 tick 自动轮询
    /// 攒下的结果抢走。这里读的是模组自己的环形缓冲，是纯读取。
    /// </summary>
    private static void Jobs(CommandCaller caller)
    {
        try
        {
            if (!Require(caller, out TritRuntimeMod? mod))
            {
                return;
            }

            IReadOnlyList<JobResult> recent = mod!.RecentJobs ?? Array.Empty<JobResult>();
            if (recent.Count == 0)
            {
                Say(caller, "最近没有完成的任务（结果由每 tick 自动轮询取出，最多保留 64 条）。", Color.Gray);
                return;
            }

            int count = Math.Min(recent.Count, MaxListRows);
            Say(caller, $"最近完成的任务（{count}/{recent.Count}，最新在最后）：");
            for (int i = recent.Count - count; i < recent.Count; i++)
            {
                JobResult job = recent[i];
                Say(caller,
                    $"  #{job.Id} {job.State} owner={job.Owner} kind={job.Kind} " +
                    $"steps={job.Steps} backend={job.Backend}" +
                    (job.Error.Length > 0 ? " 错误=" + job.Error : string.Empty),
                    job.State == JobState.Failed ? Color.Yellow : Color.Gray);
            }
        }
        catch (Exception ex)
        {
            Error(caller, "命令失败：" + ex.Message);
        }
    }

    /// <summary>取消任务：给数字按 id 取消，给别的按 owner 取消。</summary>
    private static void Cancel(CommandCaller caller, string[] args)
    {
        try
        {
            if (args.Length < 2)
            {
                Say(caller, "用法：trit cancel <任务 id | owner 名>", Color.Yellow);
                return;
            }

            if (!Require(caller, out RuntimeShim? shim))
            {
                return;
            }

            string target = args[1].Trim();
            int cancelled;
            try
            {
                cancelled = ulong.TryParse(target, NumberStyles.None, CultureInfo.InvariantCulture, out ulong id)
                    ? shim!.CancelJob(id)
                    : shim!.CancelOwner(target);
            }
            catch (Exception ex)
            {
                // 托管回退内核没有任务池，Cancel 会直接抛——那是"没这个功能"，不是错误。
                Say(caller, "内核不支持取消任务（PARALLEL 能力位未置位）：" + ex.Message, Color.Yellow);
                return;
            }

            if (cancelled <= 0)
            {
                Say(caller, $"没有取消到任何任务（{target}）。已在跑的任务只在步骤边界退出。", Color.Yellow);
                return;
            }

            Say(caller, $"已取消 {cancelled} 个任务（{target}）。");
        }
        catch (Exception ex)
        {
            Error(caller, "命令失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 对当前世界投影跑一次扫描。
    /// 只开放 histogram / density：pattern / dot 需要玩家给 pattern 数组，
    /// 聊天框里没法表达，那两种留给 <c>Mod.Call</c>。
    /// </summary>
    private static void Scan(CommandCaller caller, string[] args)
    {
        try
        {
            if (args.Length < 2)
            {
                Say(caller, "用法：trit scan histogram|density [chunk] [top]", Color.Yellow);
                return;
            }

            string kindText = args[1].Trim().ToLowerInvariant();
            ScanKind kind;
            switch (kindText)
            {
                case "histogram":
                    kind = ScanKind.Histogram;
                    break;

                case "density":
                    kind = ScanKind.Density;
                    break;

                case "pattern":
                case "dot":
                    Say(caller,
                        "pattern / dot 需要传 pattern 三值数组，聊天里没法写，" +
                        "请用 Mod.Call(\"TritRuntime.Scan\", owner, kind, trits, pattern, threshold)。",
                        Color.Yellow);
                    return;

                default:
                    Say(caller, "未知扫描种类：" + kindText + "（只开放 histogram / density）。", Color.Yellow);
                    return;
            }

            if (!Require(caller, out TritRuntimeMod? mod, out RuntimeShim? shim))
            {
                return;
            }

            int chunk = ParseInt(args, 2, 0);
            int top = ParseInt(args, 3, 16);

            WorldBridge? bridge = mod!.Bridge;
            if (bridge is null)
            {
                Say(caller, "世界投影桥未装载，没有可扫描的数据。", Color.Yellow);
                return;
            }

            byte[] trits = bridge.CaptureWorldTrits();
            if (trits.Length == 0)
            {
                Say(caller,
                    "当前没有可扫描的世界投影数据（还没进世界，或世界投影一条车道都没采样到），进世界后再试。",
                    Color.Yellow);
                return;
            }

            ulong job;
            try
            {
                job = shim!.SubmitScan(new ScanRequest
                {
                    Owner = "TritRuntimeMod",
                    Kind = kind,
                    Trits = trits,
                    Chunk = chunk,
                    Top = top,
                });
            }
            catch (Exception ex)
            {
                Say(caller, "扫描提交失败：" + ex.Message, Color.OrangeRed);
                return;
            }

            if (job == 0)
            {
                Say(caller,
                    "扫描提交失败：内核拒绝了这次提交（常见原因：没有 PARALLEL 能力、超出全局预算、" +
                    "或同一 owner 的在跑任务已达上限）。详见模组日志。",
                    Color.OrangeRed);
                return;
            }

            Say(caller,
                $"已提交扫描任务 #{job}（kind={kind} 元素={trits.Length} chunk={chunk} top={top}）；" +
                "结果用 trit jobs 看。");
        }
        catch (Exception ex)
        {
            Error(caller, "命令失败：" + ex.Message);
        }
    }

    private static void Help(CommandCaller caller)
    {
        try
        {
            Say(caller, "旁路三进制运行时（trit）可用子命令：");
            Say(caller, "  trit status                 总览：模式 / 能力位 / 任务池 / GPU", Color.Gray);
            Say(caller, "  trit gpu                    GPU 状态（adapter/backend/policy/对拍）", Color.Gray);
            Say(caller, "  trit gpu on|off|auto        切换策略并写回设置文件", Color.Gray);
            Say(caller, "  trit gpu probe              强制重新探测（会卡一下，几百毫秒）", Color.Gray);
            Say(caller, "  trit budget                 全局预算与各模组账本", Color.Gray);
            Say(caller, "  trit mem                    整进程工作集 / 托管堆 / 内核常驻 / 预算旋钮", Color.Gray);
            Say(caller, "  trit metrics                最近一轮任务池指标（Prometheus，截断）", Color.Gray);
            Say(caller, "  trit jobs                   最近完成的任务（读环形缓冲，不抢队列）", Color.Gray);
            Say(caller, "  trit cancel <id|owner>      取消任务（协作式，步骤边界退出）", Color.Gray);
            Say(caller, "  trit scan histogram|density [chunk] [top]   扫当前世界投影", Color.Gray);
            Say(caller, "  trit help                   本列表", Color.Gray);
        }
        catch (Exception ex)
        {
            Error(caller, "命令失败：" + ex.Message);
        }
    }

    // ------------------------------------------------------------------ 内部

    private static void ApplyGpuPolicy(CommandCaller caller, TritRuntimeMod mod, RuntimeShim shim, string text)
    {
        TritSettings settings = mod.Settings;
        settings.GpuPolicyText = text;
        settings.Sanitize();

        string saveNote;
        if (settings.Save(out string warning))
        {
            saveNote = "已写入设置文件。";
        }
        else
        {
            saveNote = "设置文件写入失败（" + warning + "），本次修改只在本会话生效。";
            LoggerWarn(saveNote);
        }

        GpuStatusReport? maybe = SafeGpuStatus(shim, settings.GpuPolicyKind);
        if (maybe is null)
        {
            Say(caller,
                $"GPU 策略已设为 {text}，但内核不支持 GPU 状态查询（通常是托管回退内核）。{saveNote}",
                Color.Yellow);
            return;
        }

        GpuStatusReport report = maybe;
        if (report.Available && report.Enabled)
        {
            Say(caller,
                $"GPU 已开启：adapter={report.Adapter} backend={report.Backend} " +
                $"min_trits={report.MinTrits}。{saveNote}");
            return;
        }

        // 不可用必须讲清楚是哪一种不可用，绝不能假装开启成功。
        string reason = GpuUnavailableReason(report);
        if (settings.GpuPolicyKind == GpuPolicy.On)
        {
            Say(caller,
                $"已按设置要求把策略设为 on，但 GPU 仍然不可用：{reason}。全部扫描继续走 CPU。{saveNote}",
                Color.OrangeRed);
            LoggerWarn($"设置要求开启 GPU，但本环境不可用（{reason}），已按 CPU 运行。");
            return;
        }

        Say(caller, $"GPU 策略已设为 {text}（当前不可用：{reason}）。{saveNote}", Color.Yellow);
    }

    private static void PrintGpu(CommandCaller caller, GpuStatusReport? report)
    {
        if (report is null)
        {
            Say(caller, "内核未提供 GPU 状态（通常是托管回退内核）。", Color.Yellow);
            return;
        }

        Say(caller,
            $"GPU available={OnOff(report.Available)} enabled={OnOff(report.Enabled)} " +
            $"policy={PolicyName(report.Policy)} built_with_gpu={OnOff(report.BuiltWithGpu)}");
        Say(caller,
            $"  adapter={report.Adapter} backend={report.Backend} min_trits={report.MinTrits}", Color.Gray);
        Say(caller,
            $"  对拍 cpu_dot={report.CpuDot} gpu_dot={report.GpuDot} 一致={OnOff(report.Matches)} " +
            $"ns/trit={report.NsPerTrit.ToString("0.0000", CultureInfo.InvariantCulture)}", Color.Gray);
        Say(caller,
            $"  计数 jobs={report.Jobs} gpu_jobs={report.GpuJobs} fallbacks={report.Fallbacks}", Color.Gray);

        if (!report.Available)
        {
            Say(caller, "  不可用原因：" + GpuUnavailableReason(report), Color.Yellow);
        }
    }

    /// <summary>
    /// 把"为什么不能用 GPU"翻译成人话。
    /// 内核给的 <c>Reason</c> 可能是空串，那就按状态位倒推——
    /// 玩家至少要能分辨"构建没带 feature"和"有 adapter 但对拍不一致"。
    /// </summary>
    private static string GpuUnavailableReason(GpuStatusReport report)
    {
        if (report.Reason.Length > 0)
        {
            return report.Reason;
        }

        if (!report.BuiltWithGpu)
        {
            return "内核构建未带 gpu feature";
        }

        if (report.Adapter.Length == 0)
        {
            return "没有可用 adapter";
        }

        if (!report.Matches)
        {
            return "CPU/GPU 自检对拍不一致（GPU 已被永久关闭）";
        }

        return "内核未给出具体原因";
    }

    private static GpuStatusReport? SafeGpuStatus(RuntimeShim shim, GpuPolicy? policy = null, bool probe = false)
    {
        try
        {
            return shim.GpuStatus(policy, probe);
        }
        catch (Exception ex)
        {
            LoggerWarn("读取 GPU 状态失败：" + ex.Message);
            return null;
        }
    }

    private static string? SafeMetrics(RuntimeShim shim)
    {
        try
        {
            return shim.MetricsPrometheus();
        }
        catch (Exception ex)
        {
            LoggerWarn("读取任务池指标失败：" + ex.Message);
            return null;
        }
    }

    private static BudgetSnapshot? SafeBudget(RuntimeShim shim)
    {
        try
        {
            return shim.Budget();
        }
        catch (Exception ex)
        {
            LoggerWarn("读取全局预算失败：" + ex.Message);
            return null;
        }
    }

    /// <summary>把字节数格式化成 MB（一位小数）。</summary>
    private static string Mb(long bytes) =>
        (bytes / (double)(1 << 20)).ToString("0.0", CultureInfo.InvariantCulture) + "MB";

    private static uint SafeQueryU32(Func<uint> query)
    {
        try
        {
            return query();
        }
        catch (Exception ex)
        {
            LoggerWarn("读取内核计数失败：" + ex.Message);
            return 0;
        }
    }

    private static ulong SafeQueryU64(Func<ulong> query)
    {
        try
        {
            return query();
        }
        catch (Exception ex)
        {
            LoggerWarn("读取内核计数失败：" + ex.Message);
            return 0;
        }
    }

    private static int ParseInt(string[] args, int index, int fallback)
    {
        if (index < 0 || index >= args.Length)
        {
            return fallback;
        }

        return int.TryParse(args[index].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            ? value
            : fallback;
    }

    private static string PolicyName(GpuPolicy policy) => policy switch
    {
        GpuPolicy.On => "on",
        GpuPolicy.Off => "off",
        _ => "auto",
    };

    private static string OnOff(bool value) => value ? "是" : "否";

    private static bool Require(CommandCaller caller, out RuntimeShim? shim)
    {
        TritRuntimeMod? mod = TritRuntimeMod.Instance;
        shim = mod?.Runtime;
        if (mod is null || shim is null)
        {
            Error(caller, "三进制运行时未装配，本模组本次会话不提供服务（详见模组日志）。");
            return false;
        }

        return true;
    }

    private static bool Require(CommandCaller caller, out TritRuntimeMod? mod)
    {
        mod = TritRuntimeMod.Instance;
        if (mod is null || mod.Runtime is null)
        {
            Error(caller, "三进制运行时未装配，本模组本次会话不提供服务（详见模组日志）。");
            mod = null;
            return false;
        }

        return true;
    }

    private static bool Require(CommandCaller caller, out TritRuntimeMod? mod, out RuntimeShim? shim)
    {
        if (!Require(caller, out mod))
        {
            shim = null;
            return false;
        }

        shim = mod!.Runtime;
        return true;
    }

    private static void Say(CommandCaller caller, string text) => Say(caller, text, Color.Orange);

    private static void Say(CommandCaller caller, string text, Color color)
    {
        caller.Reply(text, color);
        TritRuntimeMod.Instance?.Logger.Info("[trit] 命令输出：" + text);
    }

    private static void Error(CommandCaller caller, string text)
    {
        caller.Reply(text, Color.OrangeRed);
        TritRuntimeMod.Instance?.Logger.Warn("[trit] 命令失败：" + text);
    }

    private static void LoggerWarn(string text)
    {
        TritRuntimeMod.Instance?.Logger.Warn("[trit] " + text);
    }
}
