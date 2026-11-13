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

自动升级逻辑：

```
ObserveOnly 持续 30 天
  → 影子对拍置信度 >= 95%
  → 自动升到 PatchAfterOrig (L1)
  → 玩家在日志里看到提示："trit 内核已升级为 L1 接管模式，可在 settings.json 里关闭"

PatchAfterOrig 持续 60 天
  → 影子对拍置信度 >= 99.99%
  → 自动升到 ReplaceOrig (L2)
  → 同上提示

任何时刻置信度跌破 90% → 自动降回 ObserveOnly
```

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
| **体积** | L2 release-mobile + no-default-features ≤ 2400 KB | CI 已有体积护栏 |
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

### Phase 2：L2 TritEnginePort（~4 周）
- 新建 `trit-engine` crate
- `npc_ai_step` + `projectile_update_step` 纯函数（先覆盖最常见 20 种 NPC + 10 种 Projectile）
- `ActionPlan` 系统
- Hook 链升级为 kernel-first 模式
- **验收**：L2 下原版 AI CPU 降到 0（已跳过）；整体 CPU 使用率降 40-60%

### Phase 3：自动升级 + 仪表盘（~2 周）
- 置信度长期跟踪 + 自动升级/降级逻辑
- `/trit takeover` 子命令（on/off/status）
- `/trit takeover stats` 实时仪表盘（置信度、接管数、diff 率）
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
