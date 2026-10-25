//! 运行时本体：把路由器、事务日志、仲裁器、快照存储、确定性 PRNG 和
//! 影子日志装配成一个可被 C ABI 驱动的内核。
//!
//! 分工边界（架构的核心约束）：
//! - Rust 只处理**纯数据**：句柄、事务、动作意图、trit 张量；
//! - 它**从不持有** Player / NPC / Tile 等 Unity/tModLoader 对象；
//! - 所有真实状态修改由 C# 在游戏主线程提交。

use std::collections::{BTreeMap, VecDeque};

use serde::Deserialize;
use trit_core::value::Ternary;

use crate::arbiter::{
    ArbitrationMode, Intent, LaneDispute, ResolvedIntent, apply_delta, arbitrate,
};
use crate::error::RtError;
use crate::prng::DeterministicPrng;
use crate::router::{EventRouter, ScheduleMetrics};
use crate::snapshot::{DiffEntry, Snapshot, diff_worlds, encode_diff, encode_world};
use crate::txn::{TxnLog, TxnTable};
use crate::wire::{self, Reader, Writer, op, query};

/// 世界车道数上限（防无界增长吃内存）。
pub const MAX_WORLD_LANES: usize = 1 << 20;

/// 影子签名滚动窗口大小。
const SHADOW_WINDOW: usize = 64;

/// 运行时配置（JSON 线格式，见 ABI 文档）。
#[derive(Debug, Clone, PartialEq, Eq, Deserialize)]
#[serde(default)]
pub struct RuntimeConfig {
    /// 确定性种子。
    pub seed: u64,
    /// 订阅者上限。
    pub max_subscribers: usize,
    /// 并发事务上限。
    pub max_open_txns: usize,
    /// 快照保留数。
    pub max_snapshots: usize,
    /// 事务日志条数上限（只追加，写满即报错，上层回退到 C# 路径）。
    pub max_log_entries: usize,
    /// 仲裁模式：`"strict"` 或 `"majority"`。
    pub arbitration: ArbitrationModeSerde,
    /// 是否启用影子差分记录。
    pub shadow: bool,
}

impl Default for RuntimeConfig {
    fn default() -> Self {
        Self {
            seed: 0x5EED_1234,
            max_subscribers: 65_536,
            max_open_txns: 4_096,
            max_snapshots: 64,
            max_log_entries: 1 << 20,
            arbitration: ArbitrationModeSerde::Majority,
            shadow: true,
        }
    }
}

/// 仲裁模式的 JSON 表示。
#[derive(Debug, Clone, Copy, PartialEq, Eq, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum ArbitrationModeSerde {
    Strict,
    Majority,
}

impl From<ArbitrationModeSerde> for ArbitrationMode {
    fn from(value: ArbitrationModeSerde) -> Self {
        match value {
            ArbitrationModeSerde::Strict => Self::Strict,
            ArbitrationModeSerde::Majority => Self::Majority,
        }
    }
}

impl RuntimeConfig {
    /// 从 JSON 字节解析；空输入用默认值。
    pub fn from_json(bytes: &[u8]) -> Result<Self, RtError> {
        if bytes.is_empty() {
            return Ok(Self::default());
        }
        serde_json::from_slice(bytes)
            .map_err(|error| RtError::Decode(format!("配置 JSON 非法：{error}")))
    }
}

/// 影子日志：只记录不一致，从不提交。
#[derive(Debug, Clone, Default)]
pub struct ShadowLog {
    /// 已参与差分的次数。
    pub compared: u64,
    /// 发现不一致的次数。
    pub divergences: u64,
    /// 最近的不一致签名（滚动窗口）。
    pub recent: VecDeque<Vec<u8>>,
}

/// 统计计数器（顺序固定，见 ABI 文档）。
#[derive(Debug, Clone, Default)]
pub struct Stats {
    pub events_published: u64,
    pub events_dispatched: u64,
    pub txns_committed: u64,
    pub txns_rolled_back: u64,
    pub snapshots_taken: u64,
    pub diffs: u64,
    pub arbitrations: u64,
    pub conflicts: u64,
    pub intents_applied: u64,
    pub prng_draws: u64,
}

impl Stats {
    fn encode(&self) -> Vec<u8> {
        let mut writer = Writer::new();
        writer
            .u64(self.events_published)
            .u64(self.events_dispatched)
            .u64(self.txns_committed)
            .u64(self.txns_rolled_back)
            .u64(self.snapshots_taken)
            .u64(self.diffs)
            .u64(self.arbitrations)
            .u64(self.conflicts)
            .u64(self.intents_applied)
            .u64(self.prng_draws);
        writer.into_vec()
    }
}

/// 三进制运行时。
#[derive(Debug, Clone)]
pub struct TritRuntime {
    config: RuntimeConfig,
    arbitration: ArbitrationMode,
    tick: u64,
    router: EventRouter,
    log: TxnLog,
    txns: TxnTable,
    /// 世界投影：车道键 → 三进制值。
    world: BTreeMap<u32, Ternary>,
    snapshots: BTreeMap<u64, Snapshot>,
    next_snapshot_id: u64,
    prng: DeterministicPrng,
    shadow: ShadowLog,
    stats: Stats,
    last_error: String,
    /// 每个事件最近一次发布的 trit 载荷。
    event_payloads: BTreeMap<u32, Vec<Ternary>>,
    #[cfg(feature = "ai-voting")]
    ai_model: Option<trit_net::Model>,
}

impl TritRuntime {
    pub fn new(config: RuntimeConfig) -> Self {
        Self {
            router: EventRouter::new(config.max_subscribers),
            txns: TxnTable::new(config.max_open_txns),
            arbitration: config.arbitration.into(),
            config,
            tick: 0,
            log: TxnLog::new(),
            world: BTreeMap::new(),
            snapshots: BTreeMap::new(),
            next_snapshot_id: 0,
            prng: DeterministicPrng::new(0),
            shadow: ShadowLog::default(),
            stats: Stats::default(),
            last_error: String::new(),
            event_payloads: BTreeMap::new(),
            #[cfg(feature = "ai-voting")]
            ai_model: None,
        }
    }

    /// 当前 tick。
    pub fn tick(&self) -> u64 {
        self.tick
    }

    /// 世界投影（只读视图）。
    pub fn world(&self) -> &BTreeMap<u32, Ternary> {
        &self.world
    }

    /// 调度指标。
    pub fn schedule_metrics(&self) -> ScheduleMetrics {
        self.router.metrics()
    }

    /// 最近一次错误信息。
    pub fn last_error(&self) -> &str {
        &self.last_error
    }

    /// 能力位图。
    pub fn capability_bits(&self) -> u64 {
        let mut caps = wire::caps::ROUTER
            | wire::caps::TXN_LOG
            | wire::caps::SNAPSHOT
            | wire::caps::DIFF
            | wire::caps::ARBITRATE
            | wire::caps::PRNG
            | wire::caps::CODEC
            | wire::caps::SHADOW;
        #[cfg(feature = "ai-voting")]
        {
            caps |= wire::caps::AI;
        }
        caps
    }

    /// 分发一个 opcode。
    pub fn dispatch(&mut self, opcode: u32, payload: &[u8]) -> Result<Vec<u8>, RtError> {
        let result = match opcode {
            op::SUBSCRIBE => self.op_subscribe(payload),
            op::UNSUBSCRIBE => self.op_unsubscribe(payload),
            op::PUBLISH => self.op_publish(payload),
            op::TXN_BEGIN => self.op_txn_begin(payload),
            op::TXN_COMMIT => self.op_txn_commit(payload),
            op::TXN_ROLLBACK => self.op_txn_rollback(payload),
            op::TXN_SUBMIT => self.op_txn_submit(payload),
            op::ARBITRATE => self.op_arbitrate(payload),
            op::QUERY => self.op_query(payload),
            op::STEP => self.op_step(payload),
            op::SNAPSHOT => self.op_snapshot(payload),
            op::DIFF => self.op_diff(payload),
            op::ROLLBACK_TO => self.op_rollback_to(payload),
            op::PRNG_NEXT => self.op_prng_next(payload),
            op::SHADOW_RECORD => self.op_shadow_record(payload),
            op::CAPABILITIES => self.op_capabilities(),
            op::STATS => Ok(self.stats.encode()),
            #[cfg(feature = "ai-voting")]
            op::AI_VOTE => self.op_ai_vote(payload),
            #[cfg(feature = "ai-voting")]
            op::AI_SET_MODEL => self.op_ai_set_model(payload),
            _ => Err(RtError::InvalidInput(format!("未知 opcode {opcode:#x}"))),
        };
        if let Err(error) = &result {
            self.last_error = error.to_string();
        }
        result
    }

    // ---- opcode 实现 ----

    fn op_subscribe(&mut self, payload: &[u8]) -> Result<Vec<u8>, RtError> {
        let mut reader = Reader::new(payload);
        let mod_id = reader.u32()?;
        let event_id = reader.u32()?;
        let priority = reader.i32()?;
        let dep_count = reader.u32()? as usize;
        let mut deps = Vec::with_capacity(dep_count.min(4096));
        for _ in 0..dep_count {
            deps.push(reader.u32()?);
        }
        let sub_id = self.router.subscribe(mod_id, event_id, priority, deps)?;
        let mut writer = Writer::new();
        writer.u64(sub_id);
        Ok(writer.into_vec())
    }

    fn op_unsubscribe(&mut self, payload: &[u8]) -> Result<Vec<u8>, RtError> {
        let mut reader = Reader::new(payload);
        let sub_id = reader.u64()?;
        self.router.unsubscribe(sub_id)?;
        Ok(Vec::new())
    }

    fn op_publish(&mut self, payload: &[u8]) -> Result<Vec<u8>, RtError> {
        let mut reader = Reader::new(payload);
        let event_id = reader.u32()?;
        let trits = reader.trits()?;
        self.stats.events_published += 1;
        if self.event_payloads.len() >= 4096 {
            // 简单的滚动淘汰：清掉最早插入的一批（BTreeMap 序即事件 ID 序）。
            let oldest: Vec<u32> = self.event_payloads.keys().take(1024).copied().collect();
            for key in oldest {
                self.event_payloads.remove(&key);
            }
        }
        self.event_payloads.insert(event_id, trits);
        let dirty = self.router.publish(event_id);
        let mut writer = Writer::new();
        writer.u32(dirty as u32);
        Ok(writer.into_vec())
    }

    fn op_txn_begin(&mut self, payload: &[u8]) -> Result<Vec<u8>, RtError> {
        let mut reader = Reader::new(payload);
        let _flags = reader.u32()?;
        let txn_id = self.txns.begin(self.tick)?;
        let mut writer = Writer::new();
        writer.u64(txn_id);
        Ok(writer.into_vec())
    }

    fn op_txn_submit(&mut self, payload: &[u8]) -> Result<Vec<u8>, RtError> {
        let mut reader = Reader::new(payload);
        let txn_id = reader.u64()?;
        let key = reader.u32()?;
        let delta = wire::trit_from_byte(reader.u8()?);
        let intent = Intent { txn_id, key, delta };
        if txn_id == 0 {
            // 自动提交：立即仲裁 + 落地。
            let (applied, _) = self.commit_intents(std::slice::from_ref(&intent), 0)?;
            self.stats.intents_applied += applied as u64;
            return Ok(Vec::new());
        }
        self.txns.push_intent(txn_id, intent)?;
        Ok(Vec::new())
    }

    fn op_txn_commit(&mut self, payload: &[u8]) -> Result<Vec<u8>, RtError> {
        let mut reader = Reader::new(payload);
        let txn_id = reader.u64()?;
        // 先克隆意图做仲裁：冲突时事务保持打开，调用方可重试或回滚。
        let intents = self.txns.clone_intents(txn_id)?;
        let (applied, log_seq) = self.commit_intents(&intents, txn_id)?;
        self.txns.finish_commit(txn_id)?;
        self.stats.txns_committed += 1;
        self.stats.intents_applied += applied as u64;
        let mut writer = Writer::new();
        writer.u32(applied);
        writer.u64(log_seq);
        Ok(writer.into_vec())
    }

    fn op_txn_rollback(&mut self, payload: &[u8]) -> Result<Vec<u8>, RtError> {
        let mut reader = Reader::new(payload);
        let txn_id = reader.u64()?;
        self.txns.finish_rollback(txn_id)?;
        self.stats.txns_rolled_back += 1;
        Ok(Vec::new())
    }

    fn op_arbitrate(&mut self, payload: &[u8]) -> Result<Vec<u8>, RtError> {
        let mut reader = Reader::new(payload);
        let count = reader.u32()? as usize;
        let mut intents = Vec::with_capacity(count.min(4096));
        for _ in 0..count {
            let txn_id = reader.u64()?;
            let key = reader.u32()?;
            let delta = wire::trit_from_byte(reader.u8()?);
            intents.push(Intent { txn_id, key, delta });
        }
        self.stats.arbitrations += 1;
        match arbitrate(&intents, self.arbitration) {
            Ok(resolved) => {
                let mut writer = Writer::new();
                writer.u8(0); // status: clean
                writer.u32(resolved.len() as u32);
                for intent in &resolved {
                    writer.u32(intent.key);
                    writer.u8(wire::trit_byte(intent.delta));
                }
                writer.u32(0); // 无争议
                Ok(writer.into_vec())
            }
            Err(disputes) => {
                self.stats.conflicts += disputes.len() as u64;
                let mut writer = Writer::new();
                writer.u8(1); // status: disputes
                writer.u32(0);
                writer.u32(disputes.len() as u32);
                for dispute in &disputes {
                    dispute.encode(&mut writer);
                }
                Ok(writer.into_vec())
            }
        }
    }

    fn op_query(&mut self, payload: &[u8]) -> Result<Vec<u8>, RtError> {
        let mut reader = Reader::new(payload);
        let query_id = reader.u32()?;
        let mut writer = Writer::new();
        match query_id {
            query::DIRTY_COUNT => {
                writer.u32(self.router.dirty_count() as u32);
            }
            query::WORLD_SIZE => {
                writer.u32(self.world.len() as u32);
            }
            query::LOG_LEN => {
                writer.u64(self.log.len() as u64);
            }
            query::SNAPSHOT_COUNT => {
                writer.u32(self.snapshots.len() as u32);
            }
            query::OPEN_TXN_COUNT => {
                writer.u32(self.txns.open_count() as u32);
            }
            query::SHADOW => {
                writer.u64(self.shadow.compared);
                writer.u64(self.shadow.divergences);
            }
            6 => {
                // 取某个事件最近一次发布的载荷。
                let event_id = reader.u32()?;
                let trits = self
                    .event_payloads
                    .get(&event_id)
                    .cloned()
                    .unwrap_or_default();
                writer.trits(&trits);
            }
            _ => {
                return Err(RtError::InvalidInput(format!("未知查询 {query_id}")));
            }
        }
        Ok(writer.into_vec())
    }

    fn op_step(&mut self, payload: &[u8]) -> Result<Vec<u8>, RtError> {
        let mut reader = Reader::new(payload);
        let tick = reader.u64()?;
        self.tick = tick;
        // 每个 tick 用 (seed, tick) 重新播种 ⇒ 同 tick 同序列。
        self.prng.reseed(self.config.seed, tick);
        let dispatch = self.router.take_dispatch();
        let mut writer = Writer::new();
        writer.u32(dispatch.len() as u32);
        for sub_id in &dispatch {
            let Some(subscriber) = self.router.get(*sub_id) else {
                continue;
            };
            writer.u64(subscriber.id);
            writer.u32(subscriber.event_id);
            writer.u32(subscriber.mod_id);
            self.stats.events_dispatched += 1;
        }
        Ok(writer.into_vec())
    }

    fn op_snapshot(&mut self, payload: &[u8]) -> Result<Vec<u8>, RtError> {
        let mut reader = Reader::new(payload);
        let _flags = reader.u32()?;
        self.next_snapshot_id += 1;
        let id = self.next_snapshot_id;
        let bytes = encode_world(&self.world, self.tick, self.log.seq());
        let snapshot = Snapshot {
            id,
            tick: self.tick,
            log_seq: self.log.seq(),
            world: self.world.clone(),
            bytes,
        };
        if self.snapshots.len() >= self.config.max_snapshots {
            // 淘汰最旧的一个。
            if let Some(oldest) = self.snapshots.keys().next().copied() {
                self.snapshots.remove(&oldest);
            }
        }
        self.snapshots.insert(id, snapshot);
        self.stats.snapshots_taken += 1;
        let mut writer = Writer::new();
        writer.u64(id);
        Ok(writer.into_vec())
    }

    fn op_diff(&mut self, payload: &[u8]) -> Result<Vec<u8>, RtError> {
        let mut reader = Reader::new(payload);
        let snap_a = reader.u64()?;
        let snap_b = reader.u64()?;
        let before = self
            .snapshots
            .get(&snap_a)
            .ok_or(RtError::SnapshotNotFound(snap_a))?;
        let after = self
            .snapshots
            .get(&snap_b)
            .ok_or(RtError::SnapshotNotFound(snap_b))?;
        let entries: Vec<DiffEntry> = diff_worlds(&before.world, &after.world);
        self.stats.diffs += 1;
        Ok(encode_diff(&entries))
    }

    fn op_rollback_to(&mut self, payload: &[u8]) -> Result<Vec<u8>, RtError> {
        let mut reader = Reader::new(payload);
        let snap_id = reader.u64()?;
        let snapshot = self
            .snapshots
            .get(&snap_id)
            .ok_or(RtError::SnapshotNotFound(snap_id))?;
        // 只还原世界投影；日志保持只追加，之后可用 replay_from(snap.log_seq) 重做。
        self.world = snapshot.world.clone();
        Ok(Vec::new())
    }

    fn op_prng_next(&mut self, payload: &[u8]) -> Result<Vec<u8>, RtError> {
        let mut reader = Reader::new(payload);
        let count = reader.u32()? as usize;
        if count > 1 << 16 {
            return Err(RtError::InvalidInput(format!(
                "单次抽样数 {count} 超过上限 65536"
            )));
        }
        let trits = self.prng.fill_trits(count);
        self.stats.prng_draws += count as u64;
        let mut writer = Writer::new();
        writer.trits(&trits);
        Ok(writer.into_vec())
    }

    fn op_shadow_record(&mut self, payload: &[u8]) -> Result<Vec<u8>, RtError> {
        let mut reader = Reader::new(payload);
        let signature = reader.trits()?;
        self.shadow.compared += 1;
        // 调用方语义：签名非空 = 发现不一致。只记录，不提交，不影响任何状态。
        if !signature.is_empty() {
            self.shadow.divergences += 1;
            if self.shadow.recent.len() >= SHADOW_WINDOW {
                self.shadow.recent.pop_front();
            }
            // 存成字节，便于直接回给 C# 做诊断。
            let packed = trit_codec::pack::pack(&signature);
            self.shadow.recent.push_back(packed);
        }
        let mut writer = Writer::new();
        writer.u64(self.shadow.divergences);
        Ok(writer.into_vec())
    }

    fn op_capabilities(&self) -> Result<Vec<u8>, RtError> {
        let mut writer = Writer::new();
        writer.u32(crate::ffi::ABI_VERSION);
        writer.u32(crate::ffi::RUNTIME_VERSION);
        writer.u64(self.capability_bits());
        writer.u8(match self.config.arbitration {
            ArbitrationModeSerde::Strict => 0,
            ArbitrationModeSerde::Majority => 1,
        });
        Ok(writer.into_vec())
    }

    #[cfg(feature = "ai-voting")]
    fn op_ai_set_model(&mut self, payload: &[u8]) -> Result<Vec<u8>, RtError> {
        let mut reader = Reader::new(payload);
        let len = reader.u32()? as usize;
        let json = reader.bytes(len)?;
        let text = String::from_utf8(json)
            .map_err(|_| RtError::Decode("模型 JSON 不是合法 UTF-8".into()))?;
        let model = trit_net::Model::from_json(&text)
            .map_err(|error| RtError::InvalidInput(format!("模型非法：{error}")))?;
        self.ai_model = Some(model);
        Ok(Vec::new())
    }

    #[cfg(feature = "ai-voting")]
    fn op_ai_vote(&mut self, payload: &[u8]) -> Result<Vec<u8>, RtError> {
        let mut reader = Reader::new(payload);
        let inputs = reader.trits()?;
        let Some(model) = self.ai_model.as_ref() else {
            return Err(RtError::InvalidInput("尚未装载 AI 模型".into()));
        };
        let analysis = trit_net::analyze(model, &inputs, trit_net::DEFAULT_BUDGET)
            .map_err(|error| RtError::InvalidInput(format!("推理失败：{error}")))?;
        let mut writer = Writer::new();
        let mut count = 0usize;
        // 先写占位，回填数量。
        writer.u32(0);
        for (index, outcome) in analysis.outputs.iter().enumerate() {
            let delta = match outcome {
                trit_net::Outcome::Definite(trit_net::Sign::Pos) => Ternary::True,
                trit_net::Outcome::Definite(trit_net::Sign::Neg) => Ternary::False,
                // 弃权 / 歧义 / 超预算：一律记 0（不驱动）。
                _ => Ternary::Unknown,
            };
            writer.u32(index as u32);
            writer.u8(wire::trit_byte(delta));
            count += 1;
        }
        let bytes = writer.into_vec();
        let mut bytes = bytes;
        bytes[0..4].copy_from_slice(&(count as u32).to_le_bytes());
        Ok(bytes)
    }

    // ---- 内部 ----

    /// 仲裁一批意图并落地；冲突时整体失败（不产生部分应用）。
    ///
    /// 仲裁范围 = 本批意图 ∪ 其他未关闭事务的待落地意图——跨模组冲突在
    /// **提交时点**暴露，而不是各自落地后才互相覆盖。
    fn commit_intents(&mut self, intents: &[Intent], txn_id: u64) -> Result<(u32, u64), RtError> {
        if intents.is_empty() && self.txns.pending_intents(txn_id).is_empty() {
            return Ok((0, self.log.seq()));
        }
        let mut batch: Vec<Intent> = intents.to_vec();
        batch.extend(self.txns.pending_intents(txn_id));
        let resolved: Vec<ResolvedIntent> =
            arbitrate(&batch, self.arbitration).map_err(|disputes: Vec<LaneDispute>| {
                RtError::Conflict {
                    count: disputes.len(),
                    disputes,
                }
            })?;
        // 先做容量预检，保证「要么全落地，要么全不落地」。
        let new_lanes = resolved
            .iter()
            .filter(|intent| !self.world.contains_key(&intent.key))
            .count();
        if self.world.len() + new_lanes > MAX_WORLD_LANES {
            return Err(RtError::Capacity(format!(
                "世界车道数将达到上限 {MAX_WORLD_LANES}"
            )));
        }
        if self.log.is_full(self.config.max_log_entries) {
            return Err(RtError::Capacity(format!(
                "事务日志达到上限 {} 条",
                self.config.max_log_entries
            )));
        }
        let mut applied = 0u32;
        for intent in resolved {
            if intent.delta == Ternary::Unknown {
                continue;
            }
            let current = self
                .world
                .get(&intent.key)
                .copied()
                .unwrap_or(Ternary::Unknown);
            let next = apply_delta(current, intent.delta);
            self.world.insert(intent.key, next);
            self.log.append(txn_id, self.tick, &intent);
            applied += 1;
        }
        Ok((applied, self.log.seq()))
    }

    /// 从 `seq` 之后重放日志（确定性重放验证用）。
    pub fn replay_from(&mut self, seq: u64) -> usize {
        let entries: Vec<(u32, Ternary)> = self
            .log
            .replay_from(seq)
            .map(|entry| (entry.key, entry.delta))
            .collect();
        let mut applied = 0usize;
        for (key, delta) in entries {
            let current = self.world.get(&key).copied().unwrap_or(Ternary::Unknown);
            self.world.insert(key, apply_delta(current, delta));
            applied += 1;
        }
        applied
    }

    /// 快照字节（导出用）。
    pub fn snapshot_bytes(&self, id: u64) -> Option<&[u8]> {
        self.snapshots
            .get(&id)
            .map(|snapshot| snapshot.bytes.as_slice())
    }

    /// 快照对应的日志序列号（重放锚点）。
    pub fn snapshot_log_seq(&self, id: u64) -> Option<u64> {
        self.snapshots.get(&id).map(|snapshot| snapshot.log_seq)
    }

    /// 世界编码字节（导出用）。
    pub fn world_bytes(&self) -> Vec<u8> {
        encode_world(&self.world, self.tick, self.log.seq())
    }
}

/// 供测试/示例使用的默认配置构造。
pub fn default_runtime() -> TritRuntime {
    TritRuntime::new(RuntimeConfig::default())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn config_parses_from_json() {
        let config = RuntimeConfig::from_json(br#"{"seed":7,"arbitration":"strict"}"#).unwrap();
        assert_eq!(config.seed, 7);
        assert_eq!(config.arbitration, ArbitrationModeSerde::Strict);
        assert_eq!(config.max_snapshots, 64, "未提供的字段用默认值");
        assert_eq!(
            RuntimeConfig::from_json(b"").unwrap(),
            RuntimeConfig::default()
        );
        assert!(RuntimeConfig::from_json(b"not json").is_err());
    }

    #[test]
    fn full_transaction_lifecycle() {
        let mut rt = default_runtime();
        let begin = {
            let mut w = Writer::new();
            w.u32(0);
            w.into_vec()
        };
        let txn_id = {
            let out = rt.dispatch(op::TXN_BEGIN, &begin).unwrap();
            Reader::new(&out).u64().unwrap()
        };
        let submit = {
            let mut w = Writer::new();
            w.u64(txn_id).u32(42).u8(2); // key 42, +1
            w.into_vec()
        };
        rt.dispatch(op::TXN_SUBMIT, &submit).unwrap();
        let commit = {
            let mut w = Writer::new();
            w.u64(txn_id);
            w.into_vec()
        };
        let out = rt.dispatch(op::TXN_COMMIT, &commit).unwrap();
        let mut reader = Reader::new(&out);
        assert_eq!(reader.u32().unwrap(), 1); // applied
        assert_eq!(reader.u64().unwrap(), 1); // log seq
        assert_eq!(rt.world()[&42], Ternary::True);
    }

    #[test]
    fn snapshot_rollback_and_replay() {
        let mut rt = default_runtime();
        let autocommit = |rt: &mut TritRuntime, key: u32, delta: u8| {
            let mut w = Writer::new();
            w.u64(0).u32(key).u8(delta);
            let payload = w.into_vec();
            rt.dispatch(op::TXN_SUBMIT, &payload).unwrap();
        };
        autocommit(&mut rt, 1, 2);
        autocommit(&mut rt, 2, 2);
        let snap = {
            let mut w = Writer::new();
            w.u32(0);
            let payload = w.into_vec();
            let out = rt.dispatch(op::SNAPSHOT, &payload).unwrap();
            Reader::new(&out).u64().unwrap()
        };
        autocommit(&mut rt, 1, 0); // 撤掉 key 1
        autocommit(&mut rt, 3, 2);
        assert_eq!(rt.world().len(), 3);

        let rollback = {
            let mut w = Writer::new();
            w.u64(snap);
            w.into_vec()
        };
        rt.dispatch(op::ROLLBACK_TO, &rollback).unwrap();
        assert_eq!(rt.world().len(), 2);
        assert_eq!(rt.world()[&1], Ternary::True);

        // 重放快照之后的日志，回到回滚前状态。
        let replayed = rt.replay_from(2);
        assert_eq!(replayed, 2);
        assert_eq!(rt.world().len(), 3);
        assert_eq!(rt.world()[&1], Ternary::Unknown);
    }

    #[test]
    fn conflict_makes_commit_fail_atomically() {
        let config = RuntimeConfig {
            arbitration: ArbitrationModeSerde::Strict,
            ..RuntimeConfig::default()
        };
        let mut rt = TritRuntime::new(config);
        let begin = {
            let mut w = Writer::new();
            w.u32(0);
            w.into_vec()
        };
        let txn_a = {
            let out = rt.dispatch(op::TXN_BEGIN, &begin).unwrap();
            Reader::new(&out).u64().unwrap()
        };
        let txn_b = {
            let out = rt.dispatch(op::TXN_BEGIN, &begin).unwrap();
            Reader::new(&out).u64().unwrap()
        };
        for (txn, delta) in [(txn_a, 0u8), (txn_b, 2u8)] {
            let mut w = Writer::new();
            w.u64(txn).u32(7).u8(delta);
            rt.dispatch(op::TXN_SUBMIT, &w.into_vec()).unwrap();
        }
        let commit = {
            let mut w = Writer::new();
            w.u64(txn_a);
            w.into_vec()
        };
        let error = rt.dispatch(op::TXN_COMMIT, &commit).unwrap_err();
        assert!(
            matches!(&error, RtError::Conflict { disputes, .. } if disputes.len() == 1 && disputes[0].key == 7),
            "{error}"
        );
        // 冲突 ⇒ 什么都不落地。
        assert!(rt.world().is_empty());
    }

    #[test]
    fn unknown_opcode_is_rejected() {
        let mut rt = default_runtime();
        let error = rt.dispatch(0xFF, b"").unwrap_err();
        assert!(matches!(error, RtError::InvalidInput(_)));
    }
}
