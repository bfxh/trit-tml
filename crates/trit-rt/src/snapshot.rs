//! 快照 / 差分 / 回滚：世界状态投影成三进制张量，压成字节后做 diff。
//!
//! 格式（小端）：
//! ```text
//! [u8 version][u64 tick][u64 log_seq][u32 lane_count]
//! [u32 key] * lane_count
//! [u32 trit_count][packed trits]        // 5 trits/byte，见 trit-codec
//! ```
//! 键与值分开存放：键是稀疏 u32，值走 1.6 bit/trit 的密集张量投影。

use std::collections::BTreeMap;

use trit_core::bulk::TriVec;
use trit_core::value::Ternary;

use crate::error::RtError;
use crate::wire::{Reader, Writer};

/// 快照格式版本。
pub const SNAPSHOT_VERSION: u8 = 1;

/// 一个快照。
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Snapshot {
    /// 快照 ID。
    pub id: u64,
    /// 打快照时的 tick。
    pub tick: u64,
    /// 打快照时的事务日志序列号（回滚到此快照会截断更高序列）。
    pub log_seq: u64,
    /// 世界投影。
    pub world: BTreeMap<u32, Ternary>,
    /// 序列化字节。
    pub bytes: Vec<u8>,
}

/// 一条差分项：`(键, 旧值, 新值)`。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct DiffEntry {
    /// 世界键。
    pub key: u32,
    /// 旧值。
    pub old: Ternary,
    /// 新值。
    pub new: Ternary,
}

/// 把世界投影编码为字节。
pub fn encode_world(world: &BTreeMap<u32, Ternary>, tick: u64, log_seq: u64) -> Vec<u8> {
    // 三进制张量投影：密集 TriVec 覆盖所有活跃车道。
    let values: Vec<Ternary> = world.values().copied().collect();
    let tensor = TriVec::from_ternary(&values);
    let trits: Vec<Ternary> = (0..tensor.len()).map(|index| tensor.get(index)).collect();

    let mut writer = Writer::new();
    writer.u8(SNAPSHOT_VERSION);
    writer.u64(tick);
    writer.u64(log_seq);
    writer.u32(world.len() as u32);
    for key in world.keys() {
        writer.u32(*key);
    }
    writer.trits(&trits);
    writer.into_vec()
}

/// 从字节还原世界投影；格式不符即报错（不猜）。
pub fn decode_world(bytes: &[u8]) -> Result<(u64, u64, BTreeMap<u32, Ternary>), RtError> {
    let mut reader = Reader::new(bytes);
    let version = reader.u8()?;
    if version != SNAPSHOT_VERSION {
        return Err(RtError::Decode(format!(
            "快照版本不符：期望 {SNAPSHOT_VERSION}，实际 {version}"
        )));
    }
    let tick = reader.u64()?;
    let log_seq = reader.u64()?;
    let lane_count = reader.u32()? as usize;
    let mut keys = Vec::with_capacity(lane_count.min(1 << 20));
    for _ in 0..lane_count {
        keys.push(reader.u32()?);
    }
    let values = reader.trits()?;
    if values.len() != lane_count {
        return Err(RtError::Decode(format!(
            "键值数量不符：{lane_count} 个键，{} 个值",
            values.len()
        )));
    }
    let world = keys.into_iter().zip(values).collect();
    Ok((tick, log_seq, world))
}

/// 差分两个世界投影，返回发生变化的车道（按键升序）。
pub fn diff_worlds(
    before: &BTreeMap<u32, Ternary>,
    after: &BTreeMap<u32, Ternary>,
) -> Vec<DiffEntry> {
    let mut entries = Vec::new();
    for (key, new) in after {
        let old = before.get(key).copied().unwrap_or(Ternary::Unknown);
        if old != *new {
            entries.push(DiffEntry {
                key: *key,
                old,
                new: *new,
            });
        }
    }
    for (key, old) in before {
        if !after.contains_key(key) {
            entries.push(DiffEntry {
                key: *key,
                old: *old,
                new: Ternary::Unknown,
            });
        }
    }
    entries.sort_by_key(|entry| entry.key);
    entries
}

/// 差分项列表编码为字节。
pub fn encode_diff(entries: &[DiffEntry]) -> Vec<u8> {
    let mut writer = Writer::new();
    writer.u32(entries.len() as u32);
    for entry in entries {
        writer.u32(entry.key);
        writer.u8(crate::wire::trit_byte(entry.old));
        writer.u8(crate::wire::trit_byte(entry.new));
    }
    writer.into_vec()
}

/// 从字节还原差分项。
pub fn decode_diff(bytes: &[u8]) -> Result<Vec<DiffEntry>, RtError> {
    let mut reader = Reader::new(bytes);
    let count = reader.u32()? as usize;
    let mut entries = Vec::with_capacity(count.min(1 << 20));
    for _ in 0..count {
        let key = reader.u32()?;
        let old = crate::wire::trit_from_byte(reader.u8()?);
        let new = crate::wire::trit_from_byte(reader.u8()?);
        entries.push(DiffEntry { key, old, new });
    }
    if !reader.is_empty() {
        return Err(RtError::Decode("差分数据有多余字节".into()));
    }
    Ok(entries)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn world(pairs: &[(u32, Ternary)]) -> BTreeMap<u32, Ternary> {
        pairs.iter().copied().collect()
    }

    #[test]
    fn world_round_trips_through_bytes() {
        let original = world(&[
            (1, Ternary::False),
            (7, Ternary::True),
            (900, Ternary::Unknown),
            (4242, Ternary::True),
        ]);
        let bytes = encode_world(&original, 12, 34);
        let (tick, log_seq, decoded) = decode_world(&bytes).unwrap();
        assert_eq!(tick, 12);
        assert_eq!(log_seq, 34);
        assert_eq!(decoded, original);
    }

    #[test]
    fn empty_world_round_trips() {
        let bytes = encode_world(&BTreeMap::new(), 0, 0);
        let (_, _, decoded) = decode_world(&bytes).unwrap();
        assert!(decoded.is_empty());
    }

    #[test]
    fn bad_version_is_rejected() {
        let mut bytes = encode_world(&world(&[(1, Ternary::True)]), 0, 0);
        bytes[0] = 9;
        assert!(decode_world(&bytes).is_err());
    }

    #[test]
    fn diff_reports_changed_added_and_removed_lanes() {
        let before = world(&[
            (1, Ternary::False),
            (2, Ternary::True),
            (3, Ternary::Unknown),
        ]);
        let after = world(&[(1, Ternary::True), (2, Ternary::True), (4, Ternary::False)]);
        let entries = diff_worlds(&before, &after);
        assert_eq!(
            entries,
            vec![
                DiffEntry {
                    key: 1,
                    old: Ternary::False,
                    new: Ternary::True
                },
                DiffEntry {
                    key: 3,
                    old: Ternary::Unknown,
                    new: Ternary::Unknown
                },
                DiffEntry {
                    key: 4,
                    old: Ternary::Unknown,
                    new: Ternary::False
                },
            ]
        );
    }

    #[test]
    fn diff_round_trips_through_bytes() {
        let entries = vec![
            DiffEntry {
                key: 5,
                old: Ternary::True,
                new: Ternary::False,
            },
            DiffEntry {
                key: 77,
                old: Ternary::Unknown,
                new: Ternary::True,
            },
        ];
        let bytes = encode_diff(&entries);
        assert_eq!(decode_diff(&bytes).unwrap(), entries);
    }
}
