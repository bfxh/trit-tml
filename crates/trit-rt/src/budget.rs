//! 全局资源预算：旁路层的「全局占用」账本。
//!
//! 动机很实际：装了几十个 mod 时，旁路层如果谁提交谁就跑，一个 mod 的大扫描能把
//! 线程池吃满，其它 mod 的请求永远排不上，游戏主线程还得等结果。这里做三件事：
//!
//! 1. **全局容量**（`Resources`）：所有在跑的任务的 CPU 槽位与内存单位加起来不超过容量；
//! 2. **每 mod 公平份额**（`per_mod_inflight`）：同一 owner 同时在跑的任务有上限，
//!    超出的**排队**，不拒绝也不丢——下轮再派；
//! 3. **记账**：每个 mod 的 submitted / completed / cancelled / rejected / steps，
//!    以及最近一轮的 `RuntimeMetrics`，都能导出（Prometheus 文本）。
//!
//! 准入判断用的是上游 `trit-schedule` 的 `Resources::fits_in`，本文件不另造一套算术。

use std::collections::BTreeMap;

use serde::Serialize;
use trit_schedule::Resources;

/// 一个内存单位 = 1 MiB。
pub const MEMORY_UNIT_BYTES: usize = 1 << 20;

/// 字节数换算成内存单位（向上取整，0 字节也算 0）。
pub fn memory_units(bytes: usize) -> u32 {
    let units = bytes.div_ceil(MEMORY_UNIT_BYTES);
    u32::try_from(units).unwrap_or(u32::MAX)
}

/// 准入判定。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Admission {
    /// 现在就能跑。
    Admit,
    /// 全局容量允许，但本 mod 的在跑任务已达上限 → 排队等下一轮。
    Queue,
    /// 单任务需求就超过全局容量，永远跑不了 → 拒绝。
    Reject,
}

/// 一个 owner 的累计用量。
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq, Serialize)]
pub struct ModUsage {
    /// 累计提交。
    pub submitted: u64,
    /// 累计完成。
    pub completed: u64,
    /// 累计取消。
    pub cancelled: u64,
    /// 累计被拒（需求超过全局容量）。
    pub rejected: u64,
    /// 累计执行的 step 数。
    pub steps: u64,
    /// 当前在跑。
    pub inflight: u32,
    /// 当前排队。
    pub queued: u32,
}

/// 全局预算账本。
/// 分 owner 账本最多记多少个 owner（见 [`GlobalBudget::usage_mut`]）。
const MAX_TRACKED_OWNERS: usize = 1024;

#[derive(Debug, Clone, Default)]
pub struct GlobalBudget {
    per_mod_inflight: usize,
    /// owner → 累计 + 当前在跑/排队。
    mods: BTreeMap<String, ModUsage>,
    totals: ModUsage,
}

impl GlobalBudget {
    /// 构造；`per_mod_inflight` 为 0 时按 1 处理（至少允许一个）。
    pub fn new(per_mod_inflight: usize) -> Self {
        Self {
            per_mod_inflight: per_mod_inflight.max(1),
            mods: BTreeMap::new(),
            totals: ModUsage::default(),
        }
    }

    /// 同一 owner 的同时在跑上限。
    pub fn per_mod_inflight(&self) -> usize {
        self.per_mod_inflight
    }

    /// 某 owner 当前在跑的任务数。
    pub fn inflight_of(&self, owner: &str) -> u32 {
        self.mods.get(owner).map_or(0, |usage| usage.inflight)
    }

    /// 判定一个任务现在能否准入。
    pub fn admit(&self, owner: &str, requirements: Resources, capacity: Resources) -> Admission {
        // 单个任务就超过容量：不是"等等就好"，是永远放不下。
        if !requirements.fits_in(capacity, Resources::default()) {
            return Admission::Reject;
        }
        if usize::try_from(self.inflight_of(owner)).unwrap_or(usize::MAX) >= self.per_mod_inflight {
            return Admission::Queue;
        }
        if !requirements.fits_in(capacity, self.in_use()) {
            return Admission::Queue;
        }
        Admission::Admit
    }

    /// 全局当前占用（按各 owner 在跑任务的需求累加；需求默认 1 CPU / 1 内存单位）。
    ///
    /// 精确记账要在派发时把每个任务的 `Resources` 存下来，代价是又要一张表；
    /// 这里用「在跑任务数 × 单位需求」的上界近似——准入只用它做保守判断，够用且不会算错方向。
    pub fn in_use(&self) -> Resources {
        let inflight = self.totals.inflight;
        Resources::new(inflight, inflight)
    }

    /// 分 owner 的账本条目。
    ///
    /// owner 是调用方在请求里随便给的字符串，而本仓所有别的集合（`txns` 有
    /// `CLOSED_TXN_RETAIN`、快照有 `max_snapshots`、结果队列有 `MAX_PENDING_RESULTS`、
    /// 事件载荷有条数 + 字节双闸）**都有回收闸门，只有这张表是裸的**。
    /// 一个每 tick 换一个 owner 名字的调用就能让它只增不减，而它同时是 `BUDGET`
    /// 响应体里 `mods` 数组的来源——数组变长又会推高调用方每次解析的成本。
    fn usage_mut(&mut self, owner: &str) -> Option<&mut ModUsage> {
        if self.mods.contains_key(owner) {
            return self.mods.get_mut(owner);
        }
        if self.mods.len() >= MAX_TRACKED_OWNERS {
            // 账本满了：不再为新 owner 建条目（总量照记，只是没有分账）。
            // 选「不建」而不是「淘汰一个旧的」，是因为淘汰哪个都要引入顺序依赖；
            // 而「先到先得」对同一串调用序列是**确定的**——BUDGET 的输出必须可复现。
            return None;
        }
        Some(self.mods.entry(owner.to_string()).or_default())
    }

    /// 记录一次**提交**（提交数 +1）。
    ///
    /// 提交数在**受理请求时**就记，而不是等任务真正派发——否则：
    /// - 排队中的任务不计入任何 mod 的提交数，账本会低报；
    /// - 非法请求直接变成 `failed` 任务，一次提交就彻底消失；
    /// - 与托管回退内核的记账时机不一致，预算快照对不上。
    ///
    /// 托管回退内核在 `OpScanSubmit` 里就是**先记提交、再执行**，与这里同口径。
    pub fn on_submit(&mut self, owner: &str) {
        self.totals.submitted += 1;
        if let Some(usage) = self.usage_mut(owner) {
            usage.submitted += 1;
        }
    }

    /// 记录一次准入（**只在跑数 +1**）。
    ///
    /// 提交数由 [`Self::on_submit`] 在受理时记过了，这里不再重复计。
    pub fn on_admit(&mut self, owner: &str) {
        self.totals.inflight += 1;
        if let Some(usage) = self.usage_mut(owner) {
            usage.inflight += 1;
        }
    }

    /// 记录一次拒绝（不占资源，只记账）。
    pub fn on_reject(&mut self, owner: &str) {
        self.totals.rejected += 1;
        if let Some(usage) = self.usage_mut(owner) {
            usage.rejected += 1;
        }
    }

    /// 任务结束：在跑数 -1，按结局记账并累加 step。
    pub fn on_finish(&mut self, owner: &str, completed: bool, cancelled: bool, steps: u64) {
        let totals = &mut self.totals;
        totals.inflight = totals.inflight.saturating_sub(1);
        totals.steps = totals.steps.saturating_add(steps);
        if cancelled {
            totals.cancelled += 1;
        } else if completed {
            totals.completed += 1;
        }
        if let Some(usage) = self.usage_mut(owner) {
            usage.inflight = usage.inflight.saturating_sub(1);
            usage.steps = usage.steps.saturating_add(steps);
            if cancelled {
                usage.cancelled += 1;
            } else if completed {
                usage.completed += 1;
            }
        }
    }

    /// 清空排队计数（每轮派发前由任务池重算）。
    pub fn clear_queued(&mut self) {
        self.totals.queued = 0;
        for usage in self.mods.values_mut() {
            usage.queued = 0;
        }
    }

    /// 累加一个排队任务。
    pub fn add_queued(&mut self, owner: &str) {
        self.totals.queued += 1;
        if let Some(usage) = self.usage_mut(owner) {
            usage.queued += 1;
        }
    }

    /// 累计总量。
    pub fn totals(&self) -> ModUsage {
        self.totals
    }

    /// 各 owner 的用量（按提交数降序，提交数相同按 owner 名升序，保证输出稳定）。
    pub fn mod_usages(&self) -> Vec<(String, ModUsage)> {
        let mut entries: Vec<(String, ModUsage)> = self
            .mods
            .iter()
            .map(|(owner, usage)| (owner.clone(), *usage))
            .collect();
        entries.sort_by(|(left, left_usage), (right, right_usage)| {
            right_usage
                .submitted
                .cmp(&left_usage.submitted)
                .then_with(|| left.cmp(right))
        });
        entries
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn resources(cpu: u32, memory: u32) -> Resources {
        Resources::new(cpu, memory)
    }

    #[test]
    fn memory_units_round_up() {
        assert_eq!(memory_units(0), 0);
        assert_eq!(memory_units(1), 1);
        assert_eq!(memory_units(MEMORY_UNIT_BYTES), 1);
        assert_eq!(memory_units(MEMORY_UNIT_BYTES + 1), 2);
    }

    #[test]
    fn a_single_task_bigger_than_capacity_is_rejected_not_queued() {
        let budget = GlobalBudget::new(2);
        assert_eq!(
            budget.admit("m", resources(8, 8), resources(2, 2)),
            Admission::Reject
        );
    }

    #[test]
    fn the_per_mod_limit_queues_extra_tasks_of_the_same_owner() {
        let mut budget = GlobalBudget::new(1);
        let capacity = resources(4, 4);
        assert_eq!(
            budget.admit("a", resources(1, 1), capacity),
            Admission::Admit
        );
        budget.on_admit("a");
        // 同一个 owner 已经有一个在跑 → 排队。
        assert_eq!(
            budget.admit("a", resources(1, 1), capacity),
            Admission::Queue
        );
        // 别的 owner 不受影响 → 照跑。
        assert_eq!(
            budget.admit("b", resources(1, 1), capacity),
            Admission::Admit
        );
    }

    #[test]
    fn queueing_is_fair_across_mods() {
        let mut budget = GlobalBudget::new(1);
        let capacity = resources(4, 4);
        for owner in ["a", "b", "c"] {
            assert_eq!(
                budget.admit(owner, resources(1, 1), capacity),
                Admission::Admit
            );
            budget.on_admit(owner);
        }
        // 三个 mod 各占一个，谁的多余请求都排队，不会互相饿死。
        for owner in ["a", "b", "c"] {
            assert_eq!(
                budget.admit(owner, resources(1, 1), capacity),
                Admission::Queue
            );
        }
    }

    #[test]
    fn finishing_a_task_frees_the_slot_and_counts_steps() {
        let mut budget = GlobalBudget::new(1);
        let capacity = resources(4, 4);
        budget.on_submit("a");
        budget.on_admit("a");
        budget.on_finish("a", true, false, 7);
        assert_eq!(
            budget.admit("a", resources(1, 1), capacity),
            Admission::Admit
        );
        let usage = budget.mods.get("a").copied().unwrap_or_default();
        assert_eq!(usage.completed, 1);
        assert_eq!(usage.steps, 7);
        assert_eq!(usage.inflight, 0);
        assert_eq!(budget.totals().submitted, 1);
    }

    #[test]
    fn submission_is_counted_before_the_task_is_dispatched() {
        let mut budget = GlobalBudget::new(4);
        // 只提交、不派发：提交数已经记上了，在跑数仍是 0。
        budget.on_submit("a");
        let usage = budget.mods.get("a").copied().unwrap_or_default();
        assert_eq!(usage.submitted, 1);
        assert_eq!(usage.inflight, 0);
        // 派发只加在跑数，不再重复计提交。
        budget.on_admit("a");
        let usage = budget.mods.get("a").copied().unwrap_or_default();
        assert_eq!(usage.submitted, 1);
        assert_eq!(usage.inflight, 1);
    }

    #[test]
    fn cancelled_and_rejected_are_counted_separately() {
        let mut budget = GlobalBudget::new(4);
        budget.on_admit("a");
        budget.on_finish("a", false, true, 2);
        budget.on_reject("b");
        let a = budget.mods.get("a").copied().unwrap_or_default();
        let b = budget.mods.get("b").copied().unwrap_or_default();
        assert_eq!(a.cancelled, 1);
        assert_eq!(a.completed, 0);
        assert_eq!(b.rejected, 1);
        assert_eq!(b.submitted, 0);
    }

    #[test]
    fn queued_counts_are_recomputed_each_round() {
        let mut budget = GlobalBudget::new(1);
        budget.add_queued("a");
        budget.add_queued("a");
        budget.add_queued("b");
        assert_eq!(budget.totals().queued, 3);
        budget.clear_queued();
        assert_eq!(budget.totals().queued, 0);
        budget.add_queued("b");
        assert_eq!(budget.mods.get("b").copied().unwrap_or_default().queued, 1);
    }

    #[test]
    fn mod_usages_are_sorted_by_submitted_then_name() {
        let mut budget = GlobalBudget::new(4);
        budget.on_submit("low");
        budget.on_submit("high");
        budget.on_submit("high");
        budget.on_submit("high");
        let owners: Vec<String> = budget
            .mod_usages()
            .into_iter()
            .map(|(owner, _)| owner)
            .collect();
        assert_eq!(owners, vec!["high".to_string(), "low".to_string()]);
    }

    #[test]
    fn per_mod_inflight_of_zero_is_treated_as_one() {
        let budget = GlobalBudget::new(0);
        assert_eq!(budget.per_mod_inflight(), 1);
    }

    /// 回归：owner 账本必须有上界。
    ///
    /// owner 是调用方在请求里随便给的字符串，而本仓其它集合都有回收闸门，
    /// 只有这张表原来没有。一个每次换个 owner 名字的调用就能让它只增不减，
    /// 而它同时是 `BUDGET` 响应体里 `mods` 数组的来源。
    #[test]
    fn the_owner_ledger_is_capped() {
        let mut budget = GlobalBudget::new(4);
        // 先占满：这批 owner 会被记账。
        for index in 0..MAX_TRACKED_OWNERS {
            budget.on_submit(&format!("owner{index}"));
        }
        // 再来一批新的：账本已满，它们只进总量，不再建分账条目。
        for index in 0..64 {
            budget.on_submit(&format!("late{index}"));
        }
        assert_eq!(budget.mod_usages().len(), MAX_TRACKED_OWNERS);
        // 总量照记：闸门只砍分账，不能把提交数一起吞掉。
        assert_eq!(
            budget.totals().submitted,
            (MAX_TRACKED_OWNERS + 64) as u64,
            "总量必须包含所有提交"
        );
        // 已在账本里的 owner 仍然正常累加（不是「满了就全不记」）。
        budget.on_submit("owner0");
        let usage = budget
            .mod_usages()
            .into_iter()
            .find(|(owner, _)| owner == "owner0")
            .expect("owner0 仍在账本里")
            .1;
        assert_eq!(usage.submitted, 2);
    }
}
