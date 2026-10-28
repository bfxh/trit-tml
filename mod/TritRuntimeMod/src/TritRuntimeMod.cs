using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using Terraria;
using Terraria.ModLoader;
using Terraria.ModLoader.Config;
using TritRuntime.Kernel;
using TritRuntime.Wire;

namespace TritRuntimeMod;

/// <summary>
/// tModLoader 旁路运行时模组的门面。
/// </summary>
/// <remarks>
/// <para>
/// 职责边界：本类只做装配与生命周期管理，不承载任何三进制逻辑。
/// 采样与提交在 <see cref="WorldBridge"/>，钩子在 <see cref="TritHooks"/>，
/// 事件路由 / 事务 / 快照 / 差分全部在兼容壳（<see cref="RuntimeShim"/>）之后的内核里。
/// </para>
/// <para>
/// 对外接口就是 tML 原生的 <c>Mod.Call</c>：其他模组通过
/// <c>TritRuntimeMod.Call("TritRuntime.Capabilities")</c> 之类的调用使用本层，
/// 完全不需要引用本模组的类型。命令名一律以 <c>TritRuntime.</c> 开头且大小写不敏感，
/// 分支全在兼容壳 <see cref="RuntimeShim.Call"/> 里，本类只做转发（不重复分发）。
/// </para>
/// </remarks>
/// <remarks>
/// <para>
/// <b>其他模组怎么接入（两条路，任选其一）。</b>
/// </para>
/// <para>
/// 一、弱引用（推荐，不需要引用本程序集，本模组不存在也不会编译失败）：
/// <code>
/// Mod trit = ModLoader.GetMod("TritRuntimeMod");
/// if (trit is not null)
/// {
///     // 提交扫描，拿到 job id（0 = 提交失败）。
///     // kind 是字符串且大小写不敏感：histogram | pattern | density | dot
///     ulong job = (ulong)trit.Call(
///         "TritRuntime.Scan", "MyMod", "pattern", trits, pattern, 3);
///
///     // 取回已完成的结果（取出即出队）
///     var jobs = (IReadOnlyList&lt;JobResult&gt;)trit.Call("TritRuntime.PollJobs", 16);
///     trit.Call("TritRuntime.CancelJob", job);
///     var caps = (CapabilityReport)trit.Call("TritRuntime.Capabilities");
/// }
/// </code>
/// 全部命令清单见兼容壳的 <c>TritRuntimeCommands.All</c>。
/// </para>
/// <para>
/// 二、强引用（引用本程序集后直接调 <see cref="TritApi"/> 的静态方法）：
/// <code>
/// if (TritApi.Available)
/// {
///     ulong job = TritApi.Scan("MyMod", ScanKind.Histogram, trits);
///     if (TritApi.TryPollJob(job, out JobResult r)) { /* 用 r.ResultJson */ }
/// }
/// </code>
/// <see cref="TritApi"/> 是"永不抛"的门面：运行时不可用时返回 0 / null / 空列表，
/// 其他模组不需要写 try/catch。
/// </para>
/// </remarks>
public sealed class TritRuntimeMod : Mod
{
    /// <summary>任务结果环形缓冲容量（只留最近这么多条，供 <c>trit jobs</c> 与 <see cref="TritApi"/> 回看）。</summary>
    private const int RecentJobCapacity = 64;

    /// <summary>任务失败日志最多记多少条（之后只轮询不刷屏）。</summary>
    private const int MaxLoggedJobFailures = 10;

    /// <summary>单个 owner 的结果邮箱容量上限（防止某个模组不取结果时无限堆积）。</summary>
    private const int OwnerMailboxCapacity = 64;

    private TritSettings settings = new();
    private RuntimeShim? shim;
    private WorldBridge? bridge;
    private TritHooks? hooks;
    private bool loaded;

    // 轮询开关：一旦轮询本身抛异常就永久关闭，避免每 tick 刷一条错误日志。
    private bool jobPollingEnabled;

    private readonly List<JobResult> recentJobs = new(RecentJobCapacity);

    /// <summary>
    /// 按 owner 分桶的结果邮箱：<c>owner → 待取走的结果</c>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 为什么需要它：内核的 <c>JOB_POLL</c> 是<b>全局队列 + 取出即出队</b>。
    /// 若模组层每 tick 把队列里的结果全取走再塞进一个公共环形缓冲，
    /// 就会出现「A 模组提交的任务被 B 模组的轮询取走」——结果串台，
    /// 而且谁都拿不到自己那份（环形缓冲只有最近 64 条，早期的直接丢了）。
    /// </para>
    /// <para>
    /// 分桶之后：每个模组只从<b>自己那桶</b>取结果，互不影响；
    /// 公共环形缓冲仍保留一份，供 <c>trit jobs</c> 命令与诊断回看。
    /// </para>
    /// </remarks>
    private readonly Dictionary<string, List<JobResult>> ownerMailboxes = new(StringComparer.Ordinal);

    private int loggedJobFailures;

    /// <summary>当前装载中的模组实例（未装载时为 <c>null</c>）。</summary>
    public static TritRuntimeMod? Instance { get; private set; }

    /// <summary>运行时是否已成功装配。</summary>
    public bool RuntimeAvailable => loaded && shim is not null;

    /// <summary>装载期端到端自检是否通过（决定是否安装钩子）。</summary>
    public bool SelfCheckPassed { get; private set; }

    /// <summary>当前生效的设置（命令层与 API 层共用同一份实例，改了立即生效）。</summary>
    internal TritSettings Settings => settings;

    /// <summary>世界投影桥（未装配时为 <c>null</c>）。</summary>
    internal WorldBridge? Bridge => bridge;

    /// <summary>最近完成的任务结果（最多 <see cref="RecentJobCapacity"/> 条，最新的在末尾）。</summary>
    internal IReadOnlyList<JobResult> RecentJobs => recentJobs;

    /// <summary>任务结果轮询是否仍在工作（异常后会被永久关闭）。</summary>
    internal bool JobPollingEnabled => jobPollingEnabled;

    /// <summary>内部运行时门面（未装配时为 <c>null</c>）。</summary>
    internal RuntimeShim? Runtime => shim;

    /// <summary>按设置拼装内核配置 JSON（字段严格按 <c>docs/ABI.md</c> 的"配置 JSON"小节）。</summary>
    /// <remarks>
    /// 拼装失败一律回退空串（空串 = 内核默认配置）——配置是可选的，
    /// 绝不能因为一个设置值算错就让整个内核起不来。
    /// </remarks>
    private string BuildRuntimeConfigJson()
    {
        try
        {
            var buffer = new ArrayBufferWriter<byte>(128);
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();

                // 日志条数上限直接决定回滚窗口与常驻内存（约 5 字节/条），
                // 是这套设置里唯一需要玩家按机器调的量。
                writer.WriteNumber("max_log_entries", settings.MaxLogEntries);

                writer.WriteStartObject("parallel");
                // 关掉并行时收敛到"一个工作线程 + 单模组单任务"，等价于串行执行。
                writer.WriteNumber("workers", settings.EnableParallel ? settings.WorkerCount : 1);
                writer.WriteNumber("cpu_capacity", 0); // 0 = 自动（等于 workers）
                writer.WriteNumber("memory_capacity", settings.MemoryCapacityMb);
                writer.WriteNumber("per_mod_inflight", settings.EnableParallel ? settings.PerModInflight : 1);
                writer.WriteNumber("scan_chunk", settings.ScanChunk);
                writer.WriteEndObject();

                writer.WriteStartObject("gpu");
                writer.WriteString("policy", settings.GpuPolicyText);
                writer.WriteNumber("min_trits", settings.GpuMinTrits);
                writer.WriteEndObject();

                writer.WriteEndObject();
            }

            return Encoding.UTF8.GetString(buffer.WrittenSpan);
        }
        catch (Exception ex)
        {
            Logger.Warn("[trit] 拼装内核配置 JSON 失败，本次使用内核默认配置：" + ex.Message);
            return string.Empty;
        }
    }

    /// <summary>
    /// 把 <see cref="TritSettings"/> 的当前值同步进游戏内"配置"面板，保证面板显示真实设置。
    /// 任何异常都不允许影响装载（面板只是展示层）。
    /// </summary>
    private void SyncConfigFromSettings()
    {
        try
        {
            TritRuntimeConfig cfg = ModContent.GetInstance<TritRuntimeConfig>();
            cfg.ComputeBackend = settings.GpuPolicyKind switch
            {
                GpuPolicy.Off => ComputeBackendOption.Cpu,
                GpuPolicy.On => ComputeBackendOption.Gpu,
                _ => ComputeBackendOption.Auto,
            };
            cfg.ForceManagedFallback = settings.ForceManaged;
            cfg.VerboseLogging = settings.VerboseLogging;
            cfg.LogJobResults = settings.LogJobResults;
            cfg.EnableWorldHooks = settings.EnableHooks;
        }
        catch (Exception ex)
        {
            Logger.Warn("[trit] 同步游戏内设置面板失败（不影响运行）：" + ex.Message);
        }
    }

    /// <summary>
    /// 由游戏内"配置"面板改动触发（<see cref="TritRuntimeConfig.OnChanged"/>）：
    /// 把玩家选择写回 <see cref="TritSettings"/> 并尽量即时生效。
    /// </summary>
    /// <remarks>
    /// GPU 策略可热切换（直接推给原生内核，与控制台命令 <c>trit gpu</c> 同一条路径）；
    /// ForceManaged / EnableWorldHooks 是装载期选项，热改只落盘，下次启动生效。
    /// </remarks>
    internal void ApplyPlayerConfig(TritRuntimeConfig cfg)
    {
        try
        {
            settings.GpuPolicyText = cfg.ComputeBackend switch
            {
                ComputeBackendOption.Cpu => "off",
                ComputeBackendOption.Gpu => "on",
                _ => "auto",
            };
            settings.ForceManaged = cfg.ForceManagedFallback;
            settings.VerboseLogging = cfg.VerboseLogging;
            settings.LogJobResults = cfg.LogJobResults;
            settings.EnableHooks = cfg.EnableWorldHooks;
            settings.Save(out _);

            // GPU 策略热切换：直接推给原生内核（Auto 时内核按规模自动选，但不强制重探测）。
            if (shim is not null)
            {
                shim.GpuStatus(settings.GpuPolicyKind); // 推策略
                var gpu = shim.GpuStatus(); // 只读当前实际状态做确认
                if (gpu.Enabled)
                {
                    Logger.Info($"[trit] 计算后端策略已更新为 {settings.GpuPolicyText}，GPU 已启用：adapter={gpu.Adapter} backend={gpu.Backend}");
                }
                else
                {
                    string why = gpu.Reason.Length > 0
                        ? gpu.Reason
                        : !gpu.BuiltWithGpu ? "构建未带gpu feature" : "无可用adapter";
                    Logger.Info($"[trit] 计算后端策略已更新为 {settings.GpuPolicyText}，GPU 未启用（{why}），扫描走 CPU");
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warn("[trit] 应用游戏内设置失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 装配完成后，把"并行 / GPU 到底能不能用、为什么"讲清楚。
    /// 这一段只做观测，任何异常都不允许影响装载。
    /// </summary>
    private void ReportParallelAndGpu()
    {
        try
        {
            if (shim is null)
            {
                return;
            }

            if (!shim.Supports(CapabilityBits.Parallel))
            {
                Logger.Info("[trit] 当前内核没有并行任务池（Parallel 能力位未置位），扫描任务将同步执行。");
            }

            // 给策略本身就是"应用设置"：内核侧首次调用会自动探测一次，
            // 所以这里不需要（也不应该）开强制探测——那会拖慢启动。
            GpuStatusReport gpu = shim.GpuStatus(settings.GpuPolicyKind);
            if (gpu.Available)
            {
                Logger.Info(
                    $"[trit] GPU 可用：adapter={gpu.Adapter} backend={gpu.Backend} " +
                    $"policy={settings.GpuPolicyText} min_trits={gpu.MinTrits}");
                return;
            }

            // 不可用必须说清是哪一种不可用：构建没带 feature / 没有 adapter / 对拍不一致。
            // 否则玩家只会看到"关着"，却不知道为什么开不了。
            string why = gpu.Reason.Length > 0
                ? gpu.Reason
                : !gpu.BuiltWithGpu
                    ? "当前内核构建未带 gpu feature"
                    : "未探测到可用 adapter";

            if (settings.GpuPolicyKind == GpuPolicy.On)
            {
                Logger.Warn($"[trit] 设置要求开启 GPU（gpuPolicy=on），但本环境不可用（{why}），已按 CPU 运行。");
            }
            else
            {
                Logger.Info($"[trit] GPU 不可用：{why}（当前 policy={settings.GpuPolicyText}，全部扫描走 CPU）。");
            }
        }
        catch (Exception ex)
        {
            Logger.Warn("[trit] 读取 GPU 状态失败，本次会话按 CPU 运行：" + ex.Message);
        }
    }

    public override void Load()
    {
        Instance = this;

        settings = TritSettings.Load(out string warning);
        if (!string.IsNullOrEmpty(warning))
        {
            Logger.Warn("[trit] 设置加载提示：" + warning);
        }
        else if (!string.IsNullOrEmpty(settings.LoadedFrom))
        {
            Logger.Info("[trit] 设置来源：" + settings.LoadedFrom);
        }

        // 让游戏内"配置"面板反映当前真实设置（TritSettings 为主，ModConfig 为展示层）。
        SyncConfigFromSettings();

        try
        {
            string? libraryPath = string.IsNullOrWhiteSpace(settings.LibraryPath)
                ? null
                : settings.LibraryPath;

            // 设置里没显式指定时，用随 .tmod 分发的原生内核：
            // tML 只把 lib/<名字>.dll 当托管程序集加载，原生库得自己从包里取出落盘。
            if (libraryPath is null)
            {
                libraryPath = NativeLibraryStager.TryStage(this);
                if (libraryPath is not null)
                {
                    Logger.Info("[trit] 已从模组包取出原生内核：" + libraryPath);
                }
            }

            shim = RuntimeShim.Attach(new RuntimeHostOptions
            {
                LibraryPath = libraryPath,
                ConfigJson = BuildRuntimeConfigJson(),
                ForceManaged = settings.ForceManaged,
                ShadowMode = settings.ShadowMode,
                AllowDefaultProbing = settings.AllowDefaultProbing,
            });

            loaded = true;
            jobPollingEnabled = true;

            CapabilityReport caps = shim.Capabilities();
            Logger.Info(
                $"[trit] 运行时装配完成 mode={shim.Mode} abi={caps.AbiVersion} " +
                $"caps=0x{(ulong)caps.Capabilities:X} arbitration={caps.Arbitration}");

            if (shim.FallbackReason is { Length: > 0 } reason)
            {
                Logger.Warn("[trit] 原生内核不可用，已回退托管内核：" + reason);
            }

            if (!shim.Supports(CapabilityBits.TxnLog) || !shim.Supports(CapabilityBits.Router))
            {
                Logger.Warn("[trit] 内核缺少事务日志或事件路由能力，世界投影降级为只读观测。");
            }

            ReportParallelAndGpu();

            // 真机自检：证明本进程里"事务 → 快照 → 差分 → 回滚"闭环可用。
            // 失败只降级（不装钩子），不影响游戏。
            SelfCheckReport selfCheck = RuntimeSelfCheck.Run(shim);
            SelfCheckPassed = selfCheck.Passed;
            if (selfCheck.Passed)
            {
                Logger.Info("[trit] 自检 " + selfCheck.Message);
            }
            else
            {
                Logger.Error("[trit] 自检失败，本次会话只提供只读能力：" + selfCheck.Message);
            }

            bridge = new WorldBridge(this, settings, shim);

            if (settings.EnableHooks && SelfCheckPassed)
            {
                hooks = new TritHooks(this, settings, bridge);
                hooks.Install();
            }
            else
            {
                Logger.Info("[trit] 设置要求不安装钩子，本层仅提供 Mod.Call 内核服务。");
            }
        }
        catch (Exception ex)
        {
            // 装配失败不是游戏错误：清干净，把能力降级为"不可用"。
            Logger.Error("[trit] 运行时装配失败，本模组本次会话不提供服务：" + ex);
            Teardown();
        }
    }

    public override void Unload()
    {
        Teardown();
        Instance = null;
    }

    /// <summary>
    /// 每游戏 tick 推进一次世界投影。由 <see cref="TritWorldSystem.PostUpdateEverything"/> 驱动。
    /// </summary>
    /// <remarks>
    /// 放在这里而不是让 <see cref="TritHooks"/> 直接调用桥接层，是为了让 tick 驱动与
    /// MonoMod 钩子解耦：专用服务器上 <c>Main.Update</c> 不触发（实测），
    /// 而官方 <c>ModSystem</c> 回调两端都有。
    /// </remarks>
    internal void OnWorldTick()
    {
        if (!loaded)
        {
            return;
        }

        bridge?.OnTickEnd();
        OnPollJobs();
    }

    /// <summary>
    /// 每 tick 取回一批已完成的任务结果（取出即出队）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 与 <see cref="WorldBridge"/> 共用一条驱动链（<see cref="TritWorldSystem.PostUpdateEverything"/>
    /// → <see cref="OnWorldTick"/>），因为专用服务器上只有 <c>ModSystem</c> 回调是两端都到位的。
    /// </para>
    /// <para>
    /// 节流策略：没有结果时直接返回（绝大多数 tick 都会走这条路，一条日志都不打）；
    /// 失败任务无论是否开启明细日志都记 Warn——失败是异常信号，但只记前
    /// <see cref="MaxLoggedJobFailures"/> 条，之后闭嘴。
    /// </para>
    /// <para>
    /// 轮询本身抛异常就永久关闭本次会话的轮询：宁可没有结果回看，也不能每 tick 刷一条错误。
    /// </para>
    /// </remarks>
    internal void OnPollJobs()
    {
        if (!loaded || !jobPollingEnabled || shim is null)
        {
            return;
        }

        try
        {
            // 菜单里没有世界，也不该为空转的倒计时去打内核。
            if (Main.gameMenu)
            {
                return;
            }

            // 没有在飞任务：轮询必然空手，这一趟 FFI 与两次缓冲分配直接省掉。
            // 绝大多数会话都不提交扫描任务（只有 `trit scan` 类命令会），
            // 所以这里省掉的是每 tick 的常态开销，而不是偶发开销。
            if (shim.JobsInflight == 0)
            {
                return;
            }

            IReadOnlyList<JobResult> jobs = shim.PollJobs(settings.PollJobsPerTick);
            if (jobs is null || jobs.Count == 0)
            {
                return;
            }

            for (int i = 0; i < jobs.Count; i++)
            {
                JobResult job = jobs[i];
                if (job.State == JobState.Failed)
                {
                    if (loggedJobFailures < MaxLoggedJobFailures)
                    {
                        loggedJobFailures++;
                        Logger.Warn(
                            $"[trit] 扫描任务 #{job.Id} 失败（owner={job.Owner} kind={job.Kind}）：{job.Error}");
                    }
                }
                else if (settings.LogJobResults)
                {
                    Logger.Info(
                        $"[trit] 扫描任务 #{job.Id} {job.State} owner={job.Owner} kind={job.Kind} " +
                        $"steps={job.Steps} backend={job.Backend} result={job.ResultJson}");
                }
            }

            DispatchToOwners(jobs);
        }
        catch (Exception ex)
        {
            jobPollingEnabled = false;
            Logger.Warn("[trit] 任务结果轮询异常，本次会话已停止轮询：" + ex.Message);
        }
    }

    /// <summary>
    /// 把一批结果按 owner 派发到各自的结果邮箱，并同步进公共环形缓冲。
    /// </summary>
    /// <remarks>
    /// 环形缓冲是<b>只读视图</b>（给 <c>trit jobs</c> 与诊断用），
    /// 邮箱才是<b>取走通道</b>（给各模组取自己那份用）——
    /// 两者分开，环形缓冲满了也不会导致某个模组的结果被提前丢弃。
    /// </remarks>
    private void DispatchToOwners(IReadOnlyList<JobResult> jobs)
    {
        for (int i = 0; i < jobs.Count; i++)
        {
            JobResult job = jobs[i];
            if (!ownerMailboxes.TryGetValue(job.Owner, out List<JobResult>? mailbox))
            {
                mailbox = new List<JobResult>();
                ownerMailboxes[job.Owner] = mailbox;
            }

            mailbox.Add(job);
            if (mailbox.Count > OwnerMailboxCapacity)
            {
                mailbox.RemoveAt(0);
            }
        }

        PushRecentJobs(jobs);
    }

    /// <summary>
    /// 取走某个 owner 的结果（<b>从该 owner 的邮箱里取</b>，不动别人的）。
    /// </summary>
    /// <param name="owner">归属名；空或空白按 <c>unknown</c> 处理。</param>
    /// <param name="limit">最多取几条；<c>&lt;= 0</c> 表示全部。</param>
    internal IReadOnlyList<JobResult> TakeOwnerJobs(string? owner, int limit)
    {
        string key = NormalizeOwnerName(owner);
        if (!ownerMailboxes.TryGetValue(key, out List<JobResult>? mailbox) || mailbox.Count == 0)
        {
            return Array.Empty<JobResult>();
        }

        int count = limit <= 0 ? mailbox.Count : Math.Min(limit, mailbox.Count);
        var taken = new List<JobResult>(count);
        for (int i = 0; i < count; i++)
        {
            taken.Add(mailbox[i]);
        }

        mailbox.RemoveRange(0, count);
        if (mailbox.Count == 0)
        {
            // 取空就摘掉这个键：每个 owner 的邮箱有容量上限，但**键的个数没有**，
            // 调用方每次换一个 owner 名（比如把任务序号拼进 owner）就会让这张表
            // 无界增长。空邮箱与不存在的键在语义上完全一样，删掉不影响任何调用。
            ownerMailboxes.Remove(key);
        }

        return taken;
    }

    /// <summary>归属名归一化（与内核的 <c>normalize_owner</c> 同口径：去空白 → 空则 unknown）。</summary>
    private static string NormalizeOwnerName(string? owner)
    {
        string trimmed = owner?.Trim() ?? string.Empty;
        return trimmed.Length == 0 ? "unknown" : trimmed;
    }

    /// <summary>
    /// 把一批结果塞进环形缓冲（供 <c>trit jobs</c> 与 <see cref="TritApi"/> 回看）。
    /// 结果一旦被 <c>PollJobs</c> 取出就出队了，不缓冲就永久丢失。
    /// </summary>
    internal void PushRecentJobs(IReadOnlyList<JobResult> jobs)
    {
        if (jobs is null)
        {
            return;
        }

        for (int i = 0; i < jobs.Count; i++)
        {
            if (recentJobs.Count >= RecentJobCapacity)
            {
                recentJobs.RemoveAt(0);
            }

            recentJobs.Add(jobs[i]);
        }
    }

    /// <summary>
    /// tML 原生调用面：其他模组用 <c>Mod.Call("TritRuntime.Xxx", ...)</c> 访问三进制内核。
    /// </summary>
    /// <exception cref="ArgumentException">命令缺失、参数个数或类型不符。</exception>
    /// <exception cref="InvalidOperationException">运行时未装配（原生与托管都不可用）。</exception>
    public override object Call(params object[] args)
    {
        if (args is null || args.Length == 0)
        {
            throw new ArgumentException(
                "TritRuntimeMod.Call 需要至少一个参数：命令名，形如 \"TritRuntime.Capabilities\"。" +
                "可用命令：" + string.Join(", ", TritRuntimeCommands.All),
                nameof(args));
        }

        if (args[0] is not string command || command.Length == 0)
        {
            throw new ArgumentException(
                "TritRuntimeMod.Call 的第一个参数必须是 TritRuntime.* 命令字符串。",
                nameof(args));
        }

        RuntimeShim runtime = shim ?? throw new InvalidOperationException(
            "三进制运行时未装配（原生库与托管回退都不可用），详见模组日志。");

        var rest = new object?[args.Length - 1];
        for (int i = 1; i < args.Length; i++)
        {
            rest[i - 1] = args[i];
        }

        // 内核的部分命令（例如无载荷的 Query）确实以 null 作为合法返回，
        // 所以这里不能用空合并掩盖，只做可空性抑制。
        return runtime.Call(command, rest)!;
    }

    /// <summary>
    /// 退出/切换存档时复位整个会话：内核的世界投影、日志、快照、任务结果与
    /// 桥接层的差分采样状态全部丢弃。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 这是「整个游戏的常驻内存」里最大的一块可控项：默认配置下日志约 5MB、
    /// 快照数 MB、世界车道表数 MB。它们都是<b>某一张存档的投影</b>，退出存档后
    /// 既没有语义、又一直挂在进程里，换存档还会继续往上叠。
    /// </para>
    /// <para>
    /// 复位顺序：先清任务邮箱与环形缓冲（它们持有 <see cref="JobResult"/> 的 JSON 字符串），
    /// 再清桥接层采样，最后让内核释放自己的状态。任一步失败都只记日志，绝不冒泡。
    /// </para>
    /// </remarks>
    internal void ResetWorldSession()
    {
        if (!loaded)
        {
            return;
        }

        ownerMailboxes.Clear();
        recentJobs.Clear();

        try
        {
            bridge?.ResetSession();
        }
        catch (Exception ex)
        {
            Logger.Warn("[trit] 复位采样状态时异常（不影响运行）：" + ex.Message);
        }

        try
        {
            if (shim is not null && !shim.ResetSession())
            {
                // 旧版原生库不认识这个操作。不复位只是继续占内存，功能完全不受影响，
                // 所以这里只记一次提示，不降级、不重试。
                Logger.Info("[trit] 当前内核不支持会话复位（旧版原生库），退出存档后内存不会归还；功能不受影响。");
            }
        }
        catch (Exception ex)
        {
            Logger.Warn("[trit] 会话复位失败（不影响运行）：" + ex.Message);
        }
    }

    private void Teardown()
    {
        loaded = false;
        jobPollingEnabled = false;
        recentJobs.Clear();
        ownerMailboxes.Clear();

        try
        {
            hooks?.Uninstall();
        }
        catch (Exception ex)
        {
            Logger.Warn("[trit] 卸载钩子时异常：" + ex.Message);
        }

        hooks = null;

        try
        {
            bridge?.Dispose();
        }
        catch (Exception ex)
        {
            Logger.Warn("[trit] 释放世界投影桥时异常：" + ex.Message);
        }

        bridge = null;

        try
        {
            shim?.Dispose();
        }
        catch (Exception ex)
        {
            Logger.Warn("[trit] 释放运行时内核时异常：" + ex.Message);
        }

        shim = null;
    }
}
