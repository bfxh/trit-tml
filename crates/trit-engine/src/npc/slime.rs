//! Slime family AI —— Terraria 原版史莱姆家族（Blue / Green / Black / Slimeling 等）的核心跳跃逻辑。
//!
//! Terraria.NPC.AI 里 Slime 的逻辑（简化版，高频路径）：
//! 1. 如果站在地上：随机停顿 0-60 tick 然后跳一下
//! 2. 跳起时给 velocity.y 一个负值（向上），velocity.x 给一个随机方向
//! 3. 空中：受重力影响往下掉
//! 4. 撞墙或落地后重新选方向

use crate::npc::{NpcDelta, NpcState, direction_from_vx, sign};
use crate::prng::Prng;

/// 基础史莱姆 AI step —— 只输出 velocity sign（KernelFirst 方向门只改符号）。
pub fn step(state: &NpcState, rand: &mut Prng) -> NpcDelta {
    // Slime 的 ai[0] 是 jump 计时器
    let jump_timer = state.ai[0] as i32;

    if state.on_ground {
        if jump_timer <= 0 {
            // 该跳了：有目标向量时朝目标跳（`target_dx = 实体 - 目标`，取反号）；
            // 否则随机左右，并沿用「贴墙反向」的历史启发式（ai[1] 存上次方向）。
            let has_vector = state.target_dx.abs() >= 1.0;
            let dir = if has_vector {
                (-sign(state.target_dx)) as i32
            } else {
                rand.next_i32(-1, 1)
            };
            let last_dir = state.ai[1] as i32;
            let actual_dir = if !has_vector && state.velocity_x == 0.0 && last_dir != 0 {
                -last_dir
            } else {
                dir
            };

            return NpcDelta {
                velocity_x_sign: Some(actual_dir as f32),
                velocity_y_sign: Some(-1.0), // 向上跳
                direction: Some(actual_dir),
                speed_magnitude: Some(state.walk_speed.max(1.5)),
            };
        }
        // 还没到跳的时机 —— 方向不变（可能在抖）
        NpcDelta {
            velocity_x_sign: Some(0.0), // 无水平移动
            velocity_y_sign: Some(0.0), // 垂直静止（在地上）
            direction: Some(state.direction),
            speed_magnitude: None,
        }
    } else {
        // 在空中 —— 受重力影响，往下掉；水平方向保持或微调
        let vy = state.velocity_y;
        let vy_dir = if vy < 0.0 { -1.0 } else { 1.0 };

        let vx = state.velocity_x;
        let vx_dir = direction_from_vx(vx) as f32;

        NpcDelta {
            velocity_x_sign: Some(if vx_dir == 0.0 {
                (state.direction as f32).signum()
            } else {
                vx_dir
            }),
            velocity_y_sign: Some(vy_dir),
            direction: Some(state.direction),
            speed_magnitude: None,
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn slime_jumps_on_ground_when_timer_expires() {
        let s = NpcState {
            npc_type: 1,
            on_ground: true,
            ai: [0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0],
            ..Default::default()
        };

        let mut rand = Prng::new(42);
        let d = step(&s, &mut rand);
        assert_eq!(d.velocity_y_sign, Some(-1.0)); // 向上跳
        assert!(d.speed_magnitude.is_some());
    }

    #[test]
    fn slime_stays_when_on_ground_timer_active() {
        let s = NpcState {
            npc_type: 1,
            on_ground: true,
            ai: [30.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0],
            ..Default::default()
        };

        let mut rand = Prng::new(42);
        let d = step(&s, &mut rand);
        assert_eq!(d.velocity_x_sign, Some(0.0));
        assert_eq!(d.velocity_y_sign, Some(0.0));
    }

    #[test]
    fn slime_falls_in_air() {
        let s = NpcState {
            npc_type: 1,
            on_ground: false,
            velocity_y: -2.5, // 还在上升
            ..Default::default()
        };

        let mut rand = Prng::new(42);
        let d = step(&s, &mut rand);
        assert_eq!(d.velocity_y_sign, Some(-1.0)); // 向上的符号
    }

    #[test]
    fn slime_jumps_toward_target() {
        // 目标在右侧（target_dx < 0）→ 向右跳，而不是随机左右。
        let s = NpcState {
            npc_type: 1,
            on_ground: true,
            target: 0,
            target_dx: -160.0,
            ..Default::default()
        };

        let mut rand = Prng::new(99);
        let d = step(&s, &mut rand);
        assert_eq!(d.velocity_x_sign, Some(1.0));
        assert_eq!(d.direction, Some(1));
        assert_eq!(d.velocity_y_sign, Some(-1.0));
    }
}
