## 缺陷

`NativeKernel.Load(path)` **没有真的加载 `path` 指向的模块**。进程里只要已经加载过一个 `trit_rt`，之后无论 `Load` 传什么路径，实际用的都是**第一个**那个模块。指定路径被静默忽略，且不报任何错。

## 影响

### 1. 配置形同虚设

模组侧 `TritSettings.LibraryPath` 与环境变量 `TRIT_RT_LIBRARY` 都能指定原生库路径。原生库按**内容摘要分目录**落盘（`NativeLibraryStager`），升级后新旧两份会同时存在于磁盘。用户把 `LibraryPath` 指向新版本目录时，**实际跑的还是旧版本**，而且没有任何日志提示。

### 2. ABI 校验读的是别的模块

`NativeKernel.Load` 里的 ABI 版本检查走的是静态 `DllImport`，读的是**首个模块**的 `tt_rt_abi_version`。于是：

- 加载一个**根本不导出 `tt_rt_*`** 的库也会「成功」，并返回 `Mode = Native`；
- 直到 `Dispose()` 时才抛 `EntryPointNotFoundException`（崩在销毁路径上）。

实测（Windows）：

```
真 trit_rt 已加载（DllImport 缓存指向它）
!!! 加载 kernel32.dll 竟然成功了（Mode=Native）
    => 缺陷确认：指定路径被静默忽略，实际用的还是第一个 trit_rt 模块
```

### 3. 多实例语义不确定

`NativeImportResolver` 是进程级单例，`SetDllImportResolver` 每个程序集只能装一次。进程内同时存在两份 `trit_rt`（模组热重载后的新摘要目录、测试里的带前缀副本、并存的新旧版本）是**常态而非意外**，但所有静态 `DllImport` 只能落到其中一份。

## 根因

`dotnet/TritRuntime.Shim/src/NativeKernel.cs` 用静态 `DllImport` + 进程级 `SetDllImportResolver` 绑定原生库：

```csharp
var handle = NativeLibrary.Load(libraryPath);
NativeImportResolver.RegisterExplicitLibrary(NativeBindings.LibraryName, handle);
var abi = NativeBindings.AbiVersion();   // ← 走 DllImport，不是上面那个 handle
```

.NET 会**缓存** DllImport 的首次解析结果，因此 `RegisterExplicitLibrary` 之后的路径更新**根本不生效**——解析器闭包里读到的虽然是最新字段值，但 .NET 已经缓存了首次解析到的模块，后续直接复用。

同时 `SetDllImportResolver` 每个程序集只能装一次，解析器里只存**一个**句柄，无法按实例分流。

## 复现

仓库根目录：

```bash
cargo build --release -p trit-rt
```

```csharp
var src = "<repo>/target/release/trit_rt.dll";
using var real = NativeKernel.Load(src);          // 首个模块，OK
using var bogus = NativeKernel.Load("C:/Windows/System32/kernel32.dll");
// 期望：抛异常（缺 tt_rt_* 导出）
// 实际：成功，Mode = Native
```

## 修复方向

把**所有带句柄的调用**改成**按实例绑定**：每个 `NativeKernel` 持有自己那份模块的导出函数指针，调用时直接跳到本实例的模块，完全不依赖全局解析器。

```csharp
var library = NativeLibrary.Load(libraryPath);
var submit = Marshal.GetDelegateForFunctionPointer<NativeSubmit>(
    NativeLibrary.GetExport(library, "tt_rt_submit"));
```

这样：

- 每个实例只认自己加载的模块，指定路径真正生效；
- ABI 校验读的是**本模块**的版本，不会被别的模块污染；
- 进程内多份 `trit_rt` 互不干扰。

进程级静态 `DllImport`（ABI 版本、运行时版本这类与实例无关的查询）可以保留，但要用「首个模块先到先得」的语义，且不能再承担带句柄的调用。

> 注：原生库按内容摘要落盘时文件名可能带前缀（如 `<hash>_trit_rt.dll`）。导出函数名与文件名无关，按实例绑定只认「这个模块有没有 `tt_rt_*` 导出」，所以这种命名方式天然可用。

## 补充：Rust 侧句柄空间

`tt_rt_create` 的运行时句柄与任务 id 都是**每个模块实例独立计数**（都从 1 开始）。两个模块实例的句柄空间会碰撞。

这本身不是 bug（句柄只在所属模块内有效），但如果按实例绑定改得不彻底——比如只把 `Submit` 改成实例绑定而 `Create` 仍走静态路径——就会出现「句柄在 A 模块创建、调用却发到 B 模块」的串台，且因为句柄值相同而**不报错**，静默操作到别人的运行时。修复时需注意整条链路（创建/提交/销毁/快照）都走同一实例。

## 环境

- Windows 11 / .NET 8.0 / tModLoader 1.4.4.9
- 仓库：`bfxh/trit-tml`
- 复现分支：v2 并行扫描能力开发中（`crates/trit-rt` + `dotnet/TritRuntime.Shim`）
