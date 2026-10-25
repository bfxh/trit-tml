using System;
using System.IO;
using System.Security.Cryptography;
using Terraria;

namespace TritRuntimeMod;

/// <summary>
/// 把打进 <c>.tmod</c> 的原生内核（Rust cdylib）取出来落盘，供 <see cref="TritRuntime.Kernel.RuntimeHost"/> 加载。
/// </summary>
/// <remarks>
/// <para>
/// 为什么需要这一步：tML 的 <c>dllReferences</c> 只加载<b>托管</b>程序集
/// （<c>AssemblyManager.LoadAssemblies</c> 按 <c>lib/&lt;名字&gt;.dll</c> 精确取），
/// 原生 DLL 不能走这条路；而 <c>NativeLibrary.Load</c> 又只认文件系统路径，不认内存字节。
/// 所以原生内核只能作为普通资源打进包里，运行时取出写盘再按绝对路径加载。
/// </para>
/// <para>
/// 落盘路径带内容摘要前缀：版本升级时换路径，避免覆盖上一个进程仍在占用中的文件。
/// 任何一步失败都返回 <see langword="null"/>，由调用方回退到托管内核——绝不因为这事让游戏起不来。
/// </para>
/// </remarks>
internal static class NativeLibraryStager
{
    /// <summary>包内原生库所在目录（<c>AssemblyManager</c> 不枚举子目录，故不会误当托管程序集）。</summary>
    private const string ResourceDirectory = "lib/Native/";

    /// <summary>
    /// 尝试取出并落盘原生内核；失败返回 <see langword="null"/>。
    /// </summary>
    public static string? TryStage(TritRuntimeMod mod)
    {
        string fileName = FileName();

        // 打包时相对路径的分隔符可能是 '/' 也可能是 '\'，两种都试。
        string[] candidates =
        {
            ResourceDirectory + fileName,
            ResourceDirectory.Replace('/', '\\') + fileName,
            fileName, // 兜底：也有人直接把它丢在模组根目录
        };

        foreach (string candidate in candidates)
        {
            byte[] bytes;
            try
            {
                if (!mod.FileExists(candidate))
                {
                    continue;
                }

                bytes = mod.GetFileBytes(candidate);
            }
            catch (Exception ex)
            {
                mod.Logger.Warn($"[trit] 读取包内原生内核 {candidate} 失败：{ex.Message}");
                return null;
            }

            if (bytes is null || bytes.Length == 0)
            {
                continue;
            }

            return WriteToCache(mod, fileName, bytes);
        }

        return null;
    }

    /// <summary>按平台取原生库文件名（与 <c>RuntimeHost</c> 的探测名一致）。</summary>
    private static string FileName()
    {
        if (OperatingSystem.IsWindows())
        {
            return "trit_rt.dll";
        }

        return OperatingSystem.IsMacOS() ? "libtrit_rt.dylib" : "libtrit_rt.so";
    }

    private static string? WriteToCache(TritRuntimeMod mod, string fileName, byte[] bytes)
    {
        string root = CacheRoot();
        try
        {
            Directory.CreateDirectory(root);

            // 内容摘要做「目录」而不是文件名前缀：
            // 文件名必须保持 trit_rt.dll —— DllImport 声明的库名是常量 "trit_rt"，
            // 一旦文件名被加了前缀，从路径推导出来的名字就与它对不上，
            // 解析器不命中，加载必然失败（真机实测 0x8007007E）。
            // 用目录隔离同样能避免覆盖仍在占用中的旧版本。
            string tag = Convert.ToHexString(SHA256.HashData(bytes).AsSpan(0, 8)).ToLowerInvariant();
            string directory = Path.Combine(root, tag);
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, fileName);

            if (!File.Exists(path) || new FileInfo(path).Length != bytes.Length)
            {
                File.WriteAllBytes(path, bytes);
            }

            PruneOldVersions(root, tag);
            return path;
        }
        catch (Exception ex)
        {
            mod.Logger.Warn($"[trit] 原生内核落盘失败（本次会话使用托管回退）：{ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 清掉非当前版本的落盘目录。
    /// 摘要目录只增不删，否则每次升级都白留一份 2.5 MB；
    /// 旧目录可能被上一个尚未退出的进程占用，删除失败一律忽略。
    /// </summary>
    private static void PruneOldVersions(string root, string keep)
    {
        try
        {
            foreach (string directory in Directory.EnumerateDirectories(root))
            {
                if (string.Equals(Path.GetFileName(directory), keep, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    Directory.Delete(directory, recursive: true);
                }
                catch (Exception)
                {
                    // 被占用就下次再清
                }
            }
        }
        catch (Exception)
        {
            // 枚举失败（目录不存在 / 权限）不影响本次加载
        }
    }

    private static string CacheRoot()
    {
        try
        {
            string savePath = Main.SavePath;
            if (!string.IsNullOrEmpty(savePath))
            {
                return Path.Combine(savePath, "TritRuntime", "native");
            }
        }
        catch
        {
            // Main.SavePath 在游戏极早期可能抛异常，落到临时目录即可
        }

        return Path.Combine(Path.GetTempPath(), "TritRuntime", "native");
    }
}
