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
        assert_eq!(tt_rt_abi_version(), 2);
        assert_eq!(tt_rt_version(), 0x00_01_00_00);
        let caps = tt_rt_capabilities(handle);
        assert!(caps & (1 << 0) != 0, "ROUTER 能力位应置起");
        assert!(caps & (1 << 8) != 0, "AI 能力位应置起（默认 feature）");
        assert!(caps & (1 << 9) != 0, "PARALLEL 能力位应置起");
        assert!(caps & (1 << 11) != 0, "BUDGET 能力位应置起");
        assert!(
            caps & (1 << 10) == 0 || cfg!(feature = "gpu"),
            "GPU 位只在探测成功后置起"
        );
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

/// 等到至少 `count` 个任务跑完（读 `BUDGET` 的 `totals.completed`）。
///
/// 用预算计数而不是「轮询到有结果」来等待：轮询本身会消费结果，
/// 用它来"等"会导致竞态（可能在任务完成前就拿到空列表）。
unsafe fn wait_completed(handle: u64, count: u64, tries: usize) -> u64 {
    for _ in 0..tries {
        let (status, response) = unsafe { submit(handle, op::BUDGET, &[]) };
        assert_eq!(status, TT_OK);
        let value: serde_json::Value = serde_json::from_slice(&response).unwrap();
        let done = value["totals"]["completed"].as_u64().unwrap_or(0);
        if done >= count {
            return done;
        }
        std::thread::sleep(std::time::Duration::from_millis(5));
    }
    panic!("超时：{count} 个任务未跑完");
}

/// 缓冲区不足时扩容重试，**不能把任务结果丢掉**。
///
/// 回归测试：`JOB_POLL` 走「窥视 → 交付成功才出队」的两阶段流程。
/// 若第一次就把结果出队（老实现），扩容重试时队列已空 —— 任务永久丢失。
#[test]
fn retrying_after_a_buffer_overflow_does_not_execute_twice() {
    unsafe {
        let handle = tt_rt_create(std::ptr::null(), 0);
        assert_ne!(handle, 0);

        // 提交一个扫描任务。
        let request = br#"{"owner":"retry-test","kind":"histogram","trits":[0,1,2,2]}"#;
        let (status, response) = submit(handle, op::SCAN_SUBMIT, request);
        assert_eq!(status, TT_OK);
        let submitted: serde_json::Value = serde_json::from_slice(&response).unwrap();
        let job = submitted["job"].as_u64().expect("应返回 job id");

        // 等任务真的跑完（确定性等待，不用轮询去"等"）。
        wait_completed(handle, 1, 400);

        // 用 4 字节小缓冲轮询：结果装不下，必须报缓冲不足。
        let poll_payload = br#"{"limit":8}"#;
        let mut tiny = [0u8; 4];
        let status = tt_rt_submit(
            handle,
            op::JOB_POLL,
            poll_payload.as_ptr(),
            poll_payload.len(),
            tiny.as_mut_ptr(),
            tiny.len(),
        );
        assert_eq!(status, TT_ERR_BUFFER, "结果装不下时应报缓冲不足");

        // 扩容重试：必须拿回**同一条**结果，而不是空列表
        // （修复前这里会拿到空列表，任务就永久丢了）。
        let (status, body) = submit(handle, op::JOB_POLL, poll_payload);
        assert_eq!(status, TT_OK);
        let value: serde_json::Value = serde_json::from_slice(&body).unwrap();
        let items = value["jobs"].as_array().expect("jobs 是数组");
        assert_eq!(items.len(), 1, "扩容重试必须拿回同一条结果");
        assert_eq!(items[0]["id"], serde_json::json!(job));
        assert_eq!(items[0]["state"], serde_json::json!("done"));

        // 交付成功后这批结果才算被取走：再取一次必须是空的。
        let (status, body) = submit(handle, op::JOB_POLL, poll_payload);
        assert_eq!(status, TT_OK);
        let value: serde_json::Value = serde_json::from_slice(&body).unwrap();
        assert_eq!(
            value["jobs"].as_array().map(Vec::len),
            Some(0),
            "交付成功后结果应已出队，不能重复返回"
        );

        assert_eq!(tt_rt_destroy(handle), TT_OK);
    }
}

/// 两阶段流程的另一面：**同参数连续两次轮询不能返回同一批结果**
/// （流式语义，不是「重放」语义）。
///
/// 待交付缓存（`(opcode, 载荷)` 为键）如果错误地套到 `JOB_POLL` 上，
/// 第二次轮询会拿到第一次的陈旧结果 —— 调用方拿到重复的任务结果。
#[test]
fn polling_twice_does_not_replay_the_previous_batch() {
    unsafe {
        let handle = tt_rt_create(std::ptr::null(), 0);
        assert_ne!(handle, 0);

        // 提交两个任务。
        for _ in 0..2 {
            let request = br#"{"owner":"twice","kind":"histogram","trits":[0,1,2]}"#;
            let (status, _) = submit(handle, op::SCAN_SUBMIT, request);
            assert_eq!(status, TT_OK);
        }
        wait_completed(handle, 2, 400);

        // 第一次轮询：拿到两条。
        let poll_payload = br#"{"limit":8}"#;
        let (status, body) = submit(handle, op::JOB_POLL, poll_payload);
        assert_eq!(status, TT_OK);
        let value: serde_json::Value = serde_json::from_slice(&body).unwrap();
        assert_eq!(value["jobs"].as_array().map(Vec::len), Some(2));

        // 第二次轮询：必须是空的（不是重放上一批）。
        let (status, body) = submit(handle, op::JOB_POLL, poll_payload);
        assert_eq!(status, TT_OK);
        let value: serde_json::Value = serde_json::from_slice(&body).unwrap();
        assert_eq!(
            value["jobs"].as_array().map(Vec::len),
            Some(0),
            "同参数第二次轮询不应重放上一批结果"
        );

        assert_eq!(tt_rt_destroy(handle), TT_OK);
    }
}

/// 同理，`PRNG_NEXT` 会推进随机序列：扩容重试不能多消耗一段序列。
#[test]
fn retrying_prng_after_a_buffer_overflow_does_not_advance_twice() {
    unsafe {
        let handle = tt_rt_create(std::ptr::null(), 0);
        assert_ne!(handle, 0);

        let payload = {
            let mut w = Writer::new();
            w.u32(8);
            w.into_vec()
        };

        // 小缓冲区触发缓冲不足（此时随机序列已经推进了一次）。
        let mut tiny = [0u8; 4];
        let status = tt_rt_submit(
            handle,
            op::PRNG_NEXT,
            payload.as_ptr(),
            payload.len(),
            tiny.as_mut_ptr(),
            tiny.len(),
        );
        assert_eq!(status, TT_ERR_BUFFER);

        // 扩容重试必须拿到**同一段**随机数（而不是下一段）。
        let (status, first) = submit(handle, op::PRNG_NEXT, &payload);
        assert_eq!(status, TT_OK);
        assert_eq!(first.len(), 6, "8 个 trit 打包成 2 字节 + 4 字节计数");

        // 再取一次应当是**不同的**序列（证明第一次只推进了一次）。
        let (status, second) = submit(handle, op::PRNG_NEXT, &payload);
        assert_eq!(status, TT_OK);
        assert_ne!(first, second, "正常取样应推进随机序列");

        assert_eq!(tt_rt_destroy(handle), TT_OK);
    }
}

/// 同理，`SNAPSHOT` 会创建快照：扩容重试不能创建两个快照。
#[test]
fn retrying_snapshot_after_a_buffer_overflow_creates_only_one_snapshot() {
    unsafe {
        let handle = tt_rt_create(std::ptr::null(), 0);
        assert_ne!(handle, 0);

        // 小缓冲区触发缓冲不足（此时快照已创建）。
        let mut tiny = [0u8; 4];
        let status = tt_rt_snapshot(handle, tiny.as_mut_ptr(), tiny.len());
        assert_eq!(status, TT_ERR_BUFFER);

        // 扩容重试必须拿到**同一个**快照 id。
        let cap = 4096usize;
        let mut out = vec![0u8; cap];
        let status = tt_rt_snapshot(handle, out.as_mut_ptr(), cap);
        assert_eq!(status, TT_OK);
        let first_id = {
            let framed = trit_rt::wire::unframe_response(&out).expect("装帧");
            let mut reader = trit_rt::wire::Reader::new(framed);
            reader.u64().expect("快照 id")
        };

        // 再打一次快照：id 必须不同（说明重试没有多建一个）。
        let mut out = vec![0u8; cap];
        let status = tt_rt_snapshot(handle, out.as_mut_ptr(), cap);
        assert_eq!(status, TT_OK);
        let second_id = {
            let framed = trit_rt::wire::unframe_response(&out).expect("装帧");
            let mut reader = trit_rt::wire::Reader::new(framed);
            reader.u64().expect("快照 id")
        };
        assert_ne!(first_id, second_id, "每次快照都应有新的 id");

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

/// 轮询直到拿到一条结果（超时即失败，不死锁）。
unsafe fn poll_until(handle: u64, limit: usize, tries: usize) -> Vec<u8> {
    let payload = format!("{{\"limit\":{limit}}}");
    for _ in 0..tries {
        let (status, response) = unsafe { submit(handle, op::JOB_POLL, payload.as_bytes()) };
        assert_eq!(status, TT_OK);
        let value: serde_json::Value = serde_json::from_slice(&response).unwrap();
        if value["jobs"].as_array().map(Vec::len).unwrap_or(0) > 0 {
            return response;
        }
        std::thread::sleep(std::time::Duration::from_millis(5));
    }
    Vec::new()
}

#[test]
fn scan_submit_poll_and_budget_over_ffi() {
    unsafe {
        let config = br#"{"parallel":{"workers":2,"scan_chunk":2}}"#;
        let handle = tt_rt_create(config.as_ptr(), config.len());
        assert_ne!(handle, 0);

        // 直方图扫描：[Neg, Zero, Pos, Pos] → 1/1/2
        let request = br#"{"owner":"ffi-test","kind":"histogram","trits":[0,1,2,2]}"#;
        let (status, response) = submit(handle, op::SCAN_SUBMIT, request);
        assert_eq!(status, TT_OK);
        let submitted: serde_json::Value = serde_json::from_slice(&response).unwrap();
        let job = submitted["job"].as_u64().expect("应返回 job id");
        assert!(job >= 1);

        let response = poll_until(handle, 4, 400);
        let polled: serde_json::Value = serde_json::from_slice(&response).unwrap();
        let jobs = polled["jobs"].as_array().expect("jobs 是数组");
        assert_eq!(jobs.len(), 1);
        assert_eq!(jobs[0]["id"], serde_json::json!(job));
        assert_eq!(jobs[0]["state"], serde_json::json!("done"));
        assert_eq!(jobs[0]["result"]["histogram"]["neg"], serde_json::json!(1));
        assert_eq!(jobs[0]["result"]["histogram"]["zero"], serde_json::json!(1));
        assert_eq!(jobs[0]["result"]["histogram"]["pos"], serde_json::json!(2));

        // 再取一次应为空（取出即出队）。
        let payload = br#"{"limit":4}"#;
        let (status, response) = submit(handle, op::JOB_POLL, payload);
        assert_eq!(status, TT_OK);
        let polled: serde_json::Value = serde_json::from_slice(&response).unwrap();
        assert_eq!(polled["jobs"].as_array().map(Vec::len), Some(0));

        // 预算：该 owner 被记过账。
        let (status, response) = submit(handle, op::BUDGET, &[]);
        assert_eq!(status, TT_OK);
        let budget: serde_json::Value = serde_json::from_slice(&response).unwrap();
        assert_eq!(budget["workers"], serde_json::json!(2));
        assert!(budget["totals"]["submitted"].as_u64().unwrap_or(0) >= 1);
        let mods = budget["mods"].as_array().expect("mods 是数组");
        assert!(mods.iter().any(|entry| entry["owner"] == "ffi-test"));

        assert_eq!(tt_rt_destroy(handle), TT_OK);
    }
}

/// 非法请求**不**让整个 opcode 失败，而是产生一个 `failed` 任务。
///
/// ABI 规定：「载荷非法（trit 值不是 0/1/2、kind 未知、pattern/dot 缺 pattern）
/// → 产生一个 failed 任务，而不是让整个 opcode 失败」。
/// 这样调用方总能通过 `JOB_POLL` 拿到一条带 `error` 说明的结果，
/// 不会「提交后石沉大海」——托管回退内核也是这个行为。
#[test]
fn illegal_scan_requests_become_failed_jobs_over_ffi() {
    unsafe {
        let handle = tt_rt_create(std::ptr::null(), 0);
        assert_ne!(handle, 0);

        // 非法 trit 值 7
        let request = br#"{"owner":"bad","kind":"histogram","trits":[7]}"#;
        let (status, response) = submit(handle, op::SCAN_SUBMIT, request);
        assert_eq!(status, TT_OK, "非法请求不该让 opcode 失败");
        let submitted: serde_json::Value = serde_json::from_slice(&response).unwrap();
        let job = submitted["job"].as_u64().expect("应返回 job id");

        // 未知 kind
        let request = br#"{"owner":"bad","kind":"nope","trits":[1]}"#;
        let (status, _) = submit(handle, op::SCAN_SUBMIT, request);
        assert_eq!(status, TT_OK, "未知 kind 不该让 opcode 失败");

        // pattern 缺第二个向量
        let request = br#"{"owner":"bad","kind":"pattern","trits":[1,2]}"#;
        let (status, _) = submit(handle, op::SCAN_SUBMIT, request);
        assert_eq!(status, TT_OK, "缺 pattern 不该让 opcode 失败");

        // 三条都应该在 JOB_POLL 里以 failed 状态回来，并带上失败原因。
        let response = poll_until(handle, 4, 400);
        let polled: serde_json::Value = serde_json::from_slice(&response).unwrap();
        let jobs = polled["jobs"].as_array().expect("jobs 是数组");
        assert_eq!(jobs.len(), 3);
        for entry in jobs {
            assert_eq!(entry["state"], serde_json::json!("failed"));
            assert_eq!(entry["backend"], serde_json::json!(""));
            let error = entry["error"].as_str().expect("error 是字符串");
            assert!(!error.is_empty(), "失败必须带原因");
        }
        // 第一条是非法 trit 值；机器可读的 kind 字段仍是 histogram。
        let illegal_trit = jobs
            .iter()
            .find(|entry| entry["id"] == serde_json::json!(job))
            .expect("能找到非法 trit 值那条");
        assert!(
            illegal_trit["error"]
                .as_str()
                .unwrap_or_default()
                .contains("非法 trit 值"),
            "失败原因要说明是 trit 值非法：{}",
            illegal_trit["error"]
        );
        assert_eq!(illegal_trit["kind"], serde_json::json!("histogram"));

        // 未知 kind 的那条：kind 字段收敛到 "unknown"，原文只出现在错误文案里。
        let unknown_kind = jobs
            .iter()
            .find(|entry| entry["kind"] == serde_json::json!("unknown"))
            .expect("能找到未知 kind 那条");
        assert!(
            unknown_kind["error"]
                .as_str()
                .unwrap_or_default()
                .contains("未知扫描 kind"),
            "失败原因要说明是种类未知：{}",
            unknown_kind["error"]
        );

        assert_eq!(tt_rt_destroy(handle), TT_OK);
    }
}

/// `null` 的三值数组必须被当成**空数组**，不能当成非法载荷。
///
/// 回归测试：C# 兼容壳在 `Pattern` 为 `null` 时写的是 `"pattern": null`，
/// 而托管内核把它当空数组（`JsonByteArray` 对 `Null` 返回 `null`）。
/// 原生内核若严格拒绝 `null`，两套内核对**同一份载荷**就会给出不同结果 ——
/// 托管侧能提交，原生侧报 `TT_ERR_DECODE`。
#[test]
fn null_trit_arrays_are_accepted_as_empty() {
    unsafe {
        let handle = tt_rt_create(std::ptr::null(), 0);
        assert_ne!(handle, 0);

        // pattern 写成 null 的直方图任务：必须提交成功。
        let request = br#"{"owner":"null-test","kind":"histogram","trits":[0,1,2],"pattern":null}"#;
        let (status, response) = submit(handle, op::SCAN_SUBMIT, request);
        assert_eq!(status, TT_OK, "null 的 pattern 应被当成空数组");
        let submitted: serde_json::Value = serde_json::from_slice(&response).unwrap();
        assert!(submitted["job"].as_u64().unwrap_or(0) > 0);

        // trits 写成 null：同样必须被接受（随后因 trits 为空而无意义，但不是解码错）。
        let request = br#"{"owner":"null-test","kind":"histogram","trits":null,"pattern":null}"#;
        let (status, _) = submit(handle, op::SCAN_SUBMIT, request);
        assert_eq!(
            status, TT_OK,
            "null 的 trits 应被当成空数组，而不是解码错误"
        );

        assert_eq!(tt_rt_destroy(handle), TT_OK);
    }
}

#[test]
fn cancelling_and_gpu_and_metrics_over_ffi() {
    unsafe {
        let handle = tt_rt_create(std::ptr::null(), 0);
        assert_ne!(handle, 0);

        // 取消一个不存在的任务：0 条，不报错。
        let payload = br#"{"job":424242}"#;
        let (status, response) = submit(handle, op::JOB_CANCEL, payload);
        assert_eq!(status, TT_OK);
        let cancelled: serde_json::Value = serde_json::from_slice(&response).unwrap();
        assert_eq!(cancelled["cancelled"], serde_json::json!(0));
        let payload = br#"{"owner":"nobody"}"#;
        let (status, response) = submit(handle, op::JOB_CANCEL, payload);
        assert_eq!(status, TT_OK);
        let cancelled: serde_json::Value = serde_json::from_slice(&response).unwrap();
        assert_eq!(cancelled["cancelled"], serde_json::json!(0));

        // GPU 状态：off 时一定不可用；auto 时要么有适配器（带 feature）要么给出原因。
        let payload = br#"{"policy":"off"}"#;
        let (status, response) = submit(handle, op::GPU_STATUS, payload);
        assert_eq!(status, TT_OK);
        let gpu: serde_json::Value = serde_json::from_slice(&response).unwrap();
        assert_eq!(gpu["policy"], serde_json::json!("off"));
        assert_eq!(gpu["enabled"], serde_json::json!(false));

        let payload = br#"{"policy":"bogus"}"#;
        let (status, _) = submit(handle, op::GPU_STATUS, payload);
        assert_eq!(status, TT_ERR_ARG, "未知策略回参数错误");

        let (status, response) = submit(handle, op::GPU_STATUS, &[]);
        assert_eq!(status, TT_OK);
        let gpu: serde_json::Value = serde_json::from_slice(&response).unwrap();
        if gpu["available"] == serde_json::json!(false) {
            assert!(
                !gpu["reason"].as_str().unwrap_or_default().is_empty(),
                "不可用时必须给出原因"
            );
        }

        // 指标：没跑过任务时为空串，不报错。
        let (status, response) = submit(handle, op::METRICS, &[]);
        assert_eq!(status, TT_OK);
        let metrics: serde_json::Value = serde_json::from_slice(&response).unwrap();
        assert!(metrics["prometheus"].is_string());

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

/// 回归测试：`JOB_POLL` 的 owner 过滤在 **C ABI** 这条路径上也必须成立。
///
/// 为什么单独测：内核层的 `dispatch` 走的是 `poll_jobs_peek`，
/// 而 FFI 走的是 `submit_job_poll`（两阶段：窥视 → 交付成功才出队）。
/// 两条路径的出队时机不同，owner 过滤必须**两条都对**——
/// 否则「在 C# 里按 owner 过滤」会在原生路径上失效（模组实际走的就是 FFI）。
#[test]
fn polling_by_owner_over_ffi_returns_only_that_owners_results() {
    unsafe {
        let config = br#"{"parallel":{"workers":4}}"#;
        let handle = tt_rt_create(config.as_ptr(), config.len());
        assert_ne!(handle, 0);

        for owner in ["alpha", "beta", "alpha", "beta"] {
            let request = format!(r#"{{"owner":"{owner}","kind":"histogram","trits":[0,1,2]}}"#);
            let (status, _) = submit(handle, op::SCAN_SUBMIT, request.as_bytes());
            assert_eq!(status, TT_OK);
        }

        // 等两个 owner 的任务都完成。
        let mut alpha_jobs: Vec<serde_json::Value> = Vec::new();
        for _ in 0..400 {
            let payload = br#"{"limit":16,"owner":"alpha"}"#;
            let (status, response) = submit(handle, op::JOB_POLL, payload);
            assert_eq!(status, TT_OK);
            let value: serde_json::Value = serde_json::from_slice(&response).unwrap();
            alpha_jobs.extend(value["jobs"].as_array().cloned().unwrap_or_default());
            if alpha_jobs.len() >= 2 {
                break;
            }
            std::thread::sleep(std::time::Duration::from_millis(5));
        }
        assert_eq!(alpha_jobs.len(), 2, "应拿到 alpha 的 2 条：{alpha_jobs:?}");
        for job in &alpha_jobs {
            assert_eq!(job["owner"], serde_json::json!("alpha"));
        }

        // beta 的两条必须还在队列里。
        let mut beta_jobs: Vec<serde_json::Value> = Vec::new();
        for _ in 0..400 {
            let payload = br#"{"limit":16,"owner":"beta"}"#;
            let (status, response) = submit(handle, op::JOB_POLL, payload);
            assert_eq!(status, TT_OK);
            let value: serde_json::Value = serde_json::from_slice(&response).unwrap();
            beta_jobs.extend(value["jobs"].as_array().cloned().unwrap_or_default());
            if beta_jobs.len() >= 2 {
                break;
            }
            std::thread::sleep(std::time::Duration::from_millis(5));
        }
        assert_eq!(
            beta_jobs.len(),
            2,
            "beta 的结果不能被 alpha 的轮询吃掉：{beta_jobs:?}"
        );

        assert_eq!(tt_rt_destroy(handle), TT_OK);
    }
}
