//! Basic bullet —— 直线飞行（无重力）。

use crate::prng::Prng;
use crate::projectile::{ProjectileDelta, ProjectileState, direction_from_vx};

pub fn step(state: &ProjectileState, _rand: &mut Prng) -> ProjectileDelta {
    // Bullet 保持发射方向，速度逐渐衰减（但 KernelFirst 只关方向门，衰减不影响 sign）
    let vx_dir = direction_from_vx(state.velocity_x);
    let vy_dir = direction_from_vx(state.velocity_y);

    ProjectileDelta {
        velocity_x_sign: Some(vx_dir as f32),
        velocity_y_sign: Some(vy_dir as f32),
        direction: Some(if vx_dir != 0 { vx_dir } else { state.direction }),
        speed_magnitude: None,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn bullet_maintains_direction() {
        let s = ProjectileState {
            proj_type: 1,
            velocity_x: 3.0,
            velocity_y: 0.0,
            ..Default::default()
        };

        let mut rand = Prng::new(1);
        let d = step(&s, &mut rand);
        assert_eq!(d.velocity_x_sign, Some(1.0));
        assert_eq!(d.velocity_y_sign, Some(0.0));
    }
}
