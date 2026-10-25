using TritRuntime.Kernel;
using TritRuntime.Wire;
using Xunit;
using WireFormat = TritRuntime.Wire.Wire;

namespace TritRuntime.Shim.Tests;

/// <summary>
/// 线格式测试：C# 与 Rust 必须逐字节一致，否则影子差分会误报、
/// 跨 FFI 的请求也会被对端拒绝。这里把 5 trits/byte 打包的边界全部扫一遍。
/// </summary>
public sealed class WireTests
{
    [Fact]
    public void TritDeltaIndicesMatchRust()
    {
        Assert.Equal(0, (int)TritDelta.Neg);
        Assert.Equal(1, (int)TritDelta.Zero);
        Assert.Equal(2, (int)TritDelta.Pos);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(63)]
    [InlineData(64)]
    public void PackUnpackRoundTripsEveryLength(int count)
    {
        var values = Enumerable.Range(0, count)
            .Select(index => (TritDelta)(index % 3))
            .ToArray();

        var packed = WireFormat.PackTrits(values);
        Assert.Equal((count + 4) / 5, packed.Length);
        Assert.All(packed, value => Assert.True(value < WireFormat.Combinations, $"字节 {value} 超出 0..=242"));

        var restored = WireFormat.UnpackTrits(packed, count);
        Assert.Equal(values, restored);
    }

    [Fact]
    public void PackingUsesLittleEndianTritOrder()
    {
        // 首字节 = t0 + 3*t1 + 9*t2 + 27*t3 + 81*t4。
        var packed = WireFormat.PackTrits(new[] { TritDelta.Neg, TritDelta.Neg, TritDelta.Neg, TritDelta.Neg, TritDelta.Pos });
        Assert.Single(packed);
        Assert.Equal(162, packed[0]); // 2 * 3^4
    }

    [Fact]
    public void UnpackRejectsIllegalBytes()
    {
        var exception = Assert.Throws<WireException>(() => WireFormat.UnpackTrits(new byte[] { 243, 0 }, 5));
        Assert.Contains("243", exception.Message);
    }

    [Fact]
    public void UnpackRejectsTruncatedBuffer()
    {
        Assert.Throws<WireException>(() => WireFormat.UnpackTrits(new byte[] { 0 }, 10));
    }

    [Fact]
    public void FrameUnframeRoundTrips()
    {
        var payload = new byte[] { 1, 2, 3, 4, 5 };
        var framed = WireFormat.Frame(payload);
        Assert.Equal(4 + payload.Length, framed.Length);
        Assert.Equal(payload, WireFormat.Unframe(framed));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void UnframeRejectsShortBuffers(int length)
    {
        Assert.Null(WireFormat.Unframe(new byte[length]));
    }

    [Fact]
    public void UnframeRejectsLengthLargerThanBuffer()
    {
        var framed = new byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(framed, 100);
        Assert.Null(WireFormat.Unframe(framed));
    }

    [Fact]
    public void WriterReaderRoundTripAllPrimitives()
    {
        var bytes = new WireWriter()
            .U8(7)
            .U32(0xDEAD_BEEF)
            .I32(-5)
            .U64(0x0102_0304_0506_0708)
            .Bytes(new byte[] { 9, 8, 7 })
            .Trits(new[] { TritDelta.Neg, TritDelta.Zero, TritDelta.Pos })
            .ToArray();

        var reader = new WireReader(bytes);
        Assert.Equal(7, reader.U8());
        Assert.Equal(0xDEAD_BEEFu, reader.U32());
        Assert.Equal(-5, reader.I32());
        Assert.Equal(0x0102_0304_0506_0708ul, reader.U64());
        Assert.Equal(new byte[] { 9, 8, 7 }, reader.Bytes(3));
        Assert.Equal(
            new[] { TritDelta.Neg, TritDelta.Zero, TritDelta.Pos },
            reader.Trits());
        Assert.Equal(0, reader.Remaining);
    }

    [Fact]
    public void ReaderRejectsTruncation()
    {
        Assert.Throws<WireException>(() => new WireReader(new byte[] { 1, 2, 3 }).U64());
        Assert.Throws<WireException>(() => new WireReader(Array.Empty<byte>()).Trits());
    }

    [Fact]
    public void StatusCodesMatchRust()
    {
        Assert.Equal(0, Status.Ok);
        Assert.Equal(-1, Status.BadHandle);
        Assert.Equal(-2, Status.BadOp);
        Assert.Equal(-3, Status.Buffer);
        Assert.Equal(-4, Status.Capacity);
        Assert.Equal(-5, Status.Conflict);
        Assert.Equal(-6, Status.Panic);
        Assert.Equal(-7, Status.Decode);
        Assert.Equal(-8, Status.NotFound);
        Assert.Equal(-9, Status.State);
        Assert.Equal(-10, Status.Arg);
    }

    [Fact]
    public void OpcodeTableMatchesRust()
    {
        Assert.Equal(0x01u, Opcode.Subscribe);
        Assert.Equal(0x02u, Opcode.Unsubscribe);
        Assert.Equal(0x03u, Opcode.Publish);
        Assert.Equal(0x04u, Opcode.TxnBegin);
        Assert.Equal(0x05u, Opcode.TxnCommit);
        Assert.Equal(0x06u, Opcode.TxnRollback);
        Assert.Equal(0x07u, Opcode.TxnSubmit);
        Assert.Equal(0x08u, Opcode.Arbitrate);
        Assert.Equal(0x09u, Opcode.Query);
        Assert.Equal(0x0Au, Opcode.Step);
        Assert.Equal(0x0Bu, Opcode.Snapshot);
        Assert.Equal(0x0Cu, Opcode.Diff);
        Assert.Equal(0x0Du, Opcode.RollbackTo);
        Assert.Equal(0x0Eu, Opcode.PrngNext);
        Assert.Equal(0x0Fu, Opcode.ShadowRecord);
        Assert.Equal(0x10u, Opcode.Capabilities);
        Assert.Equal(0x11u, Opcode.Stats);
        Assert.Equal(0x12u, Opcode.AiVote);
        Assert.Equal(0x13u, Opcode.AiSetModel);
    }
}
