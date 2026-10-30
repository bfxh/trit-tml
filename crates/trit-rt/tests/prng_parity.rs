//! 确定性 PRNG 的「黄金向量」parity 契约测试。
//!
//! 这组向量由 `examples/gen_golden.rs`（已删）用与 `prng.rs` 完全独立的实现算出，
//! 固化在此处作为**跨语言契约**：原生 Rust 内核与托管 C# 回退内核必须产出逐位
//! 相同的序列，否则影子差分就会失配。任何一端的 PRNG 改动都必须让这套向量继续通过。

use trit_rt::prng::DeterministicPrng;

/// (seed, tick, 连续 16 个 u64) 的黄金向量表。
struct Golden {
    seed: u64,
    tick: u64,
    values: [u64; 16],
}

fn golden() -> [Golden; 3] {
    [
        Golden {
            seed: 0x1234_5678_9ABC_DEF0,
            tick: 1,
            values: [
                0xD28D_7482_F52C_7BD4,
                0x05A0_8709_45E6_4887,
                0xE542_DEF2_5134_2FAD,
                0x8CA9_1C60_A638_36BD,
                0x05ED_4063_4EED_1BD3,
                0x9CCC_CE99_9FCE_7E23,
                0xB3B3_175C_2876_7A89,
                0x7C18_E55B_74FF_F984,
                0x440F_2EEC_1F3A_BD76,
                0xB6C5_6D3E_2808_1825,
                0x99CA_496E_9FA3_2EC9,
                0xB56D_B19D_CE1B_5083,
                0x0FD4_1E34_8D68_61E1,
                0xF2E8_9693_86B6_5D07,
                0x3AB1_69CD_05D2_0ED2,
                0xC275_F229_C034_C469,
            ],
        },
        Golden {
            seed: 0,
            tick: 0,
            values: [
                0xE220_A839_7B1D_CDAF,
                0x6E78_9E6A_A1B9_65F4,
                0x06C4_5D18_8009_454F,
                0xF88B_B8A8_724C_81EC,
                0x1B39_896A_51A8_749B,
                0x53CB_9F0C_747E_A2EA,
                0x2C82_9ABE_1F45_32E1,
                0xC584_133A_C916_AB3C,
                0x3EE5_7890_41C9_8AC3,
                0xF3B8_488C_368C_B0A6,
                0x657E_ECDD_3CB1_3D09,
                0xC2D3_26E0_055B_DEF6,
                0x8621_A03F_E0BB_DB7B,
                0x8E1F_7555_983A_A92F,
                0xB54E_0F16_00CC_4D19,
                0x84BB_3F97_971D_80AB,
            ],
        },
        Golden {
            seed: u64::MAX,
            tick: u64::MAX,
            values: [
                0x5DC2_0AA7_B2A2_7137,
                0xBDA5_668A_01D7_049C,
                0x82B4_3276_ABB8_0226,
                0xED4D_5ED4_A6EA_59B4,
                0x8306_445E_D348_A658,
                0x276E_BC0E_52F4_1C24,
                0xDEC5_7410_1132_9D07,
                0xB94E_C6A0_4D7B_4627,
                0x10AB_666F_6224_661C,
                0xE14C_0CBD_FEC8_973A,
                0xC8FF_AD89_6927_D011,
                0x020D_5B5F_CB50_2C79,
                0xFB00_2360_5FAB_BC5E,
                0xF865_BEC1_A08D_FA21,
                0x3E80_00C9_5CFF_E134,
                0x2190_E410_D398_0854,
            ],
        },
    ]
}

#[test]
fn golden_vectors_match_exactly() {
    for case in golden() {
        let mut prng = DeterministicPrng::new(0);
        prng.reseed(case.seed, case.tick);
        for (index, expected) in case.values.iter().enumerate() {
            let got = prng.next_u64();
            assert_eq!(
                got, *expected,
                "seed={:#018X} tick={} 第 {} 抽失配: 得到 {:#018X}",
                case.seed, case.tick, index, got
            );
        }
    }
}

#[test]
fn reseed_is_deterministic_and_order_independent_for_same_inputs() {
    // 同一个 (seed, tick) 重播种任意次都得同一序列。
    for case in golden() {
        let mut a = DeterministicPrng::new(0);
        a.reseed(case.seed, case.tick);
        let first: Vec<u64> = (0..8).map(|_| a.next_u64()).collect();

        let mut b = DeterministicPrng::new(0);
        b.reseed(case.seed, case.tick);
        let second: Vec<u64> = (0..8).map(|_| b.next_u64()).collect();

        assert_eq!(first, second, "重播种后序列必须可复现");
    }
}

#[test]
fn different_ticks_produce_disjoint_sequences() {
    // 相邻 tick 的输出必须不同（否则回放时跨 tick 串味）。
    let mut base = DeterministicPrng::new(7);
    base.reseed(0xABCDE, 1);
    let s1: Vec<u64> = (0..16).map(|_| base.next_u64()).collect();

    let mut other = DeterministicPrng::new(7);
    other.reseed(0xABCDE, 2);
    let s2: Vec<u64> = (0..16).map(|_| other.next_u64()).collect();

    assert_ne!(s1, s2, "相邻 tick 的输出不应相同");
}

#[test]
fn next_trit_uses_mod_three_consistently() {
    // NextTrit 必须严格等于 NextU64() % 3 的 0/1/2 映射，与 C# 完全对齐。
    // 用两个同种子、同抽取步数的 prng 锁步：一个走 next_trit，另一个走 next_u64，
    // 二者内部状态在每步后完全一致，从而验证 trit 映射用的是「同一次」抽取。
    use trit_core::value::Ternary;
    let mut trit_prng = DeterministicPrng::new(99);
    trit_prng.reseed(0x1234_5678_9ABC_DEF0, 1);
    let mut u64_prng = DeterministicPrng::new(99);
    u64_prng.reseed(0x1234_5678_9ABC_DEF0, 1);
    for _ in 0..256 {
        let trit = trit_prng.next_trit();
        let u = u64_prng.next_u64();
        let expected = match u % 3 {
            0 => Ternary::False,
            1 => Ternary::Unknown,
            _ => Ternary::True,
        };
        assert_eq!(trit, expected, "trit 映射必须与同次 u64 % 3 一致");
    }
}

#[test]
fn draws_counter_increments_each_pull() {
    let mut prng = DeterministicPrng::new(1);
    prng.reseed(1, 1);
    for expected in 1..=128u64 {
        let _ = prng.next_u64();
        assert_eq!(prng.draws(), expected);
    }
}
