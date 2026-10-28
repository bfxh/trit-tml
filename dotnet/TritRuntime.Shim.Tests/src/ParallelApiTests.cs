#nullable enable

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using TritRuntime.Kernel;
using TritRuntime.Wire;
using Xunit;

namespace TritRuntime.Shim.Tests;

/// <summary>v2 并行扫描、GPU 与预算 API 测试。</summary>
public sealed class ParallelApiTests
{
    [Fact]
    public void ManagedHistogramUsesTernaryValueCounts()
    {
        using var kernel = new ManagedKernel();
        var result = SubmitAndPoll(kernel, new ScanRequest
        {
            Owner = "hist",
            Kind = ScanKind.Histogram,
            Trits = new byte[] { 0, 1, 2, 0, 2, 0 },
        });

        Assert.Equal(JobState.Done, result.State);
        Assert.True(ScanResultJson.TryGetHistogram(result.ResultJson, out var neg, out var zero, out var pos));
        Assert.Equal(3, neg);
        Assert.Equal(1, zero);
        Assert.Equal(2, pos);
    }

    [Fact]
    public void ManagedPatternDefaultThresholdRequiresFullScore()
    {
        using var kernel = new ManagedKernel();
        var result = SubmitAndPoll(kernel, new ScanRequest
        {
            Owner = "pattern",
            Kind = ScanKind.Pattern,
            Trits = new byte[] { 2, 2, 0, 2, 2 },
            Pattern = new byte[] { 2, 2 },
            Threshold = 0,
        });

        Assert.Equal(JobState.Done, result.State);
        Assert.Equal(new[] { (0L, 2L), (3L, 2L) }, ScanResultJson.TryGetMatches(result.ResultJson));
        Assert.True(ScanResultJson.TryGetScanned(result.ResultJson, out var scanned));
        Assert.Equal(4, scanned);
        Assert.Equal("cpu", ScanResultJson.TryGetBackend(result.ResultJson));
    }

    [Fact]
    public void ManagedPatternLongerThanTheDataHasNoMatches()
    {
        using var kernel = new ManagedKernel();
        var result = SubmitAndPoll(kernel, new ScanRequest
        {
            Kind = ScanKind.Pattern,
            Trits = new byte[] { 2, 0 },
            Pattern = new byte[] { 2, 0, 2 },
        });

        Assert.Equal(JobState.Done, result.State);
        Assert.Empty(ScanResultJson.TryGetMatches(result.ResultJson));
        Assert.True(ScanResultJson.TryGetScanned(result.ResultJson, out var scanned));
        Assert.Equal(0, scanned);
    }

    /// <summary>
    /// <b>空 pattern 是非法请求</b>：产出一条 failed 任务，而不是「跑完但一个都没匹配上」。
    /// </summary>
    /// <remarks>
    /// 原生内核与托管回退内核必须一致：否则同一份请求在原生侧失败、在托管侧成功，
    /// 影子差分会一直报分歧，而且两边给出的结果含义完全不同
    /// （一个是「参数错了」，一个是「确实没有命中」）。
    /// </remarks>
    [Fact]
    public void ManagedPatternEmptyIsRejectedAsAFailedJob()
    {
        using var kernel = new ManagedKernel();
        var result = SubmitAndPoll(kernel, new ScanRequest
        {
            Kind = ScanKind.Pattern,
            Trits = new byte[] { 2, 0 },
            Pattern = Array.Empty<byte>(),
        });

        Assert.Equal(JobState.Failed, result.State);
        Assert.Contains("扫描需要非空 pattern", result.Error);
        Assert.Equal(string.Empty, result.Backend);
    }

    [Fact]
    public void ManagedDensityUsesSemanticNonZeroTopAndStableTieOrder()
    {
        using var kernel = new ManagedKernel();
        var result = SubmitAndPoll(kernel, new ScanRequest
        {
            Kind = ScanKind.Density,
            Trits = new byte[] { 0, 2, 2, 0, 1, 2 },
            Chunk = 2,
            Top = 2,
        });

        var blocks = ScanResultJson.TryGetBlocks(result.ResultJson);
        Assert.Equal(2, blocks.Count);
        Assert.Equal((0L, 0L, 2L, 1.0), blocks[0]);
        Assert.Equal((1L, 2L, 2L, 1.0), blocks[1]);
        Assert.Contains("\"density\":1.000000", result.ResultJson);
    }

    [Fact]
    public void ManagedDotTruncatesToShorterLength()
    {
        using var kernel = new ManagedKernel();
        var result = SubmitAndPoll(kernel, new ScanRequest
        {
            Kind = ScanKind.Dot,
            Trits = new byte[] { 0, 1, 2, 2 },
            Pattern = new byte[] { 2, 0 },
        });

        Assert.True(ScanResultJson.TryGetDot(result.ResultJson, out var dot));
        Assert.Equal(-1, dot);
        using var json = JsonDocument.Parse(result.ResultJson);
        Assert.Equal(2, json.RootElement.GetProperty("length").GetInt32());
    }

    [Theory]
    [InlineData((byte)3)]
    [InlineData(byte.MaxValue)]
    public void InvalidTritProducesFailedJobWithoutThrowing(byte invalid)
    {
        using var kernel = new ManagedKernel();
        var result = SubmitAndPoll(kernel, new ScanRequest
        {
            Kind = ScanKind.Histogram,
            Trits = new[] { (byte)0, invalid, (byte)2 },
        });

        Assert.Equal(JobState.Failed, result.State);
        Assert.Contains("非法 trit 值", result.Error);
    }

    [Fact]
    public void EmptyOwnerIsUnknownAndUnknownKindFails()
    {
        using var kernel = new ManagedKernel();
        var owner = SubmitAndPoll(kernel, new ScanRequest
        {
            Owner = string.Empty,
            Kind = ScanKind.Histogram,
            Trits = Array.Empty<byte>(),
        });
        Assert.Equal("unknown", owner.Owner);

        var invalidKind = SubmitAndPoll(kernel, new ScanRequest
        {
            Kind = (ScanKind)99,
            Trits = Array.Empty<byte>(),
        });
        Assert.Equal(JobState.Failed, invalidKind.State);
        Assert.Contains("未知扫描 kind", invalidKind.Error);
    }

    [Fact]
    public void ManagedJsonKindParsingIsCaseInsensitive()
    {
        using var kernel = new ManagedKernel();
        var submit = kernel.Submit(
            Opcode.ScanSubmit,
            Encoding.UTF8.GetBytes("{\"owner\":\"case\",\"kind\":\"HiStOgRaM\",\"trits\":[0,1,2]}"));

        var id = KernelResponseMapper.ToJobId(submit);
        Assert.True(id > 0);
        var result = Assert.Single(kernel.PollJobs());
        Assert.Equal(JobState.Done, result.State);
    }

    [Fact]
    public void ManagedGpuBudgetAndMetricsExposeSafeFallbackState()
    {
        using var kernel = new ManagedKernel();
        Assert.Equal(string.Empty, kernel.MetricsPrometheus());

        var off = kernel.GpuStatus(GpuPolicy.Off, probe: true);
        Assert.False(off.Available);
        Assert.False(off.Enabled);
        Assert.False(off.BuiltWithGpu);
        Assert.Equal(GpuPolicy.Off, off.Policy);
        Assert.Equal("托管回退内核不含 GPU 路径", off.Reason);
        Assert.Equal(GpuPolicy.Auto, kernel.GpuStatus(GpuPolicy.Auto).Policy);

        SubmitAndPoll(kernel, new ScanRequest
        {
            Owner = "budget",
            Kind = ScanKind.Histogram,
            Trits = new byte[] { 0, 1, 2 },
        });
        var budget = kernel.Budget();
        Assert.Equal(1, budget.Workers);
        Assert.Equal(1, budget.CpuCapacity);
        Assert.Equal(0, budget.MemoryCapacity);
        Assert.Equal(1, budget.PerModInflight);
        Assert.Null(budget.LastMetrics);
        var mod = Assert.Single(budget.Mods);
        Assert.Equal("budget", mod.Owner);
        Assert.Equal(1UL, mod.Submitted);
        Assert.Equal(1UL, mod.Completed);

        var metrics = kernel.MetricsPrometheus();
        Assert.Contains("trit_schedule_started 1", metrics);
        Assert.Contains("trit_schedule_completed 1", metrics);
        Assert.Contains("trit_schedule_steps", metrics);
    }

    [Fact]
    public void ManagedPollingDequeuesResultsAndCancellationIsZero()
    {
        using var kernel = new ManagedKernel();
        var first = kernel.SubmitScan(new ScanRequest { Kind = ScanKind.Histogram });
        var second = kernel.SubmitScan(new ScanRequest { Kind = ScanKind.Histogram });
        Assert.Equal(0, kernel.CancelJob(first));
        Assert.Equal(0, kernel.CancelOwner("unknown"));
        Assert.Single(kernel.PollJobs(1));
        Assert.Single(kernel.PollJobs());
        Assert.Empty(kernel.PollJobs());
        Assert.True(second > first);
    }

    [Fact]
    public void WireV2ConstantsMatchAbi()
    {
        Assert.Equal(0x14u, Opcode.ScanSubmit);
        Assert.Equal(0x15u, Opcode.JobPoll);
        Assert.Equal(0x16u, Opcode.JobCancel);
        Assert.Equal(0x17u, Opcode.GpuStatus);
        Assert.Equal(0x18u, Opcode.Budget);
        Assert.Equal(0x19u, Opcode.Metrics);
        Assert.Equal(1UL << 9, (ulong)CapabilityBits.Parallel);
        Assert.Equal(1UL << 10, (ulong)CapabilityBits.Gpu);
        Assert.Equal(1UL << 11, (ulong)CapabilityBits.Budget);
    }

    [Fact]
    public void PollingByOwnerReturnsOnlyThatOwnersResults()
    {
        using var kernel = new ManagedKernel();
        kernel.SubmitScan(new ScanRequest
        {
            Owner = "alpha",
            Kind = ScanKind.Histogram,
            Trits = new byte[] { 0, 1, 2 },
        });
        kernel.SubmitScan(new ScanRequest
        {
            Owner = "beta",
            Kind = ScanKind.Histogram,
            Trits = new byte[] { 0, 1, 2 },
        });

        var alpha = kernel.PollJobs(0, "alpha");
        Assert.Single(alpha);
        Assert.Equal("alpha", alpha[0].Owner);

        // beta 的那条必须还在：alpha 的轮询不能吃掉别人的结果。
        var beta = kernel.PollJobs(0, "beta");
        Assert.Single(beta);
        Assert.Equal("beta", beta[0].Owner);
    }

    [Fact]
    public void PollingByOwnerLeavesOtherOwnersInTheQueue()
    {
        using var kernel = new ManagedKernel();
        for (var index = 0; index < 3; index++)
        {
            kernel.SubmitScan(new ScanRequest
            {
                Owner = "alpha",
                Kind = ScanKind.Histogram,
                Trits = new byte[] { 0, 1, 2 },
            });
            kernel.SubmitScan(new ScanRequest
            {
                Owner = "beta",
                Kind = ScanKind.Histogram,
                Trits = new byte[] { 0, 1, 2 },
            });
        }

        Assert.Equal(3, kernel.PollJobs(0, "alpha").Count);
        // 一条都不能少。
        Assert.Equal(3, kernel.PollJobs(0, "beta").Count);
        Assert.Empty(kernel.PollJobs());
    }

    [Fact]
    public void PollingWithoutOwnerStillReturnsEverything()
    {
        using var kernel = new ManagedKernel();
        kernel.SubmitScan(new ScanRequest { Owner = "alpha", Kind = ScanKind.Histogram });
        kernel.SubmitScan(new ScanRequest { Owner = "beta", Kind = ScanKind.Histogram });

        Assert.Equal(2, kernel.PollJobs().Count);
        Assert.Empty(kernel.PollJobs());
    }

    [Fact]
    public void AnEmptyOwnerFilterMatchesTheUnknownOwner()
    {
        using var kernel = new ManagedKernel();
        kernel.SubmitScan(new ScanRequest
        {
            Owner = string.Empty,
            Kind = ScanKind.Histogram,
            Trits = new byte[] { 0, 1, 2 },
        });

        // 空串归一化为 "unknown"，与提交侧同一口径。
        var byUnknown = kernel.PollJobs(0, "unknown");
        Assert.Single(byUnknown);
        Assert.Equal("unknown", byUnknown[0].Owner);
    }

    [Fact]
    public void AnUnknownOwnerGetsNothing()
    {
        using var kernel = new ManagedKernel();
        kernel.SubmitScan(new ScanRequest { Owner = "alpha", Kind = ScanKind.Histogram });
        Assert.Empty(kernel.PollJobs(0, "nobody"));
        // 没有被消费掉：alpha 的还在。
        Assert.Single(kernel.PollJobs(0, "alpha"));
    }

    [Fact]
    public void OwnerNamesAreTrimmedConsistently()
    {
        using var kernel = new ManagedKernel();
        kernel.SubmitScan(new ScanRequest
        {
            Owner = "  spaced  ",
            Kind = ScanKind.Histogram,
            Trits = new byte[] { 0, 1, 2 },
        });

        // 提交侧去空白 → 归属记为 "spaced"；过滤侧同样去空白才能配上。
        var jobs = kernel.PollJobs(0, "spaced");
        Assert.Single(jobs);
        Assert.Equal("spaced", jobs[0].Owner);
    }

    [Fact]
    public void NegativeLimitIsClampedInsteadOfFailing()
    {
        using var kernel = new ManagedKernel();
        kernel.SubmitScan(new ScanRequest { Owner = "m", Kind = ScanKind.Histogram });

        // 负数 = 全部（与原生路径同口径），不是解码失败。
        var jobs = kernel.PollJobs(-1);
        Assert.Single(jobs);
    }

    /// <summary>
    /// 回归：<c>NativeKernel.Load(path)</c> 必须真的加载 <b>path 那个模块</b>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 历史缺陷：兼容壳原先用静态 <c>DllImport</c> + 进程级 <c>SetDllImportResolver</c>
    /// 绑定原生库。而 .NET 会<b>缓存</b> DllImport 的首次解析结果，
    /// 所以第一次登记之后的路径<b>全部被静默忽略</b>——
    /// 无论 <c>Load</c> 传进来什么路径，实际用的都是「进程中第一个加载的 trit_rt」。
    /// </para>
    /// <para>
    /// 后果是<b>静默的错误行为</b>：加载一个根本不导出 <c>tt_rt_*</c> 的库也会
    /// 「成功」并返回 <see cref="KernelMode.Native"/>（ABI 校验读的是别的模块的版本），
    /// 直到销毁时才抛 <c>EntryPointNotFoundException</c> 崩掉。
    /// </para>
    /// <para>
    /// 真机影响：原生库按内容摘要分目录落盘，用户在设置里配了
    /// <c>LibraryPath</c> 指向某个目录（升级后的新版本、别的 mod 的副本）时，
    /// 这个配置<b>形同虚设</b>——实际跑的还是旧版本。
    /// </para>
    /// <para>
    /// 现在带句柄的调用一律走 <c>NativeApi</c>（按实例绑定导出函数指针），
    /// 每个实例只认自己加载的那个模块——本测试锁住这一点。
    /// </para>
    /// </remarks>
    [Fact]
    public void LoadingAnUnrelatedLibraryMustFailInsteadOfSilentlyUsingAnotherModule()
    {
        var source = NativeLibraryLocator.Find();
        if (source is null)
        {
            return;
        }

        // 先加载真正的 trit_rt，让进程里存在一个「可用的」模块。
        using var real = NativeKernel.Load(source);
        Assert.Equal(KernelMode.Native, real.Mode);

        // 再加载一个完全无关的系统库：它不导出任何 tt_rt_* 导出。
        var bogus = UnrelatedLibraryPath();
        if (bogus is null)
        {
            return;
        }

        var error = Assert.ThrowsAny<Exception>(() => NativeKernel.Load(bogus));
        Assert.Contains("tt_rt_", error.Message);

        // 关键：加载失败不能影响先前那个正常实例——它必须继续可用。
        Assert.Equal(KernelMode.Native, real.Mode);
        Assert.NotEqual(0u, real.Capabilities().AbiVersion);
    }

    /// <summary>取一个必然存在、且不导出 <c>tt_rt_*</c> 的系统库路径。</summary>
    private static string? UnrelatedLibraryPath()
    {
        if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var path = Path.Combine(Environment.SystemDirectory, "kernel32.dll");
            return File.Exists(path) ? path : null;
        }

        if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return File.Exists("/usr/lib/libSystem.B.dylib") ? "/usr/lib/libSystem.B.dylib" : null;
        }

        return File.Exists("/lib/x86_64-linux-gnu/libc.so.6") ? "/lib/x86_64-linux-gnu/libc.so.6" : null;
    }

    [Fact]
    public void TwoNativeInstancesEachUseTheirOwnModule()
    {
        var source = NativeLibraryLocator.Find();
        if (source is null)
        {
            return;
        }

        // 造一份「路径不同但内容相同」的副本：Windows 会把它加载成另一个模块实例。
        var directory = Path.Combine(Path.GetTempPath(), "trit-shim-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var staged = Path.Combine(directory, "deadbeefcafe1234_trit_rt.dll");
        File.Copy(source, staged, overwrite: true);

        try
        {
            using var first = NativeKernel.Load(source);
            using var second = NativeKernel.Load(staged);

            // 各自绑定自己的模块路径。
            Assert.Equal(Path.GetFullPath(source), Path.GetFullPath(first.LibraryPath));
            Assert.Equal(Path.GetFullPath(staged), Path.GetFullPath(second.LibraryPath));

            // 各自提交一个 owner 不同的任务；互不可见。
            first.SubmitScan(new ScanRequest
            {
                Owner = "first-a",
                Kind = ScanKind.Histogram,
                Trits = new byte[] { 0, 1, 2 },
            });
            second.SubmitScan(new ScanRequest
            {
                Owner = "second-a",
                Kind = ScanKind.Histogram,
                Trits = new byte[] { 0, 1, 2 },
            });

            var firstJobs = WaitForOwnerJobs(owner => first.PollJobs(0, owner), "first-a", 1);
            var secondJobs = WaitForOwnerJobs(owner => second.PollJobs(0, owner), "second-a", 1);

            Assert.Single(firstJobs);
            Assert.Equal("first-a", firstJobs[0].Owner);
            Assert.Single(secondJobs);
            Assert.Equal("second-a", secondJobs[0].Owner);

            // 交叉验证：对方 owner 一条都看不到。
            Assert.Empty(first.PollJobs(0, "second-a"));
            Assert.Empty(second.PollJobs(0, "first-a"));
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
    public void NativeAndManagedAgreeOnOwnerFiltering()
    {
        var path = NativeLibraryLocator.Find();
        if (path is null)
        {
            return;
        }

        NativeKernel native;
        try
        {
            native = NativeKernel.Load(path);
        }
        catch (InvalidOperationException)
        {
            return;
        }

        using (native)
        using (var managed = new ManagedKernel())
        {
            foreach (var kernel in new ITritKernel[] { native, managed })
            {
                kernel.SubmitScan(new ScanRequest
                {
                    Owner = "alpha",
                    Kind = ScanKind.Histogram,
                    Trits = new byte[] { 0, 1, 2 },
                });
                kernel.SubmitScan(new ScanRequest
                {
                    Owner = "beta",
                    Kind = ScanKind.Histogram,
                    Trits = new byte[] { 0, 1, 2 },
                });
            }

            // 原生侧是异步任务池，必须等结果就绪（见 WaitForOwnerJobs 的说明）。
            var nativeAlpha = WaitForOwnerJobs(owner => native.PollJobs(0, owner), "alpha", 1);
            var managedAlpha = WaitForOwnerJobs(owner => managed.PollJobs(0, owner), "alpha", 1);
            Assert.Single(nativeAlpha);
            Assert.Single(managedAlpha);
            Assert.Equal(nativeAlpha[0].Owner, managedAlpha[0].Owner);
            Assert.Equal(nativeAlpha[0].State, managedAlpha[0].State);

            // 剩下的一条都归对方，两边一致。
            var nativeBeta = WaitForOwnerJobs(owner => native.PollJobs(0, owner), "beta", 1);
            var managedBeta = WaitForOwnerJobs(owner => managed.PollJobs(0, owner), "beta", 1);
            Assert.Single(nativeBeta);
            Assert.Single(managedBeta);
            Assert.Empty(native.PollJobs(0, "beta"));
            Assert.Empty(managed.PollJobs(0, "beta"));
        }
    }

    [Fact]
    public void ShimExposesOwnerFilteredPolling()
    {
        using var shim = RuntimeShim.Attach();
        shim.SubmitScan(new ScanRequest
        {
            Owner = "alpha",
            Kind = ScanKind.Histogram,
            Trits = new byte[] { 0, 1, 2 },
        });
        shim.SubmitScan(new ScanRequest
        {
            Owner = "beta",
            Kind = ScanKind.Histogram,
            Trits = new byte[] { 0, 1, 2 },
        });

        var viaShim = WaitForOwnerJobs(owner => shim.PollJobs(0, owner), "alpha", 1);
        Assert.Single(viaShim);
        Assert.Equal("alpha", viaShim[0].Owner);

        // Mod.Call 的签名：(string owner, int limit)。
        var viaCall = WaitForOwnerJobs(
            owner => shim.Call("PollJobs", owner, 16) as IReadOnlyList<JobResult> ?? Array.Empty<JobResult>(),
            "beta",
            1);
        Assert.Single(viaCall);
        Assert.Equal("beta", viaCall[0].Owner);
    }

    [Fact]
    public void ScanRequestJsonHasExactNamesAndHandlesLargeArrays()
    {
        var trits = new byte[1_000_000];
        for (var index = 0; index < trits.Length; index++)
        {
            trits[index] = (byte)(index % 3);
        }

        var bytes = Requests.ScanSubmit(new ScanRequest
        {
            Owner = "large",
            Kind = ScanKind.Pattern,
            Trits = trits,
            Pattern = new byte[] { 2, 1, 0 },
            Threshold = 2,
            Chunk = 1024,
            Oneshot = true,
            Priority = 7,
            Top = 9,
        });
        using var json = JsonDocument.Parse(bytes);
        var root = json.RootElement;
        Assert.Equal("large", root.GetProperty("owner").GetString());
        Assert.Equal("pattern", root.GetProperty("kind").GetString());
        Assert.Equal(1_000_000, root.GetProperty("trits").GetArrayLength());
        Assert.Equal(3, root.GetProperty("pattern").GetArrayLength());
        Assert.Equal(2, root.GetProperty("threshold").GetInt32());
        Assert.Equal(1024, root.GetProperty("chunk").GetInt32());
        Assert.True(root.GetProperty("oneshot").GetBoolean());
        Assert.Equal(7, root.GetProperty("priority").GetInt32());
        Assert.Equal(9, root.GetProperty("top").GetInt32());
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("{\"jobs\":[")]
    public void MapperNeverThrowsForMalformedJson(string json)
    {
        var response = new KernelResponse(Status.Ok, Encoding.UTF8.GetBytes(json));
        Assert.Equal(0UL, KernelResponseMapper.ToJobId(response));
        Assert.Empty(KernelResponseMapper.ToJobResults(response));
        Assert.Equal(0, KernelResponseMapper.ToCancelled(response));
        Assert.False(KernelResponseMapper.ToGpuStatus(response).Available);
        Assert.Empty(KernelResponseMapper.ToBudget(response).Mods);
        Assert.Equal(string.Empty, KernelResponseMapper.ToPrometheus(response));
    }

    [Fact]
    public void NewModCallCommandsAcceptDocumentedShapes()
    {
        using var shim = RuntimeShim.Attach(new RuntimeHostOptions { ForceManaged = true });
        var id = Assert.IsType<ulong>(shim.Call("Scan", "mod", "histogram", new byte[] { 0, 1, 2 }));
        Assert.True(id > 0);
        Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<JobResult>>(shim.Call("PollJobs")));
        Assert.IsType<GpuStatusReport>(shim.Call("GpuStatus", "off", true));
        Assert.IsType<BudgetSnapshot>(shim.Call("Budget"));
        Assert.IsType<string>(shim.Call("Metrics"));
        Assert.Equal(0, shim.Call("CancelJob", (long)id));
        Assert.Equal(0, shim.Call("CancelOwner", "mod"));
        Assert.Contains(TritRuntimeCommands.Scan, TritRuntimeCommands.All);

        var unknown = Assert.Throws<ArgumentException>(() => shim.Call("Scan", "mod", "weird", Array.Empty<byte>()));
        Assert.Contains("histogram, pattern, density, dot", unknown.Message);
        var wrong = Assert.Throws<ArgumentException>(() => shim.Call("GpuStatus", 1));
        Assert.Contains("期望签名", wrong.Message);
    }

    [Fact]
    public void ManagedAndNativeScanResultsAgreeWhenV2NativeIsAvailable()
    {
        var path = NativeLibraryLocator.Find();
        if (path is null)
        {
            return;
        }

        NativeKernel native;
        try
        {
            native = NativeKernel.Load(path);
        }
        catch (InvalidOperationException)
        {
            return;
        }

        using (native)
        using (var managed = new ManagedKernel())
        {
            CompareHistogram(native, managed);
            ComparePattern(native, managed);
            CompareDot(native, managed);
        }
    }

    [Fact]
    public void NativeGpuPolicyCanRoundTripWhenV2NativeIsAvailable()
    {
        var path = NativeLibraryLocator.Find();
        if (path is null)
        {
            return;
        }

        NativeKernel native;
        try
        {
            native = NativeKernel.Load(path);
        }
        catch (InvalidOperationException)
        {
            return;
        }

        using (native)
        {
            try
            {
                Assert.Equal(GpuPolicy.Off, native.GpuStatus(GpuPolicy.Off).Policy);
            }
            finally
            {
                Assert.Equal(GpuPolicy.Auto, native.GpuStatus(GpuPolicy.Auto).Policy);
            }
        }
    }

    private static JobResult SubmitAndPoll(ITritKernel kernel, ScanRequest request)
    {
        var id = kernel.SubmitScan(request);
        Assert.True(id > 0);
        return WaitForJob(kernel, id);
    }

    private static JobResult WaitForJob(ITritKernel kernel, ulong id)
    {
        var timeout = Stopwatch.StartNew();
        while (timeout.Elapsed < TimeSpan.FromSeconds(10))
        {
            var result = kernel.PollJobs().FirstOrDefault(job => job.Id == id);
            if (result is not null)
            {
                return result;
            }

            Thread.Sleep(10);
        }

        throw new Xunit.Sdk.XunitException($"任务 {id} 在 10 秒内未完成");
    }

    /// <summary>
    /// 等某个 owner 的任务结果就绪。
    /// </summary>
    /// <remarks>
    /// 原生内核的扫描任务是<b>异步</b>跑的（任务池 worker 线程），提交完立刻 poll
    /// 很可能一条都拿不到——必须轮询等待。
    /// <para>
    /// 这里刻意<b>只 poll 指定 owner</b>：如果用无过滤的 <c>PollJobs()</c> 等待，
    /// 会把队列里其他 owner 的结果一并取走，后续断言就数不对了。
    /// </para>
    /// </remarks>
    private static IReadOnlyList<JobResult> WaitForOwnerJobs(
        Func<string, IReadOnlyList<JobResult>> poll,
        string owner,
        int expected)
    {
        var timeout = Stopwatch.StartNew();
        while (timeout.Elapsed < TimeSpan.FromSeconds(10))
        {
            var jobs = poll(owner);
            if (jobs.Count >= expected)
            {
                return jobs;
            }

            Thread.Sleep(10);
        }

        throw new Xunit.Sdk.XunitException(
            $"owner={owner} 的 {expected} 条任务在 10 秒内未就绪（实际 {poll(owner).Count} 条）");
    }

    /// <summary>
    /// 托管回退内核与原生内核对<b>同一份请求</b>必须给出<b>逐字节相同</b>的任务记录。
    /// </summary>
    /// <remarks>
    /// 这是影子差分的核心保证：托管侧是原生侧的 fallback，
    /// 两条路径的 <c>kind</c> / <c>state</c> / <c>error</c> / <c>backend</c> /
    /// <c>steps</c> / <c>result</c> 全都要一致，否则「原生报失败、托管报成功」
    /// 会让上层拿到自相矛盾的结果。
    /// <para>
    /// 这里刻意用<b>原始 JSON 载荷</b>而不是 <see cref="ScanRequest"/>：
    /// 只有原始载荷才能覆盖「缺字段 / 类型不对 / 越界数值」这些容错路径。
    /// </para>
    /// <para>
    /// 比的是<b>原始字节</b>（<c>GetRawText</c>），不是解析后的字段：
    /// 键序、浮点格式、非 ASCII 转义任何一处不一致都会被抓出来。
    /// </para>
    /// </remarks>
    [Fact]
    public void ManagedAndNativeAgreeByteForByteOnScanResultsWhenV2NativeIsAvailable()
    {
        var path = NativeLibraryLocator.Find();
        if (path is null)
        {
            return;
        }

        NativeKernel native;
        try
        {
            native = NativeKernel.Load(path);
        }
        catch (InvalidOperationException)
        {
            return;
        }

        using (native)
        using (var managed = new ManagedKernel())
        {
            // 合法请求：四种扫描的结果对象都要一致（含密度的小数格式）。
            CompareRawScanPayload(native, managed, """{"owner":"parity","kind":"histogram","trits":[0,1,2,2,0]}""");
            CompareRawScanPayload(native, managed, """{"owner":"parity","kind":"pattern","trits":[2,2,0,2,2],"pattern":[2,2]}""");
            CompareRawScanPayload(native, managed, """{"owner":"parity","kind":"dot","trits":[0,1,2,2],"pattern":[2,0]}""");
            CompareRawScanPayload(native, managed, """{"owner":"parity","kind":"density","trits":[0,2,2,0,1,2],"chunk":2,"top":2}""");

            // 非法请求：两条路径都要产出 failed 任务，且错误文案逐字相同。
            CompareRawScanPayload(native, managed, """{"owner":"parity","kind":"histogram","trits":[7]}""");
            CompareRawScanPayload(native, managed, """{"owner":"parity","kind":"nope","trits":[1]}""");
            CompareRawScanPayload(native, managed, """{"owner":"parity","kind":"pattern","trits":[1,2]}""");
            CompareRawScanPayload(native, managed, """{"owner":"parity","kind":"dot","trits":[1,2]}""");
            CompareRawScanPayload(native, managed, """{"owner":"parity"}""");
            CompareRawScanPayload(native, managed, """{"owner":"parity","kind":"pattern","trits":[1,2],"pattern":[9]}""");

            // 容错路径：缺字段、null、类型不对、超 int32 的数值。
            CompareRawScanPayload(native, managed, """{"owner":"parity","kind":"histogram"}""");
            CompareRawScanPayload(native, managed, """{"owner":"parity","kind":"histogram","trits":null,"pattern":null}""");
            CompareRawScanPayload(native, managed, """{"owner":"parity","kind":"histogram","trits":[1,2],"chunk":99999999999,"top":1.5,"oneshot":"yes"}""");
            CompareRawScanPayload(native, managed, """{"owner":"parity","kind":"  histogram  ","trits":[1,2]}""");
        }
    }

    private static void CompareRawScanPayload(ITritKernel native, ITritKernel managed, string payload)
    {
        var bytes = Encoding.UTF8.GetBytes(payload);

        var nativeSubmit = native.Submit(Opcode.ScanSubmit, bytes);
        var managedSubmit = managed.Submit(Opcode.ScanSubmit, bytes);
        // 非法请求也**不能**让 opcode 失败（ABI：产生一个 failed 任务）。
        Assert.Equal(Wire.Status.Ok, nativeSubmit.Status);
        Assert.Equal(Wire.Status.Ok, managedSubmit.Status);

        var nativeJob = KernelResponseMapper.ToJobId(nativeSubmit);
        var managedJob = KernelResponseMapper.ToJobId(managedSubmit);
        Assert.True(nativeJob > 0, "原生侧应返回任务号");
        Assert.True(managedJob > 0, "托管侧应返回任务号");

        var nativeRecord = PollRawJobRecord(native, nativeJob, "parity");
        var managedRecord = PollRawJobRecord(managed, managedJob, "parity");
        Assert.Equal(nativeRecord, managedRecord);
    }

    /// <summary>
    /// 轮询并取回某个任务的<b>原始 JSON 文本</b>。
    /// </summary>
    /// <remarks>
    /// 原生内核的扫描任务是<b>异步</b>跑的（任务池 worker 线程），提交完立刻 poll
    /// 很可能一条都拿不到——必须轮询等待。
    /// <para>
    /// 只 poll 指定 owner：否则会把队列里其他 owner 的结果一并取走。
    /// </para>
    /// </remarks>
    private static string PollRawJobRecord(ITritKernel kernel, ulong jobId, string owner)
    {
        var payload = Encoding.UTF8.GetBytes($"{{\"limit\":16,\"owner\":\"{owner}\"}}");
        var timeout = Stopwatch.StartNew();
        while (timeout.Elapsed < TimeSpan.FromSeconds(10))
        {
            var response = kernel.Submit(Opcode.JobPoll, payload);
            Assert.Equal(Wire.Status.Ok, response.Status);
            using var document = JsonDocument.Parse(response.Payload);
            foreach (var job in document.RootElement.GetProperty("jobs").EnumerateArray())
            {
                if (job.GetProperty("id").GetUInt64() == jobId)
                {
                    return job.GetRawText();
                }
            }

            Thread.Sleep(10);
        }

        throw new Xunit.Sdk.XunitException($"任务 {jobId} 在 10 秒内未完成");
    }

    private static void CompareHistogram(ITritKernel native, ITritKernel managed)
    {
        var request = new ScanRequest
        {
            Owner = "parity-histogram",
            Kind = ScanKind.Histogram,
            Trits = new byte[] { 0, 1, 2, 2, 0 },
        };
        var expected = SubmitAndPoll(managed, request);
        var actual = SubmitAndPoll(native, request);
        Assert.Equal(expected.State, actual.State);
        Assert.True(ScanResultJson.TryGetHistogram(expected.ResultJson, out var en, out var ez, out var ep));
        Assert.True(ScanResultJson.TryGetHistogram(actual.ResultJson, out var an, out var az, out var ap));
        Assert.Equal((en, ez, ep), (an, az, ap));
    }

    private static void ComparePattern(ITritKernel native, ITritKernel managed)
    {
        var request = new ScanRequest
        {
            Owner = "parity-pattern",
            Kind = ScanKind.Pattern,
            Trits = new byte[] { 2, 2, 0, 2, 2 },
            Pattern = new byte[] { 2, 2 },
        };
        var expected = SubmitAndPoll(managed, request);
        var actual = SubmitAndPoll(native, request);
        Assert.Equal(ScanResultJson.TryGetMatches(expected.ResultJson), ScanResultJson.TryGetMatches(actual.ResultJson));
        Assert.True(ScanResultJson.TryGetScanned(expected.ResultJson, out var expectedScanned));
        Assert.True(ScanResultJson.TryGetScanned(actual.ResultJson, out var actualScanned));
        Assert.Equal(expectedScanned, actualScanned);
    }

    private static void CompareDot(ITritKernel native, ITritKernel managed)
    {
        var request = new ScanRequest
        {
            Owner = "parity-dot",
            Kind = ScanKind.Dot,
            Trits = new byte[] { 0, 1, 2, 2 },
            Pattern = new byte[] { 2, 0 },
        };
        var expected = SubmitAndPoll(managed, request);
        var actual = SubmitAndPoll(native, request);
        Assert.True(ScanResultJson.TryGetDot(expected.ResultJson, out var expectedDot));
        Assert.True(ScanResultJson.TryGetDot(actual.ResultJson, out var actualDot));
        Assert.Equal(expectedDot, actualDot);
    }
}
