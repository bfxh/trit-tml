#nullable enable

using System.Diagnostics;

namespace TritRuntimeMod;

/// <summary>
/// 错误日志的节流器：同一类错误按墙上时钟限速，并把被吞掉的条数报出来。
/// </summary>
/// <remarks>
/// <para>
/// 为什么要有这个类：模组里所有被打上「吞掉异常、只记日志」的地方，
/// 都可能出现在 <b>每 tick 执行一次</b>的路径上（60 次/秒）。这类日志一旦真的开始报错，
/// 就是每秒几十条、每条还带一整份 <c>Exception.ToString()</c>（栈回溯 + 内嵌异常链）——
/// 真机上已经踩过一次：4 分钟写出 13 MB / 8.9 万行。
/// </para>
/// <para>
/// 只节流<b>还不够</b>：静默丢弃会让人误以为"只发生过一次"。所以被吞掉的条数要累计，
/// 下次放行时一起打出来——"自上次报告以来又吞了 N 次"才是能据以判断严重性的信息。
/// </para>
/// <para>
/// 一个节流器只管<b>一类</b>错误。共用节流槽会发生「成功路径把槽占掉，
/// 错误路径永远打不出来」这种静默失效（<c>WorldBridge</c> 的统计/状态日志就是这么错的）。
/// </para>
/// </remarks>
internal sealed class ErrorThrottle
{
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly double intervalSeconds;

    private double lastAt = double.NegativeInfinity;
    private int suppressed;

    public ErrorThrottle(double intervalSeconds)
    {
        this.intervalSeconds = intervalSeconds;
    }

    /// <summary>
    /// 判断本次是否应当打日志。
    /// </summary>
    /// <param name="suppressedCount">
    /// 允许打时，输出「自上次放行以来被吞掉的次数」；不允许打时输出 0。
    /// </param>
    /// <remarks>
    /// 调用方应当<b>只在返回 true 之后</b>才去拼消息字符串——
    /// 异常文本与插值的开销不低，节流的目的之一就是别在热路径上付这份钱。
    /// </remarks>
    public bool ShouldLog(out int suppressedCount)
    {
        double now = clock.Elapsed.TotalSeconds;
        if (now - lastAt < intervalSeconds)
        {
            suppressedCount = 0;
            suppressed++;
            return false;
        }

        suppressedCount = suppressed;
        suppressed = 0;
        lastAt = now;
        return true;
    }
}
