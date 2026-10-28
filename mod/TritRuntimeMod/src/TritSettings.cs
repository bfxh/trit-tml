#nullable enable

using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using TritRuntime.Kernel;

namespace TritRuntimeMod;

/// <summary>
/// 模组设置。来源优先级：<c>TritRuntimeMod.settings.json</c>（tML 存档根目录）&gt; 环境变量 &gt; 内置默认值。
/// </summary>
/// <remarks>
/// 刻意不用 <c>ModConfig</c>：那需要额外的本地化 hjson，一旦键名写错会在装载期抛异常。
/// 旁路层的设置必须"坏了也能起"，所以这里全部 try/catch 包裹，任何异常都退回默认值。
/// </remarks>
public sealed class TritSettings
{
    /// <summary>设置文件名（放在 tML 存档根目录）。</summary>
    public const string FileName = "TritRuntimeMod.settings.json";

    /// <summary>工作线程数上限（0 = 自动）。</summary>
    public const int MaxWorkerCount = 32;

    /// <summary>全局内存预算上限（MiB）。</summary>
    public const int MaxMemoryCapacityMb = 8192;

    /// <summary>事务日志条数上限（默认）。内核侧的默认值必须与此一致。</summary>
    public const int DefaultMaxLogEntries = 1 << 20;

    /// <summary>事务日志条数下限（低于此值连一次批量提交的历史都存不住）。</summary>
    public const int MinLogEntries = 4096;

    /// <summary>事务日志条数上限（16M 条约 80MB，再多就该改用更长快照间隔而不是堆日志）。</summary>
    public const int MaxLogEntriesLimit = 16 << 20;

    /// <summary>单模组同时在跑任务数上限。</summary>
    public const int MaxPerModInflight = 16;

    /// <summary>扫描分步粒度下限。</summary>
    public const int MinScanChunk = 256;

    /// <summary>扫描分步粒度上限。</summary>
    public const int MaxScanChunk = 1_048_576;

    /// <summary>GPU 收益阈值上限（2^28 ≈ 2.68 亿元素）。</summary>
    public const int MaxGpuMinTrits = 1 << 28;

    /// <summary>每 tick 取回任务结果数上限。</summary>
    public const int MaxPollJobsPerTick = 256;

    /// <summary>
    /// 采样槽位上限：每 tick 最多跟踪多少个 NPC。
    /// </summary>
    /// <remarks>
    /// <b>这个上限是内存护栏，不是性能建议。</b> <c>WorldBridge</c> 按
    /// <c>new EntityState[MaxNpcs]</c> 直接开定长数组（40 字节/项），装载期一次性分配。
    /// 设置文件里写一个 <c>2000000000</c>，装载时就是 80 GB 的分配请求 ——
    /// 表现是"模组本次会话不提供服务"（异常被装配流程吞掉），根因却是一个数字打错。
    /// 8192 项 ≈ 320 KB，放得下任何现实世界，也放不下任何手滑。
    /// </remarks>
    public const int MaxNpcsLimit = 8192;

    /// <summary>采样槽位上限：每 tick 最多跟踪多少条弹幕（16384 项 ≈ 640 KB）。</summary>
    public const int MaxProjectilesLimit = 16384;

    /// <summary>采样槽位上限：每 tick 最多跟踪多少名玩家（1024 项 ≈ 40 KB）。</summary>
    public const int MaxPlayersLimit = 1024;

    /// <summary>是否安装 Terraria.On_* 观测钩子。</summary>
    [JsonPropertyName("enableHooks")]
    public bool EnableHooks { get; set; } = true;

    /// <summary>
    /// 影子模式：同时挂第二个内核，对同一请求序列做逐字节差分。不一致只记录不提交。
    /// 上线切换前的必经阶段，代价约等于双倍内核开销。
    /// </summary>
    [JsonPropertyName("shadowMode")]
    public bool ShadowMode { get; set; }

    /// <summary>强制托管回退内核（诊断用：用于确认托管路径本身没问题）。</summary>
    [JsonPropertyName("forceManaged")]
    public bool ForceManaged { get; set; }

    /// <summary>原生库显式路径。空 = 走运行时默认探测顺序。</summary>
    [JsonPropertyName("libraryPath")]
    public string LibraryPath { get; set; } = string.Empty;

    /// <summary>是否允许默认探测（兼容壳目录、进程入口目录）。</summary>
    [JsonPropertyName("allowDefaultProbing")]
    public bool AllowDefaultProbing { get; set; } = true;

    /// <summary>每 tick 最多采样的 NPC 数。</summary>
    [JsonPropertyName("maxNpcs")]
    public int MaxNpcs { get; set; } = 256;

    /// <summary>每 tick 最多采样的 Projectile 数。</summary>
    [JsonPropertyName("maxProjectiles")]
    public int MaxProjectiles { get; set; } = 256;

    /// <summary>每 tick 最多采样的 Player 数。</summary>
    [JsonPropertyName("maxPlayers")]
    public int MaxPlayers { get; set; } = 16;

    /// <summary>每隔多少 tick 打一次快照。0 = 不打快照。</summary>
    [JsonPropertyName("snapshotIntervalTicks")]
    public int SnapshotIntervalTicks { get; set; } = 300;

    /// <summary>每隔多少 tick 输出一次内核统计。0 = 不输出。</summary>
    [JsonPropertyName("statsIntervalTicks")]
    public int StatsIntervalTicks { get; set; } = 600;

    /// <summary>是否输出每次提交 / 派发的明细日志（很吵，仅排查用）。</summary>
    [JsonPropertyName("verboseLogging")]
    public bool VerboseLogging { get; set; }

    /// <summary>连续失败多少次后自动摘掉钩子（防止把游戏拖垮）。</summary>
    [JsonPropertyName("maxConsecutiveHookFailures")]
    public int MaxConsecutiveHookFailures { get; set; } = 5;

    /// <summary>
    /// 是否启用旁路并行任务池。关闭时强制单工作线程 + 单模组单任务（等价于串行），
    /// 用于排查"任务池抢线程"这类只在多核环境出现的问题。
    /// </summary>
    [JsonPropertyName("enableParallel")]
    public bool EnableParallel { get; set; } = true;

    /// <summary>并行任务池工作线程数。0 = 自动（内核取 min(可用并行度, 4)）。</summary>
    [JsonPropertyName("workerCount")]
    public int WorkerCount { get; set; }

    /// <summary>
    /// 并行任务池的内存预算（MiB）。0 = 不限制。
    /// </summary>
    /// <remarks>
    /// 只管任务池（超过预算的任务直接判失败，不会排队等待）；事务日志与快照
    /// 的常驻内存由 <see cref="MaxLogEntries"/> 与快照间隔决定，见那边的说明。
    /// </remarks>
    [JsonPropertyName("memoryCapacityMb")]
    public int MemoryCapacityMb { get; set; } = 512;

    /// <summary>
    /// 事务日志条数上限。**这个值就是回滚窗口**：每 tick 的变化量决定它覆盖多少秒。
    /// </summary>
    /// <remarks>
    /// 每 100 万条约 5MB 常驻（日志按列存储，每条只有 <c>key</c> + <c>delta</c>）。
    /// 实测约 1600 条变化/tick 时，100 万条 ≈ 10 秒、400 万条 ≈ 40 秒。
    /// 内存吃紧就往小调，需要更长撤销历史就往大调；两头的代价都只在这一个数上。
    /// </remarks>
    [JsonPropertyName("maxLogEntries")]
    public int MaxLogEntries { get; set; } = DefaultMaxLogEntries;

    /// <summary>同一模组（owner）同时在跑的任务上限，防独占。</summary>
    [JsonPropertyName("perModInflight")]
    public int PerModInflight { get; set; } = 2;

    /// <summary>扫描任务的分步粒度（每步处理的元素数），决定抢占边界。</summary>
    [JsonPropertyName("scanChunk")]
    public int ScanChunk { get; set; } = 4096;

    /// <summary>
    /// GPU 策略文本：<c>auto</c> / <c>off</c> / <c>on</c>（大小写不敏感，解析失败按 <c>auto</c>）。
    /// 玩家可以用 <c>trit gpu on|off|auto</c> 在游戏里改，改完立即写回设置文件。
    /// </summary>
    [JsonPropertyName("gpuPolicy")]
    public string GpuPolicyText { get; set; } = "auto";

    /// <summary>小于该规模的任务走 CPU（GPU dispatch 延迟占主导，小规模反而更慢）。</summary>
    [JsonPropertyName("gpuMinTrits")]
    public int GpuMinTrits { get; set; } = 1_048_576;

    /// <summary>每 tick 最多取回多少条已完成的任务结果。</summary>
    [JsonPropertyName("pollJobsPerTick")]
    public int PollJobsPerTick { get; set; } = 8;

    /// <summary>是否在聊天/日志里输出每条扫描结果（默认关，避免刷屏）。</summary>
    [JsonPropertyName("logJobResults")]
    public bool LogJobResults { get; set; }

    /// <summary>
    /// 归一化后的 GPU 策略枚举。刻意不序列化——设置文件里存的是文本，
    /// 枚举值由 <see cref="Sanitize"/> 保证合法，因此这里不会再退回默认。
    /// </summary>
    [JsonIgnore]
    public GpuPolicy GpuPolicyKind => ParseGpuPolicy(GpuPolicyText);

    [JsonIgnore]
    public string LoadedFrom { get; private set; } = string.Empty;

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
    };

    /// <summary>读取设置；任何异常都退回默认值并在 <paramref name="warning"/> 里给出原因。</summary>
    public static TritSettings Load(out string warning)
    {
        warning = string.Empty;
        string dir;
        try
        {
            // Main.SavePath 由 Terraria 在启动早期赋值；拿不到就用当前目录。
            dir = Terraria.Main.SavePath;
            if (string.IsNullOrWhiteSpace(dir))
            {
                dir = AppContext.BaseDirectory;
            }
        }
        catch (Exception ex)
        {
            warning = "读取 Main.SavePath 失败，设置退回默认值：" + ex.Message;
            return new TritSettings();
        }

        string path;
        try
        {
            path = Path.Combine(dir, FileName);
        }
        catch (Exception ex)
        {
            warning = "拼装设置文件路径失败，设置退回默认值：" + ex.Message;
            return new TritSettings();
        }

        TritSettings settings;
        if (File.Exists(path))
        {
            try
            {
                settings = JsonSerializer.Deserialize<TritSettings>(File.ReadAllText(path), ReadOptions)
                           ?? new TritSettings();
                settings.LoadedFrom = path;
                settings.Sanitize();
                return settings;
            }
            catch (Exception ex)
            {
                warning = $"设置文件解析失败（{path}），已退回默认值：{ex.Message}";
                return new TritSettings();
            }
        }

        settings = new TritSettings();
        settings.Sanitize();

        // 首次运行：写一份带注释也合法的默认设置，方便用户改。
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(settings, WriteOptions));
            settings.LoadedFrom = path;
        }
        catch (Exception ex)
        {
            warning = "默认设置文件写出失败（不影响运行）：" + ex.Message;
        }

        return settings;
    }

    /// <summary>把明显不合理的值夹回安全范围。</summary>
    public void Sanitize()
    {
        // 采样上限两头都要夹：负数没有意义，而**过大的值是内存问题**——
        // 桥接层按这个数开定长数组，不夹住就是一个数字打错让模组装不起来。
        MaxNpcs = Clamp(MaxNpcs, 0, MaxNpcsLimit);
        MaxProjectiles = Clamp(MaxProjectiles, 0, MaxProjectilesLimit);
        MaxPlayers = Clamp(MaxPlayers, 0, MaxPlayersLimit);

        if (SnapshotIntervalTicks < 0)
        {
            SnapshotIntervalTicks = 0;
        }

        if (StatsIntervalTicks < 0)
        {
            StatsIntervalTicks = 0;
        }

        if (MaxConsecutiveHookFailures < 0)
        {
            MaxConsecutiveHookFailures = 0;
        }

        // 0 是合法值（自动），非法值（负数 / 过大）一律退回自动，绝不猜。
        if (WorkerCount < 0 || WorkerCount > MaxWorkerCount)
        {
            WorkerCount = 0;
        }

        MemoryCapacityMb = Clamp(MemoryCapacityMb, 0, MaxMemoryCapacityMb);
        MaxLogEntries = Clamp(MaxLogEntries, MinLogEntries, MaxLogEntriesLimit);
        PerModInflight = Clamp(PerModInflight, 1, MaxPerModInflight);
        ScanChunk = Clamp(ScanChunk, MinScanChunk, MaxScanChunk);
        GpuMinTrits = Clamp(GpuMinTrits, 0, MaxGpuMinTrits);
        PollJobsPerTick = Clamp(PollJobsPerTick, 1, MaxPollJobsPerTick);

        // 归一化成小写三选一：这样内核侧的字符串比较才有确定的结果。
        GpuPolicyText ??= "auto";
        string policy = GpuPolicyText.Trim().ToLowerInvariant();
        GpuPolicyText = policy is "on" or "off" ? policy : "auto";

        LibraryPath ??= string.Empty;
    }

    /// <summary>
    /// 把当前设置写回 <see cref="FileName"/>（tML 存档根目录）。
    /// 写失败只回 <paramref name="warning"/>——设置保存失败绝不能影响游戏。
    /// </summary>
    /// <returns>是否写入成功。</returns>
    public bool Save(out string warning)
    {
        warning = string.Empty;

        string path;
        try
        {
            path = Path.Combine(SettingsDirectory(), FileName);
        }
        catch (Exception ex)
        {
            warning = "拼装设置文件路径失败，本次修改只在本会话生效：" + ex.Message;
            return false;
        }

        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(this, WriteOptions));
            return true;
        }
        catch (Exception ex)
        {
            warning = $"设置文件写出失败（{path}），本次修改只在本会话生效：{ex.Message}";
            return false;
        }
    }

    /// <summary>解析 GPU 策略文本；无法识别的一律按 <see cref="GpuPolicy.Auto"/>。</summary>
    public static GpuPolicy ParseGpuPolicy(string? text)
    {
        switch (text?.Trim().ToLowerInvariant())
        {
            case "on":
                return GpuPolicy.On;
            case "off":
                return GpuPolicy.Off;
            default:
                return GpuPolicy.Auto;
        }
    }

    private static string SettingsDirectory()
    {
        try
        {
            string dir = Terraria.Main.SavePath;
            if (!string.IsNullOrWhiteSpace(dir))
            {
                return dir;
            }
        }
        catch
        {
            // Main.SavePath 在游戏极早期可能抛异常，落到入口目录即可
        }

        return AppContext.BaseDirectory;
    }

    private static int Clamp(int value, int min, int max) => value < min ? min : value > max ? max : value;
}
