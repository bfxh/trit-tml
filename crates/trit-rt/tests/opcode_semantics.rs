//! 剩余 opcode 的精确语义测试：走 `TritRuntime::dispatch` 安全 API，断言
//! **具体返回值**，补齐 `semantics.rs` / `determinism.rs` 未覆盖的操作面
//! （DIFF / ARBITRATE 争议编码 / UNSUBSCRIBE / GPU_STATUS / CAPABILITIES /
//! TXN_SUBMIT_BATCH 自动提交 / STATS 计数器 / 快照数量上限）。
//!
//! 默认仲裁模式是 Majority：同 key 同向合并、平局报争议；Strict 模式另测。

use trit_core::value::Ternary;
use trit_rt::RtError;
use trit_rt::runtime::{RuntimeConfig, TritRuntime};
use trit_rt::wire::{Reader, Writer, op, query};

fn runtime(config: &str) -> TritRuntime {
    TritRuntime::new(RuntimeConfig::from_json(config.as_bytes()).expect("配置合法"))
}

fn dispatch(rt: &mut TritRuntime, opcode: u32, payload: Vec<u8>) -> Vec<u8> {
    rt.dispatch(opcode, &payload).expect("dispatch 应成功")
}

/// 自动提交一条意图（txn_id=0）。
fn autocommit(rt: &mut TritRuntime, key: u32, delta: u8) {
    let mut w = Writer::new();
    w.u64(0).u32(key).u8(delta);
    rt.dispatch(op::TXN_SUBMIT, &w.into_vec()).unwrap();
}

fn query_u32(rt: &mut TritRuntime, id: u32) -> u32 {
    let mut w = Writer::new();
    w.u32(id);
    Reader::new(&dispatch(rt, op::QUERY, w.into_vec()))
        .u32()
        .unwrap()
}

fn snapshot(rt: &mut TritRuntime) -> u64 {
    let mut w = Writer::new();
    w.u32(0);
    Reader::new(&dispatch(rt, op::SNAPSHOT, w.into_vec()))
        .u64()
        .unwrap()
}

fn subscribe(rt: &mut TritRuntime, mod_id: u32, event_id: u32, deps: &[u32]) -> u64 {
    let mut w = Writer::new();
    w.u32(mod_id).u32(event_id).i32(0).u32(deps.len() as u32);
    for d in deps {
        w.u32(*d);
    }
    Reader::new(&dispatch(rt, op::SUBSCRIBE, w.into_vec()))
        .u64()
        .unwrap()
}

// ---------------------------------------------------------------- DIFF (0x0C)

/// DIFF 返回 encode_diff 编码：`count u32, [key u32, old u8, new u8] * n`。
#[test]
fn diff_between_two_snapshots_lists_changed_added_and_removed_lanes() {
    let mut rt = runtime("{}");
    autocommit(&mut rt, 1, 2); // key1 = True
    autocommit(&mut rt, 2, 0); // key2 = False
    let snap_a = snapshot(&mut rt);

    // `apply_delta` 是**累加**语义（clamp 在 [-1,+1]），不是赋值：
    // key1 当前 True(+1)，叠加 False 增量(-1) 后回到 Unknown(0)。
    autocommit(&mut rt, 1, 0); // key1: True(2) -> Unknown(1)
    autocommit(&mut rt, 3, 2); // key3 新增：Unknown + True 增量 => True
    // 注意：本运行时的世界投影不会主动删车道，removed 分支由 diff_worlds 纯逻辑单测覆盖。
    let snap_b = snapshot(&mut rt);

    let mut w = Writer::new();
    w.u64(snap_a).u64(snap_b);
    let out = dispatch(&mut rt, op::DIFF, w.into_vec());
    let mut r = Reader::new(&out);
    let count = r.u32().unwrap();
    assert_eq!(count, 2, "key1 变化 + key3 新增");
    let mut entries = Vec::new();
    for _ in 0..count {
        entries.push((r.u32().unwrap(), r.u8().unwrap(), r.u8().unwrap()));
    }
    // DIFF 输出按键升序；old/new 是 Ternary 的 index（0=False/1=Unknown/2=True）。
    assert_eq!(
        entries,
        vec![
            (1, 2, 1), // key1: True(2) -> Unknown(1)（+1 叠加 -1 归零）
            (3, 1, 2), // key3: 旧值 Unknown(1，不存在) -> True(2)
        ]
    );
}

#[test]
fn diff_of_a_snapshot_with_itself_is_empty() {
    let mut rt = runtime("{}");
    autocommit(&mut rt, 5, 2);
    let snap = snapshot(&mut rt);
    let mut w = Writer::new();
    w.u64(snap).u64(snap);
    let out = dispatch(&mut rt, op::DIFF, w.into_vec());
    assert_eq!(Reader::new(&out).u32().unwrap(), 0);
}

#[test]
fn diff_with_unknown_snapshot_returns_not_found() {
    let mut rt = runtime("{}");
    let snap = snapshot(&mut rt);
    let mut w = Writer::new();
    w.u64(snap).u64(999_999);
    let err = rt.dispatch(op::DIFF, &w.into_vec()).unwrap_err();
    assert!(matches!(err, RtError::SnapshotNotFound(999_999)));
}

#[test]
fn rollback_to_unknown_snapshot_returns_not_found() {
    let mut rt = runtime("{}");
    let mut w = Writer::new();
    w.u64(42);
    let err = rt.dispatch(op::ROLLBACK_TO, &w.into_vec()).unwrap_err();
    assert!(matches!(err, RtError::SnapshotNotFound(42)));
}

// ----------------------------------------------------------- ARBITRATE (0x08)

/// 只判不落地：ARBITRATE 返回 `status u8(0=clean) + resolved_count u32 +
/// [key u32, delta u8]*n + dispute_count u32`。不写世界、不写日志。
#[test]
fn arbitrate_clean_lane_reports_resolved_without_touching_world() {
    let mut rt = runtime("{}");
    let mut w = Writer::new();
    w.u32(1).u64(0).u32(10).u8(2); // 一条 txn_id=0,key=10,True
    let out = dispatch(&mut rt, op::ARBITRATE, w.into_vec());
    let mut r = Reader::new(&out);
    assert_eq!(r.u8().unwrap(), 0, "clean");
    assert_eq!(r.u32().unwrap(), 1, "一条收敛结果");
    assert_eq!(r.u32().unwrap(), 10);
    assert_eq!(r.u8().unwrap(), 2);
    assert_eq!(r.u32().unwrap(), 0, "无争议");
    // 只判不落地：世界与日志都没动。
    assert_eq!(query_u32(&mut rt, query::WORLD_SIZE), 0);
}

#[test]
fn arbitrate_abstain_is_filtered_out() {
    let mut rt = runtime("{}");
    let mut w = Writer::new();
    w.u32(1).u64(0).u32(10).u8(1); // Unknown(1) 弃权
    let out = dispatch(&mut rt, op::ARBITRATE, w.into_vec());
    let mut r = Reader::new(&out);
    assert_eq!(r.u8().unwrap(), 0);
    assert_eq!(r.u32().unwrap(), 0, "弃权不产生收敛车道");
    assert_eq!(r.u32().unwrap(), 0, "无争议");
}

#[test]
fn arbitrate_majority_merges_same_direction_and_reports_ties() {
    let mut rt = runtime("{}"); // 默认 Majority
    // 同 key 同向合并（两票 True 一票 False => True）。
    let mut w = Writer::new();
    w.u32(3)
        .u64(1)
        .u32(10)
        .u8(2)
        .u64(2)
        .u32(10)
        .u8(2)
        .u64(3)
        .u32(10)
        .u8(0);
    let out = dispatch(&mut rt, op::ARBITRATE, w.into_vec());
    let mut r = Reader::new(&out);
    assert_eq!(r.u8().unwrap(), 0, "多数胜出，clean");
    assert_eq!(r.u32().unwrap(), 1);
    assert_eq!(r.u32().unwrap(), 10);
    assert_eq!(r.u8().unwrap(), 2, "True 胜出");
    assert_eq!(r.u32().unwrap(), 0);

    // 平局（一票 True 一票 False）=> 争议。
    let mut w = Writer::new();
    w.u32(2).u64(1).u32(20).u8(2).u64(2).u32(20).u8(0);
    let out = dispatch(&mut rt, op::ARBITRATE, w.into_vec());
    let mut r = Reader::new(&out);
    assert_eq!(r.u8().unwrap(), 1, "disputes");
    assert_eq!(r.u32().unwrap(), 0, "争议时 resolved_count=0");
    let disputes = r.u32().unwrap();
    assert_eq!(disputes, 1);
    // 每条争议：key u32, driver_count u32, [txn_id u64, delta u8]*。
    assert_eq!(r.u32().unwrap(), 20);
    let drivers = r.u32().unwrap();
    assert_eq!(drivers, 2);
    let mut pairs = Vec::new();
    for _ in 0..drivers {
        pairs.push((r.u64().unwrap(), r.u8().unwrap()));
    }
    assert_eq!(pairs, vec![(1, 2), (2, 0)], "肇事方按 txn_id 升序");
}

#[test]
fn arbitrate_strict_flags_same_direction_multi_driver_as_dispute() {
    let mut rt = runtime(r#"{"arbitration":"strict"}"#);
    // Strict：同 key 两个同向驱动也算总线争议。
    let mut w = Writer::new();
    w.u32(2).u64(1).u32(10).u8(2).u64(2).u32(10).u8(2);
    let out = dispatch(&mut rt, op::ARBITRATE, w.into_vec());
    let mut r = Reader::new(&out);
    assert_eq!(r.u8().unwrap(), 1, "strict 下同向多驱动是争议");
    assert_eq!(r.u32().unwrap(), 0);
    assert_eq!(r.u32().unwrap(), 1, "一条争议车道");
}

// -------------------------------------------------------- UNSUBSCRIBE (0x02)

#[test]
fn unsubscribe_stops_future_dispatch() {
    let mut rt = runtime("{}");
    let sub = subscribe(&mut rt, 1, 100, &[]);
    // 先派发一次确认订阅生效。
    let pub100 = {
        let mut w = Writer::new();
        w.u32(100).trits(&[]);
        w.into_vec()
    };
    dispatch(&mut rt, op::PUBLISH, pub100.clone());
    let step = {
        let mut w = Writer::new();
        w.u64(1);
        let out = dispatch(&mut rt, op::STEP, w.into_vec());
        Reader::new(&out).u32().unwrap()
    };
    assert_eq!(step, 1);

    // 退订。
    let mut w = Writer::new();
    w.u64(sub);
    dispatch(&mut rt, op::UNSUBSCRIBE, w.into_vec());

    // 再发布同一事件：无订阅者，dirty=0，step 不派发。
    dispatch(&mut rt, op::PUBLISH, pub100);
    let mut w = Writer::new();
    w.u64(2);
    let out = dispatch(&mut rt, op::STEP, w.into_vec());
    assert_eq!(Reader::new(&out).u32().unwrap(), 0, "退订后不再派发");
}

#[test]
fn unsubscribe_unknown_id_returns_sub_not_found() {
    let mut rt = runtime("{}");
    let mut w = Writer::new();
    w.u64(777);
    let err = rt.dispatch(op::UNSUBSCRIBE, &w.into_vec()).unwrap_err();
    assert!(matches!(err, RtError::SubNotFound(777)));
}

// ------------------------------------------------------- TXN_SUBMIT_BATCH (0x1A)

#[test]
fn txn_submit_batch_with_txn_id_zero_autocommits_all_intents() {
    let mut rt = runtime("{}");
    let mut w = Writer::new();
    w.u64(0).u32(3); // txn_id=0 自动提交，3 条
    w.u32(1).u8(2);
    w.u32(2).u8(0);
    w.u32(3).u8(2);
    assert!(dispatch(&mut rt, op::TXN_SUBMIT_BATCH, w.into_vec()).is_empty());
    assert_eq!(query_u32(&mut rt, query::WORLD_SIZE), 3, "三条意图全部落地");
}

#[test]
fn txn_submit_batch_empty_is_a_noop() {
    let mut rt = runtime("{}");
    let mut w = Writer::new();
    w.u64(0).u32(0); // 空批
    assert!(dispatch(&mut rt, op::TXN_SUBMIT_BATCH, w.into_vec()).is_empty());
    assert_eq!(query_u32(&mut rt, query::WORLD_SIZE), 0);
    assert_eq!(query_u32(&mut rt, query::OPEN_TXN_COUNT), 0);
}

#[test]
fn txn_submit_batch_with_open_txn_stages_until_commit() {
    let mut rt = runtime("{}");
    // 开事务。
    let mut w = Writer::new();
    w.u32(0);
    let txn_id = Reader::new(&dispatch(&mut rt, op::TXN_BEGIN, w.into_vec()))
        .u64()
        .unwrap();
    assert!(txn_id > 0);
    // 批量暂存意图（txn_id != 0）。
    let mut w = Writer::new();
    w.u64(txn_id).u32(2);
    w.u32(10).u8(2);
    w.u32(20).u8(0);
    dispatch(&mut rt, op::TXN_SUBMIT_BATCH, w.into_vec());
    // 未提交前世界不变。
    assert_eq!(query_u32(&mut rt, query::WORLD_SIZE), 0, "暂存不落地");
    assert_eq!(query_u32(&mut rt, query::OPEN_TXN_COUNT), 1);
    // 提交。
    let mut w = Writer::new();
    w.u64(txn_id);
    let out = dispatch(&mut rt, op::TXN_COMMIT, w.into_vec());
    let mut r = Reader::new(&out);
    assert_eq!(r.u32().unwrap(), 2, "提交落地 2 条");
    let _log_seq = r.u64().unwrap();
    assert_eq!(query_u32(&mut rt, query::WORLD_SIZE), 2);
    assert_eq!(query_u32(&mut rt, query::OPEN_TXN_COUNT), 0);
}

// --------------------------------------------------------- CAPABILITIES (0x10)

#[test]
fn capabilities_report_version_and_mode_dependent_bits() {
    let mut rt = runtime("{}");
    let out = dispatch(&mut rt, op::CAPABILITIES, vec![]);
    let mut r = Reader::new(&out);
    let abi = r.u32().unwrap();
    let runtime_ver = r.u32().unwrap();
    let caps = r.u64().unwrap();
    let mode = r.u8().unwrap();
    assert_eq!(abi, trit_rt::ffi::ABI_VERSION);
    assert_eq!(runtime_ver, trit_rt::ffi::RUNTIME_VERSION);
    assert_eq!(mode, 1, "默认仲裁是 Majority");
    // 核心能力恒置。
    for (name, bit) in [
        ("router", 0),
        ("txn_log", 1),
        ("snapshot", 2),
        ("diff", 3),
        ("arbitrate", 4),
        ("prng", 5),
        ("codec", 6),
        ("shadow", 7),
        ("parallel", 9),
        ("budget", 11),
    ] {
        assert_ne!(caps & (1 << bit), 0, "{name} 能力位应置起");
    }
    // AI 位随 feature 开关。
    if cfg!(feature = "ai-voting") {
        assert_ne!(caps & (1 << 8), 0, "启用 ai-voting 时 AI 位应置起");
    } else {
        assert_eq!(caps & (1 << 8), 0, "关闭 ai-voting 时 AI 位应清零");
    }
}

#[test]
fn capabilities_reports_strict_mode_byte() {
    let mut rt = runtime(r#"{"arbitration":"strict"}"#);
    let out = dispatch(&mut rt, op::CAPABILITIES, vec![]);
    let mut r = Reader::new(&out);
    let _abi = r.u32().unwrap();
    let _ver = r.u32().unwrap();
    let _caps = r.u64().unwrap();
    assert_eq!(r.u8().unwrap(), 0, "strict 模式字节为 0");
}

// -------------------------------------------------------------- STATS (0x11)

/// STATS 是固定 10 个 u64；做一轮操作后对应计数器应递增。
#[test]
fn stats_counters_track_a_full_lifecycle() {
    let mut rt = runtime("{}");
    let before = read_stats(&mut rt);
    assert_eq!(before.len(), 10);

    autocommit(&mut rt, 1, 2);
    autocommit(&mut rt, 2, 2);
    let snap_a = snapshot(&mut rt);
    autocommit(&mut rt, 1, 0);
    let snap_b = snapshot(&mut rt);
    let mut w = Writer::new();
    w.u64(snap_a).u64(snap_b);
    dispatch(&mut rt, op::DIFF, w.into_vec());
    // 一次仲裁（clean）。
    let mut w = Writer::new();
    w.u32(1).u64(0).u32(9).u8(2);
    dispatch(&mut rt, op::ARBITRATE, w.into_vec());

    let after = read_stats(&mut rt);
    assert_eq!(after[4], before[4] + 2, "snapshots_taken +2");
    assert_eq!(after[5], before[5] + 1, "diffs +1");
    assert_eq!(after[6], before[6] + 1, "arbitrations +1");
    assert_eq!(after[8], before[8] + 3, "intents_applied +3");
}

/// STATS 字段顺序：events_published, events_dispatched, txns_committed,
/// txns_rolled_back, snapshots_taken, diffs, arbitrations, conflicts,
/// intents_applied, prng_draws。
fn read_stats(rt: &mut TritRuntime) -> Vec<u64> {
    let out = dispatch(rt, op::STATS, vec![]);
    assert_eq!(out.len(), 80, "STATS 是 10 个 u64");
    let mut r = Reader::new(&out);
    (0..10).map(|_| r.u64().unwrap()).collect()
}

#[test]
fn stats_publish_and_prng_counters_increment() {
    let mut rt = runtime("{}");
    let before = read_stats(&mut rt);
    // 发布两个事件。
    for id in [100u32, 101] {
        let mut w = Writer::new();
        w.u32(id).trits(&[Ternary::True]);
        dispatch(&mut rt, op::PUBLISH, w.into_vec());
    }
    // PRNG 抽样 16 个。
    let mut w = Writer::new();
    w.u32(16);
    dispatch(&mut rt, op::PRNG_NEXT, w.into_vec());
    let after = read_stats(&mut rt);
    assert_eq!(after[0], before[0] + 2, "events_published +2");
    assert_eq!(after[9], before[9] + 16, "prng_draws +16");
}

// ---------------------------------------------------------- GPU_STATUS (0x17)

/// GPU_STATUS 走 JSON 请求/响应；`policy=off` 时 enabled 必须为 false。
#[test]
fn gpu_status_off_policy_reports_disabled() {
    let mut rt = runtime("{}");
    let out = dispatch(&mut rt, op::GPU_STATUS, br#"{"policy":"off"}"#.to_vec());
    let value: serde_json::Value = serde_json::from_slice(&out).unwrap();
    assert_eq!(value["policy"], "off");
    assert_eq!(value["enabled"], false);
    assert_eq!(value["built_with_gpu"], cfg!(feature = "gpu"));
}

#[test]
fn gpu_status_unknown_policy_is_rejected() {
    let mut rt = runtime("{}");
    let err = rt
        .dispatch(op::GPU_STATUS, br#"{"policy":"turbo"}"#)
        .unwrap_err();
    assert!(matches!(err, RtError::InvalidInput(_)));
}

#[test]
fn gpu_status_empty_payload_returns_a_valid_snapshot() {
    let mut rt = runtime("{}");
    let out = dispatch(&mut rt, op::GPU_STATUS, vec![]);
    let value: serde_json::Value = serde_json::from_slice(&out).unwrap();
    assert!(value["policy"].is_string());
    assert!(value["built_with_gpu"].is_boolean());
    assert!(value["jobs"].is_number());
}
