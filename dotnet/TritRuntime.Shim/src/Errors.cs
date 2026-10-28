#nullable enable

using TritRuntime.Wire;

namespace TritRuntime.Kernel;

/// <summary>
/// 内核错误：携带 C ABI 状态码（见 <see cref="TritRuntime.Wire.Status"/>）与冲突时的争议诊断。
/// 调用方按 <see cref="Status"/> 决定「重试 / 回退到原 C# 路径 / 上报」。
/// </summary>
public sealed class KernelException : Exception
{
    /// <summary>构造内核错误。</summary>
    /// <param name="status">C ABI 状态码。</param>
    /// <param name="message">可读信息（含 Rust/托管侧 last_error 细节）。</param>
    /// <param name="disputes">冲突车道及全部肇事方；非冲突错误传 null。</param>
    public KernelException(int status, string message, IReadOnlyList<LaneDispute>? disputes = null)
        : base(message)
    {
        Status = status;
        Disputes = disputes ?? Array.Empty<LaneDispute>();
    }

    /// <summary>C ABI 状态码。</summary>
    public int Status { get; }

    /// <summary>争议车道及全部肇事方；非冲突错误为空数组。</summary>
    public IReadOnlyList<LaneDispute> Disputes { get; }

    /// <summary>是否为可恢复的「仲裁冲突」：调用方可重试、回滚，或回退到 C# 路径。</summary>
    public bool IsConflict => Status == TritRuntime.Wire.Status.Conflict;

    /// <summary>是否为「应回退到原 C# 路径」的错误（句柄丢失、容量超限、解码失败等）。</summary>
    public bool ShouldFallBack => Status is TritRuntime.Wire.Status.BadHandle
        or TritRuntime.Wire.Status.Capacity
        or TritRuntime.Wire.Status.Decode
        or TritRuntime.Wire.Status.Panic
        or TritRuntime.Wire.Status.Arg;
}

/// <summary>状态码可读名（与 Rust <c>RtError::status_code</c> 的映射一一对应）。</summary>
public static class StatusNames
{
    /// <summary>把状态码翻成中文可读名。</summary>
    public static string Describe(int status) => status switch
    {
        TritRuntime.Wire.Status.Ok => "成功",
        TritRuntime.Wire.Status.BadHandle => "句柄不存在",
        TritRuntime.Wire.Status.BadOp => "opcode 非法",
        TritRuntime.Wire.Status.Buffer => "输出缓冲区不足",
        TritRuntime.Wire.Status.Capacity => "容量超限",
        TritRuntime.Wire.Status.Conflict => "仲裁冲突",
        TritRuntime.Wire.Status.Panic => "原生侧 panic 被拦住",
        TritRuntime.Wire.Status.Decode => "线格式解码失败",
        TritRuntime.Wire.Status.NotFound => "事务/快照/订阅不存在",
        TritRuntime.Wire.Status.State => "事务状态非法",
        TritRuntime.Wire.Status.Arg => "参数非法",
        _ => $"未知状态码 {status}",
    };
}
