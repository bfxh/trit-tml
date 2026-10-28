using System;
using System.Collections.Generic;
using System.Diagnostics;
using Terraria;
using TritRuntime.Kernel;
using TritRuntime.Wire;

namespace TritRuntimeMod;

/// <summary>
/// 世界投影桥：把每 tick 的游戏状态变化压成三进制车道，提交给运行时内核。
/// </summary>
/// <remarks>
/// <para>
/// 硬约束（来自架构设计）：本类只 <b>读</b> 游戏状态，绝不回写；内核只收到
/// <c>uint</c> 车道键与 <see cref="TritDelta"/>，永远看不到 <c>NPC</c>/<c>Projectile</c> 对象。
/// 所有调用都发生在 C# 主线程（Terraria.On_* 钩子本身就在主线程）。
/// </para>
/// <para>
/// 一个 tick 的流水线：
/// <list type="number">
/// <item>采样（有界）→ 与上一 tick 比较 → 得到非零三值增量；</item>
/// <item><c>BeginTransaction</c> → 逐条 <c>SubmitIntent</c> → <c>Commit</c>（跨事务仲裁 + 只追加日志）；</item>
/// <item><c>Publish</c> 事件 → <c>Step(tick)</c> 走依赖图派发；</item>
/// <item>按间隔 <c>Snapshot</c> / <c>ShadowRecord</c> / 读 <c>Stats</c>。</item>
/// </list>
/// </para>
/// </remarks>
internal sealed class WorldBridge
{
    /// <summary>事件号：每 tick 一次的心跳。</summary>
    public const uint EventTick = 1;

    /// <summary>事件号：NPC 活动集合发生变化。</summary>
    public const uint EventNpcActivity = 2;

    /// <summary>事件号：弹幕活动集合发生变化。</summary>
    public const uint EventProjectileActivity = 3;

    /// <summary>事件号：玩家状态集合发生变化。</summary>
    public const uint EventPlayerActivity = 4;

    private const uint ModIdSeed = 0x811C9DC5u; // FNV-1a 偏移基数

    /// <summary>同类周期性日志的最小间隔（秒）。</summary>
    private const double MinLogIntervalSeconds = 15.0;

    /// <summary><c>SnapshotBytes</c> 返回值里的快照 ID 前缀长度。</summary>
    private const int SnapshotIdPrefix = 8;

    private readonly TritRuntimeMod mod;
    private readonly TritSettings settings;
    private readonly RuntimeShim shim;

    private readonly EntityState[] npcState;
    private readonly EntityState[] projectileState;
    private readonly EntityState[] playerState;

    private readonly List<uint> changedKeys = new(4096);
    private readonly List<TritDelta> changedTrits = new(4096);

    private readonly uint modId;
    private readonly ulong tickSub;
    private readonly ulong npcSub;
    private readonly ulong projectileSub;
    private readonly ulong playerSub;

    private long tick;
    private int prevActiveNpc = -1;
    private int prevActiveProjectile = -1;
    private int prevActivePlayer = -1;

    // Main.Update 每帧调用一次，而菜单/加载界面没有固定步长（实测可达数千 FPS）。
    // 用 Main.GameUpdateCount（游戏逻辑 tick，固定 60/s）做驱动节流，
    // 否则会把「帧数」当成「游戏 tick」，快照与统计每秒刷几十次。
    private uint lastGameTick;
    private bool lastGameTickValid;

    // 周期性日志按墙上时钟节流：游戏内 60 tick/s，按 tick 数打日志会在高频场景下刷爆日志
    // （实测 4 分钟写出 13 MB / 8.9 万行）。
    private readonly Stopwatch uptime = Stopwatch.StartNew();
    private double lastSnapshotLogAt = double.NegativeInfinity;
    private double lastStatsLogAt = double.NegativeInfinity;
    private double lastStaticTickLogAt = double.NegativeInfinity;
    private double lastCaptureLogAt = double.NegativeInfinity;
    private double lastStatusLogAt = double.NegativeInfinity;
    private bool warnedStaticGameTick;

    private bool disposed;

    public WorldBridge(TritRuntimeMod mod, TritSettings settings, RuntimeShim shim)
    {
        this.mod = mod;
        this.settings = settings;
        this.shim = shim;

        npcState = new EntityState[Math.Max(1, settings.MaxNpcs)];
        projectileState = new EntityState[Math.Max(1, settings.MaxProjectiles)];
        playerState = new EntityState[Math.Max(1, settings.MaxPlayers)];

        modId = StableModId(mod.Name);

        // 订阅自己的事件：Step() 会按依赖图与优先级返回本 tick 需要派发的动作意图。
        // 这是"事件路由/批处理"能力的自证——单订阅者场景下它必须每次命中。
        tickSub = shim.Subscribe(modId, EventTick, priority: 0, Array.Empty<uint>());
        npcSub = shim.Subscribe(modId, EventNpcActivity, priority: 0, Array.Empty<uint>());
        projectileSub = shim.Subscribe(modId, EventProjectileActivity, priority: 0, Array.Empty<uint>());
        playerSub = shim.Subscribe(modId, EventPlayerActivity, priority: 0, Array.Empty<uint>());
    }

    /// <summary>最近一次成功提交落地的车道数。</summary>
    public uint LastApplied { get; private set; }

    /// <summary>最近一次提交拿到的事务日志序号。</summary>
    public ulong LastLogSeq { get; private set; }

    /// <summary>已建立的快照数。</summary>
    public int SnapshotCount { get; private set; }

    /// <summary>最近一次 <c>Step</c> 返回的动作意图数。</summary>
    public int LastIntentCount { get; private set; }

    /// <summary>提交冲突次数（仲裁不通过）。</summary>
    public int ConflictCount { get; private set; }

    /// <summary>本 tick 真正跑了 AI 的 NPC 数（由钩子统计）。</summary>
    public int TickedNpcCount => npcTickedCount;

    /// <summary>本 tick 真正跑了 Update 的弹幕数（由钩子统计）。</summary>
    public int TickedProjectileCount => projectileTickedCount;

    private int npcTickedCount;
    private int projectileTickedCount;

    private readonly int[] netCounts = new int[256];
    private int netTotal;

    /// <summary>累计观测到的网络包数（由 <c>On_NetMessage.SendData</c> 钩子统计）。</summary>
    public int NetMessageCount => netTotal;

    /// <summary>钩子观测到一个出站网络包。</summary>
    public void NoteNetMessage(int msgType)
    {
        netTotal++;
        uint index = (uint)msgType;
        if (index < (uint)netCounts.Length)
        {
            netCounts[index]++;
        }
    }

    /// <summary>取某个消息类型的累计计数。</summary>
    public int NetCountOf(int msgType)
    {
        uint index = (uint)msgType;
        return index < (uint)netCounts.Length ? netCounts[index] : 0;
    }

    /// <summary>
    /// 取当前世界投影的三值序列（每元素一个 trit：0=Neg、1=Zero、2=Pos）。
    /// 这是 <c>trit scan</c> 与 <see cref="TritApi"/> 扫描命令的数据入口。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 取的是<b>整条世界投影</b>（所有曾经被写过的车道，按键升序），而不是本 tick 的增量——
    /// 增量恒为 Neg/Pos（Zero 在 <see cref="Emit"/> 里就被丢弃了），拿去做密度扫描会
    /// 得到"处处 100% 非零"这种没有信息量的结果。
    /// </para>
    /// <para>
    /// 代价：<see cref="RuntimeShim.SnapshotBytes"/> 内部会打一次快照（内核快照数 +1）。
    /// 这是手动诊断命令用的，不是热路径。
    /// 解码失败返回空数组——扫描命令会据此给玩家明确提示而不是崩溃。
    /// </para>
    /// </remarks>
    public byte[] CaptureWorldTrits()
    {
        if (disposed)
        {
            return Array.Empty<byte>();
        }

        try
        {
            byte[] raw = shim.SnapshotBytes();
            if (raw.Length == 0)
            {
                return Array.Empty<byte>();
            }

            // SnapshotBytes 的前 8 字节是快照 ID 前缀，之后才是 WorldCodec 载荷。
            // 老格式万一没有前缀，去掉前缀会解出垃圾——那就退回整体解码。
            SortedDictionary<uint, TritDelta> world;
            try
            {
                world = WorldCodec.Decode(raw.AsSpan(SnapshotIdPrefix)).World;
            }
            catch (WireException)
            {
                world = WorldCodec.Decode(raw).World;
            }

            var trits = new byte[world.Count];
            int index = 0;
            foreach (TritDelta value in world.Values)
            {
                trits[index++] = (byte)value;
            }

            LastCaptureTritCount = trits.Length;
            return trits;
        }
        catch (Exception ex)
        {
            // 拿不到数据不是错误：返回空数组，调用方负责告诉玩家"当前没有可扫描的数据"。
            if (Throttle(ref lastCaptureLogAt))
            {
                mod.Logger.Warn($"[trit] 采集世界投影三值序列失败（tick={tick}）：{ex.Message}");
            }

            return Array.Empty<byte>();
        }
    }

    /// <summary>最近一次 <see cref="CaptureWorldTrits"/> 拿到的 trit 数（0 = 还没采集过）。</summary>
    public int LastCaptureTritCount { get; private set; }

    /// <summary>钩子观测到的"本 tick 跑过 AI"计数（由 <see cref="TritHooks"/> 调用）。</summary>
    public void NoteNpcAi() => npcTickedCount++;

    /// <summary>钩子观测到的"本 tick 跑过 Update"计数。</summary>
    public void NoteProjectileUpdate() => projectileTickedCount++;

    /// <summary>
    /// 每 tick 末尾调用一次（<c>On_Main.Update</c> 之后）。
    /// </summary>
    public void OnTickEnd()
    {
        if (disposed)
        {
            return;
        }

        try
        {
            uint gameTick = Main.GameUpdateCount;

            if (!lastGameTickValid)
            {
                // 首次采样的可观测性：装载后若这条都不出现，说明钩子压根没被触发。
                mod.Logger.Info($"[trit] 世界投影开始采样，game_tick={gameTick}");
            }
            else if (gameTick == lastGameTick)
            {
                // 同一游戏 tick 内的重复帧：菜单/加载界面常见，跳过不采样。
                // 但如果**永远**相等，说明 Main.GameUpdateCount 在本进程里根本不推进
                // （专用服务器实测过这种情况），世界投影会静默停摆——必须说出来。
                if (!warnedStaticGameTick && Throttle(ref lastStaticTickLogAt))
                {
                    mod.Logger.Warn(
                        $"[trit] Main.GameUpdateCount 未推进（恒为 {gameTick}），世界投影暂停采样；" +
                        "若持续出现说明当前运行环境下该计数器不走，桥接层不会有任何 tick 数据。");
                    warnedStaticGameTick = true;
                }

                return;
            }

            lastGameTickValid = true;
            lastGameTick = gameTick;

            tick++;
            changedKeys.Clear();
            changedTrits.Clear();

            int activeNpc = SampleNpcs();
            int activeProjectile = SampleProjectiles();
            int activePlayer = SamplePlayers();

            CommitChangedLanes();
            PublishAndStep(activeNpc, activeProjectile, activePlayer);

            MaybeSnapshot();
            MaybeReportStats();
            MaybeReportStatus();

            if (settings.VerboseLogging && tick % 60 == 0)
            {
                mod.Logger.Info(
                    $"[trit] tick={tick} lanes={changedTrits.Count} applied={LastApplied} log_seq={LastLogSeq} " +
                    $"intents={LastIntentCount} npc={activeNpc} proj={activeProjectile} player={activePlayer} " +
                    $"npc_ticked={npcTickedCount} proj_ticked={projectileTickedCount}");
            }

            prevActiveNpc = activeNpc;
            prevActiveProjectile = activeProjectile;
            prevActivePlayer = activePlayer;
        }
        catch (Exception ex)
        {
            // 桥接层任何异常都不允许冒到游戏主循环。
            mod.Logger.Error("[trit] tick 流水线异常，已跳过本 tick：" + ex);
            try
            {
                shim.Kernel.RollbackTransaction(0); // 无效句柄会按错误码拒绝，只为确保不留下悬挂事务
            }
            catch
            {
                // 忽略：这里只是清理尝试
            }
        }
        finally
        {
            npcTickedCount = 0;
            projectileTickedCount = 0;
        }
    }

    // ------------------------------------------------------------------ 采样

    private int SampleNpcs()
    {
        NPC[] npcs = Main.npc;
        if (npcs is null)
        {
            return 0;
        }

        int limit = Math.Min(npcs.Length, settings.MaxNpcs);
        int active = 0;

        for (int i = 0; i < npcs.Length; i++)
        {
            NPC npc = npcs[i];
            if (npc is null)
            {
                continue;
            }

            ref EntityState state = ref npcState[0];
            bool tracked = i < npcState.Length;
            if (tracked)
            {
                state = ref npcState[i];
            }

            if (!npc.active)
            {
                if (tracked && state.Present)
                {
                    Emit(LaneMap.Key(LaneMap.KindNpc, i, LaneMap.FieldActive), TritDelta.Neg);
                    state = default;
                }

                continue;
            }

            active++;
            if (!tracked || active > limit)
            {
                continue;
            }

            EmitDelta(LaneMap.KindNpc, i, LaneMap.FieldActive, npc.active ? 1 : 0, state.Present && state.Active ? 1 : 0, state.Present);
            EmitDelta(LaneMap.KindNpc, i, LaneMap.FieldLife, npc.life, state.Life, state.Present);
            EmitDelta(LaneMap.KindNpc, i, LaneMap.FieldLifeMax, npc.lifeMax, state.LifeMax, state.Present);
            EmitDelta(LaneMap.KindNpc, i, LaneMap.FieldPositionX, (int)npc.position.X, state.X, state.Present);
            EmitDelta(LaneMap.KindNpc, i, LaneMap.FieldPositionY, (int)npc.position.Y, state.Y, state.Present);
            EmitDelta(LaneMap.KindNpc, i, LaneMap.FieldVelocityX, (int)npc.velocity.X, state.Vx, state.Present);
            EmitDelta(LaneMap.KindNpc, i, LaneMap.FieldVelocityY, (int)npc.velocity.Y, state.Vy, state.Present);
            EmitDelta(LaneMap.KindNpc, i, LaneMap.FieldDirection, npc.direction, state.Dir, state.Present);

            state.Present = true;
            state.Active = npc.active;
            state.Life = npc.life;
            state.LifeMax = npc.lifeMax;
            state.X = (int)npc.position.X;
            state.Y = (int)npc.position.Y;
            state.Vx = (int)npc.velocity.X;
            state.Vy = (int)npc.velocity.Y;
            state.Dir = npc.direction;
        }

        return active;
    }

    private int SampleProjectiles()
    {
        Projectile[] projectiles = Main.projectile;
        if (projectiles is null)
        {
            return 0;
        }

        int limit = Math.Min(projectiles.Length, settings.MaxProjectiles);
        int active = 0;

        for (int i = 0; i < projectiles.Length; i++)
        {
            Projectile projectile = projectiles[i];
            if (projectile is null)
            {
                continue;
            }

            bool tracked = i < projectileState.Length;
            ref EntityState state = ref projectileState[0];
            if (tracked)
            {
                state = ref projectileState[i];
            }

            if (!projectile.active)
            {
                if (tracked && state.Present)
                {
                    Emit(LaneMap.Key(LaneMap.KindProjectile, i, LaneMap.FieldActive), TritDelta.Neg);
                    state = default;
                }

                continue;
            }

            active++;
            if (!tracked || active > limit)
            {
                continue;
            }

            EmitDelta(LaneMap.KindProjectile, i, LaneMap.FieldActive, 1, state.Present && state.Active ? 1 : 0, state.Present);
            EmitDelta(LaneMap.KindProjectile, i, LaneMap.FieldPositionX, (int)projectile.position.X, state.X, state.Present);
            EmitDelta(LaneMap.KindProjectile, i, LaneMap.FieldPositionY, (int)projectile.position.Y, state.Y, state.Present);
            EmitDelta(LaneMap.KindProjectile, i, LaneMap.FieldVelocityX, (int)projectile.velocity.X, state.Vx, state.Present);
            EmitDelta(LaneMap.KindProjectile, i, LaneMap.FieldVelocityY, (int)projectile.velocity.Y, state.Vy, state.Present);
            EmitDelta(LaneMap.KindProjectile, i, LaneMap.FieldDirection, projectile.direction, state.Dir, state.Present);

            state.Present = true;
            state.Active = true;
            state.X = (int)projectile.position.X;
            state.Y = (int)projectile.position.Y;
            state.Vx = (int)projectile.velocity.X;
            state.Vy = (int)projectile.velocity.Y;
            state.Dir = projectile.direction;
        }

        return active;
    }

    private int SamplePlayers()
    {
        Player[] players = Main.player;
        if (players is null)
        {
            return 0;
        }

        int limit = Math.Min(players.Length, settings.MaxPlayers);
        int active = 0;

        for (int i = 0; i < players.Length; i++)
        {
            Player player = players[i];
            if (player is null)
            {
                continue;
            }

            bool tracked = i < playerState.Length;
            ref EntityState state = ref playerState[0];
            if (tracked)
            {
                state = ref playerState[i];
            }

            if (!player.active)
            {
                if (tracked && state.Present)
                {
                    Emit(LaneMap.Key(LaneMap.KindPlayer, i, LaneMap.FieldActive), TritDelta.Neg);
                    state = default;
                }

                continue;
            }

            active++;
            if (!tracked || active > limit)
            {
                continue;
            }

            EmitDelta(LaneMap.KindPlayer, i, LaneMap.FieldActive, 1, state.Present && state.Active ? 1 : 0, state.Present);
            EmitDelta(LaneMap.KindPlayer, i, LaneMap.FieldLife, player.statLife, state.Life, state.Present);
            EmitDelta(LaneMap.KindPlayer, i, LaneMap.FieldLifeMax, player.statLifeMax2, state.LifeMax, state.Present);
            EmitDelta(LaneMap.KindPlayer, i, LaneMap.FieldMana, player.statMana, state.Mana, state.Present);
            EmitDelta(LaneMap.KindPlayer, i, LaneMap.FieldPositionX, (int)player.position.X, state.X, state.Present);
            EmitDelta(LaneMap.KindPlayer, i, LaneMap.FieldPositionY, (int)player.position.Y, state.Y, state.Present);
            EmitDelta(LaneMap.KindPlayer, i, LaneMap.FieldVelocityX, (int)player.velocity.X, state.Vx, state.Present);
            EmitDelta(LaneMap.KindPlayer, i, LaneMap.FieldVelocityY, (int)player.velocity.Y, state.Vy, state.Present);
            EmitDelta(LaneMap.KindPlayer, i, LaneMap.FieldDirection, player.direction, state.Dir, state.Present);

            state.Present = true;
            state.Active = true;
            state.Life = player.statLife;
            state.LifeMax = player.statLifeMax2;
            state.Mana = player.statMana;
            state.X = (int)player.position.X;
            state.Y = (int)player.position.Y;
            state.Vx = (int)player.velocity.X;
            state.Vy = (int)player.velocity.Y;
            state.Dir = player.direction;
        }

        return active;
    }

    /// <summary>
    /// 当前值 vs 上一 tick 值 → 三值增量。相等不产生意图（0 = 不驱动，不是"命令归零"）。
    /// </summary>
    private void EmitDelta(uint kind, int slot, uint field, int current, int previous, bool hadPrevious)
    {
        if (hadPrevious && current == previous)
        {
            return;
        }

        TritDelta delta = current > previous ? TritDelta.Pos
            : current < previous ? TritDelta.Neg
            : TritDelta.Zero;

        Emit(LaneMap.Key(kind, slot, field), delta);
    }

    private void Emit(uint key, TritDelta delta)
    {
        if (delta == TritDelta.Zero)
        {
            // 0 是"未驱动"，提交它等于往仲裁里塞一个不驱动的车道，
            // 除了噪音没有任何语义，直接丢弃。
            return;
        }

        changedKeys.Add(key);
        changedTrits.Add(delta);
    }

    // -------------------------------------------------------------- 提交与派发

    private void CommitChangedLanes()
    {
        if (changedTrits.Count == 0)
        {
            LastApplied = 0;
            return;
        }

        ulong txn = 0;
        bool committed = false;
        try
        {
            // 一批车道一次提交：逐条 SubmitIntent 每条都要走一趟 FFI（实测 0.5µs/条），
            // 战斗高峰一个 tick 就能堆到上千条，等于白烧 0.8ms 的主线程时间。
            txn = shim.BeginTransaction();
            shim.SubmitIntents(txn, changedKeys, changedTrits);

            TxnCommitResult result = shim.Commit(txn);
            committed = true;
            LastApplied = result.Applied;
            LastLogSeq = result.LogSeq;
        }
        catch (KernelException ex) when (ex.ShouldFallBack)
        {
            ConflictCount++;
            LastApplied = 0;
            if (ConflictCount <= 3 || settings.VerboseLogging)
            {
                mod.Logger.Warn($"[trit] 事务提交被拒绝（第 {ConflictCount} 次）：{ex.Message}");
            }
        }
        catch (Exception ex)
        {
            // 其它内核/传输异常：不泄漏悬挂事务，并节流告警，避免每 tick 刷屏。
            ConflictCount++;
            LastApplied = 0;
            if (ConflictCount <= 3 || settings.VerboseLogging)
            {
                mod.Logger.Warn($"[trit] 事务提交异常（第 {ConflictCount} 次）：{ex.Message}");
            }
        }
        finally
        {
            // 只要没提交成功就回滚，杜绝事务泄漏累积到并发上限（-4）的二级故障。
            if (!committed && txn != 0)
            {
                TryRollback(txn, null);
            }
        }
    }

    private void TryRollback(ulong txn, Exception? cause)
    {
        try
        {
            shim.RollbackTransaction(txn);
        }
        catch (Exception rollbackError)
        {
            string causeText = cause is null ? "无" : cause.Message;
            mod.Logger.Warn($"[trit] 事务 {txn} 回滚失败（原错误：{causeText}）：{rollbackError.Message}");
        }
    }

    private void PublishAndStep(int activeNpc, int activeProjectile, int activePlayer)
    {
        TritDelta[] heartbeat =
        {
            Sign(activeNpc, prevActiveNpc),
            Sign(activeProjectile, prevActiveProjectile),
            Sign(activePlayer, prevActivePlayer),
        };

        shim.Publish(EventTick, heartbeat);

        PublishIf(EventNpcActivity, activeNpc, prevActiveNpc);
        PublishIf(EventProjectileActivity, activeProjectile, prevActiveProjectile);
        PublishIf(EventPlayerActivity, activePlayer, prevActivePlayer);

        StepResult step = shim.Step((ulong)tick);
        LastIntentCount = step.Intents.Count;

        if (settings.VerboseLogging && LastIntentCount > 0)
        {
            for (int i = 0; i < step.Intents.Count; i++)
            {
                ActionIntent intent = step.Intents[i];
                mod.Logger.Info(
                    $"[trit] 派发 sub={intent.SubId} event={intent.EventId} mod=0x{intent.ModId:X8} tick={tick}");
            }
        }
    }

    private void PublishIf(uint eventId, int current, int previous)
    {
        TritDelta delta = Sign(current, previous);
        if (delta == TritDelta.Zero)
        {
            return;
        }

        shim.Publish(eventId, new[] { delta });
    }

    private static TritDelta Sign(int current, int previous)
    {
        if (previous < 0)
        {
            return TritDelta.Zero;
        }

        return current > previous ? TritDelta.Pos
            : current < previous ? TritDelta.Neg
            : TritDelta.Zero;
    }

    private void MaybeSnapshot()
    {
        int interval = settings.SnapshotIntervalTicks;
        if (interval <= 0 || tick % interval != 0)
        {
            return;
        }

        ulong id;
        try
        {
            id = shim.Snapshot();
        }
        catch (KernelException ex)
        {
            // 快照失败不影响游戏：只降级，不装钩子之外不做任何破坏性动作。
            if (Throttle(ref lastSnapshotLogAt))
            {
                mod.Logger.Warn($"[trit] 快照失败（tick={tick}）：{ex.Message}");
            }

            return;
        }

        SnapshotCount++;

        // 影子差分：只有在真的存在第二个内核可对照时才跑。
        // 默认关闭——没有影子内核时硬塞一个签名只会污染影子日志；
        // 而且裸字节不是线格式载荷，会让内核解包失败（真机踩过：
        // 「非法字节 246：5 个 trit 只能表示 0..=242」）。
        if (settings.ShadowMode)
        {
            RecordShadowDivergence();
        }

        if (settings.VerboseLogging || Throttle(ref lastSnapshotLogAt))
        {
            mod.Logger.Info(
                $"[trit] 快照 #{SnapshotCount} id={id} tick={tick} log_seq={LastLogSeq} applied={LastApplied}");
        }
    }

    /// <summary>
    /// 影子模式下把两个内核的世界投影逐字节对拍一次。
    /// 不一致只记录、只告警，绝不提交——世界投影仍以主内核为准。
    /// </summary>
    private void RecordShadowDivergence()
    {
        ShadowDiffer? differ = shim.Differ;
        if (differ is null)
        {
            return;
        }

        try
        {
            byte[] divergence = differ.CompareWorlds();
            if (divergence.Length == 0)
            {
                return;
            }

            string summary = differ.Recent.Count > 0 ? differ.Recent[0].Summary : "（无摘要）";
            if (Throttle(ref lastStatsLogAt))
            {
                mod.Logger.Warn($"[trit] 影子对拍发现不一致（tick={tick}）：{summary}");
            }
        }
        catch (KernelException ex)
        {
            if (Throttle(ref lastStatsLogAt))
            {
                mod.Logger.Warn($"[trit] 影子对拍失败（tick={tick}）：{ex.Message}");
            }
        }
    }

    /// <summary>
    /// 周期性日志节流：距上次同类日志不足 <see cref="MinLogIntervalSeconds"/> 秒则跳过。
    /// </summary>
    private bool Throttle(ref double last)
    {
        double now = uptime.Elapsed.TotalSeconds;
        if (now - last < MinLogIntervalSeconds)
        {
            return false;
        }

        last = now;
        return true;
    }

    private void MaybeReportStats()
    {
        int interval = settings.StatsIntervalTicks;
        if (interval <= 0 || tick % interval != 0)
        {
            return;
        }

        try
        {
            ulong[] stats = shim.Stats();
            var parts = new string[stats.Length];
            for (int i = 0; i < stats.Length; i++)
            {
                parts[i] = i + "=" + stats[i];
            }

            if (settings.VerboseLogging || Throttle(ref lastStatsLogAt))
            {
                mod.Logger.Info(
                    $"[trit] 统计 tick={tick} mode={shim.Mode} " +
                    "counters[" + string.Join(" ", parts) + "] " +
                    $"net_total={netTotal}");
            }
        }
        catch (KernelException ex)
        {
            if (Throttle(ref lastStatsLogAt))
            {
                mod.Logger.Warn($"[trit] 读取统计失败（tick={tick}）：{ex.Message}");
            }
        }
    }

    /// <summary>
    /// 周期性状态日志：把"当前跑在哪个后端 / GPU 到底启用没 / 事务速率"讲清楚。
    /// 这是常驻的人读摘要，默认就打（不受 <see cref="TritSettings.VerboseLogging"/> 限制）；
    /// 非 verbose 下仍用墙钟节流兜底，避免极端高频场景刷爆日志。
    /// </summary>
    private void MaybeReportStatus()
    {
        int interval = settings.StatsIntervalTicks;
        if (interval <= 0 || tick % interval != 0)
        {
            return;
        }

        // 非 verbose 下受墙钟节流兜底；verbose 下每次到点都打，方便排查。
        if (!settings.VerboseLogging && !Throttle(ref lastStatusLogAt))
        {
            return;
        }

        try
        {
            string backend = shim.IsNative ? "Native" : "Managed(回退)";
            var caps = new List<string>(3);
            if (shim.Supports(CapabilityBits.Parallel)) caps.Add("Parallel");
            if (shim.Supports(CapabilityBits.Gpu)) caps.Add("Gpu");
            if (shim.Supports(CapabilityBits.Budget)) caps.Add("Budget");
            string capText = caps.Count > 0 ? string.Join(",", caps) : "无";

            // 无参 GpuStatus：只读当前状态、不重设策略、不重探测，零额外开销。
            var gpu = shim.GpuStatus();
            string gpuText = gpu.Enabled
                ? $"启用 adapter={gpu.Adapter} backend={gpu.Backend}"
                : $"未启用 原因={(gpu.Reason.Length > 0 ? gpu.Reason : (!gpu.BuiltWithGpu ? "构建未带gpu feature" : "无可用adapter"))}";

            ulong[] stats = shim.Stats();
            ulong committed = stats.Length > 2 ? stats[2] : 0;
            double secs = uptime.Elapsed.TotalSeconds;
            double rate = secs > 0 ? committed / secs : 0;

            mod.Logger.Info(
                $"[trit] 状态 tick={tick} 后端={backend} 能力=[{capText}] " +
                $"GPU={gpuText} policy={gpu.Policy.ToString().ToLowerInvariant()} " +
                $"事务={committed}(约{rate:F0}/s) 网络={netTotal}");
        }
        catch (KernelException ex)
        {
            if (Throttle(ref lastStatusLogAt))
            {
                mod.Logger.Warn($"[trit] 读取状态失败（tick={tick}）：{ex.Message}");
            }
        }
    }

    /// <summary>取消订阅并释放内核句柄。</summary>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;

        foreach (ulong sub in new[] { tickSub, npcSub, projectileSub, playerSub })
        {
            try
            {
                shim.Unsubscribe(sub);
            }
            catch
            {
                // 卸载阶段尽力而为：内核可能已经先一步销毁
            }
        }
    }

    /// <summary>由模组名派生的稳定 mod id（不用 <c>string.GetHashCode</c>：它在 .NET Core 每次启动都变）。</summary>
    private static uint StableModId(string name)
    {
        uint hash = ModIdSeed;
        foreach (char c in name)
        {
            hash = (hash ^ c) * 16777619u;
        }

        return hash;
    }

    /// <summary>单实体上一 tick 的采样快照。</summary>
    private struct EntityState
    {
        public bool Present;
        public bool Active;
        public int Life;
        public int LifeMax;
        public int Mana;
        public int X;
        public int Y;
        public int Vx;
        public int Vy;
        public int Dir;
    }
}
