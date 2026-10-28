//! 三进制事务日志：每次状态修改编码成一条 trit 事务，支持提交、回滚、
//! 重放与冲突消解。
//!
//! 日志**追加为主**，但通过「快照截断」保持有界：每次落盘快照后，序号
//! 不高于「最旧保留快照的 log_seq」的记录会被丢弃——因为回滚到该快照
//! 是直接还原其完整世界投影，而 `replay_from(seq)` 只需要某快照 *之后*
//! 的记录。这样既保留重放/回滚语义，又杜绝了只追加导致的无界增长。

use trit_core::value::Ternary;

use crate::arbiter::{Intent, ResolvedIntent};
use crate::error::RtError;

/// 事务状态。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum TxnState {
    /// 打开中，可继续提交意图。
    Open,
    /// 已提交。
    Committed,
    /// 已回滚。
    RolledBack,
}

/// 一条已落地的事务记录（日志项）。
///
/// 这是**逻辑视图**，物理上日志按列存储（见 [`TxnLog`]）：每条 `LogEntry` 若
/// 独立成结构体会按 8 字节对齐占 32 字节，而这个结构里有 u64、u32、u8 混排，
/// 浪费近一半空间。1M 条日志就是 32MB vs 17MB 的差别——对游戏进程是要命的比例。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct LogEntry {
    /// 事务 ID。
    pub txn_id: u64,
    /// 日志序列号（从 1 开始）。
    pub seq: u64,
    /// 落地时的 tick。
    pub tick: u64,
    /// 世界键。
    pub key: u32,
    /// 仲裁后的增量。
    pub delta: Ternary,
}

/// 一个未关闭的事务。
#[derive(Debug, Clone)]
pub struct Txn {
    /// 事务 ID。
    pub id: u64,
    /// 开启时的 tick。
    pub opened_tick: u64,
    /// 状态。
    pub state: TxnState,
    /// 意图清单（未落地）。
    pub intents: Vec<Intent>,
}

impl Txn {
    fn new(id: u64, tick: u64) -> Self {
        Self {
            id,
            opened_tick: tick,
            state: TxnState::Open,
            intents: Vec::new(),
        }
    }
}

/// 事务日志。
///
/// **按列（SoA）存储**，不是 `Vec<LogEntry>`：日志条目是本进程里最大的常驻
/// 结构（默认上限 100 万条），而 `LogEntry` 混排 u64/u32/u8，逐条对齐后要
/// 32 字节。拆成 4 个并列数组后是 4+1+4+8 = 17 字节/条，同样的上限直接省下
/// 15MB（实测 1M 条常驻从 ~32MB 降到 ~17MB）。
///
/// `seq` 不单独存：日志只追加、且截断永远切前缀，所以第 `i` 条的序号恒等于
/// `base_seq + 1 + i`，用算术即可还原，没必要每条再存 8 字节。
#[derive(Debug, Clone, Default)]
pub struct TxnLog {
    /// 世界键，每条一个。
    keys: Vec<u32>,
    /// 仲裁后的增量（1/0/2 → u8）。
    deltas: Vec<u8>,
    /// 落地时的 tick。u32 足够：42 亿 tick ≈ 2.2 年连续游戏。
    ticks: Vec<u32>,
    /// 事务 ID（u64，事务表 id 也是 u64，不做窄化以免长期运行后静默错配）。
    txn_ids: Vec<u64>,
    /// 已被截断掉的最后一条序号；`seq_at(0) = base_seq + 1`。
    base_seq: u64,
    /// 全局单调序号（= 最后一条的 seq）。
    next_seq: u64,
}

impl TxnLog {
    pub fn new() -> Self {
        Self::default()
    }

    /// 追加一条已仲裁落地的记录。
    pub fn append(&mut self, txn_id: u64, tick: u64, resolved: &ResolvedIntent) -> LogEntry {
        self.next_seq += 1;
        // tick 窄化到 u32：超出范围的 tick 在游戏里不可能出现，但静默截断会
        // 让回放对不上——用饱和转换把异常值钉在 u32::MAX，至少不会回绕成 0。
        let tick32 = u32::try_from(tick).unwrap_or(u32::MAX);
        self.keys.push(resolved.key);
        self.deltas.push(resolved.delta as u8);
        self.ticks.push(tick32);
        self.txn_ids.push(txn_id);
        LogEntry {
            txn_id,
            seq: self.next_seq,
            tick,
            key: resolved.key,
            delta: resolved.delta,
        }
    }

    /// 当前序列号（全局单调，截断不会让它回退）。
    pub fn seq(&self) -> u64 {
        self.next_seq
    }

    /// 日志长度。
    pub fn len(&self) -> usize {
        self.keys.len()
    }

    pub fn is_empty(&self) -> bool {
        self.keys.is_empty()
    }

    /// 是否已达到容量上限。
    pub fn is_full(&self, max_entries: usize) -> bool {
        self.len() >= max_entries
    }

    /// 第 `index` 条的全局序号。
    fn seq_at(&self, index: usize) -> u64 {
        self.base_seq + 1 + index as u64
    }

    /// 序号严格大于 `seq` 的第一条的下标。
    ///
    /// 因为序号连续，直接算即可——不必二分查找，更不必像旧实现那样
    /// `filter` 扫全表。
    pub fn start_after(&self, seq: u64) -> usize {
        if seq <= self.base_seq {
            return 0;
        }
        ((seq - self.base_seq) as usize).min(self.len())
    }

    /// 第 `index` 条的键。
    pub fn key_at(&self, index: usize) -> u32 {
        self.keys[index]
    }

    /// 第 `index` 条的增量。
    pub fn delta_at(&self, index: usize) -> Ternary {
        crate::wire::trit_from_byte(self.deltas[index])
    }

    /// 从 `seq` 之后开始的重放条目（按值产出逻辑视图，仅供诊断/测试）。
    pub fn replay_from(&self, seq: u64) -> impl Iterator<Item = LogEntry> + '_ {
        let start = self.start_after(seq);
        (start..self.len()).map(move |index| LogEntry {
            txn_id: self.txn_ids[index],
            seq: self.seq_at(index),
            tick: self.ticks[index] as u64,
            key: self.keys[index],
            delta: crate::wire::trit_from_byte(self.deltas[index]),
        })
    }

    /// 丢弃序号 `<= seq` 的记录（保留更晚的记录）。
    ///
    /// 记录按 seq 升序连续存储，前缀长度可以直接算出；四列必须一起切，
    /// 否则下标会错位。`next_seq` 保持单调，不影响 `seq()` 与 `replay_from`。
    /// 调用方须保证 `seq` 不高于任一仍需回滚/重放的快照锚点。
    pub fn truncate_before(&mut self, seq: u64) {
        let cut = if seq <= self.base_seq {
            0
        } else {
            ((seq - self.base_seq) as usize).min(self.len())
        };
        if cut == 0 {
            return;
        }
        self.keys.drain(0..cut);
        self.deltas.drain(0..cut);
        self.ticks.drain(0..cut);
        self.txn_ids.drain(0..cut);
        self.base_seq += cut as u64;
    }
}

/// 事务表：未关闭事务 + 状态机校验。
#[derive(Debug, Clone, Default)]
pub struct TxnTable {
    txns: std::collections::BTreeMap<u64, Txn>,
    next_id: u64,
    max_open: usize,
    committed: u64,
    rolled_back: u64,
}

/// 已关闭事务的保留条数。
///
/// 保留它们只是为了「重复提交 / 重复回滚」能报出 `TxnState` 而不是
/// `TxnNotFound`（诊断友好）。但**绝不能全留**：模组每 tick 开一个事务，
/// 全留会让事务表随 tick 无界增长，而且 `max_open` 的计数被历史事务占满——
/// 实测跑到 4096 tick 之后 `begin` 永久失败，整个旁路层就此停摆。
const CLOSED_TXN_RETAIN: usize = 1024;

impl TxnTable {
    pub fn new(max_open: usize) -> Self {
        Self {
            max_open,
            ..Self::default()
        }
    }

    /// 开事务。
    pub fn begin(&mut self, tick: u64) -> Result<u64, RtError> {
        // 上限只约束「真正打开中」的事务；已关闭的不算并发。
        let open = self
            .txns
            .values()
            .filter(|txn| txn.state == TxnState::Open)
            .count();
        if open >= self.max_open {
            return Err(RtError::Capacity(format!(
                "并发事务数达到上限 {}",
                self.max_open
            )));
        }
        // 已关闭事务留最近 `CLOSED_TXN_RETAIN` 条即可。键是自增 id，
        // BTreeMap 序即开启顺序，取前若干个就是最旧的。
        let closed = self.txns.len() - open;
        if closed > CLOSED_TXN_RETAIN {
            let stale: Vec<u64> = self
                .txns
                .iter()
                .filter(|(_, txn)| txn.state != TxnState::Open)
                .map(|(id, _)| *id)
                .take(closed - CLOSED_TXN_RETAIN)
                .collect();
            for id in stale {
                self.txns.remove(&id);
            }
        }
        self.next_id += 1;
        let id = self.next_id;
        self.txns.insert(id, Txn::new(id, tick));
        Ok(id)
    }

    /// 取一个打开中的事务；不存在或已关闭都报错。
    pub fn open(&self, txn_id: u64) -> Result<&Txn, RtError> {
        let txn = self.txns.get(&txn_id).ok_or(RtError::TxnNotFound(txn_id))?;
        if txn.state != TxnState::Open {
            return Err(RtError::TxnState(format!(
                "事务 {txn_id} 已 {}",
                if txn.state == TxnState::Committed {
                    "提交"
                } else {
                    "回滚"
                }
            )));
        }
        Ok(txn)
    }

    /// 追加意图。
    pub fn push_intent(&mut self, txn_id: u64, intent: Intent) -> Result<(), RtError> {
        let txn = self
            .txns
            .get_mut(&txn_id)
            .ok_or(RtError::TxnNotFound(txn_id))?;
        if txn.state != TxnState::Open {
            return Err(RtError::TxnState(format!("事务 {txn_id} 已关闭")));
        }
        txn.intents.push(intent);
        Ok(())
    }

    /// 复制一个未关闭事务的意图（提交前预仲裁用；失败时事务保持打开）。
    pub fn clone_intents(&self, txn_id: u64) -> Result<Vec<Intent>, RtError> {
        let txn = self.open(txn_id)?;
        Ok(txn.intents.clone())
    }

    /// 标记提交并取出意图。
    pub fn finish_commit(&mut self, txn_id: u64) -> Result<Vec<Intent>, RtError> {
        let txn = self
            .txns
            .get_mut(&txn_id)
            .ok_or(RtError::TxnNotFound(txn_id))?;
        if txn.state != TxnState::Open {
            return Err(RtError::TxnState(format!("事务 {txn_id} 已关闭")));
        }
        txn.state = TxnState::Committed;
        self.committed += 1;
        Ok(std::mem::take(&mut txn.intents))
    }

    /// 标记回滚。
    pub fn finish_rollback(&mut self, txn_id: u64) -> Result<(), RtError> {
        let txn = self
            .txns
            .get_mut(&txn_id)
            .ok_or(RtError::TxnNotFound(txn_id))?;
        if txn.state != TxnState::Open {
            return Err(RtError::TxnState(format!("事务 {txn_id} 已关闭")));
        }
        txn.state = TxnState::RolledBack;
        self.rolled_back += 1;
        Ok(())
    }

    /// 未关闭事务数（已关闭的按 `CLOSED_TXN_RETAIN` 保留在表里，不计入）。
    pub fn open_count(&self) -> usize {
        self.txns
            .values()
            .filter(|txn| txn.state == TxnState::Open)
            .count()
    }

    /// 除 `exclude` 外所有未关闭事务的待落地意图（提交时做跨事务仲裁）。
    pub fn pending_intents(&self, exclude: u64) -> Vec<Intent> {
        self.txns
            .values()
            .filter(|txn| txn.id != exclude && txn.state == TxnState::Open)
            .flat_map(|txn| txn.intents.iter().copied())
            .collect()
    }

    /// 累计提交数。
    pub fn committed(&self) -> u64 {
        self.committed
    }

    /// 累计回滚数。
    pub fn rolled_back(&self) -> u64 {
        self.rolled_back
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use trit_core::value::Ternary;

    fn intent(txn_id: u64, key: u32, delta: Ternary) -> Intent {
        Intent { txn_id, key, delta }
    }

    #[test]
    fn double_commit_is_rejected() {
        let mut table = TxnTable::new(8);
        let id = table.begin(1).unwrap();
        table.push_intent(id, intent(id, 1, Ternary::True)).unwrap();
        assert_eq!(table.finish_commit(id).unwrap().len(), 1);
        assert!(matches!(table.finish_commit(id), Err(RtError::TxnState(_))));
        assert!(matches!(
            table.finish_rollback(id),
            Err(RtError::TxnState(_))
        ));
    }

    #[test]
    fn rollback_discards_intents() {
        let mut table = TxnTable::new(8);
        let id = table.begin(1).unwrap();
        table
            .push_intent(id, intent(id, 1, Ternary::False))
            .unwrap();
        table.finish_rollback(id).unwrap();
        assert!(table.open(id).is_err());
    }

    #[test]
    fn log_is_append_only_and_replays_forward() {
        let mut log = TxnLog::new();
        let first = log.append(
            1,
            0,
            &ResolvedIntent {
                key: 1,
                delta: Ternary::True,
            },
        );
        log.append(
            1,
            0,
            &ResolvedIntent {
                key: 2,
                delta: Ternary::False,
            },
        );
        assert_eq!(log.len(), 2);
        assert_eq!(log.seq(), 2);
        assert!(!log.is_full(8));
        assert!(log.is_full(2));
        // 只追加：高序列号的记录永远可以重放。
        assert_eq!(log.replay_from(first.seq).count(), 1);
        assert_eq!(log.replay_from(0).count(), 2);
    }
}
