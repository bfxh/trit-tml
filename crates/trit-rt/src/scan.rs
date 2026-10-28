//! 扫描任务：世界三值张量上的直方图 / 模式匹配 / 密度热点 / 点积。
//!
//! 三进制的优势在这里是真的能用上，不是口号：
//! - **模式匹配 = 一次三值矩阵乘**。滑动窗口的每个窗口就是矩阵 A 的一行，
//!   待找图案是矩阵 B 的唯一一列；双导轨打包后 GPU 上一次 `matmul` 全部算完，
//!   CPU 侧用上游的位并行 `dot_words` 分块做（32 trit 打进一个 `u32`，一次
//!   `popcount` 出 32 个乘积）。
//! - **零元素不参与运算**：`Zero` 在双导轨里两根轨都不置位，区域越"空"越快。
//! - 长扫描实现 `ResumableTask`，在块边界 `Yield`——高优先级任务来了能被调度器
//!   抢占（状态保留，之后从原状态继续），取消也只在块边界生效，不会拖住游戏主线程。

use std::sync::atomic::{AtomicBool, AtomicU32, AtomicU64, Ordering};
use std::sync::{Arc, Mutex};

use serde::{Deserialize, Serialize};
use trit_schedule::{Progress, Resources, ResumableTask, StepContext};

use crate::budget::memory_units;
use crate::error::RtError;
use crate::gpu::{DualRail, GpuBackend};

/// 一个 32 位字装多少 trit（与上游 `trit-gpu` 的双导轨布局一致）。
const TRITS_PER_WORD: usize = 32;

/// 单侧矩阵缓冲区的上限；超过就别建 A 矩阵了，直接 CPU 分块扫。
const MAX_PACKED_BYTES: usize = 64 << 20;

/// 扫描种类。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ScanKind {
    /// 统计 neg / zero / pos 各多少。
    Histogram,
    /// 滑动窗口匹配图案（三值点积打分）。
    Pattern,
    /// 分块统计非零密度，返回热点块。
    Density,
    /// 两个三值向量的点积。
    Dot,
}

impl ScanKind {
    /// JSON 里的名字。
    pub fn as_str(self) -> &'static str {
        match self {
            Self::Histogram => "histogram",
            Self::Pattern => "pattern",
            Self::Density => "density",
            Self::Dot => "dot",
        }
    }

    /// 解析 JSON 里的名字（大小写不敏感）。
    pub fn parse(text: &str) -> Option<Self> {
        match text.trim().to_ascii_lowercase().as_str() {
            "histogram" => Some(Self::Histogram),
            "pattern" => Some(Self::Pattern),
            "density" => Some(Self::Density),
            "dot" => Some(Self::Dot),
            _ => None,
        }
    }

    /// 规范化 kind 文本：已知种类给**规范名**，未知种类一律给 <c>unknown</c>。
    ///
    /// 原生与托管两条路径必须给出**逐字节相同**的 `kind` 字段，
    /// 否则影子差分会一直报不一致（同一份请求、两个内核、两个答案）。
    ///
    /// 未知种类**不保留原文**：原文只出现在错误文案里（`未知扫描 kind：xxx`），
    /// 机器可读的 `kind` 字段必须收敛到固定取值，否则枚举会无限膨胀。
    pub fn canonical_text(raw: &str) -> String {
        match ScanKind::parse(raw) {
            Some(kind) => kind.as_str().to_string(),
            None => UNKNOWN_KIND_TEXT.to_string(),
        }
    }
}

/// 未知 kind 在 `kind` 字段里的规范取值。
///
/// 与托管回退内核的 `ScanKindName` 逐字对齐。
pub const UNKNOWN_KIND_TEXT: &str = "unknown";

/// 扫描任务提交（ABI `SCAN_SUBMIT` 的请求体）。
///
/// **解码必须宽容**：字段缺失、`null`、类型不对都要有确定的行为，
/// 否则同一份载荷在原生与托管两条路径上一个能跑、一个直接失败。
/// 宽容规则与托管回退内核逐条对齐：
///
/// | 字段 | 缺失 / `null` / 类型不对 | 正常值 |
/// |---|---|---|
/// | `owner` | 空串（随后归一化为 `unknown`） | 归属名 |
/// | `kind` | 空串（→ 未知 kind → 任务失败） | 种类名 |
/// | `trits` / `pattern` | 空数组 | trit 数组（越界元素钳到 255） |
/// | `threshold` / `chunk` / `priority` / `top` | `0` | int32 范围内的数 |
/// | `oneshot` | `false` | `true` / `false` |
///
/// 数值一律按 **int32 语义**接收：超出 int32 或不是整数的按 `0` 处理
/// （与托管侧的 `TryGetInt32` 一致），而不是让解码整个失败。
#[derive(Debug, Clone, Deserialize)]
#[serde(default)]
pub struct ScanRequest {
    /// 计费/配额归属（空串归为 `unknown`）。
    #[serde(deserialize_with = "lenient_string")]
    pub owner: String,
    /// 扫描种类名。
    #[serde(deserialize_with = "lenient_string")]
    pub kind: String,
    /// 每个元素一个 trit：`0 = Neg(-1)`、`1 = Zero(0)`、`2 = Pos(+1)`。
    #[serde(deserialize_with = "lenient_trit_array")]
    pub trits: Vec<u8>,
    /// `pattern` / `dot` 用的第二个向量（同样每元素一个 trit）。
    ///
    /// 允许写成 `null`（等价于空数组）——C# 兼容壳在 `Pattern` 为 `null` 时
    /// 就是这么发的；原生内核与托管内核必须对同一份载荷给出同样的结果。
    #[serde(deserialize_with = "lenient_trit_array")]
    pub pattern: Vec<u8>,
    /// 命中最低分；`<= 0` 表示满分（逐元素全等）。
    #[serde(deserialize_with = "lenient_int32")]
    pub threshold: i64,
    /// 分步粒度 / 密度块大小；`<= 0` 表示用任务池配置里的默认值。
    #[serde(deserialize_with = "lenient_int32")]
    pub chunk: i64,
    /// 一步跑完（不分步、不可抢占）。
    #[serde(deserialize_with = "lenient_bool")]
    pub oneshot: bool,
    /// 数值越大越优先。
    #[serde(deserialize_with = "lenient_int32_narrow")]
    pub priority: i32,
    /// `density` 返回的块数上限；`<= 0` 表示用默认值 16。
    #[serde(deserialize_with = "lenient_int32")]
    pub top: i64,
}

/// 单步最多处理多少元素（请求里的 `chunk` 上限）。
///
/// 与托管回退内核的 `MaxScanChunk` 一致：超大值会让"一步跑完"变成事实上的
/// 不可抢占，也会在托管侧算 step 数时溢出。
pub const MAX_SCAN_CHUNK: i64 = 1 << 20;

/// 宽容解码一个字符串字段：只有 JSON 字符串原样保留，其余一律当空串。
///
/// 与托管回退内核的 `JsonString` 逐字对齐（字段不存在 / `null` / 非字符串
/// 三种情况都压成空串）。
fn lenient_string<'de, D>(deserializer: D) -> Result<String, D::Error>
where
    D: serde::Deserializer<'de>,
{
    match serde_json::Value::deserialize(deserializer)? {
        serde_json::Value::String(text) => Ok(text),
        _ => Ok(String::new()),
    }
}

/// 宽容解码一个 trit 数组：越界元素钳到 `255`（托管侧 `JsonByteArray` 同口径）。
///
/// 非数组 → 解码失败（`TT_ERR_DECODE`），与托管侧抛 `WireException` 的结果一致。
fn lenient_trit_array<'de, D>(deserializer: D) -> Result<Vec<u8>, D::Error>
where
    D: serde::Deserializer<'de>,
{
    match serde_json::Value::deserialize(deserializer)? {
        serde_json::Value::Null => Ok(Vec::new()),
        serde_json::Value::Array(items) => Ok(items.iter().map(clamp_trit_byte).collect()),
        _ => Err(serde::de::Error::custom("trit 数组字段必须是数组")),
    }
}

/// 单个 trit 字节：能落进 `0..=255` 的整数原样保留，其余（含 `null`、字符串、
/// 小数、超 int32 的数）一律钳到 `255`——随后会被校验判为「非法 trit 值」。
fn clamp_trit_byte(value: &serde_json::Value) -> u8 {
    match value.as_i64() {
        Some(number) => u8::try_from(number).unwrap_or(u8::MAX),
        None => u8::MAX,
    }
}

/// 宽容解码一个 int32 语义的数值字段：能装进 int32 的整数原样保留，其余按 `0`。
fn lenient_int32<'de, D>(deserializer: D) -> Result<i64, D::Error>
where
    D: serde::Deserializer<'de>,
{
    let value = serde_json::Value::deserialize(deserializer)?;
    Ok(i64::from(int32_or_zero(&value)))
}

/// 宽容解码 int32 字段（窄版，直接得到 `i32`）。
fn lenient_int32_narrow<'de, D>(deserializer: D) -> Result<i32, D::Error>
where
    D: serde::Deserializer<'de>,
{
    let value = serde_json::Value::deserialize(deserializer)?;
    Ok(int32_or_zero(&value))
}

/// int32 口径取值：能装进 int32 的整数原样保留，其余（含小数、字符串、
/// 超 int32 的数、`null`）一律按 `0`——与托管侧的 `TryGetInt32` 一致。
fn int32_or_zero(value: &serde_json::Value) -> i32 {
    match value.as_i64() {
        Some(number) => i32::try_from(number).unwrap_or(0),
        None => 0,
    }
}

/// 宽容解码布尔字段：只有 JSON `true` 算 `true`，其余一律 `false`。
fn lenient_bool<'de, D>(deserializer: D) -> Result<bool, D::Error>
where
    D: serde::Deserializer<'de>,
{
    Ok(serde_json::Value::deserialize(deserializer)? == serde_json::Value::Bool(true))
}

/// 归属名归一化：**去首尾空白 → 空则归为 <c>unknown</c>**。
///
/// 提交侧（[`ScanRequest::normalized_owner`]）、轮询侧的 owner 过滤、
/// 托管回退内核都必须用**同一个规则**：否则「以空 owner 提交」的任务
/// 在轮询时用 `"unknown"` 过滤就找不到，用 `""` 过滤才找得到——
/// 两条路径的口径会分叉，同一份数据在原生与托管上表现不同。
///
/// 去空白是必须的：模组层按 `owner.Trim()` 记账，而内核按原样记账，
/// 不统一就会出现「提交时叫 `MyMod`、过滤时叫 `MyMod `」而永远匹配不上。
pub fn normalize_owner(owner: &str) -> String {
    let trimmed = owner.trim();
    if trimmed.is_empty() {
        "unknown".to_string()
    } else {
        trimmed.to_string()
    }
}

impl Default for ScanRequest {
    /// **空载荷的语义**：字段全缺 = 什么都没填。
    ///
    /// `owner` 空串随后被归一化成 `unknown`；`kind` 空串意味着**未知种类**
    /// → 任务失败（而不是悄悄当成 `histogram` 跑起来）。
    /// 托管回退内核对空载荷的处理与之完全一致。
    fn default() -> Self {
        Self {
            owner: String::new(),
            kind: String::new(),
            trits: Vec::new(),
            pattern: Vec::new(),
            threshold: 0,
            chunk: 0,
            oneshot: false,
            priority: 0,
            top: 0,
        }
    }
}

impl ScanRequest {
    /// 解析种类；未知种类报错并带上**用户传入的原文**。
    pub fn scan_kind(&self) -> Result<ScanKind, RtError> {
        ScanKind::parse(&self.kind)
            .ok_or_else(|| RtError::InvalidInput(format!("未知扫描 kind：{}", self.kind.trim())))
    }

    /// 校验：种类已知；trit 值只能是 0/1/2；`pattern` / `dot` 必须有第二个向量。
    ///
    /// **校验顺序与错误文案必须与托管回退内核逐字一致**：
    /// 先种类、再 trit 值、最后缺 pattern——顺序不同，同一份坏请求在两条路径上
    /// 会报出不同的原因，影子差分立刻炸。
    pub fn validate(&self) -> Result<ScanKind, RtError> {
        let kind = self.scan_kind()?;
        if let Some(bad) = self.trits.iter().find(|byte| **byte > 2) {
            return Err(RtError::InvalidInput(format!(
                "非法 trit 值：{bad}，每个元素只能是 0、1 或 2"
            )));
        }
        if let Some(bad) = self.pattern.iter().find(|byte| **byte > 2) {
            return Err(RtError::InvalidInput(format!(
                "非法 trit 值：{bad}，每个元素只能是 0、1 或 2"
            )));
        }
        if matches!(kind, ScanKind::Pattern | ScanKind::Dot) && self.pattern.is_empty() {
            return Err(RtError::InvalidInput(format!(
                "{} 扫描需要非空 pattern",
                kind.as_str()
            )));
        }
        Ok(kind)
    }

    /// 归一化后的归属名：空串归为 <c>unknown</c>。
    ///
    /// 与托管回退内核逐字对齐（那边是 <c>string.IsNullOrEmpty(owner) ? "unknown" : owner</c>）：
    /// 空串与 <c>"unknown"</c> 必须落到<b>同一个</b> owner 上，否则同一批任务在两条路径上
    /// 会记到两个不同的账本里，预算快照对不上。
    pub fn normalized_owner(&self) -> String {
        normalize_owner(&self.owner)
    }

    /// 载荷实际占用的字节数（`trits` + `pattern`）。
    ///
    /// 队列里的每个任务都把这份载荷**整个**拿在手里，直到它跑完——
    /// 排队本身就要吃内存，所以任务池按这个数做「在飞载荷」预算。
    pub fn payload_bytes(&self) -> usize {
        self.trits.len().saturating_add(self.pattern.len())
    }

    /// 任务占用的资源：1 个 CPU 槽位 + 载荷字节数换算的内存单位（至少 1）。
    pub fn resources(&self) -> Resources {
        Resources::new(1, memory_units(self.payload_bytes()).max(1))
    }
}

/// 任务结局。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum JobState {
    /// 正常完成。
    Done,
    /// 被取消（协作式，只在块边界生效）。
    Cancelled,
    /// 失败。
    Failed,
}

impl JobState {
    /// JSON 里的名字。
    pub fn as_str(self) -> &'static str {
        match self {
            Self::Done => "done",
            Self::Cancelled => "cancelled",
            Self::Failed => "failed",
        }
    }

    /// 从 JSON 名字反解；未知回退 `Failed`。
    pub fn from_name(name: &str) -> Self {
        match name {
            "done" => Self::Done,
            "cancelled" => Self::Cancelled,
            _ => Self::Failed,
        }
    }
}

/// 任务对象被上游执行器消费后，只剩这个槽能把结局与 step 数带回来。
#[derive(Debug, Default)]
pub struct JobSlots {
    outcome: AtomicU32,
    steps: AtomicU64,
}

impl JobSlots {
    /// 结局；`None` = 一步都没跑。
    pub fn outcome(&self) -> Option<JobState> {
        match self.outcome.load(Ordering::Relaxed) {
            0 => None,
            1 => Some(JobState::Done),
            2 => Some(JobState::Cancelled),
            _ => Some(JobState::Failed),
        }
    }

    /// 已执行的 step 数。
    pub fn steps(&self) -> u64 {
        self.steps.load(Ordering::Relaxed)
    }

    /// 写入结局；只能写一次（先写先算，避免抢占恢复后被覆盖）。
    pub fn set(&self, state: JobState, steps: u64) {
        let code = match state {
            JobState::Done => 1,
            JobState::Cancelled => 2,
            JobState::Failed => 3,
        };
        self.steps.store(steps, Ordering::Relaxed);
        let _ = self
            .outcome
            .compare_exchange(0, code, Ordering::Relaxed, Ordering::Relaxed);
    }
}

/// 一条已结束的任务记录（ABI `JOB_POLL` 的返回元素）。
#[derive(Debug, Clone, Serialize)]
pub struct JobRecord {
    /// 任务 id。
    pub id: u64,
    /// 归属 mod。
    pub owner: String,
    /// 种类名。
    pub kind: String,
    /// 结局。
    pub state: String,
    /// 结果（各 kind 自己的 JSON 对象；失败时为空对象）。
    ///
    /// 存**原始 JSON 文本**而不是 `serde_json::Value`：
    /// 密度是定点小数（`1.000000`），而 `Value` 里的浮点数会被重新格式化
    /// （serde_json 最短表示 → `1.0`），与托管回退内核产出的字节不同。
    /// 原始文本能保证两侧**逐字节一致**。
    pub result: Box<serde_json::value::RawValue>,
    /// 失败原因；成功时为空串。
    pub error: String,
    /// 执行的 step 数。
    pub steps: u64,
    /// 实际用的后端：`gpu` / `cpu` / `managed`。
    pub backend: String,
}

impl JobRecord {
    /// 构造一条记录。
    ///
    /// `kind` 收**文本**而不是 [`ScanKind`]：失败任务的种类可能根本不是
    /// 已知种类（此时应写成 `unknown`），枚举表达不了这种状态。
    pub fn new(
        id: u64,
        owner: &str,
        kind: &str,
        state: JobState,
        steps: u64,
        backend: &str,
    ) -> Self {
        Self {
            id,
            owner: owner.to_string(),
            kind: kind.to_string(),
            state: state.as_str().to_string(),
            result: empty_result(),
            error: String::new(),
            steps,
            backend: backend.to_string(),
        }
    }

    /// 结果对象的**解析视图**（内部断言与调用方检查用）。
    pub fn result_value(&self) -> serde_json::Value {
        serde_json::from_str(self.result.get()).unwrap_or(serde_json::Value::Null)
    }

    /// 是否已完成/取消/失败。
    pub fn is_terminal(&self) -> bool {
        true
    }
}

/// 密度的 JSON 值：**定点 6 位小数**，按**原始文本**嵌进响应。
///
/// 为什么不能直接丢一个 `f64` 让 serde_json 序列化：serde_json 用最短表示，
/// `1.0` 会写成 `1.0`、`0.000001` 会写成 `1e-6`；而托管回退内核写的是
/// `1.000000`。同一份请求在两条路径上产出的字节不同，影子差分就永远报分歧——
/// 而且分歧点只是数字格式，肉眼看两边「意思一样」，极难定位。
///
/// 定点 6 位还有一个好处：**跨平台、跨实现完全确定**（不依赖浮点最短表示算法）。
fn density_text(density: f64) -> Box<serde_json::value::RawValue> {
    let text = format!("{density:.6}");
    serde_json::value::RawValue::from_string(text).unwrap_or_else(|_| empty_result())
}

/// 把任意可序列化对象转成**原始 JSON 文本**。
fn raw_of<T: serde::Serialize>(value: &T) -> Box<serde_json::value::RawValue> {
    serde_json::value::to_raw_value(value).unwrap_or_else(|_| empty_result())
}

/// 失败任务的结果对象（空对象）。
fn empty_result() -> Box<serde_json::value::RawValue> {
    serde_json::value::RawValue::from_string("{}".to_string()).expect("空对象是合法 JSON")
}

/// 直方图结果（字段顺序 = 托管侧的写入顺序）。
#[derive(Debug, Clone, Serialize)]
struct HistogramResult {
    histogram: HistogramCounts,
}

/// 直方图的三个计数。
#[derive(Debug, Clone, Serialize)]
struct HistogramCounts {
    neg: u64,
    zero: u64,
    pos: u64,
}

/// 点积结果。
#[derive(Debug, Clone, Serialize)]
struct DotResult {
    dot: i64,
    length: usize,
}

/// 模式匹配的一条命中。
#[derive(Debug, Clone, Serialize)]
struct PatternMatch {
    index: usize,
    score: i64,
}

/// 模式匹配结果。
#[derive(Debug, Clone, Serialize)]
struct PatternResult {
    matches: Vec<PatternMatch>,
    scanned: usize,
    backend: String,
}

/// 密度热点块。
#[derive(Debug, Clone, Serialize)]
struct DensityBlock {
    block: usize,
    index: usize,
    nonzero: u64,
    /// 定点 6 位小数的**原始文本**（浮点数存不住这个格式）。
    density: Box<serde_json::value::RawValue>,
}

/// 密度扫描结果。
#[derive(Debug, Clone, Serialize)]
struct DensityResult {
    blocks: Vec<DensityBlock>,
    blocks_total: usize,
}

/// 一个 trit 字节（0/1/2）转成算术值（-1/0/+1）。
fn to_value(byte: u8) -> i32 {
    match byte {
        0 => -1,
        1 => 0,
        _ => 1,
    }
}

/// 从整段双导轨打包里切出一个窗口（可跨字，尾部按 `len` 掩码）。
///
/// `out_*` 长度必须是 `len.div_ceil(TRITS_PER_WORD)`。
fn window_into(
    pos: &[u32],
    neg: &[u32],
    start: usize,
    len: usize,
    out_pos: &mut [u32],
    out_neg: &mut [u32],
) {
    let words = len.div_ceil(TRITS_PER_WORD);
    for word in 0..words {
        let bit_start = start + word * TRITS_PER_WORD;
        let src = bit_start / TRITS_PER_WORD;
        let shift = bit_start % TRITS_PER_WORD;
        let low_pos = pos.get(src).copied().unwrap_or(0);
        let low_neg = neg.get(src).copied().unwrap_or(0);
        let (value_pos, value_neg) = if shift == 0 {
            (low_pos, low_neg)
        } else {
            let high_pos = pos.get(src + 1).copied().unwrap_or(0);
            let high_neg = neg.get(src + 1).copied().unwrap_or(0);
            (
                (low_pos >> shift) | (high_pos << (TRITS_PER_WORD - shift)),
                (low_neg >> shift) | (high_neg << (TRITS_PER_WORD - shift)),
            )
        };
        // 末字只保留属于本窗口的位；零填充位与 0 相与不贡献 popcount。
        let remaining = len.saturating_sub(word * TRITS_PER_WORD);
        let mask = if remaining >= TRITS_PER_WORD {
            u32::MAX
        } else {
            (1u32 << remaining) - 1
        };
        out_pos[word] = value_pos & mask;
        out_neg[word] = value_neg & mask;
    }
}

/// 一个可分步的扫描任务。
pub struct ScanJob {
    id: u64,
    owner: String,
    kind: ScanKind,
    values: Vec<i32>,
    pattern: Vec<i32>,
    pattern_packed: Option<(Vec<u32>, Vec<u32>)>,
    packed: Option<(Vec<u32>, Vec<u32>)>,
    threshold: i64,
    chunk: usize,
    top: usize,
    cursor: usize,
    steps: u64,
    backend: String,
    /// 直方图累加器：[neg, zero, pos]。
    hist: [u64; 3],
    dot: i64,
    /// 密度累加器：(块号, 非零数)。
    blocks: Vec<(usize, u64)>,
    /// 命中：(起始下标, 分数)。
    matches: Vec<(usize, i64)>,
    scratch_pos: Vec<u32>,
    scratch_neg: Vec<u32>,
    /// GPU 已经试过（避免每步重试）。
    gpu_tried: bool,
    cancel: Arc<AtomicBool>,
    slots: Arc<JobSlots>,
    sink: Arc<Mutex<Vec<JobRecord>>>,
    gpu: Arc<Mutex<GpuBackend>>,
}

impl ScanJob {
    /// 构造一个任务；`chunk` 为 0 时用 `default_chunk`。
    #[allow(clippy::too_many_arguments)]
    pub fn new(
        id: u64,
        request: &ScanRequest,
        kind: ScanKind,
        default_chunk: usize,
        cancel: Arc<AtomicBool>,
        slots: Arc<JobSlots>,
        sink: Arc<Mutex<Vec<JobRecord>>>,
        gpu: Arc<Mutex<GpuBackend>>,
    ) -> Self {
        // `chunk <= 0` → 用任务池配置的默认粒度；`chunk > MAX_SCAN_CHUNK` → 夹到上限。
        // 语义与托管回退内核（`Chunk > 0 ? Chunk : 4096`，再夹上限）逐项对齐。
        let chunk = if request.chunk <= 0 {
            default_chunk.clamp(1, MAX_SCAN_CHUNK as usize)
        } else {
            usize::try_from(request.chunk)
                .unwrap_or(MAX_SCAN_CHUNK as usize)
                .clamp(1, MAX_SCAN_CHUNK as usize)
        };
        let values: Vec<i32> = request.trits.iter().copied().map(to_value).collect();
        let pattern: Vec<i32> = request.pattern.iter().copied().map(to_value).collect();
        // 满分 = 图案里非零位置的个数：逐元素全等就是这个分数。
        let full_score = pattern.iter().filter(|value| **value != 0).count() as i64;
        let threshold = if request.threshold <= 0 {
            full_score
        } else {
            request.threshold
        };
        Self {
            id,
            owner: request.owner.clone(),
            kind,
            values,
            pattern,
            pattern_packed: None,
            packed: None,
            threshold,
            chunk,
            top: if request.top <= 0 {
                16
            } else {
                usize::try_from(request.top).unwrap_or(16)
            },
            cursor: 0,
            steps: 0,
            backend: "cpu".to_string(),
            hist: [0; 3],
            dot: 0,
            blocks: Vec::new(),
            matches: Vec::new(),
            scratch_pos: Vec::new(),
            scratch_neg: Vec::new(),
            gpu_tried: false,
            cancel,
            slots,
            sink,
            gpu,
        }
    }

    /// 一步跑完（不分步、不可抢占），返回结果记录。
    pub fn run_oneshot(mut self) -> JobRecord {
        let context = StepContext {
            cancel: trit_schedule::CancelToken::new(),
        };
        // 不分步：chunk 直接取全长，一步必完。
        self.chunk = self.values.len().max(1);
        loop {
            if self.cancel.load(Ordering::Relaxed) {
                let _ = self.finish(JobState::Cancelled, String::new());
                break;
            }
            if self.step(&context) == Progress::Done {
                break;
            }
            if self.slots.outcome().is_some() {
                break;
            }
        }
        // 结果已经写进 sink，这里取回来交还调用方。
        self.take_record().unwrap_or_else(|| {
            JobRecord::new(
                self.id,
                &self.owner,
                self.kind.as_str(),
                JobState::Failed,
                self.steps,
                &self.backend,
            )
        })
    }

    /// 从 sink 里取回自己那条记录（oneshot 用）。
    fn take_record(&self) -> Option<JobRecord> {
        let mut guard = self
            .sink
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner());
        let index = guard.iter().position(|record| record.id == self.id)?;
        Some(guard.remove(index))
    }

    /// 收尾：写记录、标记完成。
    fn finish(&mut self, state: JobState, error: String) -> Progress {
        let mut record = JobRecord::new(
            self.id,
            &self.owner,
            self.kind.as_str(),
            state,
            self.steps,
            &self.backend,
        );
        record.error = error;
        if state == JobState::Done {
            record.result = self.result_json();
        }
        self.push(record);
        Progress::Done
    }

    fn push(&self, record: JobRecord) {
        let mut guard = self
            .sink
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner());
        let steps = record.steps;
        let state = JobState::from_name(&record.state);
        guard.push(record);
        self.slots.set(state, steps);
    }

    /// 按种类拼结果 JSON。
    ///
    /// 用**结构体**而不是 `json!` 拼：结构体的字段按声明顺序序列化，
    /// `json!` 拼出来的 `Map` 在默认配置下按**字母序**排——
    /// 托管回退内核按写入顺序输出，两边字节不同，影子差分就永远报分歧。
    fn result_json(&self) -> Box<serde_json::value::RawValue> {
        match self.kind {
            ScanKind::Histogram => raw_of(&HistogramResult {
                histogram: HistogramCounts {
                    neg: self.hist[0],
                    zero: self.hist[1],
                    pos: self.hist[2],
                },
            }),
            ScanKind::Dot => raw_of(&DotResult {
                dot: self.dot,
                length: self.values.len().min(self.pattern.len()),
            }),
            ScanKind::Density => {
                let mut ranked: Vec<(usize, u64, f64)> = self
                    .blocks
                    .iter()
                    .map(|(block, nonzero)| {
                        let total = self
                            .chunk
                            .min(self.values.len().saturating_sub(block * self.chunk));
                        let density = if total == 0 {
                            0.0
                        } else {
                            *nonzero as f64 / total as f64
                        };
                        (*block, *nonzero, (density * 1e6).round() / 1e6)
                    })
                    .collect();
                // 密度降序，密度相同块号升序：输出必须确定，不能依赖排序稳定性。
                ranked.sort_by(|left, right| {
                    right
                        .2
                        .partial_cmp(&left.2)
                        .unwrap_or(std::cmp::Ordering::Equal)
                        .then_with(|| left.0.cmp(&right.0))
                });
                ranked.truncate(self.top);
                let blocks = ranked
                    .into_iter()
                    .map(|(block, nonzero, density)| DensityBlock {
                        block,
                        index: block * self.chunk,
                        nonzero,
                        density: density_text(density),
                    })
                    .collect();
                raw_of(&DensityResult {
                    blocks,
                    blocks_total: self.blocks.len(),
                })
            }
            ScanKind::Pattern => {
                let matches = self
                    .matches
                    .iter()
                    .map(|(index, score)| PatternMatch {
                        index: *index,
                        score: *score,
                    })
                    .collect();
                raw_of(&PatternResult {
                    matches,
                    scanned: self.scanned_windows(),
                    backend: self.backend.clone(),
                })
            }
        }
    }

    /// 窗口总数（用于 `scanned`）。
    fn scanned_windows(&self) -> usize {
        if self.pattern.is_empty() || self.values.len() < self.pattern.len() {
            return 0;
        }
        self.values.len() - self.pattern.len() + 1
    }

    /// 走一遍 GPU（pattern 专用）；成功返回 true。
    fn try_gpu_pattern(&mut self) -> bool {
        self.gpu_tried = true;
        let total = self.scanned_windows();
        if total == 0 {
            return false;
        }
        let words = self.pattern.len().div_ceil(TRITS_PER_WORD);
        // A 矩阵两条导轨：先算字节数，超限就不建（宁可慢，不能把内存吃穿）。
        if total.saturating_mul(words).saturating_mul(4) > MAX_PACKED_BYTES {
            return false;
        }
        let trits = total.saturating_mul(self.pattern.len());
        {
            let backend = self
                .gpu
                .lock()
                .unwrap_or_else(|poisoned| poisoned.into_inner());
            if !backend.should_use(trits) {
                return false;
            }
        }

        if self.packed.is_none() {
            let (pos, neg) = trit_gpu::pack_dual_rail(&self.values);
            self.packed = Some((pos, neg));
        }
        if self.pattern_packed.is_none() {
            let (pos, neg) = trit_gpu::pack_dual_rail(&self.pattern);
            self.pattern_packed = Some((pos, neg));
        }
        // 图案很小（也就几个字），直接复制一份交给 GPU 侧；值缓冲有几百 KB 到几 MB，
        // **绝对不能整块 clone**——旧实现每次尝试 GPU 都把整个双导轨缓冲复制一遍，
        // 光这两行内存拷贝就比 GPU 的计算本身还贵。
        let (pattern_pos, pattern_neg) = match self.pattern_packed.as_ref() {
            Some((pos, neg)) => (pos.clone(), neg.clone()),
            None => return false,
        };
        let Some((packed_pos, packed_neg)) = self.packed.as_ref() else {
            return false;
        };

        // 分块展开窗口：旧实现一次性建 `total × words` 的 im2col 缓冲
        // （4M 窗口 × 2 字 = 32MB），既吃内存又把 CPU 缓存全冲掉，还会顶到
        // 上游的 dispatch 网格上限。按块展开后峰值只剩 `block × words`（几百 KB），
        // 而且每块单独 dispatch，永远不会越界。
        const WINDOW_BLOCK: usize = 1 << 15;
        let mut scores = Vec::with_capacity(total);
        let mut block_pos: Vec<u32> = Vec::with_capacity(WINDOW_BLOCK * words);
        let mut block_neg: Vec<u32> = Vec::with_capacity(WINDOW_BLOCK * words);
        self.scratch_pos.resize(words, 0);
        self.scratch_neg.resize(words, 0);

        let mut window = 0usize;
        while window < total {
            let block_end = (window + WINDOW_BLOCK).min(total);
            block_pos.clear();
            block_neg.clear();
            for index in window..block_end {
                window_into(
                    packed_pos,
                    packed_neg,
                    index,
                    self.pattern.len(),
                    &mut self.scratch_pos,
                    &mut self.scratch_neg,
                );
                block_pos.extend_from_slice(&self.scratch_pos);
                block_neg.extend_from_slice(&self.scratch_neg);
            }

            let rows = block_end - window;
            let block = {
                let mut backend = self
                    .gpu
                    .lock()
                    .unwrap_or_else(|poisoned| poisoned.into_inner());
                backend.matmul(
                    rows,
                    1,
                    words,
                    DualRail {
                        pos: &block_pos,
                        neg: &block_neg,
                    },
                    DualRail {
                        pos: &pattern_pos,
                        neg: &pattern_neg,
                    },
                )
            };
            // 任何一块被拒（形状不划算 / 设备丢失 / panic）就整趟回 CPU：
            // 半 GPU 半 CPU 的分数不可比，也违背「同一请求结果必须一致」。
            let Some(block_scores) = block else {
                return false;
            };
            scores.extend_from_slice(&block_scores);
            window = block_end;
        }

        self.matches = scores
            .into_iter()
            .enumerate()
            .filter(|(_, score)| *score >= self.threshold)
            .collect();
        self.backend = "gpu".to_string();
        self.cursor = total;
        true
    }

    /// CPU 分块扫 `chunk` 个窗口。
    fn step_pattern_cpu(&mut self) {
        let total = self.scanned_windows();
        if total == 0 {
            self.cursor = total;
            return;
        }
        let words = self.pattern.len().div_ceil(TRITS_PER_WORD);
        if self.packed.is_none() {
            let (pos, neg) = trit_gpu::pack_dual_rail(&self.values);
            self.packed = Some((pos, neg));
        }
        if self.pattern_packed.is_none() {
            let (pos, neg) = trit_gpu::pack_dual_rail(&self.pattern);
            self.pattern_packed = Some((pos, neg));
        }
        self.scratch_pos.resize(words, 0);
        self.scratch_neg.resize(words, 0);
        let (pattern_pos, pattern_neg) = match self.pattern_packed.as_ref() {
            Some((pos, neg)) => (pos.clone(), neg.clone()),
            None => return,
        };
        let end = (self.cursor + self.chunk).min(total);
        {
            let Some((packed_pos, packed_neg)) = self.packed.as_ref() else {
                return;
            };
            for window in self.cursor..end {
                window_into(
                    packed_pos,
                    packed_neg,
                    window,
                    self.pattern.len(),
                    &mut self.scratch_pos,
                    &mut self.scratch_neg,
                );
                let score = trit_gpu::dot_words(
                    &self.scratch_pos,
                    &self.scratch_neg,
                    &pattern_pos,
                    &pattern_neg,
                );
                if score >= self.threshold {
                    self.matches.push((window, score));
                }
            }
        }
        self.cursor = end;
    }
}

impl ResumableTask for ScanJob {
    fn step(&mut self, ctx: &StepContext) -> Progress {
        if self.cancel.load(Ordering::Relaxed) || ctx.cancel.is_cancelled() {
            return self.finish(JobState::Cancelled, String::new());
        }
        self.steps += 1;

        match self.kind {
            ScanKind::Histogram => {
                let end = (self.cursor + self.chunk).min(self.values.len());
                for value in &self.values[self.cursor..end] {
                    match *value {
                        -1 => self.hist[0] += 1,
                        0 => self.hist[1] += 1,
                        _ => self.hist[2] += 1,
                    }
                }
                self.cursor = end;
                if self.cursor >= self.values.len() {
                    return self.finish(JobState::Done, String::new());
                }
            }
            ScanKind::Dot => {
                let len = self.values.len().min(self.pattern.len());
                let end = (self.cursor + self.chunk).min(len);
                for index in self.cursor..end {
                    self.dot += i64::from(self.values[index]) * i64::from(self.pattern[index]);
                }
                self.cursor = end;
                if self.cursor >= len {
                    return self.finish(JobState::Done, String::new());
                }
            }
            ScanKind::Density => {
                if self.cursor >= self.values.len() {
                    return self.finish(JobState::Done, String::new());
                }
                let end = (self.cursor + self.chunk).min(self.values.len());
                let nonzero = self.values[self.cursor..end]
                    .iter()
                    .filter(|value| **value != 0)
                    .count() as u64;
                let block = self.blocks.len();
                self.blocks.push((block, nonzero));
                self.cursor = end;
                if self.cursor >= self.values.len() {
                    return self.finish(JobState::Done, String::new());
                }
            }
            ScanKind::Pattern => {
                if !self.gpu_tried && self.try_gpu_pattern() {
                    return self.finish(JobState::Done, String::new());
                }
                self.step_pattern_cpu();
                if self.cursor >= self.scanned_windows() {
                    return self.finish(JobState::Done, String::new());
                }
            }
        }

        Progress::Yield
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn request(kind: ScanKind, trits: Vec<u8>, pattern: Vec<u8>) -> ScanRequest {
        ScanRequest {
            owner: "test".to_string(),
            kind: kind.as_str().to_string(),
            trits,
            pattern,
            ..ScanRequest::default()
        }
    }

    fn sink() -> (Arc<Mutex<Vec<JobRecord>>>, Arc<AtomicBool>, Arc<JobSlots>) {
        (
            Arc::new(Mutex::new(Vec::new())),
            Arc::new(AtomicBool::new(false)),
            Arc::new(JobSlots::default()),
        )
    }

    fn gpu() -> Arc<Mutex<GpuBackend>> {
        Arc::new(Mutex::new(
            GpuBackend::new(crate::gpu::GpuConfig::default()),
        ))
    }

    fn run_to_completion(job: &mut ScanJob) -> JobRecord {
        let token = trit_schedule::CancelToken::new();
        let ctx = StepContext { cancel: token };
        for _ in 0..10_000 {
            if job.step(&ctx) == Progress::Done {
                break;
            }
        }
        let guard = job
            .sink
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner());
        guard.last().cloned().expect("任务应产出一条记录")
    }

    fn job(
        kind: ScanKind,
        trits: Vec<u8>,
        pattern: Vec<u8>,
        chunk: usize,
    ) -> (ScanJob, Arc<Mutex<Vec<JobRecord>>>) {
        let (sink, cancel, completed) = sink();
        let mut req = request(kind, trits, pattern);
        req.chunk = i64::try_from(chunk).unwrap_or(i64::MAX);
        let kind = req.validate().expect("请求合法");
        let job = ScanJob::new(
            1,
            &req,
            kind,
            4096,
            cancel,
            completed,
            Arc::clone(&sink),
            gpu(),
        );
        (job, sink)
    }

    #[test]
    fn kind_names_round_trip_and_are_case_insensitive() {
        for kind in [
            ScanKind::Histogram,
            ScanKind::Pattern,
            ScanKind::Density,
            ScanKind::Dot,
        ] {
            assert_eq!(ScanKind::parse(kind.as_str()), Some(kind));
            assert_eq!(
                ScanKind::parse(&kind.as_str().to_uppercase()),
                Some(kind),
                "大小写不敏感"
            );
        }
        assert_eq!(ScanKind::parse("nope"), None);
    }

    #[test]
    fn an_unknown_kind_is_canonicalized_to_unknown() {
        assert_eq!(ScanKind::canonical_text("histogram"), "histogram");
        assert_eq!(ScanKind::canonical_text("HISTOGRAM"), "histogram");
        assert_eq!(ScanKind::canonical_text("  histogram  "), "histogram");
        // 未知种类收敛到固定取值，原文只出现在错误文案里。
        assert_eq!(ScanKind::canonical_text("weird"), UNKNOWN_KIND_TEXT);
        assert_eq!(ScanKind::canonical_text(""), UNKNOWN_KIND_TEXT);
    }

    #[test]
    fn missing_fields_decode_to_an_empty_request() {
        let req: ScanRequest = serde_json::from_slice(b"{}").expect("空对象可解码");
        assert_eq!(req.owner, "");
        assert_eq!(req.kind, "");
        assert!(req.trits.is_empty());
        assert!(req.pattern.is_empty());
        assert_eq!(req.threshold, 0);
        assert_eq!(req.chunk, 0);
        assert!(!req.oneshot);
        assert_eq!(req.priority, 0);
        assert_eq!(req.top, 0);
        // 缺 kind = 未知种类 → 任务失败，而不是悄悄当成 histogram 跑起来。
        let error = req.validate().expect_err("缺 kind 应失败");
        assert!(error.reason().contains("未知扫描 kind"));
        // owner 空串归一化成 unknown。
        assert_eq!(req.normalized_owner(), "unknown");
    }

    #[test]
    fn null_fields_are_treated_as_missing() {
        let req: ScanRequest = serde_json::from_slice(
            br#"{"owner":null,"kind":null,"trits":null,"pattern":null,"threshold":null,"chunk":null,"oneshot":null,"priority":null,"top":null}"#,
        )
        .expect("null 字段可解码");
        assert_eq!(req.owner, "");
        assert_eq!(req.kind, "");
        assert!(req.trits.is_empty());
        assert!(req.pattern.is_empty());
        assert_eq!(req.threshold, 0);
        assert_eq!(req.chunk, 0);
        assert!(!req.oneshot);
        assert_eq!(req.priority, 0);
        assert_eq!(req.top, 0);
    }

    #[test]
    fn out_of_range_trit_values_are_clamped_to_255() {
        let req: ScanRequest = serde_json::from_slice(
            br#"{"kind":"histogram","trits":[0,1,2,300,-1,1.5,"x",null,true]}"#,
        )
        .expect("可解码");
        // 越界值钳到 255，随后由校验判为「非法 trit 值」——
        // 而不是让解码整个失败（托管侧 `JsonByteArray` 同口径）。
        assert_eq!(req.trits, vec![0, 1, 2, 255, 255, 255, 255, 255, 255]);
        let error = req.validate().expect_err("越界 trit 应失败");
        assert!(error.reason().contains("非法 trit 值"));
    }

    #[test]
    fn numbers_outside_int32_are_read_as_zero() {
        let req: ScanRequest = serde_json::from_slice(
            br#"{"threshold":3000000000,"chunk":-5,"top":1.5,"priority":99999999999}"#,
        )
        .expect("可解码");
        // 超 int32 → 0；int32 范围内的负数原样保留（后续被夹取成默认值）。
        assert_eq!(req.threshold, 0);
        assert_eq!(req.chunk, -5);
        assert_eq!(req.top, 0);
        assert_eq!(req.priority, 0);
    }

    #[test]
    fn a_non_boolean_oneshot_is_false() {
        let req: ScanRequest = serde_json::from_slice(br#"{"oneshot":"yes"}"#).expect("可解码");
        assert!(!req.oneshot);
        let req: ScanRequest = serde_json::from_slice(br#"{"oneshot":true}"#).expect("可解码");
        assert!(req.oneshot);
    }

    #[test]
    fn a_non_array_trit_field_is_a_decode_error() {
        assert!(serde_json::from_slice::<ScanRequest>(br#"{"trits":"abc"}"#).is_err());
        assert!(serde_json::from_slice::<ScanRequest>(br#"{"pattern":{"a":1}}"#).is_err());
    }

    #[test]
    fn a_non_string_owner_is_read_as_empty() {
        let req: ScanRequest = serde_json::from_slice(br#"{"owner":123}"#).expect("可解码");
        assert_eq!(req.owner, "");
        assert_eq!(req.normalized_owner(), "unknown");
    }

    #[test]
    fn illegal_trit_values_are_rejected() {
        let req = request(ScanKind::Histogram, vec![0, 1, 2, 3], vec![]);
        assert!(req.validate().is_err());
        let req = request(ScanKind::Pattern, vec![1], vec![255]);
        assert!(req.validate().is_err());
    }

    #[test]
    fn pattern_and_dot_require_a_non_empty_pattern() {
        let req = request(ScanKind::Pattern, vec![1, 2], vec![]);
        assert!(req.validate().is_err());
        let req = request(ScanKind::Dot, vec![1, 2], vec![]);
        assert!(req.validate().is_err());
    }

    #[test]
    fn histogram_counts_all_three_states() {
        // 0=Neg, 1=Zero, 2=Pos → 两个 -1、三个 0、一个 +1
        let (mut job, _sink) = job(ScanKind::Histogram, vec![0, 0, 1, 1, 1, 2], vec![], 2);
        let record = run_to_completion(&mut job);
        assert_eq!(record.state, "done");
        assert_eq!(record.result_value()["histogram"]["neg"], 2);
        assert_eq!(record.result_value()["histogram"]["zero"], 3);
        assert_eq!(record.result_value()["histogram"]["pos"], 1);
    }

    #[test]
    fn histogram_of_empty_input_is_all_zero() {
        let (mut job, _sink) = job(ScanKind::Histogram, vec![], vec![], 4096);
        let record = run_to_completion(&mut job);
        assert_eq!(record.result_value()["histogram"]["zero"], 0);
        assert_eq!(record.state, "done");
    }

    #[test]
    fn dot_truncates_to_the_shorter_vector() {
        // 注意 trit 编码：0 = Neg(-1)、1 = Zero(0)、2 = Pos(+1)。
        // trits [2,2,0] = (+1,+1,-1)，pattern [2,0,2] = (+1,-1,+1)
        // → (+1)(+1) + (+1)(-1) + (-1)(+1) = 1 - 1 - 1 = -1
        let (mut job, _sink) = job(ScanKind::Dot, vec![2, 2, 0], vec![2, 0, 2], 2);
        let record = run_to_completion(&mut job);
        assert_eq!(record.result_value()["dot"], -1);
        assert_eq!(record.result_value()["length"], 3);
    }

    #[test]
    fn zero_trits_contribute_nothing_to_the_dot_product() {
        // Zero(1) 在双导轨里两根轨都不置位：乘任何值都是 0。
        let (mut job, _sink) = job(ScanKind::Dot, vec![1, 1, 1], vec![2, 0, 2], 8);
        let record = run_to_completion(&mut job);
        assert_eq!(record.result_value()["dot"], 0);
    }

    #[test]
    fn dot_of_shorter_second_vector_uses_the_common_prefix() {
        let (mut job, _sink) = job(ScanKind::Dot, vec![2, 2, 2], vec![2, 2], 8);
        let record = run_to_completion(&mut job);
        assert_eq!(record.result_value()["dot"], 2);
        assert_eq!(record.result_value()["length"], 2);
    }

    #[test]
    fn pattern_matches_an_exact_window() {
        // 数据里第 2 个窗口正好等于图案 [+1,-1,0]
        let data = vec![0, 0, 2, 0, 1, 1, 1];
        let (mut job, _sink) = job(ScanKind::Pattern, data, vec![2, 0, 1], 4);
        let record = run_to_completion(&mut job);
        let result = record.result_value();
        let matches = result["matches"].as_array().expect("matches 是数组");
        assert_eq!(matches.len(), 1, "只应命中一个窗口：{matches:?}");
        assert_eq!(matches[0]["index"], 2);
        assert_eq!(matches[0]["score"], 2, "两个非零位置全等 = 满分 2");
        assert_eq!(record.result_value()["scanned"], 5);
    }

    #[test]
    fn pattern_across_word_boundaries_matches_the_scalar_reference() {
        // 40 个元素 > 32：窗口跨双导轨字边界，验证移位切片没写错。
        let mut data = Vec::new();
        for index in 0..40u8 {
            data.push(index % 3);
        }
        // 在下标 30 处嵌一个 [2,2,2]（三个 +1）
        data[30] = 2;
        data[31] = 2;
        data[32] = 2;
        let (mut job, _sink) = job(ScanKind::Pattern, data.clone(), vec![2, 2, 2], 8);
        let record = run_to_completion(&mut job);
        let result = record.result_value();
        let matches = result["matches"].as_array().expect("matches 是数组");
        // 手算参考：逐窗口标量点积
        let pattern: Vec<i32> = vec![1, 1, 1];
        let values: Vec<i32> = data.iter().copied().map(to_value).collect();
        let mut expected: Vec<(usize, i64)> = Vec::new();
        for window in 0..=values.len() - 3 {
            let score: i64 = (0..3)
                .map(|offset| i64::from(values[window + offset]) * i64::from(pattern[offset]))
                .sum();
            if score >= 3 {
                expected.push((window, score));
            }
        }
        let actual: Vec<(usize, i64)> = matches
            .iter()
            .map(|entry| {
                (
                    entry["index"].as_u64().unwrap_or_default() as usize,
                    entry["score"].as_i64().unwrap_or_default(),
                )
            })
            .collect();
        assert_eq!(actual, expected, "跨字窗口必须与标量参考一致");
    }

    #[test]
    fn pattern_longer_than_the_data_matches_nothing() {
        let (mut job, _sink) = job(ScanKind::Pattern, vec![2, 2], vec![2, 2, 2], 8);
        let record = run_to_completion(&mut job);
        assert_eq!(
            record.result_value()["matches"].as_array().map(Vec::len),
            Some(0)
        );
        assert_eq!(record.result_value()["scanned"], 0);
    }

    #[test]
    fn density_ranks_the_hottest_blocks_first() {
        // 块大小 3；trit 编码 0 = Neg、1 = Zero、2 = Pos。
        // 块0 = [Zero, Zero, Pos] → 1 个非零；块1 = [Pos, Neg, Neg] → 3 个；
        // 块2 = [Neg, Zero, Neg] → 2 个。排序后 1(3/3)、2(2/3)、0(1/3)。
        let data = vec![1, 1, 2, 2, 0, 0, 0, 1, 0];
        let (mut job, _sink) = job(ScanKind::Density, data, vec![], 3);
        let record = run_to_completion(&mut job);
        let result = record.result_value();
        let blocks = result["blocks"].as_array().expect("blocks 是数组");
        assert_eq!(record.result_value()["blocks_total"], 3);
        assert_eq!(blocks[0]["block"], 1);
        assert_eq!(blocks[1]["block"], 2);
        assert_eq!(blocks[2]["block"], 0);
        assert_eq!(blocks[0]["nonzero"], 3);
        assert_eq!(blocks[1]["nonzero"], 2);
        assert_eq!(blocks[2]["nonzero"], 1);
        assert!((blocks[0]["density"].as_f64().unwrap_or_default() - 1.0).abs() < 1e-9);
        assert!((blocks[1]["density"].as_f64().unwrap_or_default() - 0.666667).abs() < 1e-9);
    }

    #[test]
    fn density_top_limits_the_output() {
        let data = vec![2; 32];
        let (mut job, _sink) = job(ScanKind::Density, data, vec![], 2);
        let req = ScanRequest {
            top: 3,
            ..ScanRequest::default()
        };
        let _ = &req;
        let record = run_to_completion(&mut job);
        let result = record.result_value();
        let blocks = result["blocks"].as_array().expect("blocks 是数组");
        assert_eq!(record.result_value()["blocks_total"], 16);
        assert!(blocks.len() <= 16);
    }

    #[test]
    fn a_cancelled_job_stops_at_the_next_chunk_boundary() {
        let (mut job, _sink) = job(ScanKind::Histogram, vec![0; 1000], vec![], 2);
        let token = trit_schedule::CancelToken::new();
        let ctx = StepContext { cancel: token };
        let _ = job.step(&ctx);
        job.cancel.store(true, Ordering::Relaxed);
        let progress = job.step(&ctx);
        assert_eq!(progress, Progress::Done);
        let guard = job
            .sink
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner());
        assert_eq!(
            guard.last().map(|record| record.state.clone()),
            Some("cancelled".to_string())
        );
    }

    #[test]
    fn chunked_steps_sum_to_the_whole_input() {
        // 分步不能漏算：块大小 3，12 个元素，直方图必须还是 4/4/4
        let mut data = Vec::new();
        for _ in 0..4 {
            data.extend_from_slice(&[0, 1, 2]);
        }
        let (mut job, _sink) = job(ScanKind::Histogram, data, vec![], 3);
        let record = run_to_completion(&mut job);
        assert_eq!(record.result_value()["histogram"]["neg"], 4);
        assert_eq!(record.result_value()["histogram"]["zero"], 4);
        assert_eq!(record.result_value()["histogram"]["pos"], 4);
        assert_eq!(record.steps, 4, "12 个元素 / 块大小 3 = 4 步");
    }

    #[test]
    fn resources_account_for_the_payload_size() {
        let mut req = request(ScanKind::Histogram, vec![1; 3 << 20], vec![]);
        assert!(req.resources().memory >= 3);
        req.trits = vec![1; 8];
        assert_eq!(req.resources().cpu, 1);
        assert_eq!(req.resources().memory, 1, "小载荷至少算 1 个单位");
    }

    #[test]
    fn oneshot_runs_without_yielding() {
        let (sink, cancel, completed) = sink();
        let mut req = request(ScanKind::Histogram, vec![0, 1, 2], vec![]);
        req.oneshot = true;
        let kind = req.validate().expect("合法");
        let job = ScanJob::new(
            7,
            &req,
            kind,
            4096,
            cancel,
            completed,
            Arc::clone(&sink),
            gpu(),
        );
        let record = job.run_oneshot();
        assert_eq!(record.state, "done");
        assert_eq!(record.id, 7);
        assert_eq!(record.result_value()["histogram"]["neg"], 1);
        // 结果已经取回，sink 里不应残留
        assert!(
            sink.lock()
                .unwrap_or_else(|poisoned| poisoned.into_inner())
                .is_empty()
        );
    }
}
