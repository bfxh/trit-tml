using System;
using Terraria.ModLoader;
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
/// 完全不需要引用本模组的类型。
/// </para>
/// </remarks>
public sealed class TritRuntimeMod : Mod
{
    private TritSettings settings = new();
    private RuntimeShim? shim;
    private WorldBridge? bridge;
    private TritHooks? hooks;
    private bool loaded;

    /// <summary>当前装载中的模组实例（未装载时为 <c>null</c>）。</summary>
    public static TritRuntimeMod? Instance { get; private set; }

    /// <summary>运行时是否已成功装配。</summary>
    public bool RuntimeAvailable => loaded && shim is not null;

    /// <summary>装载期端到端自检是否通过（决定是否安装钩子）。</summary>
    public bool SelfCheckPassed { get; private set; }

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
                ConfigJson = string.Empty,
                ForceManaged = settings.ForceManaged,
                ShadowMode = settings.ShadowMode,
                AllowDefaultProbing = settings.AllowDefaultProbing,
            });

            loaded = true;

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

    private void Teardown()
    {
        loaded = false;

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
