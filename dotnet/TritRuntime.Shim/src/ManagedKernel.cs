using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TritRuntime.Wire;
using WireFormat = TritRuntime.Wire.Wire;

namespace TritRuntime.Kernel;

/// <summary>托管内核内部错误：与 Rust <c>RtError</c> 一一对应，最终映射成 C ABI 状态码。</summary>
internal sealed class RtException : Exception
{
    /// <summary>构造内部错误。</summary>
    public RtException(int status, string message, List<LaneDispute>? disputes = null)
        : base(message)
    {
        Status = status;
        Disputes = disputes ?? new List<LaneDispute>();
    }

    /// <summary>C ABI 状态码。</summary>
    public int Status { get; }

    /// <summary>冲突车道（仅 <see cref="Status.Conflict"/> 时非空）。</summary>
    public List<LaneDispute> Disputes { get; }
}

/// <summary>托管内核的 JSON 配置（与 Rust <c>RuntimeConfig</c> 同名字段、同默认值）。</summary>
public sealed class ManagedConfig
{
    /// <summary>确定性种子。</summary>
    public ulong Seed { get; init; } = 0x5EED_1234;

    /// <summary>订阅者上限。</summary>
    public int MaxSubscribers { get; init; } = 65_536;

    /// <summary>并发事务上限。</summary>
    public int MaxOpenTxns { get; init; } = 4_096;

    /// <summary>快照保留数。</summary>
    public int MaxSnapshots { get; init; } = 64;

    /// <summary>事务日志条数上限（只追加，写满即报错，上层回退到 C# 路径）。</summary>
    public int MaxLogEntries { get; init; } = 1 << 20;

    /// <summary>仲裁模式。</summary>
    public ArbitrationMode Arbitration { get; init; } = ArbitrationMode.Majority;

    /// <summary>是否启用影子差分记录（当前为保留字段：影子日志始终记录，由上层决定是否消费）。</summary>
    public bool Shadow { get; init; } = true;

    /// <summary>从 JSON 字节解析；空输入用默认值，非法 JSON 抛 <see cref="WireException"/>。</summary>
    public static ManagedConfig FromJson(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0)
        {
            return new ManagedConfig();
        }

        try
        {
            var dto = JsonSerializer.Deserialize(bytes, ManagedConfigJsonContext.Default.ConfigDto)
                      ?? new ConfigDto();
            return new ManagedConfig
            {
                Seed = dto.Seed ?? 0x5EED_1234,
                MaxSubscribers = dto.MaxSubscribers ?? 65_536,
                MaxOpenTxns = dto.MaxOpenTxns ?? 4_096,
                MaxSnapshots = dto.MaxSnapshots ?? 64,
                MaxLogEntries = dto.MaxLogEntries ?? (1 << 20),
                Arbitration = ParseArbitration(dto.Arbitration),
                Shadow = dto.Shadow ?? true,
            };
        }
        catch (JsonException error)
        {
            throw new WireException($"配置 JSON 非法：{error.Message}");
        }
    }

    private static ArbitrationMode ParseArbitration(string? text) => text?.ToLowerInvariant() switch
    {
        null => ArbitrationMode.Majority,
        "strict" => ArbitrationMode.Strict,
        "majority" => ArbitrationMode.Majority,
        _ => throw new WireException($"配置 JSON 非法：未知仲裁模式 \"{text}\""),
    };
}

/// <summary>配置 JSON 的 DTO（字段名与 Rust 侧完全一致，区分大小写）。</summary>
internal sealed class ConfigDto
{
    /// <summary>seed。</summary>
    [JsonPropertyName("seed")]
    public ulong? Seed { get; set; }

    /// <summary>max_subscribers。</summary>
    [JsonPropertyName("max_subscribers")]
    public int? MaxSubscribers { get; set; }

    /// <summary>max_open_txns。</summary>
    [JsonPropertyName("max_open_txns")]
    public int? MaxOpenTxns { get; set; }

    /// <summary>max_snapshots。</summary>
    [JsonPropertyName("max_snapshots")]
    public int? MaxSnapshots { get; set; }

    /// <summary>max_log_entries。</summary>
    [JsonPropertyName("max_log_entries")]
    public int? MaxLogEntries { get; set; }

    /// <summary>arbitration。</summary>
    [JsonPropertyName("arbitration")]
    public string? Arbitration { get; set; }

    /// <summary>shadow。</summary>
    [JsonPropertyName("shadow")]
    public bool? Shadow { get; set; }
}

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = false)]
[JsonSerializable(typeof(ConfigDto))]
internal partial class ManagedConfigJsonContext : JsonSerializerContext;

/// <summary>一个订阅者。</summary>
internal sealed class Subscriber
{
    /// <summary>订阅 ID。</summary>
    public required ulong Id { get; init; }

    /// <summary>模组 ID。</summary>
    public required uint ModId { get; init; }

    /// <summary>事件 ID。</summary>
    public required uint EventId { get; init; }

    /// <summary>优先级；越大越先派发。</summary>
    public required int Priority { get; init; }

    /// <summary>依赖的事件 ID 列表（必须排在它们的订阅者之后）。</summary>
    public required uint[] Deps { get; init; }
}

/// <summary>
/// 事件路由器：依赖图 + 脏标记 + 批处理。静态派发序用 Kahn + 最大键
/// <c>(priority, id)</c> 复刻 <c>trit_schedule::schedule(&amp;tasks, 1)</c> 的
/// <c>ready.pop_last()</c> 语义（所有 duration 恒 1，成环即拒绝）。
/// </summary>
internal sealed class EventRouter
{
    private readonly SortedDictionary<ulong, Subscriber> subscribers = new();
    private readonly SortedDictionary<uint, SortedSet<ulong>> byEvent = new();
    private readonly SortedSet<ulong> dirty = new();
    private readonly int maxSubscribers;
    private List<ulong> order = new();
    private ulong nextId = 1;

    /// <summary>makespan（duration 恒 1 时等于订阅者数）。</summary>
    public ulong Makespan { get; private set; }

    /// <summary>关键路径长度。</summary>
    public ulong CriticalPath { get; private set; }

    /// <summary>构造路由器。</summary>
    public EventRouter(int maxSubscribers)
    {
        this.maxSubscribers = maxSubscribers;
    }

    /// <summary>订阅者数量。</summary>
    public int Count => subscribers.Count;

    /// <summary>当前脏集合大小。</summary>
    public int DirtyCount => dirty.Count;

    /// <summary>订阅；自依赖与依赖成环都会拒绝（坏订阅不留痕迹）。</summary>
    public ulong Subscribe(uint modId, uint eventId, int priority, uint[] deps)
    {
        if (subscribers.Count >= maxSubscribers)
        {
            throw new RtException(Status.Capacity, $"容量超限：订阅数达到上限 {maxSubscribers}");
        }

        if (Array.IndexOf(deps, eventId) >= 0)
        {
            throw new RtException(
                Status.Arg,
                $"输入非法：订阅者不能依赖自己订阅的事件 {eventId}");
        }

        var id = nextId;
        var subscriber = new Subscriber
        {
            Id = id,
            ModId = modId,
            EventId = eventId,
            Priority = priority,
            Deps = deps,
        };
        subscribers[id] = subscriber;
        if (!byEvent.TryGetValue(eventId, out var bucket))
        {
            bucket = new SortedSet<ulong>();
            byEvent[eventId] = bucket;
        }

        bucket.Add(id);
        try
        {
            RebuildOrder();
        }
        catch (RtException)
        {
            // 回滚插入，保持「拒绝即无副作用」。
            subscribers.Remove(id);
            bucket.Remove(id);
            if (bucket.Count == 0)
            {
                byEvent.Remove(eventId);
            }

            throw;
        }

        nextId += 1;
        return id;
    }

    /// <summary>退订。</summary>
    public void Unsubscribe(ulong subId)
    {
        if (!subscribers.Remove(subId, out var subscriber))
        {
            throw new RtException(Status.NotFound, $"订阅不存在：{subId}");
        }

        if (byEvent.TryGetValue(subscriber.EventId, out var bucket))
        {
            bucket.Remove(subId);
            if (bucket.Count == 0)
            {
                byEvent.Remove(subscriber.EventId);
            }
        }

        dirty.Remove(subId);
        RebuildOrder();
    }

    /// <summary>发布事件：把订阅者及其依赖闭包标脏，返回新标脏的数量。</summary>
    public int Publish(uint eventId)
    {
        if (!byEvent.TryGetValue(eventId, out var seeds) || seeds.Count == 0)
        {
            return 0;
        }

        var dependents = DependentsMap();
        var queue = new Queue<ulong>(seeds);
        var newlyDirty = 0;
        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            if (dirty.Add(node))
            {
                newlyDirty += 1;
            }

            if (dependents.TryGetValue(node, out var children))
            {
                foreach (var child in children)
                {
                    if (!dirty.Contains(child))
                    {
                        queue.Enqueue(child);
                    }
                }
            }
        }

        return newlyDirty;
    }

    /// <summary>取出本 tick 的派发列表（静态顺序过滤脏集合），并清空脏集合。</summary>
    public List<ulong> TakeDispatch()
    {
        if (dirty.Count == 0)
        {
            return new List<ulong>();
        }

        var dispatch = order.FindAll(id => dirty.Contains(id));
        dirty.Clear();
        return dispatch;
    }

    /// <summary>取一个订阅者。</summary>
    public Subscriber? Get(ulong subId) => subscribers.TryGetValue(subId, out var subscriber) ? subscriber : null;

    /// <summary><c>P -> {S}</c>：S 依赖 P 订阅的某个事件。</summary>
    private SortedDictionary<ulong, SortedSet<ulong>> DependentsMap()
    {
        var map = new SortedDictionary<ulong, SortedSet<ulong>>();
        foreach (var subscriber in subscribers.Values)
        {
            foreach (var eventId in subscriber.Deps)
            {
                if (!byEvent.TryGetValue(eventId, out var predecessors))
                {
                    continue;
                }

                foreach (var predecessor in predecessors)
                {
                    if (predecessor == subscriber.Id)
                    {
                        continue;
                    }

                    if (!map.TryGetValue(predecessor, out var children))
                    {
                        children = new SortedSet<ulong>();
                        map[predecessor] = children;
                    }

                    children.Add(subscriber.Id);
                }
            }
        }

        return map;
    }

    /// <summary>重算静态派发顺序；成环则抛错（订阅/退订失败由调用方回滚）。</summary>
    private void RebuildOrder()
    {
        var all = new List<Subscriber>(subscribers.Values);
        var indegree = new Dictionary<ulong, int>(all.Count);
        var dependents = new Dictionary<ulong, List<ulong>>(all.Count);
        var predecessors = new Dictionary<ulong, List<ulong>>(all.Count);
        var priority = new Dictionary<ulong, int>(all.Count);
        foreach (var subscriber in all)
        {
            var preds = new SortedSet<ulong>();
            foreach (var eventId in subscriber.Deps)
            {
                if (byEvent.TryGetValue(eventId, out var bucket))
                {
                    preds.UnionWith(bucket);
                }
            }

            preds.Remove(subscriber.Id);
            indegree[subscriber.Id] = preds.Count;
            priority[subscriber.Id] = subscriber.Priority;
            predecessors[subscriber.Id] = new List<ulong>(preds);
            foreach (var predecessor in preds)
            {
                if (!dependents.TryGetValue(predecessor, out var list))
                {
                    list = new List<ulong>();
                    dependents[predecessor] = list;
                }

                list.Add(subscriber.Id);
            }
        }

        // 大根堆：键 = (priority, id)，与 trit-schedule 的 ready.pop_last() 一致
        // （id 是 16 位十六进制零填充字符串，字典序 == 数值序）。
        var ready = new PriorityQueue<ulong, MaxKey>();
        foreach (var subscriber in all)
        {
            if (indegree[subscriber.Id] == 0)
            {
                ready.Enqueue(subscriber.Id, new MaxKey(subscriber.Priority, subscriber.Id));
            }
        }

        var scheduled = new List<ulong>(all.Count);
        while (ready.Count > 0)
        {
            ready.TryDequeue(out var id, out _);
            scheduled.Add(id);
            if (dependents.TryGetValue(id, out var children))
            {
                foreach (var child in children)
                {
                    var left = indegree[child] - 1;
                    indegree[child] = left;
                    if (left == 0)
                    {
                        ready.Enqueue(child, new MaxKey(priority[child], child));
                    }
                }
            }
        }

        if (scheduled.Count != all.Count)
        {
            var remaining = scheduled.Count == 0
                ? all.Select(subscriber => Hex(subscriber.Id)).ToList()
                : all.Where(subscriber => !scheduled.Contains(subscriber.Id))
                    .Select(subscriber => Hex(subscriber.Id))
                    .ToList();
            remaining.Sort(StringComparer.Ordinal);
            throw new RtException(
                Status.Arg,
                $"订阅依赖图非法：必需依赖存在环：{string.Join(", ", remaining)}");
        }

        // 关键路径：沿依赖边累加 duration（恒 1）。Kahn 序保证前置一定先排定，
        // 因此按调度序正序扫一遍即可。
        var longest = new Dictionary<ulong, ulong>(all.Count);
        foreach (var id in scheduled)
        {
            var start = 0UL;
            if (predecessors.TryGetValue(id, out var preds))
            {
                foreach (var predecessor in preds)
                {
                    if (longest.TryGetValue(predecessor, out var upstream))
                    {
                        start = Math.Max(start, upstream);
                    }
                }
            }

            longest[id] = start + 1;
        }

        order = scheduled;
        Makespan = (ulong)scheduled.Count;
        CriticalPath = longest.Count == 0 ? 0 : longest.Values.Max();
    }

    private static string Hex(ulong id) => id.ToString("x16");
}

/// <summary>大根堆比较键：priority 降序，再 id 降序。</summary>
internal readonly struct MaxKey : IComparable<MaxKey>
{
    private readonly int priority;
    private readonly ulong id;

    /// <summary>构造比较键。</summary>
    public MaxKey(int priority, ulong id)
    {
        this.priority = priority;
        this.id = id;
    }

    /// <summary>反转序比较（PriorityQueue 是小根堆）。</summary>
    public int CompareTo(MaxKey other)
    {
        var byPriority = other.priority.CompareTo(priority);
        return byPriority != 0 ? byPriority : other.id.CompareTo(id);
    }
}

/// <summary>一条已落地的事务记录（日志项）。</summary>
internal sealed class LogEntry
{
    /// <summary>事务 ID。</summary>
    public required ulong TxnId { get; init; }

    /// <summary>日志序列号（从 1 开始）。</summary>
    public required ulong Seq { get; init; }

    /// <summary>落地时的 tick。</summary>
    public required ulong Tick { get; init; }

    /// <summary>世界键。</summary>
    public required uint Key { get; init; }

    /// <summary>仲裁后的增量。</summary>
    public required TritDelta Delta { get; init; }
}

/// <summary>三进制事务日志：只追加，回滚到快照不截断日志（保留供重放）。</summary>
internal sealed class TxnLog
{
    private readonly List<LogEntry> entries = new();
    private ulong nextSeq;

    /// <summary>追加一条已仲裁落地的记录。</summary>
    public LogEntry Append(ulong txnId, ulong tick, uint key, TritDelta delta)
    {
        nextSeq += 1;
        var entry = new LogEntry
        {
            TxnId = txnId,
            Seq = nextSeq,
            Tick = tick,
            Key = key,
            Delta = delta,
        };
        entries.Add(entry);
        return entry;
    }

    /// <summary>当前序列号（= 日志长度）。</summary>
    public ulong Seq => nextSeq;

    /// <summary>日志长度。</summary>
    public int Length => entries.Count;

    /// <summary>是否已达到容量上限。</summary>
    public bool IsFull(int maxEntries) => entries.Count >= maxEntries;

    /// <summary>从 <paramref name="seq"/> 之后开始的重放（确定性重放用）。</summary>
    public IEnumerable<LogEntry> ReplayFrom(ulong seq) => entries.Where(entry => entry.Seq > seq);
}

/// <summary>事务状态。</summary>
internal enum TxnState
{
    /// <summary>打开中。</summary>
    Open = 0,

    /// <summary>已提交。</summary>
    Committed = 1,

    /// <summary>已回滚。</summary>
    RolledBack = 2,
}

/// <summary>一个未关闭的事务。</summary>
internal sealed class Txn
{
    /// <summary>事务 ID。</summary>
    public required ulong Id { get; init; }

    /// <summary>开启时的 tick。</summary>
    public required ulong OpenedTick { get; init; }

    /// <summary>状态。</summary>
    public TxnState State { get; set; } = TxnState.Open;

    /// <summary>意图清单（未落地）。</summary>
    public List<WriteIntent> Intents { get; } = new();
}

/// <summary>事务表：未关闭事务 + 状态机校验。</summary>
internal sealed class TxnTable
{
    private readonly SortedDictionary<ulong, Txn> txns = new();
    private readonly int maxOpen;
    private ulong nextId;
    private ulong committed;
    private ulong rolledBack;

    /// <summary>构造事务表。</summary>
    public TxnTable(int maxOpen)
    {
        this.maxOpen = maxOpen;
    }

    /// <summary>未关闭事务数。</summary>
    public int OpenCount => txns.Count;

    /// <summary>开事务。</summary>
    public ulong Begin(ulong tick)
    {
        if (txns.Count >= maxOpen)
        {
            throw new RtException(Status.Capacity, $"容量超限：并发事务数达到上限 {maxOpen}");
        }

        nextId += 1;
        var id = nextId;
        txns[id] = new Txn { Id = id, OpenedTick = tick };
        return id;
    }

    /// <summary>追加意图。</summary>
    public void PushIntent(ulong txnId, WriteIntent intent)
    {
        var txn = Require(txnId);
        if (txn.State != TxnState.Open)
        {
            throw new RtException(Status.State, $"事务状态非法：事务 {txnId} 已关闭");
        }

        txn.Intents.Add(intent);
    }

    /// <summary>复制一个未关闭事务的意图（提交前预仲裁用；失败时事务保持打开）。</summary>
    public List<WriteIntent> CloneIntents(ulong txnId)
    {
        var txn = RequireOpen(txnId);
        return new List<WriteIntent>(txn.Intents);
    }

    /// <summary>标记提交。</summary>
    public void FinishCommit(ulong txnId)
    {
        var txn = Require(txnId);
        if (txn.State != TxnState.Open)
        {
            throw new RtException(Status.State, $"事务状态非法：事务 {txnId} 已关闭");
        }

        txn.State = TxnState.Committed;
        committed += 1;
    }

    /// <summary>标记回滚。</summary>
    public void FinishRollback(ulong txnId)
    {
        var txn = Require(txnId);
        if (txn.State != TxnState.Open)
        {
            throw new RtException(Status.State, $"事务状态非法：事务 {txnId} 已关闭");
        }

        txn.State = TxnState.RolledBack;
        rolledBack += 1;
    }

    /// <summary>除 <paramref name="exclude"/> 外所有未关闭事务的待落地意图。</summary>
    public List<WriteIntent> PendingIntents(ulong exclude)
    {
        var result = new List<WriteIntent>();
        foreach (var txn in txns.Values)
        {
            if (txn.Id != exclude && txn.State == TxnState.Open)
            {
                result.AddRange(txn.Intents);
            }
        }

        return result;
    }

    /// <summary>累计提交数。</summary>
    public ulong CommittedCount => committed;

    /// <summary>累计回滚数。</summary>
    public ulong RolledBackCount => rolledBack;

    private Txn Require(ulong txnId)
    {
        if (!txns.TryGetValue(txnId, out var txn))
        {
            throw new RtException(Status.NotFound, $"事务不存在：{txnId}");
        }

        return txn;
    }

    private Txn RequireOpen(ulong txnId)
    {
        var txn = Require(txnId);
        if (txn.State != TxnState.Open)
        {
            var closed = txn.State == TxnState.Committed ? "提交" : "回滚";
            throw new RtException(Status.State, $"事务状态非法：事务 {txnId} 已{closed}");
        }

        return txn;
    }
}

/// <summary>一个快照。</summary>
internal sealed class SnapshotRecord
{
    /// <summary>快照 ID。</summary>
    public required ulong Id { get; init; }

    /// <summary>打快照时的 tick。</summary>
    public required ulong Tick { get; init; }

    /// <summary>打快照时的事务日志序列号。</summary>
    public required ulong LogSeq { get; init; }

    /// <summary>世界投影。</summary>
    public required SortedDictionary<uint, TritDelta> World { get; init; }

    /// <summary>序列化字节。</summary>
    public required byte[] Bytes { get; init; }
}

/// <summary>影子日志：只记录不一致，从不提交。</summary>
internal sealed class ShadowLog
{
    /// <summary>已参与差分的次数。</summary>
    public ulong Compared { get; set; }

    /// <summary>发现不一致的次数。</summary>
    public ulong Divergences { get; set; }

    /// <summary>最近的不一致签名（滚动窗口，最多 64 条）。</summary>
    public Queue<byte[]> Recent { get; } = new();
}

/// <summary>统计计数器（顺序固定，见 docs/ABI.md）。</summary>
internal sealed class Stats
{
    /// <summary>发布次数。</summary>
    public ulong EventsPublished { get; set; }

    /// <summary>派发次数。</summary>
    public ulong EventsDispatched { get; set; }

    /// <summary>提交事务数。</summary>
    public ulong TxnsCommitted { get; set; }

    /// <summary>回滚事务数。</summary>
    public ulong TxnsRolledBack { get; set; }

    /// <summary>快照数。</summary>
    public ulong SnapshotsTaken { get; set; }

    /// <summary>差分次数。</summary>
    public ulong Diffs { get; set; }

    /// <summary>仲裁次数。</summary>
    public ulong Arbitrations { get; set; }

    /// <summary>冲突次数。</summary>
    public ulong Conflicts { get; set; }

    /// <summary>落地意图数。</summary>
    public ulong IntentsApplied { get; set; }

    /// <summary>PRNG 抽样数。</summary>
    public ulong PrngDraws { get; set; }
}

/// <summary>
/// 托管回退内核：原生 Rust 运行时（<c>trit_rt.dll</c>）不可用时的纯 C# 实现，
/// 与 <see cref="NativeKernel"/> 实现同一 <see cref="ITritKernel"/> 契约、同一条
/// 线格式、同一套三进制语义——因此能在影子差分中与原生内核逐字节对拍。
/// </summary>
/// <remarks>
/// 与原生内核的**已知差异**（能力协商的意义正在于此）：
/// <list type="bullet">
/// <item><description>能力报告里 <c>AbiVersion</c> 恒为 0（没有 C ABI），<c>Mode = Managed</c>；</description></item>
/// <item><description>不置 <see cref="CapabilityBits.Ai"/> 位，AI 投票 opcode 直接返回参数非法——三值权重网络推理留给原生侧；</description></item>
/// <item><description>性能与内存布局不保证等同，只保证**语义与响应字节**等同。</description></item>
/// </list>
/// </remarks>
public sealed class ManagedKernel : ITritKernel
{
    /// <summary>世界车道数上限（与 Rust <c>MAX_WORLD_LANES</c> 一致）。</summary>
    public const int MaxWorldLanes = 1 << 20;

    /// <summary>影子签名滚动窗口大小。</summary>
    public const int ShadowWindow = 64;

    /// <summary>事件载荷缓存上限。</summary>
    public const int MaxEventPayloads = 4096;

    /// <summary>单次 PRNG 抽样上限。</summary>
    public const int MaxPrngCount = 1 << 16;

    private readonly ManagedConfig config;
    private readonly EventRouter router;
    private readonly TxnLog log = new();
    private readonly TxnTable txns;
    private readonly SortedDictionary<uint, TritDelta> world = new();
    private readonly SortedDictionary<ulong, SnapshotRecord> snapshots = new();
    private readonly SortedDictionary<uint, TritDelta[]> eventPayloads = new();
    private readonly DeterministicPrng prng;
    private readonly ShadowLog shadow = new();
    private readonly Stats stats = new();
    private ulong tick;
    private ulong nextSnapshotId;
    private string lastError = string.Empty;

    /// <summary>用默认配置构造托管内核。</summary>
    public ManagedKernel()
        : this(new ManagedConfig())
    {
    }

    /// <summary>用配置构造托管内核。</summary>
    public ManagedKernel(ManagedConfig config)
    {
        this.config = config;
        router = new EventRouter(config.MaxSubscribers);
        txns = new TxnTable(config.MaxOpenTxns);
        prng = new DeterministicPrng(0);
    }

    /// <summary>从 JSON 配置构造；空输入用默认值。</summary>
    public static ManagedKernel FromJson(string configJson) =>
        new(ManagedConfig.FromJson(string.IsNullOrEmpty(configJson) ? null : Encoding.UTF8.GetBytes(configJson)));

    /// <summary>运行模式。</summary>
    public KernelMode Mode => KernelMode.Managed;

    /// <summary>最近一次错误信息。</summary>
    public string LastError => lastError;

    /// <summary>调度指标（观测用）。</summary>
    public (ulong Makespan, ulong CriticalPath) ScheduleMetrics => (router.Makespan, router.CriticalPath);

    /// <summary>当前 tick。</summary>
    public ulong Tick => tick;

    /// <summary>世界投影只读视图（按键升序）。</summary>
    public IReadOnlyDictionary<uint, TritDelta> World => world;

    /// <summary>能力报告。</summary>
    public CapabilityReport Capabilities()
    {
        var response = Submit(Opcode.Capabilities, Requests.Capabilities());
        return KernelResponseMapper.ToCapabilityReport(response, KernelMode.Managed);
    }

    /// <summary>提交一个 opcode，返回拆框后的载荷。</summary>
    public KernelResponse Submit(uint opcode, byte[] payload)
    {
        try
        {
            var response = Dispatch(opcode, payload);
            return new KernelResponse(Status.Ok, response);
        }
        catch (RtException error)
        {
            lastError = error.Message;
            var diagnostics = error.Status == Status.Conflict
                ? EncodeDisputes(error.Disputes)
                : Array.Empty<byte>();
            return new KernelResponse(error.Status, diagnostics);
        }
        catch (WireException error)
        {
            lastError = error.Message;
            return new KernelResponse(Status.Decode, Array.Empty<byte>());
        }
    }

    /// <summary>查询。</summary>
    public KernelResponse Query(byte[] query) => Submit(Opcode.Query, query);

    /// <summary>step。</summary>
    public StepResult Step(ulong tick)
    {
        var response = Submit(Opcode.Step, Requests.Step(tick));
        return KernelResponseMapper.ToStepResult(response, lastError);
    }

    /// <summary>订阅。</summary>
    public ulong Subscribe(uint modId, uint eventId, int priority, uint[] deps)
    {
        var response = Submit(Opcode.Subscribe, Requests.Subscribe(modId, eventId, priority, deps));
        return KernelResponseMapper.ToU64(response, lastError);
    }

    /// <summary>退订。</summary>
    public void Unsubscribe(ulong subId)
    {
        var response = Submit(Opcode.Unsubscribe, Requests.Unsubscribe(subId));
        if (!response.IsOk)
        {
            throw new KernelException(response.Status, $"退订失败：{lastError}");
        }
    }

    /// <summary>发布。</summary>
    public uint Publish(uint eventId, TritDelta[] payload)
    {
        var response = Submit(Opcode.Publish, Requests.Publish(eventId, payload));
        return KernelResponseMapper.ToU32(response, lastError);
    }

    /// <summary>开事务。</summary>
    public ulong BeginTransaction()
    {
        var response = Submit(Opcode.TxnBegin, Requests.TxnBegin());
        return KernelResponseMapper.ToU64(response, lastError);
    }

    /// <summary>提交意图。</summary>
    public void SubmitIntent(ulong txnId, uint key, TritDelta delta)
    {
        var response = Submit(Opcode.TxnSubmit, Requests.TxnSubmit(txnId, key, delta));
        if (!response.IsOk)
        {
            throw new KernelException(response.Status, $"提交意图失败：{lastError}");
        }
    }

    /// <summary>提交事务。</summary>
    public TxnCommitResult Commit(ulong txnId)
    {
        var response = Submit(Opcode.TxnCommit, Requests.TxnCommit(txnId));
        return KernelResponseMapper.ToCommitResult(response, lastError);
    }

    /// <summary>回滚事务。</summary>
    public void RollbackTransaction(ulong txnId)
    {
        var response = Submit(Opcode.TxnRollback, Requests.TxnRollback(txnId));
        if (!response.IsOk)
        {
            throw new KernelException(response.Status, $"回滚事务失败：{lastError}");
        }
    }

    /// <summary>仲裁。</summary>
    public ArbitrateResult Arbitrate(IReadOnlyList<WriteIntent> intents)
    {
        var response = Submit(Opcode.Arbitrate, Requests.Arbitrate(intents));
        return KernelResponseMapper.ToArbitrateResult(response, lastError);
    }

    /// <summary>打快照。</summary>
    public ulong Snapshot()
    {
        var response = Submit(Opcode.Snapshot, Requests.Snapshot());
        return KernelResponseMapper.ToU64(response, lastError);
    }

    /// <summary>
    /// 快照字节：<c>[u64 id][世界编码]</c>，**不装帧**——
    /// 与原生 <c>tt_rt_snapshot</c> 经 <see cref="NativeKernel.SnapshotBytes"/>
    /// 拆框后返回的载荷逐字节一致（装帧只发生在 FFI 缓冲区协议内部）。
    /// </summary>
    public byte[] SnapshotBytes()
    {
        var id = Snapshot();
        var record = snapshots[id];
        var payload = new byte[8 + record.Bytes.Length];
        BinaryPrimitives.WriteUInt64LittleEndian(payload, id);
        record.Bytes.CopyTo(payload, 8);
        return payload;
    }

    /// <summary>差分。</summary>
    public DiffResult Diff(ulong a, ulong b)
    {
        var response = Submit(Opcode.Diff, Requests.Diff(a, b));
        return KernelResponseMapper.ToDiffResult(response, lastError);
    }

    /// <summary>回滚到快照。</summary>
    public void RollbackTo(ulong snapshotId)
    {
        var response = Submit(Opcode.RollbackTo, Requests.RollbackTo(snapshotId));
        if (!response.IsOk)
        {
            throw new KernelException(response.Status, $"回滚到快照失败：{lastError}");
        }
    }

    /// <summary>抽随机数。</summary>
    public TritDelta[] NextRandom(int count)
    {
        var response = Submit(Opcode.PrngNext, Requests.PrngNext(count));
        return KernelResponseMapper.ToTrits(response, lastError);
    }

    /// <summary>记录影子差分。</summary>
    public ulong ShadowRecord(byte[] signature)
    {
        var trits = Array.ConvertAll(signature, value => (TritDelta)value);
        var response = Submit(Opcode.ShadowRecord, Requests.ShadowRecord(trits));
        return KernelResponseMapper.ToU64(response, lastError);
    }

    /// <summary>统计。</summary>
    public ulong[] Stats()
    {
        var response = Submit(Opcode.Stats, Requests.Stats());
        return KernelResponseMapper.ToStats(response, lastError);
    }

    /// <summary>从 <paramref name="seq"/> 之后重放日志（确定性重放验证用）。</summary>
    public int ReplayFrom(ulong seq)
    {
        var applied = 0;
        foreach (var entry in log.ReplayFrom(seq))
        {
            var current = world.TryGetValue(entry.Key, out var value) ? value : TritDelta.Zero;
            world[entry.Key] = ApplyDelta(current, entry.Delta);
            applied += 1;
        }

        return applied;
    }

    /// <summary>销毁（托管内核无外部资源，保留以统一生命周期）。</summary>
    public void Dispose()
    {
    }

    // ---- opcode 分发：与 Rust TritRuntime::dispatch 逐分支对齐 ----

    private byte[] Dispatch(uint opcode, byte[] payload)
    {
        if (opcode == 0)
        {
            throw new RtException(Status.BadOp, "opcode 为 0 或保留值");
        }

        return opcode switch
        {
            Opcode.Subscribe => OpSubscribe(payload),
            Opcode.Unsubscribe => OpUnsubscribe(payload),
            Opcode.Publish => OpPublish(payload),
            Opcode.TxnBegin => OpTxnBegin(payload),
            Opcode.TxnCommit => OpTxnCommit(payload),
            Opcode.TxnRollback => OpTxnRollback(payload),
            Opcode.TxnSubmit => OpTxnSubmit(payload),
            Opcode.Arbitrate => OpArbitrate(payload),
            Opcode.Query => OpQuery(payload),
            Opcode.Step => OpStep(payload),
            Opcode.Snapshot => OpSnapshot(payload),
            Opcode.Diff => OpDiff(payload),
            Opcode.RollbackTo => OpRollbackTo(payload),
            Opcode.PrngNext => OpPrngNext(payload),
            Opcode.ShadowRecord => OpShadowRecord(payload),
            Opcode.Capabilities => OpCapabilities(),
            Opcode.Stats => OpStats(),
            Opcode.AiVote => throw new RtException(
                Status.Arg,
                "输入非法：托管回退内核不支持 AI 投票，请使用原生内核或回退到 C# 路径"),
            Opcode.AiSetModel => throw new RtException(
                Status.Arg,
                "输入非法：托管回退内核不支持装载 AI 模型"),
            _ => throw new RtException(Status.Arg, $"输入非法：未知 opcode {opcode:#x}"),
        };
    }

    private byte[] OpSubscribe(byte[] payload)
    {
        var reader = new WireReader(payload);
        var modId = reader.U32();
        var eventId = reader.U32();
        var priority = reader.I32();
        var depCount = checked((int)reader.U32());
        var deps = new uint[Math.Min(depCount, 4096)];
        for (var i = 0; i < deps.Length; i++)
        {
            deps[i] = reader.U32();
        }

        var subId = router.Subscribe(modId, eventId, priority, deps);
        return new WireWriter().U64(subId).ToArray();
    }

    private byte[] OpUnsubscribe(byte[] payload)
    {
        var reader = new WireReader(payload);
        router.Unsubscribe(reader.U64());
        return Array.Empty<byte>();
    }

    private byte[] OpPublish(byte[] payload)
    {
        var reader = new WireReader(payload);
        var eventId = reader.U32();
        var trits = reader.Trits();
        stats.EventsPublished += 1;
        if (eventPayloads.Count >= MaxEventPayloads)
        {
            // 滚动淘汰：清掉最早插入的一批（SortedDictionary 序即事件 ID 序）。
            var oldest = eventPayloads.Keys.Take(1024).ToArray();
            foreach (var key in oldest)
            {
                eventPayloads.Remove(key);
            }
        }

        eventPayloads[eventId] = trits;
        var dirty = router.Publish(eventId);
        return new WireWriter().U32((uint)dirty).ToArray();
    }

    private byte[] OpTxnBegin(byte[] payload)
    {
        _ = new WireReader(payload).U32();
        var txnId = txns.Begin(tick);
        return new WireWriter().U64(txnId).ToArray();
    }

    private byte[] OpTxnSubmit(byte[] payload)
    {
        var reader = new WireReader(payload);
        var txnId = reader.U64();
        var key = reader.U32();
        var delta = (TritDelta)reader.U8();
        var intent = new WriteIntent(txnId, key, delta);
        if (txnId == 0)
        {
            // 自动提交：立即仲裁 + 落地。
            var (applied, _) = CommitIntents(new[] { intent }, 0);
            stats.IntentsApplied += applied;
            return Array.Empty<byte>();
        }

        txns.PushIntent(txnId, intent);
        return Array.Empty<byte>();
    }

    private byte[] OpTxnCommit(byte[] payload)
    {
        var reader = new WireReader(payload);
        var txnId = reader.U64();
        // 先克隆意图做仲裁：冲突时事务保持打开，调用方可重试或回滚。
        var intents = txns.CloneIntents(txnId);
        var (applied, logSeq) = CommitIntents(intents, txnId);
        txns.FinishCommit(txnId);
        stats.TxnsCommitted += 1;
        stats.IntentsApplied += applied;
        return new WireWriter().U32(applied).U64(logSeq).ToArray();
    }

    private byte[] OpTxnRollback(byte[] payload)
    {
        var reader = new WireReader(payload);
        txns.FinishRollback(reader.U64());
        stats.TxnsRolledBack += 1;
        return Array.Empty<byte>();
    }

    private byte[] OpArbitrate(byte[] payload)
    {
        var reader = new WireReader(payload);
        var count = checked((int)reader.U32());
        var intents = new List<WriteIntent>(Math.Min(count, 4096));
        for (var i = 0; i < count; i++)
        {
            intents.Add(new WriteIntent(reader.U64(), reader.U32(), (TritDelta)reader.U8()));
        }

        stats.Arbitrations += 1;
        try
        {
            var resolved = Arbitrate(intents, config.Arbitration);
            var writer = new WireWriter().U8(0).U32((uint)resolved.Count);
            foreach (var (key, delta) in resolved)
            {
                writer.U32(key).U8((byte)delta);
            }

            return writer.U32(0).ToArray();
        }
        catch (RtException error) when (error.Status == Status.Conflict)
        {
            stats.Conflicts += (ulong)error.Disputes.Count;
            var writer = new WireWriter().U8(1).U32(0).U32((uint)error.Disputes.Count);
            foreach (var dispute in error.Disputes)
            {
                EncodeDispute(writer, dispute);
            }

            return writer.ToArray();
        }
    }

    private byte[] OpQuery(byte[] payload)
    {
        var reader = new WireReader(payload);
        var queryId = reader.U32();
        var writer = new WireWriter();
        switch (queryId)
        {
            case QueryId.DirtyCount:
                writer.U32((uint)router.DirtyCount);
                break;
            case QueryId.WorldSize:
                writer.U32((uint)world.Count);
                break;
            case QueryId.LogLen:
                writer.U64((ulong)log.Length);
                break;
            case QueryId.SnapshotCount:
                writer.U32((uint)snapshots.Count);
                break;
            case QueryId.OpenTxnCount:
                writer.U32((uint)txns.OpenCount);
                break;
            case QueryId.Shadow:
                writer.U64(shadow.Compared).U64(shadow.Divergences);
                break;
            case QueryId.EventPayload:
                var eventId = reader.U32();
                var trits = eventPayloads.TryGetValue(eventId, out var cached) ? cached : Array.Empty<TritDelta>();
                writer.Trits(trits);
                break;
            default:
                throw new RtException(Status.Arg, $"输入非法：未知查询 {queryId}");
        }

        return writer.ToArray();
    }

    private byte[] OpStep(byte[] payload)
    {
        var reader = new WireReader(payload);
        tick = reader.U64();
        // 每个 tick 用 (seed, tick) 重新播种 ⇒ 同 tick 同序列。
        prng.Reseed(config.Seed, tick);
        var dispatch = router.TakeDispatch();
        var writer = new WireWriter().U32((uint)dispatch.Count);
        foreach (var subId in dispatch)
        {
            var subscriber = router.Get(subId);
            if (subscriber is null)
            {
                continue;
            }

            writer.U64(subscriber.Id).U32(subscriber.EventId).U32(subscriber.ModId);
            stats.EventsDispatched += 1;
        }

        return writer.ToArray();
    }

    private byte[] OpSnapshot(byte[] payload)
    {
        _ = new WireReader(payload).U32();
        nextSnapshotId += 1;
        var id = nextSnapshotId;
        var bytes = WorldCodec.Encode(world, tick, log.Seq);
        var record = new SnapshotRecord
        {
            Id = id,
            Tick = tick,
            LogSeq = log.Seq,
            World = new SortedDictionary<uint, TritDelta>(world),
            Bytes = bytes,
        };
        if (snapshots.Count >= config.MaxSnapshots)
        {
            // 淘汰最旧的一个。
            var oldest = snapshots.Keys.First();
            snapshots.Remove(oldest);
        }

        snapshots[id] = record;
        stats.SnapshotsTaken += 1;
        return new WireWriter().U64(id).ToArray();
    }

    private byte[] OpDiff(byte[] payload)
    {
        var reader = new WireReader(payload);
        var snapA = reader.U64();
        var snapB = reader.U64();
        if (!snapshots.TryGetValue(snapA, out var before))
        {
            throw new RtException(Status.NotFound, $"快照不存在：{snapA}");
        }

        if (!snapshots.TryGetValue(snapB, out var after))
        {
            throw new RtException(Status.NotFound, $"快照不存在：{snapB}");
        }

        stats.Diffs += 1;
        return EncodeDiff(DiffWorlds(before.World, after.World));
    }

    private byte[] OpRollbackTo(byte[] payload)
    {
        var reader = new WireReader(payload);
        var snapId = reader.U64();
        if (!snapshots.TryGetValue(snapId, out var record))
        {
            throw new RtException(Status.NotFound, $"快照不存在：{snapId}");
        }

        // 只还原世界投影；日志保持只追加，之后可用 ReplayFrom 重做。
        world.Clear();
        foreach (var pair in record.World)
        {
            world[pair.Key] = pair.Value;
        }

        return Array.Empty<byte>();
    }

    private byte[] OpPrngNext(byte[] payload)
    {
        var reader = new WireReader(payload);
        var count = checked((int)reader.U32());
        if (count > MaxPrngCount)
        {
            throw new RtException(
                Status.Arg,
                $"输入非法：单次抽样数 {count} 超过上限 {MaxPrngCount}");
        }

        var trits = prng.FillTrits(count);
        stats.PrngDraws += (ulong)count;
        return new WireWriter().Trits(trits).ToArray();
    }

    private byte[] OpShadowRecord(byte[] payload)
    {
        var reader = new WireReader(payload);
        var signature = reader.Trits();
        shadow.Compared += 1;
        // 调用方语义：签名非空 = 发现不一致。只记录，不提交，不影响任何状态。
        if (signature.Length > 0)
        {
            shadow.Divergences += 1;
            if (shadow.Recent.Count >= ShadowWindow)
            {
                shadow.Recent.Dequeue();
            }

            shadow.Recent.Enqueue(WireFormat.PackTrits(signature));
        }

        return new WireWriter().U64(shadow.Divergences).ToArray();
    }

    private byte[] OpCapabilities()
    {
        var writer = new WireWriter()
            .U32(0)
            .U32(NativeKernel.MinimumRuntimeVersion)
            .U64(ManagedCapabilityBits())
            .U8((byte)config.Arbitration);
        return writer.ToArray();
    }

    private byte[] OpStats() => new WireWriter()
        .U64(stats.EventsPublished)
        .U64(stats.EventsDispatched)
        .U64(stats.TxnsCommitted)
        .U64(stats.TxnsRolledBack)
        .U64(stats.SnapshotsTaken)
        .U64(stats.Diffs)
        .U64(stats.Arbitrations)
        .U64(stats.Conflicts)
        .U64(stats.IntentsApplied)
        .U64(stats.PrngDraws)
        .ToArray();

    // ---- 内部：仲裁 / 落地 / 差分 ----

    /// <summary>能力位图（与 Rust 一致，但不含 AI）。</summary>
    public static ulong ManagedCapabilityBits() =>
        (ulong)(TritRuntime.Wire.CapabilityBits.Router
                | TritRuntime.Wire.CapabilityBits.TxnLog
                | TritRuntime.Wire.CapabilityBits.Snapshot
                | TritRuntime.Wire.CapabilityBits.Diff
                | TritRuntime.Wire.CapabilityBits.Arbitrate
                | TritRuntime.Wire.CapabilityBits.Prng
                | TritRuntime.Wire.CapabilityBits.Codec
                | TritRuntime.Wire.CapabilityBits.Shadow);

    /// <summary>
    /// 仲裁一批意图。Ok = 全部车道收敛（按键升序）；失败 = 争议车道 + 全部肇事方。
    /// 严格模式：同车道两个及以上驱动即争议（即使同向）；多数模式：计票，平局争议。
    /// </summary>
    private static List<(uint Key, TritDelta Delta)> Arbitrate(
        IReadOnlyList<WriteIntent> intents,
        ArbitrationMode mode)
    {
        var lanes = new SortedDictionary<uint, List<WriteIntent>>();
        foreach (var intent in intents)
        {
            if (intent.Delta == TritDelta.Zero)
            {
                continue; // 弃权 = 高阻，不驱动就不参与仲裁
            }

            if (!lanes.TryGetValue(intent.Key, out var bucket))
            {
                bucket = new List<WriteIntent>();
                lanes[intent.Key] = bucket;
            }

            bucket.Add(intent);
        }

        var resolved = new List<(uint Key, TritDelta Delta)>();
        var disputes = new List<LaneDispute>();
        foreach (var (key, drivers) in lanes)
        {
            if (mode == ArbitrationMode.Strict)
            {
                // 硬件式总线语义：同车道两个及以上驱动即争议（即使同向）。
                if (drivers.Count >= 2)
                {
                    disputes.Add(Dispute(key, drivers));
                }
                else
                {
                    resolved.Add((key, drivers[0].Delta));
                }

                continue;
            }

            // 游戏式计票：-1/+1 多数胜出，平局争议。
            var negatives = drivers.Count(intent => intent.Delta == TritDelta.Neg);
            var positives = drivers.Count(intent => intent.Delta == TritDelta.Pos);
            if (positives > negatives)
            {
                resolved.Add((key, TritDelta.Pos));
            }
            else if (negatives > positives)
            {
                resolved.Add((key, TritDelta.Neg));
            }
            else
            {
                disputes.Add(Dispute(key, drivers));
            }
        }

        if (disputes.Count > 0)
        {
            throw new RtException(
                Status.Conflict,
                $"冲突未仲裁：{disputes.Count} 条车道争议",
                disputes);
        }

        return resolved;
    }

    /// <summary>三进制累加：把增量叠加到车道当前值上，clamp 在 <c>[-1, +1]</c>。</summary>
    public static TritDelta ApplyDelta(TritDelta current, TritDelta delta)
    {
        var sum = (int)current + (int)delta - 1;
        return (TritDelta)Math.Clamp(sum, 0, 2);
    }

    /// <summary>
    /// 仲裁一批意图并落地；冲突时整体失败（不产生部分应用）。
    /// 仲裁范围 = 本批意图 ∪ 其他未关闭事务的待落地意图——跨模组冲突在
    /// **提交时点**暴露，而不是各自落地后才互相覆盖。
    /// </summary>
    private (uint Applied, ulong LogSeq) CommitIntents(IReadOnlyList<WriteIntent> intents, ulong txnId)
    {
        var pending = txns.PendingIntents(txnId);
        if (intents.Count == 0 && pending.Count == 0)
        {
            return (0, log.Seq);
        }

        var batch = new List<WriteIntent>(intents.Count + pending.Count);
        batch.AddRange(intents);
        batch.AddRange(pending);
        List<(uint Key, TritDelta Delta)> resolved;
        try
        {
            resolved = Arbitrate(batch, config.Arbitration);
        }
        catch (RtException error) when (error.Status == Status.Conflict)
        {
            throw new RtException(
                Status.Conflict,
                $"冲突未仲裁：{error.Disputes.Count} 条车道争议",
                error.Disputes);
        }

        // 先做容量预检，保证「要么全落地，要么全不落地」。
        var newLanes = resolved.Count(entry => !world.ContainsKey(entry.Key));
        if (world.Count + newLanes > MaxWorldLanes)
        {
            throw new RtException(
                Status.Capacity,
                $"容量超限：世界车道数将达到上限 {MaxWorldLanes}");
        }

        if (log.IsFull(config.MaxLogEntries))
        {
            throw new RtException(
                Status.Capacity,
                $"容量超限：事务日志达到上限 {config.MaxLogEntries} 条");
        }

        var applied = 0u;
        foreach (var (key, delta) in resolved)
        {
            if (delta == TritDelta.Zero)
            {
                continue;
            }

            var current = world.TryGetValue(key, out var value) ? value : TritDelta.Zero;
            world[key] = ApplyDelta(current, delta);
            log.Append(txnId, tick, key, delta);
            applied += 1;
        }

        return (applied, log.Seq);
    }

    /// <summary>差分两个世界投影，返回发生变化的车道（按键升序）。</summary>
    public static List<DiffEntry> DiffWorlds(
        IReadOnlyDictionary<uint, TritDelta> before,
        IReadOnlyDictionary<uint, TritDelta> after)
    {
        var entries = new List<DiffEntry>();
        foreach (var (key, updated) in after)
        {
            var old = before.TryGetValue(key, out var value) ? value : TritDelta.Zero;
            if (old != updated)
            {
                entries.Add(new DiffEntry(key, old, updated));
            }
        }

        foreach (var (key, old) in before)
        {
            if (!after.ContainsKey(key))
            {
                entries.Add(new DiffEntry(key, old, TritDelta.Zero));
            }
        }

        entries.Sort((left, right) => left.Key.CompareTo(right.Key));
        return entries;
    }

    private static LaneDispute Dispute(uint key, List<WriteIntent> drivers)
    {
        var sorted = drivers
            .Select(intent => (intent.TxnId, intent.Delta))
            .OrderBy(driver => driver.Item1)
            .ThenBy(driver => driver.Item2)
            .ToList();
        return new LaneDispute(key, sorted);
    }

    private static void EncodeDispute(WireWriter writer, LaneDispute dispute)
    {
        writer.U32(dispute.Key).U32((uint)dispute.Drivers.Count);
        foreach (var (txnId, delta) in dispute.Drivers)
        {
            writer.U64(txnId).U8((byte)delta);
        }
    }

    private static byte[] EncodeDisputes(IReadOnlyList<LaneDispute> disputes)
    {
        var writer = new WireWriter().U32((uint)disputes.Count);
        foreach (var dispute in disputes)
        {
            EncodeDispute(writer, dispute);
        }

        return writer.ToArray();
    }

    private static byte[] EncodeDiff(IReadOnlyList<DiffEntry> entries)
    {
        var writer = new WireWriter().U32((uint)entries.Count);
        foreach (var entry in entries)
        {
            writer.U32(entry.Key).U8((byte)entry.Old).U8((byte)entry.New);
        }

        return writer.ToArray();
    }
}
