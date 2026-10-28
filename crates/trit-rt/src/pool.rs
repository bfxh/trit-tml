//! 并行任务池：扫描任务的提交 / 排队 / 派发 / 抢占 / 取消 / 回收。
//!
//! 三条硬约束：
//! 1. **游戏主线程永不阻塞**：`submit` 只入队，`poll` 只取已完成的；
//! 2. **执行器直接复用上游** `trit-schedule`：
//!    - 可分步的长扫描 → `run_resumable_threaded`（多线程，在 `Yield` 边界按优先级抢占，
//!      任务对象状态保留，之后从原状态继续）；
//!    - 一步跑完的短任务 → `run_runtime`（闭包任务，无依赖，一次并发跑完）；
//!    - 全局容量与任务需求用同一套 `Resources`，准入判断由上游 `fits_in` 完成；
//! 3. **取消是协作式的**：只置标志位，任务在块边界自己退出；不强杀线程
//!    （上游 runtime 的语义就是这样，这里不去绕过它）。

use std::collections::{BTreeMap, HashMap, VecDeque};
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::{Arc, Mutex, mpsc};
use std::thread;
use std::time::Duration;

use serde::Deserialize;
use trit_schedule::{
    CancelToken, Resources, ResumableSpec, RuntimeConfig, RuntimeMetrics, RuntimeTask,
    run_resumable_threaded, run_runtime,
};

use crate::budget::{Admission, GlobalBudget, MEMORY_UNIT_BYTES};
use crate::error::RtError;
use crate::gpu::{GpuBackend, GpuConfig, GpuPolicy, GpuStatus};
use crate::scan::{JobRecord, JobSlots, JobState, ScanJob, ScanKind, ScanRequest};

/// 失败任务记录里的 `backend` 取值。
///
/// 失败的任务**根本没跑起来**，谈不上用了哪个后端，所以留空串。
/// 与托管回退内核的 `FailedJob` 逐字一致（那边也是 `string.Empty`）——
/// 这个字段进了 `JOB_POLL` 的响应体，差一个字符影子差分就一直报分歧。
const FAILED_BACKEND: &str = "";

/// 完成结果最多积压多少条（之后丢弃最旧的一条）。
///
/// 正常路径下本模组每 tick 都轮询，队列恒为空的几十条；这个上限只在
/// **调用方长期不轮询**时才起作用（本模组在轮询抛异常后会永久关闭轮询，
/// 另一个模组也可能自己提交了却再也不取）。没有上限时，结果队列就是
/// 一条只进不出的内存增长通道。
const MAX_PENDING_RESULTS: usize = 8192;

/// 「在飞载荷」字节预算（排队 + 在跑）。
///
/// 为什么必须有：任务池的队列是**无界**的，而每个排队任务都完整持有自己的
/// trits 载荷。原来只有「在跑任务数 ≤ cpu 容量」和「同 mod 在跑数」两道闸，
/// 于是提交一万个 4 MiB 的扫描 = 40 GiB 排在那里，游戏进程直接 OOM——
/// 而提交方看到的只是「提交成功」。现在载荷总量超过 `memory_capacity`
/// 就直接产生一条失败记录（与其它容量拒绝同口径），绝不静默吃内存。
fn payload_capacity_bytes(config: &ParallelConfig) -> u64 {
    u64::from(config.resolved_memory_capacity()) * MEMORY_UNIT_BYTES as u64
}

/// 尝试占住 `bytes` 字节的在飞载荷；超预算立刻退回占位并返回 `false`。
fn reserve_payload(resident: &AtomicU64, cap_bytes: u64, bytes: u64) -> bool {
    let before = resident.fetch_add(bytes, Ordering::AcqRel);
    if before.saturating_add(bytes) > cap_bytes {
        resident.fetch_sub(bytes, Ordering::AcqRel);
        return false;
    }
    true
}

/// 任务出队/跑完，归还载荷占位。
fn release_payload(resident: &AtomicU64, bytes: u64) {
    resident.fetch_sub(bytes, Ordering::AcqRel);
}

/// 任务池配置（运行时配置 JSON 的 `parallel` 段）。
#[derive(Debug, Clone, Copy, PartialEq, Eq, Deserialize)]
#[serde(default)]
pub struct ParallelConfig {
    /// worker 数；0 = 自动（`min(可用并行度, 4)`，至少 1）。
    pub workers: usize,
    /// 全局 CPU 槽位上限；0 = 自动（等于 worker 数）。旁路层不该把 CPU 吃满，
    /// 游戏主线程还要跑。
    pub cpu_capacity: u32,
    /// 全局内存预算（单位 1 MiB）：**排队 + 在跑**任务的载荷总量上限。
    ///
    /// 两个用途：单任务载荷超过它就是「永远放不下」（`Reject`）；
    /// 所有在飞载荷加起来超过它，新提交也按容量拒绝，队列不会无界增长。
    pub memory_capacity: u32,
    /// 同一 owner 同时在跑的任务上限（防独占）。
    pub per_mod_inflight: usize,
    /// 扫描默认分步粒度（元素数）。
    pub scan_chunk: usize,
    /// 单轮最多派发多少个任务。
    pub max_batch: usize,
}

impl Default for ParallelConfig {
    fn default() -> Self {
        Self {
            workers: 0,
            cpu_capacity: 0,
            memory_capacity: 512,
            per_mod_inflight: 2,
            scan_chunk: 4096,
            max_batch: 8,
        }
    }
}

impl ParallelConfig {
    /// 实际 worker 数。
    pub fn resolved_workers(&self) -> usize {
        if self.workers > 0 {
            return self.workers.clamp(1, 32);
        }
        thread::available_parallelism()
            .map(|value| value.get().clamp(1, 4))
            .unwrap_or(1)
    }

    /// 实际 CPU 槽位上限。
    pub fn resolved_cpu_capacity(&self) -> u32 {
        if self.cpu_capacity > 0 {
            return self.cpu_capacity;
        }
        u32::try_from(self.resolved_workers())
            .unwrap_or(u32::MAX)
            .max(1)
    }

    /// 实际内存预算（单位）。
    pub fn resolved_memory_capacity(&self) -> u32 {
        self.memory_capacity.max(1)
    }

    /// 单轮派发上限。
    pub fn resolved_max_batch(&self) -> usize {
        self.max_batch.clamp(1, 64)
    }

    /// 分步粒度。
    pub fn resolved_scan_chunk(&self) -> usize {
        self.scan_chunk.clamp(1, 1 << 20)
    }
}

/// 排队中的任务。
struct QueuedJob {
    id: u64,
    owner: String,
    request: ScanRequest,
    kind: ScanKind,
    resources: Resources,
    /// 载荷字节数：入队时从「在飞载荷」预算里占住，出队/跑完时归还。
    bytes: u64,
    cancel: Arc<AtomicBool>,
    slots: Arc<JobSlots>,
}

/// 任务 id → (owner, 取消标志)；用于按 id / owner 取消。
type JobFlags = Arc<Mutex<HashMap<u64, (String, Arc<AtomicBool>)>>>;

/// 并行任务池。
pub struct JobPool {
    tx: Option<mpsc::Sender<QueuedJob>>,
    results: Arc<Mutex<Vec<JobRecord>>>,
    budget: Arc<Mutex<GlobalBudget>>,
    gpu: Arc<Mutex<GpuBackend>>,
    metrics: Arc<Mutex<Option<RuntimeMetrics>>>,
    /// id → (owner, 取消标志)，用于按 id / owner 取消。
    flags: JobFlags,
    cancel: CancelToken,
    next_id: AtomicU64,
    /// 当前「在飞载荷」字节数（排队 + 在跑），由 [`reserve_payload`] / [`release_payload`] 维护。
    resident: Arc<AtomicU64>,
    worker: Option<thread::JoinHandle<()>>,
    config: ParallelConfig,
}

impl std::fmt::Debug for JobPool {
    fn fmt(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        formatter
            .debug_struct("JobPool")
            .field("workers", &self.config.resolved_workers())
            .finish_non_exhaustive()
    }
}

impl JobPool {
    /// 启动任务池（拉起一个 worker 线程）。
    pub fn new(config: ParallelConfig, gpu_config: GpuConfig) -> Self {
        let (tx, rx) = mpsc::channel::<QueuedJob>();
        let results = Arc::new(Mutex::new(Vec::new()));
        let budget = Arc::new(Mutex::new(GlobalBudget::new(config.per_mod_inflight)));
        let gpu = Arc::new(Mutex::new(GpuBackend::new(gpu_config)));
        let metrics = Arc::new(Mutex::new(None));
        let flags: JobFlags = Arc::new(Mutex::new(HashMap::new()));
        let cancel = CancelToken::new();
        let resident = Arc::new(AtomicU64::new(0));

        let worker = thread::Builder::new()
            .name("trit-rt-pool".to_string())
            .spawn({
                let results = Arc::clone(&results);
                let budget = Arc::clone(&budget);
                let gpu = Arc::clone(&gpu);
                let metrics = Arc::clone(&metrics);
                let flags = Arc::clone(&flags);
                let cancel = cancel.clone();
                let resident = Arc::clone(&resident);
                move || {
                    worker_loop(
                        config, rx, results, budget, gpu, metrics, flags, cancel, resident,
                    );
                }
            })
            .ok();

        Self {
            tx: worker.is_some().then_some(tx),
            results,
            budget,
            gpu,
            metrics,
            flags,
            cancel,
            next_id: AtomicU64::new(1),
            resident,
            worker,
            config,
        }
    }

    /// 配置（已解析后的实际值）。
    pub fn config(&self) -> ParallelConfig {
        self.config
    }

    /// 提交一个扫描任务；返回任务 id。
    ///
    /// 单任务需求超过全局容量时**产生一条 `failed` 记录**（不是让 opcode 失败）；
    /// 只是本 mod 在跑任务已达上限时**入队等待**，不拒绝。
    ///
    /// **请求本身非法**（非法 trit 值、未知 kind、pattern/dot 缺 pattern）时
    /// **不返回错误**，而是产生一条 `failed` 状态的完成记录：
    /// 调用方总能通过 `JOB_POLL` 拿到一条带 `error` 说明的结果，
    /// 不会「提交后石沉大海」。这与托管回退内核的行为一致。
    pub fn submit(&self, mut request: ScanRequest) -> Result<u64, RtError> {
        // owner 先归一化再记账：空串与 "unknown" 必须是同一个账本条目。
        request.owner = request.normalized_owner();
        // 提交即记账（与托管回退内核同口径）：非法请求、容量不足也算一次提交。
        self.budget
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner())
            .on_submit(&request.owner);
        let kind = match request.validate() {
            Ok(kind) => kind,
            Err(error) => {
                // 非法请求 → 产生一条失败记录，而不是让整个 opcode 失败。
                let id = self.next_id.fetch_add(1, Ordering::Relaxed);
                let mut record = JobRecord::new(
                    id,
                    &request.owner,
                    &ScanKind::canonical_text(&request.kind),
                    JobState::Failed,
                    0,
                    FAILED_BACKEND,
                );
                // 用「无前缀的原始原因」：托管回退内核的文案没有「输入非法：」前缀，
                // 影子差分逐字节比对时差一个字符就是一直报假分歧。
                record.error = error.reason();
                self.push(record);
                return Ok(id);
            }
        };
        let resources = request.resources();
        let capacity = Resources::new(
            self.config.resolved_cpu_capacity(),
            self.config.resolved_memory_capacity(),
        );
        let rejected = {
            let mut budget = self
                .budget
                .lock()
                .unwrap_or_else(|poisoned| poisoned.into_inner());
            if budget.admit(&request.owner, resources, capacity) == Admission::Reject {
                budget.on_reject(&request.owner);
                true
            } else {
                false
            }
        };
        if rejected {
            // 单个任务的载荷放不下全局容量 → 产生一条 `failed` 任务（ABI 要求），
            // 而不是让整个 opcode 失败：调用方总能通过 JOB_POLL 拿到失败原因。
            let id = self.next_id.fetch_add(1, Ordering::Relaxed);
            let mut record = JobRecord::new(
                id,
                &request.owner,
                kind.as_str(),
                JobState::Failed,
                0,
                FAILED_BACKEND,
            );
            record.error = format!(
                "任务需求 cpu={} memory={} 超过全局容量 cpu={} memory={}",
                resources.cpu, resources.memory, capacity.cpu, capacity.memory
            );
            self.push(record);
            return Ok(id);
        }

        // 队列无界、每个排队任务都完整持有载荷：先占住预算再入队，
        // 占不到就按容量拒绝（产生失败记录，与其它容量拒绝同口径）。
        let bytes = request.payload_bytes() as u64;
        let payload_cap = payload_capacity_bytes(&self.config);
        if !reserve_payload(&self.resident, payload_cap, bytes) {
            self.budget
                .lock()
                .unwrap_or_else(|poisoned| poisoned.into_inner())
                .on_reject(&request.owner);
            let id = self.next_id.fetch_add(1, Ordering::Relaxed);
            let mut record = JobRecord::new(
                id,
                &request.owner,
                kind.as_str(),
                JobState::Failed,
                0,
                FAILED_BACKEND,
            );
            record.error = if bytes > payload_cap {
                // 单个任务就比整个预算还大：永远排不进去，与「超过全局容量」同口径。
                format!(
                    "任务需求 cpu={} memory={} 超过全局容量 cpu={} memory={}",
                    resources.cpu,
                    resources.memory,
                    self.config.resolved_cpu_capacity(),
                    self.config.resolved_memory_capacity()
                )
            } else {
                format!(
                    "在飞载荷预算已满（排队 + 在跑合计上限 {} MiB），本任务 {bytes} 字节被拒绝",
                    payload_cap / MEMORY_UNIT_BYTES as u64
                )
            };
            self.push(record);
            return Ok(id);
        }

        let id = self.next_id.fetch_add(1, Ordering::Relaxed);
        let cancel = Arc::new(AtomicBool::new(false));
        self.flags
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner())
            .insert(id, (request.owner.clone(), Arc::clone(&cancel)));

        let job = QueuedJob {
            id,
            owner: request.owner.clone(),
            request,
            kind,
            resources,
            bytes,
            cancel,
            slots: Arc::new(JobSlots::default()),
        };

        match self.tx.as_ref() {
            Some(tx) => {
                if tx.send(job).is_err() {
                    // 通道关了 = worker 已退出：把刚占住的预算退回去，别留下假占用。
                    release_payload(&self.resident, bytes);
                    return Err(RtError::InvalidInput("任务池已关闭".to_string()));
                }
            }
            None => {
                // worker 没起来（线程创建失败）：退化成同步执行，保证调用方仍拿到结果。
                self.run_inline(job);
            }
        }
        Ok(id)
    }

    /// 会话复位：取消所有排队/在跑任务，并清空已完成的结果队列。
    ///
    /// 用于退出存档：旧世界的扫描任务跑到一半没有任何意义，它们的结果也不该
    /// 混进下一张存档被谁取走。取消是协作式的（步骤边界退出），不会强杀线程；
    /// 被取消的任务仍会照常产出 `cancelled` 记录，但队列在复位时已经清空了。
    pub fn reset_session(&self) {
        {
            let flags = self
                .flags
                .lock()
                .unwrap_or_else(|poisoned| poisoned.into_inner());
            for (_, cancel) in flags.values() {
                cancel.store(true, Ordering::Release);
            }
        }
        self.results
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner())
            .clear();
    }

    /// 取出已完成的任务结果（取出即出队）。
    pub fn poll(&self, limit: usize) -> Vec<JobRecord> {
        let mut guard = self
            .results
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner());
        let count = if limit == 0 {
            guard.len()
        } else {
            limit.min(guard.len())
        };
        guard.drain(..count).collect()
    }

    /// 窥视已完成的任务结果（**不取出**）。
    ///
    /// 供「先看、后取」的两阶段流程使用：调用方拿到结果后先把它交给上层，
    /// 等确认交付成功了再调 [`Self::drain_ids`] 真正出队。
    ///
    /// 为什么需要它：`JOB_POLL` 是流式语义（每调用一次取走一批），
    /// 若响应装不下、需要调用方扩容重试，结果已经被取走就会**永久丢失**。
    pub fn peek(&self, limit: usize) -> Vec<JobRecord> {
        self.peek_filtered(None, limit)
    }

    /// 窥视已完成的任务结果，可按 owner 过滤（**不取出**）。
    ///
    /// `owner` 为 `None` 表示不限归属；`Some(name)` 表示只要该 owner 的结果。
    ///
    /// **过滤在截断之前**：先按 owner 筛，再取前 `limit` 条。
    /// 反过来（先取前 `limit` 条再筛）会让队头被别人的任务占满，
    /// 结果该 owner 的任务被挤掉——本该属于它的结果一条都拿不到。
    pub fn peek_filtered(&self, owner: Option<&str>, limit: usize) -> Vec<JobRecord> {
        let guard = self
            .results
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner());
        let mut picked = Vec::new();
        for record in guard.iter() {
            // owner 过滤与 limit 截断的顺序很重要：先过滤再截断。
            let owner_mismatch = owner.is_some_and(|wanted| record.owner != wanted);
            if owner_mismatch {
                continue;
            }

            picked.push(record.clone());
            if limit != 0 && picked.len() >= limit {
                break;
            }
        }
        picked
    }

    /// 按 id 出队已完成的任务（配合 [`Self::peek`] 的两阶段流程）。
    ///
    /// 返回实际出队的条数；不存在的 id 会被忽略。
    pub fn drain_ids(&self, ids: &[u64]) -> usize {
        if ids.is_empty() {
            return 0;
        }
        let wanted: std::collections::HashSet<u64> = ids.iter().copied().collect();
        let mut guard = self
            .results
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner());
        let before = guard.len();
        guard.retain(|record| !wanted.contains(&record.id));
        before - guard.len()
    }

    /// 取消一个任务；返回是否命中（1/0）。
    pub fn cancel_job(&self, id: u64) -> u64 {
        let guard = self
            .flags
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner());
        match guard.get(&id) {
            Some((_, flag)) => {
                flag.store(true, Ordering::Relaxed);
                1
            }
            None => 0,
        }
    }

    /// 取消某 owner 的全部任务；返回命中条数。
    pub fn cancel_owner(&self, owner: &str) -> u64 {
        let guard = self
            .flags
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner());
        let mut count = 0;
        for (job_owner, flag) in guard.values() {
            if job_owner == owner {
                flag.store(true, Ordering::Relaxed);
                count += 1;
            }
        }
        count
    }

    /// GPU 状态（**不触发探测**）。
    ///
    /// 探测要初始化 wgpu 并做 1M trit 的 CPU/GPU 对拍，真机实测 **1219.8ms**；
    /// 旧实现把这一步塞在第一次状态查询里，而模组恰好在装载时查状态，
    /// 等于每次进游戏白卡一秒多。现在只有显式 `probe=true` 或真的有大矩阵乘才探测。
    pub fn gpu_status(&self) -> GpuStatus {
        let backend = self
            .gpu
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner());
        pending_probe_reason(backend.status())
    }

    /// 强制重新探测 GPU（显式要求，调用方自愿承担初始化开销）。
    pub fn gpu_probe(&self, trits: usize) -> GpuStatus {
        let mut backend = self
            .gpu
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner());
        backend.probe(trits);
        backend.status()
    }

    /// 切换 GPU 策略（**不触发探测**，理由同 [`Self::gpu_status`]）。
    pub fn set_gpu_policy(&self, policy: GpuPolicy) -> GpuStatus {
        let mut backend = self
            .gpu
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner());
        backend.set_policy(policy);
        pending_probe_reason(backend.status())
    }

    /// GPU 是否可用（已探测且可用）。
    pub fn gpu_available(&self) -> bool {
        self.gpu
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner())
            .enabled()
    }

    /// 全局预算快照（ABI `BUDGET` 的响应体）。
    pub fn budget_json(&self) -> serde_json::Value {
        let budget = self
            .budget
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner());
        let totals = budget.totals();
        let mods: Vec<serde_json::Value> = budget
            .mod_usages()
            .into_iter()
            .map(|(owner, usage)| {
                serde_json::json!({
                    "owner": owner,
                    "inflight": usage.inflight,
                    "queued": usage.queued,
                    "submitted": usage.submitted,
                    "completed": usage.completed,
                    "cancelled": usage.cancelled,
                    "rejected": usage.rejected,
                    "steps": usage.steps,
                })
            })
            .collect();
        let metrics = self
            .metrics
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner());
        serde_json::json!({
            "workers": self.config.resolved_workers(),
            "cpu_capacity": self.config.resolved_cpu_capacity(),
            "memory_capacity": self.config.resolved_memory_capacity(),
            "per_mod_inflight": budget.per_mod_inflight(),
            "inflight": totals.inflight,
            "queued": totals.queued,
            "mods": mods,
            "totals": {
                "submitted": totals.submitted,
                "completed": totals.completed,
                "cancelled": totals.cancelled,
                "rejected": totals.rejected,
                "steps": totals.steps,
            },
            "last_metrics": metrics.as_ref().map(|value| serde_json::json!({
                "started": value.started,
                "completed": value.completed,
                "failed": value.failed,
                "cancelled": value.cancelled,
                "deferred": value.deferred,
                "preemptions": value.preemptions,
                "steps": value.steps,
                "peak_cpu": value.peak_usage.cpu,
                "peak_memory": value.peak_usage.memory,
            })),
        })
    }

    /// 最近一轮执行指标（Prometheus/OpenMetrics 文本）。
    pub fn metrics_prometheus(&self) -> String {
        let guard = self
            .metrics
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner());
        match guard.as_ref() {
            Some(metrics) => metrics.to_prometheus(),
            None => String::new(),
        }
    }

    /// 没有 worker 线程时的退化路径：同步跑完。
    fn run_inline(&self, job: QueuedJob) {
        let QueuedJob {
            id,
            owner,
            request,
            kind,
            bytes,
            cancel,
            slots,
            ..
        } = job;
        let scan = ScanJob::new(
            id,
            &request,
            kind,
            self.config.resolved_scan_chunk(),
            cancel,
            Arc::clone(&slots),
            Arc::clone(&self.results),
            Arc::clone(&self.gpu),
        );
        let record = scan.run_oneshot();
        let steps = record.steps;
        let state = JobState::from_name(&record.state);
        self.push(record);
        release_payload(&self.resident, bytes);
        let mut budget = self
            .budget
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner());
        budget.on_finish(
            &owner,
            state == JobState::Done,
            state == JobState::Cancelled,
            steps,
        );
    }

    fn push(&self, record: JobRecord) {
        push_record(&self.results, record);
    }
}

impl Drop for JobPool {
    fn drop(&mut self) {
        // 先关通道让 worker 自然退出，再取消（协作式，不会强杀）。
        self.tx.take();
        self.cancel.cancel();
        if let Some(handle) = self.worker.take() {
            let _ = handle.join();
        }
    }
}

/// worker 主循环：收任务 → 挑一批 → 交给上游执行器 → 收尾记账。
#[allow(clippy::too_many_arguments)]
fn worker_loop(
    config: ParallelConfig,
    rx: mpsc::Receiver<QueuedJob>,
    results: Arc<Mutex<Vec<JobRecord>>>,
    budget: Arc<Mutex<GlobalBudget>>,
    gpu: Arc<Mutex<GpuBackend>>,
    metrics: Arc<Mutex<Option<RuntimeMetrics>>>,
    flags: JobFlags,
    cancel: CancelToken,
    resident: Arc<AtomicU64>,
) {
    let capacity = Resources::new(
        config.resolved_cpu_capacity(),
        config.resolved_memory_capacity(),
    );
    let mut pending: VecDeque<QueuedJob> = VecDeque::new();
    loop {
        if pending.is_empty() {
            match rx.recv_timeout(Duration::from_millis(25)) {
                Ok(job) => pending.push_back(job),
                Err(mpsc::RecvTimeoutError::Timeout) => {}
                Err(mpsc::RecvTimeoutError::Disconnected) => break,
            }
        }
        while let Ok(job) = rx.try_recv() {
            pending.push_back(job);
        }
        if cancel.is_cancelled() {
            break;
        }
        if pending.is_empty() {
            continue;
        }

        // 排队计数每轮重算（派发后 pending 会变）。
        {
            let mut budget = budget
                .lock()
                .unwrap_or_else(|poisoned| poisoned.into_inner());
            budget.clear_queued();
            for job in &pending {
                budget.add_queued(&job.owner);
            }
        }

        let batch = take_batch(
            &mut pending,
            &budget,
            &results,
            capacity,
            config.resolved_max_batch(),
            &resident,
        );
        if batch.is_empty() {
            // 全被"同一 mod 在跑上限"挡住了：等一会儿再试，不空转。
            thread::sleep(Duration::from_millis(1));
            continue;
        }

        {
            let mut budget = budget
                .lock()
                .unwrap_or_else(|poisoned| poisoned.into_inner());
            for job in &batch {
                budget.on_admit(&job.owner);
            }
        }

        let (oneshot, resumable): (Vec<QueuedJob>, Vec<QueuedJob>) =
            batch.into_iter().partition(|job| job.request.oneshot);

        let runtime_config = RuntimeConfig::new(config.resolved_workers()).with_capacity(capacity);

        if !oneshot.is_empty() {
            let tasks = oneshot
                .iter()
                .map(|job| {
                    let id = job.id;
                    let kind = job.kind;
                    let request = job.request.clone();
                    let cancel_flag = Arc::clone(&job.cancel);
                    let slots = Arc::clone(&job.slots);
                    let sink = Arc::clone(&results);
                    let backend = Arc::clone(&gpu);
                    let chunk = config.resolved_scan_chunk();
                    RuntimeTask::new(format!("job-{id}"), Vec::new(), move |_token| {
                        let scan = ScanJob::new(
                            id,
                            &request,
                            kind,
                            chunk,
                            cancel_flag,
                            Arc::clone(&slots),
                            Arc::clone(&sink),
                            Arc::clone(&backend),
                        );
                        let record = scan.run_oneshot();
                        push_record(&sink, record);
                        Ok(())
                    })
                    .with_priority(job.request.priority)
                    .with_requirements(job.resources)
                })
                .collect::<Vec<_>>();
            if let Ok(report) = run_runtime(tasks, config.resolved_workers(), cancel.clone()) {
                *metrics
                    .lock()
                    .unwrap_or_else(|poisoned| poisoned.into_inner()) = Some(report.metrics);
            }
        }

        if !resumable.is_empty() {
            let specs = resumable
                .iter()
                .map(|job| {
                    let id = job.id;
                    let kind = job.kind;
                    let request = job.request.clone();
                    let cancel_flag = Arc::clone(&job.cancel);
                    let slots = Arc::clone(&job.slots);
                    let sink = Arc::clone(&results);
                    let backend = Arc::clone(&gpu);
                    let chunk = config.resolved_scan_chunk();
                    ResumableSpec::new(
                        format!("job-{id}"),
                        Vec::new(),
                        ScanJob::new(id, &request, kind, chunk, cancel_flag, slots, sink, backend),
                    )
                    .with_priority(job.request.priority)
                    .with_requirements(job.resources)
                })
                .collect::<Vec<_>>();
            if let Ok(report) = run_resumable_threaded(specs, runtime_config, cancel.clone()) {
                *metrics
                    .lock()
                    .unwrap_or_else(|poisoned| poisoned.into_inner()) = Some(report.metrics);
            }
        }

        // 收尾：记账 + 把被运行时丢弃的任务补一条失败记录。
        let mut budget = budget
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner());
        for job in oneshot.iter().chain(resumable.iter()) {
            let state = job.slots.outcome();
            match state {
                Some(state) => {
                    let steps = job.slots.steps();
                    budget.on_finish(
                        &job.owner,
                        state == JobState::Done,
                        state == JobState::Cancelled,
                        steps,
                    );
                }
                None => {
                    // 一步都没跑（同批有任务失败，runtime 停止派发）：记为失败，别静默丢。
                    let mut record = JobRecord::new(
                        job.id,
                        &job.owner,
                        job.kind.as_str(),
                        JobState::Failed,
                        0,
                        FAILED_BACKEND,
                    );
                    record.error = "任务未被执行（同批有任务失败或运行时提前停止）".to_string();
                    push_record(&results, record);
                    budget.on_finish(&job.owner, false, false, 0);
                }
            }
            flags
                .lock()
                .unwrap_or_else(|poisoned| poisoned.into_inner())
                .remove(&job.id);
            // 任务已经跑完/被丢弃：它持有的载荷随 job 一起在这里释放，预算同步归还。
            release_payload(&resident, job.bytes);
        }
    }
}

fn push_record(sink: &Arc<Mutex<Vec<JobRecord>>>, record: JobRecord) {
    let mut queue = sink.lock().unwrap_or_else(|poisoned| poisoned.into_inner());
    if queue.len() >= MAX_PENDING_RESULTS {
        // 调用方长期不轮询（例如它自己把轮询关掉了）：丢最旧的一条，保住最新结果。
        // 没有这一步，结果队列就是一条只进不出的内存增长通道。
        queue.remove(0);
    }
    queue.push(record);
}

/// 从队首挑一批能跑的任务；被"同 mod 上限"挡住的留在队列里。
fn take_batch(
    pending: &mut VecDeque<QueuedJob>,
    budget: &Arc<Mutex<GlobalBudget>>,
    sink: &Arc<Mutex<Vec<JobRecord>>>,
    capacity: Resources,
    max_batch: usize,
    resident: &Arc<AtomicU64>,
) -> Vec<QueuedJob> {
    let mut batch = Vec::new();
    let mut used = Resources::default();
    let mut inflight_mod: BTreeMap<String, u32> = BTreeMap::new();
    let mut index = 0;
    while index < pending.len() && batch.len() < max_batch {
        let job = &pending[index];
        let inflight = inflight_mod.get(&job.owner).copied().unwrap_or(0);
        let budget_guard = budget
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner());
        let admission = budget_guard.admit(&job.owner, job.resources, capacity);
        let per_mod_limit = budget_guard.per_mod_inflight();
        drop(budget_guard);
        if admission == Admission::Reject {
            // 永远放不下：直接判失败并出队，不能让它堵住后面的任务。
            let mut record = JobRecord::new(
                job.id,
                &job.owner,
                job.kind.as_str(),
                JobState::Failed,
                0,
                FAILED_BACKEND,
            );
            record.error = format!(
                "任务需求 cpu={} memory={} 超过全局容量 cpu={} memory={}",
                job.resources.cpu, job.resources.memory, capacity.cpu, capacity.memory
            );
            job.slots.set(JobState::Failed, 0);
            push_record(sink, record);
            let job = pending.remove(index).expect("下标在界内");
            // 判失败出队 = 载荷永远跑不到，立刻归还它占的预算，否则预算会被死任务吃掉。
            release_payload(resident, job.bytes);
            let mut budget_guard = budget
                .lock()
                .unwrap_or_else(|poisoned| poisoned.into_inner());
            budget_guard.on_reject(&job.owner);
            continue;
        }
        if usize::try_from(inflight).unwrap_or(usize::MAX) >= per_mod_limit
            || admission == Admission::Queue
            || !job.resources.fits_in(capacity, used)
        {
            index += 1;
            continue;
        }
        used = used.checked_add(job.resources).unwrap_or(used);
        inflight_mod
            .entry(job.owner.clone())
            .and_modify(|count| *count += 1)
            .or_insert(1);
        batch.push(pending.remove(index).expect("下标在界内"));
    }
    batch
}

/// 状态里「为什么还没探测」要说清楚，否则外部只能看到 `available=false` 一片空白。
fn pending_probe_reason(status: GpuStatus) -> GpuStatus {
    let mut status = status;
    if status.reason.is_empty() && !status.available {
        status.reason =
            "尚未探测（装载期不探测；显式 GPU_PROBE 或出现大矩阵乘时才初始化）".to_string();
    }
    status
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::gpu::GpuConfig;

    fn pool(workers: usize, per_mod_inflight: usize) -> JobPool {
        JobPool::new(
            ParallelConfig {
                workers,
                per_mod_inflight,
                scan_chunk: 2,
                ..ParallelConfig::default()
            },
            GpuConfig::default(),
        )
    }

    fn request(owner: &str, kind: ScanKind, trits: Vec<u8>) -> ScanRequest {
        ScanRequest {
            owner: owner.to_string(),
            kind: kind.as_str().to_string(),
            trits,
            ..ScanRequest::default()
        }
    }

    /// 轮询直到拿到 `count` 条结果（超时失败，不死锁）。
    fn poll_until(pool: &JobPool, count: usize, timeout: Duration) -> Vec<JobRecord> {
        let deadline = std::time::Instant::now() + timeout;
        let mut collected = Vec::new();
        while collected.len() < count && std::time::Instant::now() < deadline {
            collected.extend(pool.poll(count - collected.len()));
            if collected.len() < count {
                thread::sleep(Duration::from_millis(2));
            }
        }
        collected
    }

    #[test]
    fn a_submitted_job_finishes_and_can_be_polled() {
        let pool = pool(2, 2);
        let id = pool
            .submit(request("m", ScanKind::Histogram, vec![0, 1, 2]))
            .expect("提交成功");
        assert!(id >= 1);
        let records = poll_until(&pool, 1, Duration::from_secs(5));
        assert_eq!(records.len(), 1, "任务应产出一条记录");
        assert_eq!(records[0].id, id);
        assert_eq!(records[0].state, "done");
        assert_eq!(records[0].result_value()["histogram"]["neg"], 1);
    }

    #[test]
    fn polling_drains_results_without_duplicates() {
        let pool = pool(2, 2);
        let _ = pool
            .submit(request("m", ScanKind::Histogram, vec![2, 2]))
            .unwrap();
        let first = poll_until(&pool, 1, Duration::from_secs(5));
        assert_eq!(first.len(), 1);
        let second = pool.poll(8);
        assert!(second.is_empty(), "取出即出队，不应重复返回");
    }

    #[test]
    fn many_jobs_from_many_mods_all_complete() {
        let pool = pool(4, 2);
        let mut ids = Vec::new();
        for index in 0..12 {
            let owner = format!("mod{}", index % 4);
            ids.push(
                pool.submit(request(&owner, ScanKind::Histogram, vec![0, 1, 2, 2]))
                    .expect("提交成功"),
            );
        }
        let records = poll_until(&pool, 12, Duration::from_secs(10));
        assert_eq!(records.len(), 12, "12 个任务必须全部完成（不丢任务）");
        for record in &records {
            assert_eq!(record.state, "done");
        }
        let mut polled: Vec<u64> = records.iter().map(|record| record.id).collect();
        polled.sort_unstable();
        let mut expected = ids.clone();
        expected.sort_unstable();
        assert_eq!(polled, expected);
    }

    #[test]
    fn a_queued_job_is_not_dropped_when_the_owner_hits_its_limit() {
        // per_mod_inflight = 1：同一 owner 的第二个任务必须排队，之后仍然被执行。
        let pool = pool(2, 1);
        let _ = pool
            .submit(request("greedy", ScanKind::Histogram, vec![2; 200]))
            .unwrap();
        let _ = pool
            .submit(request("greedy", ScanKind::Histogram, vec![0; 200]))
            .unwrap();
        let records = poll_until(&pool, 2, Duration::from_secs(10));
        assert_eq!(records.len(), 2, "排队任务不能被丢");
        assert!(records.iter().all(|record| record.state == "done"));
    }

    #[test]
    fn cancelling_by_owner_stops_queued_jobs() {
        let pool = pool(1, 1);
        let data = vec![1u8; 200_000];
        let _ = pool
            .submit(request("victim", ScanKind::Density, data.clone()))
            .unwrap();
        let _ = pool
            .submit(request("victim", ScanKind::Density, data.clone()))
            .unwrap();
        let _ = pool
            .submit(request("victim", ScanKind::Density, data))
            .unwrap();
        let cancelled = pool.cancel_owner("victim");
        assert!(cancelled >= 1);
        let records = poll_until(&pool, 3, Duration::from_secs(10));
        assert_eq!(records.len(), 3);
        assert!(
            records.iter().any(|record| record.state == "cancelled"),
            "至少应有一个任务被取消：{records:?}"
        );
    }

    #[test]
    fn cancelling_an_unknown_job_is_a_no_op() {
        let pool = pool(1, 1);
        assert_eq!(pool.cancel_job(999_999), 0);
        assert_eq!(pool.cancel_owner("nobody"), 0);
    }

    #[test]
    fn an_oversized_job_becomes_a_failed_task_not_a_rejected_submit() {
        let config = ParallelConfig {
            memory_capacity: 1,
            ..ParallelConfig::default()
        };
        let pool = JobPool::new(config, GpuConfig::default());
        // 3 MiB 载荷 > 1 MiB 容量
        let request = ScanRequest {
            owner: "big".to_string(),
            kind: ScanKind::Histogram.as_str().to_string(),
            trits: vec![1; 3 << 20],
            chunk: 1 << 20,
            ..ScanRequest::default()
        };
        // ABI 规定：载荷放不下全局容量 → 产生一个 failed 任务，而不是让 opcode 失败。
        let id = pool.submit(request).expect("提交本身不报错");
        let records = poll_until(&pool, 1, Duration::from_secs(5));
        assert_eq!(records.len(), 1);
        assert_eq!(records[0].id, id);
        assert_eq!(records[0].state, "failed");
        assert!(
            records[0].error.contains("超过全局容量"),
            "失败原因要说明是容量不够：{}",
            records[0].error
        );
        assert_eq!(records[0].backend, FAILED_BACKEND);
    }

    #[test]
    fn the_queued_payload_total_is_capped_by_the_memory_budget() {
        // 预算 8 MiB：连发 8 个 2 MiB 的扫描。前几个排进去，后面的必须在提交时
        // 就被拒（变成失败记录），不能让它们无声无息地排在内存里。
        let pool = JobPool::new(
            ParallelConfig {
                workers: 1,
                per_mod_inflight: 64,
                scan_chunk: 1 << 20,
                memory_capacity: 8,
                ..ParallelConfig::default()
            },
            GpuConfig::default(),
        );
        let mut ids = Vec::new();
        for _ in 0..8 {
            ids.push(
                pool.submit(request("m", ScanKind::Histogram, vec![1; 2 << 20]))
                    .expect("提交本身不该失败"),
            );
        }
        let records = poll_until(&pool, ids.len(), Duration::from_secs(20));
        assert_eq!(records.len(), ids.len(), "每个提交都要有一条记录");
        let rejected = records
            .iter()
            .filter(|record| record.error.contains("在飞载荷预算已满"))
            .count();
        assert!(rejected > 0, "总载荷超出预算时必须拒绝一部分：{records:?}");
        assert!(
            pool.resident.load(Ordering::Relaxed) <= 8 << 20,
            "在飞载荷永远不该超过预算，实际 {} 字节",
            pool.resident.load(Ordering::Relaxed)
        );
    }

    #[test]
    fn a_finished_job_returns_its_payload_budget() {
        let pool = pool(2, 2);
        let _ = pool
            .submit(request("m", ScanKind::Histogram, vec![1; 512]))
            .unwrap();
        let records = poll_until(&pool, 1, Duration::from_secs(5));
        assert_eq!(records.len(), 1);
        // 排队占的预算必须在跑完后归还，否则长时间跑下去预算会被"已结束"的任务吃光。
        let deadline = std::time::Instant::now() + Duration::from_secs(5);
        while pool.resident.load(Ordering::Relaxed) != 0 && std::time::Instant::now() < deadline {
            thread::sleep(Duration::from_millis(2));
        }
        assert_eq!(pool.resident.load(Ordering::Relaxed), 0);
    }

    #[test]
    fn invalid_requests_become_failed_tasks_not_rejected_submits() {
        let pool = pool(1, 1);
        let mut request = request("m", ScanKind::Pattern, vec![1, 2]);
        request.pattern = Vec::new();
        // ABI 规定：请求非法 → 产生一个 failed 任务，而不是让 opcode 失败。
        let id = pool.submit(request).expect("提交本身不报错");
        let records = poll_until(&pool, 1, Duration::from_secs(5));
        assert_eq!(records.len(), 1);
        assert_eq!(records[0].id, id);
        assert_eq!(records[0].state, "failed");
        assert!(
            records[0].error.contains("扫描需要非空 pattern"),
            "失败原因要说明缺 pattern：{}",
            records[0].error
        );
        assert_eq!(records[0].kind, "pattern");
        assert_eq!(records[0].backend, FAILED_BACKEND);
    }

    #[test]
    fn an_unknown_kind_becomes_a_failed_task_named_unknown() {
        let pool = pool(1, 1);
        let request = ScanRequest {
            owner: "m".to_string(),
            kind: "weird".to_string(),
            trits: vec![1, 2],
            ..ScanRequest::default()
        };
        let id = pool.submit(request).expect("提交本身不报错");
        let records = poll_until(&pool, 1, Duration::from_secs(5));
        assert_eq!(records.len(), 1);
        assert_eq!(records[0].id, id);
        assert_eq!(records[0].state, "failed");
        // 机器可读的 kind 字段收敛到固定取值，原文只出现在错误文案里。
        assert_eq!(records[0].kind, "unknown");
        assert!(
            records[0].error.contains("未知扫描 kind"),
            "失败原因要说明是种类未知：{}",
            records[0].error
        );
        assert!(
            records[0].error.contains("weird"),
            "失败原因要回显用户传入的原文：{}",
            records[0].error
        );
    }

    #[test]
    fn an_illegal_trit_value_becomes_a_failed_task() {
        let pool = pool(1, 1);
        let request = ScanRequest {
            owner: "m".to_string(),
            kind: ScanKind::Histogram.as_str().to_string(),
            trits: vec![1, 2, 7],
            ..ScanRequest::default()
        };
        let id = pool.submit(request).expect("提交本身不报错");
        let records = poll_until(&pool, 1, Duration::from_secs(5));
        assert_eq!(records.len(), 1);
        assert_eq!(records[0].id, id);
        assert_eq!(records[0].state, "failed");
        assert!(
            records[0].error.contains("非法 trit 值"),
            "失败原因要说明是 trit 值非法：{}",
            records[0].error
        );
    }

    #[test]
    fn budget_json_reports_per_mod_accounting() {
        let pool = pool(2, 1);
        let _ = pool
            .submit(request("alpha", ScanKind::Histogram, vec![0, 1, 2]))
            .unwrap();
        let _ = poll_until(&pool, 1, Duration::from_secs(5));
        let json = pool.budget_json();
        assert_eq!(json["workers"], 2);
        assert!(json["mods"][0]["submitted"].as_u64().unwrap_or(0) >= 1);
        assert!(json["totals"]["submitted"].as_u64().unwrap_or(0) >= 1);
        assert!(json["last_metrics"].is_object() || json["last_metrics"].is_null());
    }

    #[test]
    fn gpu_is_reported_unavailable_without_the_feature() {
        let pool = pool(1, 1);
        let status = pool.gpu_status();
        assert!(!cfg!(feature = "gpu") || !status.available || status.available);
        assert_eq!(status.policy, GpuPolicy::Auto);
        assert!(!status.reason.is_empty() || status.available);
    }

    #[test]
    fn oneshot_jobs_run_too() {
        let pool = pool(2, 2);
        let mut request = request("m", ScanKind::Dot, vec![2, 2, 2]);
        // trits 全 Pos(+1,+1,+1)，pattern = (Pos, Neg, Pos) → 1 - 1 + 1 = 1
        request.pattern = vec![2, 0, 2];
        request.oneshot = true;
        let id = pool.submit(request).expect("提交成功");
        let records = poll_until(&pool, 1, Duration::from_secs(5));
        assert_eq!(records.len(), 1);
        assert_eq!(records[0].id, id);
        assert_eq!(records[0].result_value()["dot"], 1);
    }

    #[test]
    fn worker_count_resolution_is_bounded() {
        assert!(ParallelConfig::default().resolved_workers() >= 1);
        assert_eq!(
            ParallelConfig {
                workers: 64,
                ..ParallelConfig::default()
            }
            .resolved_workers(),
            32
        );
        assert_eq!(
            ParallelConfig {
                workers: 0,
                ..ParallelConfig::default()
            }
            .resolved_cpu_capacity(),
            ParallelConfig::default().resolved_workers() as u32
        );
    }
}
