//! 确定性 PRNG：splitmix64，每次 `step(tick)` 用 `(seed, tick)` 重新播种。
//!
//! 回放确定性的关键不在算法强度，而在「同一 tick 内同一顺序的调用必然得到
//! 同一序列」。因此这里刻意选择可逐位移植到 C# 的实现（同样的混合常数、
//! 同样的 wrapping 运算、同样的取模映射），托管回退运行时才能与原生运行时
//! 在影子差分中逐字节一致。

use trit_core::value::Ternary;

/// splitmix64 确定性伪随机数发生器。
#[derive(Debug, Clone)]
pub struct DeterministicPrng {
    state: u64,
    /// 已抽取的 64 位数个数（统计用）。
    draws: u64,
}

impl DeterministicPrng {
    /// 用种子构造；`seed == 0` 也合法（splitmix64 对零种子不退化）。
    pub fn new(seed: u64) -> Self {
        Self {
            state: seed,
            draws: 0,
        }
    }

    /// 用 `(seed, tick)` 重新播种。tick 相同 ⇒ 整个序列相同。
    pub fn reseed(&mut self, seed: u64, tick: u64) {
        // 先把 (seed, tick) 混合成一个高质量状态，避免相邻 tick 的输出相关。
        let mut z = seed ^ tick.wrapping_mul(0x9E37_79B9_7F4A_7C15);
        z = (z ^ (z >> 30)).wrapping_mul(0xBF58_476D_1CE4_E5B9);
        z = (z ^ (z >> 27)).wrapping_mul(0x94D0_49BB_1331_11EB);
        self.state = z ^ (z >> 31);
    }

    /// 下一个 64 位随机数。
    pub fn next_u64(&mut self) -> u64 {
        self.state = self.state.wrapping_add(0x9E37_79B9_7F4A_7C15);
        let mut z = self.state;
        z = (z ^ (z >> 30)).wrapping_mul(0xBF58_476D_1CE4_E5B9);
        z = (z ^ (z >> 27)).wrapping_mul(0x94D0_49BB_1331_11EB);
        self.draws = self.draws.wrapping_add(1);
        z ^ (z >> 31)
    }

    /// 平衡三进制抽样：`False(-1) / Unknown(0) / True(+1)`，各 1/3。
    pub fn next_trit(&mut self) -> Ternary {
        match self.next_u64() % 3 {
            0 => Ternary::False,
            1 => Ternary::Unknown,
            _ => Ternary::True,
        }
    }

    /// 连续抽 `n` 个 trit。
    pub fn fill_trits(&mut self, n: usize) -> Vec<Ternary> {
        (0..n).map(|_| self.next_trit()).collect()
    }

    /// 已抽取次数（统计用）。
    pub fn draws(&self) -> u64 {
        self.draws
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn same_seed_and_tick_replays_identically() {
        let mut a = DeterministicPrng::new(42);
        let mut b = DeterministicPrng::new(42);
        a.reseed(42, 7);
        b.reseed(42, 7);
        let left: Vec<Ternary> = a.fill_trits(64);
        let right: Vec<Ternary> = b.fill_trits(64);
        assert_eq!(left, right);
    }

    #[test]
    fn different_ticks_diverge() {
        let mut a = DeterministicPrng::new(42);
        let mut b = DeterministicPrng::new(42);
        a.reseed(42, 7);
        b.reseed(42, 8);
        assert_ne!(a.fill_trits(16), b.fill_trits(16));
    }

    #[test]
    fn trit_distribution_is_roughly_uniform() {
        let mut prng = DeterministicPrng::new(1);
        let mut counts = [0usize; 3];
        for _ in 0..30_000 {
            counts[prng.next_trit().index()] += 1;
        }
        for count in counts {
            assert!((9_000..11_000).contains(&count), "分布偏离：{counts:?}");
        }
    }

    #[test]
    fn zero_seed_does_not_degenerate() {
        // 零种子 (0,0) 不应一路输出 0。
        let mut prng = DeterministicPrng::new(0);
        prng.reseed(0, 0);
        assert_ne!(prng.next_u64(), 0);
        // 同一零种子两次独立构造序列一致。
        let mut a = DeterministicPrng::new(0);
        let mut b = DeterministicPrng::new(0);
        a.reseed(0, 0);
        b.reseed(0, 0);
        assert_eq!(a.next_u64(), b.next_u64());
    }

    #[test]
    fn reseed_restarts_same_sequence() {
        let mut prng = DeterministicPrng::new(7);
        prng.reseed(123, 4);
        let first: Vec<u64> = (0..8).map(|_| prng.next_u64()).collect();
        prng.reseed(123, 4);
        let second: Vec<u64> = (0..8).map(|_| prng.next_u64()).collect();
        assert_eq!(first, second);
    }

    #[test]
    fn next_u64_is_reproducible() {
        let mut prng = DeterministicPrng::new(11);
        prng.reseed(11, 1);
        let a: Vec<u64> = (0..16).map(|_| prng.next_u64()).collect();
        let mut prng2 = DeterministicPrng::new(11);
        prng2.reseed(11, 1);
        let b: Vec<u64> = (0..16).map(|_| prng2.next_u64()).collect();
        assert_eq!(a, b);
    }

    #[test]
    fn fill_trits_zero_returns_empty() {
        let mut prng = DeterministicPrng::new(1);
        prng.reseed(1, 1);
        assert!(prng.fill_trits(0).is_empty());
    }

    #[test]
    fn draws_counter_increments() {
        let mut prng = DeterministicPrng::new(1);
        prng.reseed(1, 1);
        assert_eq!(prng.draws(), 0);
        let _ = prng.next_u64();
        let _ = prng.next_u64();
        assert_eq!(prng.draws(), 2);
    }

    #[test]
    fn adjacent_seeds_diverge_immediately() {
        let mut a = DeterministicPrng::new(0);
        let mut b = DeterministicPrng::new(1);
        a.reseed(0, 1);
        b.reseed(1, 1);
        assert_ne!(a.next_u64(), b.next_u64());
    }
}
