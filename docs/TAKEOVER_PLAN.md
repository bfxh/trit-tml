# Issue #2 接管方案草案：从「只观测」到「内核决策回写」

> **状态**：草案，待评审。不改动任何代码。
> **前置条件**：Issue #1（专用服务器无玩家 tick）解决、真机 benchmark 有数据、至少 30 天影子对拍无致命分歧。

---

## 1. 定义：什么是"接管"

**不是**把原版 C# AI/Update 整段替换掉。那需要在 Rust 里复现 Terraria 状态机，工程量不可控、风险是游戏随时崩。

**而是**：让 trit-rt 在旁路线程上做"同一次 tick 的**预跑/预测**"，拿到结果后**择优替换**原版 C# 的昂贵计算，用三进制投影的"整体稳定性"换取原版逐实体 AI 的"局部抖动"。

三种强度，渐进式切换：

| 档位 | 内核做什么 | C# 侧做什么 | 回写粒度 | 风险 | 体积增量 |
|---|---|---|---|---|---|
| **L0 观测（当前）** | 追踪状态 + 路由事件 + 仲裁多模组写入 | 采样 + 提交投影 + Orig 原版 tick | 无 | 零 | — |
| **L1 轻接管** | 旁路线程**复制一份**世界投影 + 跑原版等价逻辑（统计/预测/过滤） | 对拍原版 vs 内核；当内核结果**更便宜且一致**时替换 | 整实体字段（velocity / direction / active 状态） | 低（有降级路径） | ~200 KB（新增统计模块） |
| **L2 中接管** | 内核新增 **ActionPlan** 系统（每条车道的"下一帧应值"） | 接管者 Hook 改为**先跑内核结果、再用原版兜底** | 逐字段 delta + 整实体 | 中（需要 ActionPlan 正确性验证） | ~800 KB（ActionPlan + 验证） |
| **L3 硬接管** | 内核是真的**决策源**（不再只是观测者） | 原版 Orig 完全跳过，只走内核 | 全量 | 高（回滚复杂） | ~1.5 MB（大改） |

**推荐走 L1 → L2 渐进路线**。L0 → L3 一次性切换风险不可控。

---

## 2. L1 轻接管设计（MVP，投入 ~2 周）

### 2.1 新增：Rust 侧 WorldProjectionFork

在现有 `RuntimeConfig` 上新增一个可选配置：

```rust
// runtime.rs
pub struct TakeoverConfig {
    /// 0..1，该实体在影子对拍中"内核预测和原版结果一致"的概率阈值。
    /// 低于此值的实体永远不接管（只观测）。
    pub takeover_confidence_threshold: f32,
    
    /// 接管者的运行频率（每 N tick 一次预测）。
    /// 默认 3：内核在旁路线程上**每 3 tick 做一次预测**，比原版 AI 便宜 3 倍。
    pub prediction_interval_ticks: u32,
    
    /// 预测提前量：内核预测 t+1、t+2、t+3 的状态，原版执行到 t+1 时用内核结果。
    pub prediction_lookahead: u32,
    
    /// 降级策略：内核预测和原版差异超过多少算"不可用"（立即放弃本次接管）。
    pub divergence_tolerance: u32,
}

impl Default for TakeoverConfig {
    // Desktop 默认 L0（只观测）
    // cfg(target_os="android") 默认 L1（轻接管，省 CPU）
    fn default() -> Self { ... }
}
```

`RuntimeConfig` 整体新增 `takeover: Option<TakeoverConfig>` 字段，`None` = 零开销（**L0 保持现状**）。

### 2.2 新增：WorldProjectionFork

```rust
// 新建：crates/trit-rt/src/takeover/fork.rs

pub struct WorldProjectionFork {
    /// 主内核的世界投影 snapshot（不共享所有权，fork 独立副本）。
    snapshot: WorldSnapshot,
    
    /// 过去 N tick 的 lane delta 历史（trit ring buffer）。
    history: TritRingBuffer,
    
    /// 对每个可接管实体的置信度（0..1），由影子对拍持续更新。
    confidence: BTreeMap<u32, f32>,  // key = LaneMap.Key(kind, slot, field_active)
}

impl WorldProjectionFork {
    /// 从主内核 snapshot 创建 fork（每 prediction_interval_ticks 一次）。
    pub fn fork_from(primary: &WorldSnapshot) -> Self { ... }
    
    /// 基于历史预测下一次 tick 的字段值（三值：Neg / Zero / Pos）。
    pub fn predict(&self, key: u32) -> Option<Ternary> { ... }
    
    /// 记录"内核预测和原版实际结果"的 diff，更新置信度。
    pub fn record_outcome(&mut self, key: u32, predicted: Ternary, actual: Ternary) { ... }
}
```

**为什么用 WorldSnapshot 而不是直接让 fork 订阅主内核**：fork 是一个只读副本来做旁路线程预测，不能持有主内核的锁。主内核每次 Snapshot 时快照一次 world projection → fork 用这份快照启动。

### 2.3 Rust → C# 新增 FFI：TT_RT_GET_PREDICTIONS

```c
// ffi.rs 新增
/// 返回"内核对本 tick 应接管的实体字段"的预测结果。
/// 
/// payload 格式：
///   u32  count
///   (u32 key, u8  trit) × count   // key = LaneMap.Key(kind, slot, field)
///
/// trit 语义：
///   0 = Neg（-1 驱动，建议字段值下调）
///   1 = Zero（弃权，原版结果保持）
///   2 = Pos（+1 驱动，建议字段值上调）
///
/// 返回 TT_ERR_BUFFER 时 out_len 写入所需容量，调用方扩容重入。
pub extern "C" fn tt_rt_get_predictions(
    handle: u64,
    out_buf: *mut u8,
    out_capacity: u32,
    out_len: *mut u32,
) -> i32;
```

### 2.4 C# 侧接管者：EntityTakeover

```csharp
// 新建：mod/TritRuntimeMod/src/EntityTakeover.cs

/// <summary>
/// 内核决策回写层。只在 L1+ 模式下启用；L0 时整类零开销。
/// </summary>
internal sealed class EntityTakeover
{
    private readonly RuntimeShim shim;
    private readonly TritSettings settings;
    private readonly WorldBridge bridge;
    private byte[]? predBuffer;
    
    // 置信度表：key → 过去 N 次"内核预测 vs 原版结果一致"的比例
    private readonly Dictionary<uint, float> confidence = new();
    
    // 本次 tick 被接管的实体计数（日志用）
    public int TakeoverCount { get; private set; }
    
    /// <summary>
    /// 在 TritHooks 的回调链中**原版 orig 执行之后**调用。
    /// (a) 取内核预测；(b) 对拍原版 vs 内核；(c) 置信度达标则用内核结果覆盖；(d) 记录结果用于下次置信度更新。
    /// </summary>
    public void AfterOrig(NPC self)
    {
        if (!settings.EnableTakeover || !settings.EnableHooks) return;
        
        // 1. 内核拿预测
        var preds = shim.GetPredictions(ref predBuffer);
        
        // 2. 原版已经跑完 AI，self.velocity/position 是原版结果
        // 3. 内核预测的 trit 语义是"本 tick 这个字段应该比上一 tick ↑/↓"
        //    → 把 trit delta 转成建议值
        //    → 和原版当前值比较
        //    → 如果内核预测对且置信度够 → 覆盖原版值
        if (TryTakeoverFor(self, preds, out var applied))
        {
            TakeoverCount += applied;
        }
    }
}
```

### 2.5 TritHooks 的 Hook 链变化

**当前**：
```
On_NPC.AI += (orig, self) => { orig(self); bridge.NoteNpcAi(); }
```

**L1 + 接管**：
```
On_NPC.AI += (orig, self) => {
    orig(self);                              // 1. 跑原版
    bridge.NoteNpcAi();                      // 2. 观测（不变）
    takeover.AfterOrig(self);                // 3. 内核接管决策（新增）
}
```

**关键**：orig 永远第一个调用。**接管是"事后修正"不是"事前替换"** —— 这样即使内核预测错了，原版已经跑过一次正确结果，接管者最多写一次垃圾值。但因为我们有置信度门禁（`confidence >= threshold` 才接管），垃圾值不会被写入。

### 2.6 降级链

```
1. 内核没返回 predictions（predBuffer 空 / tt_rt_get_predictions 返回 TT_ERR_BUFFER）
   → 本次 tick 放弃接管，原版结果直接保留
   
2. 内核预测的 key 和 LaneMap 对不上（slot 越界 / field 不认识）
   → 跳过这个实体，其余正常接管
   
3. 对拍发现内核预测和原版差异 > divergence_tolerance
   → 记录 out-of-sync，本 tick 该实体置信度 -= 0.1
   → 置信度跌破 threshold → 以后不再对这个实体接管（只观测）
   
4. 连续 N tick 所有实体置信度跌破 threshold
   → TritHooks.Tripped 置位（现有熔断机制复用）
   → 接管层整体停摆，退回纯观测
```

---

## 3. L2 中接管设计（MVP 验证后）

L1 的问题是"fork 只做预测，不做**验证内核对版本**"。L2 让 fork 跑**完整的原版等价逻辑**（但在旁路线程上）。

### 3.1 TritEnginePort（核心工作量）

把 Terraria AI / Projectile.Update / NetMessage 的**状态变换逻辑**从 C# 翻译成 Rust 的**纯函数**：

```rust
// 新建：crates/trit-engine（独立 crate，和 trit-rt 同 workspace）

pub fn npc_ai_step(state: NpcSnapshot, rand: RandState) -> NpcDelta { ... }
pub fn projectile_update_step(state: ProjectileSnapshot, rand: RandState) -> ProjectileDelta { ... }
pub fn net_message_filter(msg_type: u32, payload: &[u8]) -> Option<NetDecision> { ... }
```

**为什么是纯函数**：同输入 → 同输出（Terraria AI 是确定性的，RNG 种子可复现）。旁路线程上复制一份状态快照跑函数，完全不需要和主线程同步。

### 3.2 ActionPlan 系统

```rust
// trit-rt 新增 ActionPlan
pub struct ActionPlan {
    /// 本 tick 应由内核"主动执行"的写入（不是观测到的）。
    pub writes: Vec<ResolvedIntent>,  // key + delta + 置信度
    
    /// 内核认为"原版这段逻辑根本不需要跑"的理由（给降级用）。
    pub skip_reasons: Vec<SkipReason>,
}

impl ActionPlan {
    pub fn plan_from(fork: &WorldProjectionFork, engine: &TritEnginePort) -> Self {
        // fork 基于历史 + engine 纯函数跑一次预测
        // 输出"本 tick 原版应该写哪些车道"
        // 注意：这和"观测到原版写了哪些车道"是反向的
    }
}
```

### 3.3 C# 侧接管 Hook 变化

**L2 Hook 链**：
```
On_NPC.AI += (orig, self) => {
    // 1. 先拿内核 ActionPlan（**在 orig 之前**！）
    var plan = shim.GetActionPlan(self.Id);
    if (plan != null && plan.HasConfidenceAbove(threshold)) {
        // 2. 跑内核决策结果（纯函数输出 delta）
        ApplyActionPlan(self, plan);
        // 3. 跳过原版 orig —— 内核结果已经写了
        return;
    }
    
    // 4. 内核不可用 / 置信度不够 → 原版兜底
    orig(self);
    bridge.NoteNpcAi();
}
```

**和 L1 的本质差异**：
- L1 = orig first，内核事后修正（安全但有"双份开销"）
- L2 = kernel first，直接替换 orig（省 CPU 但风险高，需要置信度门禁更严格）

### 3.4 体积影响

| 模块 | 代码量 | DLL 体积 | 说明 |
|---|---|---|---|
| trit-engine (NPC AI) | ~800 行 | ~300 KB | Terraria.NPC.AI 的 150+ 分支 |
| trit-engine (Projectile) | ~400 行 | ~150 KB | Projectile.Update 的 filter 逻辑 |
| ActionPlan + 置信度 | ~500 行 | ~200 KB | 大部分是状态 + 统计 |
| **合计** | ~1700 行 | **~650 KB** | release-mobile profile fat LTO 后更小 |

---

## 4. L3 硬接管（目标态，暂不做详细设计）

内核变成**唯一决策源**。原版 C# AI/Update 完全不跑。游戏通过 `ModSystem.PostUpdateEverything` → `shim.ExecuteActionPlan()` 拿到本 tick 应执行的全部写入 → C# 侧用反射/MonoMod 直接改 `Main.npc[i].velocity.X` 等字段。

**前置条件**：
- L2 连续 60 天 100% 置信度（影子对拍差异率 < 0.01%）
- 至少 200 个不同的 Terraria 模组环境下验证过
- 回滚时间 < 1 tick（接管层熔断 → 原版自动 re-enable）

**风险**：任何未覆盖的原版逻辑路径（ModAI、Hook 链、NetMessage）都会在接管后被跳过。需要完整的"原版逻辑覆盖率"仪表盘。

---

## 5. 渐进式切换：三档配置 + 自动升级

```csharp
// TritSettings.cs 新增
public enum TakeoverMode
{
    /// <summary>只观测（默认，DesktopHigh / DesktopLow）。</summary>
    ObserveOnly = 0,
    
    /// <summary>L1 轻接管：orig 先跑，内核事后修正（Android Mobile 默认）。</summary>
    PatchAfterOrig = 1,
    
    /// <summary>L2 中接管：内核先跑，跳过 orig（玩家显式选）。</summary>
    ReplaceOrig = 2,
}

public TakeoverMode Takeover { get; set; }
```

自动升级逻辑（Phase 3 实现口径）：

```
ObserveOnly + 自动档（autoTakeover=true，默认）
  → 每 autoCalibrationIntervalTicks（默认 1200 tick ≈ 20 秒）做一次 L0 校准：
    推一次全量实体状态 → 取一次 ActionPlan → 只对拍、不应用
  → 窗口连续 30 天、样本 ≥ 10000、一致率 ≥ 95%
  → 装载期或校准 tick 自动升到 PatchAfterOrig (L1)，写回 settings.json
  → 日志提示："trit 内核已升级为 L1 接管模式，可用 trit takeover auto off 关闭"

PatchAfterOrig（仅当档位由自动升级设置，autoManaged=true）
  → 单 tick 样本 ≥ 20 且一致率跌破 80%
  → 本 tick 立即回落 ObserveOnly，清空窗口重新积累

任何时刻玩家手动 trit takeover on|off|replace → autoManaged=false，自动档不再降级该档位；
trit takeover auto on|off 控制自动档本身。
```

L1 → L2 不自动升级：ReplaceOrig 始终由玩家显式开启。

---

## 6. 回滚机制（所有档位共享）

```
熔断触发（hook 连续异常 / 置信度持续下跌 / 玩家手动关）：
  ↓
TritSettings.EnableTakeover = false （自动写 settings.json）
  ↓
EntityTakeover 实例置 null 或空转（AfterOrig 直接 return）
  ↓
TritHooks 回到纯观测：orig(self); bridge.NoteNpcAi();
  ↓
下一次 tick 接管层完全零开销
  ↓
日志告警："内核接管已停用，原因=xxx；可在 trit takeover on 手动重开"
```

关键：**接管层的"关"就是一行 `if (!EnableTakeover) return;`**。没有资源清理、没有状态回滚、没有进程内同步。和现有 TritHooks.Guarded 熔断机制完全对齐。

---

## 7. 验证矩阵

| 维度 | 覆盖度要求 | 怎么测 |
|---|---|---|
| **功能正确性** | 每个可接管实体的每种状态 → 原版 vs 内核 diff = 0 | L1 下跑 100 小时 Terraria + 影子对拍 |
| **性能** | L1 CPU 开销 ≤ 观测模式的 15%；L2 CPU 开销 ≤ 原版的 50% | Android 真机 benchmark + 桌面 dotTrace |
| **安全** | 接管者写入只改 Terraria 公开字段，不碰私有/反射 | 代码审计 + Mono.TypeNameChecker |
| **回滚** | 触发熔断后 1 tick 内回到纯观测，无副作用 | 手动改 settings.json + 观察日志 |
| **兼容** | 至少 20 个热门 tML 模组环境下接管不崩 | 模组依赖矩阵测试 |
| **体积** | L2 release-mobile + no-default-features ≤ 2400 KB；release ≤ 3072 KB | CI `size-guard`（release ×2 + mobile，含「默认特性不得引入 trit-net/wgpu」的依赖树断言） |
| **三端** | Windows / Linux / macOS / Android 行为一致 | 跨平台 CI matrix |

---

## 8. 实施计划（分三期）

### Phase 1：L1 MVP（~2 周）
- `trit-rt/src/takeover/fork.rs` + `TakeoverConfig`
- Rust FFI `TT_RT_GET_PREDICTIONS`
- C# `EntityTakeover` + Hook 链变化
- `TritSettings.TakeoverMode` 枚举 + Android 默认 L1
- 日志仪表盘（当前置信度、接管数、降级原因）
- **验收**：L1 下 Android 端 CPU 使用率降 15-20%；Windows 桌面开销 ≤ 5%

### Phase 2：L2 TritEnginePort（进行中）
- 新建 `trit-engine` crate
- `npc_ai_step` + `projectile_update_step` 纯函数
- `ActionPlan` 系统（`0x1F`，带 confidence）
- Hook 链升级为 kernel-first 模式
- 当前覆盖（编号为原版 `NPCID` / `ProjectileID`，含负 ID 体型变体；以 `trit-engine/src/registry.rs` 为准）：
  - 史莱姆跳跃 AI：1、-1..-10、16（12 种）
  - 僵尸地面追击：3、-44 / -45、-54 / -55（5 种）
  - 飞行追击：2、-37..-43、5、6、23、42、49、51、60、93、-65..-56（26 种）
  - 弹幕：1..5 / 27 / 41 / 81 / 82 / 91 / 103 / 109 / 172 / 240 抛物线、
    44 / 55 / 83 / 84 / 96 / 100 / 115 / 128 / 258 / 264 / 288 直线（25 种）
  - 刻意不 port：Boss（4 Eye of Cthulhu、50 King Slime）、蠕虫体节（7..15）、骷髅（21、-46..-53）
  - 弹幕分类依据与局限：`Projectile_IDs` 页无运动学字段，「抛物线 / 直线」只能按
    `gravity > 0 / = 0` 近似归属；27 Water Bolt、258 Golem Fireball 的反弹与
    44 Demon Sickle 的追踪未建模，属明确近似。
  - 弹幕不做 KernelFirst：`Projectile.Update` 本身就是位置积分 / 重力 / 碰撞 /
    `timeLeft`，跳过 orig 会让弹幕悬停。弹幕仅参与预测与对拍；只有 NPC 走
    「跳过 `NPC.AI`」（位置由 `Main.UpdateNPC` 负责）。
  - 抛物线分支按 `velocity.y + gravity` 预测**下一 tick** 的符号，顶点处翻转；
    重力值取自状态快照（当前固定 0.08，尚未按 type 采样）。
- 采样保真：`on_ground` / `tile_below` 取碰撞真值（`npc.collideY` + 脚底 4px 实心检测），
  史莱姆 / 僵尸 / 飞行追击均按 `target_dx, dy` 转向，不再是「镜像当前速度」。
- 速度大小消费：三元车道装不下浮点速度，`ACTION_PLAN` 尾部追加可选速度表
  （`kind u8, slot u16, magnitude f32`），只下发「水平速度即移动速度」的地面家族
  （史莱姆起跳 / 僵尸追击）；飞行家族不再虚构 `walk_speed.max(1.8)`。
  KernelFirst（ReplaceOrig）写 `velocity.X` 时优先用该值，缺失则退回「保留当前绝对值」。
  旧客户端 / 旧内核双向降级，不升 ABI。
  幅度兜底：内核只能给方向，给不了幅度的通道必须能自己复现那一步的物理，否则整只交回
  原版，且都在写任何字段之前判定（避免半接管）。Y 轴不写死重力常量，而是「观测学习」：
  每个跑过 orig 的 tick 在 `NPC.AI` 两侧取 `velocity.Y` 的前后值（采样点夹在 AI 两侧，
  重力加在 AI 里还是 `Main.UpdateNPC` 里都不影响该观测），用观测对拟合一步仿射动力学
  `vy_next = α·vy + β`（常量重力是 α=1；悬停阻尼/入水阻力是 β≈0、α<1；终端速度退化成
  α=1、β=0）。是否接管只看「模型是否已确认」——内核的 vy 指令仅用于符号交叉校验，
  所以悬停这类内核不表态的空中实体也能复现；系数跨落地保留，下一次离地第一 tick 即可
  复现。地面起跳/冲量、模型未收敛、入水撞墙导致符号对不上内核预测的一律交回原版。
  实现是
  `dotnet/TritRuntime.Shim/src/VerticalMotionModel.cs` 里的纯结构体，单测覆盖常量重力 /
  阻尼 / 终端速度 / 冲量失效 / NaN 五类情形。
  X 轴要求速度表里有该实体；表里没有且 `|velocity.X| < 0.01` 时交回原版，否则飞行家族
  会被固定在 ±0.1 而不再逐 tick 加速。
  跳过 orig 的两条硬约束：①写入必须真的改变字段（否则只剩副作用，直接交回原版，
  例如地面史莱姆只输出当前朝向）；②连续接管最多 4 tick —— `ai[]` 计时器、帧动画、
  目标选择都活在被跳过的 `NPC.AI` 里，到点必须跑一次原版推进状态，同时给垂直模型
  一组真实观测（终端速度这类非线性钳制的误差最多累积 3 个 Δ）。
- 目标预测消费（v6，观测口径）：`STATE_PUSH` 记录带上实体中心坐标（i32 ×16，
  `has_position` 门控），载荷尾部追加玩家表（slot / 中心 / active·dead·ghost·immune）；
  内核为每个带位置的 NPC 取「最近的活跃玩家」（跳过死亡 / 幽灵，等距取 slot 小者），
  经 `ACTION_PLAN` 尾部目标表下发（`kind u8, slot u16, target_slot u16`，`0xFFFF` = 无目标）。
  本阶段 C# 只在 orig 之后把内核 target 与 `npc.target` 对拍，累计样本 / 命中率进
  `/trit takeover stats` 与周期日志，**不写 `npc.target`**（避免假覆盖）。
- 历史修正：早期注释凭记忆写错过编号（把 4 当成 Eye of Cthulhu、10 当成 Giant Worm Head；
  实际 4 = Unholy Arrow、10 = Purification Powder，已在 `Projectile_IDs` 页核对），
  现已改为「只登记真正有实现分支的类型」，并由 `every_ported_type_has_a_branch` 测试锁定。
- **验收**：L2 下原版 AI CPU 降到 0（已跳过）；整体 CPU 使用率降 40-60%

### Phase 3：自动升级 + 仪表盘（~2 周）— 已实现
- 置信度长期跟踪 + 自动升级/降级逻辑（`TakeoverAutoPilot`，L0 低频校准取证）
- `/trit takeover` 子命令（on / off / replace / auto on|off / status）；`replace` 是 L2 KernelFirst 的唯一入口
- `/trit takeover stats` 实时仪表盘（置信度、接管数、diff 率、升级窗口进度、垂直模型命中率/平均误差/回退次数）
- **验收**：连续 30 天置信度 ≥ 95% 后自动升级到 L1；任何异常 1 tick 内自动降级

---

## 9. 风险清单

| 风险 | 概率 | 影响 | 缓解 |
|---|---|---|---|
| Terraria 1.4.5 / 1.5 更新改 AI 逻辑，L2 接管炸 | 中 | 高 | 影子对拍持续验证 → 置信度跌破自动降级 |
| 某模组的 ModAI 和 trit-engine port 不一致 | 高 | 中 | 置信度按模组隔离：特定模组下的 NPC 永远只观测 |
| 接管写入触发 Terraria 同步锁/状态机异常 | 低 | 极高 | L1 orig-first 模式 → 写入在主线程安全位置 → 异常被 Guarded 吞掉 |
| ActionPlan 产生的 trit 签名被仲裁拒绝 | 中 | 低 | 仲裁层已有 lane dispute 机制 → 有争议车道不写入 + 记录 warning |
| trit-engine port 和原版 C# 行为在极端分支不一致 | 高 | 中 | 先覆盖高频路径（90% NPC 类型）→ 冷门 NPC 继续只观测 |

---

## 10. 不改的东西

- **`third_party/trit` 子模块**（上游只读依赖，不动）
- **FFI ABI 基本签名** —— 新增 `TT_RT_GET_PREDICTIONS` 和 `TT_RT_GET_ACTION_PLAN`，现有所有 opcode 保持不变
- **影子差分机制** —— L1/L2 接管时继续跑影子对拍，置信度持续更新
- **`#[cfg(target_os)]` Mobile profile 默认值** —— 接管档位和 memory/workers 预算是正交的两维
- **Release + no-default-features 的体积护栏** —— Phase 2 完成后重新过一遍 CI matrix
