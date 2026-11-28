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

/// 构造一整批 `STATE_PUSH` 载荷（NPC + 弹幕 + 玩家表，按 `(kind, slot)` 升序，
/// 与 mod 的采样顺序一致）。
fn build_states_payload(npcs: usize, projectiles: usize, players: usize) -> Vec<u8> {
    let mut writer = Writer::with_capacity(
        4 + ((npcs + projectiles) * wire::STATE_RECORD_FIXED_LEN)
            + 4
            + (players * wire::STATE_PLAYER_RECORD_LEN),
    );
    writer.u32((npcs + projectiles) as u32);
    for slot in 0..npcs {
        push_state_record(&mut writer, 1, slot as u16, 3);
    }
    for slot in 0..projectiles {
        push_state_record(&mut writer, 2, slot as u16, 1);
    }

    // 玩家表：均匀分布，覆盖最近玩家预测分支。
    writer.u32(players as u32);
    for slot in 0..players {
        writer
            .u16(slot as u16)
            .i32(wire::quantize_i32(
                (slot as f32 * 640.0) - 960.0,
                wire::STATE_POSITION_SCALE,
            ))
            .i32(0)
            .u8(wire::player_flags::ACTIVE);
    }
    writer.into_vec()
}

/// 写一条 28 字节定长状态记录（ai_mask = 0，不带变长 ai 值）。
fn push_state_record(writer: &mut Writer, kind: u8, slot: u16, entity_type: u16) {
    writer
        .u8(kind)
        .u16(slot)
        .u16(entity_type)
        .i16(wire::quantize_i16(1.5, wire::STATE_VELOCITY_SCALE))
        .i16(wire::quantize_i16(0.5, wire::STATE_VELOCITY_SCALE))
        .i8(if slot & 1 == 0 { 1 } else { -1 })
        .u8(wire::state_flags::ON_GROUND
            | wire::state_flags::TILE_BELOW
            | wire::state_flags::ACTIVE
            | wire::state_flags::HAS_TARGET
            | wire::state_flags::HAS_POSITION)
        .i16(wire::quantize_i16(2.0, wire::STATE_WALK_SPEED_SCALE))
        .u16(wire::quantize_u16(300.0, wire::STATE_TARGET_DISTANCE_SCALE))
        // 目标向量：交替左右，覆盖「按偏移转向」的解析分支。
        .i16(wire::quantize_i16(
            if slot & 1 == 0 { -300.0 } else { 300.0 },
            wire::STATE_TARGET_DELTA_SCALE,
        ))
        .i16(0)
        // 实体中心：沿 x 轴错开，让「最近玩家」有区分度。
        .i32(wire::quantize_i32(
            slot as f32 * 32.0,
            wire::STATE_POSITION_SCALE,
        ))
        .i32(0)
        .u8(0);
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
    // `--takeover` 额外付「STATE_PUSH + ACTION_PLAN」这两段（接管模式的真实序列）：
    // 每 tick 全量推一次实体状态，再取一次带置信度的计划。
    let takeover = args.iter().any(|a| a == "--takeover");
    // `--churn N`：每 7 tick 新建 N 个弹幕槽位（各 3 条车道），模拟战斗中
    // 弹幕不断生灭。世界结构因此每 7 tick 变一次，`refresh_world` 的键集重建
    // 也从冷路径变成热路径。只在前半程制造，避免世界无限增长。
    let churn = arg(&args, "--churn", 0);

    let lanes = all_lanes(npcs as u32, projectiles as u32, players as u32);
    let mut config = RuntimeConfig::default();
    if takeover {
        config.takeover = Some(trit_rt::takeover::TakeoverConfig {
            mode: trit_rt::takeover::TakeoverMode::PatchAfterOrig,
            prediction_interval_ticks: 1,
            ..trit_rt::takeover::TakeoverConfig::default()
        });
    }
    let mut runtime = TritRuntime::new(config);
    let states_payload = if takeover {
        build_states_payload(npcs, projectiles, players)
    } else {
        Vec::new()
    };
    let mut rng = Rng(0xD1B5_4A32_D192_ED03);
    let base = mem::rss_mb();
    println!(
        "lanes={} npcs={} proj={} players={} changes/tick={} ticks={} mode={} full_tick={} takeover={} base_rss={:.1}MB",
        lanes.len(),
        npcs,
        projectiles,
        players,
        changes,
        ticks,
        if one_step { "one-step" } else { "three-step" },
        full_tick,
        takeover,
        base.0
    );

    let mut payload = Vec::with_capacity(16 + changes * 5);
    let mut begin_failures = 0usize;
    // 分阶段耗时（微秒）：提交 / 状态推送 / 计划。
    let mut t_commit = 0.0f64;
    let mut t_states = 0.0f64;
    let mut t_plan = 0.0f64;
    let mut planned_total = 0u64;
    let mut churn_slot = projectiles;
    let started = std::time::Instant::now();

    for tick in 0..ticks {
        // 键按升序连续取（与 `WorldBridge` 的采样顺序一致：槽位与字段都递增），
        // 这样量到的才是真实热路径，而不是「乱序键」这条异常路径。
        let span = lanes.len() - changes;
        let start = (tick * 31) % span.max(1);
        let slice = &lanes[start..start + changes];

        let commit_started = std::time::Instant::now();
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
        t_commit += commit_started.elapsed().as_secs_f64() * 1e6;

        if churn > 0 && tick % 7 == 0 && tick < ticks / 2 && churn_slot < 65_000 {
            let mut writer = Writer::with_capacity(4 + churn * 3 * 5);
            writer.u32((churn * 3) as u32);
            for _ in 0..churn {
                let slot = churn_slot as u32;
                churn_slot += 1;
                for field in [0x5u32, 0x6, 0x7] {
                    writer.u32(lane_key(2, slot, field)).u8(2);
                }
            }
            runtime
                .dispatch(wire::op::TXN_APPLY, &writer.into_vec())
                .expect("churn");
        }

        if takeover {
            let started = std::time::Instant::now();
            runtime
                .dispatch(wire::op::STATE_PUSH, &states_payload)
                .expect("state push");
            t_states += started.elapsed().as_secs_f64() * 1e6;

            let started = std::time::Instant::now();
            let plan = runtime
                .dispatch(wire::op::ACTION_PLAN, b"")
                .expect("action plan");
            t_plan += started.elapsed().as_secs_f64() * 1e6;
            if plan.len() >= 4 {
                planned_total += u32::from_le_bytes(plan[..4].try_into().unwrap()) as u64;
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
    if takeover {
        println!(
            "phases per_tick: commit={:.3}ms state_push={:.3}ms action_plan={:.3}ms planned_avg={:.0}",
            t_commit / ticks as f64 / 1000.0,
            t_states / ticks as f64 / 1000.0,
            t_plan / ticks as f64 / 1000.0,
            planned_total as f64 / ticks as f64
        );
    }
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
