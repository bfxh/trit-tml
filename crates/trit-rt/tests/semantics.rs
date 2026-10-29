//! 精确语义测试：走 `TritRuntime::dispatch` 安全 API，逐条钉死
//! 「两条路径（原生 / 托管回退）本该同源」的语义契约。
//!
//! 与 `robustness.rs`（只断言不崩）互补：这里断言**具体结果**——
//! applied 数、log_seq、世界投影、查询计数、确定性序列。
//! 这些是 parity 对拍之外更细的护栏：parity 只能证明「两边恰好一样」，
//! 万一两边一起错，还得靠这些用例把正确值钉住。

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

fn autocommit(rt: &mut TritRuntime, key: u32, delta: u8) {
    let mut w = Writer::new();
    w.u64(0).u32(key).u8(delta);
    rt.dispatch(op::TXN_SUBMIT, &w.into_vec()).unwrap();
}

fn query_u32(rt: &mut TritRuntime, id: u32) -> u32 {
    let mut w = Writer::new();
    w.u32(id);
    let out = dispatch(rt, op::QUERY, w.into_vec());
    Reader::new(&out).u32().unwrap()
}

fn query_u64(rt: &mut TritRuntime, id: u32) -> u64 {
    let mut w = Writer::new();
    w.u32(id);
    let out = dispatch(rt, op::QUERY, w.into_vec());
    Reader::new(&out).u64().unwrap()
}

// ---------------------------------------------------------------- TXN_APPLY

/// 一步提交与三步流程对同一批变更逐字节等价（applied / log_seq / 世界）。
#[test]
fn txn_apply_equals_three_step_for_several_batches() {
    let batches: Vec<Vec<(u32, u8)>> = vec![
        vec![],
        vec![(1, 2)],
        vec![(1, 2), (2, 2), (3, 0)],
        vec![(10, 2), (10, 0), (20, 0), (30, 2)], // 同 key 反复写
        (0..64).map(|i| (i * 3 + 1, (i % 3) as u8)).collect(),
    ];

    for batch in batches {
        // 三步
        let mut three = runtime("{}");
        let txn = {
            let mut w = Writer::new();
            w.u32(0);
            let out = dispatch(&mut three, op::TXN_BEGIN, w.into_vec());
            Reader::new(&out).u64().unwrap()
        };
        {
            let mut w = Writer::new();
            w.u64(txn).u32(batch.len() as u32);
            for (key, delta) in &batch {
                w.u32(*key).u8(*delta);
            }
            three.dispatch(op::TXN_SUBMIT_BATCH, &w.into_vec()).unwrap();
        }
        let three_commit = {
            let mut w = Writer::new();
            w.u64(txn);
            three.dispatch(op::TXN_COMMIT, &w.into_vec())
        };

        // 一步
        let mut one = runtime("{}");
        let one_commit = {
            let mut w = Writer::new();
            w.u32(batch.len() as u32);
            for (key, delta) in &batch {
                w.u32(*key).u8(*delta);
            }
            one.dispatch(op::TXN_APPLY, &w.into_vec())
        };

        // 两条路径必须同源：要么都成功且逐字节一致，要么都是「同一种冲突」。
        // 批次里含「同 key 不同 driver」时仲裁会报 Conflict——这也是必须等价的场景，
        // 不能只测成功路径（旧写法在这里会 .expect panic，把冲突误判成测试失败）。
        //
        // 冲突诊断的 drivers 首分量是 txn_id：三步走显式事务（从 1 起递增），
        // TXN_APPLY 是自动提交（恒记 0）。这是**结构性差异**而非语义分歧——
        // 自动提交没有事务号可填。所以比「争议 key 集合 + 每条车道的驱动增量序列」，
        // 不比 txn_id 绝对值（也不比整串 Debug，那会把结构差异当成失败）。
        match (three_commit, one_commit) {
            (Ok(a), Ok(b)) => {
                assert_eq!(a, b, "批次 {batch:?} 的提交字节应一致");
                assert_eq!(
                    one.world(),
                    three.world(),
                    "批次 {batch:?} 的世界投影应一致"
                );
            }
            (Err(e1), Err(e2)) => match (&e1, &e2) {
                (
                    RtError::Conflict { disputes: d1, .. },
                    RtError::Conflict { disputes: d2, .. },
                ) => {
                    let keys1: Vec<u32> = d1.iter().map(|d| d.key).collect();
                    let keys2: Vec<u32> = d2.iter().map(|d| d.key).collect();
                    assert_eq!(keys1, keys2, "批次 {batch:?} 的争议车道应一致");
                    let drv1: Vec<Vec<Ternary>> = d1
                        .iter()
                        .map(|d| d.drivers.iter().map(|(_, t)| *t).collect())
                        .collect();
                    let drv2: Vec<Vec<Ternary>> = d2
                        .iter()
                        .map(|d| d.drivers.iter().map(|(_, t)| *t).collect())
                        .collect();
                    assert_eq!(drv1, drv2, "批次 {batch:?} 的驱动增量序列应一致");
                }
                _ => panic!("批次 {batch:?} 两边错误类型不同：三步={e1:?} 一步={e2:?}"),
            },
            (a, b) => panic!(
                "批次 {batch:?} 一条成功一条失败：三步={} 一步={}",
                a.is_ok(),
                b.is_ok()
            ),
        }
    }
}

#[test]
fn txn_apply_empty_batch_is_a_noop_but_reports_current_log_seq() {
    let mut rt = runtime("{}");
    autocommit(&mut rt, 1, 2); // 先让日志推进一格
    let before_seq = query_u64(&mut rt, query::LOG_LEN);
    let mut w = Writer::new();
    w.u32(0);
    let out = dispatch(&mut rt, op::TXN_APPLY, w.into_vec());
    let mut r = Reader::new(&out);
    assert_eq!(r.u32().unwrap(), 0, "空批次不落地");
    assert_eq!(
        r.u64().unwrap(),
        before_seq,
        "空批次返回当前 log_seq，不推进"
    );
}

// ------------------------------------------------------- 越界 trit 收敛为弃权

/// 线格式字节 3..=255 不是合法 trit，必须收敛成 `Unknown`（弃权），
/// 既不落地、也不创建世界车道、applied 也不增加。
/// 托管回退内核曾把这类字节当成一个「非法枚举值」当成真实驱动，
/// 与本用例钉住的原生语义分叉。
#[test]
fn out_of_range_trit_bytes_abstain_and_never_touch_the_world() {
    for byte in [3u8, 7, 42, 128, 254, 255] {
        let mut rt = runtime("{}");
        // 用 TXN_APPLY：count u32 + [key u32, delta u8]
        let mut apply = Writer::new();
        apply.u32(1).u32(9).u8(byte);
        let out = dispatch(&mut rt, op::TXN_APPLY, apply.into_vec());
        let applied = Reader::new(&out).u32().unwrap();
        assert_eq!(applied, 0, "字节 {byte} 应被当成弃权，不落地");
        assert_eq!(rt.world().len(), 0, "字节 {byte} 不应创建世界车道");
    }
}

// ------------------------------------------------------------------ QUERY 全表

#[test]
fn query_table_reports_consistent_counters_through_a_lifecycle() {
    let mut rt = runtime("{}");
    assert_eq!(query_u32(&mut rt, query::WORLD_SIZE), 0);
    assert_eq!(query_u32(&mut rt, query::SNAPSHOT_COUNT), 0);
    assert_eq!(query_u32(&mut rt, query::OPEN_TXN_COUNT), 0);

    // 订阅 + 发布 → dirty
    {
        let mut w = Writer::new();
        w.u32(1).u32(100).i32(0).u32(0);
        dispatch(&mut rt, op::SUBSCRIBE, w.into_vec());
    }
    {
        let mut w = Writer::new();
        w.u32(100).trits(&[Ternary::True]);
        dispatch(&mut rt, op::PUBLISH, w.into_vec());
    }
    assert_eq!(
        query_u32(&mut rt, query::DIRTY_COUNT),
        1,
        "发布后脏集合为 1"
    );

    // step 清空脏集合
    {
        let mut w = Writer::new();
        w.u64(1);
        dispatch(&mut rt, op::STEP, w.into_vec());
    }
    assert_eq!(
        query_u32(&mut rt, query::DIRTY_COUNT),
        0,
        "step 后脏集合清空"
    );

    // 开一个事务但不关 → open = 1
    {
        let mut w = Writer::new();
        w.u32(0);
        dispatch(&mut rt, op::TXN_BEGIN, w.into_vec());
    }
    assert_eq!(query_u32(&mut rt, query::OPEN_TXN_COUNT), 1);

    // 两条自动提交 → world 有两条、日志两条
    autocommit(&mut rt, 1, 2);
    autocommit(&mut rt, 2, 2);
    assert_eq!(query_u32(&mut rt, query::WORLD_SIZE), 2);
    assert_eq!(query_u64(&mut rt, query::LOG_LEN), 2);

    // 打一张快照 → snapshot count = 1
    {
        let mut w = Writer::new();
        w.u32(0);
        dispatch(&mut rt, op::SNAPSHOT, w.into_vec());
    }
    assert_eq!(query_u32(&mut rt, query::SNAPSHOT_COUNT), 1);
}

#[test]
fn shadow_query_counts_empty_and_nonempty_signatures() {
    let mut rt = runtime("{}");
    let payload = |values: &[Ternary]| {
        let mut w = Writer::new();
        w.trits(values);
        w.into_vec()
    };
    dispatch(&mut rt, op::SHADOW_RECORD, payload(&[]));
    dispatch(&mut rt, op::SHADOW_RECORD, payload(&[Ternary::True]));
    dispatch(
        &mut rt,
        op::SHADOW_RECORD,
        payload(&[Ternary::True, Ternary::False]),
    );

    let mut w = Writer::new();
    w.u32(query::SHADOW);
    let out = dispatch(&mut rt, op::QUERY, w.into_vec());
    let mut r = Reader::new(&out);
    assert_eq!(r.u64().unwrap(), 3, "compared = 3");
    assert_eq!(r.u64().unwrap(), 2, "divergences = 2（两个非空签名）");
}

#[test]
fn unknown_query_id_is_rejected_with_arg() {
    let mut rt = runtime("{}");
    let mut w = Writer::new();
    w.u32(999);
    let err = rt.dispatch(op::QUERY, &w.into_vec()).unwrap_err();
    assert_eq!(err.status_code(), trit_rt::ffi::TT_ERR_ARG);
}

// --------------------------------------------------------------- 事件载荷淘汰

/// 覆盖同一个 event id 时，字节计数必须先减旧再加新。
/// 用一个接近 8MB 上限的大载荷反复覆盖同一 id：若计数只加不减，
/// 会虚增到几倍上限，把**别的** id 的小载荷误淘汰——这是静默丢数据。
#[test]
fn republishing_the_same_event_id_does_not_evict_other_payloads() {
    let mut rt = runtime("{}");
    // 5MB trits 载荷（5 trits/byte 打包前是 500 万个元素即可，这里直接构造打包体）：
    // Writer::trits 会先写 count u32 再写 packed bytes。我们要的是 PUBLISH 的
    // 「event_id u32 + trits」结构，trits 内部自己带 count。
    let big_trits = vec![Ternary::True; 5_000_000];
    let publish = |event_id: u32, trits: &[Ternary]| {
        let mut w = Writer::new();
        w.u32(event_id).trits(trits);
        w.into_vec()
    };
    let read_payload = |rt: &mut TritRuntime, event_id: u32| -> Vec<Ternary> {
        let mut w = Writer::new();
        w.u32(6).u32(event_id);
        let out = dispatch(rt, op::QUERY, w.into_vec());
        Reader::new(&out).trits().unwrap()
    };

    // 小载荷先放一个别人的事件。
    dispatch(
        &mut rt,
        op::PUBLISH,
        publish(7, &[Ternary::True, Ternary::False]),
    );
    // 反复覆盖 id=1：每次都该先扣掉上一次的 5MB。
    for _ in 0..4 {
        dispatch(&mut rt, op::PUBLISH, publish(1, &big_trits));
    }
    // 别人的小载荷必须还在（没被虚增计数挤掉）。
    assert_eq!(read_payload(&mut rt, 7).len(), 2, "别人的载荷不能被误淘汰");
    // 自己最近一次覆盖仍可读到。
    assert_eq!(read_payload(&mut rt, 1).len(), 5_000_000);
}

/// 条数闸门：连续发布超过 MAX_EVENT_PAYLOADS(4096) 个不同 id，
/// 最旧的被淘汰，但**刚发布的那条**永远保留（不能让 publish 静默无效）。
#[test]
fn event_payload_count_gate_evicts_oldest_but_keeps_latest() {
    let mut rt = runtime("{}");
    let publish = |event_id: u32| {
        let mut w = Writer::new();
        w.u32(event_id).trits(&[Ternary::True]);
        w.into_vec()
    };
    let read = |rt: &mut TritRuntime, event_id: u32| -> Vec<Ternary> {
        let mut w = Writer::new();
        w.u32(6).u32(event_id);
        let out = dispatch(rt, op::QUERY, w.into_vec());
        Reader::new(&out).trits().unwrap()
    };
    for id in 0..4098u32 {
        dispatch(&mut rt, op::PUBLISH, publish(id));
    }
    assert!(read(&mut rt, 0).is_empty(), "最旧的应被淘汰");
    assert_eq!(read(&mut rt, 4097).len(), 1, "刚发布的必须保留");
}

// ------------------------------------------------------------------- PRNG

#[test]
fn prng_is_deterministic_for_same_seed_and_tick() {
    let draw = |seed: u64, tick: u64| -> Vec<u8> {
        let mut rt = runtime(&format!("{{\"seed\":{seed}}}"));
        {
            let mut w = Writer::new();
            w.u64(tick);
            dispatch(&mut rt, op::STEP, w.into_vec());
        }
        let mut w = Writer::new();
        w.u32(256);
        dispatch(&mut rt, op::PRNG_NEXT, w.into_vec())
    };
    assert_eq!(draw(42, 1), draw(42, 1), "同 seed 同 tick 必须逐字节一致");
    assert_ne!(draw(42, 1), draw(43, 1), "不同 seed 必须分叉");
    assert_ne!(draw(42, 1), draw(42, 2), "同 seed 不同 tick 必须分叉");
}

#[test]
fn prng_next_rejects_absurd_count() {
    let mut rt = runtime("{}");
    let mut w = Writer::new();
    w.u32(1 << 20);
    let err = rt.dispatch(op::PRNG_NEXT, &w.into_vec()).unwrap_err();
    assert_eq!(err.status_code(), trit_rt::ffi::TT_ERR_ARG);
}

// ---------------------------------------------------------------- 订阅图

#[test]
fn self_dependency_is_rejected() {
    let mut rt = runtime("{}");
    let mut w = Writer::new();
    w.u32(1).u32(100).i32(0).u32(1).u32(100); // 依赖自己
    let err = rt.dispatch(op::SUBSCRIBE, &w.into_vec()).unwrap_err();
    // 依赖成环（含自依赖）统一回 ARG（InvalidInput）。
    assert_eq!(err.status_code(), trit_rt::ffi::TT_ERR_ARG);
}

#[test]
fn dependency_closure_dispatches_in_topological_order() {
    let mut rt = runtime("{}");
    let mk = |mod_id: u32, event_id: u32, deps: &[u32]| {
        let mut w = Writer::new();
        w.u32(mod_id).u32(event_id).i32(0).u32(deps.len() as u32);
        for d in deps {
            w.u32(*d);
        }
        w.into_vec()
    };
    let s_a = Reader::new(&dispatch(&mut rt, op::SUBSCRIBE, mk(1, 100, &[])))
        .u64()
        .unwrap();
    let s_b = Reader::new(&dispatch(&mut rt, op::SUBSCRIBE, mk(2, 101, &[100])))
        .u64()
        .unwrap();
    let s_c = Reader::new(&dispatch(&mut rt, op::SUBSCRIBE, mk(3, 102, &[101])))
        .u64()
        .unwrap();
    // 发布最上游事件，闭包应把 a→b→c 全标脏。
    {
        let mut w = Writer::new();
        w.u32(100).trits(&[]);
        dispatch(&mut rt, op::PUBLISH, w.into_vec());
    }
    {
        let mut w = Writer::new();
        w.u64(1);
        let out = dispatch(&mut rt, op::STEP, w.into_vec());
        let mut r = Reader::new(&out);
        let count = r.u32().unwrap();
        assert_eq!(count, 3);
        let mut order = Vec::new();
        for _ in 0..count {
            let sub = r.u64().unwrap();
            let _event = r.u32().unwrap();
            let _mod = r.u32().unwrap();
            order.push(sub);
        }
        assert_eq!(order, vec![s_a, s_b, s_c], "按依赖拓扑序派发");
    }
}

// -------------------------------------------------------------- SESSION_RESET

#[test]
fn session_reset_clears_world_state_but_keeps_subscriptions() {
    let mut rt = runtime("{}");
    // 建立订阅（属于装载期，复位要保留）。
    {
        let mut w = Writer::new();
        w.u32(1).u32(100).i32(0).u32(0);
        dispatch(&mut rt, op::SUBSCRIBE, w.into_vec());
    }
    autocommit(&mut rt, 1, 2);
    autocommit(&mut rt, 2, 2);
    {
        let mut w = Writer::new();
        w.u32(0);
        dispatch(&mut rt, op::SNAPSHOT, w.into_vec());
    }
    {
        // 留一个未关闭事务
        let mut w = Writer::new();
        w.u32(0);
        dispatch(&mut rt, op::TXN_BEGIN, w.into_vec());
    }
    {
        // 影子签名
        let mut w = Writer::new();
        w.trits(&[Ternary::True]);
        dispatch(&mut rt, op::SHADOW_RECORD, w.into_vec());
    }
    assert!(query_u32(&mut rt, query::WORLD_SIZE) > 0);

    // 复位（无载荷）。
    dispatch(&mut rt, op::SESSION_RESET, vec![]);

    assert_eq!(query_u32(&mut rt, query::WORLD_SIZE), 0, "世界清空");
    assert_eq!(query_u64(&mut rt, query::LOG_LEN), 0, "日志清空");
    assert_eq!(query_u32(&mut rt, query::SNAPSHOT_COUNT), 0, "快照清空");
    assert_eq!(query_u32(&mut rt, query::OPEN_TXN_COUNT), 0, "事务表清空");
    {
        let mut w = Writer::new();
        w.u32(query::SHADOW);
        let out = dispatch(&mut rt, op::QUERY, w.into_vec());
        let mut r = Reader::new(&out);
        assert_eq!(r.u64().unwrap(), 0, "影子统计归零");
        assert_eq!(r.u64().unwrap(), 0);
    }

    // 订阅仍在：发布 → step 仍能派发一条。
    {
        let mut w = Writer::new();
        w.u32(100).trits(&[]);
        dispatch(&mut rt, op::PUBLISH, w.into_vec());
    }
    {
        let mut w = Writer::new();
        w.u64(1);
        let out = dispatch(&mut rt, op::STEP, w.into_vec());
        assert_eq!(Reader::new(&out).u32().unwrap(), 1, "订阅关系跨复位保留");
    }
}

#[test]
fn session_reset_rejects_any_payload() {
    let mut rt = runtime("{}");
    let err = rt.dispatch(op::SESSION_RESET, &[0u8]).unwrap_err();
    assert_eq!(err.status_code(), trit_rt::ffi::TT_ERR_ARG);
}

// ---------------------------------------------------------------- 配置边界

#[test]
fn zero_capacities_are_clamped_to_a_usable_minimum() {
    let cfg = RuntimeConfig::from_json(
        br#"{"max_subscribers":0,"max_open_txns":0,"max_snapshots":0,"max_log_entries":0}"#,
    )
    .unwrap();
    assert!(cfg.max_subscribers >= 1);
    assert!(cfg.max_open_txns >= 1);
    assert!(cfg.max_snapshots >= 1);
    assert!(cfg.max_log_entries >= 1);
    // 夹下限之后仍能真正开事务、提交（不是"能构造但一用就废"）。
    let mut rt = TritRuntime::new(cfg);
    let mut w = Writer::new();
    w.u32(0);
    let out = dispatch(&mut rt, op::TXN_BEGIN, w.into_vec());
    assert!(Reader::new(&out).u64().unwrap() > 0);
}

#[test]
fn negative_and_absurd_parallel_workers_do_not_brick_the_runtime() {
    for config in [
        r#"{"parallel":{"workers":-1}}"#,
        r#"{"parallel":{"workers":999999}}"#,
        r#"{"parallel":{"workers":0}}"#,
    ] {
        // 这些要么被解析成默认值、要么被夹到可用范围；无论哪种，
        // create 都不该让内核整体停摆——后续 STATS 必须仍可响应。
        let cfg = RuntimeConfig::from_json(config.as_bytes());
        if let Ok(cfg) = cfg {
            let mut rt = TritRuntime::new(cfg);
            let out = dispatch(&mut rt, op::STATS, vec![]);
            assert_eq!(out.len(), 80, "STATS 仍是 10 个 u64");
        }
    }
}
