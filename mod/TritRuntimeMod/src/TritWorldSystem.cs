#nullable enable

using Terraria;
using Terraria.ModLoader;

namespace TritRuntimeMod;

/// <summary>
/// 世界投影的 tick 驱动：走 tML 官方 <see cref="ModSystem"/> 生命周期，而不是 MonoMod 钩子。
/// </summary>
/// <remarks>
/// <para>
/// 为什么不用 <c>On_Main.Update</c>：专用服务器实测（无头，tML 1.4.4.9）证明
/// <c>Terraria.Main.Update(GameTime)</c> 在服务器端<b>根本不触发</b>——
/// 服务器走的是 <c>Main.DedServ</c> 自己的循环。桥接层挂在它上面时，
/// 装载期一切正常（自检 PASS、钩子报告 4/4），但之后一条 tick 日志都不会有，
/// 属于"看起来装好了、实际全程空转"的静默故障。
/// </para>
/// <para>
/// <c>PostUpdateEverything()</c> 是 tML 的官方生命周期回调，客户端与服务器都会每 tick 调用，
/// 且不依赖任何内部方法签名——比 hook 到具体方法稳定得多，也不会因为 tML 改内部实现而失效。
/// </para>
/// <para>
/// 这里只做驱动，不做任何三进制逻辑；观测异常一律由 <see cref="WorldBridge"/> 自身吞掉。
/// </para>
/// </remarks>
public sealed class TritWorldSystem : ModSystem
{
    private bool announced;

    // 「回调压根没到」和「回调到了但还在菜单、被自己 early-return 掉」在日志里长得一模一样：
    // 都是"一条 tick 数据都没有"。不区分这两种，就会把配置问题误判成引擎行为
    // （专用服务器那一轮就是这么误判的：serverconfig 里 world= 没开，
    // 服务器停在选世界界面，于是我们得出"服务器不跑世界更新循环"的错误结论）。
    private bool announcedMenuSkip;

    /// <summary>装载确认：日志里没有这一行就说明 tML 没实例化本类。</summary>
    public override void OnModLoad()
    {
        TritRuntimeMod.Instance?.Logger.Info("[trit] 世界投影 tick 驱动已装载（ModSystem）");
    }

    /// <summary>每 tick 推进一次世界投影；模组未装配、无桥接或不在世界中时静默跳过。</summary>
    /// <remarks>
    /// <c>Main.gameMenu</c> 时必须跳过：主菜单同样会走 <c>PostUpdateEverything</c>，
    /// 但那里没有世界可采样——继续跑只会每 tick 白采 256 个 NPC/弹幕槽位、
    /// 白提一次批量事务、白写日志。实测主菜单停留一分钟就能把日志推到 600 tick。
    /// </remarks>
    public override void PostUpdateEverything()
    {
        if (Main.gameMenu)
        {
            if (!announcedMenuSkip)
            {
                announcedMenuSkip = true;
                TritRuntimeMod.Instance?.Logger.Info(
                    "[trit] tick 回调已到达，但当前不在世界中（Main.gameMenu=true），暂不采样；" +
                    "若长时间停在这一步，说明还没载入世界（服务器多半是 serverconfig 里没开 world=）");
            }

            return;
        }

        if (!announced)
        {
            announced = true;
            TritRuntimeMod.Instance?.Logger.Info("[trit] 首次 tick 回调到达（PostUpdateEverything）");
        }

        TritRuntimeMod.Instance?.OnWorldTick();
    }

    /// <summary>
    /// 退出/切换存档：把整张存档的投影（世界车道、日志、快照、任务结果）放掉。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 用 tML 官方回调而不是钩子 <c>On_WorldGen.</c> 之类：这是两端（客户端 / 服务器）
    /// 都会触发的生命周期点，且不依赖内部签名（与 <see cref="PostUpdateEverything"/>
    /// 同一个理由，见类注释）。
    /// </para>
    /// <para>
    /// 不做这一步的后果不是"崩"，而是"越玩越胖"：每张存档的日志、快照、
    /// 世界投影都留在进程里，来回进出几次存档后内存只增不减。
    /// 复位本身任何异常都在模组侧吞掉，绝不让退出存档卡住。
    /// </para>
    /// </remarks>
    public override void OnWorldUnload()
    {
        TritRuntimeMod.Instance?.ResetWorldSession();
    }
}
