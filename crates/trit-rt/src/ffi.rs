//! C ABI 层：跨 FFI 只传字节和句柄，panic 全部 `catch_unwind` 拦住，
//! 永远只向调用方返回状态码（见 `TT_*` 常量与 `docs/ABI.md`）。
//!
//! 缓冲区协议：响应写进 `out`（容量 `cap`）。空间不足时把**所需长度**以
//! `u64` 小端写进 `out` 前 8 字节（要求 `cap >= 8`）并回 `TT_ERR_BUFFER`，
//! 调用方扩容后重试即可。

use std::collections::BTreeMap;
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{Mutex, OnceLock};

use crate::error::RtError;
use crate::runtime::{RuntimeConfig, TritRuntime};
use crate::wire::{Writer, frame_response, op};

/// ABI 版本：不兼容变更时递增。
///
/// v2：新增并行任务池 / GPU 后端 / 全局预算三组能力（opcode `0x14..0x19`，
/// 载荷与响应为 UTF-8 JSON，既有 opcode 的二进制格式不变）。
///
/// v3：新增 `0x1A TXN_SUBMIT_BATCH`，且 C# 壳**每个 tick 都用它**。这必须
/// 递增版本号——只加 opcode 不算破坏性变更，但「客户端默认依赖新 opcode」
/// 算了：v2 的老 DLL 会让每次批量提交拿到 `BAD_OP`，于是每 tick 都失败、
/// 钩子被熔断，表象是「模组忽然完全没作用」。递增后这种混装会在装载期被
/// 版本检查挡下，干净地退回托管内核（功能完整，只是慢）。
pub const ABI_VERSION: u32 = 3;

/// 运行时版本：`0x00_MM_mm_pp`（0.1.0）。
pub const RUNTIME_VERSION: u32 = 0x00_01_00_00;

// ---- 状态码 ----
/// 成功。
pub const TT_OK: i32 = 0;
/// 句柄为空或不存在。
pub const TT_ERR_BAD_HANDLE: i32 = -1;
/// opcode 为 0 或保留值。
pub const TT_ERR_BAD_OP: i32 = -2;
/// 输出缓冲区不足；所需长度已写入 `out` 前 8 字节。
pub const TT_ERR_BUFFER: i32 = -3;
/// 超过容量上限。
pub const TT_ERR_CAPACITY: i32 = -4;
/// 冲突仲裁未通过；争议诊断已尽量写入 `out`。
pub const TT_ERR_CONFLICT: i32 = -5;
/// Rust 侧 panic 被拦住（不应发生；发生即 bug）。
pub const TT_ERR_PANIC: i32 = -6;
/// 线格式解码失败。
pub const TT_ERR_DECODE: i32 = -7;
/// 事务 / 快照 / 订阅不存在。
pub const TT_ERR_NOT_FOUND: i32 = -8;
/// 事务状态非法（重复提交等）。
pub const TT_ERR_STATE: i32 = -9;
/// 参数非法（未知 opcode、越界维度等）。
pub const TT_ERR_ARG: i32 = -10;

type Registry = Mutex<BTreeMap<u64, RuntimeEntry>>;

/// 注册表里的一条运行时。
struct RuntimeEntry {
    runtime: TritRuntime,
    /// 「已执行、但没能送出去」的响应（见 [`PendingResponses`]）。
    pending: PendingResponses,
}

impl RuntimeEntry {
    fn new(runtime: TritRuntime) -> Self {
        Self {
            runtime,
            pending: PendingResponses::default(),
        }
    }
}

/// 待交付响应缓存。
///
/// **为什么必须缓存**：缓冲区协议规定「响应装不下 → 回 `TT_ERR_BUFFER` 并写回
/// 所需长度 → 调用方扩容后重试」。但 opcode 大多有副作用：`PRNG_NEXT` 会推进
/// 随机序列、`SUBSCRIBE`/`TXN_BEGIN`/`SNAPSHOT` 会创建资源。若重试等于二次执行，
/// 第二次拿到的就是**不同的结果**——所以这里把「已经算好、但没送出去」的响应
/// 按 `(opcode, 载荷)` 存下来，调用方扩容重试时原样取回，绝不二次执行。
///
/// **流式语义不进这个缓存**：`JOB_POLL` 每次调用取走一批结果，同参数多次调用
/// 语义不同，用缓存重放会返回**陈旧数据**。它走「窥视 → 交付成功才出队」的
/// 两阶段流程（见 [`TritRuntime::poll_jobs_peek`] / [`TritRuntime::commit_jobs`]）。
#[derive(Default)]
struct PendingResponses {
    entries: Vec<PendingEntry>,
}

struct PendingEntry {
    opcode: u32,
    payload: Vec<u8>,
    response: Vec<u8>,
}

/// 待交付响应条数上限（防止调用方一直不扩容，缓存无限增长）。
const PENDING_MAX_ENTRIES: usize = 4;
/// 待交付响应总字节上限。
const PENDING_MAX_BYTES: usize = 64 << 20;
/// 参与缓存的载荷字节上限：载荷越大越不缓存（这类 opcode 响应都很小，
/// 走不到「响应装不下」的重试路径，缓存它们只会白占内存）。
const PENDING_MAX_PAYLOAD: usize = 64 << 10;

impl PendingResponses {
    /// **取走**同一请求（opcode + 载荷逐字节相同）的待交付响应。
    ///
    /// 取走即从缓存移除：这样「缓冲区还是不够 → 再存回去」不会与「已经被取走」
    /// 混淆（否则第二次重试会误判成"没缓存"而二次执行）。写不出去时调用方
    /// 会把它重新 [`Self::store`] 回来。
    fn take(&mut self, opcode: u32, payload: &[u8]) -> Option<Vec<u8>> {
        let index = self
            .entries
            .iter()
            .position(|entry| entry.opcode == opcode && entry.payload == payload)?;
        Some(self.entries.remove(index).response)
    }

    /// 记下一条待交付响应（同键覆盖旧的）。
    fn store(&mut self, opcode: u32, payload: &[u8], response: Vec<u8>) {
        if payload.len() > PENDING_MAX_PAYLOAD {
            return;
        }
        self.entries
            .retain(|entry| !(entry.opcode == opcode && entry.payload == payload));
        if self.entries.len() >= PENDING_MAX_ENTRIES {
            self.entries.remove(0);
        }
        self.entries.push(PendingEntry {
            opcode,
            payload: payload.to_vec(),
            response,
        });
    }

    /// 淘汰超出字节上限的最旧条目。
    fn trim(&mut self) {
        let mut total: usize = self.entries.iter().map(|entry| entry.response.len()).sum();
        while total > PENDING_MAX_BYTES && !self.entries.is_empty() {
            total -= self.entries.remove(0).response.len();
        }
    }
}

/// `tt_rt_snapshot` 专用的内部键：与 `tt_rt_submit(SNAPSHOT)` 响应格式不同
/// （前者带世界字节，后者只有快照 id），必须分开缓存，否则会串味。
const OP_SNAPSHOT_BYTES: u32 = 0x8000_0001;

fn registry() -> &'static Registry {
    static REGISTRY: OnceLock<Registry> = OnceLock::new();
    REGISTRY.get_or_init(|| Mutex::new(BTreeMap::new()))
}

fn next_handle() -> u64 {
    static NEXT: AtomicU64 = AtomicU64::new(1);
    NEXT.fetch_add(1, Ordering::Relaxed)
}

/// 取一个运行时并执行闭包；句柄非法即报错。
fn with_runtime<T>(
    handle: u64,
    f: impl FnOnce(&mut TritRuntime) -> Result<T, RtError>,
) -> Result<T, RtError> {
    with_entry(handle, |entry| f(&mut entry.runtime))
}

/// 取一个运行时条目并执行闭包；句柄非法即报错。
fn with_entry<T>(
    handle: u64,
    f: impl FnOnce(&mut RuntimeEntry) -> Result<T, RtError>,
) -> Result<T, RtError> {
    if handle == 0 {
        return Err(RtError::BadHandle(0));
    }
    let mut guard = registry()
        .lock()
        .unwrap_or_else(|poisoned| poisoned.into_inner());
    let entry = guard.get_mut(&handle).ok_or(RtError::BadHandle(handle))?;
    f(entry)
}

/// 把响应字节写进调用方缓冲区。
fn write_out(out: *mut u8, cap: usize, bytes: &[u8]) -> i32 {
    if out.is_null() || cap < bytes.len() {
        if !out.is_null() && cap >= 8 {
            // 空间不足时把所需长度回写，调用方据此扩容。
            let required = (bytes.len() as u64).to_le_bytes();
            unsafe {
                std::ptr::copy_nonoverlapping(required.as_ptr(), out, 8);
            }
        }
        return TT_ERR_BUFFER;
    }
    unsafe {
        std::ptr::copy_nonoverlapping(bytes.as_ptr(), out, bytes.len());
    }
    TT_OK
}

/// 冲突错误的诊断载荷（争议车道 + 全部肇事方）。
fn conflict_diagnostics(error: &RtError) -> Option<Vec<u8>> {
    if let RtError::Conflict { disputes, .. } = error {
        let mut writer = Writer::new();
        writer.u32(disputes.len() as u32);
        for dispute in disputes {
            dispute.encode(&mut writer);
        }
        Some(writer.into_vec())
    } else {
        None
    }
}

/// 从裸指针切出载荷；空指针/零长视为空载荷。
unsafe fn payload_slice<'a>(payload: *const u8, len: usize) -> &'a [u8] {
    if payload.is_null() || len == 0 {
        &[]
    } else {
        unsafe { std::slice::from_raw_parts(payload, len) }
    }
}

/// ABI 版本号。
///
/// # Safety
///
/// 指针参数必须由调用方保证有效：`payload`/`len` 指向可读内存，
/// `out`/`cap` 指向可写内存（`out` 为空且 `cap` 为 0 时按「查询所需长度」处理）。
#[unsafe(no_mangle)]
pub extern "C" fn tt_rt_abi_version() -> u32 {
    ABI_VERSION
}

/// 运行时版本号。
///
/// # Safety
///
/// 指针参数必须由调用方保证有效：`payload`/`len` 指向可读内存，
/// `out`/`cap` 指向可写内存（`out` 为空且 `cap` 为 0 时按「查询所需长度」处理）。
#[unsafe(no_mangle)]
pub extern "C" fn tt_rt_version() -> u32 {
    RUNTIME_VERSION
}

/// 创建运行时；`config` 为 JSON 字节（可为空 = 默认配置）。失败返回 0。
///
/// # Safety
///
/// 指针参数必须由调用方保证有效：`payload`/`len` 指向可读内存，
/// `out`/`cap` 指向可写内存（`out` 为空且 `cap` 为 0 时按「查询所需长度」处理）。
#[unsafe(no_mangle)]
pub unsafe extern "C" fn tt_rt_create(config: *const u8, len: usize) -> u64 {
    let outcome = std::panic::catch_unwind(|| {
        let bytes = unsafe { payload_slice(config, len) };
        let config = RuntimeConfig::from_json(bytes)?;
        let handle = next_handle();
        registry()
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner())
            .insert(handle, RuntimeEntry::new(TritRuntime::new(config)));
        Ok::<u64, RtError>(handle)
    });
    match outcome {
        Ok(Ok(handle)) => handle,
        _ => 0,
    }
}

/// 销毁运行时。
///
/// # Safety
///
/// 指针参数必须由调用方保证有效：`payload`/`len` 指向可读内存，
/// `out`/`cap` 指向可写内存（`out` 为空且 `cap` 为 0 时按「查询所需长度」处理）。
#[unsafe(no_mangle)]
pub unsafe extern "C" fn tt_rt_destroy(handle: u64) -> i32 {
    let outcome = std::panic::catch_unwind(|| {
        let mut guard = registry()
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner());
        Ok::<bool, RtError>(guard.remove(&handle).is_some())
    });
    match outcome {
        Ok(Ok(true)) => TT_OK,
        _ => TT_ERR_BAD_HANDLE,
    }
}

/// 提交一个操作。响应写进 `out`；见文件头缓冲区协议。
///
/// 响应统一装框：`[u32 payload_len][payload]`；`TT_ERR_CONFLICT` 时
/// `out` 里是装框的争议诊断（`[u32 len][dispute_count][车道/肇事方...]`）。
///
/// **重试语义（重要）**：响应装不下时回 `TT_ERR_BUFFER` 并写回所需长度，
/// 调用方扩容后用**同样的 opcode 与载荷**重试。
///
/// - 绝大多数 opcode：本次执行的结果已被缓存，原样返回，**不会二次执行**
///   ——否则 `PRNG_NEXT` 会多消耗一段随机序列，`SUBSCRIBE`/`TXN_BEGIN` 会重复创建资源。
/// - `JOB_POLL`：**不进缓存**，走「窥视 → 交付成功才出队」的两阶段流程
///   （见 `submit_job_poll`）。它是流式语义，缓存重放会返回陈旧数据；
///   而直接出队又会在扩容重试时把结果丢掉。
///
/// # Safety
///
/// 指针参数必须由调用方保证有效：`payload`/`len` 指向可读内存，
/// `out`/`cap` 指向可写内存（`out` 为空且 `cap` 为 0 时按「查询所需长度」处理）。
#[unsafe(no_mangle)]
pub unsafe extern "C" fn tt_rt_submit(
    handle: u64,
    opcode: u32,
    payload: *const u8,
    len: usize,
    out: *mut u8,
    cap: usize,
) -> i32 {
    if opcode == 0 {
        return TT_ERR_BAD_OP;
    }
    // JOB_POLL 走两阶段专用路径（不进待交付缓存）。
    if opcode == op::JOB_POLL {
        return unsafe { submit_job_poll(handle, payload, len, out, cap) };
    }
    let outcome = std::panic::catch_unwind(|| {
        let bytes = unsafe { payload_slice(payload, len) };
        with_entry(handle, |entry| {
            // 命中「同一请求已执行、只是没送出去」的缓存 → 原样返回，不二次执行。
            if let Some(cached) = entry.pending.take(opcode, bytes) {
                return Ok(cached);
            }
            entry.runtime.dispatch(opcode, bytes)
        })
    });
    match outcome {
        Ok(Ok(response)) => {
            let framed = frame_response(&response);
            let status = write_out(out, cap, &framed);
            if status == TT_ERR_BUFFER {
                // 装不下：把响应存进待交付槽，调用方扩容后原样取回。
                let payload = unsafe { payload_slice(payload, len) }.to_vec();
                let store = std::panic::catch_unwind(|| {
                    with_entry(handle, |entry| {
                        entry.pending.store(opcode, &payload, response);
                        entry.pending.trim();
                        Ok(())
                    })
                });
                if store.is_err() {
                    return TT_ERR_PANIC;
                }
            }
            status
        }
        Ok(Err(error)) => {
            if let Some(diagnostics) = conflict_diagnostics(&error) {
                let _ = write_out(out, cap, &frame_response(&diagnostics));
            }
            error.status_code()
        }
        Err(_) => TT_ERR_PANIC,
    }
}

/// `JOB_POLL` 的两阶段提交：**窥视 → 写缓冲 → 写成功才出队**。
///
/// 为什么必须两阶段：
///
/// - **不能「取出即出队」**：缓冲区协议规定响应装不下时调用方会扩容重试；
///   若第一次就把结果出队，重试时队列已空，任务结果**永久丢失**。
/// - **不能进待交付缓存**：`JOB_POLL` 是流式语义（同参数多次调用语义不同），
///   缓存重放会把上一次的陈旧结果当成这一次的新结果返回。
///
/// 两阶段同时满足两条要求：**结果一次都不会丢**，也**一次都不会重**。
/// 交付失败（缓冲区不够）时结果仍留在队列里，调用方扩容后重试会重新窥视到
/// 它 —— 最多返回一次，绝不会丢。
///
/// # Safety
///
/// 指针参数必须由调用方保证有效：`payload`/`len` 指向可读内存，
/// `out`/`cap` 指向可写内存（`out` 为空且 `cap` 为 0 时按「查询所需长度」处理）。
unsafe fn submit_job_poll(
    handle: u64,
    payload: *const u8,
    len: usize,
    out: *mut u8,
    cap: usize,
) -> i32 {
    let outcome = std::panic::catch_unwind(|| {
        let bytes = unsafe { payload_slice(payload, len) };
        // 整个「窥视 → 写出 → 出队」过程持锁，保证「窥视到的 id」与
        // 「出队的 id」严格一致（并发轮询不会互相踩）。
        with_entry(handle, |entry| {
            let (response, ids) = entry.runtime.poll_jobs_peek(bytes)?;
            let framed = frame_response(&response);
            let status = write_out(out, cap, &framed);
            if status == TT_OK {
                // 交付成功：这批结果才算被取走。
                entry.runtime.commit_jobs(&ids);
            }
            Ok::<i32, RtError>(status)
        })
    });
    match outcome {
        Ok(Ok(status)) => status,
        Ok(Err(error)) => error.status_code(),
        Err(_) => TT_ERR_PANIC,
    }
}

/// 推进一个 tick（等价于 `submit(STEP, tick)`）。
///
/// 与 `tt_rt_submit` 一样，响应装不下时扩容重试**不会二次推进 tick**。
///
/// # Safety
///
/// 指针参数必须由调用方保证有效：`payload`/`len` 指向可读内存，
/// `out`/`cap` 指向可写内存（`out` 为空且 `cap` 为 0 时按「查询所需长度」处理）。
#[unsafe(no_mangle)]
pub unsafe extern "C" fn tt_rt_step(handle: u64, tick: u64, out: *mut u8, cap: usize) -> i32 {
    let mut writer = Writer::new();
    writer.u64(tick);
    let payload = writer.into_vec();
    let outcome = std::panic::catch_unwind(|| {
        let bytes = unsafe { payload_slice(payload.as_ptr(), payload.len()) };
        with_entry(handle, |entry| {
            // 命中「同一请求已执行、只是没送出去」的缓存 → 原样返回，不二次推进 tick。
            if let Some(cached) = entry.pending.take(op::STEP, bytes) {
                return Ok(cached);
            }
            entry.runtime.dispatch(op::STEP, bytes)
        })
    });
    match outcome {
        Ok(Ok(response)) => {
            let framed = frame_response(&response);
            let status = write_out(out, cap, &framed);
            if status == TT_ERR_BUFFER {
                let store = std::panic::catch_unwind(|| {
                    with_entry(handle, |entry| {
                        entry.pending.store(op::STEP, &payload, response);
                        entry.pending.trim();
                        Ok(())
                    })
                });
                if store.is_err() {
                    return TT_ERR_PANIC;
                }
            }
            status
        }
        Ok(Err(error)) => error.status_code(),
        Err(_) => TT_ERR_PANIC,
    }
}

/// 打快照并把快照字节写进 `out`（装框：`u64` 快照 ID + 世界投影编码）。
///
/// 与 `tt_rt_submit` 一样，响应装不下时扩容重试**不会重复创建快照**。
///
/// # Safety
///
/// 指针参数必须由调用方保证有效：`payload`/`len` 指向可读内存，
/// `out`/`cap` 指向可写内存（`out` 为空且 `cap` 为 0 时按「查询所需长度」处理）。
#[unsafe(no_mangle)]
pub unsafe extern "C" fn tt_rt_snapshot(handle: u64, out: *mut u8, cap: usize) -> i32 {
    let mut writer = Writer::new();
    writer.u32(0);
    let payload = writer.into_vec();
    let outcome = std::panic::catch_unwind(|| {
        with_entry(handle, |entry| {
            // 命中缓存 → 原样返回，不重复创建快照。
            if let Some(cached) = entry.pending.take(OP_SNAPSHOT_BYTES, &payload) {
                return Ok(cached);
            }
            let response = entry.runtime.dispatch(op::SNAPSHOT, &payload)?;
            let snapshot_id = crate::wire::Reader::new(&response).u64()?;
            let bytes = entry
                .runtime
                .snapshot_bytes(snapshot_id)
                .ok_or(RtError::SnapshotNotFound(snapshot_id))?;
            let mut writer = Writer::new();
            writer.u64(snapshot_id);
            writer.bytes(bytes);
            Ok::<Vec<u8>, RtError>(writer.into_vec())
        })
    });
    match outcome {
        Ok(Ok(response)) => {
            let framed = frame_response(&response);
            let status = write_out(out, cap, &framed);
            if status == TT_ERR_BUFFER {
                let store = std::panic::catch_unwind(|| {
                    with_entry(handle, |entry| {
                        entry.pending.store(OP_SNAPSHOT_BYTES, &payload, response);
                        entry.pending.trim();
                        Ok(())
                    })
                });
                if store.is_err() {
                    return TT_ERR_PANIC;
                }
            }
            status
        }
        Ok(Err(error)) => error.status_code(),
        Err(_) => TT_ERR_PANIC,
    }
}

/// 差分两个快照，结果写进 `out`。
///
/// # Safety
///
/// 指针参数必须由调用方保证有效：`payload`/`len` 指向可读内存，
/// `out`/`cap` 指向可写内存（`out` 为空且 `cap` 为 0 时按「查询所需长度」处理）。
#[unsafe(no_mangle)]
pub unsafe extern "C" fn tt_rt_diff(
    handle: u64,
    snapshot_a: u64,
    snapshot_b: u64,
    out: *mut u8,
    cap: usize,
) -> i32 {
    let mut writer = Writer::new();
    writer.u64(snapshot_a);
    writer.u64(snapshot_b);
    let payload = writer.into_vec();
    let outcome = std::panic::catch_unwind(|| {
        let bytes = unsafe { payload_slice(payload.as_ptr(), payload.len()) };
        with_runtime(handle, |runtime| runtime.dispatch(op::DIFF, bytes))
    });
    match outcome {
        Ok(Ok(response)) => write_out(out, cap, &frame_response(&response)),
        Ok(Err(error)) => error.status_code(),
        Err(_) => TT_ERR_PANIC,
    }
}

/// 能力位图；句柄非法返回 0。
///
/// # Safety
///
/// 指针参数必须由调用方保证有效：`payload`/`len` 指向可读内存，
/// `out`/`cap` 指向可写内存（`out` 为空且 `cap` 为 0 时按「查询所需长度」处理）。
#[unsafe(no_mangle)]
pub unsafe extern "C" fn tt_rt_capabilities(handle: u64) -> u64 {
    let outcome = std::panic::catch_unwind(|| {
        with_runtime(handle, |runtime| {
            Ok::<u64, RtError>(runtime.capability_bits())
        })
    });
    match outcome {
        Ok(Ok(caps)) => caps,
        _ => 0,
    }
}

/// 最近一次错误信息；返回写入的字节数，缓冲区不足时回写所需长度并返回 `TT_ERR_BUFFER`。
///
/// # Safety
///
/// 指针参数必须由调用方保证有效：`payload`/`len` 指向可读内存，
/// `out`/`cap` 指向可写内存（`out` 为空且 `cap` 为 0 时按「查询所需长度」处理）。
#[unsafe(no_mangle)]
pub unsafe extern "C" fn tt_rt_last_error(handle: u64, buf: *mut u8, cap: usize) -> i32 {
    let outcome = std::panic::catch_unwind(|| {
        with_runtime(handle, |runtime| {
            Ok::<String, RtError>(runtime.last_error().to_string())
        })
    });
    let message = match outcome {
        Ok(Ok(message)) => message,
        _ => return TT_ERR_BAD_HANDLE,
    };
    let bytes = message.as_bytes();
    if buf.is_null() || cap < bytes.len() {
        if !buf.is_null() && cap >= 8 {
            let required = (bytes.len() as u64).to_le_bytes();
            unsafe {
                std::ptr::copy_nonoverlapping(required.as_ptr(), buf, 8);
            }
        }
        return TT_ERR_BUFFER;
    }
    unsafe {
        std::ptr::copy_nonoverlapping(bytes.as_ptr(), buf, bytes.len());
    }
    bytes.len() as i32
}
