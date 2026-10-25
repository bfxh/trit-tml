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
pub const ABI_VERSION: u32 = 1;

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

type Registry = Mutex<BTreeMap<u64, TritRuntime>>;

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
    if handle == 0 {
        return Err(RtError::BadHandle(0));
    }
    let mut guard = registry()
        .lock()
        .unwrap_or_else(|poisoned| poisoned.into_inner());
    let runtime = guard.get_mut(&handle).ok_or(RtError::BadHandle(handle))?;
    f(runtime)
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
            .insert(handle, TritRuntime::new(config));
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
    let outcome = std::panic::catch_unwind(|| {
        let bytes = unsafe { payload_slice(payload, len) };
        with_runtime(handle, |runtime| runtime.dispatch(opcode, bytes))
    });
    match outcome {
        Ok(Ok(response)) => write_out(out, cap, &frame_response(&response)),
        Ok(Err(error)) => {
            if let Some(diagnostics) = conflict_diagnostics(&error) {
                let _ = write_out(out, cap, &frame_response(&diagnostics));
            }
            error.status_code()
        }
        Err(_) => TT_ERR_PANIC,
    }
}

/// 推进一个 tick（等价于 `submit(STEP, tick)`）。
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
        with_runtime(handle, |runtime| runtime.dispatch(op::STEP, bytes))
    });
    match outcome {
        Ok(Ok(response)) => write_out(out, cap, &frame_response(&response)),
        Ok(Err(error)) => error.status_code(),
        Err(_) => TT_ERR_PANIC,
    }
}

/// 打快照并把快照字节写进 `out`（装框：`u64` 快照 ID + 世界投影编码）。
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
        with_runtime(handle, |runtime| {
            let response = runtime.dispatch(op::SNAPSHOT, &payload)?;
            let snapshot_id = crate::wire::Reader::new(&response).u64()?;
            let bytes = runtime
                .snapshot_bytes(snapshot_id)
                .ok_or(RtError::SnapshotNotFound(snapshot_id))?;
            let mut writer = Writer::new();
            writer.u64(snapshot_id);
            writer.bytes(bytes);
            Ok::<Vec<u8>, RtError>(writer.into_vec())
        })
    });
    match outcome {
        Ok(Ok(response)) => write_out(out, cap, &frame_response(&response)),
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
