//! 飞行追击家族 AI —— Demon Eye / Servant of Cthulhu / Eater of Souls 的简化逻辑。
//!
//! 原版它们的 AI 都是「空中追踪玩家 + 撞墙转向 + 不落地」：
//! 1. 在空中飞（不受重力）
//! 2. 有目标向量（`target_dx / target_dy`）时直接朝目标飞
//! 3. 没有向量时沿用当前速度方向；水平速度接近零（多半是撞墙）时随机翻向
//!
//! 无目标时保持悬停（速度符号给 0）。

use crate::npc::{NpcDelta, NpcState, direction_from_vx, sign};
use crate::prng::Prng;

pub fn step(state: &NpcState, rand: &mut Prng) -> NpcDelta {
    if state.target < 0 {
        // 无目标 —— 悬停
        return NpcDelta {
            velocity_x_sign: Some(0.0),
            velocity_y_sign: Some(0.0),
            direction: Some(state.direction),
            speed_magnitude: None,
        };
    }

    // `target_dx = 实体 - 目标`，朝目标飞要取反号；不足 1 px 视为该轴无需转向。
    let steer_x = if state.target_dx.abs() >= 1.0 {
        -sign(state.target_dx)
    } else {
        0.0
    };
    let steer_y = if state.target_dy.abs() >= 1.0 {
        -sign(state.target_dy)
    } else {
        0.0
    };

    // 没有目标向量时退回「低速随机翻向」的启发式。
    let mut dir = state.direction;
    if steer_x == 0.0 && steer_y == 0.0 && state.velocity_x.abs() < 0.05 && rand.next_bool() {
        dir = -dir;
    }

    let vx_dir = direction_from_vx(state.velocity_x);
    let vy_dir = direction_from_vx(state.velocity_y);

    let vx = if steer_x != 0.0 {
        steer_x
    } else if vx_dir != 0 {
        vx_dir as f32
    } else {
        dir as f32
    };
    let vy = if steer_y != 0.0 {
        steer_y
    } else if vy_dir != 0 {
        vy_dir as f32
    } else {
        0.0
    };

    NpcDelta {
        velocity_x_sign: Some(vx),
        velocity_y_sign: Some(vy),
        direction: Some(if steer_x != 0.0 { steer_x as i32 } else { dir }),
        // 飞行家族的 `walk_speed.max(1.8)` 只是虚构下限，不是原版速度：
        // 执行层把它当成绝对速度写进 velocity.X 时，垂直追击（target_dx≈0）
        // 会被强行加上水平速度。这里明确不给速度大小，只驱动方向。
        speed_magnitude: None,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn flyer_flies_with_target() {
        let s = NpcState {
            npc_type: 2, // Demon Eye
            target: 0,
            ..Default::default()
        };

        let mut rand = Prng::new(7);
        let d = step(&s, &mut rand);
        assert!(d.velocity_x_sign.unwrap().abs() > 0.0);
    }

    #[test]
    fn flyer_hovers_without_target() {
        let s = NpcState {
            npc_type: 5, // Servant of Cthulhu
            target: -1,
            ..Default::default()
        };

        let mut rand = Prng::new(7);
        let d = step(&s, &mut rand);
        assert_eq!(d.velocity_x_sign, Some(0.0));
        assert_eq!(d.velocity_y_sign, Some(0.0));
    }

    #[test]
    fn flyer_steers_toward_target_vector() {
        // 目标在左上方（dx > 0、dy < 0）→ 向左、向上。
        let s = NpcState {
            npc_type: 2, // Demon Eye
            target: 0,
            target_dx: 120.0,
            target_dy: -80.0,
            velocity_x: 2.0, // 当前朝右飞，但目标在左
            ..Default::default()
        };

        let mut rand = Prng::new(7);
        let d = step(&s, &mut rand);
        assert_eq!(d.velocity_x_sign, Some(-1.0));
        assert_eq!(d.velocity_y_sign, Some(1.0));
        assert_eq!(d.direction, Some(-1));
    }
}
