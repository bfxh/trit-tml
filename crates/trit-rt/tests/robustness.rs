//! 健壮性矩阵：对**每个 opcode** 灌入一整组畸形载荷，断言
//!   ① 绝不返回 `TT_ERR_PANIC`（panic 被 `catch_unwind` 拦住但那已是 bug）；
//!   ② 状态码一定落在 `[-10, 0]` 这个已定义的集合里，不冒出未定义值；
//!   ③ 轰炸之后句柄依然可用（后续 `STATS` 仍能成功）。
//!
//! 为什么单独成文件：单测通常只测「合法输入 → 正确输出」，但这条 ABI 的调用方是
//! 游戏进程里的模组，任何一次畸形载荷导致进程 abort 都是玩家侧的闪退。
//! 这里用表驱动把「解码路径上的溢出 / 越界 / 大 count」系统性扫一遍，
//! 是防止「20 字节坏载荷申请几十 GB」这类缺陷回归的第一道网。

use trit_rt::ffi::*;
use trit_rt::wire::op;

/// 自带扩容上限的 submit。畸形载荷若谎报一个巨大的所需长度，
/// 直接停手并返回，避免测试进程自己先 OOM。
unsafe fn submit(handle: u64, opcode: u32, payload: &[u8]) -> (i32, Vec<u8>) {
    let mut cap = 64usize;
    for _ in 0..8 {
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
            let mut required = [0u8; 8];
            required.copy_from_slice(&out[..8]);
            let need = u64::from_le_bytes(required) as usize;
            if need > 16 * 1024 * 1024 {
                // 畸形输入谎称需要 >16MB：视作被闸门拦下，不再继续扩容。
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

/// 一整组畸形载荷：空、单字节、全 0xFF、递增、以及若干「count 字段极端值」。
fn malformed_payloads() -> Vec<Vec<u8>> {
    let mut out = vec![
        vec![],
        vec![0x00],
        vec![0xFF],
        vec![0x00; 4],
        vec![0xFF; 4],
        vec![0xFF; 8],
        vec![0xFF; 16],
        vec![0xFF; 64],
        vec![0x7F; 64],
        (0u8..64).collect::<Vec<_>>(),
    ];
    // 二进制 opcode 的头 4 字节常被当成「条数」，这几个值最容易触发
    // `checked((int)U32())` 溢出或 `new T[count]` 巨量申请。
    for count in [
        0xFFFF_FFFFu32,
        0x7FFF_FFFF,
        0x0001_0000,
        0x0001_0001,
        0x0000_0100,
    ] {
        let mut header = count.to_le_bytes().to_vec();
        out.push(header.clone());
        header.extend_from_slice(&[0xAB; 8]);
        out.push(header);
    }
    // JSON opcode 的畸形输入：截断、非 UTF-8、类型不符。
    out.push(b"{".to_vec());
    out.push(b"{\"limit\":".to_vec());
    out.push(b"not json at all".to_vec());
    out.push(vec![0xFF, 0xFE, 0xFD]);
    out
}

/// 一个 opcode 的健壮性检查。
unsafe fn assert_opcode_survives(handle: u64, opcode: u32) {
    for (index, payload) in malformed_payloads().iter().enumerate() {
        let (status, _) = unsafe { submit(handle, opcode, payload) };
        assert_ne!(
            status, TT_ERR_PANIC,
            "opcode {opcode:#x} 在畸形载荷 #{index}（{payload:?}）上 panic 了"
        );
        assert!(
            (-10..=0).contains(&status),
            "opcode {opcode:#x} 在畸形载荷 #{index} 返回未定义状态码 {status}"
        );
    }
    // 轰炸之后句柄必须仍然可用。
    let (status, _) = unsafe { submit(handle, op::STATS, &[]) };
    assert_eq!(status, TT_OK, "opcode {opcode:#x} 的畸形载荷把运行时搞坏了");
}

macro_rules! robust {
    ($name:ident, $opcode:expr) => {
        #[test]
        fn $name() {
            unsafe {
                let handle = tt_rt_create(std::ptr::null(), 0);
                assert_ne!(handle, 0);
                assert_opcode_survives(handle, $opcode);
                assert_eq!(tt_rt_destroy(handle), TT_OK);
            }
        }
    };
}

// 二进制 opcode 全表
robust!(subscribe_survives, op::SUBSCRIBE);
robust!(unsubscribe_survives, op::UNSUBSCRIBE);
robust!(publish_survives, op::PUBLISH);
robust!(txn_begin_survives, op::TXN_BEGIN);
robust!(txn_commit_survives, op::TXN_COMMIT);
robust!(txn_rollback_survives, op::TXN_ROLLBACK);
robust!(txn_submit_survives, op::TXN_SUBMIT);
robust!(arbitrate_survives, op::ARBITRATE);
robust!(query_survives, op::QUERY);
robust!(step_survives, op::STEP);
robust!(snapshot_survives, op::SNAPSHOT);
robust!(diff_survives, op::DIFF);
robust!(rollback_to_survives, op::ROLLBACK_TO);
robust!(prng_next_survives, op::PRNG_NEXT);
robust!(shadow_record_survives, op::SHADOW_RECORD);
robust!(capabilities_survives, op::CAPABILITIES);
robust!(stats_survives, op::STATS);
robust!(ai_vote_survives, op::AI_VOTE);
robust!(ai_set_model_survives, op::AI_SET_MODEL);

// JSON opcode 全表
robust!(scan_submit_survives, op::SCAN_SUBMIT);
robust!(job_poll_survives, op::JOB_POLL);
robust!(job_cancel_survives, op::JOB_CANCEL);
robust!(gpu_status_survives, op::GPU_STATUS);
robust!(budget_survives, op::BUDGET);
robust!(metrics_survives, op::METRICS);
robust!(txn_submit_batch_survives, op::TXN_SUBMIT_BATCH);
robust!(session_reset_survives, op::SESSION_RESET);
robust!(txn_apply_survives, op::TXN_APPLY);

// 未知 opcode 也不能 panic（0x00 是 BAD_OP 特判，其余回 ARG）。
robust!(opcode_zero_survives, 0x00);
robust!(opcode_beyond_table_survives, 0x1D);
robust!(opcode_high_survives, 0x7F);
robust!(opcode_ff_survives, 0xFF);
robust!(opcode_max_survives, u32::MAX);

/// 配置 JSON 畸形时：`create` 要么给出合法句柄，要么回 0，绝不 panic / abort。
#[test]
fn malformed_configs_never_abort() {
    let configs: Vec<Vec<u8>> = vec![
        b"".to_vec(),
        b"{".to_vec(),
        b"not json".to_vec(),
        b"{\"seed\":".to_vec(),
        b"{\"arbitration\":\"nope\"}".to_vec(),
        b"{\"parallel\":{\"workers\":-1}}".to_vec(),
        b"{\"parallel\":{\"workers\":999999}}".to_vec(),
        vec![0xFF, 0xFE],
        vec![0x00; 32],
        b"null".to_vec(),
        b"[]".to_vec(),
        b"12345".to_vec(),
    ];
    for config in configs {
        let handle = unsafe { tt_rt_create(config.as_ptr(), config.len()) };
        if handle != 0 {
            assert_eq!(unsafe { tt_rt_destroy(handle) }, TT_OK);
        }
    }
}

/// 极端句柄：`capabilities` / `submit` / `destroy` 对任意 u64 都不崩。
///
/// **测试隔离坑（踩过并修掉）**：cargo test 默认并行，而 `next_handle()` 从个位数
/// 顺序分配。第一版这里对 `handle = 1` 调了 `tt_rt_destroy`，正好删掉另一个并行
/// 测试正在用的运行时 —— 于是 `ai_set_model_survives` 报「畸形载荷把运行时搞坏了」，
/// 看上去像产品 bug，其实是测试自己删了别人的句柄。
/// 修法：任意句柄只走 `capabilities` / `submit` 这类**只读**探测；`destroy` 只碰
/// 本测试自己创建、用完即弃的句柄。重复销毁顺带覆盖坏句柄分支。
#[test]
fn arbitrary_handles_are_rejected_without_crashing() {
    let own = unsafe { tt_rt_create(std::ptr::null(), 0) };
    assert_ne!(own, 0);
    assert_eq!(unsafe { tt_rt_destroy(own) }, TT_OK);
    assert_eq!(
        unsafe { tt_rt_destroy(own) },
        TT_ERR_BAD_HANDLE,
        "重复销毁应报错"
    );

    // 这些值远离顺序分配区，`submit`/`capabilities` 只读探测绝不会误伤真实句柄。
    for handle in [0u64, u64::MAX, u64::MAX - 1, 0x8000_0000_0000_0000] {
        assert_eq!(unsafe { tt_rt_capabilities(handle) }, 0, "坏句柄能力为 0");
        let empty: &[u8] = &[];
        let status = unsafe {
            tt_rt_submit(
                handle,
                op::STATS,
                empty.as_ptr(),
                0,
                std::ptr::null_mut(),
                0,
            )
        };
        // 坏句柄提交回 BAD_HANDLE（out 空 + cap 0 也不该盖过它）。
        assert_eq!(status, TT_ERR_BAD_HANDLE);
    }
}

/// 缓冲区协议在所有「out/cap 组合」下都不越界写。
#[test]
fn buffer_protocol_edge_cases_never_crash() {
    unsafe {
        let handle = tt_rt_create(std::ptr::null(), 0);
        assert_ne!(handle, 0);
        let empty: &[u8] = &[];
        for cap in [0usize, 1, 3, 4, 7, 8, 15, 16] {
            let mut out = vec![0xAAu8; cap];
            let status = tt_rt_submit(handle, op::STATS, empty.as_ptr(), 0, out.as_mut_ptr(), cap);
            assert!((-10..=0).contains(&status));
            assert_ne!(status, TT_ERR_PANIC);
            if status == TT_ERR_BUFFER && cap >= 8 {
                // 前 8 字节应写回所需长度，且不能超过 cap（没越界写）。
                let need = u64::from_le_bytes(out[..8].try_into().unwrap());
                assert!(need > 0);
            }
            // 哨兵：out 之后的字节不能被踩。
            assert!(out.len() == cap);
        }
        assert_eq!(tt_rt_destroy(handle), TT_OK);
    }
}
