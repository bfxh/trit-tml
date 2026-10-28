//! 二进制线格式：与 C# 兼容壳（`dotnet/TritRuntime.Shim`）逐字节对齐。
//!
//! 约定：
//! - 所有多字节整数小端；
//! - trit 序列用 [`trit_codec::pack`] 打成 5 trits/byte，前面带一个 `u32` 个数；
//! - 请求由 `tt_rt_submit(handle, op, payload, len, out, cap)` 承载：
//!   `op` 是 [`op`] 里的 opcode，`payload` 是下表里的请求体；
//! - 响应写进 `out`，长度不够时回 `TT_ERR_BUFFER` 并把所需长度以 `u64`
//!   写进 `out` 前 8 字节（调用方扩容后重试）。

use trit_codec::CodecError;
use trit_codec::pack::{TRITS_PER_BYTE, pack, unpack};
use trit_core::value::Ternary;

use crate::error::RtError;

/// opcode 表。C# 侧 `Opcode` 常量必须与此一致。
pub mod op {
    /// 订阅事件：`mod_id u32, event_id u32, priority i32, dep_count u32, deps u32 * n` → `sub_id u64`
    pub const SUBSCRIBE: u32 = 0x01;
    /// 退订：`sub_id u64` → 空
    pub const UNSUBSCRIBE: u32 = 0x02;
    /// 发布事件：`event_id u32, trits` → `dirty_count u32`
    pub const PUBLISH: u32 = 0x03;
    /// 开事务：`flags u32` → `txn_id u64`
    pub const TXN_BEGIN: u32 = 0x04;
    /// 提交事务：`txn_id u64` → `applied u32, log_seq u64`
    pub const TXN_COMMIT: u32 = 0x05;
    /// 回滚事务：`txn_id u64` → 空
    pub const TXN_ROLLBACK: u32 = 0x06;
    /// 事务内提交意图：`txn_id u64, key u32, delta u8` → 空（`txn_id == 0` 表示自动提交）
    pub const TXN_SUBMIT: u32 = 0x07;
    /// 仲裁一批意图（只判不落地）：`count u32, [txn_id u64, key u32, delta u8] * n`
    /// → `status u8, resolved_count u32, [key u32, delta u8] * n, dispute_count u32, disputes`
    pub const ARBITRATE: u32 = 0x08;
    /// 查询：`query_id u32` → 各查询自己的编码
    pub const QUERY: u32 = 0x09;
    /// 推进一个 tick：`tick u64` → `intent_count u32, [sub_id u64, event_id u32, mod_id u32] * n`
    pub const STEP: u32 = 0x0A;
    /// 打快照：`flags u32` → `snap_id u64`
    pub const SNAPSHOT: u32 = 0x0B;
    /// 差分两个快照：`snap_a u64, snap_b u64` → `entry_count u32, [key u32, old u8, new u8] * n`
    pub const DIFF: u32 = 0x0C;
    /// 回滚到快照：`snap_id u64` → 空
    pub const ROLLBACK_TO: u32 = 0x0D;
    /// 抽确定性随机数：`count u32` → `trits`
    pub const PRNG_NEXT: u32 = 0x0E;
    /// 记录影子差分签名：`trits` → `divergences u64`
    pub const SHADOW_RECORD: u32 = 0x0F;
    /// 能力查询：空 → `abi u32, version u32, caps u64, mode u8`
    pub const CAPABILITIES: u32 = 0x10;
    /// 统计查询：空 → 一组 `u64` 计数器
    pub const STATS: u32 = 0x11;
    /// AI 决策投票（feature `ai-voting`）：`trits`（网络输入，`X` = 未知）
    /// → `intent_count u32, [key u32, delta u8] * n`（`delta == 1` 表示弃权）
    pub const AI_VOTE: u32 = 0x12;
    /// 装载三值权重网络模型：`json_len u32, json bytes` → 空
    pub const AI_SET_MODEL: u32 = 0x13;

    // v2 新增：以下 6 个 opcode 的载荷与响应都是 **UTF-8 JSON**
    //（既有 opcode 保持二进制不变，避免破坏兼容）。见 docs/ABI.md。
    /// 提交一个扫描任务：`ScanRequest` JSON → `{"job": u64}`
    pub const SCAN_SUBMIT: u32 = 0x14;
    /// 取出已完成的任务（取出即出队）：`{"limit": usize}` → `{"jobs": [JobRecord]}`
    pub const JOB_POLL: u32 = 0x15;
    /// 取消任务：`{"job": u64}` 或 `{"owner": string}` → `{"cancelled": u64}`
    pub const JOB_CANCEL: u32 = 0x16;
    /// GPU 后端状态：`{"policy": "auto|off|on", "probe": bool, "trits": usize}` → `GpuStatus`
    pub const GPU_STATUS: u32 = 0x17;
    /// 全局预算快照：`{}` → `BudgetSnapshot`
    pub const BUDGET: u32 = 0x18;
    /// 任务池指标：`{}` → `{"prometheus": string}`
    pub const METRICS: u32 = 0x19;
    /// 批量提交意图（二进制）：`txn_id u64, count u32, [key u32, delta u8] * n` → 空。
    ///
    /// `txn_id == 0` 时整批一次性自动提交（一次仲裁 + 一次落地），而不是逐条提交。
    /// 存在的理由：模组每 tick 会有成百上千条车道变化，逐条走 [`TXN_SUBMIT`] 时
    /// 每条一次 FFI + 一次 JSON/二进制装箱，实测 0.5µs/条；批量后同一批只付一次
    /// 调用开销。
    pub const TXN_SUBMIT_BATCH: u32 = 0x1A;
    /// 会话复位（无载荷 → 空）：把世界投影 / 日志 / 快照 / 事务 / 事件 / 影子 / 任务结果
    /// 全部清空，并把 tick 与 PRNG 归零。**配置与任务池线程保持不变**。
    ///
    /// 存在的理由：这张表里的状态都是"某个存档世界的投影"。玩家退出存档后
    /// 这些东西既没有意义、又一直占着内存（默认配置下日志约 5MB + 快照数 MB +
    /// 车道投影），下一张存档还会接着往上叠。进/出存档时复位一次，进程占用回到基线。
    ///
    /// 版本策略：这是**可选**操作（不复位也只是多占内存，功能不受影响），
    /// 所以没有因为它递增 ABI 版本——老内核返回 [`crate::error::RtError::InvalidInput`]，
    /// 上层记一条日志后继续跑，不会像 `TXN_SUBMIT_BATCH` 那样每 tick 失败。
    pub const SESSION_RESET: u32 = 0x1B;
}

/// 查询编号。
pub mod query {
    /// 本 tick 待派发订阅数 → `u32`
    pub const DIRTY_COUNT: u32 = 0;
    /// 世界车道数 → `u32`
    pub const WORLD_SIZE: u32 = 1;
    /// 事务日志长度 → `u64`
    pub const LOG_LEN: u32 = 2;
    /// 快照数 → `u32`
    pub const SNAPSHOT_COUNT: u32 = 3;
    /// 未关闭事务数 → `u32`
    pub const OPEN_TXN_COUNT: u32 = 4;
    /// 影子统计 → `compared u64, divergences u64`
    pub const SHADOW: u32 = 5;
}

/// 能力位（`tt_rt_capabilities` 的返回值）。
pub mod caps {
    pub const ROUTER: u64 = 1 << 0;
    pub const TXN_LOG: u64 = 1 << 1;
    pub const SNAPSHOT: u64 = 1 << 2;
    pub const DIFF: u64 = 1 << 3;
    pub const ARBITRATE: u64 = 1 << 4;
    pub const PRNG: u64 = 1 << 5;
    pub const CODEC: u64 = 1 << 6;
    pub const SHADOW: u64 = 1 << 7;
    pub const AI: u64 = 1 << 8;
    /// 并行任务池（v2）。
    pub const PARALLEL: u64 = 1 << 9;
    /// GPU 后端**可用**（构建带了 `gpu` feature 且运行时探测通过；v2）。
    pub const GPU: u64 = 1 << 10;
    /// 全局资源预算（v2）。
    pub const BUDGET: u64 = 1 << 11;
}

/// 读取器。
#[derive(Debug, Clone)]
pub struct Reader<'a> {
    buf: &'a [u8],
    pos: usize,
}

impl<'a> Reader<'a> {
    pub fn new(buf: &'a [u8]) -> Self {
        Self { buf, pos: 0 }
    }

    fn take(&mut self, n: usize) -> Result<&'a [u8], RtError> {
        let end = self
            .pos
            .checked_add(n)
            .ok_or_else(|| RtError::Decode("长度溢出".into()))?;
        if end > self.buf.len() {
            return Err(RtError::Decode(format!(
                "需要 {n} 字节，只剩 {}",
                self.buf.len() - self.pos
            )));
        }
        let slice = &self.buf[self.pos..end];
        self.pos = end;
        Ok(slice)
    }

    pub fn u8(&mut self) -> Result<u8, RtError> {
        Ok(self.take(1)?[0])
    }

    pub fn u32(&mut self) -> Result<u32, RtError> {
        let b = self.take(4)?;
        Ok(u32::from_le_bytes([b[0], b[1], b[2], b[3]]))
    }

    pub fn i32(&mut self) -> Result<i32, RtError> {
        let b = self.take(4)?;
        Ok(i32::from_le_bytes([b[0], b[1], b[2], b[3]]))
    }

    pub fn u64(&mut self) -> Result<u64, RtError> {
        let b = self.take(8)?;
        Ok(u64::from_le_bytes([
            b[0], b[1], b[2], b[3], b[4], b[5], b[6], b[7],
        ]))
    }

    /// 读一段定长字节。
    pub fn bytes(&mut self, len: usize) -> Result<Vec<u8>, RtError> {
        Ok(self.take(len)?.to_vec())
    }

    /// 读 `u32` 个数 + 5 trits/byte 的打包体。
    pub fn trits(&mut self) -> Result<Vec<Ternary>, RtError> {
        let count = self.u32()? as usize;
        let byte_len = count.div_ceil(TRITS_PER_BYTE);
        let bytes = self.bytes(byte_len)?;
        unpack(&bytes, count).map_err(|e: CodecError| RtError::Decode(e.to_string()))
    }

    /// 是否还有未读字节。
    pub fn is_empty(&self) -> bool {
        self.pos >= self.buf.len()
    }
}

/// 写入器。
#[derive(Debug, Clone, Default)]
pub struct Writer {
    buf: Vec<u8>,
}

impl Writer {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn u8(&mut self, value: u8) -> &mut Self {
        self.buf.push(value);
        self
    }

    pub fn u32(&mut self, value: u32) -> &mut Self {
        self.buf.extend_from_slice(&value.to_le_bytes());
        self
    }

    pub fn i32(&mut self, value: i32) -> &mut Self {
        self.buf.extend_from_slice(&value.to_le_bytes());
        self
    }

    pub fn u64(&mut self, value: u64) -> &mut Self {
        self.buf.extend_from_slice(&value.to_le_bytes());
        self
    }

    pub fn bytes(&mut self, value: &[u8]) -> &mut Self {
        self.buf.extend_from_slice(value);
        self
    }

    /// 写 `u32` 个数 + 5 trits/byte 打包体。
    pub fn trits(&mut self, values: &[Ternary]) -> &mut Self {
        self.u32(values.len() as u32);
        let packed = pack(values);
        self.buf.extend_from_slice(&packed);
        self
    }

    pub fn len(&self) -> usize {
        self.buf.len()
    }

    pub fn is_empty(&self) -> bool {
        self.buf.is_empty()
    }

    pub fn into_vec(self) -> Vec<u8> {
        self.buf
    }
}

/// trit → 线格式字节（0/1/2）。
pub fn trit_byte(value: Ternary) -> u8 {
    value.index() as u8
}

/// 线格式字节 → trit；越界回退 `Unknown`（保守，与 trit-core 的 `from_index` 一致）。
pub fn trit_from_byte(byte: u8) -> Ternary {
    Ternary::from_index(byte as usize)
}

/// 响应装框：`[u32 payload_len][payload]`。调用方据此知道有效字节数，
/// 不必猜缓冲区里哪一段是响应。
pub fn frame_response(payload: &[u8]) -> Vec<u8> {
    let mut framed = Vec::with_capacity(4 + payload.len());
    framed.extend_from_slice(&(payload.len() as u32).to_le_bytes());
    framed.extend_from_slice(payload);
    framed
}

/// 拆框；长度非法时返回 `None`。
pub fn unframe_response(framed: &[u8]) -> Option<&[u8]> {
    if framed.len() < 4 {
        return None;
    }
    let len = u32::from_le_bytes([framed[0], framed[1], framed[2], framed[3]]) as usize;
    if framed.len() < 4 + len {
        return None;
    }
    Some(&framed[4..4 + len])
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn round_trip_primitives() {
        let mut w = Writer::new();
        w.u8(7).u32(0xDEAD_BEEF).i32(-5).u64(0x0102_0304_0506_0708);
        let bytes = w.into_vec();
        let mut r = Reader::new(&bytes);
        assert_eq!(r.u8().unwrap(), 7);
        assert_eq!(r.u32().unwrap(), 0xDEAD_BEEF);
        assert_eq!(r.i32().unwrap(), -5);
        assert_eq!(r.u64().unwrap(), 0x0102_0304_0506_0708);
        assert!(r.is_empty());
    }

    #[test]
    fn round_trip_trits() {
        let values = vec![
            Ternary::False,
            Ternary::Unknown,
            Ternary::True,
            Ternary::True,
            Ternary::False,
            Ternary::Unknown,
            Ternary::True,
        ];
        let mut w = Writer::new();
        w.trits(&values);
        let bytes = w.into_vec();
        // 5 trits/byte ⇒ 7 个 trit 占 2 字节 + 4 字节个数。
        assert_eq!(bytes.len(), 4 + 2);
        let mut r = Reader::new(&bytes);
        assert_eq!(r.trits().unwrap(), values);
    }

    #[test]
    fn truncated_input_is_rejected() {
        let mut r = Reader::new(&[1, 2, 3]);
        assert!(r.u64().is_err());
        assert!(r.trits().is_err());
    }
}
