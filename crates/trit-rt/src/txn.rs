//! 三进制事务日志：每次状态修改编码成一条 trit 事务，支持提交、回滚、
//! 重放与冲突消解。日志**只追加**：回滚到快照 = 还原世界投影，日志保留
//! 以便从快照序列号向前重放（redo）。容量有上限，写满即报错（上层回退）。

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
#[derive(Debug, Clone, Default)]
pub struct TxnLog {
    entries: Vec<LogEntry>,
    next_seq: u64,
}

impl TxnLog {
    pub fn new() -> Self {
        Self::default()
    }

    /// 追加一条已仲裁落地的记录。
    pub fn append(&mut self, txn_id: u64, tick: u64, resolved: &ResolvedIntent) -> LogEntry {
        self.next_seq += 1;
        let entry = LogEntry {
            txn_id,
            seq: self.next_seq,
            tick,
            key: resolved.key,
            delta: resolved.delta,
        };
        self.entries.push(entry);
        entry
    }

    /// 当前序列号（= 日志长度）。
    pub fn seq(&self) -> u64 {
        self.next_seq
    }

    /// 日志长度。
    pub fn len(&self) -> usize {
        self.entries.len()
    }

    pub fn is_empty(&self) -> bool {
        self.entries.is_empty()
    }

    /// 是否已达到容量上限。
    pub fn is_full(&self, max_entries: usize) -> bool {
        self.entries.len() >= max_entries
    }

    /// 从 `seq` 之后开始的重放迭代器（确定性重放用）。
    pub fn replay_from(&self, seq: u64) -> impl Iterator<Item = &LogEntry> {
        self.entries.iter().filter(move |entry| entry.seq > seq)
    }

    /// 全部记录（统计/导出用）。
    pub fn entries(&self) -> &[LogEntry] {
        &self.entries
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

impl TxnTable {
    pub fn new(max_open: usize) -> Self {
        Self {
            max_open,
            ..Self::default()
        }
    }

    /// 开事务。
    pub fn begin(&mut self, tick: u64) -> Result<u64, RtError> {
        if self.txns.len() >= self.max_open {
            return Err(RtError::Capacity(format!(
                "并发事务数达到上限 {}",
                self.max_open
            )));
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

    /// 未关闭事务数。
    pub fn open_count(&self) -> usize {
        self.txns.len()
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
