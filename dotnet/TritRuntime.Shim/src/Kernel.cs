using TritRuntime.Wire;

namespace TritRuntime.Kernel;

/// <summary>内核运行模式。</summary>
public enum KernelMode
{
    /// <summary>原生 Rust 三进制运行时（trit_rt.dll）。</summary>
    Native,

    /// <summary>托管回退运行时（原生库不可用时的纯 C# 实现，语义一致）。</summary>
    Managed,
}

/// <summary>能力报告（Mod.Call "TritRuntime.Capabilities" 的返回）。</summary>
/// <param name="AbiVersion">C ABI 版本；托管回退恒为 0。</param>
/// <param name="RuntimeVersion">运行时版本。</param>
/// <param name="Capabilities">能力位图。</param>
/// <param name="Mode">运行模式。</param>
/// <param name="Arbitration">仲裁模式。</param>
public sealed record CapabilityReport(
    uint AbiVersion,
    uint RuntimeVersion,
    CapabilityBits Capabilities,
    KernelMode Mode,
    ArbitrationMode Arbitration)
{
    /// <summary>是否具备某能力。</summary>
    public bool Has(CapabilityBits bit) => (Capabilities & bit) == bit;
}

/// <summary>一次 submit 的结果。</summary>
/// <param name="Status">C ABI 状态码。</param>
/// <param name="Payload">拆框后的响应载荷（错误时可能为空）。</param>
public sealed record KernelResponse(int Status, byte[] Payload)
{
    /// <summary>是否成功。</summary>
    public bool IsOk => Status == Wire.Status.Ok;
}

/// <summary>一条写入意图。</summary>
/// <param name="TxnId">意图所属事务（0 = 自动提交）。</param>
/// <param name="Key">世界键（车道）。</param>
/// <param name="Delta">增量。</param>
public readonly record struct WriteIntent(ulong TxnId, uint Key, TritDelta Delta);

/// <summary>一条争议车道。</summary>
/// <param name="Key">世界键。</param>
/// <param name="Drivers">全部肇事方（事务号 + 增量）。</param>
public sealed record LaneDispute(uint Key, IReadOnlyList<(ulong TxnId, TritDelta Delta)> Drivers);

/// <summary>仲裁结果。</summary>
/// <param name="HasDisputes">是否存在争议车道。</param>
/// <param name="Resolved">收敛的车道增量（按键升序）。</param>
/// <param name="Disputes">争议车道及全部肇事方。</param>
public sealed record ArbitrateResult(
    bool HasDisputes,
    IReadOnlyList<(uint Key, TritDelta Delta)> Resolved,
    IReadOnlyList<LaneDispute> Disputes);

/// <summary>一次 step 的动作意图（由 C# 主线程执行）。</summary>
/// <param name="SubId">订阅 ID。</param>
/// <param name="EventId">事件 ID。</param>
/// <param name="ModId">模组 ID。</param>
public readonly record struct ActionIntent(ulong SubId, uint EventId, uint ModId);

/// <summary>step 结果。</summary>
public sealed record StepResult(IReadOnlyList<ActionIntent> Intents);

/// <summary>事务提交结果。</summary>
/// <param name="Applied">落地条数。</param>
/// <param name="LogSeq">提交后日志序列号。</param>
public sealed record TxnCommitResult(uint Applied, ulong LogSeq);

/// <summary>一条差分项。</summary>
public readonly record struct DiffEntry(uint Key, TritDelta Old, TritDelta New);

/// <summary>差分结果。</summary>
public sealed record DiffResult(IReadOnlyList<DiffEntry> Entries);

/// <summary>内核契约：原生运行时与托管回退运行时实现同一语义。</summary>
public interface ITritKernel : IDisposable
{
    /// <summary>运行模式。</summary>
    KernelMode Mode { get; }

    /// <summary>能力报告。</summary>
    CapabilityReport Capabilities();

    /// <summary>提交一个 opcode（载荷为拆框前的请求体）。</summary>
    KernelResponse Submit(uint opcode, byte[] payload);

    /// <summary>查询。</summary>
    KernelResponse Query(byte[] query);

    /// <summary>最近一次错误信息。</summary>
    string LastError { get; }

    /// <summary>便捷方法：订阅。</summary>
    ulong Subscribe(uint modId, uint eventId, int priority, uint[] deps);

    /// <summary>便捷方法：退订。</summary>
    void Unsubscribe(ulong subId);

    /// <summary>便捷方法：发布，返回新标脏数。</summary>
    uint Publish(uint eventId, TritDelta[] payload);

    /// <summary>便捷方法：step。</summary>
    StepResult Step(ulong tick);

    /// <summary>便捷方法：开事务。</summary>
    ulong BeginTransaction();

    /// <summary>便捷方法：提交意图（txnId == 0 自动提交）。</summary>
    void SubmitIntent(ulong txnId, uint key, TritDelta delta);

    /// <summary>
    /// 便捷方法：批量提交意图（txnId == 0 时整批一次性自动提交）。
    /// <para>
    /// 逐条 <see cref="SubmitIntent"/> 每条都要走一趟 FFI（实测 0.5µs/条），
    /// 每 tick 有成百上千条车道变化时批量接口能把这一层开销压掉一个数量级。
    /// </para>
    /// </summary>
    void SubmitIntents(ulong txnId, IReadOnlyList<uint> keys, IReadOnlyList<TritDelta> deltas);

    /// <summary>便捷方法：提交事务。</summary>
    TxnCommitResult Commit(ulong txnId);

    /// <summary>
    /// 便捷方法：一步提交整批意图，等价于 开事务 + 批量提交 + 提交。
    /// <para>
    /// 热路径用：省掉两次 FFI 与一次整批意图的克隆（见 <c>op::TXN_APPLY</c>）。
    /// 需要跨事务仲裁或可回滚语义时仍走三步流程。
    /// </para>
    /// </summary>
    TxnCommitResult ApplyBatch(IReadOnlyList<uint> keys, IReadOnlyList<TritDelta> deltas);

    /// <summary>便捷方法：回滚事务。</summary>
    void RollbackTransaction(ulong txnId);

    /// <summary>便捷方法：仲裁一批意图。</summary>
    ArbitrateResult Arbitrate(IReadOnlyList<WriteIntent> intents);

    /// <summary>便捷方法：打快照，返回快照 ID。</summary>
    ulong Snapshot();

    /// <summary>便捷方法：快照字节（含 ID 前缀）。</summary>
    byte[] SnapshotBytes();

    /// <summary>便捷方法：差分两个快照。</summary>
    DiffResult Diff(ulong a, ulong b);

    /// <summary>便捷方法：回滚到快照。</summary>
    void RollbackTo(ulong snapshotId);

    /// <summary>便捷方法：抽确定性随机数。</summary>
    TritDelta[] NextRandom(int count);

    /// <summary>便捷方法：记录影子差分（签名非空 = 发现不一致）。</summary>
    ulong ShadowRecord(byte[] signature);

    /// <summary>
    /// 异步提交扫描任务并立即返回任务号；原生内核在工作线程执行，托管回退同步完成后入结果队列。
    /// 返回 0 表示提交失败。
    /// </summary>
    ulong SubmitScan(ScanRequest request);

    /// <summary>取出已完成任务，取出即出队；<paramref name="limit"/> 非正数表示全部。</summary>
    /// <param name="limit">最多取几条；<c>&lt;= 0</c> 表示全部。</param>
    /// <param name="owner">
    /// 归属过滤；<see langword="null"/> 表示不限归属。
    /// <b>只有本次真正返回的任务会出队</b>，其他 owner 的结果仍留在队列里。
    /// </param>
    IReadOnlyList<JobResult> PollJobs(int limit = 0, string? owner = null);

    /// <summary>协作式取消单个任务；已运行任务在步骤边界退出，不强杀线程。</summary>
    int CancelJob(ulong jobId);

    /// <summary>协作式取消指定 owner 的全部排队或运行任务。</summary>
    int CancelOwner(string owner);

    /// <summary>查询 GPU 后端状态；给出策略时切换策略，探测失败会安全回退 CPU。</summary>
    GpuStatusReport GpuStatus(GpuPolicy? policy = null, bool probe = false);

    /// <summary>查询全局资源预算与各 owner 累计账本。</summary>
    BudgetSnapshot Budget();

    /// <summary>查询最近一轮任务池的 Prometheus/OpenMetrics 文本。</summary>
    string MetricsPrometheus();

    /// <summary>便捷方法：查询统计（10 个 u64）。</summary>
    ulong[] Stats();
}
