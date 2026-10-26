using System.Runtime.InteropServices;

namespace TritRuntime.Kernel;

/// <summary>
/// trit_rt.dll 的<b>按实例</b> C ABI 绑定（见 <c>docs/ABI.md</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 每个 <see cref="NativeApi"/> 持有<b>自己那份模块</b>的导出函数指针，
/// 调用时直接跳到<b>本实例</b>的模块，不经过任何全局状态。
/// </para>
/// <para>
/// <b>为什么不能靠静态 <c>DllImport</c> + 全局解析器</b>：
/// <c>NativeLibrary.SetDllImportResolver</c> 在一个程序集里<b>只能装一次</b>，
/// 而解析器把库名映射到<b>一个</b>句柄。进程里一旦加载了第二份 trit_rt
/// （模组热重载后的新摘要目录、测试里的带前缀副本、并存的新旧版本），
/// 后登记的那份会顶掉前一份的登记，于是<b>先前创建的运行时实例的调用
/// 会被路由到新模块</b>——句柄在另一个模块的注册表里查不到，
/// 表现为「读到别人的数据」或莫名其妙的错误码。
/// 这种故障极难从日志里看出来，因为一切看起来都「调用成功」。
/// </para>
/// <para>
/// 进程内同时存在多份 trit_rt 是<b>常态而非意外</b>：原生库按内容摘要分目录落盘，
/// 升级后新旧两份可能同时被占用；tML 的模组包也会带自己的副本。
/// 因此「按实例绑定」不是过度设计，而是正确性要求。
/// </para>
/// </remarks>
public sealed class NativeApi : IDisposable
{
    /// <summary>原生库名；解析顺序由 <see cref="NativeLibrary"/> 规则 + 显式路径决定。</summary>
    public const string LibraryName = NativeBindings.LibraryName;

    /// <summary>按平台尝试的库文件名（用于「按库名加载」的兜底探测）。</summary>
    private static readonly string[] LibraryFileCandidates = BuildLibraryFileCandidates();

    private readonly IntPtr library;
    private readonly string libraryPath;
    private readonly NativeAbiVersion abiVersion;
    private readonly NativeVersion version;
    private readonly NativeCreate create;
    private readonly NativeDestroy destroy;
    private readonly NativeSubmit submit;
    private readonly NativeStep step;
    private readonly NativeSnapshot snapshot;
    private readonly NativeDiff diff;
    private readonly NativeCapabilities capabilities;
    private readonly NativeLastError lastError;
    private bool disposed;

    private NativeApi(
        IntPtr library,
        string libraryPath,
        NativeAbiVersion abiVersion,
        NativeVersion version,
        NativeCreate create,
        NativeDestroy destroy,
        NativeSubmit submit,
        NativeStep step,
        NativeSnapshot snapshot,
        NativeDiff diff,
        NativeCapabilities capabilities,
        NativeLastError lastError)
    {
        this.library = library;
        this.libraryPath = libraryPath;
        this.abiVersion = abiVersion;
        this.version = version;
        this.create = create;
        this.destroy = destroy;
        this.submit = submit;
        this.step = step;
        this.snapshot = snapshot;
        this.diff = diff;
        this.capabilities = capabilities;
        this.lastError = lastError;
    }

    /// <summary>本实例实际加载的模块路径（诊断用）。</summary>
    public string LibraryPath => libraryPath;

    /// <summary>本实例的模块句柄（供进程级 DllImport 解析器登记用）。</summary>
    public IntPtr ModuleHandle => library;

    /// <summary>
    /// 按显式 DLL 路径加载并绑定导出函数。
    /// </summary>
    /// <param name="libraryPath">trit_rt.dll 的完整路径。</param>
    /// <remarks>
    /// <b>文件名不必叫 trit_rt.dll</b>：导出函数名与文件名无关，
    /// 按实例绑定只认「这个模块有没有 <c>tt_rt_*</c> 导出」。
    /// 这正是原生库能按内容摘要自由命名 / 分目录落盘的前提。
    /// </remarks>
    public static NativeApi Load(string libraryPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryPath);

        var library = NativeLibrary.Load(libraryPath);
        try
        {
            return new NativeApi(
                library,
                libraryPath,
                Get<NativeAbiVersion>(library, "tt_rt_abi_version"),
                Get<NativeVersion>(library, "tt_rt_version"),
                Get<NativeCreate>(library, "tt_rt_create"),
                Get<NativeDestroy>(library, "tt_rt_destroy"),
                Get<NativeSubmit>(library, "tt_rt_submit"),
                Get<NativeStep>(library, "tt_rt_step"),
                Get<NativeSnapshot>(library, "tt_rt_snapshot"),
                Get<NativeDiff>(library, "tt_rt_diff"),
                Get<NativeCapabilities>(library, "tt_rt_capabilities"),
                Get<NativeLastError>(library, "tt_rt_last_error"));
        }
        catch
        {
            // 导出解析失败（架构不对、导出被裁剪）时必须把模块还回去，
            // 否则这一份就永久占着文件句柄。
            NativeLibrary.Free(library);
            throw;
        }
    }

    /// <summary>
    /// 按库名加载（依赖系统搜索路径 / apphost 探测规则）。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="Load"/> 不同，这条路<b>不会</b>被全局 DllImport 解析器接管，
    /// 也不受「谁最后登记谁生效」的影响——它拿的是操作系统此刻能搜到的那个模块。
    /// </remarks>
    public static NativeApi LoadByLibraryName()
    {
        var failures = new List<string>();
        foreach (var candidate in LibraryFileCandidates)
        {
            if (!NativeLibrary.TryLoad(candidate, out var library))
            {
                failures.Add(candidate);
                continue;
            }

            try
            {
                return new NativeApi(
                    library,
                    candidate,
                    Get<NativeAbiVersion>(library, "tt_rt_abi_version"),
                    Get<NativeVersion>(library, "tt_rt_version"),
                    Get<NativeCreate>(library, "tt_rt_create"),
                    Get<NativeDestroy>(library, "tt_rt_destroy"),
                    Get<NativeSubmit>(library, "tt_rt_submit"),
                    Get<NativeStep>(library, "tt_rt_step"),
                    Get<NativeSnapshot>(library, "tt_rt_snapshot"),
                    Get<NativeDiff>(library, "tt_rt_diff"),
                    Get<NativeCapabilities>(library, "tt_rt_capabilities"),
                    Get<NativeLastError>(library, "tt_rt_last_error"));
            }
            catch (Exception error)
            {
                NativeLibrary.Free(library);
                throw new DllNotFoundException(
                    $"按库名加载 trit_rt 失败（候选 {candidate} 缺少必要的导出）：{error.Message}", error);
            }
        }

        throw new DllNotFoundException(
            "按库名加载 trit_rt 失败（尝试过：" + string.Join(", ", failures) + "）");
    }

    /// <summary>ABI 版本号。</summary>
    public uint AbiVersion() => abiVersion();

    /// <summary>运行时版本号。</summary>
    public uint Version() => version();

    /// <summary>创建运行时；config 为 JSON 字节（可为空 = 默认配置）。失败返回 0。</summary>
    public ulong Create(byte[]? config, int len)
    {
        ThrowIfDisposed();
        return create(config, (nuint)len);
    }

    /// <summary>销毁运行时。</summary>
    public int Destroy(ulong handle)
    {
        ThrowIfDisposed();
        return destroy(handle);
    }

    /// <summary>提交一个操作。</summary>
    public int Submit(ulong handle, uint opcode, byte[]? payload, int len, byte[]? output, int capacity)
    {
        ThrowIfDisposed();
        return submit(handle, opcode, payload, (nuint)len, output, (nuint)capacity);
    }

    /// <summary>推进一个 tick。</summary>
    public int Step(ulong handle, ulong tick, byte[]? output, int capacity)
    {
        ThrowIfDisposed();
        return step(handle, tick, output, (nuint)capacity);
    }

    /// <summary>打快照并把快照字节写进 output。</summary>
    public int Snapshot(ulong handle, byte[]? output, int capacity)
    {
        ThrowIfDisposed();
        return snapshot(handle, output, (nuint)capacity);
    }

    /// <summary>差分两个快照。</summary>
    public int Diff(ulong handle, ulong snapshotA, ulong snapshotB, byte[]? output, int capacity)
    {
        ThrowIfDisposed();
        return diff(handle, snapshotA, snapshotB, output, (nuint)capacity);
    }

    /// <summary>能力位图；句柄非法返回 0。</summary>
    public ulong Capabilities(ulong handle)
    {
        ThrowIfDisposed();
        return capabilities(handle);
    }

    /// <summary>最近一次错误信息。</summary>
    public int LastError(ulong handle, byte[]? buffer, int capacity)
    {
        ThrowIfDisposed();
        return lastError(handle, buffer, (nuint)capacity);
    }

    /// <summary>
    /// 释放本实例。
    /// </summary>
    /// <remarks>
    /// <b>刻意不卸载模块</b>（不调 <see cref="NativeLibrary.Free"/>）：
    /// 模块一旦卸载，别的实例若还持有指向它的函数指针就会崩；
    /// 而进程级 DllImport 解析器可能随时被调用，它需要一个始终有效的目标。
    /// 原生库只有一份、且随进程存活，<b>不卸载</b>是安全且唯一正确的选择。
    /// 本方法只把实例标记为已释放，重复调用会被忽略。
    /// </remarks>
    public void Dispose()
    {
        disposed = true;
    }

    private static T Get<T>(IntPtr library, string export) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, export));

    private void ThrowIfDisposed()
    {
        if (disposed)
        {
            throw new ObjectDisposedException(nameof(NativeApi), "原生内核实例已释放，不能再发起调用。");
        }
    }

    private static string[] BuildLibraryFileCandidates()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Windows 的 LoadLibrary 会自动补 ".dll"，两个名字都试是为了覆盖
            // 「只装了 trit_rt.dll 但环境变量把名字改过」这类边角情况。
            return new[] { "trit_rt", "trit_rt.dll" };
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return new[] { "libtrit_rt.dylib", "trit_rt.dylib" };
        }

        return new[] { "libtrit_rt.so", "trit_rt.so" };
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint NativeAbiVersion();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint NativeVersion();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate ulong NativeCreate(byte[]? config, nuint len);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NativeDestroy(ulong handle);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NativeSubmit(
        ulong handle,
        uint opcode,
        byte[]? payload,
        nuint len,
        byte[]? output,
        nuint capacity);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NativeStep(ulong handle, ulong tick, byte[]? output, nuint capacity);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NativeSnapshot(ulong handle, byte[]? output, nuint capacity);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NativeDiff(
        ulong handle,
        ulong snapshotA,
        ulong snapshotB,
        byte[]? output,
        nuint capacity);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate ulong NativeCapabilities(ulong handle);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NativeLastError(ulong handle, byte[]? buffer, nuint capacity);
}

/// <summary>
/// trit_rt.dll 的进程级静态绑定（C ABI，见 <c>docs/ABI.md</c>）。
/// </summary>
/// <remarks>
/// <para>
/// <b>用途受限，请勿用于带句柄的调用。</b>
/// 静态 <c>DllImport</c> 只能通过<b>一个</b>全局解析器定位模块，
/// 而进程里可能同时加载着多份 trit_rt——此时「DllImport 落到哪一份」
/// 取决于登记顺序，无法确定。
/// </para>
/// <para>
/// 所以：<b>带句柄</b>的调用（创建、提交、销毁、快照……）一律走
/// <see cref="NativeApi"/>——它按实例绑定，绝不会串台。
/// 本类只保留两类调用：
/// </para>
/// <list type="bullet">
/// <item><description>
/// <b>与实例无关的查询</b>：<see cref="AbiVersion"/> / <see cref="Version"/>。
/// 它们读的是模块头部的常量，与句柄无关。
/// </description></item>
/// <item><description>
/// <b>历史兼容</b>：其余成员保留是为了不破坏既有调用方，
/// 但它们在多实例场景下的行为<b>不确定</b>，新代码请用 <see cref="NativeApi"/>。
/// </description></item>
/// </list>
/// </remarks>
public static class NativeBindings
{
    /// <summary>原生库名；加载顺序由 <see cref="NativeLibrary"/> 解析规则 + 显式路径决定。</summary>
    public const string LibraryName = "trit_rt";

    /// <summary>ABI 版本号。</summary>
    [DllImport(LibraryName, EntryPoint = "tt_rt_abi_version", CallingConvention = CallingConvention.Cdecl)]
    public static extern uint AbiVersion();

    /// <summary>运行时版本号。</summary>
    [DllImport(LibraryName, EntryPoint = "tt_rt_version", CallingConvention = CallingConvention.Cdecl)]
    public static extern uint Version();

    /// <summary>创建运行时；config 为 JSON 字节（可为空 = 默认配置）。失败返回 0。</summary>
    [DllImport(LibraryName, EntryPoint = "tt_rt_create", CallingConvention = CallingConvention.Cdecl)]
    public static extern ulong Create(byte[]? config, int len);

    /// <summary>销毁运行时。</summary>
    [DllImport(LibraryName, EntryPoint = "tt_rt_destroy", CallingConvention = CallingConvention.Cdecl)]
    public static extern int Destroy(ulong handle);

    /// <summary>提交一个操作。<b>多实例场景下行为不确定，请改用 <see cref="NativeApi.Submit"/>。</b></summary>
    [DllImport(LibraryName, EntryPoint = "tt_rt_submit", CallingConvention = CallingConvention.Cdecl)]
    public static extern int Submit(ulong handle, uint opcode, byte[]? payload, int len, byte[]? output, int capacity);

    /// <summary>推进一个 tick。<b>多实例场景下行为不确定，请改用 <see cref="NativeApi.Step"/>。</b></summary>
    [DllImport(LibraryName, EntryPoint = "tt_rt_step", CallingConvention = CallingConvention.Cdecl)]
    public static extern int Step(ulong handle, ulong tick, byte[]? output, int capacity);

    /// <summary>打快照并把快照字节写进 output。<b>多实例场景下行为不确定，请改用 <see cref="NativeApi.Snapshot"/>。</b></summary>
    [DllImport(LibraryName, EntryPoint = "tt_rt_snapshot", CallingConvention = CallingConvention.Cdecl)]
    public static extern int Snapshot(ulong handle, byte[]? output, int capacity);

    /// <summary>差分两个快照。<b>多实例场景下行为不确定，请改用 <see cref="NativeApi.Diff"/>。</b></summary>
    [DllImport(LibraryName, EntryPoint = "tt_rt_diff", CallingConvention = CallingConvention.Cdecl)]
    public static extern int Diff(ulong handle, ulong snapshotA, ulong snapshotB, byte[]? output, int capacity);

    /// <summary>能力位图；句柄非法返回 0。<b>多实例场景下行为不确定，请改用 <see cref="NativeApi.Capabilities"/>。</b></summary>
    [DllImport(LibraryName, EntryPoint = "tt_rt_capabilities", CallingConvention = CallingConvention.Cdecl)]
    public static extern ulong Capabilities(ulong handle);

    /// <summary>最近一次错误信息。<b>多实例场景下行为不确定，请改用 <see cref="NativeApi.LastError"/>。</b></summary>
    [DllImport(LibraryName, EntryPoint = "tt_rt_last_error", CallingConvention = CallingConvention.Cdecl)]
    public static extern int LastError(ulong handle, byte[]? buffer, int capacity);
}
