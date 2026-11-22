//! 世界投影的紧凑存储。
//!
//! 键布局与 C# `LaneMap` 对齐：`[31..28] kind | [27..12] slot | [11..0] field`。
//! 生产字段只有 1..=9、种类只有 1..=3，因此按「种类 × 字段」切成 27 个平面，
//! 每个平面按 slot 分页，每个 trit 占 2 bit：
//!
//! ```text
//! fast[kind-1][field-1].pages[slot / 128] -> [u64; 4]  // 128 slot × 2 bit
//! ```
//!
//! `0b11` 是缺失哨兵（`Ternary` 只占 0/1/2），所以不需要额外的存在位图。
//! 不满足快速通道的键（`kind ∉ 1..=3`、`field ∉ 1..=9`）落到 `overflow`，
//! 主要服务于测试与异常输入——生产流量不会走那里。
//!
//! 与 `BTreeMap<u32, Ternary>` 相比，1 万车道的常驻内存从约 100 KB 降到约 4 KB。

use std::collections::BTreeMap;
use std::sync::atomic::{AtomicU64, Ordering};

use trit_core::value::Ternary;

const KIND_SHIFT: u32 = 28;
const SLOT_SHIFT: u32 = 12;
const SLOT_MASK: u32 = 0xFFFF;
const FIELD_MASK: u32 = 0xFFF;

const FAST_KINDS: usize = 3;
const FAST_FIELDS: usize = 9;

const SLOTS_PER_PAGE: usize = 128;
const WORDS_PER_PAGE: usize = 4; // 128 slot × 2 bit = 256 bit = 4 × u64
const BITS_PER_WORD: usize = 32;

const ABSENT: u64 = 0b11;
const ABSENT_WORD: u64 = u64::MAX;

/// 结构版本号：仅在「新增车道 / 清空 / 重建」时变化，fork 用它判断是否需要重建键集。
static NEXT_SERIAL: AtomicU64 = AtomicU64::new(1);

fn next_serial() -> u64 {
    NEXT_SERIAL.fetch_add(1, Ordering::Relaxed)
}

/// 拆出快速通道坐标 `(kind_idx, slot, field_idx)`。
#[inline]
fn fast_parts(key: u32) -> Option<(usize, usize, usize)> {
    let kind = key >> KIND_SHIFT;
    if kind == 0 || kind > FAST_KINDS as u32 {
        return None;
    }
    let field = key & FIELD_MASK;
    if field == 0 || field > FAST_FIELDS as u32 {
        return None;
    }
    let slot = ((key >> SLOT_SHIFT) & SLOT_MASK) as usize;
    Some(((kind - 1) as usize, slot, (field - 1) as usize))
}

#[derive(Debug, Default, Clone)]
struct Plane {
    pages: Vec<Option<Box<[u64; WORDS_PER_PAGE]>>>,
}

impl Plane {
    #[inline]
    fn get(&self, slot: usize) -> Option<Ternary> {
        let page = self.pages.get(slot / SLOTS_PER_PAGE)?.as_ref()?;
        let within = slot % SLOTS_PER_PAGE;
        let code = (page[within / BITS_PER_WORD] >> ((within % BITS_PER_WORD) * 2)) & 0b11;
        if code == ABSENT {
            None
        } else {
            Some(Ternary::from_index(code as usize))
        }
    }

    /// 写入一个值；返回是否新建了车道（用于 `len` 记账）。
    fn set(&mut self, slot: usize, value: Ternary) -> bool {
        let page_idx = slot / SLOTS_PER_PAGE;
        if self.pages.len() <= page_idx {
            self.pages.resize_with(page_idx + 1, || None);
        }
        let page =
            self.pages[page_idx].get_or_insert_with(|| Box::new([ABSENT_WORD; WORDS_PER_PAGE]));
        let within = slot % SLOTS_PER_PAGE;
        let word = &mut page[within / BITS_PER_WORD];
        let shift = (within % BITS_PER_WORD) * 2;
        let old = (*word >> shift) & 0b11;
        let code = value.index() as u64;
        if old == code {
            return false;
        }
        *word = (*word & !(0b11 << shift)) | (code << shift);
        old == ABSENT
    }

    fn for_each(&self, mut f: impl FnMut(usize, Ternary)) {
        for (page_idx, page) in self.pages.iter().enumerate() {
            let Some(page) = page else { continue };
            for (word_idx, word) in page.iter().enumerate() {
                if *word == ABSENT_WORD {
                    continue;
                }
                for bit in 0..BITS_PER_WORD {
                    let code = (*word >> (bit * 2)) & 0b11;
                    if code == ABSENT {
                        continue;
                    }
                    let slot = page_idx * SLOTS_PER_PAGE + word_idx * BITS_PER_WORD + bit;
                    f(slot, Ternary::from_index(code as usize));
                }
            }
        }
    }

    fn allocated_bytes(&self) -> usize {
        let mut bytes =
            self.pages.capacity() * std::mem::size_of::<Option<Box<[u64; WORDS_PER_PAGE]>>>();
        bytes += self.pages.iter().flatten().count() * std::mem::size_of::<[u64; WORDS_PER_PAGE]>();
        bytes
    }
}

/// 分页 2-bit 世界投影。
#[derive(Debug, Clone)]
pub struct TritWorld {
    fast: [[Plane; FAST_FIELDS]; FAST_KINDS],
    overflow: BTreeMap<u32, u8>,
    len: usize,
    serial: u64,
}

impl Default for TritWorld {
    fn default() -> Self {
        Self::new()
    }
}

impl TritWorld {
    /// 空世界。
    pub fn new() -> Self {
        Self {
            fast: std::array::from_fn(|_| std::array::from_fn(|_| Plane::default())),
            overflow: BTreeMap::new(),
            len: 0,
            serial: next_serial(),
        }
    }

    /// 从传统映射构建（快照回滚等冷路径）。
    pub fn from_map(map: &BTreeMap<u32, Ternary>) -> Self {
        let mut world = Self::new();
        for (&key, &value) in map {
            world.set(key, value);
        }
        world
    }

    /// 结构版本号：新增车道 / 清空 / 重建时变化，值变更不影响它。
    #[inline]
    pub fn structure_serial(&self) -> u64 {
        self.serial
    }

    /// 读取一个键；不存在返回 `None`。
    #[inline]
    pub fn get(&self, key: u32) -> Option<Ternary> {
        match fast_parts(key) {
            Some((kind, slot, field)) => self.fast[kind][field].get(slot),
            None => self
                .overflow
                .get(&key)
                .map(|&code| Ternary::from_index(code as usize)),
        }
    }

    /// 写入一个键（不存在则新建）。
    pub fn set(&mut self, key: u32, value: Ternary) {
        let new_lane = match fast_parts(key) {
            Some((kind, slot, field)) => self.fast[kind][field].set(slot, value),
            None => {
                let old = self.overflow.get(&key).copied();
                let code = value.index() as u8;
                if old == Some(code) {
                    return;
                }
                self.overflow.insert(key, code);
                old.is_none()
            }
        };
        if new_lane {
            self.len += 1;
            self.serial = next_serial();
        }
    }

    /// 键是否存在。
    #[inline]
    pub fn contains_key(&self, key: u32) -> bool {
        self.get(key).is_some()
    }

    /// 车道数。
    #[inline]
    pub fn len(&self) -> usize {
        self.len
    }

    /// 是否为空。
    #[inline]
    pub fn is_empty(&self) -> bool {
        self.len == 0
    }

    /// 清空全部车道。
    pub fn clear(&mut self) {
        if self.len != 0
            || !self.overflow.is_empty()
            || self
                .fast
                .iter()
                .any(|f| f.iter().any(|p| !p.pages.is_empty()))
        {
            self.fast = std::array::from_fn(|_| std::array::from_fn(|_| Plane::default()));
            self.overflow.clear();
            self.len = 0;
            self.serial = next_serial();
        }
    }

    /// 遍历全部车道。顺序为「溢出键 → 种类 → 字段 → slot」，不保证按 key 升序；
    /// 需要确定性排序的调用方自行排序。
    pub fn for_each(&self, mut f: impl FnMut(u32, Ternary)) {
        for (&key, &code) in &self.overflow {
            f(key, Ternary::from_index(code as usize));
        }
        for (kind_idx, fields) in self.fast.iter().enumerate() {
            let kind = (kind_idx + 1) as u32;
            for (field_idx, plane) in fields.iter().enumerate() {
                let field = (field_idx + 1) as u32;
                plane.for_each(|slot, value| {
                    let key = (kind << KIND_SHIFT) | ((slot as u32) << SLOT_SHIFT) | field;
                    f(key, value);
                });
            }
        }
    }

    /// 常驻字节数估算（结构体 + 页 + 溢出节点），用于回归断言。
    pub fn allocated_bytes(&self) -> usize {
        let mut bytes = std::mem::size_of::<Self>();
        for fields in &self.fast {
            for plane in fields {
                bytes += plane.allocated_bytes();
            }
        }
        // 每个 BTreeMap 节点保守按 key(4) + value(1) + 结构开销(16) 计。
        bytes + self.overflow.len() * 24
    }
}

impl PartialEq for TritWorld {
    fn eq(&self, other: &Self) -> bool {
        if self.len != other.len {
            return false;
        }
        let mut same = true;
        self.for_each(|key, value| {
            if same && other.get(key) != Some(value) {
                same = false;
            }
        });
        same
    }
}

impl Eq for TritWorld {}

#[cfg(test)]
mod tests {
    use super::*;

    fn key(kind: u32, slot: u32, field: u32) -> u32 {
        (kind << KIND_SHIFT) | (slot << SLOT_SHIFT) | field
    }

    #[test]
    fn round_trips_all_trits_across_page_boundaries() {
        let mut world = TritWorld::new();
        for (i, slot) in [0u32, 31, 32, 63, 64, 127, 128, 255]
            .into_iter()
            .enumerate()
        {
            for field in 1..=9 {
                let value = Ternary::from_index((i + field as usize) % 3);
                world.set(key(2, slot, field), value);
                assert_eq!(world.get(key(2, slot, field)), Some(value));
            }
        }
        assert_eq!(world.len(), 8 * 9);
    }

    #[test]
    fn matches_btree_map_for_mixed_keys() {
        let mut world = TritWorld::new();
        let mut map = BTreeMap::new();
        let mut state = 0x1234_5678_u32;
        for _ in 0..2_000 {
            state = state.wrapping_mul(1_664_525).wrapping_add(1_013_904_223);
            // 一半快速通道、一半溢出键（kind 0/4..15、field 0/10+）。
            let mixed = if state & 1 == 0 {
                key(
                    1 + (state >> 8) % 3,
                    (state >> 12) % 900,
                    1 + (state >> 24) % 9,
                )
            } else {
                key(
                    (state >> 20) % 16,
                    (state >> 4) % 2_000,
                    (state >> 8) % 4096,
                )
            };
            let value = Ternary::from_index((state >> 3) as usize % 3);
            world.set(mixed, value);
            map.insert(mixed, value);
        }

        assert_eq!(world.len(), map.len());
        for (&k, &v) in &map {
            assert_eq!(world.get(k), Some(v), "键 {k:#x}");
        }
        for &k in map.keys() {
            assert!(world.contains_key(k));
        }
        assert_eq!(world, TritWorld::from_map(&map));

        let mut seen = BTreeMap::new();
        world.for_each(|k, v| {
            assert!(seen.insert(k, v).is_none(), "重复车道 {k:#x}");
        });
        assert_eq!(seen, map);
    }

    #[test]
    fn clear_resets_content_and_bumps_serial() {
        let mut world = TritWorld::new();
        world.set(key(1, 7, 5), Ternary::True);
        world.set(42, Ternary::False);
        let before = world.structure_serial();
        world.clear();
        assert!(world.is_empty());
        assert_eq!(world.get(key(1, 7, 5)), None);
        assert_eq!(world.get(42), None);
        assert_ne!(world.structure_serial(), before);
        assert_eq!(world, TritWorld::new());
    }

    #[test]
    fn value_update_keeps_structure_serial() {
        let mut world = TritWorld::new();
        world.set(key(3, 100, 9), Ternary::False);
        let serial = world.structure_serial();
        world.set(key(3, 100, 9), Ternary::True);
        assert_eq!(world.structure_serial(), serial);
        assert_eq!(world.len(), 1);
    }

    #[test]
    fn ten_thousand_lanes_fit_in_four_kilobytes() {
        let mut world = TritWorld::new();
        // 9000 条抛射物车道（1000 slot × 9 字段）+ 1000 条 NPC 车道（200 slot × 5 字段）。
        for slot in 0..1_000 {
            for field in 1..=9 {
                world.set(key(2, slot, field), Ternary::Unknown);
            }
        }
        for slot in 0..200 {
            for field in 1..=5 {
                world.set(key(1, slot, field), Ternary::True);
            }
        }
        assert_eq!(world.len(), 10_000);
        assert!(
            world.allocated_bytes() <= 4096,
            "1 万车道应 ≤ 4 KB，实际 {} B",
            world.allocated_bytes()
        );
    }
}
