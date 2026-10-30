#nullable enable

using TritRuntime.Kernel;
using TritRuntime.Wire;
using Xunit;

namespace TritRuntime.Shim.Tests;

/// <summary>
/// 确定性 PRNG 的「黄金向量」parity 契约测试。
///
/// 与 <c>trit-rt/tests/prng_parity.rs</c> 使用同一组固化向量：原生 Rust 内核与托管 C#
/// 回退内核必须产出逐位相同的 <c>(seed, tick)</c> 序列，否则影子差分会失配。任何一端
/// 改动 PRNG 都必须让这两套测试继续通过。
/// </summary>
public sealed class PrngParityTests
{
    // (seed, tick, 连续 16 个 u64)，与 Rust 端完全一致。
    private static readonly (ulong Seed, ulong Tick, ulong[] Values)[] Golden =
    [
        (
            0x1234_5678_9ABC_DEF0UL,
            1UL,
            new ulong[]
            {
                0xD28D_7482_F52C_7BD4, 0x05A0_8709_45E6_4887, 0xE542_DEF2_5134_2FAD, 0x8CA9_1C60_A638_36BD,
                0x05ED_4063_4EED_1BD3, 0x9CCC_CE99_9FCE_7E23, 0xB3B3_175C_2876_7A89, 0x7C18_E55B_74FF_F984,
                0x440F_2EEC_1F3A_BD76, 0xB6C5_6D3E_2808_1825, 0x99CA_496E_9FA3_2EC9, 0xB56D_B19D_CE1B_5083,
                0x0FD4_1E34_8D68_61E1, 0xF2E8_9693_86B6_5D07, 0x3AB1_69CD_05D2_0ED2, 0xC275_F229_C034_C469,
            }
        ),
        (
            0UL,
            0UL,
            new ulong[]
            {
                0xE220_A839_7B1D_CDAF, 0x6E78_9E6A_A1B9_65F4, 0x06C4_5D18_8009_454F, 0xF88B_B8A8_724C_81EC,
                0x1B39_896A_51A8_749B, 0x53CB_9F0C_747E_A2EA, 0x2C82_9ABE_1F45_32E1, 0xC584_133A_C916_AB3C,
                0x3EE5_7890_41C9_8AC3, 0xF3B8_488C_368C_B0A6, 0x657E_ECDD_3CB1_3D09, 0xC2D3_26E0_055B_DEF6,
                0x8621_A03F_E0BB_DB7B, 0x8E1F_7555_983A_A92F, 0xB54E_0F16_00CC_4D19, 0x84BB_3F97_971D_80AB,
            }
        ),
        (
            0xFFFF_FFFF_FFFF_FFFFUL,
            0xFFFF_FFFF_FFFF_FFFFUL,
            new ulong[]
            {
                0x5DC2_0AA7_B2A2_7137, 0xBDA5_668A_01D7_049C, 0x82B4_3276_ABB8_0226, 0xED4D_5ED4_A6EA_59B4,
                0x8306_445E_D348_A658, 0x276E_BC0E_52F4_1C24, 0xDEC5_7410_1132_9D07, 0xB94E_C6A0_4D7B_4627,
                0x10AB_666F_6224_661C, 0xE14C_0CBD_FEC8_973A, 0xC8FF_AD89_6927_D011, 0x020D_5B5F_CB50_2C79,
                0xFB00_2360_5FAB_BC5E, 0xF865_BEC1_A08D_FA21, 0x3E80_00C9_5CFF_E134, 0x2190_E410_D398_0854,
            }
        ),
    ];

    [Fact]
    public void GoldenVectorsMatchExactly()
    {
        foreach (var (seed, tick, values) in Golden)
        {
            var prng = new DeterministicPrng(0);
            prng.Reseed(seed, tick);
            for (var index = 0; index < values.Length; index++)
            {
                var got = prng.NextU64();
                Assert.True(values[index] == got,
                    $"seed={seed:X16} tick={tick} 第 {index} 抽失配: 期望 {values[index]:X16} 得到 {got:X16}");
            }
        }
    }

    [Fact]
    public void ReseedIsDeterministic()
    {
        foreach (var (seed, tick, _) in Golden)
        {
            var a = new DeterministicPrng(0);
            a.Reseed(seed, tick);
            var first = Enumerable.Range(0, 8).Select(_ => a.NextU64()).ToArray();

            var b = new DeterministicPrng(0);
            b.Reseed(seed, tick);
            var second = Enumerable.Range(0, 8).Select(_ => b.NextU64()).ToArray();

            Assert.Equal(first, second);
        }
    }

    [Fact]
    public void DifferentTicksProduceDisjointSequences()
    {
        var basePrng = new DeterministicPrng(7);
        basePrng.Reseed(0xABCDE, 1);
        var s1 = Enumerable.Range(0, 16).Select(_ => basePrng.NextU64()).ToArray();

        var other = new DeterministicPrng(7);
        other.Reseed(0xABCDE, 2);
        var s2 = Enumerable.Range(0, 16).Select(_ => other.NextU64()).ToArray();

        Assert.NotEqual(s1, s2);
    }

    [Fact]
    public void NextTritUsesModThreeConsistently()
    {
        // 两个同种子、同抽取步数的 prng 锁步：一个走 NextTrit，另一个走 NextU64，
        // 二者内部状态在每步后完全一致，从而验证 trit 映射用的是「同一次」抽取。
        var tritPrng = new DeterministicPrng(99);
        tritPrng.Reseed(0x1234_5678_9ABC_DEF0, 1);
        var u64Prng = new DeterministicPrng(99);
        u64Prng.Reseed(0x1234_5678_9ABC_DEF0, 1);
        for (var i = 0; i < 256; i++)
        {
            var trit = tritPrng.NextTrit();
            var u = u64Prng.NextU64();
            var expected = (u % 3) switch
            {
                0UL => TritDelta.Neg,
                1UL => TritDelta.Zero,
                _ => TritDelta.Pos,
            };
            Assert.Equal(expected, trit);
        }
    }

    [Fact]
    public void DrawsCounterIncrementsEachPull()
    {
        var prng = new DeterministicPrng(1);
        prng.Reseed(1, 1);
        for (ulong expected = 1; expected <= 128; expected++)
        {
            _ = prng.NextU64();
            Assert.Equal(expected, prng.Draws);
        }
    }
}
