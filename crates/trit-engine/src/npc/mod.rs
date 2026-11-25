//! NPC AI step —— Terraria 原版等价逻辑端口。
//!
//! 每个已 port 的 type 有一个独立文件（`slime.rs`, `zombie.rs`, `bat.rs` 等）。
//! `dispatch()` 按 `state.npc_type` 分发给对应的纯函数。

use crate::prng::Prng;

/// 喂给引擎的 NPC 状态快照。
///
/// C# 侧 STATE_PUSH opcode 序列化这个结构体的字段（IEEE 754 LE f32），
/// Rust 侧 op_state_push() 反序列化后填充 `entity_states`。
/// 不同 NPC type 只消费自己需要的字段 —— 多余字段为零也不影响。
#[derive(Debug, Clone, Copy)]
pub struct NpcState {
    pub npc_type: i32,
    pub velocity_x: f32,
    pub velocity_y: f32,
    pub position_x: f32,
    pub position_y: f32,
    pub direction: i32,
    pub life: i32,
    pub life_max: i32,
    /// 追踪目标索引（player whoAmI 或 -1 无目标）
    pub target: i32,
    /// Terraria NPC.AI 的 8 个槽
    pub ai: [f32; 8],
    /// Terraria.NPCUtils.Speed 近似值
    pub walk_speed: f32,
    /// 距离追踪目标的像素距离
    pub target_distance: f32,
    /// 目标相对实体的水平像素偏移（`npc.Center.X - target.Center.X`）。
    /// 0 = 无目标 / 旧版载荷未携带；引擎据此算追踪方向。
    pub target_dx: f32,
    /// 目标相对实体的垂直像素偏移（`npc.Center.Y - target.Center.Y`）。
    pub target_dy: f32,
    pub tile_below: bool,
    pub on_ground: bool,
}

impl Default for NpcState {
    fn default() -> Self {
        Self {
            npc_type: 0,
            velocity_x: 0.0,
            velocity_y: 0.0,
            position_x: 0.0,
            position_y: 0.0,
            direction: 1,
            life: 1,
            life_max: 1,
            target: -1,
            ai: [0.0; 8],
            walk_speed: 2.0,
            target_distance: 1000.0,
            target_dx: 0.0,
            target_dy: 0.0,
            tile_below: true,
            on_ground: true,
        }
    }
}

/// 引擎 AI step 的输出。
///
/// `Option` 语义：`Some` = 引擎建议的值；`None` = 弃权（调用方 fallback 历史外推）。
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct NpcDelta {
    /// velocity.X 的新方向符号（+1.0 向右 / -1.0 向左 / 0.0 不变）
    pub velocity_x_sign: Option<f32>,
    /// velocity.Y 的新方向符号（+1.0 向下 / -1.0 向上 / 0.0 不变）
    pub velocity_y_sign: Option<f32>,
    /// 新方向（-1 左 / +1 右 / 0 不变）
    pub direction: Option<i32>,
    /// 新的速度大小（None = 保持原绝对值，只改符号）
    pub speed_magnitude: Option<f32>,
}

impl NpcDelta {
    /// 弃权标记 —— 调用方 fallback 历史外推。
    pub const UNSET: Self = Self {
        velocity_x_sign: None,
        velocity_y_sign: None,
        direction: None,
        speed_magnitude: None,
    };

    /// 全零：引擎算出 delta，但方向不变。
    pub fn zero() -> Self {
        Self {
            velocity_x_sign: Some(0.0),
            velocity_y_sign: Some(0.0),
            direction: None,
            speed_magnitude: None,
        }
    }
}

// ──────────────────────────────────────────────────────────────────────────
// Dispatch 入口 —— 按 type 分发给已 port 的分支
// ──────────────────────────────────────────────────────────────────────────

pub mod flyer;
pub mod slime;
pub mod zombie;

/// 顶层 dispatch —— 按 npc_type 调对应的 port 分支。
///
/// 分支集合必须与 [`crate::registry::PORTED_NPC_TYPES`] 完全一致，
/// 由本模块的 `every_ported_type_has_a_branch` 测试锁定。
pub fn dispatch(state: &NpcState, rand: &mut Prng) -> NpcDelta {
    match state.npc_type {
        // 史莱姆家族（跳跃 AI）：Blue / Slimeling / Slimer / Green / Pinky / Baby /
        // Black / Purple / Red / Yellow / Jungle / Mother Slime
        1 | -1 | -2 | -3 | -4 | -5 | -6 | -7 | -8 | -9 | -10 | 16 => slime::step(state, rand),
        // 僵尸家族（地面追击）：Zombie / Female Zombie / Raincoat Zombie
        3 | -44 | -45 | -54 | -55 => zombie::step(state, rand),
        // 飞行追击家族：Demon Eye（含变体）/ Servant of Cthulhu / Eater of Souls /
        // Meteor Head / Hornet（含体型变体）/ Cave Bat / Jungle Bat / Hellbat / Giant Bat
        2 | -37 | -38 | -39 | -40 | -41 | -42 | -43 | 5 | 6 | 23 | 42 | 49 | 51 | 60 | 93 | -65
        | -64 | -63 | -62 | -61 | -60 | -59 | -58 | -57 | -56 => flyer::step(state, rand),
        // 未 port 的 type 一律 UNSET → 调用方 fallback 历史外推。
        _ => NpcDelta::UNSET,
    }
}

// ──────────────────────────────────────────────────────────────────────────
// 辅助函数
// ──────────────────────────────────────────────────────────────────────────

/// 根据 velocity.x 的当前符号决定 direction。
pub fn direction_from_vx(vx: f32) -> i32 {
    if vx > 0.01 {
        1
    } else if vx < -0.01 {
        -1
    } else {
        0
    }
}

/// sign helper
pub fn sign(v: f32) -> f32 {
    if v > 0.0 {
        1.0
    } else if v < 0.0 {
        -1.0
    } else {
        0.0
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn default_state_is_sensible() {
        let s = NpcState::default();
        assert_eq!(s.npc_type, 0);
        assert_eq!(s.direction, 1);
        assert_eq!(s.walk_speed, 2.0);
    }

    #[test]
    fn unset_delta_fallback() {
        let d = NpcDelta::UNSET;
        assert!(d.velocity_x_sign.is_none());
        assert!(d.velocity_y_sign.is_none());
        assert!(d.direction.is_none());
    }

    #[test]
    fn sign_helper_works() {
        assert_eq!(sign(5.0), 1.0);
        assert_eq!(sign(-3.0), -1.0);
        assert_eq!(sign(0.0), 0.0);
    }

    #[test]
    fn direction_from_vx_maps_sign() {
        assert_eq!(direction_from_vx(1.5), 1);
        assert_eq!(direction_from_vx(-0.5), -1);
        assert_eq!(direction_from_vx(0.0), 0);
    }

    /// 不变量：registry 说「已 port」的每个 type 都必须真有实现分支。
    /// 否则该类型会既不产出引擎预测、又抑制历史外推（比不 port 更差）。
    #[test]
    fn every_ported_type_has_a_branch() {
        let base = NpcState {
            on_ground: true,
            target: 0,
            ..Default::default()
        };
        for &npc_type in crate::registry::PORTED_NPC_TYPES {
            let state = NpcState { npc_type, ..base };
            let delta = dispatch(&state, &mut Prng::new(1));
            assert_ne!(
                delta,
                NpcDelta::UNSET,
                "type {npc_type} 在 registry 里但无实现"
            );
        }
    }
}
