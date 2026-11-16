//! Arrow —— 抛物线（重力影响 velocity.y）。

use crate::prng::Prng;
use crate::projectile::{ProjectileDelta, ProjectileState, direction_from_vx};

pub fn step(state: &ProjectileState, _rand: &mut Prng) -> ProjectileDelta {
    // Arrow 受 gravity 影响，velocity.y 方向会从负（向上飞）变成正（往下落）
    let vx_dir = direction_from_vx(state.velocity_x);
    let vy_dir = direction_from_vx(state.velocity_y);

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
}
