//! C ABI 集成测试：全部通过 `tt_rt_*` 原生入口驱动，覆盖缓冲区协议、
//! 错误码、事务、快照/差分/回滚与空指针安全。

use trit_rt::ffi::*;
use trit_rt::wire::{Reader, Writer, op, query};

/// 带自动扩容的 submit；返回 `(状态码, 响应字节)`。
unsafe fn submit(handle: u64, opcode: u32, payload: &[u8]) -> (i32, Vec<u8>) {
    let mut cap = 64usize;
    loop {
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
            // 协议：所需长度已写进 out 前 8 字节。
            let mut required = [0u8; 8];
            required.copy_from_slice(&out[..8]);
            cap = u64::from_le_bytes(required) as usize + 16;
            continue;
        }
        match status {
            TT_OK | TT_ERR_CONFLICT => {
                return (
                    status,
                    trit_rt::wire::unframe_response(&out)
                        .unwrap_or_default()
                        .to_vec(),
                );
            }
            _ => return (status, Vec::new()),
        }
    }
}

unsafe fn step(handle: u64, tick: u64) -> (i32, Vec<u8>) {
    let cap = 256usize;
    let mut out = vec![0u8; cap];
    let status = unsafe { tt_rt_step(handle, tick, out.as_mut_ptr(), cap) };
    assert_eq!(status, TT_OK);
    (
        status,
        trit_rt::wire::unframe_response(&out).unwrap().to_vec(),
    )
}

unsafe fn snapshot(handle: u64) -> (i32, Vec<u8>) {
    let cap = 4096usize;
    let mut out = vec![0u8; cap];
    let status = unsafe { tt_rt_snapshot(handle, out.as_mut_ptr(), cap) };
    let payload = trit_rt::wire::unframe_response(&out)
        .unwrap_or_default()
        .to_vec();
    (status, payload)
}

unsafe fn diff(handle: u64, a: u64, b: u64) -> (i32, Vec<u8>) {
    let cap = 4096usize;
    let mut out = vec![0u8; cap];
    let status = unsafe { tt_rt_diff(handle, a, b, out.as_mut_ptr(), cap) };
    let payload = trit_rt::wire::unframe_response(&out)
        .unwrap_or_default()
        .to_vec();
    (status, payload)
}

fn u64_le(bytes: &[u8]) -> u64 {
    let mut buf = [0u8; 8];
    buf.copy_from_slice(&bytes[..8]);
    u64::from_le_bytes(buf)
}

#[test]
fn create_capabilities_and_destroy() {
    unsafe {
        let handle = tt_rt_create(std::ptr::null(), 0);
        assert_ne!(handle, 0, "空配置应走默认值并成功创建");
        assert_eq!(tt_rt_abi_version(), 1);
        assert_eq!(tt_rt_version(), 0x00_01_00_00);
        let caps = tt_rt_capabilities(handle);
        assert!(caps & (1 << 0) != 0, "ROUTER 能力位应置起");
        assert!(caps & (1 << 8) != 0, "AI 能力位应置起（默认 feature）");
        let (status, response) = submit(handle, op::CAPABILITIES, &[]);
        assert_eq!(status, TT_OK);
        let mut reader = Reader::new(&response);
        assert_eq!(reader.u32().unwrap(), tt_rt_abi_version());
        assert_eq!(tt_rt_destroy(handle), TT_OK);
        assert_eq!(tt_rt_destroy(handle), TT_ERR_BAD_HANDLE, "重复销毁应报错");
        assert_eq!(tt_rt_capabilities(handle), 0, "坏句柄能力为 0");
    }
}

#[test]
fn bad_handle_and_bad_op_return_status_codes() {
    unsafe {
        let payload = {
            let mut w = Writer::new();
            w.u32(0);
            w.into_vec()
        };
        assert_eq!(
            tt_rt_submit(
                999_999,
                op::STEP,
                payload.as_ptr(),
                payload.len(),
                std::ptr::null_mut(),
                0
            ),
            TT_ERR_BAD_HANDLE
        );
        let handle = tt_rt_create(std::ptr::null(), 0);
        assert_ne!(handle, 0);
        let empty: &[u8] = &[];
        assert_eq!(
            tt_rt_submit(handle, 0, empty.as_ptr(), 0, std::ptr::null_mut(), 0),
            TT_ERR_BAD_OP
        );
        let (status, _) = submit(handle, 0x7F, empty);
        assert_eq!(status, TT_ERR_ARG, "未知 opcode 回参数错误");
        // last_error 应能取出非空信息。
        let mut buf = vec![0u8; 256];
        let len = tt_rt_last_error(handle, buf.as_mut_ptr(), buf.len());
        assert!(len > 0, "错误信息应可读");
        buf.truncate(len as usize);
        assert!(!buf.is_empty());
        assert_eq!(tt_rt_destroy(handle), TT_OK);
    }
}

#[test]
fn buffer_protocol_reports_required_size() {
    unsafe {
        let handle = tt_rt_create(std::ptr::null(), 0);
        assert_ne!(handle, 0);
        // 故意给一个 4 字节的小缓冲区。
        let mut tiny = [0u8; 4];
        let status = tt_rt_submit(
            handle,
            op::STATS,
            std::ptr::null(),
            0,
            tiny.as_mut_ptr(),
            tiny.len(),
        );
        assert_eq!(status, TT_ERR_BUFFER);
        // 扩容后重试即成功。
        let (status, response) = submit(handle, op::STATS, &[]);
        assert_eq!(status, TT_OK);
        assert_eq!(response.len(), 80, "10 个 u64 计数器");
        assert_eq!(tt_rt_destroy(handle), TT_OK);
    }
}

#[test]
fn subscribe_publish_step_over_ffi() {
    unsafe {
        let handle = tt_rt_create(std::ptr::null(), 0);
        assert_ne!(handle, 0);
        // mod 1 订阅事件 100；mod 2 订阅事件 101 且依赖 100。
        let subscribe_a = {
            let mut w = Writer::new();
            w.u32(1).u32(100).i32(0).u32(0);
            w.into_vec()
        };
        let (status, response) = submit(handle, op::SUBSCRIBE, &subscribe_a);
        assert_eq!(status, TT_OK);
        assert_eq!(u64_le(&response), 1);

        let subscribe_b = {
            let mut w = Writer::new();
            w.u32(2).u32(101).i32(0).u32(1).u32(100);
            w.into_vec()
        };
        let (status, _) = submit(handle, op::SUBSCRIBE, &subscribe_b);
        assert_eq!(status, TT_OK);

        let publish = {
            let mut w = Writer::new();
            w.u32(100);
            w.trits(&[trit_core::value::Ternary::True; 3]);
            w.into_vec()
        };
        let (status, response) = submit(handle, op::PUBLISH, &publish);
        assert_eq!(status, TT_OK);
        let mut reader = Reader::new(&response);
        assert_eq!(reader.u32().unwrap(), 2, "100 与依赖它的 101 都应标脏");

        // 事件载荷可回查。
        let query_payload = {
            let mut w = Writer::new();
            w.u32(6).u32(100);
            w.into_vec()
        };
        let (status, response) = submit(handle, op::QUERY, &query_payload);
        assert_eq!(status, TT_OK);
        assert_eq!(Reader::new(&response).trits().unwrap().len(), 3);

        let (status, response) = step(handle, 7);
        assert_eq!(status, TT_OK);
        let mut reader = Reader::new(&response);
        let count = reader.u32().unwrap();
        assert_eq!(count, 2);
        let mut order = Vec::new();
        for _ in 0..count {
            let sub_id = reader.u64().unwrap();
            let event_id = reader.u32().unwrap();
            let mod_id = reader.u32().unwrap();
            order.push((sub_id, event_id, mod_id));
        }
        assert_eq!(order[0], (1, 100, 1), "无依赖者先派发");
        assert_eq!(order[1], (2, 101, 2), "依赖者后派发");

        // 第二次 step：脏集合已清空，无派发。
        let (_, response) = step(handle, 8);
        assert_eq!(Reader::new(&response).u32().unwrap(), 0);
        assert_eq!(tt_rt_destroy(handle), TT_OK);
    }
}

#[test]
fn transaction_commit_rollback_and_conflict_diagnostics() {
    unsafe {
        let handle = tt_rt_create(
            br#"{"arbitration":"strict"}"#.as_ptr(),
            br#"{"arbitration":"strict"}"#.len(),
        );
        assert_ne!(handle, 0);
        let begin = {
            let mut w = Writer::new();
            w.u32(0);
            w.into_vec()
        };
        let (status, response) = submit(handle, op::TXN_BEGIN, &begin);
        assert_eq!(status, TT_OK);
        let txn_a = u64_le(&response);
        let (status, response) = submit(handle, op::TXN_BEGIN, &begin);
        assert_eq!(status, TT_OK);
        let txn_b = u64_le(&response);

        for (txn, delta) in [(txn_a, 0u8), (txn_b, 2u8)] {
            let mut w = Writer::new();
            w.u64(txn).u32(7).u8(delta);
            let (status, _) = submit(handle, op::TXN_SUBMIT, &w.into_vec());
            assert_eq!(status, TT_OK);
        }
        let commit = {
            let mut w = Writer::new();
            w.u64(txn_a);
            w.into_vec()
        };
        let (status, diagnostics) = submit(handle, op::TXN_COMMIT, &commit);
        assert_eq!(status, TT_ERR_CONFLICT, "严格模式下跨事务反向写入即冲突");
        // 诊断载荷：争议数 + 每个车道的全部肇事方。
        let mut reader = Reader::new(&diagnostics);
        assert_eq!(reader.u32().unwrap(), 1);
        assert_eq!(reader.u32().unwrap(), 7, "争议车道键");
        assert_eq!(reader.u32().unwrap(), 2, "两个肇事方");
        assert_eq!(reader.u64().unwrap(), txn_a);
        assert_eq!(reader.u8().unwrap(), 0, "txn_a 的增量是 -1");
        assert_eq!(reader.u64().unwrap(), txn_b);
        assert_eq!(reader.u8().unwrap(), 2, "txn_b 的增量是 +1");

        // 冲突未落地：世界为空。
        let query_world = {
            let mut w = Writer::new();
            w.u32(query::WORLD_SIZE);
            w.into_vec()
        };
        let (status, response) = submit(handle, op::QUERY, &query_world);
        assert_eq!(status, TT_OK);
        assert_eq!(Reader::new(&response).u32().unwrap(), 0);

        // 回滚 txn_b 后 txn_a 可以单独提交。
        let rollback = {
            let mut w = Writer::new();
            w.u64(txn_b);
            w.into_vec()
        };
        let (status, _) = submit(handle, op::TXN_ROLLBACK, &rollback);
        assert_eq!(status, TT_OK);
        let (status, response) = submit(handle, op::TXN_COMMIT, &commit);
        assert_eq!(status, TT_OK);
        let mut reader = Reader::new(&response);
        assert_eq!(reader.u32().unwrap(), 1, "落地一条");
        assert_eq!(tt_rt_destroy(handle), TT_OK);
    }
}

#[test]
fn snapshot_diff_rollback_over_ffi() {
    unsafe {
        let handle = tt_rt_create(std::ptr::null(), 0);
        assert_ne!(handle, 0);
        let autocommit = |key: u32, delta: u8| {
            let mut w = Writer::new();
            w.u64(0).u32(key).u8(delta);
            w.into_vec()
        };
        let (status, _) = submit(handle, op::TXN_SUBMIT, &autocommit(1, 2));
        assert_eq!(status, TT_OK);
        let (status, _) = submit(handle, op::TXN_SUBMIT, &autocommit(2, 2));
        assert_eq!(status, TT_OK);

        let snap_flags = {
            let mut w = Writer::new();
            w.u32(0);
            w.into_vec()
        };
        let (status, response) = submit(handle, op::SNAPSHOT, &snap_flags);
        assert_eq!(status, TT_OK);
        let snap_a = u64_le(&response);

        let (status, _) = submit(handle, op::TXN_SUBMIT, &autocommit(1, 0));
        assert_eq!(status, TT_OK);
        let (status, response) = submit(handle, op::SNAPSHOT, &snap_flags);
        assert_eq!(status, TT_OK);
        let snap_b = u64_le(&response);

        // tt_rt_snapshot 返回的字节应能解码（前 8 字节是快照 ID）。
        let (status, bytes) = snapshot(handle);
        assert_eq!(status, TT_OK);
        let (_, _, world) = trit_rt::snapshot::decode_world(&bytes[8..]).unwrap();
        assert_eq!(world.len(), 2);

        // diff：key 1 从 True 变 Unknown。
        let (status, response) = diff(handle, snap_a, snap_b);
        assert_eq!(status, TT_OK);
        let entries = trit_rt::snapshot::decode_diff(&response).unwrap();
        assert_eq!(entries.len(), 1);
        assert_eq!(entries[0].key, 1);

        // 回滚到 snap_a 后世界恢复。
        let rollback = {
            let mut w = Writer::new();
            w.u64(snap_a);
            w.into_vec()
        };
        let (status, _) = submit(handle, op::ROLLBACK_TO, &rollback);
        assert_eq!(status, TT_OK);
        let query_world = {
            let mut w = Writer::new();
            w.u32(query::WORLD_SIZE);
            w.into_vec()
        };
        let (status, response) = submit(handle, op::QUERY, &query_world);
        assert_eq!(status, TT_OK);
        assert_eq!(Reader::new(&response).u32().unwrap(), 2);
        assert_eq!(tt_rt_destroy(handle), TT_OK);
    }
}

#[test]
fn null_arguments_never_crash() {
    unsafe {
        let handle = tt_rt_create(std::ptr::null(), 0);
        assert_ne!(handle, 0);
        // STEP 需要 tick 载荷；空载荷回解码错误而不是崩溃。
        let tick_payload = {
            let mut w = Writer::new();
            w.u64(1);
            w.into_vec()
        };
        let (status, _) = submit(handle, op::STEP, &tick_payload);
        assert_eq!(status, TT_OK);
        let (status, _) = submit(handle, op::STEP, &[]);
        assert_eq!(status, TT_ERR_DECODE, "空载荷回解码错误");
        // out 为空指针 + cap 0：回缓冲区错误，不崩。
        assert_eq!(
            tt_rt_submit(
                handle,
                op::STATS,
                std::ptr::null(),
                0,
                std::ptr::null_mut(),
                0
            ),
            TT_ERR_BUFFER
        );
        // last_error 缓冲区过小：回写所需长度。
        let mut tiny = [0u8; 4];
        let status = tt_rt_last_error(handle, tiny.as_mut_ptr(), tiny.len());
        assert_eq!(status, TT_ERR_BUFFER);
        assert_eq!(tt_rt_destroy(handle), TT_OK);
    }
}
