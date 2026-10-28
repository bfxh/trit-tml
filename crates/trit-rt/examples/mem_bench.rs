//! 长跑内存实测台（开发用，不进 DLL）。
//!
//! 目的：把「模组跑一段时间后内核到底吃多少内存、日志和快照各占多少」
//! 用数字摆出来——而不是靠读代码猜。
//!
//! 负载按模组真实路径模拟：
//! - 车道键沿用 `LaneMap` 编码（kind<<28 | slot<<12 | field）；
//! - 每 tick 提交约 1600 条变化，走 `TXN_SUBMIT_BATCH`（与 `WorldBridge` 一致）；
//! - 每 300 tick 落一张快照（`TritSettings.SnapshotIntervalTicks` 默认值）。
//!
//! 用法：
//!   cargo run --release -p trit-rt --example mem_bench
//!   cargo run --release -p trit-rt --example mem_bench -- --ticks 60000
//!   cargo run --release -p trit-rt --example mem_bench -- --log-entries 4194304
//!
//! `--log-entries` 直接对应模组设置里的 `maxLogEntries`，用来量「窗口换内存」
//! 的实际价码，而不是按字节数估算。

use trit_rt::runtime::{RuntimeConfig, TritRuntime};
use trit_rt::wire::{self, Writer};

#[cfg(windows)]
mod mem {
    use std::ffi::c_void;

    #[repr(C)]
    struct Pmc {
        cb: u32,
        page_faults: u32,
        peak_ws: usize,
        ws: usize,
        peak_paged: usize,
        paged: usize,
        peak_nonpaged: usize,
        nonpaged: usize,
        pagefile: usize,
        peak_pagefile: usize,
    }

    #[link(name = "kernel32")]
    unsafe extern "system" {
        fn GetCurrentProcess() -> *mut c_void;
        fn K32GetProcessMemoryInfo(handle: *mut c_void, counters: *mut Pmc, cb: u32) -> i32;
    }

    /// 返回 (当前工作集 MB, 峰值工作集 MB)。
    pub fn rss_mb() -> (f64, f64) {
        let mut counters: Pmc = unsafe { std::mem::zeroed() };
        counters.cb = std::mem::size_of::<Pmc>() as u32;
        let ok =
            unsafe { K32GetProcessMemoryInfo(GetCurrentProcess(), &mut counters, counters.cb) };
        if ok == 0 {
            return (0.0, 0.0);
        }
        (
            counters.ws as f64 / (1024.0 * 1024.0),
            counters.peak_ws as f64 / (1024.0 * 1024.0),
        )
    }
}

#[cfg(not(windows))]
mod mem {
    pub fn rss_mb() -> (f64, f64) {
        (0.0, 0.0)
    }
}

/// `LaneMap.Key` 的 Rust 同构：种类 4 位 | 槽位 16 位 | 字段 12 位。
fn lane_key(kind: u32, slot: u32, field: u32) -> u32 {
    (kind << 28) | (slot << 12) | field
}

/// 世界投影的车道集合：贴合模组的采样范围（NPC 200 / 弹幕 1000 / 玩家 255）。
fn all_lanes() -> Vec<u32> {
    let mut keys = Vec::with_capacity(5000);
    for slot in 0..200u32 {
        for field in [0x5, 0x6, 0x7] {
            keys.push(lane_key(1, slot, field));
        }
    }
    for slot in 0..1000u32 {
        for field in [0x5, 0x6, 0x7] {
            keys.push(lane_key(2, slot, field));
        }
    }
    for slot in 0..255u32 {
        for field in [0x1, 0x3, 0x4, 0x5, 0x6] {
            keys.push(lane_key(3, slot, field));
        }
    }
    keys
}

/// xorshift：确定性，保证每次跑的是同一份数据。
struct Rng(u64);

impl Rng {
    fn next(&mut self) -> u64 {
        self.0 ^= self.0 << 13;
        self.0 ^= self.0 >> 7;
        self.0 ^= self.0 << 17;
        self.0
    }
}

fn main() {
    let args: Vec<String> = std::env::args().collect();
    let ticks: usize = args
        .iter()
        .position(|a| a == "--ticks")
        .and_then(|i| args.get(i + 1))
        .and_then(|v| v.parse().ok())
        .unwrap_or(20_000);
    let snapshot_interval = 300;
    let changes_per_tick = 1600;

    let lanes = all_lanes();
    let mut config = RuntimeConfig::default();
    if let Some(entries) = args
        .iter()
        .position(|a| a == "--log-entries")
        .and_then(|i| args.get(i + 1))
        .and_then(|v| v.parse::<usize>().ok())
    {
        // 走 from_json 而不是直接赋值：这样量的就是模组真实经过
        // `RuntimeConfig::from_json` 的那条路径（含下限夹取）。
        config = RuntimeConfig::from_json(format!(r#"{{"max_log_entries":{entries}}}"#).as_bytes())
            .expect("config");
    }
    let mut runtime = TritRuntime::new(config);
    let mut rng = Rng(0x9E37_79B9_7F4A_7C15);
    let base = mem::rss_mb();
    println!(
        "lanes={} ticks={} changes/tick={} snapshot/{} tick rss={:.1}MB",
        lanes.len(),
        ticks,
        changes_per_tick,
        snapshot_interval,
        base.0
    );

    // 预分配一次批量载荷缓冲，避免把 example 自身的分配算进内核。
    let mut payload = Vec::with_capacity(16 + changes_per_tick * 5);
    let mut begin_failures = 0usize;

    for tick in 0..ticks {
        let mut begin = Writer::new();
        begin.u32(0);
        let begin = begin.into_vec();
        let txn_id = match runtime.dispatch(wire::op::TXN_BEGIN, &begin) {
            Ok(response) => Some(u64::from_le_bytes(response[..8].try_into().unwrap())),
            Err(_) => {
                // 事务表被占满（真机上是「跑到一定 tick 之后每 tick 都失败」）。
                begin_failures += 1;
                None
            }
        };

        if let Some(txn_id) = txn_id {
            payload.clear();
            let mut writer = Writer::new();
            writer.u64(txn_id).u32(changes_per_tick as u32);
            for _ in 0..changes_per_tick {
                let key = lanes[(rng.next() as usize) % lanes.len()];
                let delta = if rng.next() & 1 == 0 { 1 } else { 2 };
                writer.u32(key).u8(delta);
            }
            payload.extend_from_slice(&writer.into_vec());
            runtime
                .dispatch(wire::op::TXN_SUBMIT_BATCH, &payload)
                .expect("submit batch");

            let mut commit = Writer::new();
            commit.u64(txn_id);
            let commit = commit.into_vec();
            runtime
                .dispatch(wire::op::TXN_COMMIT, &commit)
                .expect("commit");
        }

        if snapshot_interval > 0 && tick > 0 && tick % snapshot_interval == 0 {
            let mut snap = Writer::new();
            snap.u32(0);
            let snap = snap.into_vec();
            runtime
                .dispatch(wire::op::SNAPSHOT, &snap)
                .expect("snapshot");
        }

        if tick % 2000 == 0 || tick + 1 == ticks {
            let (rss, peak) = mem::rss_mb();
            let world = query_u64(&mut runtime, wire::query::WORLD_SIZE);
            let log = query_u64(&mut runtime, wire::query::LOG_LEN);
            let snaps = query_u64(&mut runtime, wire::query::SNAPSHOT_COUNT);
            println!(
                "tick={tick:>6} rss={rss:>7.1}MB peak={peak:>7.1}MB world={world:>6} log={log:>8} snaps={snaps:>3} open_txn={:>5} begin_fail={begin_failures}",
                query_u64(&mut runtime, wire::query::OPEN_TXN_COUNT)
            );
        }
    }

    let (rss, peak) = mem::rss_mb();
    println!("done rss={rss:.1}MB peak={peak:.1}MB");

    // 退出存档的实测：SESSION_RESET 之后，内核占的内存必须真的还回去，
    // 而不是"清空了索引但页还挂着"。分配器可能保留一部分给下次复用，
    // 所以这里如实打印前后两个数，不做「应当等于基线」的断言。
    let before = mem::rss_mb().0;
    runtime
        .dispatch(wire::op::SESSION_RESET, &[])
        .expect("session reset");
    let after = mem::rss_mb().0;
    println!(
        "session_reset: rss {before:.1}MB -> {after:.1}MB（回收 {:.1}MB）world={} log={} snaps={}",
        before - after,
        query_u64(&mut runtime, wire::query::WORLD_SIZE),
        query_u64(&mut runtime, wire::query::LOG_LEN),
        query_u64(&mut runtime, wire::query::SNAPSHOT_COUNT),
    );
}

/// `WORLD_SIZE` / `SNAPSHOT_COUNT` 回 u32，`LOG_LEN` 回 u64，按实际宽度读。
fn query_u64(runtime: &mut TritRuntime, kind: u32) -> u64 {
    let mut writer = Writer::new();
    writer.u32(kind);
    let payload = writer.into_vec();
    let response = runtime.dispatch(wire::op::QUERY, &payload).expect("query");
    match response.len() {
        4 => u32::from_le_bytes(response[..4].try_into().unwrap()) as u64,
        8 => u64::from_le_bytes(response[..8].try_into().unwrap()),
        other => panic!("查询 {kind} 响应宽度异常：{other}"),
    }
}
