#nullable enable

using System;
using System.Text;

namespace TritRuntime.Kernel;

/// <summary>装载期自检结果。</summary>
/// <param name="Passed">闭环是否全部走通。</param>
/// <param name="Message">人类可读的逐步报告（成功与失败都带环节明细）。</param>
public readonly record struct SelfCheckReport(bool Passed, string Message);

/// <summary>
/// 端到端自检：在宿主进程里跑一遍"事务 → 快照 → 差分 → 回滚"闭环。
/// </summary>
/// <remarks>
/// <para>
/// 单元测试跑在测试宿主里，证明不了"目标进程里内核真的活着"——原生 DLL 可能被安全软件拦截、
/// ABI 可能不符、托管回退也可能被宿主裁剪。所以嵌入方（例如 tModLoader 模组）在装配后
/// 应该调一次本方法，把结果写日志。
/// </para>
/// <para>
/// 探针车道 <see cref="ProbeLane"/> 的 kind=0xF 与调用方约定的种类空间（通常 1..3）不相交；
/// 闭环最后回滚到基线快照，世界投影恢复原状。事务日志是只追加设计，
/// 自检条目会留在日志里——调用方应把这一点写进文档。
/// </para>
/// </remarks>
public static class RuntimeSelfCheck
{
    /// <summary>自检专用探针车道（高位种类 0xF）。</summary>
    public const uint ProbeLane = 0xF0FF_0FFF;

    /// <summary>跑完整闭环。</summary>
    /// <param name="shim">已装配的运行时门面。</param>
    public static SelfCheckReport Run(RuntimeShim shim)
    {
        ArgumentNullException.ThrowIfNull(shim);

        var report = new StringBuilder(256);
        ulong baseline = 0;
        bool hasBaseline = false;

        try
        {
            baseline = shim.Snapshot();
            hasBaseline = true;
            report.Append("snapshot=").Append(baseline).Append(' ');

            ulong txn = shim.BeginTransaction();
            shim.SubmitIntent(txn, ProbeLane, Wire.TritDelta.Pos);
            TxnCommitResult commit = shim.Commit(txn);
            report.Append("commit(applied=").Append(commit.Applied)
                  .Append(",seq=").Append(commit.LogSeq).Append(") ");

            if (commit.Applied != 1)
            {
                report.Append("[FAIL 提交未落地 1 条]");
                return new SelfCheckReport(false, report.ToString());
            }

            StepResult step = shim.Step(ulong.MaxValue);
            report.Append("step(intents=").Append(step.Intents.Count).Append(") ");

            ulong after = shim.Snapshot();
            report.Append("snapshot2=").Append(after).Append(' ');

            DiffResult diff = shim.Diff(baseline, after);
            bool foundProbe = false;
            for (int i = 0; i < diff.Entries.Count; i++)
            {
                if (diff.Entries[i].Key == ProbeLane)
                {
                    foundProbe = true;
                    report.Append("diff(0x").Append(ProbeLane.ToString("X8"))
                          .Append('=').Append(diff.Entries[i].New).Append(") ");
                    break;
                }
            }

            if (!foundProbe)
            {
                report.Append("[FAIL 差分未包含探针车道，entries=").Append(diff.Entries.Count).Append("]");
                return new SelfCheckReport(false, report.ToString());
            }

            shim.RollbackTo(baseline);
            report.Append("rollback=ok ");

            Wire.TritDelta[] random = shim.NextRandom(4);
            report.Append("prng=[").Append(string.Join(",", random)).Append("] ");

            byte[] bytes = shim.SnapshotBytes();
            report.Append("snapshot_bytes=").Append(bytes.Length).Append(' ');

            ulong[] stats = shim.Stats();
            report.Append("stats=").Append(stats.Length).Append(' ');

            report.Append("=> PASS");
            return new SelfCheckReport(true, report.ToString());
        }
        catch (Exception ex)
        {
            report.Append("=> FAIL 在异常处中断：").Append(ex.GetType().Name).Append(": ").Append(ex.Message);

            if (hasBaseline)
            {
                try
                {
                    shim.RollbackTo(baseline);
                    report.Append(" （已回滚到基线快照）");
                }
                catch
                {
                    report.Append(" （回滚也失败）");
                }
            }

            return new SelfCheckReport(false, report.ToString());
        }
    }
}
