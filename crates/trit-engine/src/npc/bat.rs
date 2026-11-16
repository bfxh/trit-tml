//! Bat AI —— 简单飞行追踪。
//!
//! 1. 在空中飞（不受重力）
//! 2. 追踪最近的玩家
//! 3. 撞墙时随机转向

use crate::npc::{NpcDelta, NpcState, direction_from_vx};
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

    // 撞墙检测：velocity_x 很小 + 当前 direction → 随机翻方向
    let mut dir = state.direction;
    if state.velocity_x.abs() < 0.05 && rand.next_bool() {
        dir = -dir;
    }

    let vx_dir = direction_from_vx(state.velocity_x);
    let vy_dir = direction_from_vx(state.velocity_y);

    NpcDelta {
        velocity_x_sign: Some(if vx_dir != 0 {
            vx_dir as f32
        } else {
            dir as f32
        }),
        velocity_y_sign: Some(if vy_dir != 0 { vy_dir as f32 } else { 0.0 }),
        direction: Some(dir),
        speed_magnitude: Some(state.walk_speed.max(1.8)),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn bat_flies_with_target() {
        let s = NpcState {
            npc_type: 5,
            target: 0,
            ..Default::default()
        };

        let mut rand = Prng::new(7);
        let d = step(&s, &mut rand);
        assert!(d.velocity_x_sign.unwrap().abs() > 0.0);
    }

    #[test]
    fn bat_hovers_without_target() {
        let s = NpcState {
            npc_type: 5,
            target: -1,
            ..Default::default()
        };

        let mut rand = Prng::new(7);
        let d = step(&s, &mut rand);
        assert_eq!(d.velocity_x_sign, Some(0.0));
        assert_eq!(d.velocity_y_sign, Some(0.0));
    }
}
