#nullable enable

using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using TritRuntime.Kernel;
using Terraria;

namespace TritRuntimeMod;

/// <summary>
/// 内核接管层 —— Phase 1.5/1.6 + Phase 2 MVP + KernelFirst 完整版。
/// </summary>
/// <remarks>
/// <b>模式与拦截点</b>：
/// <list type="bullet">
/// <item><c>ObserveOnly</c>（默认 / 桌面）：AfterOrig 第一行 return → 零开销</item>
/// <item><c>PatchAfterOrig</c>（Android 默认）：orig 先跑 → 对拍 → RecordOutcome → Apply
///   confidence ≥ 0.95 才 apply（方向门）</item>
/// <item><c>ReplaceOrig</c>（KernelFirst 完整版，Android only）：
///   <b>Per-NPC 粒度跳过 orig(self)</b> —— 只对 confidence ≥ 0.9999 的 NPC 跳过 orig，
///   直接写内核预测方向；其他实体正常走 PatchAfterOrig fallback</item>
/// </list>
/// <para>
/// KernelFirst 架构：tick 开头 PreUpdateEntities() 预拉 ActionPlan + 按 slot 分桶缓存；
/// On_NPC.AI 钩子内查缓存，confidence ≥ 0.9999 的 NPC 跳过 orig 直接写。
/// 对拍策略：KernelFirst 实体跳过（没有原版值可比）；PatchAfterOrig fallback 正常对拍 + RecordOutcome。
/// </para>
/// <para>
/// <b>弹幕刻意不做 KernelFirst</b>：NPC 的位置积分由 `Main.UpdateNPC` 在 `NPC.AI` 之外完成，
/// 跳过 `NPC.AI` 只丢决策；而 `Projectile.Update` 本身就是整段物理（位置积分、重力、
/// 碰撞、`timeLeft` 递减）。跳过它会让被接管的弹幕原地悬停、永不消失。
/// 因此弹幕只参与预测 / 对拍（PatchAfterOrig），不进入 KernelFirst 缓存。
/// </para>
/// </remarks>
internal sealed class EntityTakeover
{
    private readonly RuntimeShim shim;
    private readonly TritSettings settings;
    private readonly TakeoverAutoPilot? autoPilot;

    /// <summary>本 tick 的校准结果是否已经由 <see cref="PrepareTickPlan"/> 取好（L0 自动档）。</summary>
    private bool calibrationPending;

    /// <summary>最近一次 GetActionPlan 返回的 intent 列表（带 confidence）。</summary>
    private IReadOnlyList<(uint Key, byte Delta, float Confidence)>? lastActionPlan;

    /// <summary>
    /// KernelFirst 缓存：confidence ≥ 0.9999 的 intent 按 (kind, slot) 分桶。
    /// <para>key = ((ulong)kind << 32) | (uint)slot；value = 该 slot 下所有 KernelFirst intent</para>
    /// 构建时机：<see cref="PrepareKernelFirstCache"/>（tick 开头 PreUpdateEntities）。
    /// </summary>
    private Dictionary<ulong, List<(uint Key, byte Delta, float Confidence)>>? kernelFirstCache;

    /// <summary>
    /// KernelFirst 速度大小表：`(kind, slot)` → 引擎给出的绝对速度（`ACTION_PLAN` 尾部可选表）。
    /// <para>车道值只有三值（Neg/Zero/Pos），装不下速度大小；引擎把史莱姆起跳 / 僵尸追击速度
    /// 单独放在这里。旧 DLL 不下发时为 <see langword="null"/>，`TryApplyKernelFirst*` 退回「只改符号」。</para>
    /// </summary>
    private Dictionary<ulong, float>? speedByEntity;

    // ── 热路径复用缓冲 ───────────────────────────────────────────────────────
    // 目标：稳态 0 托管分配/tick。所有缓冲只在容量不足时扩容，之后一律 Clear 复用。
    private readonly List<EntitySnapshot> stateBuffer = new(512);
    private readonly List<(uint Key, bool Consistent)> outcomeBuffer = new(256);

    /// <summary>ActionPlan 复用缓冲：GetActionPlanInto 清空后重填，不再每 tick 新建数组。</summary>
    private readonly List<(uint Key, byte Delta, float Confidence)> actionPlanBuffer = new(8192);

    /// <summary>ActionPlan 尾部速度表复用缓冲。</summary>
    private readonly List<(byte Kind, int Slot, float Magnitude)> actionPlanSpeedBuffer = new(512);

    /// <summary>速度表复用字典（每 tick Clear 后重填，稳态零分配）。</summary>
    private readonly Dictionary<ulong, float> speedBuffer = new();
    private readonly Dictionary<ulong, List<(uint Key, byte Delta, float Confidence)>> bucketBuffer = new();
    private readonly Stack<List<(uint Key, byte Delta, float Confidence)>> bucketPool = new();

    /// <summary>接管日志的 tick 节流计数器（LogJobResults 开启时每 60 tick 最多一条）。</summary>
    private int takeoverLogCounter;

    /// <summary>本 tick 被 KernelFirst 跳过 orig 的实体数（钩子层计数）。</summary>
    public int KernelFirstTakeoverCount { get; private set; }

    /// <summary>本 tick 被方向门接管的实体字段计数（AfterOrig 里 PatchAfterOrig fallback）。</summary>
    public int TakeoverCount { get; private set; }

    /// <summary>本 tick 内核输出的 intent 条数。</summary>
    public int PlanCount => lastActionPlan?.Count ?? 0;

    /// <summary>本 tick 对拍后回写的置信度样本数。</summary>
    public int RecordedCount { get; private set; }

    /// <summary>本 tick 对拍结果为一致的样本数（<see cref="RecordedCount"/> 的子集）。</summary>
    public int ConsistentCount { get; private set; }

    // 进程内累计（命令层 /trit takeover stats 的长期观测口径，不持久化）。
    private long totalPlans;
    private long totalRecorded;
    private long totalConsistent;
    private long totalTakeovers;
    private long totalKernelFirst;

    /// <summary>
    /// ReplaceOrig KernelFirst 的置信度门槛。
    /// Rust TakeoverConfig.confidence_threshold 由 BuildRuntimeConfigJson 对齐到 0.9999。
    /// C# 侧在这个值之上再叠一层 KernelFirst 门 —— 只有 confidence ≥ 0.9999 的实体才真正跳过 orig。
    /// </summary>
    private const float ReplaceOrigConfidenceMin = 0.9999f;

    /// <summary>
    /// PatchAfterOrig 的 confidence 门槛 —— 和 Rust 默认值对齐（0.95）。
    /// Phase 2 KernelFirst 里：confidence ≥ 0.95 但 < 0.9999 的 intent 走 PatchAfterOrig fallback。
    /// </summary>
    private const float PatchAfterOrigConfidenceMin = 0.95f;

    public EntityTakeover(RuntimeShim shim, TritSettings settings, TakeoverAutoPilot? autoPilot = null)
    {
        this.shim = shim;
        this.settings = settings;
        this.autoPilot = autoPilot;
    }

    /// <summary>
    /// tick 开头预拉计划：ReplaceOrig 建 KernelFirst 缓存；L0 自动档按校准间隔取样。
    /// <para>由 TritWorldSystem.PreUpdateEntities 驱动，保证在实体 AI 之前完成。</para>
    /// </summary>
    public void PrepareTickPlan()
    {
        if (settings.Takeover == TritSettings.TakeoverMode.ReplaceOrig)
        {
            PrepareKernelFirstCache();
            return;
        }

        // 非 KernelFirst：plan 在 tick 末尾现取。这里清掉上一 tick 的缓存，
        // 否则 PatchAfterOrig 的 `?? GetActionPlan()` 会永远复用第一 tick 的 plan。
        lastActionPlan = null;
        calibrationPending = false;
        KernelFirstTakeoverCount = 0;

        if (settings.Takeover != TritSettings.TakeoverMode.ObserveOnly) return;
        if (autoPilot is null || !autoPilot.IsCalibrationTick()) return;

        // 校准与 KernelFirst 用同一个采样位置（AI 之前），这样引擎预测描述的是
        // "本 tick 待执行的变化"，tick 末尾才能和原版实际结果对齐。
        PushEntityStates();
        shim.GetActionPlanInto(actionPlanBuffer);
        lastActionPlan = actionPlanBuffer;
        calibrationPending = lastActionPlan.Count > 0;
    }

    /// <summary>接管层统计快照：本 tick + 进程内累计。</summary>
    public readonly struct TakeoverSnapshot
    {
        public TakeoverSnapshot(
            int tickPlans, int tickRecorded, int tickConsistent, int tickTakeovers, int tickKernelFirst,
            long totalPlans, long totalRecorded, long totalConsistent, long totalTakeovers, long totalKernelFirst)
        {
            TickPlans = tickPlans;
            TickRecorded = tickRecorded;
            TickConsistent = tickConsistent;
            TickTakeovers = tickTakeovers;
            TickKernelFirst = tickKernelFirst;
            TotalPlans = totalPlans;
            TotalRecorded = totalRecorded;
            TotalConsistent = totalConsistent;
            TotalTakeovers = totalTakeovers;
            TotalKernelFirst = totalKernelFirst;
        }

        /// <summary>本 tick 内核输出 intent 条数。</summary>
        public int TickPlans { get; }

        /// <summary>本 tick 对拍样本数。</summary>
        public int TickRecorded { get; }

        /// <summary>本 tick 一致的样本数。</summary>
        public int TickConsistent { get; }

        /// <summary>本 tick 方向门接管字段数。</summary>
        public int TickTakeovers { get; }

        /// <summary>本 tick 跳过 orig 的实体数。</summary>
        public int TickKernelFirst { get; }

        /// <summary>累计 plan 条数。</summary>
        public long TotalPlans { get; }

        /// <summary>累计对拍样本数。</summary>
        public long TotalRecorded { get; }

        /// <summary>累计一致的样本数。</summary>
        public long TotalConsistent { get; }

        /// <summary>累计方向门接管字段数。</summary>
        public long TotalTakeovers { get; }

        /// <summary>累计跳过 orig 的实体数。</summary>
        public long TotalKernelFirst { get; }

        /// <summary>累计一致率；没有样本时返回 0。</summary>
        public double ConsistencyRate => TotalRecorded > 0 ? (double)TotalConsistent / TotalRecorded : 0d;
    }

    /// <summary>取当前统计（命令层 / 日志用）。</summary>
    public TakeoverSnapshot Snapshot() => new(
        PlanCount, RecordedCount, ConsistentCount, TakeoverCount, KernelFirstTakeoverCount,
        totalPlans, totalRecorded, totalConsistent, totalTakeovers, totalKernelFirst);

    // ──────────────────────────────────────────────────────────────────────────
    // KernelFirst 缓存层（tick 开头 PreUpdateEntities 驱动）
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// tick 开头预拉 ActionPlan 并按 slot 分桶缓存，供 On_NPC.AI 钩子查询。
    /// <para>只缓存 NPC —— 弹幕跳过 <c>Projectile.Update</c> 会丢物理，见类型注释。</para>
    /// <para>由 TritWorldSystem.PreUpdateEntities() 驱动，保证在实体 AI 之前完成。</para>
    /// <para>ReplaceOrig 模式下每 tick 一次；非 ReplaceOrig 模式下空操作（return）。</para>
    /// </summary>
    public void PrepareKernelFirstCache()
    {
        KernelFirstTakeoverCount = 0;
        kernelFirstCache = null;
        lastActionPlan = null;
        speedByEntity = null;
        ReleaseBuckets();

        if (settings.Takeover != TritSettings.TakeoverMode.ReplaceOrig)
        {
            return; // 非 KernelFirst 模式 —— 钩子层全部走 orig 路径
        }

        // TritEnginePort：先把本 tick 的实体状态全量推给内核，
        // 后续 ActionPlan 才能给出原版等价的 delta（confidence 固定 1.0）。
        PushEntityStates();

        shim.GetActionPlanInto(actionPlanBuffer, actionPlanSpeedBuffer);
        var plan = actionPlanBuffer;
        lastActionPlan = plan;

        if (plan.Count == 0)
        {
            return; // Rust confidence 全不过 / prediction_interval_ticks 门控 / 托管回退
        }

        // 引擎速度大小（可选尾部表）：旧 DLL / 托管回退时为空，退回只改符号。
        if (actionPlanSpeedBuffer.Count > 0)
        {
            speedBuffer.Clear();
            foreach (var (kind, slot, magnitude) in actionPlanSpeedBuffer)
            {
                speedBuffer[((ulong)kind << 32) | (uint)slot] = magnitude;
            }

            speedByEntity = speedBuffer;
        }

        // 复用分桶表：所有 List 从 bucketPool 租，Clear 后再用。
        var buckets = bucketBuffer;
        foreach (var (key, delta, conf) in plan)
        {
            if (delta is 0 or > 2) continue;  // 内核弃权
            if (conf < ReplaceOrigConfidenceMin) continue;  // KernelFirst 门槛

            uint kind = LaneMap.KindOf(key);
            // 弹幕不接管：跳过 Projectile.Update 会丢掉位置积分 / 重力 / 碰撞 / timeLeft，
            // 弹幕会原地悬停。弹幕 intent 仍会在 AfterOrig 里对拍 + 方向门。
            if (kind != LaneMap.KindNpc) continue;

            int slot = LaneMap.SlotOf(key);
            ulong bucketKey = ((ulong)kind << 32) | (uint)slot;

            if (!buckets.TryGetValue(bucketKey, out var list))
            {
                list = bucketPool.Count > 0
                    ? bucketPool.Pop()
                    : new List<(uint, byte, float)>(3);  // 每个实体最多 3 个字段：velX/velY/dir
                buckets[bucketKey] = list;
            }
            list.Add((key, delta, conf));
        }

        if (buckets.Count > 0)
        {
            kernelFirstCache = buckets;
        }
    }

    /// <summary>把上一 tick 的分桶 List 归还池子，供下一 tick 复用。</summary>
    private void ReleaseBuckets()
    {
        if (bucketBuffer.Count == 0)
        {
            return;
        }

        foreach (var list in bucketBuffer.Values)
        {
            list.Clear();
            bucketPool.Push(list);
        }
        bucketBuffer.Clear();
    }

    /// <summary>
    /// 把当前世界所有 active NPC / Projectile 的状态推给内核（TritEnginePort 的输入）。
    /// <para>内核按 <c>whoAmI</c> 槽位建表、整批替换，所以每 tick 必须传全量。</para>
    /// <para>
    /// 引擎只关心方向与目标偏移（velocity / direction / target_dx,dy），这里推的是物理量原值，
    /// 由内核侧的 ported 逻辑自己算符号 —— C# 不再重复实现原版 AI。
    /// </para>
    /// </summary>
    private void PushEntityStates()
    {
        // 复用缓冲：shim.PushEntityStates 立即序列化，不会在调用后持有引用。
        List<EntitySnapshot> states = stateBuffer;
        states.Clear();

        NPC[] npcs = Main.npc;
        for (int i = 0; i < npcs.Length; i++)
        {
            NPC npc = npcs[i];
            if (npc is null || !npc.active)
            {
                continue;
            }

            bool hasTarget = npc.HasValidTarget;
            float targetDistance = 0f;
            float targetDx = 0f;
            float targetDy = 0f;
            if (hasTarget)
            {
                Player target = Main.player[npc.target];
                if (target is not null && target.active)
                {
                    targetDx = npc.Center.X - target.Center.X;
                    targetDy = npc.Center.Y - target.Center.Y;
                    targetDistance = (float)Math.Sqrt((targetDx * targetDx) + (targetDy * targetDy));
                }
            }

            // 站在地面：用上一 tick 的垂直碰撞结果（tick 开头、AI 之前采样时 collideY 仍是上次的值）。
            // 不能用 |velocity.Y| < 0.01 近似 —— 起跳/下落途中瞬时速度可能恰好很小，会把空中误判成地面。
            bool onGround = npc.collideY;
            // 脚下有方块：检测脚底往下 4px 的实心碰撞，用来区分「站在平台上」与「悬空边缘」。
            bool tileBelow = Collision.SolidCollision(
                new Vector2(npc.position.X, npc.Bottom.Y),
                npc.width,
                4);

            states.Add(new EntitySnapshot
            {
                Kind = EntityKinds.Npc,
                Slot = npc.whoAmI,
                Type = npc.type,
                VelocityX = npc.velocity.X,
                VelocityY = npc.velocity.Y,
                PositionX = npc.position.X,
                PositionY = npc.position.Y,
                Direction = npc.direction,
                Life = npc.life,
                LifeMax = npc.lifeMax,
                Target = hasTarget ? npc.target : -1,
                Ai0 = npc.ai.Length > 0 ? npc.ai[0] : 0f,
                Ai1 = npc.ai.Length > 1 ? npc.ai[1] : 0f,
                Ai2 = npc.ai.Length > 2 ? npc.ai[2] : 0f,
                Ai3 = npc.ai.Length > 3 ? npc.ai[3] : 0f,
                WalkSpeed = Math.Abs(npc.velocity.X),
                TargetDistance = targetDistance,
                TargetDx = targetDx,
                TargetDy = targetDy,
                TileBelow = onGround,
                OnGround = onGround,
            });
        }

        Projectile[] projectiles = Main.projectile;
        for (int i = 0; i < projectiles.Length; i++)
        {
            Projectile projectile = projectiles[i];
            if (projectile is null || !projectile.active)
            {
                continue;
            }

            states.Add(new EntitySnapshot
            {
                Kind = EntityKinds.Projectile,
                Slot = projectile.whoAmI,
                Type = projectile.type,
                VelocityX = projectile.velocity.X,
                VelocityY = projectile.velocity.Y,
                PositionX = projectile.position.X,
                PositionY = projectile.position.Y,
                Direction = projectile.direction,
                Life = projectile.timeLeft,
                // 弹幕没有追踪目标槽：显式 -1，避免默认 0 被当成「有目标」。
                Target = -1,
                Ai0 = projectile.ai.Length > 0 ? projectile.ai[0] : 0f,
                Ai1 = projectile.ai.Length > 1 ? projectile.ai[1] : 0f,
                // Projectile 记录里 on_ground 复用为 active
                OnGround = true,
            });
        }

        shim.PushEntityStates(states);
    }

    /// <summary>kernelFirstCache 当前有多少个实体（调试/日志用）。</summary>
    public int KernelFirstEntityCount => kernelFirstCache?.Count ?? 0;

    // ──────────────────────────────────────────────────────────────────────────
    // KernelFirst 应用（钩子层调用，返回是否成功接管）
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 尝试对这个 NPC 做 KernelFirst 接管 —— 查缓存、写字段。
    /// <para>返回 true 表示接管成功（钩子层应该跳过 orig(self)）；false 表示走正常 orig。</para>
    /// <para>条件：ReplaceOrig 模式 + 缓存里有该 slot + confidence ≥ 0.9999。</para>
    /// </summary>
    public bool TryApplyKernelFirstToNpc(NPC npc)
    {
        if (settings.Takeover != TritSettings.TakeoverMode.ReplaceOrig) return false;
        if (kernelFirstCache is null) return false;
        if (npc is null || !npc.active) return false;

        ulong bucketKey = ((ulong)LaneMap.KindNpc << 32) | (uint)npc.whoAmI;
        if (!kernelFirstCache.TryGetValue(bucketKey, out var intents)) return false;

        // 预检：内核预测「向上」，而当前 vertical velocity ≈ 0 —— 说明实体站在地面、
        // 内核要求起跳。上升冲量的大小不在 STATE_PUSH 快照里（ACTION_PLAN 只有方向
        // 通道），KernelFirstSign 会把 |vy| 兜成 0.1，史莱姆的跳跃会退化成原地抖动。
        // 这一 tick 整只放弃接管、让原版定冲量；空中下一 tick 再按当前速度接管。
        // 必须在写任何字段之前判断，避免半接管（X 已改、Y 没改）。
        foreach (var (key, delta, _) in intents)
        {
            if (delta == 1
                && LaneMap.FieldOf(key) == LaneMap.FieldVelocityY
                && Math.Abs(npc.velocity.Y) < GroundJumpVelocityEpsilon)
            {
                return false;
            }
        }

        bool applied = false;
        foreach (var (key, delta, _) in intents)
        {
            uint field = LaneMap.FieldOf(key);
            float fdelta = delta == 2 ? +1f : -1f;

            switch (field)
            {
                case LaneMap.FieldVelocityX:
                    // 有引擎速度大小时用它（史莱姆起跳 / 僵尸追击），否则保持当前绝对值。
                    npc.velocity.X = KernelFirstVelocity(
                        fdelta,
                        npc.velocity.X,
                        SpeedOf(LaneMap.KindNpc, npc.whoAmI));
                    applied = true;
                    break;
                case LaneMap.FieldVelocityY:
                    npc.velocity.Y = KernelFirstSign(fdelta, npc.velocity.Y);
                    applied = true;
                    break;
                case LaneMap.FieldDirection:
                    npc.direction = delta == 2 ? 1 : -1;
                    applied = true;
                    break;
            }
        }

        if (applied)
        {
            KernelFirstTakeoverCount++;
            totalKernelFirst++;
        }

        return applied;
    }

    /// <summary>判定「站在地面」的 vertical velocity 阈值（像素/tick）。</summary>
    private const float GroundJumpVelocityEpsilon = 0.01f;

    /// <summary>
    /// KernelFirst 直接写字段：取内核预测方向的绝对值，确保非零，再乘方向符号。
    /// velocity=0 时内核预测方向本身，给个微小初速度让实体"看起来动起来"。
    /// </summary>
    private static float KernelFirstSign(float sign, float currentValue)
    {
        float abs = currentValue < 0f ? -currentValue : currentValue;
        if (abs < 0.01f) abs = 0.1f;  // 零速度时给个微小初速度
        return sign * abs;
    }

    /// <summary>
    /// KernelFirst 水平速度：优先用引擎给出的速度大小；没有（未下发 / 该家族不提供）
    /// 时退回「保留当前绝对值」。
    /// </summary>
    private static float KernelFirstVelocity(float sign, float currentValue, float magnitude)
    {
        float abs = magnitude > 0.001f ? magnitude : (currentValue < 0f ? -currentValue : currentValue);
        if (abs < 0.01f) abs = 0.1f;
        return sign * abs;
    }

    /// <summary>查实体级引擎速度大小；没有速度表 / 该实体不在表里返回 0。</summary>
    private float SpeedOf(uint kind, int slot)
    {
        if (speedByEntity is null)
        {
            return 0f;
        }

        return speedByEntity.TryGetValue(((ulong)kind << 32) | (uint)slot, out float magnitude)
            ? magnitude
            : 0f;
    }

    // ──────────────────────────────────────────────────────────────────────────
    // AfterOrig（tick 末尾，KernelFirst 钩子全部跑完后）
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 在 TritRuntimeMod.OnWorldTick 末尾所有原版 AI 跑完后调用。
    /// <para>
    /// KernelFirst 完整版对拍策略：
    /// <list type="bullet">
    /// <item><b>confidence ≥ 0.9999</b>（KernelFirst 已接管）→ 跳过 orig 没法对比，不对拍，RecordOutcome 不写</item>
    /// <item><b>confidence ≥ 0.95 但 < 0.9999</b>（PatchAfterOrig fallback）→ 正常对拍 + RecordOutcome + Apply（方向门）</item>
    /// </list>
    /// 非 ReplaceOrig 模式（PatchAfterOrig / ObserveOnly）→ 保持原有全部对拍 + RecordOutcome 行为。
    /// </para>
    /// </summary>
    public void AfterOrig()
    {
        // L0：不做接管，只在自动档轮到校准的 tick 做一次"只对拍、不应用"的取样。
        if (settings.Takeover == TritSettings.TakeoverMode.ObserveOnly)
        {
            RunCalibration();
            return;
        }

        // KernelFirst 模式下 PrepareKernelFirstCache 已经拉过 plan 了（tick 开头），复用；
        // PatchAfterOrig 模式下必须每 tick 现拉（不能用上一 tick 的陈旧 plan）。
        bool isKernelFirst = settings.Takeover == TritSettings.TakeoverMode.ReplaceOrig;
        if (!(isKernelFirst && lastActionPlan is not null))
        {
            shim.GetActionPlanInto(actionPlanBuffer);
        }

        var plan = actionPlanBuffer;
        lastActionPlan = plan;
        TakeoverCount = 0;
        RecordedCount = 0;
        ConsistentCount = 0;
        totalPlans += plan.Count;

        if (plan.Count == 0)
        {
            return;
        }

        float confMin = PatchAfterOrigConfidenceMin;

        // ── 只对 PatchAfterOrig fallback intent 做对拍 + RecordOutcome ──
        // KernelFirst 实体（confidence >= 0.9999）跳过 orig 没有原版值可对比，不对拍
        // 复用缓冲：RecordOutcome 立即序列化，不会在调用后持有引用。
        var records = outcomeBuffer;
        records.Clear();

        foreach (var (key, delta, confidence) in plan)
        {
            if (delta is 0 or > 2) continue;  // 内核弃权

            if (isKernelFirst && confidence >= ReplaceOrigConfidenceMin)
            {
                // KernelFirst 已接管：不对拍，不 RecordOutcome
                continue;
            }

            if (confidence < confMin) continue;  // PatchAfterOrig 门槛都没过

            uint kind = LaneMap.KindOf(key);
            int slot = LaneMap.SlotOf(key);
            uint field = LaneMap.FieldOf(key);

            // 对拍：原版值方向 vs 内核预测方向（PatchAfterOrig 路径）
            bool consistent = kind switch
            {
                LaneMap.KindNpc => CheckNpcConsistency(slot, field, delta),
                LaneMap.KindProjectile => CheckProjectileConsistency(slot, field, delta),
                _ => false,
            };
            records.Add((key, consistent));
            if (consistent)
            {
                ConsistentCount++;
            }

            // 方向门 apply（PatchAfterOrig 路径）
            int applied = kind switch
            {
                LaneMap.KindNpc => ApplyToNpc(slot, field, delta),
                LaneMap.KindProjectile => ApplyToProjectile(slot, field, delta),
                _ => 0,
            };
            TakeoverCount += applied;
        }

        // ── 批量回写 fork 置信度（只有 PatchAfterOrig fallback 实体） ──
        if (records.Count > 0)
        {
            shim.RecordOutcome(records);
            RecordedCount = records.Count;
            totalRecorded += records.Count;
            totalConsistent += ConsistentCount;
        }

        totalTakeovers += TakeoverCount;

        // 每 60 tick 最多一条：接管热路径不能逐 tick 做字符串插值 + 写盘。
        if (settings.LogJobResults && (++takeoverLogCounter % 60) == 0)
        {
            if (isKernelFirst && (KernelFirstTakeoverCount > 0 || TakeoverCount > 0))
            {
                TritRuntimeMod.Instance?.Logger?.Info(
                    $"[trit] KernelFirst：{plan.Count} plan → 跳过 orig {KernelFirstTakeoverCount} " +
                    $"→ fallback 方向门 {TakeoverCount} → 对拍回写 {RecordedCount}");
            }
            else if (!isKernelFirst && (TakeoverCount > 0 || RecordedCount > 0))
            {
                TritRuntimeMod.Instance?.Logger?.Info(
                    $"[trit] PatchAfterOrig：{plan.Count} plan → 对拍 {RecordedCount} → 方向门 {TakeoverCount}");
            }
        }

        // ── 自动降级：只看自动升级出来的 L1，命中就在本 tick 回落 ObserveOnly ──
        // 下一 tick 起 AfterOrig 会走 L0 校准分支，接管层不再写任何字段。
        autoPilot?.RecordTakeoverTick(RecordedCount, ConsistentCount);
    }

    /// <summary>
    /// L0 校准：<see cref="PrepareTickPlan"/> 已经取到 plan，这里只做对拍与置信度回写，
    /// <b>不写任何实体字段</b>。样本同时交给自动档累计升级窗口。
    /// </summary>
    private void RunCalibration()
    {
        TakeoverCount = 0;
        RecordedCount = 0;
        ConsistentCount = 0;

        if (!calibrationPending || lastActionPlan is null)
        {
            return;
        }

        calibrationPending = false;
        var plan = lastActionPlan;
        totalPlans += plan.Count;

        int recorded = 0;
        int consistent = 0;
        var records = outcomeBuffer;
        records.Clear();

        foreach (var (key, delta, confidence) in plan)
        {
            if (delta is 0 or > 2) continue;
            if (confidence < PatchAfterOrigConfidenceMin) continue;

            int slot = LaneMap.SlotOf(key);
            uint field = LaneMap.FieldOf(key);

            bool ok = LaneMap.KindOf(key) switch
            {
                LaneMap.KindNpc => CheckNpcConsistency(slot, field, delta),
                LaneMap.KindProjectile => CheckProjectileConsistency(slot, field, delta),
                _ => false,
            };

            records.Add((key, ok));
            recorded++;
            if (ok)
            {
                consistent++;
            }
        }

        if (records.Count == 0)
        {
            return;
        }

        // 回写 fork 置信度：升级后 L1 直接复用这份置信度，不需要从零重新收敛。
        shim.RecordOutcome(records);
        RecordedCount = recorded;
        ConsistentCount = consistent;
        totalRecorded += recorded;
        totalConsistent += consistent;
        autoPilot?.RecordCalibration(recorded, consistent);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 对拍（原版值方向 vs 内核 delta）
    // ──────────────────────────────────────────────────────────────────────────

    private static bool CheckNpcConsistency(int slot, uint field, byte delta)
    {
        if (slot < 0 || slot >= Main.npc.Length) return false;
        var npc = Main.npc[slot];
        if (npc is null || !npc.active) return false;

        float sign = delta == 2 ? +1f : -1f;
        return field switch
        {
            LaneMap.FieldVelocityX => IsConsistent(npc.velocity.X, sign),
            LaneMap.FieldVelocityY => IsConsistent(npc.velocity.Y, sign),
            LaneMap.FieldDirection => IsConsistentDir(npc.direction, sign),
            _ => false,
        };
    }

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

    private static bool IsConsistent(float value, float sign)
    {
        if (sign > 0) return value >= 0f;
        if (sign < 0) return value <= 0f;
        return true;
    }

    private static bool IsConsistentDir(int direction, float sign)
    {
        if (sign > 0) return direction > 0;
        if (sign < 0) return direction < 0;
        return true;
    }

    // ──────────────────────────────────────────────────────────────────────────
    // PatchAfterOrig 方向门 Apply（与 KernelFirst 直接写字段不同）
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
