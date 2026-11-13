using System.Collections.Generic;
using TritRuntime.Kernel;

namespace TritRuntimeMod;

/// <summary>
/// 内核接管层（Phase 1 L1 MVP）。
/// </summary>
/// <remarks>
/// 设计约束：
/// - <b>L0 零开销</b>：Takeover 为 ObserveOnly 时，整个类不做任何事（AfterOrig 直接 return）。
/// - <b>主线程安全</b>：接管决策发生在 TritHooks 的 orig 回调之后 —— 已经在游戏主线程，
///   所以 C# 对象修改是安全的；置信度表是 ConcurrentDictionary，旁路线程也能写。
/// - <b>降级链完整</b>：GetPredictions 返回空 / 内核不认识 opcode / 载荷损坏 → 跳过接管，
///   置信度持续下跌 → 跌破 threshold → 永远对该实体弃权。
/// - <b>Phase 1 MVP 只跑链路验证</b>：GetPredictions + 置信度表 + L0/L1 切换，
///   真正的"trit delta → C# NPC 字段"映射留到 Phase 1.5（需要 LaneMap key↔Terraria 对象字段绑定）。
/// </remarks>
internal sealed class EntityTakeover
{
    private readonly RuntimeShim shim;
    private readonly TritSettings settings;

    /// <summary>
    /// 最近一次 GetPredictions 返回的预测结果。
    /// ConcurrentDictionary 但实际上只用主线程写（AfterOrig 调 predictions），
    /// 旁路线程只会读（如果未来有 record_outcome 的旁路版本）。
    /// </summary>
    private IReadOnlyList<(uint Key, byte Delta)>? lastPredictions;

    /// <summary>本 tick 被接管的实体计数（日志 + 观测用）。</summary>
    public int TakeoverCount { get; private set; }

    /// <summary>本 tick 收到的预测数（内核那边 fork.predict_all 返回了多少 key）。</summary>
    public int PredictionCount => lastPredictions?.Count ?? 0;

    public EntityTakeover(RuntimeShim shim, TritSettings settings)
    {
        this.shim = shim;
        this.settings = settings;
    }

    /// <summary>
    /// 在 TritHooks 的 Hook 链里、原版 orig 执行之后调用。
    /// Phase 1 MVP 不做实际字段覆盖 —— 只调 predictions + 更新观测计数器。
    /// 真正的"内核结果覆盖原版"留到 Phase 1.5（需要 LaneMap 映射）。
    /// </summary>
    /// <remarks>
    /// 调用方必须保证：settings.Takeover 已经是 Sanitize 后的合法值。
    /// </remarks>
    public void AfterOrig()
    {
        // L0 零开销降级：直接 return，不进任何分支
        if (settings.Takeover == TritSettings.TakeoverMode.ObserveOnly)
        {
            return;
        }

        // 托管回退 / 旧版原生库不认识 Predictions opcode
        // → GetPredictions 返回空列表 → 以下循环跳过，开销 = 一次 Submit(Opcode.Predictions, []) 调用
        var preds = shim.GetPredictions();
        lastPredictions = preds;
        TakeoverCount = 0;

        // Phase 1 MVP：只跑链路验证 + 日志。
        // 映射 LaneMap key → Terraria 实体字段是 Phase 1.5 的事。
        if (preds.Count > 0 && settings.LogJobResults)
        {
            TritRuntimeMod.Instance?.Logger?.Info(
                $"[trit] 内核预测 {preds.Count} 个实体（MVP 不做实际回写）");
        }
    }
}
