#nullable enable

using System.Collections.Generic;
using TritRuntime.Kernel;
using TritRuntime.Wire;
using Xunit;

namespace TritRuntime.Shim.Tests;

/// <summary>
/// 托管确定性 PRNG 的属性测试，与 <c>trit-rt/src/prng.rs</c> 的单元测试镜像。
/// 验证零种子不退化、reseed 重置、序列可复现、trit 映射、分布与计数等不变量。
/// </summary>
public class DeterministicPrngTests
{
    [Fact]
    public void ZeroSeedDoesNotDegenerate()
    {
        var prng = new DeterministicPrng(0);
        prng.Reseed(0, 0);
        Assert.NotEqual(0UL, prng.NextU64());

        var a = new DeterministicPrng(0);
        var b = new DeterministicPrng(0);
        a.Reseed(0, 0);
        b.Reseed(0, 0);
        Assert.Equal(a.NextU64(), b.NextU64());
    }

    [Fact]
    public void ReseedRestartsSameSequence()
    {
        var prng = new DeterministicPrng(7);
        prng.Reseed(123, 4);
        var first = new List<ulong>();
        for (var i = 0; i < 8; i++) first.Add(prng.NextU64());
        prng.Reseed(123, 4);
        var second = new List<ulong>();
        for (var i = 0; i < 8; i++) second.Add(prng.NextU64());
        Assert.Equal(first, second);
    }

    [Fact]
    public void NextU64IsReproducible()
    {
        var a = new DeterministicPrng(11);
        a.Reseed(11, 1);
        var seqA = new List<ulong>();
        for (var i = 0; i < 16; i++) seqA.Add(a.NextU64());

        var b = new DeterministicPrng(11);
        b.Reseed(11, 1);
        var seqB = new List<ulong>();
        for (var i = 0; i < 16; i++) seqB.Add(b.NextU64());

        Assert.Equal(seqA, seqB);
    }

    [Fact]
    public void FillTritsZeroReturnsEmpty()
    {
        var prng = new DeterministicPrng(1);
        prng.Reseed(1, 1);
        Assert.Empty(prng.FillTrits(0));
    }

    [Fact]
    public void DrawsCounterIncrements()
    {
        var prng = new DeterministicPrng(1);
        prng.Reseed(1, 1);
        Assert.Equal(0UL, prng.Draws);
        _ = prng.NextU64();
        _ = prng.NextU64();
        Assert.Equal(2UL, prng.Draws);
    }

    [Fact]
    public void AdjacentSeedsDivergeImmediately()
    {
        var a = new DeterministicPrng(0);
        var b = new DeterministicPrng(1);
        a.Reseed(0, 1);
        b.Reseed(1, 1);
        Assert.NotEqual(a.NextU64(), b.NextU64());
    }

    [Fact]
    public void NextTritMatchesModThreeOfNextU64()
    {
        // NextTrit 必须严格等于 NextU64() % 3 的 0/1/2 映射，与 Rust 端对齐。
        var tritPrng = new DeterministicPrng(99);
        var u64Prng = new DeterministicPrng(99);
        tritPrng.Reseed(0x1234_5678_9ABC_DEF0, 1);
        u64Prng.Reseed(0x1234_5678_9ABC_DEF0, 1);
        for (var i = 0; i < 256; i++)
        {
            var u = u64Prng.NextU64();
            var trit = tritPrng.NextTrit();
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
    public void TritDistributionIsRoughlyUniform()
    {
        var prng = new DeterministicPrng(1);
        prng.Reseed(1, 1);
        var counts = new long[3];
        for (var i = 0; i < 30_000; i++)
        {
            counts[(int)(prng.NextU64() % 3)]++;
        }
        foreach (var c in counts)
        {
            Assert.InRange(c, 9000, 11000);
        }
    }
}
