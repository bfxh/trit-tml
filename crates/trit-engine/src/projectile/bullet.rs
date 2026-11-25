//! Basic bullet —— 直线飞行（无重力）。

use crate::prng::Prng;
use crate::projectile::{ProjectileDelta, ProjectileState, direction_from_vx};

pub fn step(state: &ProjectileState, _rand: &mut Prng) -> ProjectileDelta {
    // 无重力直线：velocity 每 tick 不变，方向即当前方向。
    // 追踪（44 Demon Sickle）/ 弹跳（258 Golem Fireball）没有单独建模，
    // 由注册表注释标注为近似。
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
