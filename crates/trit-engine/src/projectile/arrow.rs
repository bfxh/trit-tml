//! Arrow —— 抛物线（重力影响 velocity.y）。

use crate::prng::Prng;
use crate::projectile::{ProjectileDelta, ProjectileState, direction_from_vx};

pub fn step(state: &ProjectileState, _rand: &mut Prng) -> ProjectileDelta {
    // 抛物线弹幕每 tick 的 velocity.y 会被重力推进，越过顶点后符号翻转。
    // 预测「下一 tick 的符号」而不是镜像当前符号 —— 顶点附近两者不同，
    // 镜像会让对拍永远滞后一 tick。
    // 重力值来自状态快照（当前 runtime 固定 0.08，尚未按 type 采样）。
    let vx_dir = direction_from_vx(state.velocity_x);
    let vy_dir = direction_from_vx(state.velocity_y + state.gravity);

    ProjectileDelta {
        velocity_x_sign: Some(vx_dir as f32),
        velocity_y_sign: Some(vy_dir as f32),
        direction: Some(vx_dir),
        speed_magnitude: None,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn arrow_turns_down_after_apex() {
        let s = ProjectileState {
            proj_type: 2,
            velocity_x: 2.5,
            velocity_y: -1.0, // 上升阶段
            ..Default::default()
        };

        let mut rand = Prng::new(1);
        let d = step(&s, &mut rand);
        assert_eq!(d.velocity_x_sign, Some(1.0));
        assert_eq!(d.velocity_y_sign, Some(-1.0)); // 还在上升
    }

    #[test]
    fn arrow_flips_sign_when_gravity_crosses_apex() {
        // 顶点附近：vy = -0.05，gravity = 0.08 → 下一 tick 已转为下落。
        let s = ProjectileState {
            proj_type: 1,
            velocity_x: 3.0,
            velocity_y: -0.05,
            gravity: 0.08,
            ..Default::default()
        };

        let mut rand = Prng::new(1);
        let d = step(&s, &mut rand);
        assert_eq!(d.velocity_y_sign, Some(1.0));
    }

    #[test]
    fn arrow_without_gravity_mirrors_sign() {
        // gravity = 0 的弹幕不做预测，退回镜像当前符号。
        let s = ProjectileState {
            proj_type: 1,
            velocity_x: 3.0,
            velocity_y: -0.5,
            gravity: 0.0,
            ..Default::default()
        };

        let mut rand = Prng::new(1);
        let d = step(&s, &mut rand);
        assert_eq!(d.velocity_y_sign, Some(-1.0));
    }
}
