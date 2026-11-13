//! 内核接管系统（L1 轻接管 MVP）。
//!
//! 设计原则：
//! - **零开销 L0**：`RuntimeConfig.takeover == None` 时，整模块不会被 Rust 侧调起。
//!   `predictions` opcode 返回空 payload，C# 侧跳过接管。
//! - **fork 不持有主内核锁**：每次 predictions 调用从 `Runtime.world()` 克隆一份
//!   `BTreeMap<u32, Ternary>`（世界投影快照），fork 独立工作。
//! - **置信度 = 影子对拍延伸**：fork 的 `record_outcome` 被 C# 侧在对拍原版 vs 内核
//!   时调用，自动更新置信度表。
//!
//! Phase 1 只做**历史外推**预测（L1），不跑原版等价逻辑（那是 L2 trit-engine 的事）。

use std::collections::{BTreeMap, BTreeSet};

use trit_core::value::Ternary;

/// 接管模式。
///
/// Phase 1 只实现 L0（只观测）和 L1（PatchAfterOrig：原版先跑、内核事后修正）。
/// L2 及以上留给后续。
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default, serde::Serialize, serde::Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum TakeoverMode {
    /// 只观测（默认，DesktopHigh / DesktopLow）。
    #[default]
    ObserveOnly,
    /// L1 轻接管：原版先跑、内核事后修正（Android Mobile 默认）。
    PatchAfterOrig,
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

/// 世界投影 fork：独立快照 + 历史外推预测。
///
/// 每个键在 fork 里维护：
/// - 当前世界值（从主内核克隆）
/// - 过去 `confidence_window` 次"内核预测 vs 原版实际"结果
/// - 置信度 = 一致样本 / 窗口大小
#[derive(Clone, Debug)]
pub struct WorldProjectionFork {
    /// 世界投影（fork 独立副本，不影响主内核）。
    snapshot: BTreeMap<u32, Ternary>,

    /// 每个键的历史窗口：true = 内核预测和原版一致，false = 不一致。
    /// 长度上限 = `confidence_window`。
    history: BTreeMap<u32, Vec<bool>>,

    /// 每个键累计的不一致次数（超过 `divergence_tolerance` 后降级为只观测）。
    divergences: BTreeMap<u32, u32>,

    /// 配置（阈值 / 窗口 / 间隔）。
    config: TakeoverConfig,

    /// 已观察键集（用于 predict_all 遍历）。
    keys: BTreeSet<u32>,
}

impl WorldProjectionFork {
    /// 从主内核的世界投影创建 fork。
    ///
    /// `snapshot` 可以来自 `Runtime.world().clone()` 或任意 `BTreeMap<u32, Ternary>`。
    pub fn fork_from(snapshot: &BTreeMap<u32, Ternary>, config: TakeoverConfig) -> Self {
        let keys: BTreeSet<u32> = snapshot.keys().copied().collect();
        Self {
            snapshot: snapshot.clone(),
            history: BTreeMap::new(),
            divergences: BTreeMap::new(),
            config,
            keys,
        }
    }

    /// 从主内核世界投影刷新 fork 的世界值。
    /// 不重置历史 —— 置信度是长期跟踪，刷新世界值是每次 tick 都要做的。
    pub fn refresh_world(&mut self, snapshot: &BTreeMap<u32, Ternary>) {
        self.snapshot = snapshot.clone();
        self.keys = snapshot.keys().copied().collect();
    }

    /// 对每个可接管键跑历史外推预测。
    ///
    /// 预测规则（Phase 1 MVP）：
    /// - 置信度 < threshold → 弃权（Zero）
    /// - 历史最近 N 次都是 Pos → Pos
    /// - 历史最近 N 次都是 Neg → Neg
    /// - 震荡或样本不足 → 弃权
    ///
    /// 返回 key → 预测 trit 的列表（按 key 升序，便于 C# 侧按 LaneMap 查找）。
    pub fn predict_all(&self) -> Vec<(u32, Ternary)> {
        let mut preds = Vec::new();

        for key in &self.keys {
            let Some(trit) = self.predict(*key) else {
                continue;
            };
            if trit != Ternary::Unknown {
                preds.push((*key, trit));
            }
        }

        preds
    }

    /// 预测单个键的下一 tick 值（三值：Neg / Zero 弃权 / Pos）。
    pub fn predict(&self, key: u32) -> Option<Ternary> {
        // 置信度门禁：不一致次数超标 → 永远弃权
        if self
            .divergences
            .get(&key)
            .map(|d| *d >= self.config.divergence_tolerance)
            .unwrap_or(false)
        {
            return Some(Ternary::Unknown);
        }

        let conf = self.confidence_of(key);
        if conf < self.config.confidence_threshold {
            return Some(Ternary::Unknown);
        }

        // 历史外推：连续 Pos → Pos，连续 Neg → Neg
        let hist = self.history.get(&key)?;
        let window = self.config.confidence_window as usize;
        let start = hist.len().saturating_sub(window);
        let recent = &hist[start..];

        if recent.len() < 3 {
            return Some(Ternary::Unknown); // 样本太少
        }

        let pos_count = recent.iter().filter(|&&v| v).count();
        let neg_count = recent.len() - pos_count;

        if pos_count == recent.len() {
            Some(Ternary::True)
        } else if neg_count == recent.len() {
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
        if hist.len() < 5 {
            return 0.0;
        }
        let matches = hist.iter().filter(|&&v| v).count();
        matches as f32 / hist.len() as f32
    }

    /// 记录一次"内核预测 vs 原版实际"的对拍结果。
    ///
    /// `consistent = true` 表示内核预测的 trit 和原版观察到的结果一致。
    pub fn record_outcome(&mut self, key: u32, consistent: bool) {
        let hist = self
            .history
            .entry(key)
            .or_insert_with(|| Vec::with_capacity(self.config.confidence_window as usize));

        hist.push(consistent);

        // 超窗口时丢掉最旧样本
        let window = self.config.confidence_window as usize;
        if hist.len() > window {
            hist.drain(..hist.len() - window);
        }

        if !consistent {
            let d = self.divergences.entry(key).or_insert(0);
            *d = d.saturating_add(1);
        }
    }

    /// 当前可预测键数。
    pub fn key_count(&self) -> usize {
        self.keys.len()
    }
}
