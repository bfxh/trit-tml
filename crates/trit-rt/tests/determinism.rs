//! 确定性对拍：两个同种子运行时执行同一操作序列，每一步的响应必须逐字节一致；
//! 快照 → 修改 → 回滚 → 重放，必须精确回到修改后的状态。

use trit_rt::ffi::*;
use trit_rt::wire::{Reader, Writer, op, query};

/// 执行一段固定脚本，收集每步响应。
fn run_script(seed_json: &[u8]) -> Vec<Vec<u8>> {
    let mut responses = Vec::new();
    unsafe {
        let handle = tt_rt_create(seed_json.as_ptr(), seed_json.len());
        assert_ne!(handle, 0);
        let ops: Vec<(u32, Vec<u8>)> = vec![
            (op::SUBSCRIBE, {
                let mut w = Writer::new();
                w.u32(1).u32(100).i32(3).u32(0);
                w.into_vec()
            }),
            (op::SUBSCRIBE, {
                let mut w = Writer::new();
                w.u32(2).u32(101).i32(1).u32(1).u32(100);
                w.into_vec()
            }),
            (op::PUBLISH, {
                let mut w = Writer::new();
                w.u32(100);
                w.trits(&[
                    trit_core::value::Ternary::True,
                    trit_core::value::Ternary::False,
                    trit_core::value::Ternary::Unknown,
                ]);
                w.into_vec()
            }),
            (op::STEP, {
                let mut w = Writer::new();
                w.u64(1);
                w.into_vec()
            }),
            (op::PRNG_NEXT, {
                let mut w = Writer::new();
                w.u32(8);
                w.into_vec()
            }),
            (op::PRNG_NEXT, {
                let mut w = Writer::new();
                w.u32(8);
                w.into_vec()
            }),
            (op::TXN_BEGIN, {
                let mut w = Writer::new();
                w.u32(0);
                w.into_vec()
            }),
            (op::SNAPSHOT, {
                let mut w = Writer::new();
                w.u32(0);
                w.into_vec()
            }),
        ];
        for (opcode, payload) in ops {
            let mut cap = 256usize;
            loop {
                let mut out = vec![0u8; cap];
                let status = tt_rt_submit(
                    handle,
                    opcode,
                    payload.as_ptr(),
                    payload.len(),
                    out.as_mut_ptr(),
                    cap,
                );
                if status == TT_ERR_BUFFER && cap >= 8 {
                    let mut required = [0u8; 8];
                    required.copy_from_slice(&out[..8]);
                    cap = u64::from_le_bytes(required) as usize + 16;
                    continue;
                }
                assert_eq!(status, TT_OK, "opcode {opcode:#x} 应成功");
                responses.push(trit_rt::wire::unframe_response(&out).unwrap().to_vec());
                break;
            }
        }
        assert_eq!(tt_rt_destroy(handle), TT_OK);
    }
    responses
}

#[test]
fn same_seed_same_script_identical_responses() {
    let config = br#"{"seed":12345}"#;
    let left = run_script(config);
    let right = run_script(config);
    assert_eq!(left, right, "同种子同脚本必须逐字节一致");
    // PRNG 两步各 8 个 trit，且两步不同（流在推进）。
    let first = Reader::new(&left[4]).trits().unwrap();
    let second = Reader::new(&left[5]).trits().unwrap();
    assert_eq!(first.len(), 8);
    assert_ne!(first, second);
}

#[test]
fn different_seed_diverges() {
    let left = run_script(br#"{"seed":1}"#);
    let right = run_script(br#"{"seed":2}"#);
    assert_ne!(left[4], right[4], "不同种子的 PRNG 流必须不同");
}

#[test]
fn rollback_restores_snapshot_state() {
    unsafe {
        let handle = tt_rt_create(std::ptr::null(), 0);
        assert_ne!(handle, 0);
        let autocommit = |key: u32, delta: u8| {
            let mut w = Writer::new();
            w.u64(0).u32(key).u8(delta);
            w.into_vec()
        };
        let submit = |opcode: u32, payload: &[u8]| {
            let mut cap = 256usize;
            loop {
                let mut out = vec![0u8; cap];
                let status = tt_rt_submit(
                    handle,
                    opcode,
                    payload.as_ptr(),
                    payload.len(),
                    out.as_mut_ptr(),
                    cap,
                );
                if status == TT_ERR_BUFFER && cap >= 8 {
                    let mut required = [0u8; 8];
                    required.copy_from_slice(&out[..8]);
                    cap = u64::from_le_bytes(required) as usize + 16;
                    continue;
                }
                assert_eq!(status, TT_OK);
                return trit_rt::wire::unframe_response(&out).unwrap().to_vec();
            }
        };
        submit(op::TXN_SUBMIT, &autocommit(10, 2));
        submit(op::TXN_SUBMIT, &autocommit(20, 2));
        let snap = {
            let payload = {
                let mut w = Writer::new();
                w.u32(0);
                w.into_vec()
            };
            let response = submit(op::SNAPSHOT, &payload);
            Reader::new(&response).u64().unwrap()
        };
        // 快照后的世界字节。
        let before = world_bytes(handle);
        // 继续改。
        submit(op::TXN_SUBMIT, &autocommit(10, 0));
        submit(op::TXN_SUBMIT, &autocommit(30, 2));
        let after = world_bytes(handle);
        assert_ne!(before, after);
        // 回滚到快照。
        let rollback = {
            let mut w = Writer::new();
            w.u64(snap);
            w.into_vec()
        };
        submit(op::ROLLBACK_TO, &rollback);
        assert_eq!(world_bytes(handle), before, "回滚后必须与快照一致");
        assert_eq!(tt_rt_destroy(handle), TT_OK);
    }
}

/// 回滚后从快照序列号重放日志，必须精确回到修改后的状态（安全 API 直连）。
#[test]
fn replay_after_rollback_matches_forward_state() {
    use trit_rt::runtime::{RuntimeConfig, TritRuntime};
    use trit_rt::wire::op;

    let mut runtime = TritRuntime::new(RuntimeConfig::default());
    let autocommit = |runtime: &mut TritRuntime, key: u32, delta: u8| {
        let mut w = Writer::new();
        w.u64(0).u32(key).u8(delta);
        runtime.dispatch(op::TXN_SUBMIT, &w.into_vec()).unwrap();
    };
    autocommit(&mut runtime, 10, 2);
    autocommit(&mut runtime, 20, 2);
    let snap = {
        let mut w = Writer::new();
        w.u32(0);
        let response = runtime.dispatch(op::SNAPSHOT, &w.into_vec()).unwrap();
        Reader::new(&response).u64().unwrap()
    };
    let snap_seq = runtime.snapshot_log_seq(snap).expect("快照应存在");
    assert_eq!(snap_seq, 2, "快照锚在两条日志之后");
    let forward_world = runtime.world().clone();
    autocommit(&mut runtime, 10, 0);
    autocommit(&mut runtime, 30, 2);
    assert_ne!(&runtime.world().clone(), &forward_world);

    // 回滚到快照，再从 snap_seq 重放。
    let rollback = {
        let mut w = Writer::new();
        w.u64(snap);
        w.into_vec()
    };
    runtime.dispatch(op::ROLLBACK_TO, &rollback).unwrap();
    assert_eq!(runtime.world(), &forward_world);
    let replayed = runtime.replay_from(snap_seq);
    assert_eq!(replayed, 2, "重放快照之后的两条日志");
    assert_eq!(runtime.world().len(), 3);
}

/// 通过 FFI 导出当前世界投影（解码成车道映射做语义比较；
/// 快照字节头的 log_seq 反映当前日志位置，回滚不截断日志，故不能逐字节比）。
unsafe fn world_bytes(handle: u64) -> std::collections::BTreeMap<u32, trit_core::value::Ternary> {
    let cap = 4096usize;
    let mut out = vec![0u8; cap];
    let status = unsafe { tt_rt_snapshot(handle, out.as_mut_ptr(), cap) };
    assert_eq!(status, TT_OK);
    // 拆框后前 8 字节是快照 ID，其余是世界投影编码。
    let payload = trit_rt::wire::unframe_response(&out).unwrap();
    let (_, _, world) = trit_rt::snapshot::decode_world(&payload[8..]).unwrap();
    world
}

#[test]
fn shadow_records_divergences_without_touching_state() {
    unsafe {
        let handle = tt_rt_create(std::ptr::null(), 0);
        assert_ne!(handle, 0);
        let world_before = world_bytes(handle);
        let submit = |opcode: u32, payload: &[u8]| {
            let mut cap = 256usize;
            loop {
                let mut out = vec![0u8; cap];
                let status = tt_rt_submit(
                    handle,
                    opcode,
                    payload.as_ptr(),
                    payload.len(),
                    out.as_mut_ptr(),
                    cap,
                );
                if status == TT_ERR_BUFFER && cap >= 8 {
                    let mut required = [0u8; 8];
                    required.copy_from_slice(&out[..8]);
                    cap = u64::from_le_bytes(required) as usize + 16;
                    continue;
                }
                assert_eq!(status, TT_OK);
                return trit_rt::wire::unframe_response(&out).unwrap().to_vec();
            }
        };
        // 一致：空签名。
        let empty = {
            let mut w = Writer::new();
            w.trits(&[]);
            w.into_vec()
        };
        let response = submit(op::SHADOW_RECORD, &empty);
        assert_eq!(Reader::new(&response).u64().unwrap(), 0);
        // 不一致：非空签名。
        let signature = {
            let mut w = Writer::new();
            w.trits(&[trit_core::value::Ternary::True]);
            w.into_vec()
        };
        let response = submit(op::SHADOW_RECORD, &signature);
        assert_eq!(Reader::new(&response).u64().unwrap(), 1);
        let query_shadow = {
            let mut w = Writer::new();
            w.u32(query::SHADOW);
            w.into_vec()
        };
        let response = submit(op::QUERY, &query_shadow);
        let mut reader = Reader::new(&response);
        assert_eq!(reader.u64().unwrap(), 2, "compared");
        assert_eq!(reader.u64().unwrap(), 1, "divergences");
        // 影子记录不得改变世界状态。
        assert_eq!(world_bytes(handle), world_before);
        assert_eq!(tt_rt_destroy(handle), TT_OK);
    }
}
