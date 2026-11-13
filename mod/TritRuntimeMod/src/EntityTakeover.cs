#nullable enable

using System.Collections.Generic;
using TritRuntime.Kernel;
using Terraria;

namespace TritRuntimeMod;

/// <summary>
/// 内核接管层（L1 PatchAfterOrig + 置信度长期跟踪）。
/// </summary>
/// <remarks>
/// Phase 1 → Phase 1.5 → Phase 1.6（当前）：
/// <list type="number">
/// <item>Phase 1 MVP：链路验证 — predictions + 零开销降级</item>
/// <item>Phase 1.5：方向门接管 — 把内核预测方向应用到 NPC/Projectile velocity/direction</item>
/// <item><b>Phase 1.6（本文件）</b>：置信度长期跟踪 — 原版值方向 vs 内核预测方向对拍 + 批量回写</item>
/// </list>
/// <para>
/// 对拍语义：
/// </para>
/// <para>
/// 每条 prediction 包含 (key, delta)，delta=2(Pos) 表示内核认为该字段"应该朝正方向"，
/// delta=1(Neg) 表示"应该朝负方向"。对拍在 <b>原版 AI 刚跑完、接管还没发生时</b>
/// 读取原版当前值，检查它的方向是否和 delta 一致。一致 = confidence++，
/// 不一致 = divergence++。fork.predict() 用这些数据决定下一轮该 key 是 Pos/Neg/Unknown。
/// </para>
/// <para>
/// <b>时序约束</b>：对拍必须在方向门 Apply 之前做 —— 否则原版值被我们改了，
/// 对拍结果永远是"一致"（方向门自己改的），置信度虚高。
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

    /// <summary>本 tick 对拍后回写的置信度样本数。</summary>
    public int RecordedCount { get; private set; }

    public EntityTakeover(RuntimeShim shim, TritSettings settings)
    {
        this.shim = shim;
        this.settings = settings;
    }

    /// <summary>
    /// 在 <see cref="TritRuntimeMod.OnWorldTick"/> 里全部原版 AI 跑完后调用。
    /// </summary>
    /// <remarks>
    /// <b>顺序</b>：先 predictions → 先对拍（原版值还没被我们改）→ 批量 RecordOutcome → 再 Apply 方向门。
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
        RecordedCount = 0;

        if (preds.Count == 0)
        {
            return; // Rust confidence 全不过 / prediction_interval_ticks 门控 / 托管回退
        }

        // ── Phase 1.6：原版值方向 vs 内核预测方向对拍 ──
        // 必须在 ApplyToNpc/Projectile 之前做 —— Apply 之后值被我们改了会让对拍结果虚高
        var records = new List<(uint Key, bool Consistent)>(preds.Count);
        foreach (var (key, delta) in preds)
        {
            if (delta is 0 or > 2) continue; // 内核弃权的 key 不记对拍

            uint kind = LaneMap.KindOf(key);
            int slot = LaneMap.SlotOf(key);
            uint field = LaneMap.FieldOf(key);

            bool consistent = kind switch
            {
                LaneMap.KindNpc => CheckNpcConsistency(slot, field, delta),
                LaneMap.KindProjectile => CheckProjectileConsistency(slot, field, delta),
                _ => false, // Player 不接管，也不记对拍
            };
            // consistent=false 也照样回写 —— 这正是 fork 需要用来算置信度的数据
            records.Add((key, consistent));
        }

        if (records.Count > 0)
        {
            // 托管回退 → 空 Submits，RuntimeShim.RecordOutcome 返回 true 统一成 OK
            shim.RecordOutcome(records);
            RecordedCount = records.Count;
        }

        // ── Phase 1.5：方向门接管（原版值已经读过，可以放心改了） ──
        foreach (var (key, delta) in preds)
        {
            if (delta is 0 or > 2) continue;

            uint kind = LaneMap.KindOf(key);
            int slot = LaneMap.SlotOf(key);
            uint field = LaneMap.FieldOf(key);

            int applied = kind switch
            {
                LaneMap.KindNpc => ApplyToNpc(slot, field, delta),
                LaneMap.KindProjectile => ApplyToProjectile(slot, field, delta),
                _ => 0,
            };

            TakeoverCount += applied;
        }

        if (settings.LogJobResults && (TakeoverCount > 0 || RecordedCount > 0))
        {
            TritRuntimeMod.Instance?.Logger?.Info(
                $"[trit] 接管层：{preds.Count} 预测 → 对拍 {RecordedCount} 条 → 方向门 {TakeoverCount} 条");
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 对拍（原版值方向 vs 内核预测方向）
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>检查单个 NPC 某个字段的原版方向是否与内核 delta 一致。</summary>
    private static bool CheckNpcConsistency(int slot, uint field, byte delta)
    {
        if (slot < 0 || slot >= Main.npc.Length) return false;
        var npc = Main.npc[slot];
        if (npc is null || !npc.active) return false;

        float sign = delta == 2 ? +1f : -1f; // Pos→+1, Neg→-1

        return field switch
        {
            LaneMap.FieldVelocityX => IsConsistent(npc.velocity.X, sign),
            LaneMap.FieldVelocityY => IsConsistent(npc.velocity.Y, sign),
            LaneMap.FieldDirection => IsConsistentDir(npc.direction, sign),
            _ => false, // 不接管的字段也不记对拍（省对拍开销 + 省置信度历史噪声）
        };
    }

    /// <summary>检查单个 Projectile 某个字段的原版方向是否与内核 delta 一致。</summary>
    private static bool CheckProjectileConsistency(int slot, uint field, byte delta)
    {
        if (slot < 0 || slot >= Main.projectile.Length) return false;
        var proj = Main.projectile[slot];
        if (proj is null || !proj.active) return false;

        float sign = delta == 2 ? +1f : -1f;

        return field switch
        {
            LaneMap.FieldVelocityX => IsConsistent(proj.velocity.X, sign),
            LaneMap.FieldVelocityY => IsConsistent(proj.velocity.Y, sign),
            LaneMap.FieldDirection => IsConsistentDir(proj.direction, sign),
            _ => false,
        };
    }

    /// <summary>Pos 期望 ≥ 0，Neg 期望 ≤ 0。</summary>
    private static bool IsConsistent(float value, float sign)
    {
        if (sign > 0) return value >= 0f;
        if (sign < 0) return value <= 0f;
        return true; // sign == 0（理论上不会进来）
    }

    /// <summary>direction 是 int -1/0/+1：Pos 期望 > 0（即 +1），Neg 期望 < 0（即 -1）。</summary>
    private static bool IsConsistentDir(int direction, float sign)
    {
        if (sign > 0) return direction > 0;
        if (sign < 0) return direction < 0;
        return true;
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 方向门接管（Phase 1.5，不变）
    // ──────────────────────────────────────────────────────────────────────────

    private static int ApplyToNpc(int slot, uint field, byte delta)
    {
        if (slot < 0 || slot >= Main.npc.Length) return 0;
        var npc = Main.npc[slot];
        if (npc is null || !npc.active) return 0;

        float fdelta = delta == 2 ? +1f : -1f;

        return field switch
        {
            LaneMap.FieldVelocityX => GateFloat(ref npc.velocity.X, fdelta),
            LaneMap.FieldVelocityY => GateFloat(ref npc.velocity.Y, fdelta),
            LaneMap.FieldDirection => GateDirection(ref npc.direction, delta),
            _ => 0,
        };
    }

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

    private static int GateFloat(ref float value, float sign)
    {
        if (sign > 0 && value < 0f) { value = -value; return 1; }
        if (sign < 0 && value > 0f) { value = -value; return 1; }
        return 0;
    }

    private static int GateDirection(ref int direction, byte delta)
    {
        int target = delta == 2 ? +1 : -1;
        if (direction == target) return 0;
        direction = target;
        return 1;
    }
}
