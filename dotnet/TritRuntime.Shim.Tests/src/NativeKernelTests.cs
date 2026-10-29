#nullable enable

using System.Runtime.InteropServices;
using TritRuntime.Kernel;
using TritRuntime.Wire;
using Xunit;
using WireFormat = TritRuntime.Wire.Wire;

namespace TritRuntime.Shim.Tests;

/// <summary>
/// 原生内核定位：测试进程的工作目录不固定，按候选路径逐个探测。
/// </summary>
internal static class NativeLibraryLocator
{
    /// <summary>按当前平台给出原生库文件名（与 NativeBindings 的兜底候选同口径）。</summary>
    private static string[] FileNames()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return new[] { "trit_rt.dll" };
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return new[] { "libtrit_rt.dylib", "trit_rt.dylib" };
        }

        return new[] { "libtrit_rt.so", "trit_rt.so" };
    }

    /// <summary>按当前平台找原生库；找不到返回 null（parity 测试据此跳过）。</summary>
    public static string? Find()
    {
        // 显式环境变量优先级最高：跨平台 CI 可以直接把编译产物路径喂进来，
        // 覆盖掉下面所有探测路径。存在但文件缺失时仍继续走自动探测。
        var fromEnvironment = Environment.GetEnvironmentVariable(RuntimeHost.LibraryPathEnvironmentVariable);
        if (!string.IsNullOrEmpty(fromEnvironment) && File.Exists(fromEnvironment))
        {
            return fromEnvironment;
        }

        var candidates = new List<string>();
        void Add(string? path)
        {
            if (!string.IsNullOrEmpty(path) && !candidates.Contains(path))
            {
                candidates.Add(path);
            }
        }

        var names = FileNames();

        // 仓库约定：target/release/<平台库名>（相对测试程序目录逐级上溯）。
        // CI 上先 `cargo build --release` 再 `dotnet test`，产物就在这一层。
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; depth < 8 && directory is not null; depth++)
        {
            foreach (var name in names)
            {
                Add(Path.Combine(directory.FullName, "target", "release", name));
            }

            directory = directory.Parent;
        }

        // 测试输出目录（csproj 按平台 CopyToOutputDirectory 的那份）。
        foreach (var name in names)
        {
            Add(Path.Combine(AppContext.BaseDirectory, name));
        }

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}

/// <summary>原生内核测试基类：库缺失时跳过并说明原因。</summary>
public abstract class NativeKernelTestBase
{
    /// <summary>原生库路径；null 表示本机没有构建产物。</summary>
    protected static readonly string? LibraryPath = NativeLibraryLocator.Find();

    /// <summary>原生库缺失时的提示语。</summary>
    protected const string MissingLibraryHint =
        "本机没有 target/release/trit_rt.dll；先在仓库根目录执行 cargo build --release -p trit-rt";

    /// <summary>装载原生内核；库缺失时失败并给出可操作提示（不静默跳过）。</summary>
    protected static NativeKernel LoadNative(string configJson = "")
    {
        if (LibraryPath is null)
        {
            throw new InvalidOperationException(MissingLibraryHint);
        }

        return NativeKernel.Load(LibraryPath, configJson);
    }
}

/// <summary>原生内核（Rust trit_rt.dll）测试；本机没有构建产物时自动跳过。</summary>
public sealed class NativeKernelTests : NativeKernelTestBase
{
    /// <summary>
    /// 回归：显式路径加载时，落盘文件名与 DllImport 声明名<b>不一致</b>也必须加载成功。
    /// </summary>
    /// <remarks>
    /// 真机（tML 专用服务器实测）上原生库从 <c>.tmod</c> 取出后按「内容摘要 + 文件名」落盘，
    /// 变成 <c>f5324a5d3721286d_trit_rt.dll</c>。而登记到解析器的名字曾是从路径推导的，
    /// 于是得到 <c>f5324a5d3721286d_trit_rt</c>，与 <see cref="NativeBindings.LibraryName"/>
    /// 常量 <c>trit_rt</c> 不等，解析器永不命中，退回默认探测后抛
    /// <c>DllNotFoundException: Unable to load DLL 'trit_rt' ... (0x8007007E)</c>。
    /// 测试进程里 dll 恰好就叫 trit_rt.dll，所以这条路径长期没被覆盖到。
    /// </remarks>
    [Fact]
    public void ExplicitPathLoadSucceedsWhenFileNameIsPrefixed()
    {
        var source = LibraryPath ?? throw new InvalidOperationException(MissingLibraryHint);

        var directory = Path.Combine(Path.GetTempPath(), "trit-shim-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var staged = Path.Combine(directory, "deadbeefcafe1234_trit_rt.dll");
        File.Copy(source, staged, overwrite: true);

        try
        {
            using var kernel = NativeKernel.Load(staged);
            Assert.Equal(KernelMode.Native, kernel.Mode);
            Assert.Equal(NativeKernel.ExpectedAbiVersion, NativeBindings.AbiVersion());

            // 不只是加载成功，还要真的能用：订阅 → 发布 → 推进。
            kernel.Subscribe(1, 100, 0, Array.Empty<uint>());
            kernel.Publish(100, new[] { TritDelta.Pos });
            Assert.NotEmpty(kernel.Step(1).Intents);
        }
        finally
        {
            // Windows 上已加载的 DLL 文件被进程锁定，删不掉——忽略即可，目录在临时目录下。
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception)
            {
                // 清理失败不影响测试结论
            }
        }
    }

    [Fact]
    public void AbiAndVersionAreAccepted()
    {
        using var kernel = LoadNative();
        Assert.Equal(NativeKernel.ExpectedAbiVersion, NativeBindings.AbiVersion());
        Assert.True(NativeBindings.Version() >= NativeKernel.MinimumRuntimeVersion);
    }

    [Fact]
    public void CapabilitiesReportNativeMode()
    {
        using var kernel = LoadNative();
        var report = kernel.Capabilities();
        Assert.Equal(KernelMode.Native, report.Mode);
        Assert.Equal(NativeKernel.ExpectedAbiVersion, report.AbiVersion);
        Assert.True(report.Has(CapabilityBits.Router));
        Assert.True(report.Has(CapabilityBits.TxnLog));
        Assert.True(report.Has(CapabilityBits.Snapshot));
        Assert.True(report.Has(CapabilityBits.Diff));
        Assert.True(report.Has(CapabilityBits.Arbitrate));
        Assert.True(report.Has(CapabilityBits.Prng));
        Assert.True(report.Has(CapabilityBits.Codec));
        Assert.True(report.Has(CapabilityBits.Shadow));
        Assert.True(report.Has(CapabilityBits.Ai), "默认特性应启用 AI 投票");
    }

    [Fact]
    public void SubscribePublishStepRoundTrip()
    {
        using var kernel = LoadNative();
        var a = kernel.Subscribe(1, 100, 0, Array.Empty<uint>());
        var b = kernel.Subscribe(2, 101, 0, new uint[] { 100 });
        Assert.Equal(2u, kernel.Publish(100, new[] { TritDelta.Pos, TritDelta.Neg, TritDelta.Pos }));
        var dispatch = kernel.Step(1).Intents;
        Assert.Equal(new[] { a, b }, dispatch.Select(intent => intent.SubId).ToArray());
    }

    [Fact]
    public void TransactionAndSnapshotRoundTrip()
    {
        using var kernel = LoadNative();
        var txn = kernel.BeginTransaction();
        kernel.SubmitIntent(txn, 5, TritDelta.Pos);
        var commit = kernel.Commit(txn);
        Assert.Equal(1u, commit.Applied);

        var snapshot = kernel.Snapshot();
        Assert.True(snapshot > 0);
        kernel.SubmitIntent(0, 5, TritDelta.Neg);
        var diff = kernel.Diff(snapshot, kernel.Snapshot());
        var entry = Assert.Single(diff.Entries);
        Assert.Equal(5u, entry.Key);
        Assert.Equal(TritDelta.Pos, entry.Old);
        Assert.Equal(TritDelta.Zero, entry.New);
    }

    [Fact]
    public void SnapshotBytesCarryIdPrefixAndWorld()
    {
        using var kernel = LoadNative();
        kernel.Step(4);
        kernel.SubmitIntent(0, 4242, TritDelta.Pos);
        var bytes = kernel.SnapshotBytes();

        // 布局：[u64 id][世界编码]，不装帧（与 ManagedKernel 逐字节一致）。
        var reader = new WireReader(bytes);
        var id = reader.U64();
        var (tick, _, world) = WorldCodec.Decode(reader.Bytes(reader.Remaining));
        Assert.True(id > 0);
        Assert.Equal(4ul, tick);
        Assert.Equal(TritDelta.Pos, world[4242]);
    }

    [Fact]
    public void BadLibraryPathThrowsInsteadOfSilentlyFallingBack()
    {
        if (LibraryPath is null)
        {
            throw new InvalidOperationException(MissingLibraryHint);
        }

        var missing = Path.Combine(Path.GetTempPath(), "definitely-not-trit-rt.dll");
        var error = Assert.ThrowsAny<Exception>(() => NativeKernel.Load(missing));
        Assert.Contains("definitely-not-trit-rt", error.Message);
    }

    [Fact]
    public void BadHandleIsReportedAsBadHandle()
    {
        if (LibraryPath is null)
        {
            throw new InvalidOperationException(MissingLibraryHint);
        }

        // 句柄 0 永远非法（不依赖具体库实例）。
        var status = NativeBindings.Submit(0, Opcode.Capabilities, null, 0, new byte[256], 256);
        Assert.Equal(Status.BadHandle, status);
    }
}
