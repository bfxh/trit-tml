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
/// 这是**逻辑视图**，物理上日志按列存储（见 [`TxnLog`]），逐条还原成这个
/// 结构只是给诊断/测试一个方便的形状。
///
/// **刻意不存 `txn_id` 与 `tick`**：两者都是「这条记录是谁在什么时候写的」，
/// 而没有任何代码路径读它们——重放只需要 `(key, delta)`，回滚靠快照的完整
/// 世界投影，冲突诊断读的是**未落地**意图而不是日志。存它们要每条约
/// 12 字节：100 万条就是 12MB 常驻，换来的信息一次都没被读过。
/// 真需要按 tick 定位时，用相邻快照的 `(tick, log_seq)` 锚点夹出区间即可。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct LogEntry {
    /// 日志序列号（从 1 开始）。
    pub seq: u64,
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
/// 结构（默认上限 100 万条），而 `LogEntry` 里 u64/u32/u8 混排，逐条对齐后
/// 要 32 字节。拆成并列数组后每条只留**真正参与重放的两列** `key u32` +
/// `delta u8` = **5 字节**，1M 条上限常驻约 5MB。
///
/// 三样东西刻意不存：
/// - `seq`：日志只追加、截断永远切前缀，第 `i` 条序号恒为 `base_seq + 1 + i`；
/// - `txn_id`（8 字节/条）+ `tick`（4 字节/条）：没有代码路径读它们。重放只要
///   `(key, delta)`，回滚靠快照的完整世界投影，冲突诊断读的是未落地意图。
///   这两列曾是 1M 条 12MB 常驻，纯粹为「将来可能要用」付的账。
#[derive(Debug, Clone, Default)]
pub struct TxnLog {
    /// 世界键，每条一个。
    keys: Vec<u32>,
    /// 仲裁后的增量（1/0/2 → u8）。
    deltas: Vec<u8>,
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
    pub fn append(&mut self, resolved: &ResolvedIntent) -> LogEntry {
        self.next_seq += 1;
        self.keys.push(resolved.key);
        self.deltas.push(resolved.delta as u8);
        LogEntry {
            seq: self.next_seq,
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
            seq: self.seq_at(index),
            key: self.keys[index],
            delta: crate::wire::trit_from_byte(self.deltas[index]),
        })
    }

    /// 丢弃序号 `<= seq` 的记录（保留更晚的记录）。
    ///
    /// 记录按 seq 升序连续存储，前缀长度可以直接算出；两列必须一起切，
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
        let first = log.append(&ResolvedIntent {
            key: 1,
            delta: Ternary::True,
        });
        log.append(&ResolvedIntent {
            key: 2,
            delta: Ternary::False,
        });
        assert_eq!(log.len(), 2);
        assert_eq!(log.seq(), 2);
        assert!(!log.is_full(8));
        assert!(log.is_full(2));
        // 只追加：高序列号的记录永远可以重放。
        assert_eq!(log.replay_from(first.seq).count(), 1);
        assert_eq!(log.replay_from(0).count(), 2);
    }
}
