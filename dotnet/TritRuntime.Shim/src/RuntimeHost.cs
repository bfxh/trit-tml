using System.Reflection;
using System.Runtime.InteropServices;
using TritRuntime.Wire;

namespace TritRuntime.Kernel;

/// <summary>宿主选项。</summary>
public sealed class RuntimeHostOptions
{
    /// <summary>原生库显式路径（优先于其他搜索位置）。</summary>
    public string? LibraryPath { get; init; }

    /// <summary>附加搜索目录（按顺序探测）。</summary>
    public IReadOnlyList<string> SearchDirectories { get; init; } = Array.Empty<string>();

    /// <summary>运行时配置 JSON（空 = 默认）。</summary>
    public string ConfigJson { get; init; } = string.Empty;

    /// <summary>强制使用托管回退内核（测试 / 诊断用）。</summary>
    public bool ForceManaged { get; init; }

    /// <summary>
    /// 影子模式：同时装载第二个内核，对同一请求序列做逐字节差分。
    /// 原生不可用时第二个内核也是托管的（用于验证差分器自身）。
    /// </summary>
    public bool ShadowMode { get; init; }

    /// <summary>
    /// 是否允许默认探测（兼容壳自身目录、进程入口目录、系统搜索路径）。
    /// 置 false 时只认 <see cref="LibraryPath"/> 与 <see cref="SearchDirectories"/>——
    /// 嵌入方需要精确控制 DLL 来源时使用，也让回退路径可确定性地测试。
    /// </summary>
    public bool AllowDefaultProbing { get; init; } = true;
}

/// <summary>原生库装载失败的诊断信息。</summary>
public sealed class NativeLoadReport
{
    /// <summary>是否成功装载原生内核。</summary>
    public bool Loaded { get; }

    /// <summary>失败原因（成功时为 null）。</summary>
    public string? Reason { get; }

    /// <summary>实际尝试过的候选路径。</summary>
    public IReadOnlyList<string> AttemptedPaths { get; }

    /// <summary>构造装载报告。</summary>
    public NativeLoadReport(bool loaded, string? reason, IReadOnlyList<string> attemptedPaths)
    {
        Loaded = loaded;
        Reason = reason;
        AttemptedPaths = attemptedPaths;
    }
}

/// <summary>
/// 运行时宿主：能力协商 + 原生/托管选择 + 失败回退。
/// </summary>
/// <remarks>
/// 这是「兼容性不会炸」的第一道闸门：原生库缺失、ABI 不符、版本过低、
/// 句柄创建失败——任何一种都只降级到语义等价的托管内核，绝不向模组抛异常。
/// 是否降级、为什么降级，都能通过 <see cref="Mode"/> / <see cref="FallbackReason"/>
/// 观测到；影子模式下还能对两个内核做逐字节差分。
/// </remarks>
public sealed class RuntimeHost : IDisposable
{
    /// <summary>环境变量：显式指定原生库路径。</summary>
    public const string LibraryPathEnvironmentVariable = "TRIT_RT_LIBRARY";

    private readonly List<ITritKernel> owned = new();
    private bool disposed;

    private RuntimeHost(ITritKernel kernel, NativeLoadReport report, RuntimeHostOptions options)
    {
        Kernel = kernel;
        NativeLoad = report;
        Options = options;
        if (options.ShadowMode)
        {
            // 影子内核尽量取「另一种实现」：原生主内核 → 托管影子；托管主内核
            // （原生不可用或强制托管）→ 仍用托管影子，此时只验证差分器自身。
            var config = ManagedConfig.FromJson(ConfigBytes(options));
            ShadowKernel = new ManagedKernel(config);
            owned.Add(ShadowKernel);
            Differ = new ShadowDiffer(kernel, ShadowKernel);
        }
    }

    /// <summary>当前生效的内核。</summary>
    public ITritKernel Kernel { get; }

    /// <summary>影子内核（仅影子模式非 null）。</summary>
    public ITritKernel? ShadowKernel { get; }

    /// <summary>影子差分器（仅影子模式非 null）。</summary>
    public ShadowDiffer? Differ { get; }

    /// <summary>当前运行模式。</summary>
    public KernelMode Mode => Kernel.Mode;

    /// <summary>能力报告。</summary>
    public CapabilityReport Capabilities => Kernel.Capabilities();

    /// <summary>非 null 表示已回退到托管内核，值为原因。</summary>
    public string? FallbackReason => NativeLoad.Loaded ? null : NativeLoad.Reason;

    /// <summary>原生库装载诊断。</summary>
    public NativeLoadReport NativeLoad { get; }

    /// <summary>生效的选项。</summary>
    public RuntimeHostOptions Options { get; }

    /// <summary>是否具备某能力。</summary>
    public bool Supports(CapabilityBits bit) => Capabilities.Has(bit);

    /// <summary>
    /// 创建宿主。除参数为空这类编程错误外不抛异常：原生装载失败自动降级。
    /// </summary>
    public static RuntimeHost Create(RuntimeHostOptions? options = null)
    {
        options ??= new RuntimeHostOptions();
        var configBytes = ConfigBytes(options);

        if (options.ForceManaged)
        {
            var managed = new ManagedKernel(ManagedConfig.FromJson(configBytes));
            return new RuntimeHost(
                managed,
                new NativeLoadReport(false, "已按选项强制使用托管回退内核", Array.Empty<string>()),
                options);
        }

        var report = TryLoadNative(options, configBytes, out var native);
        if (native is not null)
        {
            return new RuntimeHost(native, report, options);
        }

        var fallback = new ManagedKernel(ManagedConfig.FromJson(configBytes));
        return new RuntimeHost(fallback, report, options);
    }

    /// <summary>销毁宿主及影子内核。</summary>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        Differ?.Dispose();
        foreach (var kernel in owned)
        {
            kernel.Dispose();
        }

        owned.Clear();
        Kernel.Dispose();
        GC.SuppressFinalize(this);
    }

    private static byte[]? ConfigBytes(RuntimeHostOptions options) =>
        string.IsNullOrEmpty(options.ConfigJson) ? null : System.Text.Encoding.UTF8.GetBytes(options.ConfigJson);

    private static NativeLoadReport TryLoadNative(
        RuntimeHostOptions options,
        byte[]? configBytes,
        out NativeKernel? kernel)
    {
        var attempted = new List<string>();
        foreach (var candidate in CandidatePaths(options))
        {
            attempted.Add(candidate);
            try
            {
                kernel = NativeKernel.Load(candidate, options.ConfigJson);
                return new NativeLoadReport(true, null, attempted);
            }
            catch (Exception error) when (error is DllNotFoundException or BadImageFormatException
                or InvalidOperationException or ArgumentException)
            {
                // 换下一个候选；全部失败则降级。
            }
        }

        // 最后尝试依赖系统搜索路径 / apphost 探测规则。
        if (options.AllowDefaultProbing)
        {
            try
            {
                kernel = NativeKernel.LoadByLibraryName(options.ConfigJson);
                return new NativeLoadReport(true, null, attempted);
            }
            catch (Exception error) when (error is DllNotFoundException or BadImageFormatException
                or InvalidOperationException or EntryPointNotFoundException)
            {
                kernel = null;
                var where = attempted.Count == 0 ? "系统搜索路径" : string.Join("; ", attempted);
                return new NativeLoadReport(
                    false,
                    $"原生三进制运行时不可用，已回退到托管内核：{error.Message}（尝试过：{where}）",
                    attempted);
            }
        }

        kernel = null;
        var described = attempted.Count == 0 ? "（未配置任何候选路径）" : string.Join("; ", attempted);
        return new NativeLoadReport(
            false,
            $"原生三进制运行时不可用，已回退到托管内核：已禁用默认探测，仅尝试过 {described}",
            attempted);
    }

    /// <summary>候选原生库路径，按优先级排列。</summary>
    public static IReadOnlyList<string> CandidatePaths(RuntimeHostOptions options)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrEmpty(options.LibraryPath))
        {
            candidates.Add(options.LibraryPath);
        }

        var fromEnvironment = Environment.GetEnvironmentVariable(LibraryPathEnvironmentVariable);
        if (!string.IsNullOrEmpty(fromEnvironment))
        {
            candidates.Add(fromEnvironment);
        }

        foreach (var directory in options.SearchDirectories)
        {
            if (!string.IsNullOrEmpty(directory))
            {
                candidates.Add(Path.Combine(directory, LibraryFileName));
            }
        }

        if (!options.AllowDefaultProbing)
        {
            return candidates;
        }

        // 兼容壳自身所在目录（tModLoader 会把模组输出平铺到 Mods/<模组名>/）。
        candidates.Add(Path.Combine(AppContext.BaseDirectory, LibraryFileName));

        // 进程主程序目录。
        var entry = Assembly.GetEntryAssembly()?.Location;
        if (!string.IsNullOrEmpty(entry))
        {
            candidates.Add(Path.Combine(Path.GetDirectoryName(entry)!, LibraryFileName));
        }

        return candidates;
    }

    /// <summary>当前平台的原生库文件名。</summary>
    public static string LibraryFileName =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "trit_rt.dll"
        : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "libtrit_rt.dylib"
        : "libtrit_rt.so";
}
