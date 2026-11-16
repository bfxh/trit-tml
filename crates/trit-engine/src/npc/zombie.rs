//! Zombie family AI —— Terraria 原版地面追击逻辑核心路径。
//!
//! Terraria.NPC.AI 里 Zombie 的简化逻辑：
//! 1. 朝追踪玩家方向移动
//! 2. 脚下悬空时主动跳（跳过沟壑）
//! 3. 贴脸时小跳
//! 4. 落地后继续追击

use crate::npc::{NpcDelta, NpcState, direction_from_vx};
use crate::prng::Prng;

pub fn step(state: &NpcState, _rand: &mut Prng) -> NpcDelta {
    if state.target < 0 {
        // 没目标 → 原地站
        return NpcDelta {
            velocity_x_sign: Some(0.0),
            velocity_y_sign: Some(0.0),
            direction: Some(state.direction),
            speed_magnitude: None,
        };
    }

    if !state.on_ground {
        // 在空中 —— 保持水平方向，垂直按当前速度符号下落/上升
        let vx_dir = direction_from_vx(state.velocity_x);
        let vy_dir = direction_from_vx(state.velocity_y);
        return NpcDelta {
            velocity_x_sign: Some(if vx_dir != 0 {
                vx_dir as f32
            } else {
                state.direction as f32
            }),
            velocity_y_sign: Some(if vy_dir > 0 { 1.0 } else { -1.0 }),
            direction: Some(state.direction),
            speed_magnitude: None,
        };
    }

    // 在地上 —— 沿当前朝向追击；需要跳时给向上的垂直速度
    let dir_f = state.direction as f32;
    let vy_sign = if !state.tile_below || state.target_distance < 50.0 {
        -1.0 // 脚下悬空 / 贴脸 → 跳
    } else {
        0.0
    };

    NpcDelta {
        velocity_x_sign: Some(dir_f),
        velocity_y_sign: Some(vy_sign),
        direction: Some(state.direction),
        speed_magnitude: Some(state.walk_speed),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn zombie_chases_on_ground() {
        let s = NpcState {
            npc_type: 3,
            on_ground: true,
            tile_below: true,
            target: 0,
            target_distance: 300.0,
            ..Default::default()
        };

        let mut rand = Prng::new(1);
        let d = step(&s, &mut rand);
        assert!(d.velocity_x_sign.unwrap().abs() > 0.0);
        assert_eq!(d.velocity_y_sign, Some(0.0));
    }

    #[test]
    fn zombie_jumps_over_ledge() {
        let s = NpcState {
            npc_type: 3,
            on_ground: true,
            tile_below: false,
            target: 0,
            ..Default::default()
        };

        let mut rand = Prng::new(1);
        let d = step(&s, &mut rand);
        assert_eq!(d.velocity_y_sign, Some(-1.0)); // 向上跳
    }

    #[test]
    fn zombie_stops_without_target() {
        let s = NpcState {
            npc_type: 3,
            target: -1,
            ..Default::default()
        };

        let mut rand = Prng::new(1);
        let d = step(&s, &mut rand);
        assert_eq!(d.velocity_x_sign, Some(0.0));
        assert_eq!(d.velocity_y_sign, Some(0.0));
    }
}
