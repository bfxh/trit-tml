//! GPU 后端：能力探测 + 兼容性自检 + 三值点积/矩阵乘的 GPU/CPU 双路径。
//!
//! 设计约束（真机教训驱动，别改）：
//! - **玩家可关**：`policy` 三态（`auto` / `off` / `on`），`off` 时一次都不碰 GPU；
//! - **兼容优先于性能**：拿到 adapter 只是第一步，还要拿 CPU 位并行结果做**自检对拍**，
//!   对不上就永久关闭 GPU 并把原因写进 `reason`（宁可慢，不能算错）；
//! - **小任务不上 GPU**：上游实测「1M 规模以下 CPU 更快，dispatch 延迟占主导」，
//!   所以规模小于 `min_trits` 时主动走 CPU；
//! - CPU 侧镜像全部调用上游 `trit-gpu` 的位并行内核（`dot_words` / `matmul_words`），
//!   本文件不重新实现任何三值算法。

use serde::{Deserialize, Serialize};

/// 默认「值得上 GPU」的规模下限（trit 数）。
///
/// 依据上游 `trit-gpu` 的实测：1M 规模下 dispatch 延迟占主导，GPU 反而比 CPU 慢；
/// 要到「数据大到装不进缓存」才拉开差距。
pub const DEFAULT_MIN_TRITS: usize = 1 << 20;

/// 单个缓冲区上限：`wgpu` 默认 limits 的 storage binding 是 128 MiB，这里留一半。
const MAX_BUFFER_BYTES: usize = 64 << 20;

/// 提交给 GPU 前抽查对拍的输出个数（只抽前几个，全量对拍会把 GPU 的收益吃光）。
#[cfg(feature = "gpu")]
const VERIFY_SAMPLES: usize = 8;

/// GPU 使用策略。
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum GpuPolicy {
    /// 可用就用，但小任务仍走 CPU。
    #[default]
    Auto,
    /// 完全不用 GPU。
    Off,
    /// 可用就尽量用（仍然要求自检对拍通过）。
    On,
}

/// GPU 配置（运行时配置 JSON 的 `gpu` 段）。
#[derive(Debug, Clone, Copy, PartialEq, Eq, Deserialize)]
#[serde(default)]
pub struct GpuConfig {
    /// 使用策略。
    pub policy: GpuPolicy,
    /// 小于该 trit 数走 CPU。
    pub min_trits: usize,
}

impl Default for GpuConfig {
    fn default() -> Self {
        Self {
            policy: GpuPolicy::Auto,
            min_trits: DEFAULT_MIN_TRITS,
        }
    }
}

/// GPU 后端状态（ABI `GPU_STATUS` 的响应体）。
#[derive(Debug, Clone, Serialize)]
pub struct GpuStatus {
    /// 构建带 `gpu` feature **且**探测到 adapter **且**自检对拍一致。
    pub available: bool,
    /// `available` 且策略允许且没被自检永久关闭。
    pub enabled: bool,
    /// 当前策略。
    pub policy: GpuPolicy,
    /// 设备名（未探测到为空）。
    pub adapter: String,
    /// 后端（Vulkan / Metal / Dx12 …）。
    pub backend: String,
    /// 本次构建是否带 `gpu` feature。
    pub built_with_gpu: bool,
    /// 不可用/被关闭的原因；可用时为空串。
    pub reason: String,
    /// 最后一次自检的 CPU 位并行点积结果。
    pub cpu_dot: i64,
    /// 最后一次自检的 GPU 点积结果。
    pub gpu_dot: i64,
    /// 自检对拍是否一致。
    pub matches: bool,
    /// 当前规模门槛。
    pub min_trits: usize,
    /// 自检得到的 GPU 三值点积耗时（ns/trit）；未探测为 0。
    pub ns_per_trit: f64,
    /// 交给后端判定的任务数。
    pub jobs: u64,
    /// 真正跑在 GPU 上的任务数。
    pub gpu_jobs: u64,
    /// 回退到 CPU 的次数（含自检失败）。
    pub fallbacks: u64,
}

impl Default for GpuStatus {
    fn default() -> Self {
        Self {
            available: false,
            enabled: false,
            policy: GpuPolicy::Auto,
            adapter: String::new(),
            backend: String::new(),
            built_with_gpu: cfg!(feature = "gpu"),
            reason: String::new(),
            cpu_dot: 0,
            gpu_dot: 0,
            matches: false,
            min_trits: DEFAULT_MIN_TRITS,
            ns_per_trit: 0.0,
            jobs: 0,
            gpu_jobs: 0,
            fallbacks: 0,
        }
    }
}

/// 一条双导轨缓冲：`pos` 存 +1 位、`neg` 存 -1 位，`Zero` 两位都不置位。
///
/// 打包成两个 `u32` 数组（每个字装 32 个 trit）后，一次 `popcount` 就能算出
/// 32 个三值乘积——这是三进制扫描能做位并行的根本原因。
#[derive(Debug, Clone, Copy)]
pub struct DualRail<'a> {
    /// +1 导轨。
    pub pos: &'a [u32],
    /// -1 导轨。
    pub neg: &'a [u32],
}

/// GPU 后端：状态 + 计数器 + 双路径矩阵乘。
#[derive(Debug)]
pub struct GpuBackend {
    status: GpuStatus,
    /// 是否已经探测过（探测要初始化 wgpu，不能在每次能力查询里做）。
    probed: bool,
    /// 自检失败后**永久**关闭，避免每帧重试。
    disabled: bool,
}

impl GpuBackend {
    /// 按配置构造；**不做任何探测**（探测可能耗时，留给显式调用）。
    pub fn new(config: GpuConfig) -> Self {
        let reason = if cfg!(feature = "gpu") {
            String::new()
        } else {
            "构建时未启用 gpu feature（cargo build --features gpu）".to_string()
        };
        Self {
            status: GpuStatus {
                policy: config.policy,
                min_trits: config.min_trits,
                reason,
                ..GpuStatus::default()
            },
            probed: false,
            disabled: false,
        }
    }
    /// 当前策略。
    pub fn policy(&self) -> GpuPolicy {
        self.status.policy
    }

    /// 切换策略；`on` 但不可用时不会假装可用（只记原因）。
    pub fn set_policy(&mut self, policy: GpuPolicy) {
        self.status.policy = policy;
        if policy != GpuPolicy::Off && !self.status.available && self.status.reason.is_empty() {
            self.status.reason = "尚未探测（调用 GPU_STATUS 触发探测）".to_string();
        }
    }

    /// 设置规模门槛。
    pub fn set_min_trits(&mut self, min_trits: usize) {
        self.status.min_trits = min_trits;
    }

    /// 只探测一次；已探测过就什么都不做。
    pub fn ensure_probed(&mut self, trits: usize) {
        if self.probed {
            return;
        }
        self.probe(trits);
    }

    /// 强制重新探测（策略切换或玩家显式要求时用）。
    #[cfg_attr(not(feature = "gpu"), allow(unused_variables))]
    pub fn probe(&mut self, trits: usize) {
        self.probed = true;
        self.status.jobs = 0;
        self.status.gpu_jobs = 0;
        self.status.fallbacks = 0;
        self.disabled = false;

        if !cfg!(feature = "gpu") {
            self.status.available = false;
            self.status.reason =
                "构建时未启用 gpu feature（cargo build --features gpu）".to_string();
        } else {
            #[cfg(feature = "gpu")]
            {
                let trits = trits.max(1024);
                match trit_gpu::gpu::run(trits) {
                    Some(report) => {
                        self.status.adapter = report.adapter.clone();
                        self.status.backend = report.backend.clone();
                        self.status.cpu_dot = report.cpu;
                        self.status.gpu_dot = report.gpu;
                        self.status.matches = report.matches;
                        self.status.ns_per_trit = report.gpu_ns_per_trit;
                        if report.matches {
                            self.status.available = true;
                            self.status.reason.clear();
                        } else {
                            self.status.available = false;
                            self.disabled = true;
                            self.status.reason = format!(
                                "GPU 与 CPU 点积自检对拍不一致（cpu={} gpu={}），已永久回退 CPU",
                                report.cpu, report.gpu
                            );
                            self.status.fallbacks += 1;
                        }
                    }
                    None => {
                        self.status.available = false;
                        self.status.reason =
                            "没有可用的图形适配器（wgpu request_adapter 失败）".to_string();
                    }
                }
            }
        }
    }

    /// 状态快照（`enabled` 是按当前策略实时算出来的）。
    pub fn status(&self) -> GpuStatus {
        let mut status = self.status.clone();
        status.enabled = self.enabled();
        status
    }

    /// 当前是否允许使用 GPU。
    ///
    /// `available` 只有在带 `gpu` feature 的构建里才可能被置真（探测代码在 cfg 块里），
    /// 所以这里不需要再判一次 feature——避免出现"状态说可用、实际不能调"的二义。
    pub fn enabled(&self) -> bool {
        self.status.policy != GpuPolicy::Off && self.status.available && !self.disabled
    }

    /// 该规模是否值得上 GPU。
    pub fn should_use(&self, trits: usize) -> bool {
        if !self.enabled() {
            return false;
        }
        match self.status.policy {
            GpuPolicy::On => true,
            GpuPolicy::Auto => trits >= self.status.min_trits.max(1),
            GpuPolicy::Off => false,
        }
    }

    /// 记录一次回退；回退原因是「算错」时永久关闭。
    pub fn note_fallback(&mut self, reason: &str, permanent: bool) {
        self.status.fallbacks += 1;
        if permanent {
            self.disabled = true;
            self.status.available = false;
        }
        self.status.reason = reason.to_string();
    }

    /// 三值矩阵乘：GPU 优先，失败/不划算返回 `None`（调用方必须回退 CPU）。
    ///
    /// 参数布局与上游 `trit-gpu` 一致：`a` 是 `m` 行 × `words` 字的行主序双导轨，
    /// `b` 是 `n` 列 × `words` 字的转置行主序双导轨；返回 `m × n` 行主序。
    #[cfg_attr(not(feature = "gpu"), allow(unused_variables))]
    pub fn matmul(
        &mut self,
        m: usize,
        n: usize,
        words: usize,
        a: DualRail<'_>,
        b: DualRail<'_>,
    ) -> Option<Vec<i64>> {
        if m == 0 || n == 0 || words == 0 {
            return None;
        }
        self.status.jobs += 1;
        let trits = m.saturating_mul(words).saturating_mul(32);
        if !self.should_use(trits) {
            return None;
        }
        // 单缓冲区过大时 wgpu 默认 limits 放不下，直接走 CPU（不记回退：这不是故障）。
        if m.saturating_mul(words).saturating_mul(4) > MAX_BUFFER_BYTES {
            return None;
        }

        #[cfg(feature = "gpu")]
        {
            let gpu = trit_gpu::forward::matmul_packed(m, n, words, a.pos, a.neg, b.pos, b.neg);
            let Some(gpu) = gpu else {
                self.note_fallback(
                    "GPU 矩阵乘返回空（设备丢失或 dispatch 失败），本次回退 CPU",
                    false,
                );
                return None;
            };
            if gpu.len() != m * n {
                self.note_fallback(&format!("GPU 返回长度 {} != {}", gpu.len(), m * n), true);
                return None;
            }
            if !self.verify(m, n, words, &gpu, a, b) {
                self.note_fallback("GPU 矩阵乘与 CPU 位并行结果不一致，已永久回退 CPU", true);
                return None;
            }
            self.status.gpu_jobs += 1;
            Some(gpu.into_iter().map(i64::from).collect())
        }

        #[cfg(not(feature = "gpu"))]
        {
            None
        }
    }

    /// 抽查前若干个输出与 CPU 位并行镜像对拍。
    #[cfg(feature = "gpu")]
    fn verify(
        &self,
        m: usize,
        n: usize,
        words: usize,
        gpu: &[i32],
        a: DualRail<'_>,
        b: DualRail<'_>,
    ) -> bool {
        let samples = VERIFY_SAMPLES.min(m * n);
        for row in 0..samples.min(m) {
            let a_base = row * words;
            let expected = trit_gpu::dot_words(
                &a.pos[a_base..a_base + words],
                &a.neg[a_base..a_base + words],
                &b.pos[..words],
                &b.neg[..words],
            );
            if i64::from(gpu[row * n]) != expected {
                return false;
            }
        }
        true
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn default_policy_is_auto_with_a_size_threshold() {
        let backend = GpuBackend::new(GpuConfig::default());
        assert_eq!(backend.policy(), GpuPolicy::Auto);
        assert_eq!(GpuConfig::default().min_trits, DEFAULT_MIN_TRITS);
    }

    #[test]
    fn off_policy_never_uses_gpu_even_when_available() {
        let mut backend = GpuBackend::new(GpuConfig::default());
        backend.set_policy(GpuPolicy::Off);
        assert!(!backend.enabled());
        assert!(!backend.should_use(1 << 24));
        assert!(!backend.status().enabled);
    }

    #[test]
    fn auto_policy_skips_small_workloads() {
        let mut backend = GpuBackend::new(GpuConfig::default());
        // 没有 gpu feature 时 available 永远为 false，这里只验证门槛逻辑本身。
        backend.status.available = true;
        backend.probed = true;
        assert!(!backend.should_use(1024));
        assert!(backend.should_use(DEFAULT_MIN_TRITS));
    }

    #[test]
    fn on_policy_ignores_the_threshold() {
        let mut backend = GpuBackend::new(GpuConfig {
            policy: GpuPolicy::On,
            min_trits: DEFAULT_MIN_TRITS,
        });
        backend.status.available = true;
        backend.probed = true;
        assert!(backend.should_use(16));
    }

    #[test]
    fn a_mismatch_disables_the_backend_permanently() {
        let mut backend = GpuBackend::new(GpuConfig::default());
        backend.status.available = true;
        backend.note_fallback("对拍不一致", true);
        assert!(!backend.enabled());
        assert_eq!(backend.status().fallbacks, 1);
        assert!(backend.status().reason.contains("对拍不一致"));
    }

    #[test]
    fn a_transient_fallback_keeps_the_backend_usable() {
        let mut backend = GpuBackend::new(GpuConfig::default());
        backend.status.available = true;
        backend.note_fallback("设备丢失", false);
        assert!(backend.enabled());
        assert_eq!(backend.status().fallbacks, 1);
    }

    #[test]
    fn matmul_returns_none_without_the_gpu_feature() {
        let mut backend = GpuBackend::new(GpuConfig::default());
        backend.status.available = true;
        let out = backend.matmul(
            2,
            1,
            1,
            DualRail {
                pos: &[1, 2],
                neg: &[0, 0],
            },
            DualRail {
                pos: &[3],
                neg: &[0],
            },
        );
        assert!(out.is_none() || cfg!(feature = "gpu"));
    }

    #[test]
    fn empty_shapes_are_rejected_before_touching_the_gpu() {
        let mut backend = GpuBackend::new(GpuConfig::default());
        backend.status.available = true;
        let empty = DualRail { pos: &[], neg: &[] };
        let one = DualRail {
            pos: &[0],
            neg: &[0],
        };
        let zero = DualRail {
            pos: &[0],
            neg: &[0],
        };
        assert!(backend.matmul(0, 1, 1, empty, zero).is_none());
        assert!(backend.matmul(1, 0, 1, one, empty).is_none());
        assert!(backend.matmul(1, 1, 0, one, empty).is_none());
    }
}
