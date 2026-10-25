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

    /// <summary>装载确认：日志里没有这一行就说明 tML 没实例化本类。</summary>
    public override void OnModLoad()
    {
        TritRuntimeMod.Instance?.Logger.Info("[trit] 世界投影 tick 驱动已装载（ModSystem）");
    }

    /// <summary>每 tick 推进一次世界投影；模组未装配或无桥接时静默跳过。</summary>
    public override void PostUpdateEverything()
    {
        if (!announced)
        {
            announced = true;
            TritRuntimeMod.Instance?.Logger.Info("[trit] 首次 tick 回调到达（PostUpdateEverything）");
        }

        TritRuntimeMod.Instance?.OnWorldTick();
    }
}
