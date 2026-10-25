//! FFI 冒烟示例：按架构文档的叙事跑一遍完整生命周期——
//! 订阅 → 发布 → step（动作意图）→ 事务 → 冲突 → 快照 → 差分 → 回滚 → 重放。
//!
//! 运行：`cargo run -p trit-rt --example ffi_smoke`

use trit_rt::ffi::*;
use trit_rt::snapshot::{decode_diff, decode_world};
use trit_rt::wire::{Reader, Writer, op, query};

fn submit(handle: u64, opcode: u32, payload: &[u8]) -> (i32, Vec<u8>) {
    let mut cap = 512usize;
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
            let mut required = [0u8; 8];
            required.copy_from_slice(&out[..8]);
            cap = u64::from_le_bytes(required) as usize + 16;
            continue;
        }
        if status == TT_OK || status == TT_ERR_CONFLICT {
            return (
                status,
                trit_rt::wire::unframe_response(&out)
                    .unwrap_or_default()
                    .to_vec(),
            );
        }
        return (status, Vec::new());
    }
}

fn u64_le(bytes: &[u8]) -> u64 {
    let mut buf = [0u8; 8];
    buf.copy_from_slice(&bytes[..8]);
    u64::from_le_bytes(buf)
}

fn main() {
    unsafe {
        let handle = tt_rt_create(std::ptr::null(), 0);
        assert_ne!(handle, 0);
        println!(
            "runtime {:#x}, abi {}, caps {:#018b}",
            tt_rt_version(),
            tt_rt_abi_version(),
            tt_rt_capabilities(handle)
        );

        // 1. 两个模组订阅：模组 1 监听 NPC_SPAWN，模组 2 监听 DIFFICULTY_SCALE 且依赖 NPC_SPAWN。
        let sub1 = {
            let mut w = Writer::new();
            w.u32(1).u32(100).i32(10).u32(0);
            w.into_vec()
        };
        let (status, response) = submit(handle, op::SUBSCRIBE, &sub1);
        assert_eq!(status, TT_OK);
        println!(
            "[subscribe] mod 1 -> event 100, sub_id = {}",
            u64_le(&response)
        );

        let sub2 = {
            let mut w = Writer::new();
            w.u32(2).u32(101).i32(5).u32(1).u32(100);
            w.into_vec()
        };
        let (status, _) = submit(handle, op::SUBSCRIBE, &sub2);
        assert_eq!(status, TT_OK);
        println!("[subscribe] mod 2 -> event 101 (depends on 100)");

        // 2. 发布事件：标脏传递闭包。
        let publish = {
            let mut w = Writer::new();
            w.u32(100);
            w.trits(&[
                trit_core::value::Ternary::True,
                trit_core::value::Ternary::Unknown,
            ]);
            w.into_vec()
        };
        let (status, response) = submit(handle, op::PUBLISH, &publish);
        assert_eq!(status, TT_OK);
        println!(
            "[publish] event 100 -> {} subscribers dirty",
            Reader::new(&response).u32().unwrap()
        );

        // 3. step：输出本 tick 的动作意图（C# 主线程负责执行）。
        let tick = {
            let mut w = Writer::new();
            w.u64(1);
            w.into_vec()
        };
        let (status, response) = submit(handle, op::STEP, &tick);
        assert_eq!(status, TT_OK);
        let mut reader = Reader::new(&response);
        let count = reader.u32().unwrap();
        println!("[step] tick 1 -> {count} action intents:");
        for _ in 0..count {
            let sub_id = reader.u64().unwrap();
            let event_id = reader.u32().unwrap();
            let mod_id = reader.u32().unwrap();
            println!("    intent: sub={sub_id} event={event_id} mod={mod_id}");
        }

        // 4. 事务：三个模组同时写同一车道 → 多数仲裁（2 票 +1 胜过 1 票 -1）。
        let begin = {
            let mut w = Writer::new();
            w.u32(0);
            w.into_vec()
        };
        let mut txns = Vec::new();
        for _ in 0..3 {
            let (status, response) = submit(handle, op::TXN_BEGIN, &begin);
            assert_eq!(status, TT_OK);
            txns.push(u64_le(&response));
        }
        for (index, (txn, delta, label)) in [
            (txns[0], 2u8, "+1"),
            (txns[1], 0u8, "-1"),
            (txns[2], 2u8, "+1"),
        ]
        .into_iter()
        .enumerate()
        {
            let mut w = Writer::new();
            w.u64(txn).u32(42).u8(delta);
            let (status, _) = submit(handle, op::TXN_SUBMIT, &w.into_vec());
            assert_eq!(status, TT_OK);
            println!("[txn {}] intent key=42 delta={label}", index + 1);
        }
        for txn in &txns {
            let commit = {
                let mut w = Writer::new();
                w.u64(*txn);
                w.into_vec()
            };
            let (status, response) = submit(handle, op::TXN_COMMIT, &commit);
            if status == TT_ERR_CONFLICT {
                // 平局争议：把全部肇事方交回 C#（按模组注册序裁决），事务保持打开。
                println!("[txn {txn}] commit -> CONFLICT (tie), diagnostics:");
                let mut reader = Reader::new(&response);
                let disputes = reader.u32().unwrap();
                for _ in 0..disputes {
                    let key = reader.u32().unwrap();
                    let drivers = reader.u32().unwrap();
                    let mut who = Vec::new();
                    for _ in 0..drivers {
                        let txn_id = reader.u64().unwrap();
                        let delta = reader.u8().unwrap();
                        who.push(format!("txn{txn_id}:{delta}"));
                    }
                    println!("    lane {key} disputed by {who:?} -> C# falls back");
                }
                continue;
            }
            assert_eq!(status, TT_OK);
            let mut reader = Reader::new(&response);
            println!(
                "[txn {txn}] commit -> applied={}, log_seq={}",
                reader.u32().unwrap(),
                reader.u64().unwrap()
            );
        }
        println!("[arbiter] first commit resolved lane 42 by majority: +1 (2 vs 1)");

        // 5. 快照 → 差分 → 回滚。
        let flags = {
            let mut w = Writer::new();
            w.u32(0);
            w.into_vec()
        };
        let (status, response) = submit(handle, op::SNAPSHOT, &flags);
        assert_eq!(status, TT_OK);
        let snap_a = u64_le(&response);
        let mut w = Writer::new();
        w.u64(0).u32(42).u8(0);
        let (status, _) = submit(handle, op::TXN_SUBMIT, &w.into_vec());
        assert_eq!(status, TT_OK);
        let (status, response) = submit(handle, op::SNAPSHOT, &flags);
        assert_eq!(status, TT_OK);
        let snap_b = u64_le(&response);

        let mut w = Writer::new();
        w.u64(snap_a).u64(snap_b);
        let (status, response) = submit(handle, op::DIFF, &w.into_vec());
        assert_eq!(status, TT_OK);
        let entries = decode_diff(&response).unwrap();
        println!("[diff] snap {snap_a} -> {snap_b}: {:?}", entries);

        let rollback = {
            let mut w = Writer::new();
            w.u64(snap_a);
            w.into_vec()
        };
        let (status, _) = submit(handle, op::ROLLBACK_TO, &rollback);
        assert_eq!(status, TT_OK);
        println!("[rollback] world restored to snap {snap_a}");

        // 6. 统计与影子查询收尾。
        let (status, response) = submit(handle, op::STATS, &[]);
        assert_eq!(status, TT_OK);
        let mut reader = Reader::new(&response);
        let stats: Vec<u64> = (0..10).map(|_| reader.u64().unwrap()).collect();
        println!(
            "[stats] published={} dispatched={} committed={} applied={}",
            stats[0], stats[1], stats[2], stats[8]
        );
        let query_shadow = {
            let mut w = Writer::new();
            w.u32(query::SHADOW);
            w.into_vec()
        };
        let (status, response) = submit(handle, op::QUERY, &query_shadow);
        assert_eq!(status, TT_OK);
        let mut reader = Reader::new(&response);
        println!(
            "[shadow] compared={} divergences={}",
            reader.u64().unwrap(),
            reader.u64().unwrap()
        );

        // 7. 世界字节可导出（解码验证）。
        let cap = 4096usize;
        let mut out = vec![0u8; cap];
        let status = tt_rt_snapshot(handle, out.as_mut_ptr(), cap);
        assert_eq!(status, TT_OK);
        // 拆框后前 8 字节是快照 ID，其余是世界投影编码。
        let payload = trit_rt::wire::unframe_response(&out).unwrap();
        let (_, _, world) = decode_world(&payload[8..]).unwrap();
        println!("[export] world lanes = {}", world.len());

        assert_eq!(tt_rt_destroy(handle), TT_OK);
        println!("done.");
    }
}
