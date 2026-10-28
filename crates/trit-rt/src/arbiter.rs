//! 跨模组写冲突仲裁：把多个模组对同一「车道」（世界键）的写入意图
//! 收敛成单一结果，或列出全部肇事方交给上层裁决。
//!
//! 三进制语义：`False = -1`（减少/移除）、`Unknown = 0`（不变/弃权）、
//! `True = +1`（增加/设置）。弃权是总线上的高阻——不驱动就不参与仲裁。
//! 严格模式直接复用 trit-core 的 [`resolve_drivers`]（多驱动总线解析，
//! 一次性收集全部冲突车道）；多数模式在驱动清单上做 -1/+1 计票。

use std::collections::BTreeMap;

use trit_core::tristate::{LaneConflict, TriState, TriStateDriver, resolve_drivers};
use trit_core::value::Ternary;

use crate::wire::Writer;

/// 仲裁模式。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ArbitrationMode {
    /// 任一车道出现互斥驱动即失败，并给出全部争议车道（硬件式严格）。
    Strict,
    /// 计票：同车道 -1/+1 多数胜出；平局视为争议（游戏式宽容）。
    Majority,
}

/// 一条写入意图。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Intent {
    /// 意图所属事务（0 = 自动提交）。
    pub txn_id: u64,
    /// 世界键（车道）。
    pub key: u32,
    /// 增量：`False(-1) / Unknown(0 弃权) / True(+1)`。
    pub delta: Ternary,
}

/// 仲裁通过的车道结果。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct ResolvedIntent {
    /// 世界键。
    pub key: u32,
    /// 收敛后的增量。
    pub delta: Ternary,
}

/// 一条争议车道：键 + 该车道上全部非弃权驱动 `(事务, 增量)`。
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct LaneDispute {
    /// 世界键。
    pub key: u32,
    /// 全部肇事方，按事务号升序。
    pub drivers: Vec<(u64, Ternary)>,
}

impl LaneDispute {
    /// 编码成诊断字节（冲突时随 `TT_ERR_CONFLICT` 一起返回给 C#）。
    pub fn encode(&self, w: &mut Writer) {
        w.u32(self.key);
        w.u32(self.drivers.len() as u32);
        for (txn_id, delta) in &self.drivers {
            w.u64(*txn_id);
            w.u8(crate::wire::trit_byte(*delta));
        }
    }
}

/// delta → 三态驱动；`Unknown`（弃权）不驱动。
fn driver_of(intent: &Intent) -> TriStateDriver {
    TriStateDriver::new(
        intent.delta != Ternary::Unknown,
        match intent.delta {
            Ternary::True => TriState::High,
            _ => TriState::Low,
        },
    )
}

/// 按车道分组，返回 `(排序后的键, 每车道驱动列表)`；只保留有驱动的车道。
fn group_by_key(intents: &[Intent]) -> Vec<(u32, Vec<&Intent>)> {
    let mut lanes: BTreeMap<u32, Vec<&Intent>> = BTreeMap::new();
    for intent in intents {
        if intent.delta != Ternary::Unknown {
            lanes.entry(intent.key).or_default().push(intent);
        }
    }
    lanes.into_iter().collect()
}

/// 仲裁一批意图。
///
/// - `Ok`：全部车道收敛，返回每个车道的最终增量（按键升序）；
/// - `Err`：存在争议车道，携带全部肇事方（按键升序，肇事方按事务号升序）。
///
/// 模式语义：
/// - [`ArbitrationMode::Strict`]：硬件式总线语义——同一车道出现**两个及以上**
///   驱动即争议（即使同向），交由上层裁决；
/// - [`ArbitrationMode::Majority`]：游戏式计票——同车道 -1/+1 多数胜出，平局争议。
pub fn arbitrate(
    intents: &[Intent],
    mode: ArbitrationMode,
) -> Result<Vec<ResolvedIntent>, Vec<LaneDispute>> {
    if let Some(resolved) = resolve_sorted_unique(intents) {
        return Ok(resolved);
    }
    let lanes = group_by_key(intents);
    match mode {
        ArbitrationMode::Strict => arbitrate_strict(&lanes),
        ArbitrationMode::Majority => arbitrate_majority(&lanes),
    }
}

/// 键已升序且唯一时的直通解析；不满足条件返回 `None`，交回分组路径。
///
/// 为什么要这条快路：`group_by_key` 对每条车道都要建一个 `Vec`，并对每个键做一次
/// `BTreeMap` 插入。每 tick 8000 条变化就是 8000 次堆分配 + 8000 次树查找，
/// 实测占掉整批提交 1.07ms 里的绝大部分（见 `examples/stress_bench.rs`）。
///
/// 为什么不破坏语义：单驱动车道在两种模式下都没有争议可言——
/// - `Strict` 只会把弃权（`Unknown`）滤掉；
/// - `Majority` 对弃权会按「平局」判争议，所以这里一遇到弃权就退回慢路，交给原逻辑。
fn resolve_sorted_unique(intents: &[Intent]) -> Option<Vec<ResolvedIntent>> {
    let mut resolved: Vec<ResolvedIntent> = Vec::with_capacity(intents.len());
    let mut previous: Option<u32> = None;
    for intent in intents {
        if intent.delta == Ternary::Unknown {
            return None;
        }
        if previous.is_some_and(|prev| intent.key <= prev) {
            return None;
        }
        previous = Some(intent.key);
        resolved.push(ResolvedIntent {
            key: intent.key,
            delta: intent.delta,
        });
    }
    Some(resolved)
}

/// 严格模式：走 trit-core 的多驱动总线解析。
fn arbitrate_strict(
    lanes: &[(u32, Vec<&Intent>)],
) -> Result<Vec<ResolvedIntent>, Vec<LaneDispute>> {
    let bus_input: Vec<Vec<TriStateDriver>> = lanes
        .iter()
        .map(|(_, intents)| intents.iter().map(|intent| driver_of(intent)).collect())
        .collect();
    match resolve_drivers(&bus_input) {
        Ok(bus) => {
            let resolved = lanes
                .iter()
                .zip(bus.values.iter())
                .filter_map(|((key, _), value)| {
                    trit_of(*value).map(|delta| ResolvedIntent { key: *key, delta })
                })
                .collect();
            Ok(resolved)
        }
        Err(conflicts) => {
            let disputes = conflicts
                .iter()
                .map(|conflict: &LaneConflict| {
                    let mut drivers: Vec<(u64, Ternary)> = conflict
                        .drivers
                        .iter()
                        .map(|(index, value)| {
                            (
                                lanes[conflict.lane].1[*index].txn_id,
                                trit_of(*value).unwrap_or(Ternary::Unknown),
                            )
                        })
                        .collect();
                    drivers.sort_unstable();
                    LaneDispute {
                        key: lanes[conflict.lane].0,
                        drivers,
                    }
                })
                .collect();
            Err(disputes)
        }
    }
}

/// 多数模式：-1/+1 计票，平局争议。
fn arbitrate_majority(
    lanes: &[(u32, Vec<&Intent>)],
) -> Result<Vec<ResolvedIntent>, Vec<LaneDispute>> {
    let mut resolved = Vec::with_capacity(lanes.len());
    let mut disputes = Vec::new();
    for (key, intents) in lanes {
        let negatives = intents
            .iter()
            .filter(|intent| intent.delta == Ternary::False)
            .count();
        let positives = intents
            .iter()
            .filter(|intent| intent.delta == Ternary::True)
            .count();
        match positives.cmp(&negatives) {
            std::cmp::Ordering::Greater => resolved.push(ResolvedIntent {
                key: *key,
                delta: Ternary::True,
            }),
            std::cmp::Ordering::Less => resolved.push(ResolvedIntent {
                key: *key,
                delta: Ternary::False,
            }),
            std::cmp::Ordering::Equal => {
                let mut drivers: Vec<(u64, Ternary)> = intents
                    .iter()
                    .map(|intent| (intent.txn_id, intent.delta))
                    .collect();
                drivers.sort_unstable();
                disputes.push(LaneDispute { key: *key, drivers });
            }
        }
    }
    if disputes.is_empty() {
        resolved.sort_by_key(|intent| intent.key);
        Ok(resolved)
    } else {
        disputes.sort_by_key(|dispute| dispute.key);
        Err(disputes)
    }
}

fn trit_of(value: TriState) -> Option<Ternary> {
    match value {
        TriState::Low => Some(Ternary::False),
        TriState::High => Some(Ternary::True),
        TriState::HighZ => None,
    }
}

/// 三进制累加：把增量叠加到车道当前值上， clamp 在 `[-1, +1]`。
pub fn apply_delta(current: Ternary, delta: Ternary) -> Ternary {
    let sum = current.index() as i8 + delta.index() as i8 - 1;
    Ternary::from_index((sum.clamp(0, 2)) as usize)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn intent(txn_id: u64, key: u32, delta: Ternary) -> Intent {
        Intent { txn_id, key, delta }
    }

    #[test]
    fn sorted_unique_lanes_match_the_grouping_path() {
        // 升序唯一键走快路；同样的车道乱序提交会走分组路径。两者结果必须一致。
        let sorted = [
            intent(1, 10, Ternary::True),
            intent(1, 20, Ternary::False),
            intent(1, 30, Ternary::True),
        ];
        let shuffled = [
            intent(1, 30, Ternary::True),
            intent(1, 10, Ternary::True),
            intent(1, 20, Ternary::False),
        ];
        for mode in [ArbitrationMode::Strict, ArbitrationMode::Majority] {
            assert_eq!(
                arbitrate(&sorted, mode).unwrap(),
                arbitrate(&shuffled, mode).unwrap()
            );
        }
    }

    #[test]
    fn single_driver_passes_through() {
        let resolved = arbitrate(&[intent(1, 10, Ternary::True)], ArbitrationMode::Strict).unwrap();
        assert_eq!(
            resolved,
            vec![ResolvedIntent {
                key: 10,
                delta: Ternary::True
            }]
        );
    }

    #[test]
    fn same_direction_drivers_merge_under_majority() {
        // 严格模式下同向多驱动仍是总线争议（硬件语义）。
        let intents = [
            intent(1, 10, Ternary::True),
            intent(2, 10, Ternary::True),
            intent(3, 11, Ternary::False),
        ];
        assert!(arbitrate(&intents, ArbitrationMode::Strict).is_err());
        // 多数模式下同向多驱动合并成一个结果。
        let resolved = arbitrate(&intents, ArbitrationMode::Majority).unwrap();
        assert_eq!(
            resolved,
            vec![
                ResolvedIntent {
                    key: 10,
                    delta: Ternary::True
                },
                ResolvedIntent {
                    key: 11,
                    delta: Ternary::False
                },
            ]
        );
    }

    #[test]
    fn abstain_never_drives() {
        let intents = [
            intent(1, 10, Ternary::Unknown),
            intent(2, 10, Ternary::Unknown),
        ];
        assert!(
            arbitrate(&intents, ArbitrationMode::Strict)
                .unwrap()
                .is_empty()
        );
    }

    #[test]
    fn strict_mode_reports_every_conflicting_lane_and_driver() {
        let intents = [
            intent(7, 10, Ternary::False),
            intent(3, 10, Ternary::True),
            intent(5, 12, Ternary::True),
            intent(9, 12, Ternary::False),
            intent(4, 13, Ternary::True),
        ];
        let disputes = arbitrate(&intents, ArbitrationMode::Strict).unwrap_err();
        assert_eq!(disputes.len(), 2);
        assert_eq!(disputes[0].key, 10);
        assert_eq!(
            disputes[0].drivers,
            vec![(3, Ternary::True), (7, Ternary::False)]
        );
        assert_eq!(disputes[1].key, 12);
        assert_eq!(
            disputes[1].drivers,
            vec![(5, Ternary::True), (9, Ternary::False)]
        );
    }

    #[test]
    fn majority_mode_resolves_by_vote_and_ties_dispute() {
        // 车道 10：两票 +1 一票 -1 ⇒ +1。
        let intents = [
            intent(1, 10, Ternary::True),
            intent(2, 10, Ternary::True),
            intent(3, 10, Ternary::False),
            // 车道 11：一票 -1 两票 +1 ⇒ -1。
            intent(4, 11, Ternary::False),
            intent(5, 11, Ternary::False),
            intent(6, 11, Ternary::True),
        ];
        let resolved = arbitrate(&intents, ArbitrationMode::Majority).unwrap();
        assert_eq!(
            resolved,
            vec![
                ResolvedIntent {
                    key: 10,
                    delta: Ternary::True
                },
                ResolvedIntent {
                    key: 11,
                    delta: Ternary::False
                },
            ]
        );
        // 平局 ⇒ 争议。
        let tie = [intent(1, 20, Ternary::True), intent(2, 20, Ternary::False)];
        let disputes = arbitrate(&tie, ArbitrationMode::Majority).unwrap_err();
        assert_eq!(disputes.len(), 1);
        assert_eq!(disputes[0].key, 20);
    }

    #[test]
    fn delta_accumulation_clamps_at_bounds() {
        assert_eq!(apply_delta(Ternary::Unknown, Ternary::True), Ternary::True);
        assert_eq!(apply_delta(Ternary::True, Ternary::True), Ternary::True);
        assert_eq!(apply_delta(Ternary::False, Ternary::False), Ternary::False);
        assert_eq!(apply_delta(Ternary::True, Ternary::False), Ternary::Unknown);
        assert_eq!(apply_delta(Ternary::False, Ternary::True), Ternary::Unknown);
    }
}
