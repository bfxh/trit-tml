using TritRuntime.Wire;

namespace TritRuntime.Kernel;

/// <summary>一次不一致的完整记录。</summary>
/// <param name="Opcode">出问题的 opcode。</param>
/// <param name="Request">双方收到的同一份请求字节。</param>
/// <param name="Primary">主内核响应（拆框后的载荷 + 状态码）。</param>
/// <param name="Shadow">影子内核响应。</param>
public sealed record Divergence(
    uint Opcode,
    byte[] Request,
    KernelResponse Primary,
    KernelResponse Shadow)
{
    /// <summary>摘要（可读）。</summary>
    public string Summary =>
        $"opcode {Opcode:#x}：主内核 status={Primary.Status} len={Primary.Payload.Length}，"
        + $"影子内核 status={Shadow.Status} len={Shadow.Payload.Length}";
}

/// <summary>
/// 影子差分器：把同一请求序列喂给主内核与影子内核，逐字节比较响应。
/// </summary>
/// <remarks>
/// 语义对应 Rust 的 <c>SHADOW_RECORD</c>：发现不一致**只记录，不提交**，
/// 不影响任何一方的状态。调用方按 <see cref="Divergences"/> 决定是否把
/// 某个热点从 Rust 旁路切回原 C# 路径——「切换前先影子差分」的落地处。
/// </remarks>
public sealed class ShadowDiffer : IDisposable
{
    /// <summary>保留的最近不一致条数上限。</summary>
    public const int RecentWindow = 64;

    private readonly ITritKernel primary;
    private readonly ITritKernel shadow;
    private readonly List<Divergence> recent = new();
    private bool disposed;

    /// <summary>构造差分器。</summary>
    /// <param name="primary">主内核（响应返回给调用方，并接收影子签名记录）。</param>
    /// <param name="shadow">影子内核（只对照，不影响调用方）。</param>
    public ShadowDiffer(ITritKernel primary, ITritKernel shadow)
    {
        this.primary = primary;
        this.shadow = shadow;
    }

    /// <summary>主内核。</summary>
    public ITritKernel Primary => primary;

    /// <summary>影子内核。</summary>
    public ITritKernel Shadow => shadow;

    /// <summary>已参与差分的请求数。</summary>
    public ulong Compared { get; private set; }

    /// <summary>发现不一致的次数。</summary>
    public ulong Divergences { get; private set; }

    /// <summary>最近的不一致记录（最多 <see cref="RecentWindow"/> 条，新的在前）。</summary>
    public IReadOnlyList<Divergence> Recent => recent;

    /// <summary>
    /// 对同一 opcode + 载荷做差分，返回主内核的响应。
    /// 不一致时把签名记进主内核的影子日志（只记录，不提交）。
    /// </summary>
    public KernelResponse Run(uint opcode, byte[] payload)
    {
        var primaryResponse = primary.Submit(opcode, payload);
        var shadowResponse = shadow.Submit(opcode, payload);
        Compared += 1;
        if (!Same(primaryResponse, shadowResponse))
        {
            Divergences += 1;
            recent.Insert(0, new Divergence(opcode, payload, primaryResponse, shadowResponse));
            if (recent.Count > RecentWindow)
            {
                recent.RemoveAt(recent.Count - 1);
            }

            // 签名 = 差异指纹；交给主内核存档，供事后诊断。
            _ = primary.ShadowRecord(Fingerprint(opcode, primaryResponse.Payload, shadowResponse.Payload));
        }

        return primaryResponse;
    }

    /// <summary>
    /// 世界投影逐字节对拍：两侧各打一次快照并比较快照字节。
    /// 返回空数组表示完全一致，否则返回差异指纹。
    /// </summary>
    public byte[] CompareWorlds()
    {
        var left = primary.SnapshotBytes();
        var right = shadow.SnapshotBytes();
        Compared += 1;
        if (left.AsSpan().SequenceEqual(right))
        {
            return Array.Empty<byte>();
        }

        Divergences += 1;
        var signature = Fingerprint(Opcode.Snapshot, left, right);
        recent.Insert(
            0,
            new Divergence(
                Opcode.Snapshot,
                Array.Empty<byte>(),
                new KernelResponse(TritRuntime.Wire.Status.Ok, left),
                new KernelResponse(TritRuntime.Wire.Status.Ok, right)));
        if (recent.Count > RecentWindow)
        {
            recent.RemoveAt(recent.Count - 1);
        }

        _ = primary.ShadowRecord(signature);
        return signature;
    }

    /// <summary>清空本地记录（内核里的影子日志不动）。</summary>
    public void Reset()
    {
        recent.Clear();
        Compared = 0;
        Divergences = 0;
    }

    /// <summary>销毁影子内核。</summary>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        shadow.Dispose();
        GC.SuppressFinalize(this);
    }

    private static bool Same(KernelResponse left, KernelResponse right) =>
        left.Status == right.Status && left.Payload.AsSpan().SequenceEqual(right.Payload);

    /// <summary>
    /// 差异指纹：取两侧载荷的按位异或，逐字节映射成 trit（0/1/2）。
    /// 确定性、紧凑，且能直接喂给 <c>SHADOW_RECORD</c>。
    /// </summary>
    private static byte[] Fingerprint(uint opcode, byte[] left, byte[] right)
    {
        var width = Math.Min(left.Length, right.Length);
        var trits = new List<TritDelta>(width + 8);
        foreach (var digit in BitConverter.GetBytes(opcode))
        {
            trits.Add((TritDelta)(digit % 3));
        }

        for (var i = 0; i < width; i++)
        {
            trits.Add((TritDelta)((left[i] ^ right[i]) % 3));
        }

        return Array.ConvertAll(trits.ToArray(), value => (byte)value);
    }
}
