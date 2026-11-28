#nullable enable

namespace TritRuntime.Kernel;

/// <summary>
/// 垂直速度的一步仿射动力学模型：<c>vy_next = Alpha * vy + Beta</c>。
/// </summary>
/// <remarks>
/// <para>
/// 用途：ReplaceOrig 接管里要跳过原版 <c>NPC.AI</c>，但重力、阻尼、阻力都写在那段代码里，
/// 快照又没有这些常量。这里不猜任何游戏常量，只用「AI 前后 velocity.Y 的观测对」拟合：
/// </para>
/// <list type="bullet">
/// <item>常量重力：<c>Alpha = 1</c>、<c>Beta = Δvy</c>（最常见）</item>
/// <item>悬停阻尼 / 入水阻力：<c>Beta ≈ 0</c>、<c>Alpha &lt; 1</c></item>
/// <item>终端速度钳制：速度不再变化 → 退化成 <c>Alpha = 1</c>、<c>Beta = 0</c></item>
/// </list>
/// <para>
/// 拟合出的模型必须先用后续观测「确认」（残差在容差内）才允许外推；只要出现一次明显
/// 不符（冲量、撞墙、状态切换）就重新拟合并清零确认计数，因此错误模型最多只影响一 tick。
/// </para>
/// <para>
/// 纯结构体、无分配、无线程状态：调用方按实体槽位各持一份即可。
/// </para>
/// </remarks>
public struct VerticalMotionModel
{
    /// <summary>拟合斜率所需的最小 vy 间隔；两次观测靠得太近时退化成常量 Δ 模型。</summary>
    public const float MinSlopeSpan = 0.05f;

    /// <summary>拟合后需要多少次「残差在容差内」的观测才允许外推。</summary>
    public const int RequiredConfirmations = 1;

    /// <summary>斜率合理范围：物理上阻尼/重力都不会让一步变化超过 2 倍。</summary>
    private const float MinAlpha = -0.25f;
    private const float MaxAlpha = 2f;

    /// <summary>截距合理范围（像素/tick）；超过说明这一对样本不是自由演化。</summary>
    private const float MaxBeta = 8f;

    private float lastBefore;
    private float lastAfter;
    private bool hasLast;

    private float alpha;
    private float beta;
    private bool fitted;
    private int confirmations;

    /// <summary>模型是否已拟合且通过确认，可以用于外推。</summary>
    public bool CanPredict => fitted && confirmations >= RequiredConfirmations;

    /// <summary>拟合出的斜率（仅供测试/诊断）。</summary>
    public float Alpha => alpha;

    /// <summary>拟合出的截距（仅供测试/诊断）。</summary>
    public float Beta => beta;

    /// <summary>清空全部观测与模型（落地、切类型、冲量之后调用）。</summary>
    public void Reset()
    {
        lastBefore = 0f;
        lastAfter = 0f;
        hasLast = false;
        alpha = 0f;
        beta = 0f;
        fitted = false;
        confirmations = 0;
    }

    /// <summary>按模型外推下一 tick 的 vy（调用前必须检查 <see cref="CanPredict"/>）。</summary>
    public float Predict(float vy) => alpha * vy + beta;

    /// <summary>
    /// 喂入一对观测：同一段代码执行前后的 <paramref name="vyBefore"/> / <paramref name="vyAfter"/>。
    /// <para>采样点必须固定夹在同一段逻辑两侧（例如 AI 调用前后），否则差值没有意义。</para>
    /// </summary>
    public void Observe(float vyBefore, float vyAfter)
    {
        if (!float.IsFinite(vyBefore) || !float.IsFinite(vyAfter))
        {
            Reset(); // NaN/Inf 会污染拟合，直接丢弃整段历史
            return;
        }

        if (!hasLast)
        {
            lastBefore = vyBefore;
            lastAfter = vyAfter;
            hasLast = true;
            return;
        }

        if (fitted)
        {
            float predicted = alpha * vyBefore + beta;
            if (Math.Abs(vyAfter - predicted) <= Tolerance(predicted))
            {
                if (confirmations < int.MaxValue)
                {
                    confirmations++;
                }

                lastBefore = vyBefore;
                lastAfter = vyAfter;
                return;
            }
        }

        Fit(vyBefore, vyAfter);
        lastBefore = vyBefore;
        lastAfter = vyAfter;
    }

    private void Fit(float vyBefore, float vyAfter)
    {
        float span = vyBefore - lastBefore;
        if (Math.Abs(span) >= MinSlopeSpan)
        {
            float a = (vyAfter - lastAfter) / span;
            float b = vyAfter - a * vyBefore;
            if (IsSane(a, b))
            {
                alpha = a;
                beta = b;
                fitted = true;
                confirmations = 0;
                return;
            }
        }

        // 速度几乎没变（终端速度 / 悬停）或斜率不合理：退化成常量 Δ。
        alpha = 1f;
        beta = vyAfter - vyBefore;
        fitted = float.IsFinite(beta) && Math.Abs(beta) <= MaxBeta;
        confirmations = 0;
    }

    private static bool IsSane(float a, float b) =>
        float.IsFinite(a)
        && float.IsFinite(b)
        && a >= MinAlpha
        && a <= MaxAlpha
        && Math.Abs(b) <= MaxBeta;

    /// <summary>残差容差：绝对值下限 + 相对项，照顾大速度下的浮点噪声。</summary>
    private static float Tolerance(float predicted) => 0.005f + 0.005f * Math.Abs(predicted);
}
