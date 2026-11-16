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
pub mod rocket;

/// 顶层 dispatch —— 按 proj_type 分发给 port 分支。
pub fn dispatch(state: &ProjectileState, rand: &mut Prng) -> ProjectileDelta {
    match state.proj_type {
        1 => bullet::step(state, rand),
        2 => arrow::step(state, rand),
        3 => rocket::step(state, rand),
        // 128 Bullet（Terraria.ProjectileID.Bullet = 128）
        128 => bullet::step(state, rand),
        _ => ProjectileDelta::UNSET,
    }
}
