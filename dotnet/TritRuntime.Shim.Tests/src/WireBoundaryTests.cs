#nullable enable

using System.Buffers.Binary;
using TritRuntime.Wire;
using Xunit;
using WireFormat = TritRuntime.Wire.Wire;

namespace TritRuntime.Shim.Tests;

/// <summary>
/// 线格式解码的边界与异常契约：解码路径上的任何越界、负长度、u32 溢出、
/// 非法打包字节，都必须收敛成 <see cref="WireException"/>（→ <c>Status.Decode</c>），
/// 绝不能让 <c>OverflowException</c> / <c>ArgumentOutOfRangeException</c> /
/// <c>IndexOutOfRangeException</c> 穿透到模组调用方。
///
/// 与 <see cref="WireTests"/> 的分工：那边测「正确输入输出是否逐字节对齐 Rust」，
/// 这边测「错误输入是否以约定的方式失败」。
/// </summary>
public sealed class WireBoundaryTests
{
    /// <summary>
    /// Count32 的溢出边界：u32 只要在 int.MaxValue 之内就正常返回，超过就必须
    /// 抛 <see cref="WireException"/>——而不是 <c>checked((int)U32())</c> 的
    /// <see cref="OverflowException"/>（那不在 Submit 的捕获清单里）。
    /// </summary>
    [Theory]
    [InlineData(0u, false)]
    [InlineData(1u, false)]
    [InlineData(4096u, false)]
    [InlineData(int.MaxValue, false)]          // 恰好可表示
    [InlineData((uint)int.MaxValue + 1, true)] // 0x8000_0000：溢出
    [InlineData(0xFFFF_FFFEu, true)]
    [InlineData(0xFFFF_FFFFu, true)]           // 坏载荷最爱用的巨量 count
    public void Count32ClampsOverflowToWireException(uint raw, bool shouldThrow)
    {
        var payload = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, raw);
        var reader = new WireReader(payload);

        if (shouldThrow)
        {
            var exception = Record.Exception(() => reader.Count32());
            Assert.IsType<WireException>(exception);
        }
        else
        {
            Assert.Equal((int)raw, reader.Count32());
        }
    }

    /// <summary>Bytes 收到负长度必须抛 WireException，不能落到 Span/Slice 上抛参数异常。</summary>
    [Fact]
    public void BytesRejectsNegativeLengthAsWireException()
    {
        var reader = new WireReader(new byte[] { 1, 2, 3, 4 });
        var exception = Record.Exception(() => reader.Bytes(-1));
        Assert.IsType<WireException>(exception);
    }

    /// <summary>
    /// position 已推进后再读一个巨大长度：<c>position + count</c> 在 int 下会溢出回绕成负数，
    /// 绕过 <c>&gt; buffer.Length</c> 的判断，落到 Slice 抛 ArgumentOutOfRangeException。
    /// 与 <see cref="UnframeRejectsOversizedLengthWithoutThrowing"/> 是同一类「整数溢出绕过闸门」，
    /// 加法必须用 long 做。旧实现在这里会穿透非约定异常。
    /// </summary>
    [Fact]
    public void EnsureRejectsPositionPlusLengthOverflowAsWireException()
    {
        var reader = new WireReader(new byte[8]);
        reader.U32(); // position 推进到 4，制造 4 + int.MaxValue 的溢出
        var exception = Record.Exception(() => reader.Bytes(int.MaxValue));
        Assert.IsType<WireException>(exception);
    }

    /// <summary>Ensure 各字段越界读取都必须是 WireException，而不是索引/参数异常。</summary>
    [Theory]
    [InlineData("u8")]
    [InlineData("u32")]
    [InlineData("i32")]
    [InlineData("u64")]
    [InlineData("bytes")]
    [InlineData("trits")]
    public void TruncatedReadsAreAlwaysWireException(string field)
    {
        // 每种字段给一个「刚好不足」的缓冲区，逼出 Ensure 的越界分支。
        var reader = field switch
        {
            "u8" => new WireReader(Array.Empty<byte>()),
            "u32" => new WireReader(new byte[3]),
            "i32" => new WireReader(new byte[3]),
            "u64" => new WireReader(new byte[7]),
            "bytes" => new WireReader(new byte[3]),
            "trits" => new WireReader(Array.Empty<byte>()),
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };

        var exception = Record.Exception(() =>
        {
            switch (field)
            {
                case "u8": reader.U8(); break;
                case "u32": reader.U32(); break;
                case "i32": reader.I32(); break;
                case "u64": reader.U64(); break;
                case "bytes": reader.Bytes(4); break;
                case "trits": reader.Trits(); break;
            }
        });

        Assert.IsType<WireException>(exception);
    }

    /// <summary>
    /// Trits() 里 count 是 int.MaxValue 这种值时，(count+4)/5 可能在 unchecked 下
    /// 算成负数或巨值——两条都必须被 Ensure 挡成 WireException，绝不能穿透或申请巨量内存。
    /// </summary>
    [Theory]
    [InlineData(int.MaxValue)]
    [InlineData(1 << 30)]
    [InlineData(0x0001_0000)]
    public void TritsWithHugeCountFailsFastAsWireException(int count)
    {
        var payload = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(payload, count);
        var reader = new WireReader(payload);

        var exception = Record.Exception(() => reader.Trits());
        Assert.IsType<WireException>(exception);
    }

    /// <summary>UnpackTrits 的非法打包字节（243..255）一律 WireException。</summary>
    [Theory]
    [InlineData((byte)243)]
    [InlineData((byte)244)]
    [InlineData((byte)250)]
    [InlineData((byte)255)]
    public void UnpackRejectsEveryIllegalByte(byte illegal)
    {
        Assert.Throws<WireException>(() => WireFormat.UnpackTrits(new[] { illegal }, 1));
    }

    /// <summary>UnpackTrits 声明的 trit 数超过缓冲容量时必须 WireException。</summary>
    [Theory]
    [InlineData(6, 1)]
    [InlineData(11, 2)]
    [InlineData(1000, 3)]
    public void UnpackRejectsCountBeyondBufferCapacity(int count, int byteLength)
    {
        var buffer = new byte[byteLength];
        Assert.Throws<WireException>(() => WireFormat.UnpackTrits(buffer, count));
    }

    /// <summary>
    /// Unframe：长度字段谎报成 0xFFFF_FFFF 时必须返回 null（先比长度再 Slice），
    /// 不能让 (int)length 变成负数后落进 Slice 抛 ArgumentOutOfRangeException。
    /// </summary>
    [Theory]
    [InlineData(0xFFFF_FFFFu)]
    [InlineData(0x8000_0000u)]
    [InlineData(1000u)]
    public void UnframeRejectsOversizedLengthWithoutThrowing(uint declared)
    {
        var framed = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(framed, declared);

        byte[]? result = null;
        var exception = Record.Exception(() => result = WireFormat.Unframe(framed));

        Assert.Null(exception);
        Assert.Null(result);
    }

    /// <summary>Unframe 缓冲区尾部有多余字节时，按声明长度截取前面部分（忽略尾部）。</summary>
    [Fact]
    public void UnframeIgnoresTrailingBytesBeyondDeclaredLength()
    {
        var framed = new byte[4 + 2 + 3];
        BinaryPrimitives.WriteUInt32LittleEndian(framed, 2);
        framed[4] = 0xAA;
        framed[5] = 0xBB;
        framed[6] = 0xCC;
        framed[7] = 0xDD;
        framed[8] = 0xEE;

        Assert.Equal(new byte[] { 0xAA, 0xBB }, WireFormat.Unframe(framed));
    }

    /// <summary>Pack/Unpack 往返覆盖较长序列，钉死分块边界（含最后不足 5 个）。</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(31)]
    [InlineData(32)]
    [InlineData(33)]
    [InlineData(127)]
    [InlineData(256)]
    [InlineData(1000)]
    public void PackUnpackRoundTripsAcrossChunkBoundaries(int count)
    {
        var values = Enumerable.Range(0, count)
            .Select(index => (TritDelta)(index % 3))
            .ToArray();
        var packed = WireFormat.PackTrits(values);
        Assert.Equal((count + 4) / 5, packed.Length);
        Assert.Equal(values, WireFormat.UnpackTrits(packed, count));
    }

    /// <summary>
    /// 越界裸字节（3..255）解释成 trit 时必须收敛为 Zero/弃权，与 Rust
    /// <c>Ternary::from_index</c> 同语义——不能留成非法枚举值参与仲裁。
    /// </summary>
    [Theory]
    [InlineData((byte)0, TritDelta.Neg)]
    [InlineData((byte)1, TritDelta.Zero)]
    [InlineData((byte)2, TritDelta.Pos)]
    [InlineData((byte)3, TritDelta.Zero)]
    [InlineData((byte)7, TritDelta.Zero)]
    [InlineData((byte)42, TritDelta.Zero)]
    [InlineData((byte)128, TritDelta.Zero)]
    [InlineData((byte)254, TritDelta.Zero)]
    [InlineData((byte)255, TritDelta.Zero)]
    public void TritDecoderConvergesOutOfRangeBytesToAbstain(byte raw, TritDelta expected)
    {
        Assert.Equal(expected, TritDecoder.FromByte(raw));
    }
}
