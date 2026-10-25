using TritRuntime.Wire;

namespace TritRuntime.Kernel;

/// <summary>
/// 确定性 PRNG：splitmix64，与 Rust <c>trit_rt::prng::DeterministicPrng</c> 使用
/// **完全相同的混合常数与 wrapping 运算**，因此同一 <c>(seed, tick)</c> 在原生内核
/// 与托管回退内核上产出逐位相同的序列——回放确定性的前提。
/// </summary>
public sealed class DeterministicPrng
{
    private const ulong GoldenGamma = 0x9E37_79B9_7F4A_7C15;
    private const ulong Mix1 = 0xBF58_476D_1CE4_E5B9;
    private const ulong Mix2 = 0x94D0_49BB_1331_11EB;

    private ulong state;
    private ulong draws;

    /// <summary>用种子构造；<c>seed == 0</c> 也合法（splitmix64 对零种子不退化）。</summary>
    public DeterministicPrng(ulong seed)
    {
        state = seed;
    }

    /// <summary>用 <c>(seed, tick)</c> 重新播种；tick 相同 ⇒ 整个序列相同。</summary>
    public void Reseed(ulong seed, ulong tick)
    {
        var z = seed ^ (tick * GoldenGamma);
        z = (z ^ (z >> 30)) * Mix1;
        z = (z ^ (z >> 27)) * Mix2;
        state = z ^ (z >> 31);
    }

    /// <summary>下一个 64 位随机数。</summary>
    public ulong NextU64()
    {
        state += GoldenGamma;
        var z = state;
        z = (z ^ (z >> 30)) * Mix1;
        z = (z ^ (z >> 27)) * Mix2;
        draws += 1;
        return z ^ (z >> 31);
    }

    /// <summary>平衡三进制抽样：<c>Neg(-1) / Zero(0) / Pos(+1)</c>，各 1/3。</summary>
    public TritDelta NextTrit()
    {
        var value = NextU64() % 3;
        return value switch
        {
            0 => TritDelta.Neg,
            1 => TritDelta.Zero,
            _ => TritDelta.Pos,
        };
    }

    /// <summary>连续抽 <paramref name="count"/> 个 trit。</summary>
    public TritDelta[] FillTrits(int count)
    {
        var values = new TritDelta[count];
        for (var i = 0; i < count; i++)
        {
            values[i] = NextTrit();
        }

        return values;
    }

    /// <summary>已抽取次数（统计用）。</summary>
    public ulong Draws => draws;
}
