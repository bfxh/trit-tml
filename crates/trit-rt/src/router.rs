//! 事件路由：把「每帧全量遍历订阅表」换成「依赖图 + 脏标记 + 批处理」。
//!
//! - 订阅即建边：订阅者声明自己依赖哪些事件（`deps`），构成静态有向图；
//! - 发布即标脏：发布事件 E 后，E 的订阅者及其依赖传递闭包全部标脏；
//! - 派发即批处理：`step(tick)` 按 trit-schedule 算出的静态顺序过滤脏集合，
//!   一次输出本 tick 的全部动作意图，交给 C# 主线程执行。
//!
//! 静态顺序每次拓扑变更时用 [`trit_schedule::schedule`] 重算：它同时承担
//! 成环检测（拒绝坏订阅）和 makespan / 关键路径统计。

use std::collections::{BTreeMap, BTreeSet, VecDeque};

use trit_core::value::Ternary;
use trit_schedule::{Dependency, Schedule, Task};

use crate::error::RtError;

/// 一个订阅者。
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Subscriber {
    /// 订阅 ID。
    pub id: u64,
    /// 模组 ID（调用方命名空间）。
    pub mod_id: u32,
    /// 订阅的事件 ID。
    pub event_id: u32,
    /// 优先级；越大越先派发。
    pub priority: i32,
    /// 依赖的事件 ID 列表（必须排在它们的订阅者之后）。
    pub deps: Vec<u32>,
}

/// 静态调度指标（来自 trit-schedule）。
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct ScheduleMetrics {
    /// makespan（所有订阅者 duration = 1 时等于派发轮数）。
    pub makespan: u64,
    /// 关键路径长度。
    pub critical_path: u64,
    /// 被延迟的订阅者数（本路由器不产生 Unknown 依赖，恒为 0，保留字段以便观测）。
    pub deferred: usize,
}

/// 事件路由器。
#[derive(Debug, Clone)]
pub struct EventRouter {
    subscribers: BTreeMap<u64, Subscriber>,
    by_event: BTreeMap<u32, BTreeSet<u64>>,
    /// 静态派发顺序（订阅 ID 序列）。
    order: Vec<u64>,
    /// 本 tick 脏集合。
    dirty: BTreeSet<u64>,
    next_id: u64,
    max_subscribers: usize,
    metrics: ScheduleMetrics,
}

impl EventRouter {
    pub fn new(max_subscribers: usize) -> Self {
        Self {
            subscribers: BTreeMap::new(),
            by_event: BTreeMap::new(),
            order: Vec::new(),
            dirty: BTreeSet::new(),
            next_id: 1,
            max_subscribers,
            metrics: ScheduleMetrics::default(),
        }
    }

    /// 订阅者数量。
    pub fn len(&self) -> usize {
        self.subscribers.len()
    }

    pub fn is_empty(&self) -> bool {
        self.subscribers.is_empty()
    }

    /// 当前脏集合大小。
    pub fn dirty_count(&self) -> usize {
        self.dirty.len()
    }

    pub fn metrics(&self) -> ScheduleMetrics {
        self.metrics
    }

    /// 订阅；自依赖与依赖成环都会拒绝（坏订阅不留痕迹）。
    pub fn subscribe(
        &mut self,
        mod_id: u32,
        event_id: u32,
        priority: i32,
        deps: Vec<u32>,
    ) -> Result<u64, RtError> {
        if self.subscribers.len() >= self.max_subscribers {
            return Err(RtError::Capacity(format!(
                "订阅数达到上限 {}",
                self.max_subscribers
            )));
        }
        if deps.contains(&event_id) {
            return Err(RtError::InvalidInput(format!(
                "订阅者不能依赖自己订阅的事件 {event_id}"
            )));
        }
        let id = self.next_id;
        let subscriber = Subscriber {
            id,
            mod_id,
            event_id,
            priority,
            deps,
        };
        self.subscribers.insert(id, subscriber);
        self.by_event.entry(event_id).or_default().insert(id);
        if let Err(error) = self.rebuild_order() {
            // 回滚插入，保持「拒绝即无副作用」。
            self.subscribers.remove(&id);
            if let Some(subs) = self.by_event.get_mut(&event_id) {
                subs.remove(&id);
                if subs.is_empty() {
                    self.by_event.remove(&event_id);
                }
            }
            return Err(error);
        }
        self.next_id += 1;
        Ok(id)
    }

    /// 退订。
    pub fn unsubscribe(&mut self, sub_id: u64) -> Result<(), RtError> {
        let subscriber = self
            .subscribers
            .remove(&sub_id)
            .ok_or(RtError::SubNotFound(sub_id))?;
        if let Some(subs) = self.by_event.get_mut(&subscriber.event_id) {
            subs.remove(&sub_id);
            if subs.is_empty() {
                self.by_event.remove(&subscriber.event_id);
            }
        }
        self.dirty.remove(&sub_id);
        self.rebuild_order()?;
        Ok(())
    }

    /// 发布事件：把订阅者及其依赖闭包标脏，返回新标脏的数量。
    pub fn publish(&mut self, event_id: u32) -> usize {
        let Some(seeds) = self.by_event.get(&event_id) else {
            return 0;
        };
        if seeds.is_empty() {
            return 0;
        }
        // 依赖闭包：P -> S 表示 S 依赖 P 订阅的事件。
        let dependents = self.dependents_map();
        let mut queue: VecDeque<u64> = seeds.iter().copied().collect();
        let mut newly_dirty = 0usize;
        while let Some(node) = queue.pop_front() {
            if self.dirty.insert(node) {
                newly_dirty += 1;
            }
            if let Some(children) = dependents.get(&node) {
                for child in children {
                    if !self.dirty.contains(child) {
                        queue.push_back(*child);
                    }
                }
            }
        }
        newly_dirty
    }

    /// 取出本 tick 的派发列表（静态顺序过滤脏集合），并清空脏集合。
    pub fn take_dispatch(&mut self) -> Vec<u64> {
        if self.dirty.is_empty() {
            return Vec::new();
        }
        let dispatch: Vec<u64> = self
            .order
            .iter()
            .copied()
            .filter(|id| self.dirty.contains(id))
            .collect();
        self.dirty.clear();
        dispatch
    }

    /// 取一个订阅者。
    pub fn get(&self, sub_id: u64) -> Option<&Subscriber> {
        self.subscribers.get(&sub_id)
    }

    /// `P -> {S}`：S 依赖 P 订阅的某个事件。
    fn dependents_map(&self) -> BTreeMap<u64, BTreeSet<u64>> {
        let mut map: BTreeMap<u64, BTreeSet<u64>> = BTreeMap::new();
        for subscriber in self.subscribers.values() {
            for event_id in &subscriber.deps {
                if let Some(predecessors) = self.by_event.get(event_id) {
                    for predecessor in predecessors {
                        if *predecessor != subscriber.id {
                            map.entry(*predecessor).or_default().insert(subscriber.id);
                        }
                    }
                }
            }
        }
        map
    }

    /// 用 trit-schedule 重算静态顺序；成环则返回错误。
    fn rebuild_order(&mut self) -> Result<(), RtError> {
        let tasks: Vec<Task> = self
            .subscribers
            .values()
            .map(|subscriber| {
                // 依赖边去重：同一事件被多个前置订阅时只保留一条边，
                // 否则 trit-schedule 的入度会重复计数。
                let mut predecessors: BTreeSet<u64> = BTreeSet::new();
                for event_id in &subscriber.deps {
                    if let Some(subs) = self.by_event.get(event_id) {
                        predecessors.extend(subs.iter().copied());
                    }
                }
                predecessors.remove(&subscriber.id);
                Task {
                    id: format!("{:016x}", subscriber.id),
                    duration: 1,
                    priority: subscriber.priority,
                    deps: predecessors
                        .into_iter()
                        .map(|predecessor| Dependency {
                            task: format!("{:016x}", predecessor),
                            required: Ternary::True,
                        })
                        .collect(),
                }
            })
            .collect();
        let schedule: Schedule = trit_schedule::schedule(&tasks, 1)
            .map_err(|error| RtError::InvalidInput(format!("订阅依赖图非法：{error}")))?;
        self.order = schedule
            .scheduled
            .iter()
            .filter_map(|task| u64::from_str_radix(&task.id, 16).ok())
            .collect();
        self.metrics = ScheduleMetrics {
            makespan: schedule.makespan,
            critical_path: schedule.critical_path,
            deferred: schedule.deferred.len(),
        };
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn publish_marks_dependency_closure_dirty() {
        let mut router = EventRouter::new(16);
        let a = router.subscribe(1, 100, 0, vec![]).unwrap();
        let b = router.subscribe(2, 101, 0, vec![100]).unwrap();
        let c = router.subscribe(3, 102, 0, vec![101]).unwrap();
        let unrelated = router.subscribe(4, 200, 0, vec![]).unwrap();

        let dirty = router.publish(100);
        assert_eq!(dirty, 3, "发布 100 应标脏 a/b/c 的传递闭包");
        assert_eq!(router.dirty_count(), 3);

        let dispatch = router.take_dispatch();
        assert_eq!(dispatch.len(), 3);
        // 依赖序：a 在 b 前，b 在 c 前。
        let pos = |id: u64| dispatch.iter().position(|x| *x == id).unwrap();
        assert!(pos(a) < pos(b));
        assert!(pos(b) < pos(c));
        assert!(!dispatch.contains(&unrelated));
        assert_eq!(router.dirty_count(), 0);
    }

    #[test]
    fn unknown_event_is_a_no_op() {
        let mut router = EventRouter::new(16);
        router.subscribe(1, 100, 0, vec![]).unwrap();
        assert_eq!(router.publish(999), 0);
        assert!(router.take_dispatch().is_empty());
    }

    #[test]
    fn cycles_are_rejected_at_subscribe_time() {
        let mut router = EventRouter::new(16);
        let a = router.subscribe(1, 100, 0, vec![101]).unwrap();
        let error = router.subscribe(2, 101, 0, vec![100]).unwrap_err();
        assert!(matches!(error, RtError::InvalidInput(_)), "{error}");
        // 坏订阅不应留下痕迹：a 仍然在，顺序仍可用。
        router.unsubscribe(a).unwrap();
        router.subscribe(2, 101, 0, vec![100]).unwrap();
        assert_eq!(router.len(), 1);
    }

    #[test]
    fn priority_orders_independent_subscribers() {
        let mut router = EventRouter::new(16);
        let low = router.subscribe(1, 100, -5, vec![]).unwrap();
        let high = router.subscribe(2, 101, 5, vec![]).unwrap();
        router.publish(100);
        router.publish(101);
        let dispatch = router.take_dispatch();
        let pos = |id: u64| dispatch.iter().position(|x| *x == id).unwrap();
        assert!(pos(high) < pos(low), "高优先级先派发：{dispatch:?}");
    }

    #[test]
    fn unsubscribe_removes_from_dispatch() {
        let mut router = EventRouter::new(16);
        let a = router.subscribe(1, 100, 0, vec![]).unwrap();
        router.publish(100);
        router.unsubscribe(a).unwrap();
        assert!(router.take_dispatch().is_empty());
    }

    #[test]
    fn subscribe_enforces_capacity() {
        let mut router = EventRouter::new(2);
        router.subscribe(1, 100, 0, vec![]).unwrap();
        router.subscribe(2, 101, 0, vec![]).unwrap();
        assert!(matches!(
            router.subscribe(3, 102, 0, vec![]),
            Err(RtError::Capacity(_))
        ));
    }

    #[test]
    fn subscribe_rejects_self_dependency() {
        // 订阅者不能依赖自己订阅的事件。
        let mut router = EventRouter::new(8);
        assert!(matches!(
            router.subscribe(1, 100, 0, vec![100]),
            Err(RtError::InvalidInput(_))
        ));
    }

    #[test]
    fn unsubscribe_unknown_is_sub_not_found() {
        let mut router = EventRouter::new(8);
        assert!(matches!(
            router.unsubscribe(404),
            Err(RtError::SubNotFound(404))
        ));
    }

    #[test]
    fn publish_is_idempotent_for_dirty_count() {
        let mut router = EventRouter::new(8);
        router.subscribe(1, 100, 0, vec![]).unwrap();
        assert_eq!(router.publish(100), 1);
        // 重复发布同一事件，不重复计入脏集合。
        assert_eq!(router.publish(100), 0);
        assert_eq!(router.dirty_count(), 1);
    }

    #[test]
    fn take_dispatch_clears_dirty_set() {
        let mut router = EventRouter::new(8);
        router.subscribe(1, 100, 0, vec![]).unwrap();
        router.publish(100);
        assert!(router.dirty_count() > 0);
        let dispatch = router.take_dispatch();
        assert_eq!(dispatch.len(), 1);
        assert_eq!(router.dirty_count(), 0);
    }

    #[test]
    fn empty_router_publish_is_noop() {
        let mut router = EventRouter::new(8);
        assert_eq!(router.publish(1), 0);
        assert!(router.take_dispatch().is_empty());
    }

    #[test]
    fn metrics_populated_after_subscribe() {
        let mut router = EventRouter::new(8);
        router.subscribe(1, 100, 0, vec![]).unwrap();
        let m = router.metrics();
        // 单订阅无依赖：makespan / critical_path 至少反映 1 个任务。
        assert!(m.makespan >= 1);
        assert!(m.critical_path >= 1);
    }

    #[test]
    fn publish_does_not_dirty_predecessors() {
        // b 依赖 a：发布 b 的事件只标脏 b，不标脏 a（方向正确）。
        let mut router = EventRouter::new(8);
        let a = router.subscribe(1, 100, 0, vec![]).unwrap();
        let b = router.subscribe(2, 101, 0, vec![100]).unwrap();
        router.publish(101);
        let dispatch = router.take_dispatch();
        assert!(dispatch.contains(&b));
        assert!(!dispatch.contains(&a));
    }
}
