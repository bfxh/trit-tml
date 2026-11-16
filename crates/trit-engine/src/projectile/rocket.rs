//! Rocket —— 追踪弹（AI[0] 倒计时 + 目标方向修正）。

use crate::prng::Prng;
use crate::projectile::{ProjectileDelta, ProjectileState, direction_from_vx};

pub fn step(state: &ProjectileState, _rand: &mut Prng) -> ProjectileDelta {
    let vx_dir = direction_from_vx(state.velocity_x);
    let vy_dir = direction_from_vx(state.velocity_y);

    ProjectileDelta {
        velocity_x_sign: Some(if vx_dir != 0 {
            vx_dir as f32
        } else {
            state.direction as f32
        }),
        velocity_y_sign: Some(vy_dir as f32),
        direction: Some(if vx_dir != 0 { vx_dir } else { state.direction }),
        speed_magnitude: None,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn rocket_maintains_direction() {
        let s = ProjectileState {
            proj_type: 3,
            velocity_x: 4.0,
            ..Default::default()
        };

        let mut rand = Prng::new(1);
        let d = step(&s, &mut rand);
        assert_eq!(d.velocity_x_sign, Some(1.0));
    }
}
