//! 并发安全测试：全部走 `tt_rt_*` C ABI，钉死 FFI 全局注册表
//! （`Mutex<BTreeMap<u64, RuntimeEntry>>` + 原子句柄分配）在多线程下的契约：
//!
//!   ① 并发 `create` 给出的句柄**互不重复且非 0**（`fetch_add` 唯一性）；
//!   ② 并发只读提交返回稳定一致的结果，绝不 panic / 死锁；
//!   ③ 不同句柄的世界状态**相互隔离**（一个句柄的写入不会串到另一个）；
//!   ④ 多个线程并发写**同一个句柄**，因全局锁串行化，写入不丢不崩；
//!   ⑤ `destroy` 与在飞的 `submit` 竞争时只返回合法状态码，绝不 use-after-free / panic；
//!   ⑥ 并发 `step` 推进不 panic；
//!   ⑦ 反复并发创建/销毁不复用句柄、销毁后句柄必须失效（无泄漏）。
//!
//! **测试隔离纪律（沿用 robustness.rs 的教训）**：cargo 会把本文件编译成独立测试
//! 可执行文件，句柄在本进程内从 1 起递增。每个测试**只销毁自己 create 的句柄**，
//! 绝不对猜测的句柄号调用 destroy，也绝不在测试之间共享同一个"待销毁"句柄，
//! 以免并行测试互相踩。唯一故意制造竞争的是 `destroy_racing_submit_never_panics`，
//! 它只断言状态码合法，不依赖具体时序，因此不会 flaky。

use std::collections::HashSet;
use std::sync::{Arc, Barrier, Mutex};

use trit_rt::ffi::*;
use trit_rt::wire::{Reader, Writer, op, query};

const THREADS: usize = 8;

/// 带自动扩容的 submit；返回 `(状态码, 解帧后的响应)`。
/// 与 ffi_abi.rs 同源，额外加了扩容上限防止畸形响应把测试进程拖垮。
unsafe fn submit(handle: u64, opcode: u32, payload: &[u8]) -> (i32, Vec<u8>) {
    let mut cap = 256usize;
    for _ in 0..12 {
        let mut out = vec![0u8; cap];
        let status = unsafe {
            tt_rt_submit(
                handle,
                opcode,
                payload.as_ptr(),
                payload.len(),
                out.as_mut_ptr(),
                cap,
            )
        };
        if status == TT_ERR_BUFFER && cap >= 8 {
            let need = u64::from_le_bytes(out[..8].try_into().unwrap()) as usize;
            if need > 32 * 1024 * 1024 {
                return (status, Vec::new());
            }
            cap = need.saturating_add(16).max(cap.saturating_mul(2));
            continue;
        }
        return match status {
            TT_OK | TT_ERR_CONFLICT => (
                status,
                trit_rt::wire::unframe_response(&out)
                    .unwrap_or_default()
                    .to_vec(),
            ),
            _ => (status, Vec::new()),
        };
    }
    (TT_ERR_BUFFER, Vec::new())
}

/// 查世界车道数（`WORLD_SIZE` 语义 = `world.len()`）。
fn query_world_size(handle: u64) -> u32 {
    let mut w = Writer::new();
    w.u32(query::WORLD_SIZE);
    let (status, resp) = unsafe { submit(handle, op::QUERY, &w.into_vec()) };
    assert_eq!(status, TT_OK);
    Reader::new(&resp).u32().unwrap()
}

/// 一条自动提交写（`txn_id == 0`，`delta = 2` 即 True/+1，会落地成一条车道）。
fn autocommit(handle: u64, key: u32) -> i32 {
    let mut w = Writer::new();
    w.u64(0).u32(key).u8(2);
    unsafe { submit(handle, op::TXN_SUBMIT, &w.into_vec()) }.0
}

/// 竞态用例的状态码断言：只要求落在已定义集合且不是 panic，不依赖时序。
fn assert_legal(status: i32) {
    assert_ne!(status, TT_ERR_PANIC, "并发调用不应触发 panic");
    assert!((-10..=0).contains(&status), "未定义状态码 {status}");
}

/// ① 并发 create 的句柄全局唯一、非 0。
#[test]
fn concurrent_create_yields_unique_handles() {
    let barrier = Arc::new(Barrier::new(THREADS));
    let mut joins = Vec::with_capacity(THREADS);
    for _ in 0..THREADS {
        let b = Arc::clone(&barrier);
        joins.push(std::thread::spawn(move || {
            b.wait();
            unsafe { tt_rt_create(std::ptr::null(), 0) }
        }));
    }
    let handles: Vec<u64> = joins.into_iter().map(|j| j.join().unwrap()).collect();

    assert!(handles.iter().all(|&h| h != 0), "并发 create 不应返回 0");
    let uniq: HashSet<u64> = handles.iter().copied().collect();
    assert_eq!(uniq.len(), THREADS, "并发 create 必须给出互不相同的句柄");

    // 只销毁本测试创建的句柄。
    for h in handles {
        assert_eq!(unsafe { tt_rt_destroy(h) }, TT_OK);
    }
}

/// ② 并发只读：capabilities 位图稳定、响应结构一致，全程不 panic / 不死锁。
#[test]
fn concurrent_readonly_submit_is_consistent() {
    let handle = unsafe { tt_rt_create(std::ptr::null(), 0) };
    assert_ne!(handle, 0);
    let expected_caps = unsafe { tt_rt_capabilities(handle) };

    let barrier = Arc::new(Barrier::new(THREADS));
    let mut joins = Vec::with_capacity(THREADS);
    for _ in 0..THREADS {
        let b = Arc::clone(&barrier);
        joins.push(std::thread::spawn(move || {
            b.wait();
            for _ in 0..120 {
                assert_eq!(unsafe { tt_rt_capabilities(handle) }, expected_caps);

                let (s, caps) = unsafe { submit(handle, op::CAPABILITIES, &[]) };
                assert_eq!(s, TT_OK);
                assert_eq!(Reader::new(&caps).u32().unwrap(), tt_rt_abi_version());

                let (s, stats) = unsafe { submit(handle, op::STATS, &[]) };
                assert_eq!(s, TT_OK);
                assert!(!stats.is_empty(), "STATS 响应不应为空");
            }
        }));
    }
    for j in joins {
        j.join().unwrap();
    }
    assert_eq!(unsafe { tt_rt_destroy(handle) }, TT_OK);
}

/// ③ 不同句柄的状态隔离：各写各的 key 段，各自的世界只含自己的写入。
#[test]
fn concurrent_writes_on_distinct_handles_are_isolated() {
    const PER: u32 = 24;
    let mut joins = Vec::with_capacity(THREADS);
    for t in 0..THREADS {
        joins.push(std::thread::spawn(move || {
            let h = unsafe { tt_rt_create(std::ptr::null(), 0) };
            assert_ne!(h, 0);
            let base = (t as u32) * 1000 + 1;
            for i in 0..PER {
                assert_eq!(autocommit(h, base + i), TT_OK);
            }
            assert_eq!(
                query_world_size(h),
                PER,
                "句柄 {h} 的世界应只含自己写入的 {PER} 条车道，不能串到别的句柄"
            );
            assert_eq!(unsafe { tt_rt_destroy(h) }, TT_OK);
        }));
    }
    for j in joins {
        j.join().unwrap();
    }
}

/// ④ 并发写同一个句柄：全局锁把 dispatch 串行化，写入不丢、状态不裂。
#[test]
fn concurrent_writes_on_one_handle_are_all_counted() {
    const PER: u32 = 24;
    let handle = unsafe { tt_rt_create(std::ptr::null(), 0) };
    assert_ne!(handle, 0);

    let barrier = Arc::new(Barrier::new(THREADS));
    let mut joins = Vec::with_capacity(THREADS);
    for t in 0..THREADS {
        let b = Arc::clone(&barrier);
        joins.push(std::thread::spawn(move || {
            b.wait();
            let base = (t as u32) * 1000 + 1; // 各线程 key 段不重叠，避免仲裁干扰
            for i in 0..PER {
                let status = autocommit(handle, base + i);
                assert!(status == TT_OK || status == TT_ERR_CONFLICT);
            }
        }));
    }
    for j in joins {
        j.join().unwrap();
    }

    assert_eq!(
        query_world_size(handle),
        (THREADS as u32) * PER,
        "全局锁串行化后，所有并发写入都应落地，一条都不能丢"
    );
    assert_eq!(unsafe { tt_rt_destroy(handle) }, TT_OK);
}

/// ⑤ destroy 与 submit 竞争：不依赖谁先谁后，只要求状态码合法、绝不 panic。
/// 这是 FFI 最关键的安全底线——玩家侧多线程热路径里销毁与调用可能撞上。
#[test]
fn destroy_racing_submit_never_panics() {
    let handle = unsafe { tt_rt_create(std::ptr::null(), 0) };
    assert_ne!(handle, 0);

    let barrier = Arc::new(Barrier::new(THREADS + 1));
    let mut joins = Vec::with_capacity(THREADS);
    for _ in 0..THREADS {
        let b = Arc::clone(&barrier);
        joins.push(std::thread::spawn(move || {
            b.wait();
            for _ in 0..200 {
                for opcode in [op::STATS, op::CAPABILITIES] {
                    let (s, _) = unsafe { submit(handle, opcode, &[]) };
                    assert_legal(s); // destroy 之前 OK、之后 BAD_HANDLE，都合法
                }
            }
        }));
    }
    let destroyer = {
        let b = Arc::clone(&barrier);
        std::thread::spawn(move || {
            b.wait();
            std::thread::yield_now();
            let r = unsafe { tt_rt_destroy(handle) };
            assert!(r == TT_OK || r == TT_ERR_BAD_HANDLE);
        })
    };

    for j in joins {
        j.join().unwrap();
    }
    destroyer.join().unwrap();
}

/// ⑥ 并发 step 推进：不 panic、状态码合法（cap 足够大，不触发缓存重试分支）。
#[test]
fn concurrent_step_never_panics() {
    let handle = unsafe { tt_rt_create(std::ptr::null(), 0) };
    assert_ne!(handle, 0);

    let barrier = Arc::new(Barrier::new(THREADS));
    let mut joins = Vec::with_capacity(THREADS);
    for _ in 0..THREADS {
        let b = Arc::clone(&barrier);
        joins.push(std::thread::spawn(move || {
            b.wait();
            for tick in 0..100u64 {
                let mut out = vec![0u8; 4096];
                let s = unsafe { tt_rt_step(handle, tick, out.as_mut_ptr(), out.len()) };
                assert_legal(s);
            }
        }));
    }
    for j in joins {
        j.join().unwrap();
    }
    assert_eq!(unsafe { tt_rt_destroy(handle) }, TT_OK);
}

/// ⑦ 并发创建/销毁压力：句柄不复用、销毁后失效，注册表不泄漏。
#[test]
fn concurrent_create_destroy_stress_is_leak_free() {
    const ROUNDS: usize = 40;
    let all = Arc::new(Mutex::new(Vec::with_capacity(THREADS * ROUNDS)));
    let barrier = Arc::new(Barrier::new(THREADS));
    let mut joins = Vec::with_capacity(THREADS);
    for _ in 0..THREADS {
        let b = Arc::clone(&barrier);
        let a = Arc::clone(&all);
        joins.push(std::thread::spawn(move || {
            b.wait();
            let mut local = Vec::with_capacity(ROUNDS);
            for _ in 0..ROUNDS {
                let h = unsafe { tt_rt_create(std::ptr::null(), 0) };
                assert_ne!(h, 0);
                let (s, _) = unsafe { submit(h, op::CAPABILITIES, &[]) };
                assert_eq!(s, TT_OK);
                assert_eq!(unsafe { tt_rt_destroy(h) }, TT_OK);
                local.push(h);
            }
            a.lock().unwrap().extend(local);
        }));
    }
    for j in joins {
        j.join().unwrap();
    }

    let guard = all.lock().unwrap();
    let uniq: HashSet<u64> = guard.iter().copied().collect();
    assert_eq!(
        uniq.len(),
        THREADS * ROUNDS,
        "反复创建/销毁不应复用或重复句柄"
    );
    // 全部销毁过一遍后再销毁必须报 BAD_HANDLE——证明 remove 真的生效、注册表没攒着旧条目。
    for &h in guard.iter() {
        assert_eq!(
            unsafe { tt_rt_destroy(h) },
            TT_ERR_BAD_HANDLE,
            "销毁后的句柄必须失效"
        );
    }
}
