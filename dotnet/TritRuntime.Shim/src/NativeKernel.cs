using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using TritRuntime.Wire;
using WireFormat = TritRuntime.Wire.Wire;

namespace TritRuntime.Kernel;

/// <summary>原生内核：把请求交给 trit_rt.dll，按缓冲区协议自动扩容重试。</summary>
/// <remarks>
/// <para>
/// 所有<b>带句柄</b>的调用都走本实例的 <see cref="NativeApi"/>（按实例绑定导出函数指针），
/// 而不是全局静态 <c>DllImport</c>。原因见 <see cref="NativeApi"/> 的说明：
/// 进程内同时加载多份 trit_rt 时，静态 DllImport 会把调用路由到「最后登记」的那份，
/// 导致先前创建的实例读到别的模块的数据。
/// </para>
/// </remarks>
public sealed class NativeKernel : ITritKernel
{
    /// <summary>本机期望的 ABI 版本；v2 新增并行任务池、GPU 与全局预算 opcode。</summary>
    public const uint ExpectedAbiVersion = 2;

    /// <summary>本机期望的最低运行时版本（0.1.0）。</summary>
    public const uint MinimumRuntimeVersion = 0x00_01_00_00;

    private readonly NativeApi api;
    private readonly ulong handle;
    private readonly ArbitrationMode arbitration;
    private string lastError = string.Empty;
    private bool disposed;

    private NativeKernel(NativeApi api, ulong handle, ArbitrationMode arbitration)
    {
        this.api = api;
        this.handle = handle;
        this.arbitration = arbitration;
    }

    /// <summary>运行模式。</summary>
    public KernelMode Mode => KernelMode.Native;

    /// <summary>最近一次错误信息。</summary>
    public string LastError => lastError;

    /// <summary>本实例实际加载的原生库路径（诊断用）。</summary>
    public string LibraryPath => api.LibraryPath;

    /// <summary>按显式 DLL 路径加载原生库并创建运行时。</summary>
    /// <param name="libraryPath">trit_rt.dll 的完整路径。</param>
    /// <param name="configJson">配置 JSON（空 = 默认）。</param>
    /// <remarks>
    /// <b>文件名不必叫 trit_rt.dll</b>：原生库按内容摘要分目录落盘时，
    /// 文件名可能带前缀；导出函数名与文件名无关，按实例绑定照样认得出来。
    /// </remarks>
    public static NativeKernel Load(string libraryPath, string configJson = "")
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryPath);

        var api = NativeApi.Load(libraryPath);
        try
        {
            var abi = api.AbiVersion();
            if (abi != ExpectedAbiVersion)
            {
                throw new InvalidOperationException(
                    $"trit_rt ABI 版本不符：期望 {ExpectedAbiVersion}，实际 {abi}（{libraryPath}）");
            }

            var version = api.Version();
            if (version < MinimumRuntimeVersion)
            {
                throw new InvalidOperationException(
                    $"trit_rt 版本过低：{version:x8} < {MinimumRuntimeVersion:x8}");
            }

            var configBytes = string.IsNullOrEmpty(configJson) ? null : Encoding.UTF8.GetBytes(configJson);
            var runtimeHandle = api.Create(configBytes, configBytes?.Length ?? 0);
            if (runtimeHandle == 0)
            {
                throw new InvalidOperationException("tt_rt_create 失败：返回空句柄");
            }

            var caps = api.Capabilities(runtimeHandle);
            if (caps == 0)
            {
                throw new InvalidOperationException("tt_rt_capabilities 返回 0：运行时不可用");
            }

            // 登记给静态 DllImport 用：兼容壳里残留的进程级查询（ABI/版本）
            // 需要能在「库不在默认搜索路径」的真机场景下工作。
            // 注意这只影响 legacy 静态路径，**不影响本实例的任何调用**。
            NativeImportResolver.RegisterExplicitLibrary(NativeBindings.LibraryName, api.ModuleHandle);

            return new NativeKernel(api, runtimeHandle, ReadArbitration(api, runtimeHandle));
        }
        catch
        {
            api.Dispose();
            throw;
        }
    }

    /// <summary>按库名（依赖系统搜索路径 / apphost 探测规则）加载。</summary>
    public static NativeKernel LoadByLibraryName(string configJson = "")
    {
        var api = NativeApi.LoadByLibraryName();
        try
        {
            var abi = api.AbiVersion();
            if (abi != ExpectedAbiVersion)
            {
                throw new InvalidOperationException($"trit_rt ABI 版本不符：期望 {ExpectedAbiVersion}，实际 {abi}");
            }

            var configBytes = string.IsNullOrEmpty(configJson) ? null : Encoding.UTF8.GetBytes(configJson);
            var handle = api.Create(configBytes, configBytes?.Length ?? 0);
            if (handle == 0)
            {
                throw new InvalidOperationException("tt_rt_create 失败：返回空句柄");
            }

            NativeImportResolver.RegisterExplicitLibrary(NativeBindings.LibraryName, api.ModuleHandle);
            return new NativeKernel(api, handle, ReadArbitration(api, handle));
        }
        catch
        {
            api.Dispose();
            throw;
        }
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
            var status = api.Submit(handle, opcode, payload, payload.Length, output, capacity);
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
            var status = api.Snapshot(handle, output, capacity);
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

    /// <summary>异步提交扫描任务；返回 0 表示提交失败。</summary>
    public ulong SubmitScan(ScanRequest request)
    {
        var response = Submit(Opcode.ScanSubmit, Requests.ScanSubmit(request));
        return KernelResponseMapper.ToJobId(response);
    }

    /// <summary>取出已完成任务，取出即出队。</summary>
    /// <param name="limit">最多取几条；<c>&lt;= 0</c> 表示全部。</param>
    /// <param name="owner">归属过滤；<see langword="null"/> 表示不限归属。</param>
    public IReadOnlyList<JobResult> PollJobs(int limit = 0, string? owner = null)
    {
        var response = Submit(Opcode.JobPoll, Requests.JobPoll(limit, owner));
        return KernelResponseMapper.ToJobResults(response);
    }

    /// <summary>协作式取消单个任务。</summary>
    public int CancelJob(ulong jobId)
    {
        var response = Submit(Opcode.JobCancel, Requests.JobCancelJob(jobId));
        return KernelResponseMapper.ToCancelled(response);
    }

    /// <summary>协作式取消指定 owner 的全部任务。</summary>
    public int CancelOwner(string owner)
    {
        var response = Submit(Opcode.JobCancel, Requests.JobCancelOwner(owner));
        return KernelResponseMapper.ToCancelled(response);
    }

    /// <summary>查询或切换 GPU 后端状态。</summary>
    public GpuStatusReport GpuStatus(GpuPolicy? policy = null, bool probe = false)
    {
        var response = Submit(Opcode.GpuStatus, Requests.GpuStatus(policy, probe, 1_048_576));
        return KernelResponseMapper.ToGpuStatus(response);
    }

    /// <summary>查询全局资源预算。</summary>
    public BudgetSnapshot Budget()
    {
        var response = Submit(Opcode.Budget, Requests.Budget());
        return KernelResponseMapper.ToBudget(response);
    }

    /// <summary>查询任务池 Prometheus 指标。</summary>
    public string MetricsPrometheus()
    {
        var response = Submit(Opcode.Metrics, Requests.Metrics());
        return KernelResponseMapper.ToPrometheus(response);
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
        if (disposed)
        {
            return;
        }

        disposed = true;
        if (handle != 0)
        {
            _ = api.Destroy(handle);
        }

        api.Dispose();
    }

    private static ArbitrationMode ReadArbitration(NativeApi api, ulong runtimeHandle)
    {
        var capacity = 256;
        var output = new byte[capacity];
        var status = api.Submit(
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
        var length = api.LastError(handle, buffer, buffer.Length);
        if (length <= 0)
        {
            return string.Empty;
        }

        return Encoding.UTF8.GetString(buffer, 0, Math.Min(length, buffer.Length));
    }
}

/// <summary>
/// 显式路径加载的原生库登记：把静态 <c>DllImport("trit_rt")</c> 解析到指定路径的句柄。
/// </summary>
/// <remarks>
/// <para>
/// <b>只服务于进程级静态查询</b>（ABI 版本、运行时版本，以及残留的 legacy 静态入口）。
/// 带句柄的调用一律走 <see cref="NativeApi"/> 的按实例绑定，<b>不受本登记影响</b>。
/// </para>
/// <para>
/// 为什么还需要它：真机上原生库按「内容摘要 + 目录」落盘，
/// 不在默认搜索路径里；没有登记的话静态 <c>DllImport</c> 会抛
/// <c>DllNotFoundException (0x8007007E)</c>。
/// </para>
/// <para>
/// 登记名必须是 DllImport 声明用的名字（<c>trit_rt</c>），绝不能从落盘文件名推导：
/// 推导会得到 "<c>&lt;hash&gt;_trit_rt</c>"，与声明名不等，解析器永不命中。
/// </para>
/// <para>
/// <b>先到先得</b>：第一次登记之后不再更换目标。早先的实现是「后登记顶掉先登记」，
/// 那正是跨实例串台的来源——先创建的实例的静态调用会被路由到后加载的模块。
/// 现在静态路径恒定指向首个模块，而<b>所有</b>带句柄的调用都走按实例绑定，
/// 不受此影响。
/// </para>
/// </remarks>
internal static class NativeImportResolver
{
    private static readonly object Gate = new();
    private static bool installed;
    private static string? explicitName;
    private static IntPtr explicitHandle;

    /// <summary>登记显式加载的库；首次调用时安装程序集级解析器。</summary>
    /// <param name="libraryName">DllImport 声明名（固定为 <c>trit_rt</c>）。</param>
    /// <param name="handle">已加载模块的句柄。</param>
    public static void RegisterExplicitLibrary(string libraryName, IntPtr handle)
    {
        lock (Gate)
        {
            if (explicitHandle != IntPtr.Zero)
            {
                // 已有目标：保持不变（先到先得），避免把既有实例的静态调用改道。
                return;
            }

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
