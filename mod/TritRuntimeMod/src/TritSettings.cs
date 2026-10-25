using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

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
        if (MaxNpcs < 0)
        {
            MaxNpcs = 0;
        }

        if (MaxProjectiles < 0)
        {
            MaxProjectiles = 0;
        }

        if (MaxPlayers < 0)
        {
            MaxPlayers = 0;
        }

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

        LibraryPath ??= string.Empty;
    }
}
