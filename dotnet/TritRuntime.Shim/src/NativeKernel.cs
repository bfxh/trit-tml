using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using TritRuntime.Wire;
using WireFormat = TritRuntime.Wire.Wire;

namespace TritRuntime.Kernel;

/// <summary>原生内核：把请求交给 trit_rt.dll，按缓冲区协议自动扩容重试。</summary>
public sealed class NativeKernel : ITritKernel
{
    /// <summary>本机期望的 ABI 版本。</summary>
    public const uint ExpectedAbiVersion = 1;

    /// <summary>本机期望的最低运行时版本（0.1.0）。</summary>
    public const uint MinimumRuntimeVersion = 0x00_01_00_00;

    private readonly ulong handle;
    private readonly ArbitrationMode arbitration;
    private string lastError = string.Empty;

    private NativeKernel(ulong handle, ArbitrationMode arbitration)
    {
        this.handle = handle;
        this.arbitration = arbitration;
    }

    /// <summary>运行模式。</summary>
    public KernelMode Mode => KernelMode.Native;

    /// <summary>最近一次错误信息。</summary>
    public string LastError => lastError;

    /// <summary>按显式 DLL 路径加载原生库并创建运行时。</summary>
    /// <param name="libraryPath">trit_rt.dll 的完整路径。</param>
    /// <param name="configJson">配置 JSON（空 = 默认）。</param>
    public static NativeKernel Load(string libraryPath, string configJson = "")
    {
        var handle = NativeLibrary.Load(libraryPath);

        // 登记名必须是 DllImport 声明用的名字，绝不能从落盘文件名推导：
        // 真机上原生库按「内容摘要 + 文件名」落盘（避免覆盖仍在占用中的旧版本），
        // 从路径推导会得到 "<hash>_trit_rt"，与 DllImport 的 "trit_rt" 不等，
        // 解析器永不命中，于是退回默认探测并抛 DllNotFoundException（0x8007007E）。
        NativeImportResolver.RegisterExplicitLibrary(NativeBindings.LibraryName, handle);

        var abi = NativeBindings.AbiVersion();
        if (abi != ExpectedAbiVersion)
        {
            throw new InvalidOperationException(
                $"trit_rt ABI 版本不符：期望 {ExpectedAbiVersion}，实际 {abi}（{libraryPath}）");
        }

        var version = NativeBindings.Version();
        if (version < MinimumRuntimeVersion)
        {
            throw new InvalidOperationException(
                $"trit_rt 版本过低：{version:x8} < {MinimumRuntimeVersion:x8}");
        }

        var configBytes = string.IsNullOrEmpty(configJson) ? null : Encoding.UTF8.GetBytes(configJson);
        var runtimeHandle = NativeBindings.Create(configBytes, configBytes?.Length ?? 0);
        if (runtimeHandle == 0)
        {
            throw new InvalidOperationException("tt_rt_create 失败：返回空句柄");
        }

        var caps = NativeBindings.Capabilities(runtimeHandle);
        if (caps == 0)
        {
            throw new InvalidOperationException("tt_rt_capabilities 返回 0：运行时不可用");
        }

        return new NativeKernel(runtimeHandle, ReadArbitration(runtimeHandle));
    }

    /// <summary>按库名（依赖系统搜索路径 / apphost 探测规则）加载。</summary>
    public static NativeKernel LoadByLibraryName(string configJson = "")
    {
        var abi = NativeBindings.AbiVersion();
        if (abi != ExpectedAbiVersion)
        {
            throw new InvalidOperationException($"trit_rt ABI 版本不符：期望 {ExpectedAbiVersion}，实际 {abi}");
        }

        var configBytes = string.IsNullOrEmpty(configJson) ? null : Encoding.UTF8.GetBytes(configJson);
        var handle = NativeBindings.Create(configBytes, configBytes?.Length ?? 0);
        if (handle == 0)
        {
            throw new InvalidOperationException("tt_rt_create 失败：返回空句柄");
        }

        return new NativeKernel(handle, ReadArbitration(handle));
    }

    /// <summary>能力报告。</summary>
    public CapabilityReport Capabilities()
    {
        var response = Submit(Opcode.Capabilities, Requests.Capabilities());
        return KernelResponseMapper.ToCapabilityReport(response, KernelMode.Native, lastError);
    }

    /// <summary>提交一个 opcode，返回拆框后的载荷。</summary>
    public KernelResponse Submit(uint opcode, byte[] payload)
    {
        var capacity = 512;
        while (true)
        {
            var output = new byte[capacity];
            var status = NativeBindings.Submit(handle, opcode, payload, payload.Length, output, capacity);
            if (status == Status.Buffer && capacity >= 8)
            {
                // 协议：所需长度已写进 output 前 8 字节。
                capacity = (int)BinaryPrimitives.ReadUInt64LittleEndian(output) + 16;
                continue;
            }

            if (status == Status.Ok || status == Status.Conflict)
            {
                var framed = output.AsSpan(0, capacity);
                var unframed = WireFormat.Unframe(framed);
                return new KernelResponse(status, unframed ?? Array.Empty<byte>());
            }

            lastError = ReadLastError();
            return new KernelResponse(status, Array.Empty<byte>());
        }
    }

    /// <summary>查询。</summary>
    public KernelResponse Query(byte[] query) => Submit(Opcode.Query, query);

    /// <summary>按查询编号查询；事件载荷查询需带 eventId。</summary>
    public KernelResponse QueryById(uint queryId, uint eventId = 0) =>
        Submit(Opcode.Query, Requests.Query(queryId, eventId));

    /// <summary>step。</summary>
    public StepResult Step(ulong tick)
    {
        var response = Submit(Opcode.Step, Requests.Step(tick));
        return KernelResponseMapper.ToStepResult(response);
    }

    /// <summary>订阅。</summary>
    public ulong Subscribe(uint modId, uint eventId, int priority, uint[] deps)
    {
        var response = Submit(Opcode.Subscribe, Requests.Subscribe(modId, eventId, priority, deps));
        return KernelResponseMapper.ToU64(response);
    }

    /// <summary>退订。</summary>
    public void Unsubscribe(ulong subId)
    {
        Submit(Opcode.Unsubscribe, Requests.Unsubscribe(subId));
    }

    /// <summary>发布。</summary>
    public uint Publish(uint eventId, TritDelta[] payload)
    {
        var response = Submit(Opcode.Publish, Requests.Publish(eventId, payload));
        return KernelResponseMapper.ToU32(response);
    }

    /// <summary>开事务。</summary>
    public ulong BeginTransaction()
    {
        var response = Submit(Opcode.TxnBegin, Requests.TxnBegin());
        return KernelResponseMapper.ToU64(response);
    }

    /// <summary>提交意图。</summary>
    public void SubmitIntent(ulong txnId, uint key, TritDelta delta)
    {
        Submit(Opcode.TxnSubmit, Requests.TxnSubmit(txnId, key, delta));
    }

    /// <summary>提交事务。</summary>
    public TxnCommitResult Commit(ulong txnId)
    {
        var response = Submit(Opcode.TxnCommit, Requests.TxnCommit(txnId));
        return KernelResponseMapper.ToCommitResult(response);
    }

    /// <summary>回滚事务。</summary>
    public void RollbackTransaction(ulong txnId)
    {
        Submit(Opcode.TxnRollback, Requests.TxnRollback(txnId));
    }

    /// <summary>仲裁。</summary>
    public ArbitrateResult Arbitrate(IReadOnlyList<WriteIntent> intents)
    {
        var response = Submit(Opcode.Arbitrate, Requests.Arbitrate(intents));
        return KernelResponseMapper.ToArbitrateResult(response);
    }

    /// <summary>打快照。</summary>
    public ulong Snapshot()
    {
        var response = Submit(Opcode.Snapshot, Requests.Snapshot());
        return KernelResponseMapper.ToU64(response);
    }

    /// <summary>快照字节（[u64 id][世界编码]）。</summary>
    public byte[] SnapshotBytes()
    {
        var capacity = 4096;
        while (true)
        {
            var output = new byte[capacity];
            var status = NativeBindings.Snapshot(handle, output, capacity);
            if (status == Status.Buffer && capacity >= 8)
            {
                capacity = (int)BinaryPrimitives.ReadUInt64LittleEndian(output) + 16;
                continue;
            }

            if (status != Status.Ok)
            {
                lastError = ReadLastError();
                return Array.Empty<byte>();
            }

            return WireFormat.Unframe(output.AsSpan(0, capacity)) ?? Array.Empty<byte>();
        }
    }

    /// <summary>差分。</summary>
    public DiffResult Diff(ulong a, ulong b)
    {
        var response = Submit(Opcode.Diff, Requests.Diff(a, b));
        return KernelResponseMapper.ToDiffResult(response);
    }

    /// <summary>回滚到快照。</summary>
    public void RollbackTo(ulong snapshotId)
    {
        Submit(Opcode.RollbackTo, Requests.RollbackTo(snapshotId));
    }

    /// <summary>抽随机数。</summary>
    public TritDelta[] NextRandom(int count)
    {
        var response = Submit(Opcode.PrngNext, Requests.PrngNext(count));
        return KernelResponseMapper.ToTrits(response);
    }

    /// <summary>记录影子差分。</summary>
    public ulong ShadowRecord(byte[] signature)
    {
        var trits = Array.ConvertAll(signature, value => (TritDelta)value);
        var response = Submit(Opcode.ShadowRecord, Requests.ShadowRecord(trits));
        return KernelResponseMapper.ToU64(response);
    }

    /// <summary>统计。</summary>
    public ulong[] Stats()
    {
        var response = Submit(Opcode.Stats, Requests.Stats());
        return KernelResponseMapper.ToStats(response);
    }

    /// <summary>销毁运行时。</summary>
    public void Dispose()
    {
        if (handle != 0)
        {
            _ = NativeBindings.Destroy(handle);
        }
    }

    private static ArbitrationMode ReadArbitration(ulong runtimeHandle)
    {
        var capacity = 256;
        var output = new byte[capacity];
        var status = NativeBindings.Submit(
            runtimeHandle,
            Opcode.Capabilities,
            null,
            0,
            output,
            capacity);
        if (status != Status.Ok)
        {
            return ArbitrationMode.Majority;
        }

        var unframed = WireFormat.Unframe(output.AsSpan(0, capacity));
        if (unframed is null || unframed.Length < 17)
        {
            return ArbitrationMode.Majority;
        }

        return (ArbitrationMode)unframed[16];
    }

    private string ReadLastError()
    {
        var buffer = new byte[512];
        var length = NativeBindings.LastError(handle, buffer, buffer.Length);
        if (length <= 0)
        {
            return string.Empty;
        }

        return Encoding.UTF8.GetString(buffer, 0, Math.Min(length, buffer.Length));
    }
}

/// <summary>显式路径加载的原生库登记：把 DllImport("trit_rt") 解析到指定路径的句柄。</summary>
internal static class NativeImportResolver
{
    private static readonly object Gate = new();
    private static bool installed;
    private static string? explicitName;
    private static IntPtr explicitHandle;

    /// <summary>登记显式加载的库；首次调用时安装程序集级解析器。</summary>
    public static void RegisterExplicitLibrary(string libraryName, IntPtr handle)
    {
        lock (Gate)
        {
            explicitName = libraryName;
            explicitHandle = handle;
            if (!installed)
            {
                NativeLibrary.SetDllImportResolver(
                    typeof(NativeBindings).Assembly,
                    (name, _, _) => name == explicitName ? explicitHandle : IntPtr.Zero);
                installed = true;
            }
        }
    }
}
