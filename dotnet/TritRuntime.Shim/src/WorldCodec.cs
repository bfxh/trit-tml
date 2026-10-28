#nullable enable

using System.Buffers.Binary;
using TritRuntime.Wire;
using WireFormat = TritRuntime.Wire.Wire;

namespace TritRuntime.Kernel;

/// <summary>
/// 世界投影编解码：布局与 Rust <c>trit_rt::snapshot::encode_world</c> 逐字节一致
/// （小端）：
/// <code>
/// [u8 version][u64 tick][u64 log_seq][u32 lane_count]
/// [u32 key] * lane_count
/// [u32 trit_count][packed trits]        // 5 trits/byte
/// </code>
/// 键与值分开存放：键是稀疏 u32，值走 5 trits/byte 的密集张量投影。
/// 托管回退内核必须产出与原生内核完全相同的字节，否则影子差分会误报。
/// </summary>
public static class WorldCodec
{
    /// <summary>世界投影格式版本（与 Rust <c>SNAPSHOT_VERSION</c> 一致）。</summary>
    public const byte Version = 1;

    /// <summary>把世界投影编码为字节。</summary>
    /// <param name="world">车道键 → 三进制值（按键升序遍历）。</param>
    /// <param name="tick">当前 tick。</param>
    /// <param name="logSeq">事务日志序列号。</param>
    public static byte[] Encode(IReadOnlyDictionary<uint, TritDelta> world, ulong tick, ulong logSeq)
    {
        var keys = new List<uint>(world.Keys);
        keys.Sort();
        var values = new TritDelta[keys.Count];
        for (var i = 0; i < keys.Count; i++)
        {
            values[i] = world[keys[i]];
        }

        var packed = WireFormat.PackTrits(values);
        var buffer = new byte[1 + 8 + 8 + 4 + (4 * keys.Count) + 4 + packed.Length];
        var offset = 0;
        buffer[offset++] = Version;
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(offset), tick);
        offset += 8;
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(offset), logSeq);
        offset += 8;
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset), (uint)keys.Count);
        offset += 4;
        foreach (var key in keys)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset), key);
            offset += 4;
        }

        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset), (uint)values.Length);
        offset += 4;
        packed.CopyTo(buffer, offset);
        return buffer;
    }

    /// <summary>从字节还原世界投影；格式不符即抛 <see cref="WireException"/>（不猜）。</summary>
    public static (ulong Tick, ulong LogSeq, SortedDictionary<uint, TritDelta> World) Decode(ReadOnlySpan<byte> bytes)
    {
        var reader = new WireReader(bytes);
        var version = reader.U8();
        if (version != Version)
        {
            throw new WireException($"快照版本不符：期望 {Version}，实际 {version}");
        }

        var tick = reader.U64();
        var logSeq = reader.U64();
        var laneCount = checked((int)reader.U32());
        var keys = new List<uint>(Math.Min(laneCount, 1 << 20));
        for (var i = 0; i < laneCount; i++)
        {
            keys.Add(reader.U32());
        }

        var values = reader.Trits();
        if (values.Length != laneCount)
        {
            throw new WireException($"键值数量不符：{laneCount} 个键，{values.Length} 个值");
        }

        var world = new SortedDictionary<uint, TritDelta>();
        for (var i = 0; i < keys.Count; i++)
        {
            world[keys[i]] = values[i];
        }

        return (tick, logSeq, world);
    }
}
