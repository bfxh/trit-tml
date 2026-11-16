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

pub mod bat;
pub mod slime;
pub mod zombie;

/// 顶层 dispatch —— 按 npc_type 调对应的 port 分支。
pub fn dispatch(state: &NpcState, rand: &mut Prng) -> NpcDelta {
    match state.npc_type {
        1 | 10 => slime::step(state, rand), // Blue Slime / Black Slime
        3 | 14 | 15 => zombie::step(state, rand), // Zombie / Armed Zombie / Sand Zombie
        5 => bat::step(state, rand),
        // 还没 port 的 type 即使 registry 里列了也给 UNSET（保守 fallback）
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
}
