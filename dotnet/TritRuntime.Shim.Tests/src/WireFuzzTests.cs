#nullable enable

using System;
using TritRuntime.Kernel;
using TritRuntime.Wire;
using Xunit;

using WireFormat = TritRuntime.Wire.Wire;
using WireDecoder = TritRuntime.Wire.TritDecoder;

namespace TritRuntime.Shim.Tests;

/// <summary>
/// 线格式编解码的「模糊往返」测试：用固定种子 PRNG 喂入数千个随机载荷，
/// 验证 <see cref="WireFormat.PackTrits"/> / <see cref="WireFormat.UnpackTrits"/> 的 trits 往返、
/// <see cref="WireFormat.Frame"/> / <see cref="WireFormat.Unframe"/> 的响应往返、单字节 trit 收敛幂等，
/// 以及畸形帧返回 <c>null</c> 而不是抛异常。与 Rust 侧 <c>tests/wire_roundtrip.rs</c> 对称。
/// </summary>
public sealed class WireFuzzTests
{
    private static TritDelta RandomTrit(Random rng) => (TritDelta)rng.Next(0, 3);

    [Fact]
    public void TritsRoundTripAcrossThousandsOfPayloads()
    {
        var rng = new Random(0xBEEF);
        for (var iter = 0; iter < 3000; iter++)
        {
            var len = rng.Next(0, 600);
            var expected = new TritDelta[len];
            for (var i = 0; i < len; i++)
            {
                expected[i] = RandomTrit(rng);
            }

            var bytes = WireFormat.PackTrits(expected);
            var got = WireFormat.UnpackTrits(bytes, len);
            Assert.Equal(expected, got);
        }
    }

    [Fact]
    public void FrameUnframeRoundTripAcrossThousandsOfPayloads()
    {
        var rng = new Random(0xCAFE);
        for (var iter = 0; iter < 3000; iter++)
        {
            var len = rng.Next(0, 2000);
            var payload = new byte[len];
            rng.NextBytes(payload);

            var framed = WireFormat.Frame(payload);
            var unframed = WireFormat.Unframe(framed);
            Assert.NotNull(unframed);
            Assert.Equal(payload, unframed);
        }
    }

    [Fact]
    public void SingleByteTritConvergenceIsIdempotent()
    {
        for (var b = 0; b <= 255; b++)
        {
            var trit = WireDecoder.FromByte((byte)b);
            var reconverged = WireDecoder.FromByte((byte)((int)trit));
            Assert.Equal(trit, reconverged);
            if (b <= 2)
            {
                Assert.Equal((TritDelta)b, trit);
            }
        }
    }

    [Fact]
    public void MalformedFramesReturnNullNotThrow()
    {
        Assert.Null(WireFormat.Unframe(Array.Empty<byte>()));
        Assert.Null(WireFormat.Unframe(new byte[] { 1, 2, 3 }));

        // 声明长度溢出：u32 = 0xFFFFFFF0。
        var framed = new byte[] { 0xFF, 0xFF, 0xFF, 0xF0, 1, 2, 3 };
        Assert.Null(WireFormat.Unframe(framed));

        // 声明长度比实际多 1 字节。
        var payload = new byte[] { 9, 8, 7, 6, 5 };
        var good = WireFormat.Frame(payload);
        good[3] += 1;
        Assert.Null(WireFormat.Unframe(good));
    }

    [Fact]
    public void ReaderOverrunReturnsDecodeErrorNotThrow()
    {
        // 声明 100 个 trit 但只给 1 字节（最多 5 个 trit）。
        using var ms = new System.IO.MemoryStream();
        var w = new WireWriter();
        w.U32(100).Bytes(new byte[] { 0 });
        var bytes = w.ToArray();
        var r = new WireReader(bytes);
        Assert.Throws<WireException>(() => r.Trits());

        // 截断的原语读取。
        var w2 = new WireWriter();
        w2.U8(1).U32(2);
        var r2 = new WireReader(w2.ToArray());
        _ = r2.U8();
        _ = r2.U32();
        Assert.Throws<WireException>(() => r2.U64());
    }

    [Fact]
    public void Count32RejectsOverlargeUintAsDecode()
    {
        // u32 = 0xFFFFFFFF 超过 int.MaxValue → 必须抛 WireException（Decode），不能 OverflowException。
        var w = new WireWriter();
        w.U32(0xFFFF_FFFF);
        var r = new WireReader(w.ToArray());
        Assert.Throws<WireException>(() => r.Count32());
    }
}
