//! 内核接管系统（L1 轻接管 MVP）。
//!
//! 设计原则：
//! - **零开销 L0**：`RuntimeConfig.takeover == None` 时，整模块不会被 Rust 侧调起。
//!   `predictions` opcode 返回空 payload，C# 侧跳过接管。
//! - **fork 不持有主内核锁**：fork 只保存键集（`Vec<u32>`）与对拍历史，不复制世界值；
//!   `refresh_world` 仅在结构变化（新增车道 / 清空 / 重建）时重建键集，稳态零分配。
//! - **置信度 = 影子对拍延伸**：fork 的 `record_outcome` 被 C# 侧在对拍原版 vs 内核
//!   时调用，自动更新置信度表（环形位图，每键一个 `u64`，不再有独立堆分配）。
//!
//! Phase 1 只做**历史外推**预测（L1），不跑原版等价逻辑（那是 L2 trit-engine 的事）。

use std::collections::BTreeMap;

use trit_core::value::Ternary;

use crate::world::TritWorld;

/// 车道键解码 —— 与 C# `LaneMap` 的编码严格对齐（ABI，改动必须同步升 LayoutVersion）。
///
/// 编码：`[31..28] kind | [27..12] slot | [11..0] field`。
pub mod lane {
    /// 种类：NPC。
    pub const KIND_NPC: u8 = 1;
    /// 种类：Projectile。
    pub const KIND_PROJECTILE: u8 = 2;
    /// 种类：Player。
    pub const KIND_PLAYER: u8 = 3;

    /// 字段：velocity.X。
    pub const FIELD_VELOCITY_X: u32 = 0x007;
    /// 字段：velocity.Y。
    pub const FIELD_VELOCITY_Y: u32 = 0x008;
    /// 字段：direction。
    pub const FIELD_DIRECTION: u32 = 0x009;

    const SLOT_SHIFT: u32 = 12;
    const KIND_SHIFT: u32 = 28;
    const SLOT_MASK: u32 = 0xFFFF;
    const KIND_MASK: u32 = 0xF;
    const FIELD_MASK: u32 = 0xFFF;

    /// 拆出种类（4 位）。
    #[inline]
    pub fn kind(key: u32) -> u8 {
        ((key >> KIND_SHIFT) & KIND_MASK) as u8
    }

    /// 拆出槽位（16 位，即 `whoAmI`）。
    #[inline]
    pub fn slot(key: u32) -> i32 {
        ((key >> SLOT_SHIFT) & SLOT_MASK) as i32
    }

    /// 拆出字段号（12 位）。
    #[inline]
    pub fn field(key: u32) -> u32 {
        key & FIELD_MASK
    }
}

/// 接管模式。
///
/// Phase 1 MVP 只实现 L0（只观测）和 L1（PatchAfterOrig）。
/// Phase 2 MVP 新增 L2 ReplaceOrig —— 枚举位保留，完整 KernelFirst 行为后续接入。
/// 当前 L2 在 Rust 侧行为等价于 L1（Fork.predict 不读 mode 字段，只靠 confidence_threshold 区分），
/// mode 字符串主要用于 C# 侧日志与硬夹。
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default, serde::Serialize, serde::Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum TakeoverMode {
    /// 只观测（默认，DesktopHigh / DesktopLow）。
    #[default]
    ObserveOnly,
    /// L1 轻接管：原版先跑、内核事后修正（Android Mobile 默认）。
    PatchAfterOrig,
    /// L2 中接管：完整 KernelFirst（先跑内核 ActionPlan，跳过 orig）。
    /// Phase 2 MVP 里 Rust 侧暂时等价于 PatchAfterOrig（Fork.predict 只看 confidence_threshold），
    /// C# 侧用更高门槛（≥ 0.9999）区分 —— 真正跳过 orig 等 KernelFirst 完整版。
    ReplaceOrig,
}

/// 接管配置。`None` = L0（零开销）。
#[derive(Debug, Clone, serde::Serialize, serde::Deserialize)]
pub struct TakeoverConfig {
    /// 接管模式。
    #[serde(default)]
    pub mode: TakeoverMode,

    /// 置信度阈值：影子对拍历史 ≥ 此值的实体才允许接管。
    /// 低于此值的实体永远只观测，内核预测结果被自动丢弃。
    ///
    /// 默认 0.95：30 个采样窗口里内核预测和原版一致 ≥ 29 次才放行。
    #[serde(default = "default_threshold")]
    pub confidence_threshold: f32,

    /// 置信度窗口大小：每个键用多少个样本更新置信度。
    ///
    /// 默认 30。窗口越大越稳，但置信度收敛更慢。
    #[serde(default = "default_window")]
    pub confidence_window: u32,

    /// 接管者运行频率：每 N tick 做一次预测（降低旁路线程开销）。
    ///
    /// 默认 3：内核每 3 tick 预测一次，比原版 AI 便宜 3×。
    #[serde(default = "default_interval")]
    pub prediction_interval_ticks: u32,

    /// 不可用降级阈值：内核预测和原版实际结果差异的累计次数。
    /// 超过后把置信度跌破 threshold，该实体后续永远不再接管。
    ///
    /// 默认 50。
    #[serde(default = "default_divergence_tolerance")]
    pub divergence_tolerance: u32,
}

fn default_threshold() -> f32 {
    0.95
}
fn default_window() -> u32 {
    30
}
fn default_interval() -> u32 {
    3
}
fn default_divergence_tolerance() -> u32 {
    50
}

impl Default for TakeoverConfig {
    fn default() -> Self {
        Self {
            mode: TakeoverMode::default(),
            confidence_threshold: default_threshold(),
            confidence_window: default_window(),
            prediction_interval_ticks: default_interval(),
            divergence_tolerance: default_divergence_tolerance(),
        }
    }
}

impl Eq for TakeoverConfig {}

// f32 没有 Eq，手动实现用 to_bits 保证确定性比较
impl PartialEq for TakeoverConfig {
    fn eq(&self, other: &Self) -> bool {
        self.mode == other.mode
            && self.confidence_threshold.to_bits() == other.confidence_threshold.to_bits()
            && self.confidence_window == other.confidence_window
            && self.prediction_interval_ticks == other.prediction_interval_ticks
            && self.divergence_tolerance == other.divergence_tolerance
    }
}

/// 单键的对拍历史：一个 64 位环形位图 + 累计不一致次数。
#[derive(Clone, Copy, Debug)]
struct HistoryRing {
    /// bit = 1 表示该次内核预测与原版一致；只使用低 `window` 位。
    bits: u64,
    /// 有效样本数（≤ window）。
    len: u8,
    /// 下一个写入位。
    head: u8,
    /// 窗口大小（1..=64）。
    window: u8,
}

impl HistoryRing {
    fn new(window: u32) -> Self {
        Self {
            bits: 0,
            len: 0,
            head: 0,
            window: window.clamp(1, 64) as u8,
        }
    }

    fn push(&mut self, consistent: bool) {
        let mask = 1u64 << self.head;
        if consistent {
            self.bits |= mask;
        } else {
            self.bits &= !mask;
        }
        self.head += 1;
        if self.head >= self.window {
            self.head = 0;
        }
        if self.len < self.window {
            self.len += 1;
        }
    }

    #[inline]
    fn samples(&self) -> usize {
        self.len as usize
    }

    #[inline]
    fn matches(&self) -> usize {
        self.bits.count_ones() as usize
    }
}

#[derive(Clone, Copy, Debug)]
struct KeyHistory {
    ring: HistoryRing,
    divergences: u32,
}

impl KeyHistory {
    fn new(window: u32) -> Self {
        Self {
            ring: HistoryRing::new(window),
            divergences: 0,
        }
    }
}

fn window_of(config: &TakeoverConfig) -> u32 {
    config.confidence_window.clamp(1, 64)
}

/// 世界投影 fork：键集 + 历史外推预测。
///
/// 每个键维护过去 `confidence_window` 次「内核预测 vs 原版实际」结果（环形位图）。
/// 世界值本身不复制——预测只需要键集，键集只随世界结构变化而重建。
#[derive(Clone, Debug)]
pub struct WorldProjectionFork {
    /// 已观察键集（升序，用于 predict_all 遍历）。
    keys: Vec<u32>,

    /// 每个键的对拍历史与不一致计数。
    history: BTreeMap<u32, KeyHistory>,

    /// 配置（阈值 / 窗口 / 间隔）。
    config: TakeoverConfig,

    /// 键集对应的世界结构版本；相同则 `refresh_world` 直接返回。
    seen_serial: u64,
}

impl WorldProjectionFork {
    /// 从主内核的世界投影创建 fork。
    pub fn fork_from(snapshot: &TritWorld, config: TakeoverConfig) -> Self {
        let mut keys = Vec::with_capacity(snapshot.len());
        snapshot.for_each(|key, _| keys.push(key));
        keys.sort_unstable();
        Self {
            keys,
            history: BTreeMap::new(),
            config,
            seen_serial: snapshot.structure_serial(),
        }
    }

    /// 从主内核世界投影刷新键集。
    /// 只在世界结构变化时重建，不重置历史 —— 置信度是长期跟踪。
    pub fn refresh_world(&mut self, snapshot: &TritWorld) {
        let serial = snapshot.structure_serial();
        if serial == self.seen_serial {
            return;
        }
        self.keys.clear();
        snapshot.for_each(|key, _| self.keys.push(key));
        self.keys.sort_unstable();
        self.seen_serial = serial;
    }

    /// 已观察键集（升序，TritEnginePort 双轨 predict 遍历用）。
    pub fn keys(&self) -> &[u32] {
        &self.keys
    }

    /// 对每个可接管键跑历史外推预测，结果写入 `out`（按 key 升序）。
    ///
    /// 复用调用方缓冲：稳态不再分配。
    pub fn predict_all_into(&self, out: &mut Vec<(u32, Ternary, f32)>) {
        out.clear();
        for &key in &self.keys {
            let Some(trit) = self.predict(key) else {
                continue;
            };
            if trit != Ternary::Unknown {
                out.push((key, trit, self.confidence_of(key)));
            }
        }
        out.sort_unstable_by_key(|(key, _, _)| *key);
    }

    /// 预测单个键的下一 tick 值（三值：Neg / Zero 弃权 / Pos）。
    pub fn predict(&self, key: u32) -> Option<Ternary> {
        // 置信度门禁：不一致次数超标 → 永远弃权
        if self
            .history
            .get(&key)
            .map(|h| h.divergences >= self.config.divergence_tolerance)
            .unwrap_or(false)
        {
            return Some(Ternary::Unknown);
        }

        let conf = self.confidence_of(key);
        if conf < self.config.confidence_threshold {
            return Some(Ternary::Unknown);
        }

        // 历史外推：全部 Pos → Pos，全部 Neg → Neg，否则震荡。
        let hist = self.history.get(&key)?;
        if hist.ring.samples() < 3 {
            return Some(Ternary::Unknown); // 样本太少
        }

        let pos = hist.ring.matches();
        let neg = hist.ring.samples() - pos;
        if neg == 0 {
            Some(Ternary::True)
        } else if pos == 0 {
            Some(Ternary::False)
        } else {
            Some(Ternary::Unknown) // 震荡
        }
    }

    /// 置信度（0..1）。历史样本不足 5 个时返回 0（永远弃权）。
    pub fn confidence_of(&self, key: u32) -> f32 {
        let Some(hist) = self.history.get(&key) else {
            return 0.0;
        };
        let samples = hist.ring.samples();
        if samples < 5 {
            return 0.0;
        }
        hist.ring.matches() as f32 / samples as f32
    }

    /// 记录一次「内核预测 vs 原版实际」的对拍结果。
    ///
    /// `consistent = true` 表示内核预测的 trit 和原版观察到的结果一致。
    pub fn record_outcome(&mut self, key: u32, consistent: bool) {
        let window = window_of(&self.config);
        let hist = self
            .history
            .entry(key)
            .or_insert_with(|| KeyHistory::new(window));
        hist.ring.push(consistent);
        if !consistent {
            hist.divergences = hist.divergences.saturating_add(1);
        }
    }

    /// 当前可预测键数。
    pub fn key_count(&self) -> usize {
        self.keys.len()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    const KEY: u32 = (2 << 28) | (7 << 12) | 8;

    fn fork(window: u32) -> WorldProjectionFork {
        let config = TakeoverConfig {
            confidence_threshold: 0.0,
            confidence_window: window,
            ..TakeoverConfig::default()
        };
        WorldProjectionFork::fork_from(&TritWorld::new(), config)
    }

    #[test]
    fn ring_confidence_matches_vec_semantics() {
        for window in [1u32, 3, 5, 30, 64] {
            let mut fork = fork(window);
            // 与旧 `Vec<bool>` + drain 的语义逐项对拍。
            let mut reference: Vec<bool> = Vec::new();
            let mut state = 0x9E37_79B9_u32;
            for _ in 0..200 {
                state = state.wrapping_mul(1_664_525).wrapping_add(1_013_904_223);
                let consistent = state & 1 == 0;
                reference.push(consistent);
                if reference.len() > window as usize {
                    reference.drain(..reference.len() - window as usize);
                }
                fork.record_outcome(KEY, consistent);

                let expected_conf = if reference.len() < 5 {
                    0.0
                } else {
                    reference.iter().filter(|&&v| v).count() as f32 / reference.len() as f32
                };
                assert!(
                    (fork.confidence_of(KEY) - expected_conf).abs() < f32::EPSILON,
                    "window {window} 置信度不一致"
                );
            }
        }
    }

    #[test]
    fn ring_prediction_matches_vec_semantics() {
        for pattern in [
            vec![true; 6],
            vec![false; 6],
            vec![true, false, true, false, true, true],
        ] {
            let mut fork = fork(30);
            for &consistent in &pattern {
                fork.record_outcome(KEY, consistent);
            }
            let expected = if pattern.iter().all(|&v| v) {
                Some(Ternary::True)
            } else if pattern.iter().all(|&v| !v) {
                Some(Ternary::False)
            } else {
                Some(Ternary::Unknown)
            };
            assert_eq!(fork.predict(KEY), expected);
        }
    }

    #[test]
    fn refresh_only_rebuilds_on_structural_change() {
        let mut world = TritWorld::new();
        world.set(KEY, Ternary::True);
        let mut fork = WorldProjectionFork::fork_from(&world, TakeoverConfig::default());
        assert_eq!(fork.keys(), &[KEY]);

        world.set(KEY, Ternary::False); // 只改值，不重建
        fork.refresh_world(&world);
        assert_eq!(fork.keys(), &[KEY]);

        let other = (1 << 28) | (3 << 12) | 7;
        world.set(other, Ternary::True); // 新车道，重建
        fork.refresh_world(&world);
        assert_eq!(fork.keys(), &[other.min(KEY), other.max(KEY)]);
    }

    #[test]
    fn predict_all_into_reuses_buffer() {
        let mut world = TritWorld::new();
        world.set(KEY, Ternary::True);
        let mut fork = WorldProjectionFork::fork_from(&world, TakeoverConfig::default());
        for _ in 0..6 {
            fork.record_outcome(KEY, true);
        }
        let mut out = Vec::with_capacity(8);
        fork.predict_all_into(&mut out);
        assert_eq!(out.len(), 1);
        assert_eq!(out[0].0, KEY);
        assert_eq!(out[0].1, Ternary::True);
        let cap = out.capacity();
        fork.predict_all_into(&mut out);
        assert_eq!(out.capacity(), cap, "第二次不应扩容");
    }
}
