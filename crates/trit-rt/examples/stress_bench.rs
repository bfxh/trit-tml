//! 重负载实测台（开发用，不进 DLL）。
//!
//! 目的：把「大量敌人 / 大量状态同时变化时，这层内核到底吃多少内存、
//! 每 tick 要多久」用数字摆出来。不靠估算，也不做「应该不会涨」这种判断。
//!
//! 负载按最坏情况构造：
//! - 车道数按 `--npcs/--projectiles/--players` 铺满（每个实体 3-5 个字段）；
//! - 每 tick 提交 `--changes` 条变化，走 `TXN_SUBMIT_BATCH`（与 `WorldBridge` 一致）；
//! - 每 300 tick 落一张快照；
//! - 打印 RSS、每 tick 平均耗时、以及日志/快照的实际计数。
//!
//! 用法：
//!   cargo run --release -p trit-rt --example stress_bench
//!   cargo run --release -p trit-rt --example stress_bench -- --npcs 5000 --projectiles 3000 --changes 20000 --ticks 6000

use trit_core::value::Ternary;
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

fn lane_key(kind: u32, slot: u32, field: u32) -> u32 {
    (kind << 28) | (slot << 12) | field
}

/// 每个 NPC 5 个字段（生命/增益位/状态时长/速度/朝向），每个弹幕 3 个，每个玩家 5 个。
fn all_lanes(npcs: u32, projectiles: u32, players: u32) -> Vec<u32> {
    let mut keys = Vec::with_capacity(((npcs + projectiles) * 5 + players * 5) as usize);
    for slot in 0..npcs {
        for field in [0x1, 0x3, 0x5, 0x6, 0x7] {
            keys.push(lane_key(1, slot, field));
        }
    }
    for slot in 0..projectiles {
        for field in [0x5, 0x6, 0x7] {
            keys.push(lane_key(2, slot, field));
        }
    }
    for slot in 0..players {
        for field in [0x1, 0x3, 0x5, 0x6, 0x7] {
            keys.push(lane_key(3, slot, field));
        }
    }
    keys
}

struct Rng(u64);

impl Rng {
    fn next(&mut self) -> u64 {
        self.0 ^= self.0 << 13;
        self.0 ^= self.0 >> 7;
        self.0 ^= self.0 << 17;
        self.0
    }
}

fn lanes_len(npcs: usize, projectiles: usize, players: usize) -> usize {
    npcs * 5 + projectiles * 3 + players * 5
}

fn arg(args: &[String], name: &str, default: usize) -> usize {
    args.iter()
        .position(|a| a == name)
        .and_then(|i| args.get(i + 1))
        .and_then(|v| v.parse().ok())
        .unwrap_or(default)
}

fn main() {
    let args: Vec<String> = std::env::args().collect();
    let ticks = arg(&args, "--ticks", 6000);
    let npcs = arg(&args, "--npcs", 2000);
    let projectiles = arg(&args, "--projectiles", 1000);
    let players = arg(&args, "--players", 4);
    let changes = arg(&args, "--changes", 8000).min(lanes_len(npcs, projectiles, players));
    // 提交路径：`three` = 开事务 + 批量提交 + 提交（旧热路径）；
    //           `one`   = TXN_APPLY 一步提交（新热路径）。
    let one_step = args.iter().any(|a| a == "--one-step");
    // `--full-tick` 额外付「发布 + Step」这两段（`WorldBridge.PublishAndStep` 的真实序列）：
    // 每 tick 1 条心跳事件，每 7 tick 再发 3 条活动事件（对应实体生灭的稀发节奏）。
    let full_tick = args.iter().any(|a| a == "--full-tick");

    let lanes = all_lanes(npcs as u32, projectiles as u32, players as u32);
    let mut runtime = TritRuntime::new(RuntimeConfig::default());
    let mut rng = Rng(0xD1B5_4A32_D192_ED03);
    let base = mem::rss_mb();
    println!(
        "lanes={} npcs={} proj={} players={} changes/tick={} ticks={} mode={} full_tick={} base_rss={:.1}MB",
        lanes.len(),
        npcs,
        projectiles,
        players,
        changes,
        ticks,
        if one_step { "one-step" } else { "three-step" },
        full_tick,
        base.0
    );

    let mut payload = Vec::with_capacity(16 + changes * 5);
    let mut begin_failures = 0usize;
    let started = std::time::Instant::now();

    for tick in 0..ticks {
        // 键按升序连续取（与 `WorldBridge` 的采样顺序一致：槽位与字段都递增），
        // 这样量到的才是真实热路径，而不是「乱序键」这条异常路径。
        let span = lanes.len() - changes;
        let start = (tick * 31) % span.max(1);
        let slice = &lanes[start..start + changes];

        if one_step {
            let mut writer = Writer::new();
            writer.u32(changes as u32);
            for key in slice {
                // 只有「真的变了」的车道才会进批量载荷，所以增量只取 -1 / +1，不含弃权。
                let delta = if rng.next() & 1 == 0 { 0u8 } else { 2u8 };
                writer.u32(*key).u8(delta);
            }
            payload.clear();
            payload.extend_from_slice(&writer.into_vec());
            runtime
                .dispatch(wire::op::TXN_APPLY, &payload)
                .expect("apply batch");
        } else {
            let mut begin = Writer::new();
            begin.u32(0);
            let begin = begin.into_vec();
            let txn_id = match runtime.dispatch(wire::op::TXN_BEGIN, &begin) {
                Ok(response) => Some(u64::from_le_bytes(response[..8].try_into().unwrap())),
                Err(_) => {
                    begin_failures += 1;
                    None
                }
            };

            if let Some(txn_id) = txn_id {
                let mut writer = Writer::new();
                writer.u64(txn_id).u32(changes as u32);
                for key in slice {
                    let delta = if rng.next() & 1 == 0 { 0u8 } else { 2u8 };
                    writer.u32(*key).u8(delta);
                }
                payload.clear();
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
        }

        if full_tick {
            // 与 `WorldBridge.PublishAndStep` 同序：先发心跳，再发稀发活动事件，最后 Step。
            let mut beat = Writer::new();
            beat.u32(1);
            beat.trits(&[Ternary::True, Ternary::False, Ternary::True]);
            runtime
                .dispatch(wire::op::PUBLISH, &beat.into_vec())
                .expect("publish heartbeat");

            if tick % 7 == 0 {
                for event_id in [2u32, 3, 4] {
                    let mut activity = Writer::new();
                    activity.u32(event_id);
                    activity.trits(&[Ternary::True]);
                    runtime
                        .dispatch(wire::op::PUBLISH, &activity.into_vec())
                        .expect("publish activity");
                }
            }

            let mut step = Writer::new();
            step.u64(tick as u64);
            runtime
                .dispatch(wire::op::STEP, &step.into_vec())
                .expect("step");
        }

        if tick > 0 && tick % 300 == 0 {
            let mut snap = Writer::new();
            snap.u32(0);
            let snap = snap.into_vec();
            runtime
                .dispatch(wire::op::SNAPSHOT, &snap)
                .expect("snapshot");
        }

        if tick % 1000 == 0 || tick + 1 == ticks {
            let (rss, peak) = mem::rss_mb();
            println!(
                "tick={tick:>5} rss={rss:>7.1}MB peak={peak:>7.1}MB world={:>6} log={:>8} snaps={:>3} begin_fail={begin_failures}",
                query_u64(&mut runtime, wire::query::WORLD_SIZE),
                query_u64(&mut runtime, wire::query::LOG_LEN),
                query_u64(&mut runtime, wire::query::SNAPSHOT_COUNT),
            );
        }
    }

    let elapsed = started.elapsed();
    let (rss, peak) = mem::rss_mb();
    println!(
        "done ticks={ticks} elapsed={:.1}ms per_tick={:.3}ms rss={rss:.1}MB peak={peak:.1}MB begin_fail={begin_failures}",
        elapsed.as_secs_f64() * 1000.0,
        elapsed.as_secs_f64() * 1000.0 / ticks as f64
    );
}

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
