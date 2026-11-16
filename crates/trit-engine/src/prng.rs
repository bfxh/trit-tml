//! 确定性 PRNG —— 引擎必须可复现。
//!
//! Terraria 用 `System.Random`（xorshift+ 或类似）。MVP 用一个简单的
//! splitmix64 实现 —— 种子相同则输出序列完全确定。

/// 确定性伪随机数生成器。
#[derive(Debug, Clone)]
pub struct Prng {
    state: u64,
}

impl Prng {
    /// 从种子创建。
    pub fn new(seed: u64) -> Self {
        Self { state: seed }
    }

    /// 下一个 u64。
    #[inline]
    pub fn next_u64(&mut self) -> u64 {
        // splitmix64：确定性、速度快、统计质量够用
        let mut z = self.state.wrapping_add(0x9E37_79B9_7F4A_7C15);
        z = (z ^ (z >> 30)).wrapping_mul(0xBF58_476D_1CE4_E5B9);
        z = (z ^ (z >> 27)).wrapping_mul(0x94D0_49BB_1331_11EB);
        z ^= z >> 31;
        self.state = z;
        z
    }

    /// 下一个 i32，范围 [min, max]（两端都包含）。模拟 `Main.rand.Next(min, max + 1)`。
    pub fn next_i32(&mut self, min: i32, max: i32) -> i32 {
        if min >= max {
            return min;
        }
        let range = (max - min + 1) as u64;
        let r = self.next_u64() % range;
        min + r as i32
    }

    /// 下一个 f32，范围 [0.0, 1.0)。
    pub fn next_f32(&mut self) -> f32 {
        (self.next_u64() >> 11) as f32 / (1u64 << 53) as f32
    }

    /// 50% 概率 —— 模拟 `Main.rand.NextBool()`。
    pub fn next_bool(&mut self) -> bool {
        (self.next_u64() & 1) != 0
    }

    /// 重新播种。
    pub fn reseed(&mut self, seed: u64) {
        self.state = seed;
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn same_seed_same_sequence() {
        let mut a = Prng::new(42);
        let mut b = Prng::new(42);
        for _ in 0..100 {
            assert_eq!(a.next_u64(), b.next_u64());
        }
    }

    #[test]
    fn next_i32_in_range() {
        let mut r = Prng::new(12345);
        for _ in 0..1000 {
            let v = r.next_i32(1, 6);
            assert!((1..=6).contains(&v));
        }
    }

    #[test]
    fn next_f32_in_range() {
        let mut r = Prng::new(7);
        for _ in 0..1000 {
            let v = r.next_f32();
            assert!((0.0..1.0).contains(&v));
        }
    }
}
