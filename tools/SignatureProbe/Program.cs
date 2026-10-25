using System.Reflection;

namespace TritTml.Tools;

/// <summary>
/// 签名探针：对 tModLoader 安装目录里的程序集做<b>只读元数据</b>反射，
/// 导出写 MonoMod 钩子前必须确认的真实签名。绝不实例化、绝不调用任何方法——
/// 探针的目的就是避免"猜签名"式写钩子（猜错的钩子只在加载模组时才炸）。
/// </summary>
internal static class Program
{
    private static string _root = string.Empty;

    private static int Main(string[] args)
    {
        _root = args.Length > 0
            ? args[0]
            : Environment.GetEnvironmentVariable("TML_STEAM_PATH") ?? @"D:\SteamLibrary\steamapps\common\tModLoader";
        _root = Path.GetFullPath(_root);
        if (!File.Exists(Path.Combine(_root, "tModLoader.dll")))
        {
            Console.Error.WriteLine($"找不到 tModLoader.dll：{_root}");
            Console.Error.WriteLine("用法：SignatureProbe [tModLoader安装根目录]  或设置环境变量 TML_STEAM_PATH");
            return 2;
        }

        // 运行时只做元数据查询，但触达具体 Type 仍可能级联加载基元程序集；
        // 这里把 Libraries 全树挂到解析器上，保证 GetType/GetMethods 不会炸。
        AppDomain.CurrentDomain.AssemblyResolve += ResolveFromLibraries;

        Console.WriteLine($"# tModLoader 根目录：{_root}");
        var tml = Assembly.LoadFrom(Path.Combine(_root, "tModLoader.dll"));
        DumpAssemblyVersion(tml);

        var hooksPath = FindHooks();
        Assembly? hooks = null;
        if (hooksPath is not null)
        {
            hooks = Assembly.LoadFrom(hooksPath);
            Console.WriteLine($"\n# TerrariaHooks：{hooksPath}");
        }
        else
        {
            Console.WriteLine("\n# 未找到 TerrariaHooks.dll");
        }

        Console.WriteLine("\n================ Mod.Call 契约 ================");
        DumpTypeMethods(tml, "Terraria.ModLoader.Mod", new[]
        {
            "Call", "Load", "Unload", "get_Logger", "get_LoggerOrNull",
        });

        Console.WriteLine("\n================ ModSystem 候选钩子（更新/AI 相关） ================");
        DumpModSystemHooks(tml);

        Console.WriteLine("\n================ Terraria.NPC ================");
        DumpTypeMethods(tml, "Terraria.NPC", new[]
        {
            "AI", "Update", "SetDefaults", "get_netUpdate", "set_netUpdate",
            "get_whoAmI", "get_type", "get_life", "set_life", "get_lifeMax", "get_active",
            "get_position", "set_position", "get_velocity", "set_velocity", "get_direction", "set_direction",
            "get_boss", "get_realLife", "ScaleStats",
        });

        Console.WriteLine("\n================ Terraria.Projectile ================");
        DumpTypeMethods(tml, "Terraria.Projectile", new[]
        {
            "AI", "Update", "get_type", "get_owner", "get_active", "get_position",
            "get_velocity", "get_damage", "Kill", "get_identity",
        });

        Console.WriteLine("\n================ Terraria.NetMessage.SendData 重载 ================");
        DumpOverloads(tml, "Terraria.NetMessage", "SendData");

        Console.WriteLine("\n================ Terraria.NetMessage.SendData 重载（参数名/类型逐个） ================");
        DumpSendDataParameters(tml);

        Console.WriteLine("\n================ Terraria.Player（桥接用字段） ================");
        DumpTypeMethods(tml, "Terraria.Player", new[]
        {
            "get_whoAmI", "get_statLife", "set_statLife", "get_statLifeMax", "get_position",
            "get_velocity", "get_direction", "get_active", "get_dead",
        });

        Console.WriteLine("\n================ Terraria.ModLoader.ModPacket（网络路径） ================");
        DumpTypeMethods(tml, "Terraria.ModLoader.ModPacket", new[]
        {
            ".ctor", "Send", "Write", "ReadString", "WriteString", "ReadInt32", "WriteInt32", "get_Reader", "get_Writer",
        });

        Console.WriteLine("\n================ MonoMod.RuntimeDetour.Hook ================");
        DumpTypeMethods(LoadMonoModDetour() ?? tml, "MonoMod.RuntimeDetour.Hook", Array.Empty<string>(), all: true);

        if (hooks is not null)
        {
            Console.WriteLine("\n================ TerrariaHooks：目标 On_* 类型的钩子字段 ================");
            foreach (var typeName in new[]
                     {
                         "Terraria.On_NPC",
                         "Terraria.On_Projectile",
                         "Terraria.On_NetMessage",
                         "Terraria.On_Player",
                         "Terraria.On_WorldGen",
                         "Terraria.On_MessageBuffer",
                         "Terraria.On_Main",
                         "Terraria.On_RemoteClient",
                     })
            {
                DumpFullStructure(hooks, typeName);
            }

            Console.WriteLine("\n================ Terraria.On_NPC 完整结构（含非公共成员） ================");
            DumpFullStructure(hooks, "Terraria.On_NPC");
            DumpFullStructure(hooks, "Terraria.On_NetMessage");
        }

        Console.WriteLine("\n================ MonoMod.RuntimeDetour：静态 Hook 工厂 ================");
        DumpStaticHookFactories();

        Console.WriteLine("\n================ 桥接静态面：Main / NPCSpawnParams / ID ================");
        DumpTypeMethods(tml, "Terraria.Main", new[]
        {
            "get_GameUpdateCount", "get_npc", "get_projectile", "get_player", "get_maxNPCs", "get_maxProjectiles",
            "get_netMode", "get_dedServ", "get_time", "get_dayTime",
        });
        DumpTypeMethods(tml, "Terraria.NPCSpawnParams", Array.Empty<string>(), all: true);
        DumpTypeMethods(tml, "Terraria.ID.NPCID", new[] { "get_Count", "get_NegativeIDCount" });

        Console.WriteLine("\n================ 桥接静态面：ModSystem 生命周期 ================");
        DumpTypeMethods(tml, "Terraria.ModLoader.ModSystem", new[]
        {
            "get_Mod", "OnModLoad", "OnModUnload", "PostUpdateEverything", "PreUpdateEntities",
        });

        Console.WriteLine("\n================ 桥接静态面：NetMessage.SendData 事件钩子形态 ================");
        DumpEventHooks(hooks, "Terraria.On_NetMessage", "SendData");
        DumpEventHooks(hooks, "Terraria.On_NPC", "SetDefaults");
        DumpEventHooks(hooks, "Terraria.On_NPC", "AI");
        DumpEventHooks(hooks, "Terraria.On_Projectile", "AI");

        Console.WriteLine("\n================ 桥接静态面：On 根类型定位 ================");
        foreach (var candidate in new[] { "MonoMod.RuntimeDetour.HookGen.On", "On" })
        {
            var type = hooks?.GetType(candidate, throwOnError: false);
            Console.WriteLine($"  {candidate} => {(type is null ? "不存在" : type.FullName + "（所在程序集 " + type.Assembly.GetName().Name + "）")}");
            if (type is null)
            {
                continue;
            }

            foreach (var nested in type.GetNestedTypes(BindingFlags.Public).OrderBy(t => t.Name, StringComparer.Ordinal).Take(12))
            {
                Console.WriteLine($"      嵌套 {nested.Name}");
            }
        }

        Console.WriteLine("\n# 探针结束：以上为元数据反射结果，未实例化任何类型。");
        return 0;
    }

    /// <summary>
    /// tML 的 On.* 钩子是"事件"形态：外层类上 add_/remove_ 一对方法，
    /// 载荷是 hook_* 委托（第一参数固定为 orig_* 委托）。这里把三者一起列出来。
    /// </summary>
    private static void DumpEventHooks(Assembly? hooks, string typeName, string hookName)
    {
        if (hooks is null)
        {
            Console.WriteLine($"  !! 程序集缺失，跳过 {typeName}.{hookName}");
            return;
        }

        var type = hooks.GetType(typeName, throwOnError: false);
        if (type is null)
        {
            Console.WriteLine($"  !! 找不到 {typeName}");
            return;
        }

        var add = type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .FirstOrDefault(m => m.Name == $"add_{hookName}");
        if (add is null)
        {
            Console.WriteLine($"  !! {typeName} 没有事件 {hookName}");
            return;
        }

        var hookDelegate = add.GetParameters()[0].ParameterType;
        var invoke = hookDelegate.GetMethod("Invoke");
        var ps = invoke?.GetParameters() ?? Array.Empty<ParameterInfo>();
        Console.WriteLine($"  == {typeName}.{hookName} 事件");
        Console.WriteLine($"     订阅形态：{typeName}.{hookName} += (orig, ...) => {{ ...; orig(...); }};");
        Console.WriteLine($"     hook 委托：{Fmt(hookDelegate)}");
        Console.WriteLine($"     签名：({string.Join(", ", ps.Select(Fmt))}) => {Fmt(invoke?.ReturnType ?? typeof(void))}");
        // hook 委托的第一参数就是 orig_* 委托（调用原方法的入口）。
        var origDelegate = ps.Length > 0 && typeof(Delegate).IsAssignableFrom(ps[0].ParameterType) ? ps[0].ParameterType : null;
        if (origDelegate is not null)
        {
            var origInvoke = origDelegate.GetMethod("Invoke");
            var origPs = origInvoke?.GetParameters() ?? Array.Empty<ParameterInfo>();
            Console.WriteLine($"     orig 委托：{Fmt(origDelegate)}");
            Console.WriteLine($"     orig 签名：({string.Join(", ", origPs.Select(Fmt))}) => {Fmt(origInvoke?.ReturnType ?? typeof(void))}");
        }
    }

    private static void DumpStaticHookFactories()
    {
        var detour = LoadMonoModDetour();
        if (detour is null)
        {
            Console.WriteLine("  !! 未找到 MonoMod.RuntimeDetour.dll");
            return;
        }

        Console.WriteLine($"  # 程序集 {detour.GetName().Name} {detour.GetName().Version}");
        foreach (var type in SafeTypes(detour).OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            var statics = type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Where(m => m.Name is "Hook" or "Detour")
                .ToList();
            if (statics.Count == 0)
            {
                continue;
            }

            Console.WriteLine($"  == {type.FullName}");
            foreach (var m in statics)
            {
                Console.WriteLine($"    static {Fmt(m.ReturnType)} {m.Name}({string.Join(", ", m.GetParameters().Select(Fmt))})");
            }
        }
    }

    /// <summary>
    /// 安全枚举类型：依赖程序集里有 ref 程序集时 GetTypes 会整体抛
    /// ReflectionTypeLoadException，这里只保留成功加载的部分。
    /// </summary>
    private static IEnumerable<Type> SafeTypes(Assembly asm)
    {
        try
        {
            return asm.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t is not null).Select(t => t!);
        }
    }

    // ---------------------------------------------------------------- 基础设施

    private static Assembly? ResolveFromLibraries(object? sender, ResolveEventArgs args)
    {
        var name = new AssemblyName(args.Name).Name;
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        foreach (var candidate in EnumerateLibraries())
        {
            var path = Path.Combine(candidate, name + ".dll");
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                return Assembly.LoadFrom(path);
            }
            catch (BadImageFormatException)
            {
                // NuGet 包里混有 ref 程序集（标记 ReferenceAssembly），不能用于执行；
                // 继续找下一个候选路径。
                continue;
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumerateLibraries()
    {
        var libs = Path.Combine(_root, "Libraries");
        if (!Directory.Exists(libs))
        {
            yield break;
        }

        foreach (var dir in Directory.EnumerateDirectories(libs, "*", SearchOption.AllDirectories))
        {
            yield return dir;
        }

        yield return libs;
        yield return _root;
    }

    private static string? FindHooks()
    {
        var dir = Path.Combine(_root, "Libraries", "TerrariaHooks");
        if (!Directory.Exists(dir))
        {
            return null;
        }

        return Directory.EnumerateFiles(dir, "TerrariaHooks.dll", SearchOption.AllDirectories).FirstOrDefault();
    }

    private static Assembly? LoadMonoModDetour()
    {
        foreach (var dir in EnumerateLibraries())
        {
            var path = Path.Combine(dir, "MonoMod.RuntimeDetour.dll");
            if (File.Exists(path))
            {
                return Assembly.LoadFrom(path);
            }
        }

        return null;
    }

    private static void DumpAssemblyVersion(Assembly asm)
    {
        Console.WriteLine($"# 程序集：{asm.GetName().Name} {asm.GetName().Version}");
    }

    // ---------------------------------------------------------------- 输出格式

    private static void DumpTypeMethods(Assembly asm, string typeName, string[] names, bool all = false)
    {
        var type = asm.GetType(typeName, throwOnError: false);
        if (type is null)
        {
            Console.WriteLine($"  !! 找不到类型 {typeName}");
            return;
        }

        Console.WriteLine($"  == {type.FullName}（{(type.IsAbstract ? "abstract " : "")}{type.BaseType?.Name ?? "-"}）");
        var methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(m => all || names.Contains(m.Name))
            .OrderBy(m => m.Name, StringComparer.Ordinal)
            .ThenBy(m => m.GetParameters().Length);
        foreach (var m in methods)
        {
            var flags = (m.IsPublic ? "public " : m.IsFamily ? "protected " : m.IsAssembly ? "internal " : "private ")
                        + (m.IsStatic ? "static " : m.IsVirtual ? "virtual " : "");
            Console.WriteLine($"    {flags}{Fmt(m.ReturnType)} {m.Name}({string.Join(", ", m.GetParameters().Select(Fmt))})");
        }

        if (names.Length == 0 && !all)
        {
            return;
        }

        // 桥接需要的实例字段/属性单独列（NPC/Projectile/Player 的公开表面）。
        var members = type.GetFields(BindingFlags.Public | BindingFlags.Instance)
            .Select(f => $"field {Fmt(f.FieldType)} {f.Name}")
            .Concat(type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.GetIndexParameters().Length == 0)
                .Select(p => $"prop  {Fmt(p.PropertyType)} {p.Name}"))
            .OrderBy(s => s, StringComparer.Ordinal);
        foreach (var member in members)
        {
            Console.WriteLine($"    · {member}");
        }
    }

    private static void DumpOverloads(Assembly asm, string typeName, string methodName)
    {
        var type = asm.GetType(typeName, throwOnError: false);
        if (type is null)
        {
            Console.WriteLine($"  !! 找不到类型 {typeName}");
            return;
        }

        foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                     .Where(m => m.Name == methodName)
                     .OrderBy(m => m.GetParameters().Length))
        {
            var flags = m.IsStatic ? "static " : "";
            Console.WriteLine($"    {flags}{Fmt(m.ReturnType)} {m.Name}({string.Join(", ", m.GetParameters().Select(Fmt))})");
        }
    }

    private static void DumpSendDataParameters(Assembly asm)
    {
        var type = asm.GetType("Terraria.NetMessage", throwOnError: false);
        if (type is null)
        {
            Console.WriteLine("  !! 找不到类型 Terraria.NetMessage");
            return;
        }

        foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
                     .Where(m => m.Name == "SendData")
                     .OrderBy(m => m.GetParameters().Length))
        {
            var ps = m.GetParameters();
            Console.WriteLine($"  SendData({ps.Length} 参数)：");
            for (var i = 0; i < ps.Length; i++)
            {
                var p = ps[i];
                Console.WriteLine($"    [{i}] {Fmt(p.ParameterType)} {p.Name}{(p.HasDefaultValue ? $" = {p.DefaultValue ?? "null"}" : string.Empty)}");
            }
        }
    }

    private static void DumpModSystemHooks(Assembly asm)
    {
        var type = asm.GetType("Terraria.ModLoader.ModSystem", throwOnError: false);
        if (type is null)
        {
            Console.WriteLine("  !! 找不到类型 Terraria.ModLoader.ModSystem");
            return;
        }

        foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                     .Where(m => m.Name.Contains("Update", StringComparison.Ordinal)
                                 || m.Name.Contains("AI", StringComparison.Ordinal)
                                 || m.Name.Contains("Load", StringComparison.Ordinal)
                                 || m.Name.Contains("Unload", StringComparison.Ordinal))
                     .OrderBy(m => m.Name, StringComparer.Ordinal))
        {
            var flags = m.IsVirtual ? "virtual " : "";
            Console.WriteLine($"    {flags}{Fmt(m.ReturnType)} {m.Name}({string.Join(", ", m.GetParameters().Select(Fmt))})");
        }
    }

    private static void DumpOnNamespaces(Assembly hooks)
    {
        var onType = hooks.GetType("On", throwOnError: false);
        if (onType is null)
        {
            Console.WriteLine("  !! 找不到 On 类型");
            return;
        }

        foreach (var nested in onType.GetNestedTypes(BindingFlags.Public).OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            Console.WriteLine($"    {nested.FullName}  （{nested.GetNestedTypes(BindingFlags.Public).Length} 个子类型）");
        }
    }

    /// <summary>完整 dump：外层类的全部方法 + 全部嵌套类型（含非公共）及其字段/方法。</summary>
    private static void DumpFullStructure(Assembly hooks, string typeName)
    {
        var type = hooks.GetType(typeName, throwOnError: false);
        if (type is null)
        {
            Console.WriteLine($"  !! 找不到 {typeName}");
            return;
        }

        Console.WriteLine($"  == {type.FullName} isAbstract={type.IsAbstract} isSealed={type.IsSealed}");
        foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                     .OrderBy(m => m.Name, StringComparer.Ordinal))
        {
            Console.WriteLine($"    method {Fmt(m.ReturnType)} {m.Name}({string.Join(", ", m.GetParameters().Select(Fmt))})");
        }

        foreach (var nested in type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic).OrderBy(t => t.Name, StringComparer.Ordinal))
        {
            var kind = nested.IsValueType ? "struct" : nested.IsInterface ? "interface" : nested.IsEnum ? "enum" : typeof(Delegate).IsAssignableFrom(nested) ? "delegate" : "class";
            Console.WriteLine($"    nested {kind} {nested.Name}");
            foreach (var f in nested.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                Console.WriteLine($"        field {Fmt(f.FieldType)} {f.Name}{(f.IsLiteral ? $" = {f.GetRawConstantValue()}" : string.Empty)}");
            }

            foreach (var m in nested.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                         .Where(m => !m.IsSpecialName)
                         .OrderBy(m => m.Name, StringComparer.Ordinal))
            {
                Console.WriteLine($"        method {Fmt(m.ReturnType)} {m.Name}({string.Join(", ", m.GetParameters().Select(Fmt))})");
            }
        }
    }

    private static string Fmt(ParameterInfo p)
    {
        var mods = new List<string>();
        if (p.IsOut) mods.Add("out");
        if (p.IsIn) mods.Add("in");
        if (p.ParameterType.IsByRef) mods.Add("ref");
        return $"{(mods.Count > 0 ? string.Join(" ", mods) + " " : string.Empty)}{Fmt(p.ParameterType.IsByRef ? p.ParameterType.GetElementType()! : p.ParameterType)} {p.Name}";
    }

    private static string Fmt(Type t)
    {
        if (t.IsGenericType)
        {
            var name = t.Name[..t.Name.IndexOf('`')];
            return $"{name}<{string.Join(", ", t.GetGenericArguments().Select(Fmt))}>";
        }

        return t.Name;
    }
}
