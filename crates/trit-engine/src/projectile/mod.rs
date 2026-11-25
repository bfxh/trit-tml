//! Projectile.Update step —— Terraria 原版等价逻辑端口。

use crate::prng::Prng;

// re-export npc 辅助函数供 projectile 子模块用
pub(crate) use crate::npc::direction_from_vx;

/// Projectile 状态快照（STATE_PUSH 推送）。
#[derive(Debug, Clone, Copy)]
pub struct ProjectileState {
    pub proj_type: i32,
    pub velocity_x: f32,
    pub velocity_y: f32,
    pub position_x: f32,
    pub position_y: f32,
    pub direction: i32,
    /// Terraria Projectile.AI 的 2 个槽
    pub ai: [f32; 2],
    pub active: bool,
    pub tile_below: bool,
    /// 追踪目标索引（-1 无目标）
    pub target: i32,
    /// 目标相对弹幕的水平像素偏移（`弹幕中心.X - 目标中心.X`）；0 = 无目标/未知。
    pub target_dx: f32,
    /// 目标相对弹幕的垂直像素偏移。
    pub target_dy: f32,
    pub gravity: f32,
}

impl Default for ProjectileState {
    fn default() -> Self {
        Self {
            proj_type: 0,
            velocity_x: 0.0,
            velocity_y: 0.0,
            position_x: 0.0,
            position_y: 0.0,
            direction: 1,
            ai: [0.0; 2],
            active: true,
            tile_below: false,
            target: -1,
            target_dx: 0.0,
            target_dy: 0.0,
            gravity: 0.08,
        }
    }
}

/// Projectile step 输出。
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct ProjectileDelta {
    pub velocity_x_sign: Option<f32>,
    pub velocity_y_sign: Option<f32>,
    pub direction: Option<i32>,
    pub speed_magnitude: Option<f32>,
}

impl ProjectileDelta {
    pub const UNSET: Self = Self {
        velocity_x_sign: None,
        velocity_y_sign: None,
        direction: None,
        speed_magnitude: None,
    };
}

pub mod arrow;
pub mod bullet;

/// 顶层 dispatch —— 按 proj_type 分发给 port 分支。
///
/// 分支集合必须与 [`crate::registry::PORTED_PROJECTILE_TYPES`] 完全一致，
/// 由本模块的 `every_ported_type_has_a_branch` 测试锁定。
pub fn dispatch(state: &ProjectileState, rand: &mut Prng) -> ProjectileDelta {
    match state.proj_type {
        // 1 Wooden Arrow / 2 Fire Arrow / 4 Unholy Arrow：抛物线（受重力）。
        // 3 Shuriken：投掷物，同样受重力、没有追踪——不能套追踪弹逻辑。
        1..=4 => arrow::step(state, rand),
        // 128 Frost Blast（敌对直线弹）。
        128 => bullet::step(state, rand),
        _ => ProjectileDelta::UNSET,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// 不变量：registry 说「已 port」的每个弹幕 type 都必须真有实现分支。
    #[test]
    fn every_ported_type_has_a_branch() {
        for &proj_type in crate::registry::PORTED_PROJECTILE_TYPES {
            let state = ProjectileState {
                proj_type,
                ..Default::default()
            };
            let delta = dispatch(&state, &mut Prng::new(1));
            assert_ne!(
                delta,
                ProjectileDelta::UNSET,
                "type {proj_type} 在 registry 里但无实现"
            );
        }
    }
}
