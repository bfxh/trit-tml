#nullable enable

using System.Buffers.Binary;
using System.Diagnostics;
using TritRuntime.Kernel;
using TritRuntime.Wire;
using Xunit;

namespace TritRuntime.Shim.Tests;

/// <summary>
/// 托管回退内核的畸形载荷矩阵：对<b>每个 opcode</b>灌入一整组坏载荷，断言
///   ① 绝不向调用方穿透任何异常（模组 <c>Mod.Call</c> 层只认状态码）；
///   ② 返回的状态码一定落在已定义集合 [-10, 0]，不冒出未定义值；
///   ③ 轰炸之后内核依然可用（后续 Capabilities 仍返回 Ok）。
///
/// 与 Rust 侧 <c>tests/robustness.rs</c> 同构——两条路径都不能被一份 20 字节坏载荷
/// 打穿：C ABI 那边怕 panic abort，托管这边怕 <c>OverflowException</c> / OOM /
/// <c>ArgumentOutOfRangeException</c> 穿透到游戏进程。
/// </summary>
public sealed class ManagedRobustnessTests
{
    /// <summary>全部已知 opcode（1..0x1C）+ 0（BAD_OP）+ 若干越界/保留值。</summary>
    private static readonly int[] Opcodes =
    {
        0,
        1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16,
        17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28,
        29, 0x7F, 0xFF, 0x1000, unchecked((int)0xFFFF_FFFFu),
    };

    /// <summary>
    /// 一整组畸形载荷标识。用 string 而非 byte[] 作 TheoryData 参数，
    /// 避免 xUnit 对不可序列化 theory data 的告警；实际载荷在 <see cref="MakePayload"/> 生成。
    /// </summary>
    private static readonly string[] PayloadKinds =
    {
        "empty",       // 空载荷
        "one0",        // 单字节 0
        "oneFF",       // 单字节 0xFF
        "zeros4",      // 4×0（u32 = 0）
        "ff4",         // 4×0xFF（u32 = 0xFFFF_FFFF → 巨量 count）
        "ff8",         // 8×0xFF（u64 max）
        "ff16",        // 16×0xFF
        "ff64",        // 64×0xFF
        "7f64",        // 64×0x7F（接近 int.MaxValue 的连续字节）
        "seq64",       // 0..63 递增
        "u32max+8",    // 头 u32=0xFFFF_FFFF + 8 字节 0xAB
        "i32max+8",    // 头 u32=0x7FFF_FFFF + 8 字节 0xAB
        "json-trunc",  // 截断 JSON "{"
        "json-bad",    // 非 JSON 文本
        "utf8-bad",    // 非法 UTF-8
        "ab64",        // 64×0xAB
    };

    /// <summary>生成 opcode × 载荷种类的全组合 theory data。</summary>
    public static TheoryData<int, string> MalformedCases()
    {
        var data = new TheoryData<int, string>();
        foreach (var opcode in Opcodes)
        {
            foreach (var kind in PayloadKinds)
            {
                data.Add(opcode, kind);
            }
        }

        return data;
    }

    private static byte[] MakePayload(string kind) => kind switch
    {
        "empty" => Array.Empty<byte>(),
        "one0" => new byte[] { 0x00 },
        "oneFF" => new byte[] { 0xFF },
        "zeros4" => new byte[4],
        "ff4" => Repeat(0xFF, 4),
        "ff8" => Repeat(0xFF, 8),
        "ff16" => Repeat(0xFF, 16),
        "ff64" => Repeat(0xFF, 64),
        "7f64" => Repeat(0x7F, 64),
        "seq64" => Enumerable.Range(0, 64).Select(i => (byte)i).ToArray(),
        "u32max+8" => WithCountHeader(0xFFFF_FFFFu, 8),
        "i32max+8" => WithCountHeader(0x7FFF_FFFFu, 8),
        "json-trunc" => System.Text.Encoding.UTF8.GetBytes("{"),
        "json-bad" => System.Text.Encoding.UTF8.GetBytes("not json at all"),
        "utf8-bad" => new byte[] { 0xFF, 0xFE, 0xFD },
        "ab64" => Repeat(0xAB, 64),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static byte[] Repeat(byte value, int count) =>
        Enumerable.Repeat(value, count).ToArray();

    private static byte[] WithCountHeader(uint count, int extra)
    {
        var buffer = new byte[4 + extra];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, count);
        for (var i = 4; i < buffer.Length; i++)
        {
            buffer[i] = 0xAB;
        }

        return buffer;
    }

    /// <summary>
    /// 核心矩阵：任一 (opcode, 畸形载荷) 组合都不得穿透异常、不得返回未定义状态码，
    /// 且轰炸后内核仍可用。断言消息带上 opcode 与载荷种类，失败时能立刻定位组合。
    /// </summary>
    [Theory]
    [MemberData(nameof(MalformedCases))]
    public void MalformedNeverLeaksExceptionAndKeepsKernelUsable(int opcodeInt, string kind)
    {
        var opcode = unchecked((uint)opcodeInt);
        var payload = MakePayload(kind);
        using var kernel = new ManagedKernel();

        KernelResponse? response = null;
        var exception = Record.Exception(() => response = kernel.Submit(opcode, payload));

        Assert.True(
            exception is null,
            $"opcode=0x{opcode:X8} kind={kind} 穿透了非约定异常：{exception}");
        Assert.NotNull(response);
        Assert.InRange(response!.Status, -10, 0);

        // 轰炸之后内核依然可用：能力查询必须仍返回 Ok。
        var alive = kernel.Submit(Opcode.Capabilities, Array.Empty<byte>());
        Assert.Equal(Status.Ok, alive.Status);
    }

    /// <summary>
    /// 精确回归：一步提交声明 0xFFFF_FFFF 条意图，只有 4 字节载荷。
    /// 必须<b>快速</b>报 Decode——旧代码会 <c>new WriteIntent[count]</c> 去申请几十 GB，
    /// 这条路一旦回归，这里会因耗时/内存直接暴露，而不是悄悄通过。
    /// </summary>
    [Fact]
    public void GiantDeclaredBatchCountIsRejectedFast()
    {
        var payload = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, 0xFFFF_FFFFu);
        using var kernel = new ManagedKernel();

        var stopwatch = Stopwatch.StartNew();
        var response = kernel.Submit(Opcode.TxnApply, payload);
        stopwatch.Stop();

        Assert.Equal(Status.Decode, response.Status);
        Assert.True(
            stopwatch.ElapsedMilliseconds < 1000,
            $"坏载荷耗时 {stopwatch.ElapsedMilliseconds}ms，疑似触发了巨量分配");
    }

    /// <summary>同一份畸形载荷连续提交两次，状态码必须一致（结果不依赖内部残留状态）。</summary>
    [Theory]
    [InlineData((int)Opcode.TxnApply, "ff4")]
    [InlineData((int)Opcode.TxnSubmitBatch, "u32max+8")]
    [InlineData((int)Opcode.Publish, "ff4")]
    [InlineData((int)Opcode.Subscribe, "i32max+8")]
    [InlineData((int)Opcode.Query, "ff4")]
    public void MalformedPayloadStatusIsDeterministic(int opcodeInt, string kind)
    {
        var opcode = unchecked((uint)opcodeInt);
        var payload = MakePayload(kind);
        using var kernel = new ManagedKernel();

        var first = kernel.Submit(opcode, payload).Status;
        var second = kernel.Submit(opcode, payload).Status;
        Assert.Equal(first, second);
    }
}
