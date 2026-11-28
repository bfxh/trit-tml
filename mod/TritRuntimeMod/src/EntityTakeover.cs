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
/// <para>
/// <b>跳过 orig 的两条硬约束</b>（否则会冻结原版状态机）：
/// ① 写入必须真的改变字段 —— 三个字段都等于当前值时跳过 orig 只剩副作用，
/// 直接交回原版（史莱姆在地面时内核只重复输出当前朝向，就属于这一类）；
/// ② 连续接管最多 4 tick —— `NPC.AI` 里的 `ai[]` 计时器、帧动画、目标选择不会自己前进，
/// 定期跑一次原版把状态推进一步，同时给垂直模型一次真实观测。
/// </para>
/// <para>
/// <b>垂直通道靠「学」而不是「猜」</b>：重力加速、落地反弹、悬停阻尼都在 `NPC.AI` 内部，
/// 快照里既没有重力值也没有起跳冲量，写死常量就是假覆盖。这里的做法是在每一段
/// 「跑了 orig 的 tick」里观测 AI 前后的 <c>velocity.Y</c>，拟合一步仿射动力学
/// `vy_next = Alpha * vy + Beta`（常量重力是 Alpha=1 的特例；阻尼/阻力是 Beta≈0、
/// Alpha&lt;1 的特例；终端速度退化成 Alpha=1、Beta=0）。模型要先被后续观测确认
/// 才允许外推，出现冲量、碰撞、入水导致残差超限时自动交回原版并重新拟合。
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

    /// <summary>ActionPlan 尾部目标表复用缓冲（内核预测的 NPC 目标槽）。</summary>
    private readonly List<(byte Kind, int Slot, int TargetSlot)> actionPlanTargetBuffer = new(512);

    /// <summary>玩家表复用缓冲（STATE_PUSH 尾部）。</summary>
    private readonly List<PlayerSnapshot> playerBuffer = new(16);

    /// <summary>
    /// tick 开头（AI 之前）推送那一刻的 NPC 真值快照：target / type / 中心坐标。
    /// <para>内核的目标预测用的就是这批输入，对拍必须拿同一时刻的真值，
    /// 否则「预测在 tick 头、答案在 tick 尾」会凭空多出一批错样本。</para>
    /// </summary>
    private int[] npcTargetAtPush = Array.Empty<int>();
    private int[] npcTypeAtPush = Array.Empty<int>();
    private float[] npcCenterXAtPush = Array.Empty<float>();
    private float[] npcCenterYAtPush = Array.Empty<float>();

    /// <summary>tick 开头各玩家槽位的 aggro（影子对拍用）。</summary>
    private int[] playerAggroAtPush = Array.Empty<int>();

    /// <summary>按 NPC 类型分桶的目标对拍统计（影子对拍，只观测）。</summary>
    private readonly Dictionary<int, TargetTypeBucket> targetTypeBuckets = new(256);

    /// <summary>单个 NPC 类型的目标对拍计数。</summary>
    private struct TargetTypeBucket
    {
        public int Samples;
        public int EuclideanHits;
        public int ManhattanHits;
        public int AggroHits;
    }

    /// <summary>速度表复用字典（每 tick Clear 后重填，稳态零分配）。</summary>
    private readonly Dictionary<ulong, float> speedBuffer = new();
    private readonly Dictionary<ulong, List<(uint Key, byte Delta, float Confidence)>> bucketBuffer = new();
    private readonly Stack<List<(uint Key, byte Delta, float Confidence)>> bucketPool = new();

    /// <summary>接管日志的 tick 节流计数器（LogJobResults 开启时每 60 tick 最多一条）。</summary>
    private int takeoverLogCounter;

    /// <summary>本 tick 被 KernelFirst 跳过 orig 的实体数（钩子层计数）。</summary>
    public int KernelFirstTakeoverCount { get; private set; }

    /// <summary>本 tick 用学到的 Δvy 复现了垂直运动的实体数（<see cref="KernelFirstTakeoverCount"/> 的子集）。</summary>
    public int KernelFirstVerticalCount { get; private set; }

    /// <summary>本 tick 因为垂直模型未就绪 / 地面冲量而放弃 KernelFirst 的次数。</summary>
    public int KernelFirstVerticalFallbackCount { get; private set; }

    /// <summary>本 tick 被方向门接管的实体字段计数（AfterOrig 里 PatchAfterOrig fallback）。</summary>
    public int TakeoverCount { get; private set; }

    /// <summary>本 tick 内核输出的 intent 条数。</summary>
    public int PlanCount => lastActionPlan?.Count ?? 0;

    /// <summary>本 tick 对拍后回写的置信度样本数。</summary>
    public int RecordedCount { get; private set; }

    /// <summary>本 tick 对拍结果为一致的样本数（<see cref="RecordedCount"/> 的子集）。</summary>
    public int ConsistentCount { get; private set; }

    /// <summary>本 tick 目标预测对拍样本数（只统计「原版确实有目标」的 NPC）。</summary>
    public int TargetSampleCount { get; private set; }

    /// <summary>本 tick 目标预测命中的样本数（<see cref="TargetSampleCount"/> 的子集）。</summary>
    public int TargetHitCount { get; private set; }

    /// <summary>本 tick 影子对拍：按曼哈顿距离选玩家时的命中数。</summary>
    public int TargetManhattanHitCount { get; private set; }

    /// <summary>本 tick 影子对拍：按曼哈顿距离减 aggro 选玩家时的命中数。</summary>
    public int TargetAggroHitCount { get; private set; }

    /// <summary>本 tick 因实体被 KernelFirst 接管（没跑 orig、没有真值）而跳过的样本数。</summary>
    public int TargetSkippedTakenOver { get; private set; }

    /// <summary>本 tick 因目标玩家不在推送表 / 已死亡 / 幽灵而跳过的样本数。</summary>
    public int TargetSkippedInvalid { get; private set; }

    // 进程内累计（命令层 /trit takeover stats 的长期观测口径，不持久化）。
    private long totalPlans;
    private long totalRecorded;
    private long totalConsistent;
    private long totalTakeovers;
    private long totalKernelFirst;
    private long totalKernelFirstVertical;
    private long totalVerticalFallbacks;
    private long modelEvalSamples;
    private long modelEvalHits;
    private double modelErrorSum;
    private float modelErrorMax;
    private long totalTargetSamples;
    private long totalTargetHits;
    private long totalTargetManhattanHits;
    private long totalTargetAggroHits;

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
        KernelFirstVerticalCount = 0;
        KernelFirstVerticalFallbackCount = 0;

        if (settings.Takeover != TritSettings.TakeoverMode.ObserveOnly) return;
        if (autoPilot is null || !autoPilot.IsCalibrationTick()) return;

        // 校准与 KernelFirst 用同一个采样位置（AI 之前），这样引擎预测描述的是
        // "本 tick 待执行的变化"，tick 末尾才能和原版实际结果对齐。
        PushEntityStates();
        shim.GetActionPlanInto(actionPlanBuffer, targets: actionPlanTargetBuffer);
        lastActionPlan = actionPlanBuffer;
        calibrationPending = lastActionPlan.Count > 0;
    }

    /// <summary>接管层统计快照：本 tick + 进程内累计。</summary>
    public readonly struct TakeoverSnapshot
    {
        public TakeoverSnapshot(
            int tickPlans, int tickRecorded, int tickConsistent, int tickTakeovers, int tickKernelFirst,
            int tickVertical, int tickVerticalFallback,
            long totalPlans, long totalRecorded, long totalConsistent, long totalTakeovers, long totalKernelFirst,
            long totalVertical, long totalVerticalFallbacks,
            long modelSamples, long modelHits, double modelAverageError, float modelMaxError,
            int tickTargetSamples, int tickTargetHits, long totalTargetSamples, long totalTargetHits,
            int tickTargetManhattan, int tickTargetAggro,
            int tickSkippedTakenOver, int tickSkippedInvalid,
            long totalTargetManhattan, long totalTargetAggro)
        {
            TickPlans = tickPlans;
            TickRecorded = tickRecorded;
            TickConsistent = tickConsistent;
            TickTakeovers = tickTakeovers;
            TickKernelFirst = tickKernelFirst;
            TickVertical = tickVertical;
            TickVerticalFallback = tickVerticalFallback;
            TotalPlans = totalPlans;
            TotalRecorded = totalRecorded;
            TotalConsistent = totalConsistent;
            TotalTakeovers = totalTakeovers;
            TotalKernelFirst = totalKernelFirst;
            TotalVertical = totalVertical;
            TotalVerticalFallbacks = totalVerticalFallbacks;
            ModelEvalSamples = modelSamples;
            ModelEvalHits = modelHits;
            ModelAverageError = modelAverageError;
            ModelMaxError = modelMaxError;
            TickTargetSamples = tickTargetSamples;
            TickTargetHits = tickTargetHits;
            TotalTargetSamples = totalTargetSamples;
            TotalTargetHits = totalTargetHits;
            TickTargetManhattanHits = tickTargetManhattan;
            TickTargetAggroHits = tickTargetAggro;
            TickTargetSkippedTakenOver = tickSkippedTakenOver;
            TickTargetSkippedInvalid = tickSkippedInvalid;
            TotalTargetManhattanHits = totalTargetManhattan;
            TotalTargetAggroHits = totalTargetAggro;
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

        /// <summary>本 tick 走垂直复现的实体数（<see cref="TickKernelFirst"/> 的子集）。</summary>
        public int TickVertical { get; }

        /// <summary>本 tick 因垂直模型未就绪 / 地面冲量而放弃接管的次数。</summary>
        public int TickVerticalFallback { get; }

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

        /// <summary>累计走垂直复现的实体数。</summary>
        public long TotalVertical { get; }

        /// <summary>累计因垂直模型未就绪 / 地面冲量而放弃接管的次数。</summary>
        public long TotalVerticalFallbacks { get; }

        /// <summary>垂直模型的一步预测评估样本数（只在跑过 orig 的 tick 上采样）。</summary>
        public long ModelEvalSamples { get; }

        /// <summary>评估样本中「一步误差在容差内」的次数。</summary>
        public long ModelEvalHits { get; }

        /// <summary>垂直模型一步预测的平均绝对误差（像素/tick）。</summary>
        public double ModelAverageError { get; }

        /// <summary>垂直模型一步预测的最大绝对误差（像素/tick）。</summary>
        public float ModelMaxError { get; }

        /// <summary>本 tick 目标预测对拍样本数。</summary>
        public int TickTargetSamples { get; }

        /// <summary>本 tick 目标预测命中的样本数。</summary>
        public int TickTargetHits { get; }

        /// <summary>累计目标预测样本数。</summary>
        public long TotalTargetSamples { get; }

        /// <summary>累计目标预测命中数。</summary>
        public long TotalTargetHits { get; }

        /// <summary>目标预测命中率；没有样本时返回 0。</summary>
        public double TargetHitRate => TotalTargetSamples > 0 ? (double)TotalTargetHits / TotalTargetSamples : 0d;

        /// <summary>本 tick 影子对拍（曼哈顿距离）命中数。</summary>
        public int TickTargetManhattanHits { get; }

        /// <summary>本 tick 影子对拍（曼哈顿距离 − aggro）命中数。</summary>
        public int TickTargetAggroHits { get; }

        /// <summary>本 tick 因 KernelFirst 接管（无真值）跳过的样本数。</summary>
        public int TickTargetSkippedTakenOver { get; }

        /// <summary>本 tick 因目标无效（不在玩家里 / 死亡 / 幽灵）跳过的样本数。</summary>
        public int TickTargetSkippedInvalid { get; }

        /// <summary>累计影子对拍（曼哈顿距离）命中数。</summary>
        public long TotalTargetManhattanHits { get; }

        /// <summary>累计影子对拍（曼哈顿距离 − aggro）命中数。</summary>
        public long TotalTargetAggroHits { get; }

        /// <summary>曼哈顿口径的目标命中率；没有样本时返回 0。</summary>
        public double TargetManhattanHitRate =>
            TotalTargetSamples > 0 ? (double)TotalTargetManhattanHits / TotalTargetSamples : 0d;

        /// <summary>曼哈顿 − aggro 口径的目标命中率；没有样本时返回 0。</summary>
        public double TargetAggroHitRate =>
            TotalTargetSamples > 0 ? (double)TotalTargetAggroHits / TotalTargetSamples : 0d;

        /// <summary>累计一致率；没有样本时返回 0。</summary>
        public double ConsistencyRate => TotalRecorded > 0 ? (double)TotalConsistent / TotalRecorded : 0d;

        /// <summary>垂直模型命中率；没有评估样本时返回 0。</summary>
        public double ModelHitRate => ModelEvalSamples > 0 ? (double)ModelEvalHits / ModelEvalSamples : 0d;
    }

    /// <summary>记一次「垂直通道没能接管」（地面冲量 / 模型未就绪）。</summary>
    private void CountVerticalFallback()
    {
        KernelFirstVerticalFallbackCount++;
        totalVerticalFallbacks++;
    }

    /// <summary>取当前统计（命令层 / 日志用）。</summary>
    public TakeoverSnapshot Snapshot() => new(
        PlanCount, RecordedCount, ConsistentCount, TakeoverCount, KernelFirstTakeoverCount,
        KernelFirstVerticalCount, KernelFirstVerticalFallbackCount,
        totalPlans, totalRecorded, totalConsistent, totalTakeovers, totalKernelFirst,
        totalKernelFirstVertical, totalVerticalFallbacks,
        modelEvalSamples, modelEvalHits,
        modelEvalSamples > 0 ? modelErrorSum / modelEvalSamples : 0d, modelErrorMax,
        TargetSampleCount, TargetHitCount, totalTargetSamples, totalTargetHits,
        TargetManhattanHitCount, TargetAggroHitCount,
        TargetSkippedTakenOver, TargetSkippedInvalid,
        totalTargetManhattanHits, totalTargetAggroHits);

    /// <summary>
    /// 按 NPC 类型输出目标对拍明细（按「内核错失数」降序，最多 <paramref name="top"/> 行）。
    /// <para>用于区分「模型口径不对」与「这类 NPC 本来就不按最近玩家选目标」。</para>
    /// </summary>
    public string FormatTargetTypeReport(int top = 5)
    {
        if (targetTypeBuckets.Count == 0)
        {
            return "（暂无样本）";
        }

        var entries = new List<KeyValuePair<int, TargetTypeBucket>>(targetTypeBuckets);
        entries.Sort((left, right) =>
        {
            int leftMisses = left.Value.Samples - left.Value.EuclideanHits;
            int rightMisses = right.Value.Samples - right.Value.EuclideanHits;
            int byMisses = rightMisses.CompareTo(leftMisses);
            return byMisses != 0 ? byMisses : right.Value.Samples.CompareTo(left.Value.Samples);
        });

        var builder = new System.Text.StringBuilder();
        int written = 0;
        foreach (var entry in entries)
        {
            if (written >= top)
            {
                break;
            }

            TargetTypeBucket bucket = entry.Value;
            int misses = bucket.Samples - bucket.EuclideanHits;
            if (misses <= 0 && written > 0)
            {
                break;
            }

            if (written > 0)
            {
                builder.Append('\n');
            }

            builder.Append("    type=").Append(entry.Key)
                .Append(" 样本=").Append(bucket.Samples)
                .Append(" 内核=").Append(Percent(bucket.EuclideanHits, bucket.Samples))
                .Append(" 曼哈顿=").Append(Percent(bucket.ManhattanHits, bucket.Samples))
                .Append(" +aggro=").Append(Percent(bucket.AggroHits, bucket.Samples));
            written++;
        }

        return builder.Length == 0 ? "（暂无偏差样本）" : builder.ToString();
    }

    private static string Percent(int hits, int samples) =>
        samples > 0 ? ((double)hits / samples).ToString("P1") : "n/a";

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
        KernelFirstVerticalCount = 0;
        KernelFirstVerticalFallbackCount = 0;
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

        shim.GetActionPlanInto(actionPlanBuffer, actionPlanSpeedBuffer, actionPlanTargetBuffer);
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

        // 真值快照表按世界容量对齐（NPC / 玩家数组长度固定，实际只扩容一次）。
        EnsurePushTables(Main.npc.Length, Main.player.Length);

        NPC[] npcs = Main.npc;
        for (int i = 0; i < npcs.Length; i++)
        {
            NPC npc = npcs[i];
            if (npc is null || !npc.active)
            {
                continue;
            }

            bool hasTarget = npc.HasValidTarget;

            // 对拍真值：AI 之前的 target / type / 中心。内核此时拿到的也是同一份状态。
            int npcSlot = npc.whoAmI;
            npcTargetAtPush[npcSlot] = hasTarget ? npc.target : -1;
            npcTypeAtPush[npcSlot] = npc.type;
            npcCenterXAtPush[npcSlot] = npc.Center.X;
            npcCenterYAtPush[npcSlot] = npc.Center.Y;
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
                // 内核需要「实体中心」来挑最近的玩家（协议里 pos 与 target_dx 同语义）。
                PositionX = npc.Center.X,
                PositionY = npc.Center.Y,
                HasPosition = true,
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
                TileBelow = tileBelow,
                OnGround = onGround,
            });
        }

        // 玩家表（整批替换）：内核据此为带位置的 NPC 预测最近的玩家。
        List<PlayerSnapshot> players = playerBuffer;
        players.Clear();
        Player[] playerList = Main.player;
        for (int i = 0; i < playerList.Length; i++)
        {
            Player player = playerList[i];
            if (player is null || !player.active)
            {
                continue;
            }

            players.Add(new PlayerSnapshot
            {
                Slot = player.whoAmI,
                CenterX = player.Center.X,
                CenterY = player.Center.Y,
                Active = true,
                Dead = player.dead,
                Ghost = player.ghost,
                Immune = player.immune,
            });
            playerAggroAtPush[player.whoAmI] = player.aggro;
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
                PositionX = projectile.Center.X,
                PositionY = projectile.Center.Y,
                HasPosition = true,
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

        shim.PushEntityStates(states, players);
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

        // 预检：内核只能给方向，给不了幅度的通道必须有「自己复现物理」的依据，
        // 否则整只交回原版。必须在写任何字段之前判断，避免半接管（X 已改、Y 没改）。
        //
        // Y 轴：快照没有垂直速度大小、没有重力、没有起跳冲量，但 AI 每一步对 vy 的
        //   影响是可以观测的（见 ObserveNpcAiDelta）。空中且仿射模型（α·vy + β）
        //   已被确认时按模型外推；在地面要求跳/冲量、或模型还没收敛时，交回原版定幅度。
        // X 轴：速度表（ACTION_PLAN 尾部）只有「水平速度就是移动速度」的地面家族。
        //   该实体不在表里且 |vx| ≈ 0 时，KernelFirstVelocity 只能兜成 0.1 并跳过 orig，
        //   飞行家族会被卡在原地（原版本会逐 tick 加速）。此时同样交回原版。
        float speedMagnitude = SpeedOf(LaneMap.KindNpc, npc.whoAmI);
        bool onGround = npc.collideY;
        bool hasXIntent = false;
        byte xDelta = 0;
        bool hasDirectionIntent = false;
        byte directionDelta = 0;
        bool hasVerticalIntent = false;
        byte verticalDelta = 0;
        foreach (var (key, delta, _) in intents)
        {
            switch (LaneMap.FieldOf(key))
            {
                case LaneMap.FieldVelocityX:
                    hasXIntent = true;
                    xDelta = delta;
                    if (speedMagnitude <= 0f
                        && Math.Abs(npc.velocity.X) < StationaryVelocityEpsilon)
                    {
                        return false;
                    }
                    break;
                case LaneMap.FieldVelocityY:
                    hasVerticalIntent = true;
                    verticalDelta = delta;
                    break;
                case LaneMap.FieldDirection:
                    hasDirectionIntent = true;
                    directionDelta = delta;
                    break;
            }
        }

        // 连续接管到上限：强制跑一次 orig。跳过 orig 时 `NPC.AI` 的计时器、帧动画、
        // 目标选择都不推进，长期接管会让状态机停摆（史莱姆的跳跃计时器就是一例）。
        if (TakeoverStreakExhausted(npc)) return false;

        // 垂直通道：内核有没有 vy 指令，只决定要不要额外做符号交叉校验。
        // 真正的前提是「能不能把这一步的物理算出来」—— 有确认过的仿射模型就能复现
        // 原版对 vy 的作用（重力、悬停阻尼、阻力），否则一律交回原版。
        float emulatedVelocityY = 0f;
        bool emulateVertical = false;
        if (onGround)
        {
            // 地面：起跳/冲量必须由原版定幅度（Δvy 记不到）；落地残余速度也交给原版归零。
            if (hasVerticalIntent)
            {
                CountVerticalFallback();
                return false;
            }

            if (Math.Abs(npc.velocity.Y) >= GroundedVelocityLimit)
            {
                return false;
            }
        }
        else if (TryEmulateVertical(npc, hasVerticalIntent, verticalDelta, out emulatedVelocityY))
        {
            emulateVertical = true;
        }
        else
        {
            // 空中且模型还没确认：原版 AI 仍在改 vy（重力/阻尼/撞墙反弹），
            // 跳过 orig 会把它冻住。
            CountVerticalFallback();
            return false;
        }

        // 写入前先算好目标值：如果三个字段都等于当前值，跳过 orig 只会冻结状态机，
        // 没有任何接管收益，直接交回原版。
        float newVelocityX = hasXIntent
            ? KernelFirstVelocity(xDelta == 2 ? +1f : -1f, npc.velocity.X, speedMagnitude)
            : npc.velocity.X;
        int newDirection = hasDirectionIntent ? (directionDelta == 2 ? 1 : -1) : npc.direction;

        bool changes = newVelocityX != npc.velocity.X || newDirection != npc.direction;
        if (emulateVertical && emulatedVelocityY != npc.velocity.Y)
        {
            changes = true;
        }

        if (!changes) return false;

        if (hasXIntent)
        {
            // 有引擎速度大小时用它（史莱姆起跳 / 僵尸追击），否则保持当前绝对值。
            npc.velocity.X = newVelocityX;
        }

        if (hasDirectionIntent)
        {
            npc.direction = newDirection;
        }

        if (emulateVertical)
        {
            npc.velocity.Y = emulatedVelocityY;
        }

        MarkTakeover(npc, emulateVertical);
        KernelFirstTakeoverCount++;
        totalKernelFirst++;
        return true;
    }

    /// <summary>判定「静止」的 velocity 阈值（像素/tick）。</summary>
    private const float StationaryVelocityEpsilon = 0.01f;

    /// <summary>
    /// KernelFirst 水平速度：优先用引擎给出的速度大小；没有（未下发 / 该家族不提供）
    /// 时退回「保留当前绝对值」。
    /// <para>速度表缺失且当前静止的情形同样由调用方预检挡下，不会走到 0.1 兜底。</para>
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
    // 接管模型（连续接管上限 + 垂直一步仿射动力学）
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 单个 NPC 槽位的接管模型（按槽位下标索引，槽位复用给别的类型时重置）。
    /// </summary>
    private struct NpcTakeoverTrack
    {
        /// <summary>建模时的 NPC 类型；与当前类型不符即视为新实体。</summary>
        public int Type;

        /// <summary>垂直一步动力学：`vy_next = Alpha * vy + Beta`，由观测对拟合。</summary>
        public VerticalMotionModel Model;

        /// <summary>连续接管的 tick 数；到达上限时强制跑一次 orig。</summary>
        public int TakeoverStreak;
    }

    /// <summary>按 whoAmI 索引的接管模型表（容量跟 Main.npc 走，稳态零分配）。</summary>
    private NpcTakeoverTrack[] npcTracks = Array.Empty<NpcTakeoverTrack>();

    /// <summary>单 tick Δvy 的合理性上限；超过说明这一 tick 不是自由演化（冲量/碰撞/传送）。</summary>
    private const float VerticalDeltaSanity = 4f;

    /// <summary>
    /// 连续接管多少 tick 后强制跑一次 orig（所有通道共用）。
    /// <para>
    /// 只跳过 orig 的代价是 `NPC.AI` 里的状态机（`ai[]` 计时器、帧动画、目标选择、
    /// 声音/网络同步）也不推进。定期跑一次原版，把这类状态推进一步，同时给垂直模型
    /// 一次真实观测 —— 非线性钳制（终端速度等）本地学不到，4 tick 的误差上限也可接受。
    /// </para>
    /// </summary>
    private const int TakeoverStreakLimit = 4;

    /// <summary>落地后残留垂直速度的上限：超过就交回原版归零。</summary>
    private const float GroundedVelocityLimit = 0.5f;

    /// <summary>
    /// 钩子内 orig 之后调用：喂给垂直模型一对观测（<paramref name="velocityYBefore"/> →
    /// 当前 <c>velocity.Y</c>）。
    /// <para>
    /// 采样点严格夹在 `NPC.AI` 两侧（<paramref name="velocityYBefore"/> /
    /// <paramref name="onGroundBefore"/> 由调用方在 orig 之前取），所以差值只含 AI 的贡献 ——
    /// 重力加在被跳过的那段代码里、还是 `Main.UpdateNPC` 里，都不影响这个差值。
    /// </para>
    /// <para>
    /// 站在地面、或 Δvy 大到不像自由演化（冲量/碰撞/传送）时清空模型：
    /// 这些样本对仿射模型没有意义，留着只会让拟合偏掉。
    /// </para>
    /// </summary>
    public void ObserveNpcAiDelta(NPC npc, float velocityYBefore, bool onGroundBefore)
    {
        if (settings.Takeover != TritSettings.TakeoverMode.ReplaceOrig) return;
        if (npc is null || !npc.active) return;

        int slot = npc.whoAmI;
        if (slot < 0) return;
        EnsureTrackTable(slot);

        ref NpcTakeoverTrack track = ref npcTracks[slot];
        if (track.Type != npc.type)
        {
            track = default;
            track.Type = npc.type;
        }

        if (onGroundBefore)
        {
            // 地面样本是「重力 − 支撑」，不能进拟合；但仿射系数是同一实体的物理常数，
            // 跨落地保留 —— 下一次离地的第一 tick 就能直接复现，不必重新积累样本。
            track.TakeoverStreak = 0;
            return;
        }

        if (Math.Abs(npc.velocity.Y - velocityYBefore) > VerticalDeltaSanity)
        {
            // 空中的冲量/碰撞/传送：这一对样本不属于自由演化，清空重学。
            track.Model.Reset();
            track.TakeoverStreak = 0;
            return;
        }

        // 模型评估：这一 tick 跑了原版，等于拿到一个「真值」样本 —— 先在拟合前用旧模型
        // 预测一次，量出一步误差。这是收敛速度 / 命中率 / 回退率的采集口径。
        if (track.Model.CanPredict)
        {
            float predicted = track.Model.Predict(velocityYBefore);
            float error = Math.Abs(npc.velocity.Y - predicted);
            modelEvalSamples++;
            if (error <= 0.05f + 0.01f * Math.Abs(predicted))
            {
                modelEvalHits++;
            }

            modelErrorSum += error;
            if (error > modelErrorMax)
            {
                modelErrorMax = error;
            }
        }

        track.Model.Observe(velocityYBefore, npc.velocity.Y);
        track.TakeoverStreak = 0; // 这一 tick 跑过 orig，接管计数清零
    }

    /// <summary>
    /// 判断能否用学到的仿射模型复现原版这一步对 vy 的作用。
    /// <para>
    /// <paramref name="hasSign"/> 为 true 时（内核给了 vy 指令）额外要求模型外推和内核
    /// 预测的符号一致 —— 不一致说明模型已经跟不上（撞墙、入水、悬停阻尼变化），
    /// 交回原版并让观测把模型纠正回来。没有指令时只要求模型已确认。
    /// </para>
    /// </summary>
    private bool TryEmulateVertical(NPC npc, bool hasSign, byte delta, out float predicted)
    {
        predicted = 0f;
        int slot = npc.whoAmI;
        if (slot < 0 || slot >= npcTracks.Length) return false;

        ref NpcTakeoverTrack track = ref npcTracks[slot];
        if (track.Type != npc.type) return false;
        if (!track.Model.CanPredict) return false;

        predicted = track.Model.Predict(npc.velocity.Y);
        if (!hasSign) return true;

        float sign = delta == 2 ? +1f : -1f;
        if (sign > 0f ? predicted < 0f : predicted > 0f) return false;
        return true;
    }

    /// <summary>记一次接管（<paramref name="vertical"/> 标记是否走了垂直复现）。</summary>
    private void MarkTakeover(NPC npc, bool vertical)
    {
        int slot = npc.whoAmI;
        if (slot < 0 || slot >= npcTracks.Length) return;

        ref NpcTakeoverTrack track = ref npcTracks[slot];
        if (track.Type != npc.type)
        {
            // 还没观测过就接管（例如出生即在空中）：先建槽，接管计数从 1 开始。
            track = default;
            track.Type = npc.type;
        }

        track.TakeoverStreak++;
        if (vertical)
        {
            KernelFirstVerticalCount++;
            totalKernelFirstVertical++;
        }
    }

    /// <summary>连续接管已达上限？到点必须跑一次 orig，让被跳过的那段状态机前进。</summary>
    private bool TakeoverStreakExhausted(NPC npc)
    {
        int slot = npc.whoAmI;
        if (slot < 0 || slot >= npcTracks.Length) return false;

        ref NpcTakeoverTrack track = ref npcTracks[slot];
        return track.Type == npc.type && track.TakeoverStreak >= TakeoverStreakLimit;
    }

    /// <summary>确保接管模型表覆盖到这个槽位（只在容量不足时扩容）。</summary>
    private void EnsureTrackTable(int slot)
    {
        if (slot < npcTracks.Length) return;
        int size = Math.Max(Main.npc.Length, slot + 1);
        Array.Resize(ref npcTracks, size);
    }

    /// <summary>确保目标对拍真值表覆盖到当前世界容量（只扩容，不清旧值）。</summary>
    private void EnsurePushTables(int npcCapacity, int playerCapacity)
    {
        if (npcTargetAtPush.Length < npcCapacity)
        {
            Array.Resize(ref npcTargetAtPush, npcCapacity);
            Array.Resize(ref npcTypeAtPush, npcCapacity);
            Array.Resize(ref npcCenterXAtPush, npcCapacity);
            Array.Resize(ref npcCenterYAtPush, npcCapacity);
        }

        if (playerAggroAtPush.Length < playerCapacity)
        {
            Array.Resize(ref playerAggroAtPush, playerCapacity);
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // AfterOrig（tick 末尾，KernelFirst 钩子全部跑完后）
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 目标预测对拍（纯观测，不写 <c>npc.target</c>）。
    /// <para>
    /// 口径修正：
    /// <list type="bullet">
    /// <item>真值取 <b>推送那一刻</b>（AI 之前）的 target —— 与内核预测用的是同一份输入；</item>
    /// <item>被 KernelFirst 接管（这一 tick 没跑 orig）的实体没有真值，直接跳过；</item>
    /// <item>目标玩家不在推送表 / 已死亡 / 幽灵的样本跳过 —— 内核恒不选这类玩家。</item>
    /// </list>
    /// </para>
    /// <para>
    /// 影子对拍：同一份推送快照上并行算「曼哈顿距离」「曼哈顿距离 − aggro」两种候选，
    /// 分别累计命中率，用来区分「模型口径问题」和「统计口径问题」。
    /// 内核自身用的是欧氏平方，即 <see cref="TargetHitCount"/>。
    /// </para>
    /// </summary>
    private void CompareTargets()
    {
        TargetSampleCount = 0;
        TargetHitCount = 0;
        TargetManhattanHitCount = 0;
        TargetAggroHitCount = 0;
        TargetSkippedTakenOver = 0;
        TargetSkippedInvalid = 0;

        var targets = actionPlanTargetBuffer;
        if (targets.Count == 0)
        {
            return;
        }

        List<PlayerSnapshot> players = playerBuffer;
        if (players.Count == 0)
        {
            return;
        }

        NPC[] npcs = Main.npc;
        foreach (var (kind, slot, targetSlot) in targets)
        {
            if (kind != LaneMap.KindNpc) continue;
            if (slot < 0 || slot >= npcs.Length || slot >= npcTargetAtPush.Length) continue;

            NPC npc = npcs[slot];
            if (npc is null || !npc.active) continue;

            // 这一 tick 被 KernelFirst 跳过 orig：target 不会更新，没有可比较的真值。
            // TakeoverStreak 在跑 orig 时清零、接管成功时自增，因此 > 0 即「本 tick 接管过」。
            if (slot < npcTracks.Length
                && npcTracks[slot].Type == npc.type
                && npcTracks[slot].TakeoverStreak > 0)
            {
                TargetSkippedTakenOver++;
                continue;
            }

            // slot 被复用（type 变了）时旧快照不可信。
            int truth = npcTargetAtPush[slot];
            if (npcTypeAtPush[slot] != npc.type)
            {
                TargetSkippedInvalid++;
                continue;
            }

            PlayerSnapshot truthPlayer = default;
            bool found = false;
            for (int i = 0; i < players.Count; i++)
            {
                if (players[i].Slot == truth)
                {
                    truthPlayer = players[i];
                    found = true;
                    break;
                }
            }

            if (!found || !truthPlayer.Active || truthPlayer.Dead || truthPlayer.Ghost)
            {
                // 内核恒跳过这类玩家，没有可比较的答案（不是模型错）。
                TargetSkippedInvalid++;
                continue;
            }

            TargetSampleCount++;

            // 影子对拍：用与内核完全相同的推送快照（位置 + 玩家表），只换距离度量。
            float npcX = npcCenterXAtPush[slot];
            float npcY = npcCenterYAtPush[slot];
            int manhattanWinner = -1;
            int aggroWinner = -1;
            float bestManhattan = 0f;
            float bestAggro = 0f;
            for (int i = 0; i < players.Count; i++)
            {
                PlayerSnapshot player = players[i];
                if (!player.Active || player.Dead || player.Ghost)
                {
                    continue;
                }

                float dx = npcX - player.CenterX;
                float dy = npcY - player.CenterY;
                float manhattan = Math.Abs(dx) + Math.Abs(dy);
                int playerSlot = player.Slot;
                int aggro = playerSlot >= 0 && playerSlot < playerAggroAtPush.Length
                    ? playerAggroAtPush[playerSlot]
                    : 0;

                if (manhattanWinner < 0 || manhattan < bestManhattan)
                {
                    bestManhattan = manhattan;
                    manhattanWinner = playerSlot;
                }

                float adjusted = manhattan - aggro;
                if (aggroWinner < 0 || adjusted < bestAggro)
                {
                    bestAggro = adjusted;
                    aggroWinner = playerSlot;
                }
            }

            if (truth == targetSlot) TargetHitCount++;
            if (truth == manhattanWinner) TargetManhattanHitCount++;
            if (truth == aggroWinner) TargetAggroHitCount++;

            if (!targetTypeBuckets.TryGetValue(npc.type, out TargetTypeBucket bucket))
            {
                bucket = default;
            }

            bucket.Samples++;
            if (truth == targetSlot) bucket.EuclideanHits++;
            if (truth == manhattanWinner) bucket.ManhattanHits++;
            if (truth == aggroWinner) bucket.AggroHits++;
            targetTypeBuckets[npc.type] = bucket;
        }

        totalTargetSamples += TargetSampleCount;
        totalTargetHits += TargetHitCount;
        totalTargetManhattanHits += TargetManhattanHitCount;
        totalTargetAggroHits += TargetAggroHitCount;
    }

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
            shim.GetActionPlanInto(actionPlanBuffer, targets: actionPlanTargetBuffer);
        }

        var plan = actionPlanBuffer;
        lastActionPlan = plan;
        TakeoverCount = 0;
        RecordedCount = 0;
        ConsistentCount = 0;
        totalPlans += plan.Count;

        // 目标预测对拍：tick 开头内核按位置挑「最近玩家」，这里和 orig 跑完的
        // npc.target 对比。纯观测，不写 npc.target。
        CompareTargets();

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
                    $"[trit] KernelFirst：{plan.Count} plan → 跳过 orig {KernelFirstTakeoverCount}" +
                    $"（垂直 {KernelFirstVerticalCount} / 回退 {KernelFirstVerticalFallbackCount}）" +
                    $"→ fallback 方向门 {TakeoverCount} → 对拍回写 {RecordedCount}" +
                    $" → 目标 {TargetHitCount}/{TargetSampleCount}" +
                    $"（曼哈顿 {TargetManhattanHitCount}/{TargetSampleCount}，" +
                    $"跳过 接管 {TargetSkippedTakenOver}/无效 {TargetSkippedInvalid}）");
            }
            else if (!isKernelFirst && (TakeoverCount > 0 || RecordedCount > 0 || TargetSampleCount > 0))
            {
                TritRuntimeMod.Instance?.Logger?.Info(
                    $"[trit] PatchAfterOrig：{plan.Count} plan → 对拍 {RecordedCount} → 方向门 {TakeoverCount}" +
                    $" → 目标 {TargetHitCount}/{TargetSampleCount}" +
                    $"（曼哈顿 {TargetManhattanHitCount}/{TargetSampleCount}）");
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
