//! 已 port 的实体类型注册表。
//!
//! type 编号按 Terraria 原版 `Terraria.ID.NPCID` / `ProjectileID`（含负数的体型变体）。
//! 不在列表里的 type → 双轨 predict 走历史外推。
//!
//! **不变量**：列在这里的每个 type 必须在 `npc::dispatch` / `projectile::dispatch`
//! 里有实际分支（由各 dispatch 模块的 `every_ported_type_has_a_branch` 测试锁定）。
//! 只登记「确实有实现」的类型 —— 登记了却弃权，反而会抑制历史外推。

/// 已 port 的 Terraria NPC type 列表（按 `Terraria.ID.NPCID`）。
///
/// 按 AI 家族分组：
/// - 史莱姆（跳跃 AI）：1 Blue Slime、-1 Slimeling、-2 Slimer、-3 Green Slime、
///   -4 Pinky、-5 Baby Slime、-6 Black Slime、-7 Purple Slime、-8 Red Slime、
///   -9 Yellow Slime、-10 Jungle Slime、16 Mother Slime
/// - 僵尸（地面追击）：3 Zombie、-44 / -45 Female Zombie、-54 / -55 Raincoat Zombie
/// - 飞行追击：2 Demon Eye、-37..-43 Demon Eye 变体、5 Servant of Cthulhu、
///   6 Eater of Souls、23 Meteor Head、42 Hornet（含 -65..-56 体型变体）、
///   49 Cave Bat、51 Jungle Bat、60 Hellbat、93 Giant Bat
///
/// 刻意不包含：Boss（4 Eye of Cthulhu、50 King Slime）、蠕虫体节（7..15 的
/// Devourer / Giant Worm / Eater of Worlds）、骷髅（21 及 -46..-53）——
/// 它们的 AI 与上面的简化家族差异过大，套用即假覆盖。
pub const PORTED_NPC_TYPES: &[i32] = &[
    1, -1, -2, -3, -4, -5, -6, -7, -8, -9, -10, 16, // 史莱姆家族
    3, -44, -45, -54, -55, // 僵尸家族
    2, -37, -38, -39, -40, -41, -42, -43, 5, 6, // 飞行追击家族
    23, 42, 49, 51, 60, 93, // 飞行追击：陨石怪 / 黄蜂 / 蝙蝠系
    -65, -64, -63, -62, -61, -60, -59, -58, -57, -56, // Hornet 体型变体
];

/// 已 port 的 Terraria Projectile type 列表（按 `Terraria.ID.ProjectileID`）。
///
/// - 抛物线（受重力）：1 Wooden Arrow、2 Fire Arrow、3 Shuriken、4 Unholy Arrow、
///   5 Jester's Arrow、41 Hellfire Arrow、81 Wooden Arrow（敌）、82 Flaming Arrow（敌）、
///   91 Holy Arrow、103 Cursed Arrow、109 Snow Ball（敌）、172 Frostburn Arrow、
///   240 Cannonball（敌）、27 Water Bolt（敌，弹跳）
/// - 直线：44 Demon Sickle、55 Stinger（敌）、83 Eye Laser、84 Pink Laser、
///   96 Cursed Flame（敌）、100 Death Laser、115 Unholy Trident（敌）、
///   128 Frost Blast（敌）、258 Fireball（敌）、264 Rain（敌）、288 Golden Shower（敌）
///
/// **分类依据与其局限**：terraria.wiki.gg 的 `Projectile_IDs` 页只有 ID / 名称 /
/// 来源，没有任何运动学字段，所以「抛物线 / 直线」无法逐条从 wiki 证实。
/// 这里的两类只是「`Projectile.gravity > 0` / `= 0`」的近似归属，以下几类行为
/// 没有建模，属于明确近似：27 Water Bolt 与 258 Fireball 会反弹、
/// 44 Demon Sickle 会追踪玩家。对拍若在这些弹幕上持续分歧，应优先怀疑此处。
///
/// 另：弹幕目前不做 KernelFirst（跳过 `Projectile.Update` 会丢位置积分 / 重力 /
/// 碰撞 / `timeLeft`），只参与预测与对拍，见 `mod/TritRuntimeMod/src/EntityTakeover.cs`。
pub const PORTED_PROJECTILE_TYPES: &[i32] = &[
    1, 2, 3, 4, 5, 27, 41, 81, 82, 91, 103, 109, 172, 240, // 抛物线
    44, 55, 83, 84, 96, 100, 115, 128, 258, 264, 288, // 直线
];

/// 查这个 NPC type 有没有被 port。
#[inline]
pub fn is_ported_npc_type(npc_type: i32) -> bool {
    PORTED_NPC_TYPES.contains(&npc_type)
}

/// 查这个 Projectile type 有没有被 port。
#[inline]
pub fn is_ported_projectile_type(proj_type: i32) -> bool {
    PORTED_PROJECTILE_TYPES.contains(&proj_type)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn ported_checks() {
        assert!(is_ported_npc_type(1)); // Blue Slime
        assert!(is_ported_npc_type(-6)); // Black Slime（负 ID 变体）
        assert!(is_ported_npc_type(3)); // Zombie
        assert!(is_ported_npc_type(6)); // Eater of Souls
        assert!(is_ported_npc_type(49)); // Cave Bat
        assert!(is_ported_npc_type(-60)); // Little Hornet（体型变体）
        assert!(!is_ported_npc_type(4)); // Eye of Cthulhu（Boss，不 port）
        assert!(!is_ported_npc_type(10)); // Giant Worm Head（体节，不 port）
        assert!(!is_ported_npc_type(99999));
        assert!(!is_ported_npc_type(0));
    }

    #[test]
    fn ported_projectile_checks() {
        assert!(is_ported_projectile_type(1)); // Wooden Arrow
        assert!(is_ported_projectile_type(3)); // Shuriken
        assert!(is_ported_projectile_type(82)); // Flaming Arrow（敌）
        assert!(is_ported_projectile_type(100)); // Death Laser（敌）
        assert!(is_ported_projectile_type(128)); // Frost Blast
        assert!(!is_ported_projectile_type(0));
        assert!(!is_ported_projectile_type(99999));
    }
}
