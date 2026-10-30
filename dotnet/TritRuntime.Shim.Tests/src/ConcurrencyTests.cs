#nullable enable

using System.Collections.Concurrent;
using TritRuntime.Kernel;
using Xunit;

namespace TritRuntime.Shim.Tests;

/// <summary>
/// 托管内核的并发契约测试。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么只测扫描路径</b>：<see cref="ManagedKernel"/> 只有扫描这一段
/// （<c>SubmitScan</c> / <c>PollJobs</c> / <c>Budget</c> / <c>MetricsPrometheus</c> /
/// <c>ResetSession</c>）在 <c>scanGate</c> 锁内；世界投影、事务日志、快照、事件等
/// 其余状态都是<b>无锁</b>的（见根 README「已知未修」表）。给无锁状态做多线程
/// 断言会 flaky——它本来就不承诺线程安全，游戏主线程串行调用是设计前提。
/// 这里钉死的是<b>被显式承诺为线程安全的那条路径</b>。
/// </para>
/// <para>
/// <b>为什么提交后能立刻 poll</b>：托管扫描在 <c>SubmitScan</c> 返回前就在锁内
/// 同步跑完并入队（<c>ExecuteScan</c> → <c>completedJobs.Add</c>），不像原生内核是
/// 线程池异步完成。所以并发提交全部 join 之后，一次 poll 就能拿全，无需等待。
/// </para>
/// <para>
/// <b>为什么用 <c>TaskCompletionSource</c> 起跑门而不是 <c>Barrier</c></b>：
/// <c>Barrier.SignalAndWait</c> 会阻塞线程池线程，在 2 核 CI runner 上八方栅栏可能
/// 把线程池占满、靠注入线程才解锁，甚至死锁。改用 async worker <c>await</c> 一个
/// TCS 门：worker 等门时释放线程，测试线程 <c>SetResult</c> 唤醒全部，既不饿死线程池
/// 也拿得到真实并发交错；测试主体全程 <c>await</c>，顺带消掉 xUnit1031 阻塞告警。
/// </para>
/// </remarks>
public sealed class ConcurrencyTests
{
    private const int Threads = 8;
    private const int PerThread = 24;

    /// <summary>创建一个 async 起跑门；worker 先 await，测试线程再一次性放行。</summary>
    private static TaskCompletionSource Gate() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// 并发提交扫描：<c>nextJobId</c> 在锁内自增，所有任务 id 必须唯一且非 0，
    /// 且全部 join 后能一次性取回 Thread×PerThread 条，不丢不重。
    /// </summary>
    [Fact]
    public async Task ConcurrentScanSubmissionAssignsUniqueIdsAndKeepsEveryResult()
    {
        using var kernel = new ManagedKernel();
        var ids = new ConcurrentBag<ulong>();
        var gate = Gate();

        var workers = new Task[Threads];
        for (var t = 0; t < Threads; t++)
        {
            var worker = t;
            workers[t] = Task.Run(async () =>
            {
                await gate.Task;
                for (var i = 0; i < PerThread; i++)
                {
                    var id = kernel.SubmitScan(new ScanRequest
                    {
                        Owner = $"mod-{worker}",
                        Kind = ScanKind.Histogram,
                        // 每个任务的三值数组都不同，确保 ExecuteScan 真的执行，
                        // 而不是命中某种常量折叠/缓存。
                        Trits = new byte[] { 0, (byte)(i % 3), (byte)(worker % 3), 2 },
                    });
                    Assert.True(id > 0, "并发提交不应返回失败 id 0");
                    ids.Add(id);
                }
            });
        }

        gate.SetResult();
        await Task.WhenAll(workers);

        // id 全局唯一：scanGate 保护 nextJobId 自增，并发也不能撞号。
        var idList = ids.ToArray();
        Assert.Equal(Threads * PerThread, idList.Length);
        Assert.Equal(idList.Length, idList.Distinct().Count());

        // 托管扫描同步完成，全部 join 后一次 poll 即可取全。
        var polled = kernel.PollJobs();
        Assert.Equal(Threads * PerThread, polled.Count);
        Assert.Equal(polled.Count, polled.Select(job => job.Id).Distinct().Count());
        Assert.Equal(
            idList.OrderBy(x => x),
            polled.Select(job => job.Id).OrderBy(x => x));

        // 取出即出队：再 poll 一次应为空。
        Assert.Empty(kernel.PollJobs());
    }

    /// <summary>
    /// 并发按 owner 轮询：整段「窥视 → 取出」在锁内，同一个任务只能被一个线程取走，
    /// 反复并发 poll 的总量恰好等于提交量，绝不重复、绝不丢失。
    /// </summary>
    [Fact]
    public async Task ConcurrentPollingNeverDoubleDeliversAResult()
    {
        using var kernel = new ManagedKernel();
        const int total = 120;
        for (var i = 0; i < total; i++)
        {
            var id = kernel.SubmitScan(new ScanRequest
            {
                Owner = "shared",
                Kind = ScanKind.Histogram,
                Trits = new byte[] { 0, (byte)(i % 3), 2 },
            });
            Assert.True(id > 0);
        }

        var collected = new ConcurrentBag<ulong>();
        var gate = Gate();
        var pollers = new Task[Threads];
        for (var t = 0; t < Threads; t++)
        {
            pollers[t] = Task.Run(async () =>
            {
                await gate.Task;
                // 每个线程反复 poll 直到队列被抽空；limit 取小值放大竞争窗口。
                while (true)
                {
                    var batch = kernel.PollJobs(limit: 3, owner: "shared");
                    if (batch.Count == 0)
                    {
                        break;
                    }

                    foreach (var job in batch)
                    {
                        collected.Add(job.Id);
                    }

                    await Task.Yield();
                }
            });
        }

        gate.SetResult();
        await Task.WhenAll(pollers);

        var ids = collected.ToArray();
        Assert.Equal(total, ids.Length);
        // 决定性断言：每条任务恰好被交付一次——并发 poll 不能重复消费。
        Assert.Equal(ids.Length, ids.Distinct().Count());
        Assert.Empty(kernel.PollJobs());
    }

    /// <summary>
    /// 不同 owner 的结果在并发提交 + 并发按 owner 轮询下互不串味：
    /// 每个 owner 只取回自己的那批任务。
    /// </summary>
    [Fact]
    public async Task ConcurrentOwnerIsolationHoldsUnderParallelSubmitAndPoll()
    {
        using var kernel = new ManagedKernel();
        const string ownerA = "alpha";
        const string ownerB = "beta";

        var idsA = new ConcurrentBag<ulong>();
        var idsB = new ConcurrentBag<ulong>();
        var gate = Gate();

        var submitters = new Task[Threads];
        for (var t = 0; t < Threads; t++)
        {
            var worker = t;
            submitters[t] = Task.Run(async () =>
            {
                await gate.Task;
                var owner = worker % 2 == 0 ? ownerA : ownerB;
                var sink = worker % 2 == 0 ? idsA : idsB;
                for (var i = 0; i < PerThread; i++)
                {
                    sink.Add(kernel.SubmitScan(new ScanRequest
                    {
                        Owner = owner,
                        Kind = ScanKind.Histogram,
                        Trits = new byte[] { (byte)(worker % 3), 1, 2 },
                    }));
                }
            });
        }

        gate.SetResult();
        await Task.WhenAll(submitters);

        // 两个线程并发、各自只 poll 自己的 owner，不能拿到对方的结果。
        var polledA = new List<JobResult>();
        var polledB = new List<JobResult>();
        await Task.WhenAll(
            Task.Run(() => polledA.AddRange(kernel.PollJobs(owner: ownerA))),
            Task.Run(() => polledB.AddRange(kernel.PollJobs(owner: ownerB))))
            ;

        Assert.Equal(idsA.Count, polledA.Count);
        Assert.Equal(idsB.Count, polledB.Count);
        Assert.All(polledA, job => Assert.Equal(ownerA, job.Owner));
        Assert.All(polledB, job => Assert.Equal(ownerB, job.Owner));
        Assert.Empty(polledA.Select(j => j.Id).Intersect(polledB.Select(j => j.Id)));
        Assert.Empty(kernel.PollJobs());
    }

    /// <summary>
    /// 并发读取预算 / 指标快照：两者都在 scanGate 内累加计数，
    /// 提交全部完成后读到的 Submitted 总量必须等于实际提交量，且并发读不抛异常。
    /// </summary>
    [Fact]
    public async Task ConcurrentBudgetAndMetricsReadsAreStableAfterSubmission()
    {
        using var kernel = new ManagedKernel();
        const int total = Threads * PerThread;
        var gate = Gate();
        var submitters = new Task[Threads];
        for (var t = 0; t < Threads; t++)
        {
            submitters[t] = Task.Run(async () =>
            {
                await gate.Task;
                for (var i = 0; i < PerThread; i++)
                {
                    Assert.True(kernel.SubmitScan(new ScanRequest
                    {
                        Owner = "budget",
                        Kind = ScanKind.Histogram,
                        Trits = new byte[] { 0, 1, 2 },
                    }) > 0);
                }
            });
        }

        gate.SetResult();
        await Task.WhenAll(submitters);

        // 并发读快照：不应抛集合被修改之类的异常。
        var readers = new Task[Threads];
        for (var t = 0; t < Threads; t++)
        {
            readers[t] = Task.Run(() =>
            {
                var budget = kernel.Budget();
                Assert.Equal((ulong)total, budget.Totals.Submitted);
                var metrics = kernel.MetricsPrometheus();
                Assert.False(string.IsNullOrWhiteSpace(metrics));
            });
        }
        await Task.WhenAll(readers);
    }

    /// <summary>
    /// 并发 <c>ResetSession</c>：本身也在 scanGate 内，与并发提交交错时
    /// 结果与 id 依然唯一、不抛异常；复位后队列清空但账本按设计保留。
    /// </summary>
    [Fact]
    public async Task ConcurrentResetSessionDoesNotCorruptIdsOrThrow()
    {
        using var kernel = new ManagedKernel();
        var ids = new ConcurrentBag<ulong>();
        var gate = Gate();

        var submitters = new Task[Threads];
        for (var t = 0; t < Threads; t++)
        {
            var worker = t;
            submitters[t] = Task.Run(async () =>
            {
                await gate.Task;
                for (var i = 0; i < PerThread; i++)
                {
                    var id = kernel.SubmitScan(new ScanRequest
                    {
                        Owner = $"reset-{worker}",
                        Kind = ScanKind.Histogram,
                        Trits = new byte[] { 0, 1, 2 },
                    });
                    Assert.True(id > 0);
                    ids.Add(id);
                }
            });
        }

        var resetter = Task.Run(async () =>
        {
            await gate.Task;
            // 与提交交错地反复复位，压出潜在的锁竞争 / 集合被换掉的问题。
            for (var i = 0; i < 40; i++)
            {
                kernel.ResetSession();
                await Task.Yield();
            }
        });

        gate.SetResult();
        await Task.WhenAll(submitters.Append(resetter));

        // 无论复位发生在什么时刻，id 分配都不会撞号（nextJobId 只在锁内自增，
        // 且 ResetSession 不重置它——保证 id 单调唯一）。
        var idList = ids.ToArray();
        Assert.Equal(Threads * PerThread, idList.Length);
        Assert.Equal(idList.Length, idList.Distinct().Count());

        // 收尾再复位一次：结果队列被清空（completedJobs.Clear()），但提交计数
        // 是**跨存档保留**的统计（modBudgets 不在 ResetSession 清理之列，见根 README
        // 「已知未修」表与 OpBudget 实现），所以 Submitted 仍是累计的 total，
        // 不会归零。这条同时钉死「复位只清世界态、不清账本」这个易被误解的语义。
        kernel.ResetSession();
        Assert.Empty(kernel.PollJobs());
        Assert.Equal((ulong)(Threads * PerThread), kernel.Budget().Totals.Submitted);
    }
}
