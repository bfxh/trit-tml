using System.Buffers.Binary;
using System.Text;

namespace TritRuntime.Wire;

/// <summary>三进制增量语义：Neg = -1（减少/移除）、Zero = 0（不变/弃权）、Pos = +1（增加/设置）。</summary>
public enum TritDelta
{
    /// <summary>-1：减少/移除。</summary>
    Neg = 0,

    /// <summary>0：不变/弃权/未驱动（总线高阻）。</summary>
    Zero = 1,

    /// <summary>+1：增加/设置。</summary>
    Pos = 2,
}

/// <summary>opcode 表，必须与 Rust 侧 <c>trit_rt::wire::op</c> 逐项对齐。</summary>
public static class Opcode
{
    /// <summary>订阅事件。</summary>
    public const uint Subscribe = 0x01;

    /// <summary>退订。</summary>
    public const uint Unsubscribe = 0x02;

    /// <summary>发布事件。</summary>
    public const uint Publish = 0x03;

    /// <summary>开事务。</summary>
    public const uint TxnBegin = 0x04;

    /// <summary>提交事务。</summary>
    public const uint TxnCommit = 0x05;

    /// <summary>回滚事务。</summary>
    public const uint TxnRollback = 0x06;

    /// <summary>事务内提交意图（txnId == 0 表示自动提交）。</summary>
    public const uint TxnSubmit = 0x07;

    /// <summary>仲裁一批意图（只判不落地）。</summary>
    public const uint Arbitrate = 0x08;

    /// <summary>查询。</summary>
    public const uint Query = 0x09;

    /// <summary>推进一个 tick。</summary>
    public const uint Step = 0x0A;

    /// <summary>打快照。</summary>
    public const uint Snapshot = 0x0B;

    /// <summary>差分两个快照。</summary>
    public const uint Diff = 0x0C;

    /// <summary>回滚到快照。</summary>
    public const uint RollbackTo = 0x0D;

    /// <summary>抽确定性随机数。</summary>
    public const uint PrngNext = 0x0E;

    /// <summary>记录影子差分签名。</summary>
    public const uint ShadowRecord = 0x0F;

    /// <summary>能力查询。</summary>
    public const uint Capabilities = 0x10;

    /// <summary>统计查询。</summary>
    public const uint Stats = 0x11;

    /// <summary>AI 决策投票。</summary>
    public const uint AiVote = 0x12;

    /// <summary>装载三值权重网络模型。</summary>
    public const uint AiSetModel = 0x13;

    /// <summary>提交并行扫描任务。</summary>
    public const uint ScanSubmit = 0x14;

    /// <summary>取出已完成任务。</summary>
    public const uint JobPoll = 0x15;

    /// <summary>协作式取消任务。</summary>
    public const uint JobCancel = 0x16;

    /// <summary>查询或切换 GPU 后端状态。</summary>
    public const uint GpuStatus = 0x17;

    /// <summary>查询全局资源预算。</summary>
    public const uint Budget = 0x18;

    /// <summary>查询任务池 Prometheus 指标。</summary>
    public const uint Metrics = 0x19;
}

/// <summary>查询编号，与 Rust 侧 <c>trit_rt::wire::query</c> 对齐。</summary>
public static class QueryId
{
    /// <summary>本 tick 待派发订阅数 → u32。</summary>
    public const uint DirtyCount = 0;

    /// <summary>世界车道数 → u32。</summary>
    public const uint WorldSize = 1;

    /// <summary>事务日志长度 → u64。</summary>
    public const uint LogLen = 2;

    /// <summary>快照数 → u32。</summary>
    public const uint SnapshotCount = 3;

    /// <summary>未关闭事务数 → u32。</summary>
    public const uint OpenTxnCount = 4;

    /// <summary>影子统计 → compared u64, divergences u64。</summary>
    public const uint Shadow = 5;

    /// <summary>某事件最近一次发布的载荷 → trits。</summary>
    public const uint EventPayload = 6;
}

/// <summary>能力位，与 Rust 侧 <c>trit_rt::wire::caps</c> 对齐。</summary>
[Flags]
public enum CapabilityBits : ulong
{
    /// <summary>无。</summary>
    None = 0,

    /// <summary>事件路由（依赖图 + 脏标记 + 批处理）。</summary>
    Router = 1UL << 0,

    /// <summary>三进制事务日志。</summary>
    TxnLog = 1UL << 1,

    /// <summary>快照。</summary>
    Snapshot = 1UL << 2,

    /// <summary>差分。</summary>
    Diff = 1UL << 3,

    /// <summary>冲突仲裁。</summary>
    Arbitrate = 1UL << 4,

    /// <summary>确定性 PRNG。</summary>
    Prng = 1UL << 5,

    /// <summary>序列化编解码。</summary>
    Codec = 1UL << 6,

    /// <summary>影子差分。</summary>
    Shadow = 1UL << 7,

    /// <summary>AI 决策投票。</summary>
    Ai = 1UL << 8,

    /// <summary>并行任务池。</summary>
    Parallel = 1UL << 9,

    /// <summary>GPU 后端运行时可用。</summary>
    Gpu = 1UL << 10,

    /// <summary>全局资源预算。</summary>
    Budget = 1UL << 11,
}

/// <summary>C ABI 状态码，与 Rust 侧 <c>trit_rt::ffi::TT_*</c> 对齐。</summary>
public static class Status
{
    /// <summary>成功。</summary>
    public const int Ok = 0;

    /// <summary>句柄为空或不存在。</summary>
    public const int BadHandle = -1;

    /// <summary>opcode 为 0 或保留值。</summary>
    public const int BadOp = -2;

    /// <summary>输出缓冲区不足；所需长度已写入 out 前 8 字节。</summary>
    public const int Buffer = -3;

    /// <summary>超过容量上限。</summary>
    public const int Capacity = -4;

    /// <summary>冲突仲裁未通过；争议诊断已写入 out。</summary>
    public const int Conflict = -5;

    /// <summary>Rust 侧 panic 被拦住。</summary>
    public const int Panic = -6;

    /// <summary>线格式解码失败。</summary>
    public const int Decode = -7;

    /// <summary>事务 / 快照 / 订阅不存在。</summary>
    public const int NotFound = -8;

    /// <summary>事务状态非法。</summary>
    public const int State = -9;

    /// <summary>参数非法。</summary>
    public const int Arg = -10;
}

/// <summary>仲裁模式。</summary>
public enum ArbitrationMode
{
    /// <summary>严格：同车道两个及以上驱动即争议。</summary>
    Strict = 0,

    /// <summary>多数：-1/+1 计票，平局争议。</summary>
    Majority = 1,
}

/// <summary>线格式读写器：小端整数 + 5 trits/byte 打包体（与 Rust trit-codec 逐字节一致）。</summary>
public static class Wire
{
    /// <summary>每字节 trit 数（3^5 = 243 ≤ 256）。</summary>
    public const int TritsPerByte = 5;

    /// <summary>单字节可表示的最大合法值（0..=242）。</summary>
    public const int Combinations = 243;

    /// <summary>把 trit 序列打包成字节；最后不足 5 个的低位补 Neg（解码时按长度截断）。</summary>
    public static byte[] PackTrits(IReadOnlyList<TritDelta> values)
    {
        var bytes = new byte[(values.Count + TritsPerByte - 1) / TritsPerByte];
        for (var index = 0; index < values.Count; index++)
        {
            var digit = (int)values[index];
            bytes[index / TritsPerByte] += (byte)(digit * Pow3(index % TritsPerByte));
        }

        return bytes;
    }

    /// <summary>解码前 count 个 trit；非法字节报错（不猜）。</summary>
    public static TritDelta[] UnpackTrits(ReadOnlySpan<byte> bytes, int count)
    {
        if (count > bytes.Length * TritsPerByte)
        {
            throw new WireException($"长度不足：需要 {count} 个 trit，容量只有 {bytes.Length * TritsPerByte}");
        }

        var values = new TritDelta[count];
        for (var byteIndex = 0; byteIndex < bytes.Length; byteIndex++)
        {
            if (bytes[byteIndex] >= Combinations)
            {
                throw new WireException($"非法字节 {bytes[byteIndex]}：5 个 trit 只能表示 0..=242");
            }

            var rest = bytes[byteIndex];
            for (var slot = 0; slot < TritsPerByte; slot++)
            {
                var position = byteIndex * TritsPerByte + slot;
                if (position >= count)
                {
                    break;
                }

                values[position] = (TritDelta)(rest % 3);
                rest = (byte)(rest / 3);
            }
        }

        return values;
    }

    /// <summary>响应装框：[u32 payload_len][payload]。</summary>
    public static byte[] Frame(ReadOnlySpan<byte> payload)
    {
        var framed = new byte[4 + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(framed, (uint)payload.Length);
        payload.CopyTo(framed.AsSpan(4));
        return framed;
    }

    /// <summary>拆框；长度非法返回 null。</summary>
    public static byte[]? Unframe(ReadOnlySpan<byte> framed)
    {
        if (framed.Length < 4)
        {
            return null;
        }

        var length = BinaryPrimitives.ReadUInt32LittleEndian(framed);
        if (framed.Length < 4 + length)
        {
            return null;
        }

        return framed.Slice(4, (int)length).ToArray();
    }

    private static int Pow3(int exponent)
    {
        var result = 1;
        for (var i = 0; i < exponent; i++)
        {
            result *= 3;
        }

        return result;
    }
}

/// <summary>线格式异常（与 Rust RtError::Decode 对应）。</summary>
public sealed class WireException : Exception
{
    /// <summary>构造线格式异常。</summary>
    public WireException(string message)
        : base(message)
    {
    }
}

/// <summary>线格式写入器。</summary>
public sealed class WireWriter
{
    private readonly MemoryStream stream = new();

    /// <summary>写 u8。</summary>
    public WireWriter U8(byte value)
    {
        stream.WriteByte(value);
        return this;
    }

    /// <summary>写 u32（小端）。</summary>
    public WireWriter U32(uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        stream.Write(buffer);
        return this;
    }

    /// <summary>写 i32（小端）。</summary>
    public WireWriter I32(int value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, value);
        stream.Write(buffer);
        return this;
    }

    /// <summary>写 u64（小端）。</summary>
    public WireWriter U64(ulong value)
    {
        Span<byte> buffer = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(buffer, value);
        stream.Write(buffer);
        return this;
    }

    /// <summary>写定长字节。</summary>
    public WireWriter Bytes(ReadOnlySpan<byte> value)
    {
        stream.Write(value);
        return this;
    }

    /// <summary>写 u32 个数 + 5 trits/byte 打包体。</summary>
    public WireWriter Trits(IReadOnlyList<TritDelta> values)
    {
        U32((uint)values.Count);
        Bytes(Wire.PackTrits(values));
        return this;
    }

    /// <summary>写出为字节数组。</summary>
    public byte[] ToArray()
    {
        return stream.ToArray();
    }
}

/// <summary>线格式读取器。</summary>
public sealed class WireReader
{
    private readonly ReadOnlyMemory<byte> buffer;
    private int position;

    /// <summary>构造读取器。</summary>
    public WireReader(ReadOnlySpan<byte> buffer)
    {
        this.buffer = buffer.ToArray();
    }

    /// <summary>剩余未读字节数。</summary>
    public int Remaining => buffer.Length - position;

    /// <summary>读 u8。</summary>
    public byte U8()
    {
        Ensure(1);
        return buffer.Span[position++];
    }

    /// <summary>读 u32（小端）。</summary>
    public uint U32()
    {
        Ensure(4);
        var value = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Span.Slice(position, 4));
        position += 4;
        return value;
    }

    /// <summary>读 i32（小端）。</summary>
    public int I32()
    {
        Ensure(4);
        var value = BinaryPrimitives.ReadInt32LittleEndian(buffer.Span.Slice(position, 4));
        position += 4;
        return value;
    }

    /// <summary>读 u64（小端）。</summary>
    public ulong U64()
    {
        Ensure(8);
        var value = BinaryPrimitives.ReadUInt64LittleEndian(buffer.Span.Slice(position, 8));
        position += 8;
        return value;
    }

    /// <summary>读定长字节。</summary>
    public byte[] Bytes(int length)
    {
        Ensure(length);
        var value = buffer.Slice(position, length).ToArray();
        position += length;
        return value;
    }

    /// <summary>读 u32 个数 + 打包体，还原成 trit 序列。</summary>
    public TritDelta[] Trits()
    {
        var count = checked((int)U32());
        var byteLength = (count + Wire.TritsPerByte - 1) / Wire.TritsPerByte;
        var bytes = Bytes(byteLength);
        return Wire.UnpackTrits(bytes, count);
    }

    private void Ensure(int count)
    {
        if (position + count > buffer.Length)
        {
            throw new WireException($"需要 {count} 字节，只剩 {Remaining}");
        }
    }
}
