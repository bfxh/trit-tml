#nullable enable

using System.Collections.Generic;
using TritRuntime.Kernel;
using Terraria;

namespace TritRuntimeMod;

/// <summary>
/// 内核接管层（L1 PatchAfterOrig）。
/// </summary>
/// <remarks>
/// <para>
/// Phase 1 MVP 链路验证版 → Phase 1.5 真正接管：按 LaneMap key 解码
/// <c>kind / slot / field</c>，把内核返回的 trit delta（Neg / Zero / Pos）
/// 映射成 Terraria 实体字段的 <b>方向门</b>。
/// </para>
/// <para>
/// <b>方向门语义</b>（Phase 1.5 MVP）：
/// <list type="bullet">
/// <item><c>delta=2 (Pos)</c> → 确保字段值 ≥ 0（velocity 朝正方向；direction = +1）</item>
/// <item><c>delta=1 (Neg)</c> → 确保字段值 ≤ 0（velocity 朝负方向；direction = -1）</item>
/// <item><c>delta=0 (Zero / 弃权)</c> → 跳过（原版结果保留）</item>
/// </list>
/// </para>
/// <para>
/// <b>零开销降级链</b>：
/// <list type="bullet">
/// <item>ObserveOnly → 构造函数里创建但 AfterOrig 第一行就 return</item>
/// <item>托管回退 / 旧版原生库 → GetPredictions 返回空列表 → 全循环跳过</item>
/// <item>Rust 侧 confidence &lt; threshold → predict 返回 Unknown → C# delta=0 → 跳过</item>
/// <item>实体 inactive / slot 越界 → 跳过这条 prediction（内核可能没同步采样但 fork 里有旧数据）</item>
/// </list>
/// </para>
/// <para>
/// <b>只接管方向类字段</b>：VelocityX / VelocityY / Direction。不碰状态机字段
/// （Active / Life / LifeMax 受事件驱动不是 AI 直接改的，接管会炸游戏逻辑）。
/// </para>
/// </remarks>
internal sealed class EntityTakeover
{
    private readonly RuntimeShim shim;
    private readonly TritSettings settings;

    /// <summary>最近一次 GetPredictions 返回的预测结果。</summary>
    private IReadOnlyList<(uint Key, byte Delta)>? lastPredictions;

    /// <summary>本 tick 被方向门接管的实体字段计数（日志 / 观测用）。</summary>
    public int TakeoverCount { get; private set; }

    /// <summary>本 tick 收到的内核预测条数（fork.predict_all 返回量）。</summary>
    public int PredictionCount => lastPredictions?.Count ?? 0;

    public EntityTakeover(RuntimeShim shim, TritSettings settings)
    {
        this.shim = shim;
        this.settings = settings;
    }

    /// <summary>
    /// 在 <see cref="TritRuntimeMod.OnWorldTick"/> 里全部原版 AI 跑完后调用。
    /// <b>PatchAfterOrig 语义</b>：原版已经算完一遍正确结果，这里只是"内核置信度够高就用内核方向修正"。
    /// </summary>
    /// <remarks>
    /// 调用方必须包裹 <c>try/catch</c> —— 任何接管异常绝不能中断游戏 tick。
    /// </remarks>
    public void AfterOrig()
    {
        // L0 零开销降级
        if (settings.Takeover == TritSettings.TakeoverMode.ObserveOnly)
        {
            return;
        }

        var preds = shim.GetPredictions();
        lastPredictions = preds;
        TakeoverCount = 0;

        if (preds.Count == 0)
        {
            return; // Rust confidence 全不过 / prediction_interval_ticks 门控 / 托管回退
        }

        foreach (var (key, delta) in preds)
        {
            // delta=0 是内核弃权（confidence < threshold / 震荡 / 样本不足）
            // 托管回退 OpPredictions 返回空帧 → preds.Count=0 已经在上层 return 了
            if (delta is 0 or > 2)
            {
                continue;
            }

            uint kind = LaneMap.KindOf(key);
            int slot = LaneMap.SlotOf(key);
            uint field = LaneMap.FieldOf(key);

            int applied = kind switch
            {
                LaneMap.KindNpc => ApplyToNpc(slot, field, delta),
                LaneMap.KindProjectile => ApplyToProjectile(slot, field, delta),
                // Player 目前不接管（玩家 velocity 受玩家输入驱动，内核不该乱改）
                _ => 0,
            };

            TakeoverCount += applied;
        }

        if (settings.LogJobResults && TakeoverCount > 0)
        {
            TritRuntimeMod.Instance?.Logger?.Info(
                $"[trit] 接管层：{preds.Count} 条预测 → {TakeoverCount} 条方向门已应用");
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 方向门实现
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>对单个 NPC 的单个字段应用方向门。返回 1 表示实际改了值。</summary>
    private static int ApplyToNpc(int slot, uint field, byte delta)
    {
        // Main.npc 总长度 = Main.npc.Length（NPC 是预分配数组，不是 active 数量）
        if (slot < 0 || slot >= Main.npc.Length) return 0;
        var npc = Main.npc[slot];
        if (npc is null || !npc.active) return 0;

        float fdelta = delta == 2 ? +1f : -1f; // Pos→+1, Neg→-1

        return field switch
        {
            LaneMap.FieldVelocityX => GateFloat(ref npc.velocity.X, fdelta),
            LaneMap.FieldVelocityY => GateFloat(ref npc.velocity.Y, fdelta),
            LaneMap.FieldDirection => GateDirection(ref npc.direction, delta),
            // 其他字段（Life / Active / Position 等）不接管 —— 安全优先
            _ => 0,
        };
    }

    /// <summary>对单个 Projectile 的单个字段应用方向门。返回 1 表示实际改了值。</summary>
    private static int ApplyToProjectile(int slot, uint field, byte delta)
    {
        if (slot < 0 || slot >= Main.projectile.Length) return 0;
        var proj = Main.projectile[slot];
        if (proj is null || !proj.active) return 0;

        float fdelta = delta == 2 ? +1f : -1f;

        return field switch
        {
            LaneMap.FieldVelocityX => GateFloat(ref proj.velocity.X, fdelta),
            LaneMap.FieldVelocityY => GateFloat(ref proj.velocity.Y, fdelta),
            LaneMap.FieldDirection => GateDirection(ref proj.direction, delta),
            _ => 0,
        };
    }

    /// <summary>
    /// 方向门：Pos → 确保 <c>refVal ≥ 0</c>；Neg → 确保 <c>refVal ≤ 0</c>。
    /// </summary>
    /// <returns>1 表示本次修改了值，0 表示值已经在正确方向。</returns>
    private static int GateFloat(ref float value, float sign)
    {
        if (sign > 0 && value < 0f)
        {
            value = -value; // 取反成正
            return 1;
        }

        if (sign < 0 && value > 0f)
        {
            value = -value; // 取反成负
            return 1;
        }

        return 0;
    }

    /// <summary>
    /// direction 是 int（-1 / 0 / +1），方向门语义比 velocity 简单：
    /// Pos → 强制 +1；Neg → 强制 -1。
    /// </summary>
    private static int GateDirection(ref int direction, byte delta)
    {
        int target = delta == 2 ? +1 : -1;
        if (direction == target) return 0;
        direction = target;
        return 1;
    }
}
