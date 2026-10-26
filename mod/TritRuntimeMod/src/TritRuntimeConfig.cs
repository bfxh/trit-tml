using System.ComponentModel;
using Terraria.ModLoader;
using Terraria.ModLoader.Config;

// tML 1.4.4.9 将 [Label]/[Tooltip] 标记为过时（改用本地化 hjson），
// 但旧属性在本版本仍正常渲染中文标签，且能避开 hjson 键名错配导致的装载期异常。
// 本模组刻意不用本地化 hjson，故在此抑制该过时警告，保持"0 警告"整洁度。
#pragma warning disable CS0618

namespace TritRuntimeMod;

/// <summary>
/// 计算后端选择：映射到原生内核的 GPU 策略（Auto→auto、Cpu→off、Gpu→on）。
/// </summary>
public enum ComputeBackendOption
{
    /// <summary>由模组按任务规模自动选 CPU / GPU。</summary>
    Auto,

    /// <summary>强制只用 CPU（等价于 gpuPolicy=off）。</summary>
    Cpu,

    /// <summary>尽量用 GPU；不可用时自动回退 CPU（等价于 gpuPolicy=on）。</summary>
    Gpu,
}

/// <summary>
/// 游戏内设置面板（tML "配置" 入口，从 Mods 菜单的齿轮点开）。
/// 这是玩家可改设置的<b>展示层</b>，真实落盘与运行时读取仍以 <see cref="TritSettings"/> 为准——
/// 本类的任何改动都会在 <see cref="OnChanged"/> 里写回 <see cref="TritSettings"/> 并即时生效。
/// </summary>
/// <remarks>
/// 刻意不用本地化 hjson：字段名 / 枚举名本身就是可读标签，避免键名写错在装载期抛异常。
/// 展示层与持久层（TritRuntimeMod.settings.json）的同步发生在 <c>TritRuntimeMod.Load</c> 与
/// <see cref="OnChanged"/>：装载时从 <see cref="TritSettings"/> 读入（保证面板反映真实设置），
/// 改动时写回 <see cref="TritSettings"/>（保证持久化与控制台命令 trit gpu 同源）。
/// </remarks>
[Label("TritRuntime 设置")]
public sealed class TritRuntimeConfig : ModConfig
{
    /// <summary>客户端侧配置（每个玩家独立，存到客户端的 ModConfigs 目录）。</summary>
    public override ConfigScope Mode => ConfigScope.ClientSide;

    /// <summary>计算后端：Auto / Cpu / Gpu。</summary>
    [Label("计算后端")]
    [Tooltip("Auto = 模组按任务规模自动选 CPU/GPU；Cpu = 强制只用 CPU；Gpu = 尽量用 GPU，不可用时自动回退 CPU。")]
    [DefaultValue(ComputeBackendOption.Auto)]
    public ComputeBackendOption ComputeBackend { get; set; } = ComputeBackendOption.Auto;

    /// <summary>是否强制使用托管回退内核。</summary>
    [Label("强制托管回退内核")]
    [Tooltip("诊断用：勾选后不使用原生 Rust 内核，改用纯托管实现。修改后需重启游戏生效。")]
    [DefaultValue(false)]
    public bool ForceManagedFallback { get; set; }

    /// <summary>是否输出详细日志。</summary>
    [Label("详细日志")]
    [Tooltip("输出每次提交 / 派发的明细日志，很吵，仅排查用。")]
    [DefaultValue(false)]
    public bool VerboseLogging { get; set; }

    /// <summary>是否在聊天/日志里输出每条扫描结果。</summary>
    [Label("记录扫描结果")]
    [Tooltip("在聊天 / 日志里输出每条扫描结果，默认关闭避免刷屏。")]
    [DefaultValue(false)]
    public bool LogJobResults { get; set; }

    /// <summary>是否安装世界观测钩子。</summary>
    [Label("安装世界观测钩子")]
    [Tooltip("是否挂 Terraria 钩子做世界投影。修改后需重启游戏生效。")]
    [DefaultValue(true)]
    public bool EnableWorldHooks { get; set; } = true;

    /// <summary>
    /// 玩家在面板里改完点"完成"时触发：把选择写回 <see cref="TritSettings"/> 并尽量即时生效。
    /// GPU 策略是热切换的（与控制台命令 trit gpu 同一条路径）；
    /// ForceManaged / EnableWorldHooks 是装载期选项，热改只落盘，下次启动生效。
    /// </summary>
    public override void OnChanged()
    {
        TritRuntimeMod.Instance?.ApplyPlayerConfig(this);
    }
}
