using TritRuntime.Wire;

namespace TritRuntime.Kernel;

/// <summary>Mod.Call 命令名表（大小写不敏感匹配，见 <see cref="RuntimeShim.Call"/>）。</summary>
public static class TritRuntimeCommands
{
    /// <summary>前缀：所有命令都以它开头。</summary>
    public const string Prefix = "TritRuntime.";

    /// <summary>能力报告。</summary>
    public const string Capabilities = "TritRuntime.Capabilities";

    /// <summary>运行模式（"Native" / "Managed"）。</summary>
    public const string Mode = "TritRuntime.Mode";

    /// <summary>回退原因（无回退为空串）。</summary>
    public const string FallbackReason = "TritRuntime.FallbackReason";

    /// <summary>透传一个 opcode。</summary>
    public const string Submit = "TritRuntime.Submit";

    /// <summary>查询。</summary>
    public const string Query = "TritRuntime.Query";

    /// <summary>回滚到快照。</summary>
    public const string Rollback = "TritRuntime.Rollback";

    /// <summary>打快照。</summary>
    public const string Snapshot = "TritRuntime.Snapshot";

    /// <summary>差分两个快照。</summary>
    public const string Diff = "TritRuntime.Diff";

    /// <summary>推进一个 tick。</summary>
    public const string Step = "TritRuntime.Step";

    /// <summary>订阅。</summary>
    public const string Subscribe = "TritRuntime.Subscribe";

    /// <summary>退订。</summary>
    public const string Unsubscribe = "TritRuntime.Unsubscribe";

    /// <summary>发布。</summary>
    public const string Publish = "TritRuntime.Publish";

    /// <summary>开事务。</summary>
    public const string BeginTransaction = "TritRuntime.BeginTransaction";

    /// <summary>事务内提交意图。</summary>
    public const string SubmitIntent = "TritRuntime.SubmitIntent";

    /// <summary>提交事务。</summary>
    public const string Commit = "TritRuntime.Commit";

    /// <summary>回滚事务。</summary>
    public const string RollbackTransaction = "TritRuntime.RollbackTransaction";

    /// <summary>仲裁一批意图。</summary>
    public const string Arbitrate = "TritRuntime.Arbitrate";

    /// <summary>抽确定性随机数。</summary>
    public const string NextRandom = "TritRuntime.NextRandom";

    /// <summary>记录影子差分签名。</summary>
    public const string ShadowRecord = "TritRuntime.ShadowRecord";

    /// <summary>统计。</summary>
    public const string Stats = "TritRuntime.Stats";

    /// <summary>提交扫描任务。</summary>
    public const string Scan = "TritRuntime.Scan";

    /// <summary>取出已完成任务。</summary>
    public const string PollJobs = "TritRuntime.PollJobs";

    /// <summary>取消单个任务。</summary>
    public const string CancelJob = "TritRuntime.CancelJob";

    /// <summary>取消指定 owner 的任务。</summary>
    public const string CancelOwner = "TritRuntime.CancelOwner";

    /// <summary>查询或切换 GPU 状态。</summary>
    public const string GpuStatus = "TritRuntime.GpuStatus";

    /// <summary>查询全局资源预算。</summary>
    public const string Budget = "TritRuntime.Budget";

    /// <summary>查询 Prometheus 指标。</summary>
    public const string Metrics = "TritRuntime.Metrics";

    /// <summary>全部命令（错误信息里列出，方便调用方自查）。</summary>
    public static readonly string[] All =
    {
        Capabilities, Mode, FallbackReason, Submit, Query, Rollback, Snapshot, Diff, Step,
        Subscribe, Unsubscribe, Publish, BeginTransaction, SubmitIntent, Commit,
        RollbackTransaction, Arbitrate, NextRandom, ShadowRecord, Stats, Scan, PollJobs,
        CancelJob, CancelOwner, GpuStatus, Budget, Metrics,
    };
}

/// <summary>
/// 旁路三进制运行时的 C# 门面：其他模组通过 <c>Mod.Call</c> 或强类型方法访问，
/// 内部由 <see cref="RuntimeHost"/> 在原生内核与托管回退内核之间选择。
/// </summary>
/// <remarks>
/// 兼容性契约（本类的存在意义）：
/// <list type="number">
/// <item><description>API 表面稳定：命令名与返回类型只增不改；</description></item>
/// <item><description>能力协商 + 回退：原生不可用时自动降级到语义等价的托管内核，调用方无感；</description></item>
/// <item><description>影子差分：切换默认路径前先逐字节对拍，不一致只记录不提交；</description></item>
/// <item><description>主线程提交：Rust 侧只产出动作意图，真实状态修改由 C# 主线程执行。</description></item>
/// </list>
/// </remarks>
public sealed class RuntimeShim : IDisposable
{
    private readonly RuntimeHost host;
    private bool disposed;

    private RuntimeShim(RuntimeHost host)
    {
        this.host = host;
    }

    /// <summary>装载（或降级装载）运行时。</summary>
    /// <param name="options">宿主选项；null = 默认搜索 + 原生优先 + 失败回退。</param>
    public static RuntimeShim Attach(RuntimeHostOptions? options = null) => new(RuntimeHost.Create(options));

    /// <summary>内部宿主（能力协商与观测入口）。</summary>
    public RuntimeHost Host => host;

    /// <summary>当前内核。</summary>
    public ITritKernel Kernel => host.Kernel;

    /// <summary>运行模式。</summary>
    public KernelMode Mode => host.Mode;

    /// <summary>是否跑在原生 Rust 内核上。</summary>
    public bool IsNative => host.Mode == KernelMode.Native;

    /// <summary>回退到托管内核的原因；未回退为 null。</summary>
    public string? FallbackReason => host.FallbackReason;

    /// <summary>能力报告。</summary>
    public CapabilityReport Capabilities() => host.Capabilities;

    /// <summary>是否具备某能力。</summary>
    public bool Supports(CapabilityBits bit) => host.Supports(bit);

    /// <summary>推进一个 tick，返回本 tick 的动作意图（由 C# 主线程执行）。</summary>
    public StepResult Step(ulong tick) => host.Kernel.Step(tick);

    /// <summary>订阅事件。</summary>
    public ulong Subscribe(uint modId, uint eventId, int priority, uint[] deps) =>
        host.Kernel.Subscribe(modId, eventId, priority, deps);

    /// <summary>退订。</summary>
    public void Unsubscribe(ulong subId) => host.Kernel.Unsubscribe(subId);

    /// <summary>发布事件，返回新标脏的订阅数。</summary>
    public uint Publish(uint eventId, TritDelta[] payload) => host.Kernel.Publish(eventId, payload);

    /// <summary>开事务。</summary>
    public ulong BeginTransaction() => host.Kernel.BeginTransaction();

    /// <summary>事务内提交意图（txnId == 0 表示自动提交）。</summary>
    public void SubmitIntent(ulong txnId, uint key, TritDelta delta) =>
        host.Kernel.SubmitIntent(txnId, key, delta);

    /// <summary>
    /// 批量提交意图（txnId == 0 时整批一次性自动提交）。
    /// 逐条提交每条一趟 FFI，批量提交同一批只付一次调用开销。
    /// </summary>
    public void SubmitIntents(ulong txnId, IReadOnlyList<uint> keys, IReadOnlyList<TritDelta> deltas) =>
        host.Kernel.SubmitIntents(txnId, keys, deltas);

    /// <summary>提交事务。</summary>
    public TxnCommitResult Commit(ulong txnId) => host.Kernel.Commit(txnId);

    /// <summary>回滚事务。</summary>
    public void RollbackTransaction(ulong txnId) => host.Kernel.RollbackTransaction(txnId);

    /// <summary>仲裁一批意图（只判不落地）。</summary>
    public ArbitrateResult Arbitrate(IReadOnlyList<WriteIntent> intents) => host.Kernel.Arbitrate(intents);

    /// <summary>打快照，返回快照 ID。</summary>
    public ulong Snapshot() => host.Kernel.Snapshot();

    /// <summary>快照字节（含 ID 前缀）。</summary>
    public byte[] SnapshotBytes() => host.Kernel.SnapshotBytes();

    /// <summary>差分两个快照。</summary>
    public DiffResult Diff(ulong a, ulong b) => host.Kernel.Diff(a, b);

    /// <summary>回滚到快照。</summary>
    public void RollbackTo(ulong snapshotId) => host.Kernel.RollbackTo(snapshotId);

    /// <summary>抽确定性随机数。</summary>
    public TritDelta[] NextRandom(int count) => host.Kernel.NextRandom(count);

    /// <summary>
    /// 记录影子差分签名（非空 = 发现不一致，只记录不提交）。
    /// </summary>
    /// <param name="signature">
    /// <b>每个元素是一个 trit 值（0/1/2）</b>，不是已编码的线格式载荷——内核负责编码。
    /// 把任意二进制数据（例如 tick 或 log_seq 的字节）塞进来会让内核解出越界 trit，
    /// 表现为「非法字节 &gt; 242」的解码失败。
    /// </param>
    public ulong ShadowRecord(byte[] signature) => host.Kernel.ShadowRecord(signature);

    /// <summary>
    /// 记录影子差分签名（非空 = 发现不一致，只记录不提交）。
    /// 三值数组版本：内部转成 trit 值字节，避免调用方手工转换时写出越界值。
    /// </summary>
    public ulong ShadowRecord(TritDelta[] signature) =>
        host.Kernel.ShadowRecord(Array.ConvertAll(signature, value => (byte)value));

    /// <summary>
    /// 影子差分器：把同一请求喂给主内核与影子内核逐字节对拍。
    /// 仅 <see cref="RuntimeHostOptions.ShadowMode"/> 为真时非 <see langword="null"/>。
    /// </summary>
    public ShadowDiffer? Differ => host.Differ;

    /// <summary>提交扫描任务；原生路径异步执行，托管回退同步完成后入队。</summary>
    public ulong SubmitScan(ScanRequest request) => host.Kernel.SubmitScan(request);

    /// <summary>取出已完成任务，取出即出队。</summary>
    /// <param name="limit">最多取几条；<c>&lt;= 0</c> 表示全部。</param>
    /// <param name="owner">
    /// 归属过滤；<see langword="null"/> 表示不限归属。
    /// <b>只有本次真正返回的任务会出队</b>，其他 owner 的结果仍留在队列里。
    /// </param>
    public IReadOnlyList<JobResult> PollJobs(int limit = 0, string? owner = null) =>
        host.Kernel.PollJobs(limit, owner);

    /// <summary>协作式取消单个任务。</summary>
    public int CancelJob(ulong jobId) => host.Kernel.CancelJob(jobId);

    /// <summary>协作式取消指定 owner 的任务。</summary>
    public int CancelOwner(string owner) => host.Kernel.CancelOwner(owner);

    /// <summary>查询或切换 GPU 后端状态。</summary>
    public GpuStatusReport GpuStatus(GpuPolicy? policy = null, bool probe = false) =>
        host.Kernel.GpuStatus(policy, probe);

    /// <summary>查询全局资源预算。</summary>
    public BudgetSnapshot Budget() => host.Kernel.Budget();

    /// <summary>查询任务池 Prometheus 指标。</summary>
    public string MetricsPrometheus() => host.Kernel.MetricsPrometheus();

    /// <summary>统计计数器。</summary>
    public ulong[] Stats() => host.Kernel.Stats();

    /// <summary>
    /// <c>Mod.Call</c> 契约入口。模组侧这样暴露：
    /// <code>
    /// public override object Call(params object[] args)
    ///     => Trit.Attach().Call(args[0] as string ?? "", args.Skip(1).ToArray());
    /// </code>
    /// 其他模组即可 <c>mod.Call("TritRuntime.Capabilities")</c>。
    /// </summary>
    /// <param name="command">命令名，见 <see cref="TritRuntimeCommands"/>。</param>
    /// <param name="args">命令参数（已去掉命令名本身）。</param>
    /// <exception cref="ArgumentException">命令名未知或参数类型/个数不符。</exception>
    public object? Call(string command, params object?[] args)
    {
        ArgumentNullException.ThrowIfNull(command);
        var normalized = command.Trim();
        if (!normalized.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            normalized = Prefix + normalized;
        }

        args ??= Array.Empty<object?>();
        switch (normalized.ToLowerInvariant())
        {
            case "tritruntime.capabilities":
                Expect(args, 0, normalized);
                return Capabilities();

            case "tritruntime.mode":
                Expect(args, 0, normalized);
                return Mode.ToString();

            case "tritruntime.fallbackreason":
                Expect(args, 0, normalized);
                return FallbackReason ?? string.Empty;

            case "tritruntime.submit":
                Expect(args, 2, normalized);
                return host.Kernel.Submit(Args.UInt32(args[0], "opcode", normalized), Args.Bytes(args[1], "payload", normalized)).Payload;

            case "tritruntime.query":
                Expect(args, 1, 2, normalized);
                var queryId = Args.UInt32(args[0], "queryId", normalized);
                var eventId = args.Length > 1 ? Args.UInt32(args[1], "eventId", normalized) : 0u;
                return host.Kernel.Submit(Opcode.Query, Requests.Query(queryId, eventId)).Payload;

            case "tritruntime.rollback":
                Expect(args, 1, normalized);
                RollbackTo(Args.UInt64(args[0], "snapshotId", normalized));
                return null;

            case "tritruntime.snapshot":
                Expect(args, 0, normalized);
                return Snapshot();

            case "tritruntime.diff":
                Expect(args, 2, normalized);
                return Diff(Args.UInt64(args[0], "snapshotA", normalized), Args.UInt64(args[1], "snapshotB", normalized));

            case "tritruntime.step":
                Expect(args, 1, normalized);
                return Step(Args.UInt64(args[0], "tick", normalized));

            case "tritruntime.subscribe":
                Expect(args, 4, normalized);
                return Subscribe(
                    Args.UInt32(args[0], "modId", normalized),
                    Args.UInt32(args[1], "eventId", normalized),
                    Args.Int32(args[2], "priority", normalized),
                    Args.UInt32Array(args[3], "deps", normalized));

            case "tritruntime.unsubscribe":
                Expect(args, 1, normalized);
                Unsubscribe(Args.UInt64(args[0], "subId", normalized));
                return null;

            case "tritruntime.publish":
                Expect(args, 2, normalized);
                return Publish(
                    Args.UInt32(args[0], "eventId", normalized),
                    Args.Trits(args[1], "payload", normalized));

            case "tritruntime.begintransaction":
                Expect(args, 0, normalized);
                return BeginTransaction();

            case "tritruntime.submitintent":
                Expect(args, 3, normalized);
                SubmitIntent(
                    Args.UInt64(args[0], "txnId", normalized),
                    Args.UInt32(args[1], "key", normalized),
                    Args.Trit(args[2], "delta", normalized));
                return null;

            case "tritruntime.commit":
                Expect(args, 1, normalized);
                return Commit(Args.UInt64(args[0], "txnId", normalized));

            case "tritruntime.rollbacktransaction":
                Expect(args, 1, normalized);
                RollbackTransaction(Args.UInt64(args[0], "txnId", normalized));
                return null;

            case "tritruntime.arbitrate":
                if (args.Length != 1 && args.Length % 3 != 0)
                {
                    throw new ArgumentException(
                        $"命令 {normalized} 需要 1 个 WriteIntent[]，或 3 的倍数个扁平参数"
                        + $"（txnId, key, delta 三元组），实际收到 {args.Length} 个");
                }

                return Arbitrate(Args.Intents(args.Length == 1 ? args[0] : args, "intents", normalized));

            case "tritruntime.nextrandom":
                Expect(args, 1, normalized);
                return NextRandom(Args.Int32(args[0], "count", normalized));

            case "tritruntime.shadowrecord":
                Expect(args, 1, normalized);
                return ShadowRecord(Args.Bytes(args[0], "signature", normalized));

            case "tritruntime.scan":
                return SubmitScan(ScanArguments(args, normalized));

            case "tritruntime.polljobs":
                var (pollLimit, pollOwner) = PollArguments(args, normalized);
                return PollJobs(pollLimit, pollOwner);

            case "tritruntime.canceljob":
                return CancelJob(CancelJobId(args, normalized));

            case "tritruntime.cancelowner":
                return CancelOwner(CancelOwnerName(args, normalized));

            case "tritruntime.gpustatus":
                var (policy, probe) = GpuStatusArguments(args, normalized);
                return GpuStatus(policy, probe);

            case "tritruntime.budget":
                ExpectSignature(args, 0, normalized, "()");
                return Budget();

            case "tritruntime.metrics":
                ExpectSignature(args, 0, normalized, "()");
                return MetricsPrometheus();

            case "tritruntime.stats":
                Expect(args, 0, normalized);
                return Stats();

            default:
                throw new ArgumentException(
                    $"未知命令 \"{command}\"。可用命令：{string.Join(", ", TritRuntimeCommands.All)}");
        }
    }

    private static ScanRequest ScanArguments(object?[] args, string command)
    {
        const string signatures = "(ScanRequest)；(string owner, string kind, byte[] trits)；"
                                  + "(string owner, string kind, byte[] trits, byte[] pattern, int threshold)";
        if (args.Length == 1 && args[0] is ScanRequest request)
        {
            return request;
        }

        if ((args.Length == 3 || args.Length == 5)
            && args[0] is string owner
            && args[1] is string kind
            && args[2] is byte[] trits
            && (args.Length == 3 || (args[3] is byte[] && args[4] is int)))
        {
            return new ScanRequest
            {
                Owner = owner,
                Kind = ParseScanKindArgument(kind, command),
                Trits = trits,
                Pattern = args.Length == 5 ? (byte[])args[3]! : null,
                Threshold = args.Length == 5 ? (int)args[4]! : 0,
            };
        }

        throw SignatureError(command, signatures, args);
    }

    /// <summary>解析 <c>PollJobs</c> 的参数：<c>(int limit)</c> / <c>(string owner)</c> / <c>(string owner, int limit)</c>。</summary>
    /// <remarks>
    /// 三种签名都留着：<c>(int limit)</c> 是老用法（不限归属），
    /// 加 owner 的两个签名是为了让多个模组各取各的结果而互不吞掉。
    /// </remarks>
    private static (int Limit, string? Owner) PollArguments(object?[] args, string command)
    {
        const string signatures = "()；(int limit)；(string owner)；(string owner, int limit)";
        if (args.Length == 0)
        {
            return (0, null);
        }

        if (args.Length == 1)
        {
            if (args[0] is int singleLimit)
            {
                return (singleLimit, null);
            }

            if (args[0] is string singleOwner)
            {
                return (0, singleOwner);
            }
        }

        if (args.Length == 2 && args[0] is string pairOwner && args[1] is int pairLimit)
        {
            return (pairLimit, pairOwner);
        }

        throw SignatureError(command, signatures, args);
    }

    private static ulong CancelJobId(object?[] args, string command)
    {
        const string signatures = "(ulong jobId)；(int jobId)；(long jobId)";
        try
        {
            return args.Length == 1 && args[0] is ulong or int or long
                ? Args.UInt64(args[0], "jobId", command)
                : throw SignatureError(command, signatures, args);
        }
        catch (OverflowException)
        {
            throw SignatureError(command, signatures, args);
        }
    }

    private static string CancelOwnerName(object?[] args, string command)
    {
        const string signatures = "(string owner)";
        return args.Length == 1 && args[0] is string owner
            ? owner
            : throw SignatureError(command, signatures, args);
    }

    private static (GpuPolicy? Policy, bool Probe) GpuStatusArguments(object?[] args, string command)
    {
        const string signatures = "()；(string policy)；(string policy, bool probe)";
        if (args.Length == 0)
        {
            return (null, false);
        }

        if ((args.Length == 1 || args.Length == 2)
            && args[0] is string policy
            && (args.Length == 1 || args[1] is bool))
        {
            return (ParseGpuPolicyArgument(policy, command), args.Length == 2 && (bool)args[1]!);
        }

        throw SignatureError(command, signatures, args);
    }

    /// <summary>
    /// 命令行层的种类解析。
    /// </summary>
    /// <remarks>
    /// 与内核的宽容解码不同：这里**直接抛异常**——用户在命令行里写错了种类，
    /// 应当立刻看到「你写错了」，而不是拿到一个失败任务再去轮询。
    /// 但去首尾空白与内核保持一致（内核两侧都 trim）。
    /// </remarks>
    private static ScanKind ParseScanKindArgument(string value, string command) => value.Trim().ToLowerInvariant() switch
    {
        "histogram" => ScanKind.Histogram,
        "pattern" => ScanKind.Pattern,
        "density" => ScanKind.Density,
        "dot" => ScanKind.Dot,
        _ => throw new ArgumentException(
            $"命令 {command} 的 kind \"{value}\" 未知；可选值：histogram, pattern, density, dot"),
    };

    private static GpuPolicy ParseGpuPolicyArgument(string value, string command) => value.ToLowerInvariant() switch
    {
        "auto" => GpuPolicy.Auto,
        "off" => GpuPolicy.Off,
        "on" => GpuPolicy.On,
        _ => throw new ArgumentException(
            $"命令 {command} 的 policy \"{value}\" 未知；可选值：auto, off, on"),
    };

    private static void ExpectSignature(object?[] args, int count, string command, string signatures)
    {
        if (args.Length != count)
        {
            throw SignatureError(command, signatures, args);
        }
    }

    private static ArgumentException SignatureError(string command, string signatures, object?[] args) =>
        new($"命令 {command} 期望签名：{signatures}；实际收到 {args.Length} 个参数"
            + (args.Length == 0
                ? string.Empty
                : $"（{string.Join(", ", args.Select(value => value?.GetType().Name ?? "null"))}）"));

    /// <summary>销毁宿主。</summary>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        host.Dispose();
        GC.SuppressFinalize(this);
    }

    private const string Prefix = TritRuntimeCommands.Prefix;

    private static void Expect(object?[] args, int count, string command) => Expect(args, count, count, command);

    private static void Expect(object?[] args, int min, int max, string command)
    {
        if (args.Length < min || args.Length > max)
        {
            var range = min == max ? $"{min}" : $"{min}..{max}";
            throw new ArgumentException(
                $"命令 {command} 需要 {range} 个参数，实际收到 {args.Length} 个");
        }
    }
}

/// <summary>Mod.Call 参数转换：容忍各种整数宽度，拒绝 silently 猜错。</summary>
internal static class Args
{
    public static int Int32(object? value, string name, string command) => Convert(value, name, command) switch
    {
        int typed => typed,
        long typed => checked((int)typed),
        _ => throw Type(value, name, command, "整数"),
    };

    public static uint UInt32(object? value, string name, string command) => Convert(value, name, command) switch
    {
        int typed => checked((uint)typed),
        uint typed => typed,
        long typed => checked((uint)typed),
        ulong typed => checked((uint)typed),
        _ => throw Type(value, name, command, "非负整数"),
    };

    public static ulong UInt64(object? value, string name, string command) => Convert(value, name, command) switch
    {
        int typed => checked((ulong)typed),
        uint typed => typed,
        long typed => checked((ulong)typed),
        ulong typed => typed,
        _ => throw Type(value, name, command, "非负整数"),
    };

    public static byte[] Bytes(object? value, string name, string command) => value switch
    {
        null => Array.Empty<byte>(),
        byte[] typed => typed,
        _ => throw Type(value, name, command, "byte[]"),
    };

    public static TritDelta[] Trits(object? value, string name, string command) => value switch
    {
        null => Array.Empty<TritDelta>(),
        TritDelta[] typed => typed,
        byte[] bytes => Array.ConvertAll(bytes, item => (TritDelta)item),
        int[] ints => Array.ConvertAll(ints, item => (TritDelta)item),
        _ => throw Type(value, name, command, "TritDelta[] / byte[]"),
    };

    public static TritDelta Trit(object? value, string name, string command) => value switch
    {
        TritDelta typed => typed,
        int typed => (TritDelta)typed,
        _ => throw Type(value, name, command, "TritDelta 或 0/1/2"),
    };

    public static uint[] UInt32Array(object? value, string name, string command) => value switch
    {
        null => Array.Empty<uint>(),
        uint[] typed => typed,
        int[] ints => Array.ConvertAll(ints, item => checked((uint)item)),
        long[] longs => Array.ConvertAll(longs, item => checked((uint)item)),
        _ => throw Type(value, name, command, "uint[]"),
    };

    /// <summary>接受 <see cref="WriteIntent"/> 数组或扁平 <c>(txnId, key, delta)</c> 三元组。</summary>
    public static IReadOnlyList<WriteIntent> Intents(object? value, string name, string command) => value switch
    {
        null => Array.Empty<WriteIntent>(),
        WriteIntent[] typed => typed,
        IReadOnlyList<WriteIntent> typed => typed,
        object?[] tuples when tuples.Length % 3 == 0 => BuildTuples(tuples, command),
        _ => throw Type(value, name, command, "WriteIntent[] 或 (txnId, key, delta) 扁平三元组"),
    };

    private static WriteIntent[] BuildTuples(object?[] tuples, string command)
    {
        var result = new WriteIntent[tuples.Length / 3];
        for (var i = 0; i < result.Length; i++)
        {
            result[i] = new WriteIntent(
                UInt64(tuples[(3 * i) + 0], $"intents[{i}].txnId", command),
                UInt32(tuples[(3 * i) + 1], $"intents[{i}].key", command),
                Trit(tuples[(3 * i) + 2], $"intents[{i}].delta", command));
        }

        return result;
    }

    private static object? Convert(object? value, string name, string command) => value switch
    {
        null => throw new ArgumentException($"命令 {command} 的参数 {name} 不能为 null"),
        sbyte typed => typed,
        byte typed => typed,
        short typed => typed,
        ushort typed => typed,
        int typed => typed,
        uint typed => typed,
        long typed => typed,
        ulong typed => typed,
        _ => throw Type(value, name, command, "整数"),
    };

    private static ArgumentException Type(object? value, string name, string command, string expected) =>
        new($"命令 {command} 的参数 {name} 需要 {expected}，实际是 {value?.GetType().Name ?? "null"}");
}
