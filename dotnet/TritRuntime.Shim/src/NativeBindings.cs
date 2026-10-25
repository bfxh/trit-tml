using System.Runtime.InteropServices;
using TritRuntime.Wire;

namespace TritRuntime.Kernel;

/// <summary>trit_rt.dll 的 P/Invoke 绑定（C ABI，见 docs/ABI.md）。</summary>
public static class NativeBindings
{
    /// <summary>原生库名；加载顺序由 <see cref="NativeLibrary"/> 解析规则 + 显式路径决定。</summary>
    public const string LibraryName = "trit_rt";

    /// <summary>ABI 版本号。</summary>
    [DllImport(LibraryName, EntryPoint = "tt_rt_abi_version", CallingConvention = CallingConvention.Cdecl)]
    public static extern uint AbiVersion();

    /// <summary>运行时版本号。</summary>
    [DllImport(LibraryName, EntryPoint = "tt_rt_version", CallingConvention = CallingConvention.Cdecl)]
    public static extern uint Version();

    /// <summary>创建运行时；config 为 JSON 字节（可为空 = 默认配置）。失败返回 0。</summary>
    [DllImport(LibraryName, EntryPoint = "tt_rt_create", CallingConvention = CallingConvention.Cdecl)]
    public static extern ulong Create(byte[]? config, int len);

    /// <summary>销毁运行时。</summary>
    [DllImport(LibraryName, EntryPoint = "tt_rt_destroy", CallingConvention = CallingConvention.Cdecl)]
    public static extern int Destroy(ulong handle);

    /// <summary>提交一个操作。</summary>
    [DllImport(LibraryName, EntryPoint = "tt_rt_submit", CallingConvention = CallingConvention.Cdecl)]
    public static extern int Submit(ulong handle, uint opcode, byte[]? payload, int len, byte[]? output, int capacity);

    /// <summary>推进一个 tick。</summary>
    [DllImport(LibraryName, EntryPoint = "tt_rt_step", CallingConvention = CallingConvention.Cdecl)]
    public static extern int Step(ulong handle, ulong tick, byte[]? output, int capacity);

    /// <summary>打快照并把快照字节写进 output。</summary>
    [DllImport(LibraryName, EntryPoint = "tt_rt_snapshot", CallingConvention = CallingConvention.Cdecl)]
    public static extern int Snapshot(ulong handle, byte[]? output, int capacity);

    /// <summary>差分两个快照。</summary>
    [DllImport(LibraryName, EntryPoint = "tt_rt_diff", CallingConvention = CallingConvention.Cdecl)]
    public static extern int Diff(ulong handle, ulong snapshotA, ulong snapshotB, byte[]? output, int capacity);

    /// <summary>能力位图；句柄非法返回 0。</summary>
    [DllImport(LibraryName, EntryPoint = "tt_rt_capabilities", CallingConvention = CallingConvention.Cdecl)]
    public static extern ulong Capabilities(ulong handle);

    /// <summary>最近一次错误信息。</summary>
    [DllImport(LibraryName, EntryPoint = "tt_rt_last_error", CallingConvention = CallingConvention.Cdecl)]
    public static extern int LastError(ulong handle, byte[]? buffer, int capacity);
}
