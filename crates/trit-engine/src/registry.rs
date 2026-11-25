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
///   6 Eater of Souls
///
/// 刻意不包含：Boss（4 Eye of Cthulhu、50 King Slime）、蠕虫体节（7..15 的
/// Devourer / Giant Worm / Eater of Worlds）、骷髅（21 及 -46..-53）——
/// 它们的 AI 与上面的简化家族差异过大，套用即假覆盖。
pub const PORTED_NPC_TYPES: &[i32] = &[
    1, -1, -2, -3, -4, -5, -6, -7, -8, -9, -10, 16, // 史莱姆家族
    3, -44, -45, -54, -55, // 僵尸家族
    2, -37, -38, -39, -40, -41, -42, -43, 5, 6, // 飞行追击家族
];

/// 已 port 的 Terraria Projectile type 列表（按 `Terraria.ID.ProjectileID`）。
///
/// - 1 Wooden Arrow / 2 Fire Arrow / 3 Shuriken / 4 Unholy Arrow → 抛物线（受重力）
/// - 128 Frost Blast（敌对直线弹）
pub const PORTED_PROJECTILE_TYPES: &[i32] = &[1, 2, 3, 4, 128];

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
        assert!(!is_ported_npc_type(4)); // Eye of Cthulhu（Boss，不 port）
        assert!(!is_ported_npc_type(10)); // Giant Worm Head（体节，不 port）
        assert!(!is_ported_npc_type(99999));
        assert!(!is_ported_npc_type(0));
    }

    #[test]
    fn ported_projectile_checks() {
        assert!(is_ported_projectile_type(1)); // Wooden Arrow
        assert!(is_ported_projectile_type(3)); // Shuriken
        assert!(is_ported_projectile_type(128)); // Frost Blast
        assert!(!is_ported_projectile_type(0));
        assert!(!is_ported_projectile_type(99999));
    }
}
