#nullable enable

using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.Linq;
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
                // 容量下限必须与 Rust 侧 RuntimeConfig::sanitize 完全一致：
                // 两个内核在同一份坏配置下要表现得一样，否则回退到托管路径时
                // 才会暴露「日志永远是满的」这类停摆问题。
                MaxSubscribers = Math.Max(1, dto.MaxSubscribers ?? 65_536),
                MaxOpenTxns = Math.Max(1, dto.MaxOpenTxns ?? 4_096),
                MaxSnapshots = Math.Max(1, dto.MaxSnapshots ?? 64),
                MaxLogEntries = Math.Max(4_096, dto.MaxLogEntries ?? (1 << 20)),
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
    /// <summary>
    /// 已关闭事务的保留条数；与 Rust 侧 <c>CLOSED_TXN_RETAIN</c> 同值同语义。
    /// </summary>
    /// <remarks>
    /// 保留它们只是为了「重复提交 / 重复回滚」能报出状态错误而不是「事务不存在」
    /// （诊断友好）。但绝不能全留：模组每个 tick 开一个事务，全留会让事务表随 tick
    /// 无界增长，而且 <c>max_open</c> 的额度被历史事务占满——原生侧实测跑到 4096 tick
    /// 之后 <c>begin</c> 永久失败、**整个旁路写入路径就此停摆**。
    /// 托管回退内核必须同口径：回退不是换一套表现，是换一条实现。
    /// </remarks>
    private const int ClosedRetain = 1024;

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

    /// <summary>
    /// 未关闭事务数。
    /// </summary>
    /// <remarks>
    /// <b>只数还在打开中的</b>：已提交 / 已回滚的不算并发。用条目总数当并发数，
    /// 会让一张只增不减的表把容量额度吃光。
    /// </remarks>
    public int OpenCount => txns.Values.Count(txn => txn.State == TxnState.Open);

    /// <summary>开事务。</summary>
    public ulong Begin(ulong tick)
    {
        // 上限只约束「真正打开中」的事务；已关闭的不算并发。
        int open = OpenCount;
        if (open >= maxOpen)
        {
            throw new RtException(Status.Capacity, $"并发事务数达到上限 {maxOpen}");
        }

        // 已关闭事务留最近 ClosedRetain 条即可。键是自增 id，SortedDictionary 的
        // 序即开启顺序，取前若干个就是最旧的。
        int closed = txns.Count - open;
        if (closed > ClosedRetain)
        {
            int drop = closed - ClosedRetain;
            var stale = txns
                .Values.Where(txn => txn.State != TxnState.Open)
                .Take(drop)
                .Select(txn => txn.Id)
                .ToList();
            foreach (ulong staleId in stale)
            {
                txns.Remove(staleId);
            }
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

/// <summary>托管扫描路径的单 owner 累计账本。</summary>
internal sealed class ModBudgetAccumulator
{
    /// <summary>已提交任务数。</summary>
    public ulong Submitted { get; set; }

    /// <summary>成功完成任务数。</summary>
    public ulong Completed { get; set; }

    /// <summary>已取消任务数。</summary>
    public ulong Cancelled { get; set; }

    /// <summary>因预算拒绝的任务数。</summary>
    public ulong Rejected { get; set; }

    /// <summary>累计执行步数。</summary>
    public ulong Steps { get; set; }
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

    /// <summary>事件载荷缓存上限（条数）。</summary>
    public const int MaxEventPayloads = 4096;

    /// <summary>
    /// 事件载荷缓存上限（trit 总数）；与 Rust 侧 <c>MAX_EVENT_PAYLOAD_TRITS</c> 同值。
    /// </summary>
    /// <remarks>
    /// 两道闸缺一不可，理由原生侧写在 <c>op_publish</c> 上：条数闸挡「很多个小事件」，
    /// 字节闸挡「少量超大事件」。只有条数闸时，4096 条 × 每条 100 万 trit = 4 GB 常驻，
    /// 一次坏调用就能把进程顶到系统拒绝分配——而调用方只是「发布了一个大事件」，看不出错。
    /// </remarks>
    public const int MaxEventPayloadTrits = 8 << 20;

    /// <summary>单次 PRNG 抽样上限。</summary>
    public const int MaxPrngCount = 1 << 16;

    private readonly ManagedConfig config;
    // 这三个**不是** readonly：会话复位时整实例替换，比逐个 Clear() 更容易保证
    // 「一点旧状态都不留」（尤其是 TxnLog 的内部容量）。
    // 路由器不替换：订阅关系属于"模组装载期"，跨存档依然有效。
    private readonly EventRouter router;
    private TxnLog log = new();
    private TxnTable txns;
    private readonly SortedDictionary<uint, TritDelta> world = new();
    private readonly SortedDictionary<ulong, SnapshotRecord> snapshots = new();
    private readonly SortedDictionary<uint, TritDelta[]> eventPayloads = new();

    /// <summary>当前缓存的事件载荷 trit 总数（字节闸门的判据）。</summary>
    private int eventPayloadTrits;
    private DeterministicPrng prng;
    private readonly ShadowLog shadow = new();
    private readonly Stats stats = new();
    /// <summary>已完成任务队列；由 <see cref="scanGate"/> 保护。</summary>
    /// <remarks>
    /// 刻意用 <see cref="List{T}"/> 而不是 <c>ConcurrentQueue</c>：
    /// 按 owner 过滤时必须做到「**只移除本次返回的那几条**」，
    /// 队列的 <c>TryDequeue</c> 只能取出队头，无法把中间某几条挑出来。
    /// </remarks>
    private readonly List<JobResult> completedJobs = new();
    private readonly Dictionary<string, ModBudgetAccumulator> modBudgets = new(StringComparer.Ordinal);
    private readonly object scanGate = new();
    private ulong nextJobId;
    private ulong poolStarted;
    private ulong poolCompleted;
    private ulong poolFailed;
    private ulong poolCancelled;
    private ulong poolSteps;
    private GpuPolicy gpuPolicy = GpuPolicy.Auto;
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
        catch (JsonException error)
        {
            lastError = $"JSON 载荷非法：{error.Message}";
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

    /// <summary>批量提交意图。</summary>
    public void SubmitIntents(ulong txnId, IReadOnlyList<uint> keys, IReadOnlyList<TritDelta> deltas)
    {
        if (keys.Count == 0)
        {
            return;
        }

        var response = Submit(Opcode.TxnSubmitBatch, Requests.TxnSubmitBatch(txnId, keys, deltas));
        if (!response.IsOk)
        {
            throw new KernelException(response.Status, $"批量提交意图失败：{lastError}");
        }
    }

    /// <summary>提交事务。</summary>
    public TxnCommitResult Commit(ulong txnId)
    {
        var response = Submit(Opcode.TxnCommit, Requests.TxnCommit(txnId));
        return KernelResponseMapper.ToCommitResult(response, lastError);
    }

    /// <summary>一步提交整批意图（等价于 开事务 + 批量提交 + 提交）。</summary>
    public TxnCommitResult ApplyBatch(IReadOnlyList<uint> keys, IReadOnlyList<TritDelta> deltas)
    {
        var response = Submit(Opcode.TxnApply, Requests.TxnApply(keys, deltas));
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
        // 特意**不**在这里做越界收敛：`ShadowRecord(byte[])` 是给调用方用的诊断入口，
        // 真机踩过的坑正是「把任意二进制字节当签名传进来」，静默收敛成一个合法 trit
        // 就会把一串垃圾记成「发现了一个分歧」。越界必须**响亮地报错**
        // （由下面的打包阶段抛 WireException → Status.Decode），与原生同口径。
        // 越界收敛只用于**线格式解码**路径（见 `Wire.TritDecoder`），那里必须与
        // 原生 `trit_from_byte` 一致，否则仲裁结果会分叉。
        var trits = Array.ConvertAll(signature, value => (TritDelta)value);
        var response = Submit(Opcode.ShadowRecord, Requests.ShadowRecord(trits));
        return KernelResponseMapper.ToU64(response, lastError);
    }

    /// <summary>同步执行扫描并把终态放入已完成队列；返回 0 表示本地编码失败。</summary>
    public ulong SubmitScan(ScanRequest request)
    {
        if (request is null)
        {
            lastError = "扫描请求不能为 null";
            return 0;
        }

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

    /// <summary>托管任务提交时已同步完成，因此不可取消。</summary>
    public int CancelJob(ulong jobId)
    {
        var response = Submit(Opcode.JobCancel, Requests.JobCancelJob(jobId));
        return KernelResponseMapper.ToCancelled(response);
    }

    /// <summary>托管任务提交时已同步完成，因此不可取消。</summary>
    public int CancelOwner(string owner)
    {
        var response = Submit(Opcode.JobCancel, Requests.JobCancelOwner(owner));
        return KernelResponseMapper.ToCancelled(response);
    }

    /// <summary>查询或切换托管 GPU 策略；托管路径始终不可用。</summary>
    public GpuStatusReport GpuStatus(GpuPolicy? policy = null, bool probe = false)
    {
        var response = Submit(Opcode.GpuStatus, Requests.GpuStatus(policy, probe, 1_048_576));
        return KernelResponseMapper.ToGpuStatus(response);
    }

    /// <summary>查询托管路径的单工作者预算快照。</summary>
    public BudgetSnapshot Budget()
    {
        var response = Submit(Opcode.Budget, Requests.Budget());
        return KernelResponseMapper.ToBudget(response);
    }

    /// <summary>查询托管任务池同构的 Prometheus 指标。</summary>
    public string MetricsPrometheus()
    {
        var response = Submit(Opcode.Metrics, Requests.Metrics());
        return KernelResponseMapper.ToPrometheus(response);
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

    /// <summary>
    /// 会话复位：清空与"这张存档"相关的一切状态，保留配置与跨存档统计。
    /// </summary>
    /// <remarks>
    /// 与原生内核 <c>0x1B SESSION_RESET</c> 同语义（见 <see cref="Opcode.SessionReset"/>）：
    /// 世界投影 / 日志 / 快照 / 事务 / 事件载荷 / 已完成任务 / tick / PRNG 全部归零，
    /// 订阅者与统计计数器保留。进出存档时各调一次。
    /// </remarks>
    public void ResetSession()
    {
        lock (scanGate)
        {
            world.Clear();
            snapshots.Clear();
            eventPayloads.Clear();
            eventPayloadTrits = 0;
            completedJobs.Clear();
            nextSnapshotId = 0;
            tick = 0;
            // 换实例而不是 Clear()：连内部容量一起丢掉，不会留下上一张存档撑出来的大数组。
            // 订阅者**不动**：订阅关系属于模组装载期，跨存档依然有效，
            // 清掉会让 WorldBridge 持有的 sub id 全部失效（派发静默停止）。
            log = new TxnLog();
            txns = new TxnTable(config.MaxOpenTxns);
            prng = new DeterministicPrng(0);
            shadow.Compared = 0;
            shadow.Divergences = 0;
            shadow.Recent.Clear();
            lastError = string.Empty;
        }
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
            Opcode.TxnSubmitBatch => OpTxnSubmitBatch(payload),
            Opcode.TxnApply => OpTxnApply(payload),
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
            Opcode.ScanSubmit => OpScanSubmit(payload),
            Opcode.JobPoll => OpJobPoll(payload),
            Opcode.JobCancel => OpJobCancel(payload),
            Opcode.GpuStatus => OpGpuStatus(payload),
            Opcode.Budget => OpBudget(),
            Opcode.Metrics => OpMetrics(),
            Opcode.SessionReset => OpSessionReset(payload),
            _ => throw new RtException(Status.Arg, $"输入非法：未知 opcode {opcode:#x}"),
        };
    }

    private byte[] OpSessionReset(byte[] payload)
    {
        if (payload.Length != 0)
        {
            throw new RtException(Status.Arg, $"输入非法：SESSION_RESET 不接受载荷，收到 {payload.Length} 字节");
        }

        ResetSession();
        return Array.Empty<byte>();
    }

    private byte[] OpScanSubmit(byte[] payload)
    {
        using var document = ParseJsonObject(payload);
        var root = document.RootElement;
        var owner = JsonString(root, "owner");
        owner = NormalizeOwner(owner);
        var request = new ScanRequest
        {
            Owner = owner,
            Kind = ParseScanKind(JsonString(root, "kind")),
            // 种类原文：失败文案要回显用户到底写了什么（枚举装不下）。
            KindText = JsonString(root, "kind"),
            Trits = JsonByteArray(root, "trits") ?? Array.Empty<byte>(),
            Pattern = JsonByteArray(root, "pattern"),
            Threshold = JsonInt32(root, "threshold"),
            Chunk = JsonInt32(root, "chunk"),
            Oneshot = JsonBoolean(root, "oneshot"),
            Priority = JsonInt32(root, "priority"),
            Top = root.TryGetProperty("top", out _) ? JsonInt32(root, "top") : 16,
        };

        ulong id;
        JobResult result;
        lock (scanGate)
        {
            nextJobId += 1;
            id = nextJobId;
            if (!modBudgets.TryGetValue(owner, out var budget))
            {
                budget = new ModBudgetAccumulator();
                modBudgets[owner] = budget;
            }

            budget.Submitted += 1;
            poolStarted += 1;
            result = ExecuteScan(id, request);
            budget.Steps += result.Steps;
            poolSteps += result.Steps;
            if (result.State == JobState.Done)
            {
                budget.Completed += 1;
                poolCompleted += 1;
            }
            else if (result.State == JobState.Cancelled)
            {
                budget.Cancelled += 1;
                poolCancelled += 1;
            }
            else
            {
                poolFailed += 1;
            }

            completedJobs.Add(result);
        }

        return JsonBytes(writer => writer.WriteNumber("job", id));
    }

    private byte[] OpJobPoll(byte[] payload)
    {
        using var document = ParseJsonObject(payload);
        var limit = JsonInt32(document.RootElement, "limit");
        // owner 过滤：字段缺省或 null = 不限归属；给了就只取该 owner 的。
        // 归一化口径必须与提交侧一致（空串 → "unknown"），否则两条路径对不上。
        var owner = JsonOptionalString(document.RootElement, "owner");
        string? wanted = owner is null ? null : NormalizeOwner(owner);

        var jobs = new List<JobResult>();
        lock (scanGate)
        {
            // **只移除本次真正返回的任务**：其他 owner 的结果仍留在队列里，
            // 否则一次按 owner 的轮询会把别人的结果一起吃掉。
            if (wanted is null)
            {
                var count = limit <= 0 ? completedJobs.Count : Math.Min(limit, completedJobs.Count);
                jobs.AddRange(completedJobs.Take(count));
                completedJobs.RemoveRange(0, count);
            }
            else
            {
                foreach (var job in completedJobs)
                {
                    if (limit > 0 && jobs.Count >= limit)
                    {
                        break;
                    }

                    if (job.Owner == wanted)
                    {
                        jobs.Add(job);
                    }
                }

                foreach (var job in jobs)
                {
                    completedJobs.Remove(job);
                }
            }
        }

        return JsonBytes(writer =>
        {
            writer.WritePropertyName("jobs");
            writer.WriteStartArray();
            foreach (var job in jobs)
            {
                writer.WriteStartObject();
                writer.WriteNumber("id", job.Id);
                writer.WriteString("owner", job.Owner);
                writer.WriteString("kind", ScanKindName(job.Kind));
                writer.WriteString("state", JobStateName(job.State));
                writer.WritePropertyName("result");
                writer.WriteRawValue(job.ResultJson, skipInputValidation: false);
                writer.WriteString("error", job.Error);
                writer.WriteNumber("steps", job.Steps);
                writer.WriteString("backend", job.Backend);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        });
    }

    private static byte[] OpJobCancel(byte[] payload)
    {
        using var _ = ParseJsonObject(payload);
        // 托管路径在 SubmitScan 返回前已同步完成；完成结果不可取消。
        return JsonBytes(writer => writer.WriteNumber("cancelled", 0));
    }

    private byte[] OpGpuStatus(byte[] payload)
    {
        using var document = ParseJsonObject(payload);
        if (document.RootElement.TryGetProperty("policy", out var policyElement)
            && policyElement.ValueKind != JsonValueKind.Null)
        {
            if (policyElement.ValueKind != JsonValueKind.String)
            {
                throw new RtException(Status.Arg, "输入非法：GPU policy 必须是 auto、off 或 on");
            }

            gpuPolicy = policyElement.GetString()?.ToLowerInvariant() switch
            {
                "auto" => GpuPolicy.Auto,
                "off" => GpuPolicy.Off,
                "on" => GpuPolicy.On,
                _ => throw new RtException(Status.Arg, "输入非法：GPU policy 必须是 auto、off 或 on"),
            };
        }

        return JsonBytes(writer =>
        {
            writer.WriteBoolean("available", false);
            writer.WriteBoolean("enabled", false);
            writer.WriteString("policy", GpuPolicyName(gpuPolicy));
            writer.WriteString("adapter", string.Empty);
            writer.WriteString("backend", string.Empty);
            writer.WriteBoolean("built_with_gpu", false);
            writer.WriteString("reason", "托管回退内核不含 GPU 路径");
            writer.WriteNumber("cpu_dot", 0);
            writer.WriteNumber("gpu_dot", 0);
            writer.WriteBoolean("matches", false);
            writer.WriteNumber("min_trits", 1_048_576);
            writer.WriteNumber("ns_per_trit", 0);
            writer.WriteNumber("jobs", poolStarted);
            writer.WriteNumber("gpu_jobs", 0);
            writer.WriteNumber("fallbacks", 0);
        });
    }

    private byte[] OpBudget()
    {
        lock (scanGate)
        {
            var submitted = 0UL;
            var completed = 0UL;
            var cancelled = 0UL;
            var rejected = 0UL;
            var steps = 0UL;
            return JsonBytes(writer =>
            {
                writer.WriteNumber("workers", 1);
                writer.WriteNumber("cpu_capacity", 1);
                writer.WriteNumber("memory_capacity", 0);
                writer.WriteNumber("per_mod_inflight", 1);
                writer.WriteNumber("inflight", 0);
                writer.WriteNumber("queued", 0);
                writer.WritePropertyName("mods");
                writer.WriteStartArray();
                foreach (var (owner, budget) in modBudgets.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                {
                    writer.WriteStartObject();
                    writer.WriteString("owner", owner);
                    writer.WriteNumber("inflight", 0);
                    writer.WriteNumber("queued", 0);
                    writer.WriteNumber("submitted", budget.Submitted);
                    writer.WriteNumber("completed", budget.Completed);
                    writer.WriteNumber("cancelled", budget.Cancelled);
                    writer.WriteNumber("rejected", budget.Rejected);
                    writer.WriteNumber("steps", budget.Steps);
                    writer.WriteEndObject();
                    submitted += budget.Submitted;
                    completed += budget.Completed;
                    cancelled += budget.Cancelled;
                    rejected += budget.Rejected;
                    steps += budget.Steps;
                }

                writer.WriteEndArray();
                writer.WritePropertyName("totals");
                writer.WriteStartObject();
                writer.WriteNumber("submitted", submitted);
                writer.WriteNumber("completed", completed);
                writer.WriteNumber("cancelled", cancelled);
                writer.WriteNumber("rejected", rejected);
                writer.WriteNumber("steps", steps);
                writer.WriteEndObject();
                writer.WriteNull("last_metrics");
            });
        }
    }

    private byte[] OpMetrics()
    {
        string prometheus;
        lock (scanGate)
        {
            prometheus = poolStarted == 0
                ? string.Empty
                : $"trit_schedule_started {poolStarted}\n"
                  + $"trit_schedule_completed {poolCompleted}\n"
                  + $"trit_schedule_failed {poolFailed}\n"
                  + $"trit_schedule_cancelled {poolCancelled}\n"
                  + $"trit_schedule_steps {poolSteps}\n";
        }

        return JsonBytes(writer => writer.WriteString("prometheus", prometheus));
    }

    /// <summary>
    /// 执行一次扫描；请求非法时**产出失败记录**而不是抛异常。
    /// </summary>
    /// <remarks>
    /// **校验顺序与错误文案必须与 Rust 侧 <c>ScanRequest::validate</c> 逐字一致**：
    /// ①种类未知 → ②trit 值非法 → ③缺 pattern。
    /// 顺序不同、文案不同，同一份坏请求在两条路径上就会报出不同的原因，
    /// 影子差分（逐字节比对响应体）立刻炸。
    /// </remarks>
    private static JobResult ExecuteScan(ulong id, ScanRequest request)
    {
        var trits = request.Trits ?? Array.Empty<byte>();
        var pattern = request.Pattern ?? Array.Empty<byte>();

        // ① 种类未知：回显用户传入的原文（去首尾空白）。
        if (request.Kind == ScanKind.Unknown)
        {
            return FailedJob(id, request, $"未知扫描 kind：{request.KindText.Trim()}");
        }

        // ② trit 值非法：先看 trits，再看 pattern（与原生侧同序）。
        var badTrit = trits
            .Concat(pattern)
            .Select(value => (int?)value)
            .FirstOrDefault(value => value > 2);
        if (badTrit is not null)
        {
            return FailedJob(id, request, $"非法 trit 值：{badTrit}，每个元素只能是 0、1 或 2");
        }

        // ③ pattern / dot 必须有第二个向量。
        if (request.Kind is ScanKind.Pattern or ScanKind.Dot && pattern.Length == 0)
        {
            return FailedJob(id, request, $"{ScanKindName(request.Kind)} 扫描需要非空 pattern");
        }

        try
        {
            var (resultJson, work) = request.Kind switch
            {
                ScanKind.Histogram => HistogramJson(trits),
                ScanKind.Pattern => PatternJson(trits, pattern, request.Threshold),
                ScanKind.Density => DensityJson(trits, request.Chunk, request.Top),
                ScanKind.Dot => DotJson(trits, pattern),
                _ => ("{}", 0L),
            };
            var chunk = ClampChunk(request.Chunk);
            // 步数口径必须与原生侧一致：**成功的任务至少 1 步**。
            // 原生侧即使输入为空也会执行一次 step（把空结果算出来），
            // 而按 work 算出来的托管侧步数会是 0——两条路径对同一份请求
            // 报出不同的步数，影子差分立刻炸。
            var steps = request.Oneshot
                ? 1UL
                : (ulong)Math.Max(1L, (work + chunk - 1) / chunk);
            return new JobResult(id, request.Owner, request.Kind, JobState.Done, resultJson, string.Empty, steps, "cpu");
        }
        catch (Exception error)
        {
            return FailedJob(id, request, $"扫描失败：{error.Message}");
        }
    }

    private static JobResult FailedJob(ulong id, ScanRequest request, string error) =>
        new(id, request.Owner, request.Kind, JobState.Failed, "{}", error, 0, string.Empty);

    private static (string Json, long Work) HistogramJson(byte[] trits)
    {
        var neg = 0L;
        var zero = 0L;
        var pos = 0L;
        foreach (var trit in trits)
        {
            switch (trit)
            {
                case 0:
                    neg += 1;
                    break;
                case 1:
                    zero += 1;
                    break;
                case 2:
                    pos += 1;
                    break;
            }
        }

        return (JsonText(writer =>
        {
            writer.WritePropertyName("histogram");
            writer.WriteStartObject();
            writer.WriteNumber("neg", neg);
            writer.WriteNumber("zero", zero);
            writer.WriteNumber("pos", pos);
            writer.WriteEndObject();
        }), trits.LongLength);
    }

    private static (string Json, long Work) PatternJson(byte[] trits, byte[] pattern, int threshold)
    {
        var matches = new List<(long Index, long Score)>();
        var scanned = pattern.Length == 0 || pattern.Length > trits.Length
            ? 0
            : trits.Length - pattern.Length + 1;

        // 满分 = 图案里**非零位置**的个数：三值点积里 Zero 贡献 0 分，
        // 所以「逐元素全等」的实际得分就是非零位置的个数。
        //
        // 必须与 Rust 侧 `ScanJob::new` 的 `full_score` 逐字对齐，
        // 否则含 Zero 的 pattern 在两条路径上的满分不同：
        // 托管侧按 pattern.Length 算就会**永远命中不了**（要求 3 分，满分只有 2）。
        var fullScore = 0;
        foreach (var value in pattern)
        {
            if (TritValue(value) != 0)
            {
                fullScore++;
            }
        }

        var required = threshold <= 0 ? fullScore : threshold;
        for (var index = 0; index < scanned; index++)
        {
            var score = 0L;
            for (var offset = 0; offset < pattern.Length; offset++)
            {
                score += TritValue(trits[index + offset]) * TritValue(pattern[offset]);
            }

            if (score >= required)
            {
                matches.Add((index, score));
            }
        }

        return (JsonText(writer =>
        {
            writer.WritePropertyName("matches");
            writer.WriteStartArray();
            foreach (var (index, score) in matches)
            {
                writer.WriteStartObject();
                writer.WriteNumber("index", index);
                writer.WriteNumber("score", score);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteNumber("scanned", scanned);
            writer.WriteString("backend", "cpu");
        }), scanned);
    }

    private static (string Json, long Work) DensityJson(byte[] trits, int requestedChunk, int requestedTop)
    {
        var chunk = ClampChunk(requestedChunk);
        var top = requestedTop > 0 ? requestedTop : 16;
        var blocks = new List<(long Block, long Index, long NonZero, double Density)>();
        for (var index = 0; index < trits.Length; index += chunk)
        {
            var length = Math.Min(chunk, trits.Length - index);
            var nonZero = 0L;
            for (var offset = 0; offset < length; offset++)
            {
                if (trits[index + offset] != 1)
                {
                    nonZero += 1;
                }
            }

            var block = index / chunk;
            // 密度口径必须与 Rust 侧**逐位一致**：
            // 先放大 1e6 → 远离零四舍五入 → 再除回 1e6。
            // 不能用 Math.Round(x, 6)：那是「按十进制位四舍五入」，
            // 与「先放大再四舍五入」在二进制边界上会差 1 个 ULP，
            // 写出来的定点文本就差最后一位。
            var scaled = Math.Round(nonZero / (double)length * 1e6, MidpointRounding.AwayFromZero);
            var density = scaled / 1e6;
            blocks.Add((block, index, nonZero, density));
        }

        var selected = blocks
            .OrderByDescending(block => block.Density)
            .ThenBy(block => block.Block)
            .Take(top)
            .ToArray();
        return (JsonText(writer =>
        {
            writer.WritePropertyName("blocks");
            writer.WriteStartArray();
            foreach (var block in selected)
            {
                writer.WriteStartObject();
                writer.WriteNumber("block", block.Block);
                writer.WriteNumber("index", block.Index);
                writer.WriteNumber("nonzero", block.NonZero);
                writer.WritePropertyName("density");
                writer.WriteRawValue(
                    Math.Round(block.Density, 6).ToString("0.000000", CultureInfo.InvariantCulture),
                    skipInputValidation: true);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteNumber("blocks_total", blocks.Count);
        }), trits.LongLength);
    }

    private static (string Json, long Work) DotJson(byte[] trits, byte[] pattern)
    {
        var length = Math.Min(trits.Length, pattern.Length);
        var dot = 0L;
        for (var index = 0; index < length; index++)
        {
            dot += TritValue(trits[index]) * TritValue(pattern[index]);
        }

        return (JsonText(writer =>
        {
            writer.WriteNumber("dot", dot);
            writer.WriteNumber("length", length);
        }), length);
    }

    private static int TritValue(byte trit) => trit - 1;

    private static JsonDocument ParseJsonObject(byte[] payload)
    {
        var document = JsonDocument.Parse(payload.Length == 0 ? "{}" : Encoding.UTF8.GetString(payload));
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            document.Dispose();
            throw new WireException("JSON 载荷必须是对象");
        }

        return document;
    }

    private static byte[]? JsonByteArray(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var array) || array.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (array.ValueKind != JsonValueKind.Array)
        {
            throw new WireException($"JSON 字段 {name} 必须是数组");
        }

        var values = new byte[array.GetArrayLength()];
        var index = 0;
        foreach (var element in array.EnumerateArray())
        {
            values[index++] = element.ValueKind == JsonValueKind.Number
                              && element.TryGetInt32(out var value)
                              && value is >= byte.MinValue and <= byte.MaxValue
                ? (byte)value
                : byte.MaxValue;
        }

        return values;
    }

    private static string JsonString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    /// <summary>
    /// 读取可选字符串：<b>区分「缺省 / null」与「空串」</b>。
    /// </summary>
    /// <remarks>
    /// <see cref="JsonString"/> 把三者（字段不存在、值为 null、值为 ""）都压成空串，
    /// 而 owner 过滤必须区分：<b>缺省 = 不限归属</b>，<b>空串 = 过滤 unknown</b>。
    /// 这两件事语义相反，压平了就会把「取全部」变成「只取 unknown」。
    /// </remarks>
    private static string? JsonOptionalString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value)
            ? value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? string.Empty
                : value.ValueKind == JsonValueKind.Null
                    ? null
                    : string.Empty
            : null;

    /// <summary>
    /// 归属名归一化：**去首尾空白 → 空则归为 <c>unknown</c>**。
    /// </summary>
    /// <remarks>
    /// 复用 <see cref="Requests.NormalizeOwner"/>（与 Rust 侧 <c>normalize_owner</c> 逐字对齐）：
    /// 提交侧记账、轮询侧过滤、托管回退内核三处必须共用同一规则，
    /// 否则同一批任务在两条路径上会落到两个不同的 owner 上，影子对拍一直报不一致。
    /// </remarks>
    private static string NormalizeOwner(string owner) => Requests.NormalizeOwner(owner);

    /// <summary>
    /// 单块扫描粒度的上限（与 Rust 侧 <c>MAX_SCAN_CHUNK</c> 同值）。
    /// </summary>
    /// <remarks>
    /// 超大 chunk 会让「步数」的计算与原生侧对不上（原生夹到上限，托管不夹），
    /// 影子差分会一直报假分歧。
    /// </remarks>
    private const int MaxScanChunk = 1 << 20;

    /// <summary>
    /// 把请求里的 <c>chunk</c> 夹到合法范围：<c>&lt;= 0</c> 用默认粒度，超限夹到上限。
    /// </summary>
    /// <remarks>
    /// 必须与 Rust 侧 <c>ScanJob::new</c> 的夹取规则<b>逐项对齐</b>。
    /// </remarks>
    private static int ClampChunk(int requestedChunk) =>
        requestedChunk <= 0
            ? 4096
            : Math.Min(requestedChunk, MaxScanChunk);

    private static int JsonInt32(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var result)
            ? result
            : 0;

    private static bool JsonBoolean(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.True;

    /// <summary>
    /// 解析种类名；无法识别时给 <see cref="ScanKind.Unknown"/>。
    /// </summary>
    /// <remarks>
    /// 必须与 Rust 侧 <c>ScanKind::parse</c> 逐字对齐：
    /// <b>先去首尾空白</b>再比小写（否则 <c>" histogram "</c> 在托管侧解析失败、
    /// 在原生侧解析成功），未知种类一律收敛到 <see cref="ScanKind.Unknown"/>
    /// （不再用 <c>(ScanKind)(-1)</c> 这种没有名字的魔数）。
    /// </remarks>
    private static ScanKind ParseScanKind(string name) => name.Trim().ToLowerInvariant() switch
    {
        "histogram" => ScanKind.Histogram,
        "pattern" => ScanKind.Pattern,
        "density" => ScanKind.Density,
        "dot" => ScanKind.Dot,
        _ => ScanKind.Unknown,
    };

    /// <summary>
    /// 种类 → JSON 契约里的规范名（未知种类给 <c>"unknown"</c>）。
    /// </summary>
    /// <remarks>
    /// 复用 <see cref="Requests.ScanKindName"/>：这份映射以前在两个文件里各写一遍，
    /// 改一处漏一处就会让托管侧的 <c>kind</c> 字段与原生侧分叉。
    /// </remarks>
    private static string ScanKindName(ScanKind kind) => Requests.ScanKindName(kind);

    private static string JobStateName(JobState state) => state switch
    {
        JobState.Done => "done",
        JobState.Cancelled => "cancelled",
        _ => "failed",
    };

    private static string GpuPolicyName(GpuPolicy policy) => policy switch
    {
        GpuPolicy.Off => "off",
        GpuPolicy.On => "on",
        _ => "auto",
    };

    /// <summary>
    /// JSON 响应的编码器：**不转义非 ASCII 字符**。
    /// </summary>
    /// <remarks>
    /// <see cref="Utf8JsonWriter"/> 默认用 <c>JavaScriptEncoder.Default</c>，
    /// 会把中文写成 <c>é</c> 这样的转义序列；而 Rust 侧的 serde_json
    /// 输出的是**原始 UTF-8**。两者对同一份中文错误文案产出的字节完全不同，
    /// 影子差分（逐字节比对）会永远报分歧——而且分歧点全在转义上，
    /// 肉眼看两边「意思一样」，极难定位。
    /// <para>
    /// 这里用 <c>UnsafeRelaxedJsonEscaping</c>：只转义 JSON 规范要求转义的
    /// 字符（引号、反斜杠、控制字符），非 ASCII 原样输出，与 serde_json 对齐。
    /// 载荷只在本进程与原生库之间流转，不进 HTML， relaxed 转义没有注入面。
    /// </para>
    /// </remarks>
    private static readonly System.Text.Encodings.Web.JavaScriptEncoder JsonEncoder =
        System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping;

    private static byte[] JsonBytes(Action<Utf8JsonWriter> writeProperties)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = JsonEncoder }))
        {
            writer.WriteStartObject();
            writeProperties(writer);
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    private static string JsonText(Action<Utf8JsonWriter> writeProperties) =>
        Encoding.UTF8.GetString(JsonBytes(writeProperties));

    private byte[] OpSubscribe(byte[] payload)
    {
        var reader = new WireReader(payload);
        var modId = reader.U32();
        var eventId = reader.U32();
        var priority = reader.I32();
        var depCount = reader.Count32();
        // 与原生 op_subscribe 同口径：声明多少条就读多少条，载荷不够报 Decode。
        // 旧写法把上限夹成 4096 后照常返回成功——「声明 4097 条依赖、载荷只给 4096 条」
        // 会在托管侧静默建成一张少了边的订阅图，原生侧则报 Decode：状态码与图都分叉。
        // 先按剩余字节夹一道，避免拿 int.MaxValue 去跑循环（同样报 Decode）。
        const int headerBytes = 16;
        var available = Math.Max(0, (payload.Length - headerBytes) / sizeof(uint));
        if (depCount > available)
        {
            throw new WireException(
                $"订阅载荷声明 {depCount} 条依赖，实际只有 {available} 条的空间");
        }

        var deps = new uint[depCount];
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

        // 覆盖同 id 的旧载荷前必须先扣掉旧长度：只加不减会让计数器虚增
        // （同一 id 发布 N 次就报 N×size），而它正是字节闸门的判据——虚增会让淘汰
        // 误判超预算，把**别人**的载荷当受害者提前淘汰，表现为读别人的事件时拿到空数组。
        // 这是静默丢数据，比内存涨更难查。
        if (eventPayloads.TryGetValue(eventId, out var previous))
        {
            eventPayloadTrits -= previous.Length;
        }

        eventPayloadTrits += trits.Length;
        eventPayloads[eventId] = trits;
        EvictEventPayloads(eventId);

        var dirty = router.Publish(eventId);
        return new WireWriter().U32((uint)dirty).ToArray();
    }

    /// <summary>
    /// 按条数 + trit 总数双预算淘汰最旧的事件载荷，**绝不淘汰刚发布的那条**；
    /// 与原生 <c>evict_event_payloads</c> 逐行为一致。
    /// </summary>
    /// <remarks>
    /// 刚发布的那条若自身就超预算，就留它一个：淘汰它等于让 <c>publish</c> 什么都不做，
    /// 调用方会拿到「成功」但监听者读不到载荷，这种静默失败比拒绝更难查。
    /// 事件 ID 递增是既有假设（SortedDictionary 序即 ID 序），所以最旧的恒为最小键。
    /// </remarks>
    private void EvictEventPayloads(uint keep)
    {
        while (true)
        {
            bool overCount = eventPayloads.Count > MaxEventPayloads;
            bool overBytes = eventPayloadTrits > MaxEventPayloadTrits;
            if (!overCount && !overBytes)
            {
                return;
            }

            // 取最旧的一个键；若它就是刚发布的那条，退一步取第二旧的。
            uint victim;
            using (var keys = eventPayloads.Keys.GetEnumerator())
            {
                if (!keys.MoveNext())
                {
                    return;
                }

                uint first = keys.Current;
                if (first != keep)
                {
                    victim = first;
                }
                else if (keys.MoveNext())
                {
                    victim = keys.Current;
                }
                else
                {
                    // 只剩刚发布的那条：预算再超也只能留着（见上面的取舍说明）。
                    return;
                }
            }

            if (eventPayloads.Remove(victim, out var dropped))
            {
                eventPayloadTrits -= dropped.Length;
            }
        }
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
        var delta = TritDecoder.FromByte(reader.U8());
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

    private byte[] OpTxnSubmitBatch(byte[] payload)
    {
        var reader = new WireReader(payload);
        var txnId = reader.U64();
        var count = reader.U32();
        GuardBatchCount(count, payload.Length, headerBytes: 12);
        var intents = new WriteIntent[count];
        for (int i = 0; i < count; i++)
        {
            intents[i] = new WriteIntent(txnId, reader.U32(), TritDecoder.FromByte(reader.U8()));
        }

        if (intents.Length == 0)
        {
            return Array.Empty<byte>();
        }

        if (txnId == 0)
        {
            // 自动提交：整批一次仲裁 + 一次落地（与逐条 submit 等价）。
            var (applied, _) = CommitIntents(intents, 0);
            stats.IntentsApplied += applied;
            return Array.Empty<byte>();
        }

        foreach (var intent in intents)
        {
            txns.PushIntent(txnId, intent);
        }

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

    /// <summary>一步提交：读一批无事务号的意图，一次仲裁 + 一次落地。</summary>
    private byte[] OpTxnApply(byte[] payload)
    {
        var reader = new WireReader(payload);
        var count = reader.U32();
        GuardBatchCount(count, payload.Length, headerBytes: 4);
        var intents = new WriteIntent[count];
        for (int i = 0; i < count; i++)
        {
            intents[i] = new WriteIntent(0, reader.U32(), TritDecoder.FromByte(reader.U8()));
        }

        if (intents.Length == 0)
        {
            return new WireWriter().U32(0).U64(log.Seq).ToArray();
        }

        var (applied, logSeq) = CommitIntents(intents, 0);
        stats.TxnsCommitted += 1;
        stats.IntentsApplied += applied;
        return new WireWriter().U32(applied).U64(logSeq).ToArray();
    }

    /// <summary>
    /// 批量载荷的条数闸门：<c>count</c> 是载荷里的 u32，坏载荷可以是 0xFFFF_FFFF。
    /// </summary>
    /// <remarks>
    /// 原生内核是边读边解、读不动就报 <c>TT_ERR_DECODE</c>，从不为 count 预先分配；
    /// 托管这边如果直接 <c>new WriteIntent[count]</c>，一条 20 字节的坏载荷就能让进程
    /// 去申请几十 GB。所以先按剩余字节数夹一道——而且必须报与原生**同一个**状态码
    /// （<see cref="Status.Decode"/>），否则"托管回退"不是回退，而是换了一套故障表现。
    /// </remarks>
    private static void GuardBatchCount(uint count, int payloadLength, int headerBytes)
    {
        long available = (payloadLength - headerBytes) / 5;
        if (available < 0)
        {
            available = 0;
        }

        if (count > (uint)available)
        {
            throw new RtException(
                Status.Decode,
                $"批量提交条数 {count} 超出载荷长度（最多 {available} 条）");
        }
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
        var count = reader.Count32();
        var intents = new List<WriteIntent>(Math.Min(count, 4096));
        for (var i = 0; i < count; i++)
        {
            intents.Add(new WriteIntent(reader.U64(), reader.U32(), TritDecoder.FromByte(reader.U8())));
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
        var count = reader.Count32();
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
                | TritRuntime.Wire.CapabilityBits.Shadow
                | TritRuntime.Wire.CapabilityBits.Budget);

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
