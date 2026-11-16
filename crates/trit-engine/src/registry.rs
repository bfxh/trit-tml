//! 已 port 的实体类型注册表。
//!
//! MVP v0.1 只 port 高频 + 简单的类型。不在列表里的 type → 双轨 predict 走历史外推。

/// 已 port 的 Terraria NPC type 列表（按 Terraria.ID.NPCID）。
///
/// MVP v0.1 覆盖 7 种高频 NPC：
/// type 1 Blue Slime, 2 Demon Eye, 3 Zombie, 4 Skeleton, 5 Bat,
/// type 6 Slime King, 10 Black Slime。
pub const PORTED_NPC_TYPES: &[i32] = &[1, 2, 3, 4, 5, 6, 10];

/// 已 port 的 Terraria Projectile type 列表（按 Terraria.ID.ProjectileID）。
///
/// MVP v0.1 覆盖 4 种高频弹道。
pub const PORTED_PROJECTILE_TYPES: &[i32] = &[1, 2, 3, 128];

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
        assert!(is_ported_npc_type(3)); // Zombie
        assert!(is_ported_npc_type(6)); // Slime King
        assert!(!is_ported_npc_type(99999));
        assert!(!is_ported_npc_type(0));
    }
}
