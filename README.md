# trit-tml

[![ci](https://github.com/bfxh/trit-tml/actions/workflows/ci.yml/badge.svg)](https://github.com/bfxh/trit-tml/actions/workflows/ci.yml)

**tModLoader 的旁路三进制运行时改善层** — 不替换 C#/CLR/Unity 主循环，只把其中"可判定、可回退、可差分"的那层内核旁路接出来。

> A bypass ternary runtime layer for tModLoader. The vanilla C#/Unity main loop stays authoritative;
> a Rust ternary kernel takes over scheduling, transactions, snapshots and codec — with capability
> negotiation, per-request shadow diffing and automatic fallback. Other mods keep seeing the original
> C# API, so compatibility can't explode.

```
其他模组 ──原样调用──> tModLoader C# API
                          │
                          ├──────────────> (不变) 原 C#/Unity 运行时继续跑
                          │
                          └──> Terraria.On_* 钩子（只观测）
                                    │
                                    ▼
                          C# Runtime Shim（薄层，保住兼容性）
                          Mod.Call 契约 · 能力协商 · 影子差分 · 托管回退
                                    │  纯数据 + 句柄，主线程提交
                                    ▼
                          Rust 三进制内核 trit_rt.dll
                          事件路由 · 事务日志 · 快照/差分 · 确定性 PRNG · 序列化
```

Rust **从不持有** `Player`/`NPC`/`Tile` 这类 Unity/tModLoader 对象。跨边界只有 `uint` 车道键、三值增量、
句柄和错误码；所有游戏状态读写都在 C# 主线程。

## v2 能力：并行任务池 · 扫描算子 · GPU 可选 · 全局预算

v2 在原有能力之上加了一层**旁路任务池**（Rust 侧多线程，游戏主线程只入队、只取结果）：

| 能力 | 位置 | 说明 |
|---|---|---|
| 并行任务池 | `1<<9` `PARALLEL` | 扫描任务异步跑在 Rust 工作线程上；可抢占、协作式取消 |
| 扫描算子 | `0x14 SCAN_SUBMIT` | `histogram` / `pattern` / `density` / `dot` 四种算子 |
| GPU 可选 | `1<<10` `GPU` | 构建带 `gpu` feature 才可用；玩家可设 `auto/on/off`，自动探测 + 对拍不一致永久回退 CPU |
| 全局预算 | `1<<11` `BUDGET` | 多 mod 共用时的全局资源账本：每 mod 在跑上限、排队数、计费 |

**扫描算子**发挥三进制的优势：`pattern` 用位并行的双导轨点积（三值矩阵乘），
规模够大且 GPU 可用时一次矩阵乘算完，否则 CPU 分块扫。

**多 mod 隔离**是这一层的设计前提：任务池是全局的，但 `JOB_POLL` 支持按 `owner` 过滤，
**只出队真正返回的那几条**，其他 mod 的结果留在队列里。计费也按 owner 分账
（`BUDGET` 响应里的 `mods[]`）。

**给其他 mod 的接口**（`Mod.Call` 契约，命令名以 `TritRuntime.` 开头且大小写不敏感）：

```
TritRuntime.Capabilities      能力报告（判断有没有 PARALLEL / GPU / BUDGET 位）
TritRuntime.Scan              提交扫描任务，返回 job id
TritRuntime.PollJobs          取回结果，可按 owner 过滤：(string owner, int limit)
TritRuntime.CancelJob         取消单个任务
TritRuntime.CancelOwner       取消该 owner 的全部排队任务
TritRuntime.GpuStatus         查询 / 切换 GPU 策略
TritRuntime.Budget            全局预算快照
TritRuntime.Metrics           任务池 Prometheus 指标
```

引用了本程序集的 mod 也可以直接用 `TritApi` 静态方法（编译期就有类型检查）：

```csharp
ulong job = TritApi.Scan("MyMod", ScanKind.Histogram, trits);
if (TritApi.TryPollJob(job, out JobResult? r))
{
    // r.ResultJson 例如 {"histogram":{"neg":1,"zero":2,"pos":3}}
}
```

完整用法见 [mod/TritRuntimeMod/README.md](mod/TritRuntimeMod/README.md)，
底层 ABI 见 [docs/ABI.md](docs/ABI.md)。

## 兼容性为什么能守住

| 机制 | 落地位置 |
|---|---|
| API 表面不变 | 其他模组仍走原生 `Mod.Call` / 原生类型，本层不改任何签名 |
| 能力协商 + 回退 | `RuntimeHost`：原生库缺失 / ABI 不符 / 装载失败 → 托管内核（语义逐字节等价） |
| 影子模式差分 | `ShadowDiffer`：同一请求喂两个内核，逐字节比对，不一致只记录不提交 |
| 主线程提交 | 所有内核调用都发生在 `Terraria.On_*` 钩子内（即主线程），无跨线程状态 |
| 失败降级 | panic 在 Rust 侧 `catch_unwind`，跨 FFI 只返回错误码；C# 侧任何异常都不冒到主循环 |

## 仓库结构

```
crates/trit-rt/          Rust 内核 + C ABI（cdylib: trit_rt.dll / libtrit_rt.so）
  src/wire.rs            线格式：状态码、opcode、三值增量、拆装箱
  src/router.rs          事件路由（依赖图 + 脏标记 + 批处理）
  src/txn.rs             三进制事务日志（只追加 / 回滚 / 重放）
  src/arbiter.rs         跨模组写冲突仲裁（严格总线 / 多数计票）
  src/snapshot.rs        世界三进制张量投影的快照与差分
  src/prng.rs            确定性 PRNG（splitmix64）
  src/runtime.rs         装配体：dispatch(opcode, payload) 唯一入口
  src/ffi.rs             extern "C" 导出面，panic 全拦
dotnet/TritRuntime.Shim/ C# 兼容壳（零第三方依赖）
  src/Requests.cs        两个内核共用的请求构造器（保证同操作逐字节同请求）
  src/KernelResponseMapper.cs  两个内核共用的响应解码器
  src/ManagedKernel.cs   Rust 内核的 C# 逐分支移植（回退路径）
  src/RuntimeHost.cs     能力协商 + 原生/托管选择 + 失败回退
  src/ShadowDiffer.cs    影子差分器
  src/RuntimeSelfCheck.cs 端到端自检（事务→快照→差分→回滚）
  src/RuntimeShim.cs     门面 + Mod.Call 契约（20 个命令）
dotnet/TritRuntime.Shim.Tests/  xUnit 测试
mod/TritRuntimeMod/      tModLoader 模组：钩子、世界投影、Mod.Call 转发
tools/SignatureProbe/    反射探针：导出 tML 真实 API/钩子签名（不猜签名写钩子）
third_party/trit/        三进制库子模块 —— 只调用，不修改
```

## 快速开始

```bash
# 1. Rust 内核
cargo test -p trit-rt --release
cargo build --release -p trit-rt          # 产物 target/release/trit_rt.dll

# 2. C# 兼容壳 + 测试（143 个）
dotnet test dotnet/TritRuntime.Shim.Tests/TritRuntime.Shim.Tests.csproj

# 3. tModLoader 模组
dotnet build mod/TritRuntimeMod/TritRuntimeMod.csproj                        # 仅编译验证
dotnet build mod/TritRuntimeMod/TritRuntimeMod.csproj -p:BuildMod=true       # 打包 .tmod
```

模组细节、设置项、`Mod.Call` 用法见 [mod/TritRuntimeMod/README.md](mod/TritRuntimeMod/README.md)。

## 验证状态（诚实版）

| 项 | 状态 | 依据 |
|---|---|---|
| Rust 内核测试 | ✅ 130/130（98 单测 + 5 确定性 + 16 FFI + 11 运行时） | `cargo test -p trit-rt --release` |
| Rust clippy / fmt | ✅ 0 警告 / 干净 | `cargo clippy --all-targets`、`cargo fmt --check` |
| C# 兼容壳测试 | ✅ 152/152 | `dotnet test`，含原生 vs 托管逐字节对拍 |
| 原生 / 托管语义等价 | ✅ 快照字节、统计、PRNG 流、冲突诊断全部对拍 | `ParityTests` |
| 原生 / 托管**扫描结果逐字节等价** | ✅ 4 合法 + 5 非法 + 4 容错载荷，两侧 `JOB_POLL` 记录逐字节相同 | `ParallelApiTests.ManagedAndNativeScanResultsAgreeWhenV2NativeIsAvailable` |
| 非法请求语义统一 | ✅ 非法 trit / 未知 kind / 缺 pattern / 超容量两侧都产生 `failed` 任务，`error` 文案逐字一致、无前缀 | `ParallelApiTests` + `crates/trit-rt` 测试 |
| JSON 键序 / 浮点 / 编码一致 | ✅ 键序=书写序、`density` 定点 6 位、中文不转义 | ABI「JSON 键序、浮点格式与字符编码」节 + 字节级对拍 |
| 多 mod 结果隔离 | ✅ 按 owner 过滤，只出队真正返回的那几条 | `ParallelApiTests` |
| 原生库多实例隔离 | ✅ 每个实例只调用自己加载的模块 | `TwoNativeInstancesEachUseTheirOwnModule` |
| 已知能力差异 | 仅 `Capabilities` 一处：托管回退无 AI 投票（`caps` 少 `0x100` 位、`abi=0`） | 测试中逐字节钉死 |
| 模组编译 | ✅ 0 警告 0 错误（含 tML 游戏内编译口径，见下） | `dotnet build`、游戏内 `Build + Reload` |
| 模组 `.tmod` 打包 | ✅ 走 tML 官方管线，含 `lib/TritRuntime.Shim.dll` + `lib/Native/trit_rt.dll` + 三件套图标 | `-p:BuildMod=true`（需先关游戏） |
| 模组**进游戏实跑**（客户端） | ✅ 已跑通（本机 tML 1.4.4.9 / 2026.8.3.0） | 见下 |
| 模组**专用服务器实跑**（无头） | ✅ 装载 / 自检 / 原生库加载全通；`mode=Native` | 见下 |
| 服务器侧 tick 数据 | ❌ 未取得 | **已用探针定性**：无玩家连接时 `Main.GameUpdateCount` 两分钟一动不动（引擎行为），见下 |


### 真机实跑记录（2026-10-26）

在真实 tModLoader 客户端里装载并运行，日志实证：

```
[.NET TP Worker/INFO] [tML]: 加载程序集: TritRuntimeMod v0.1.0
[.NET TP Worker/INFO] [TritRuntimeMod]: [trit] 自检 snapshot=1 commit(applied=1,seq=1) step(intents=0)
                                        snapshot2=2 diff(0xF0FF0FFF=Pos) rollback=ok
                                        prng=[Pos,Neg,Pos,Pos] snapshot_bytes=33 stats=10 => PASS
[.NET TP Worker/INFO] [TritRuntimeMod]: [trit] 钩子安装完成：4/4
```

已确认：模组加载、`lib/TritRuntime.Shim.dll` 随包加载、装载期自检 **PASS**、
4 个钩子全部装上、原生内核缺失时**自动回退托管且游戏未崩**。

**这次实跑揪出 3 个真 bug，均已修复并加回归测试：**

| # | 问题 | 根因 | 修法 |
|---|---|---|---|
| 1 | 每快照周期抛 `非法字节 246：5 个 trit 只能表示 0..=242`，4 分钟刷出 3311 条异常栈 | 模组把 tick / log_seq 的**任意二进制字节**当影子签名传给 `ShadowRecord`。该 API 的 `byte[]` 语义是「每元素一个 trit 值 0/1/2」，内核逐元素转 `TritDelta` 后编码，246 越界 | 默认路径不再伪造签名；影子模式改用 `ShadowDiffer.CompareWorlds()` 做**真实对拍** |
| 2 | 日志 4 分钟写 13 MB / 8.9 万行 | `On_Main.Update` 每帧触发，菜单无步长限制（实测约 3000 FPS），把**帧数**当成了游戏 tick | 改用 `Main.GameUpdateCount` 驱动；周期性日志按墙上时钟节流（≥15 s 一条） |
| 3 | Rust 原生内核在真机上从未被用上 | `.tmod` 里只有托管 Shim，没打原生 `trit_rt.dll`；而 tML 的 `dllReferences` 只加载托管程序集 | 原生库打进 `lib/Native/`，运行时由 `NativeLibraryStager` 取出落盘再加载 |

> 修复过程中我先误判了一次：以为 `ShadowRecord(byte[])` 要的是**已编码**载荷，
> 于是给 `ShadowDiffer.Fingerprint` 加了预编码——结果原有 2 个测试立刻失败（双重编码）。
> 读两个内核的实现才确认语义是「每元素一个 trit 值」，已回退并补测试钉死。
> 这条现在写在 `ShadowDiffer.Fingerprint` 的注释里，免得再踩。

### 专用服务器实跑记录（2026-10-26，无头 `-server`）

客户端跑能验证「装得上」，但每次都要开 Steam GUI。专用服务器可以无头启动，
于是我自己闭环跑了一遍 —— 这一轮的价值是**证明 Rust 原生内核真的在真机上跑起来了**：

```
[trit] 已从模组包取出原生内核：...\TritRuntime\native\f5324a5d3721286d\trit_rt.dll
[trit] 运行时装配完成 mode=Native abi=1 caps=0x1FF arbitration=Majority
[trit] 自检 snapshot=1 commit(applied=1,seq=1) ... => PASS
[trit] 钩子安装完成：3/3
```

`mode` 从 `Managed` 变成 `Native`，`caps` 从 `0xFF` 变成 `0x1FF`（AI 投票位回归）——
上一轮修的「原生库随包分发」到这里才算真的闭环。

**同时又揪出 1 个 bug（第 4 个）：**

| # | 问题 | 根因 | 修法 |
|---|---|---|---|
| 4 | 原生库取出来了却加载失败：`Unable to load DLL 'trit_rt' (0x8007007E)` | 解析器登记名从落盘路径推导，落盘名是 `<hash>_trit_rt.dll`，于是登记成 `<hash>_trit_rt`；而 `DllImport` 用的是常量 `"trit_rt"`，**永不命中**。测试进程里 dll 恰好就叫 `trit_rt.dll`，所以这个组合长期没被覆盖 | 登记改用 `NativeBindings.LibraryName` 常量；落盘改为 `native/<hash>/trit_rt.dll`（摘要降为目录名，文件名保持原样） |

**还发现一个「装好了但全程空转」的静默故障：**

服务器上跑 100+ 秒，一条 tick 日志都没有。这一条后来**复查时推翻并重做了**（见下节）：
那一轮 `serverconfig.txt` 里的 `world=` 是注释掉的，服务器停在选世界界面（`Main.gameMenu=true`），
所以"服务器不跑世界更新循环"这个结论当时其实没有被真正验证过。

### 专用服务器复查（2026-10-29）：用探针把结论钉死

带上 `-world` 载入真实世界重跑，并且**先补一条诊断**——原来的日志分不出这两种情况：

- (a) `PostUpdateEverything` 压根没被调用；
- (b) 回调到了，但被我们自己的 `Main.gameMenu` 判断 early-return 掉。

两者在日志里长得一模一样（都是"一条 tick 数据都没有"），上一次就是在这里误判的。
现在 `TritWorldSystem` 会在"回调到了但还在菜单"时打一条明确日志。

然后又加了一个**临时后台采样线程**（独立于 ModSystem 回调，验完即删），每 3 秒读一次
`Main.GameUpdateCount` 与 `PostUpdateEverything` 的实际调用次数。结果：

```
t=0..2   GameUpdateCount=0  delta=0   gameMenu=True   PostUpdateEverything 次数=0   ← 还在载入
t=3..39  GameUpdateCount=0  delta=0   gameMenu=False  PostUpdateEverything 次数=0   ← 世界已载入，两分钟一动不动
```

**结论（这次有直接证据，不是推断）**：世界确实载入了（4200×1200、`gameMenu=False`、已开始监听），
但 `Main.GameUpdateCount` 在整整两分钟里**一次都没推进**，`PostUpdateEverything` 也一次没到。
所以是**引擎在无玩家连接时不跑世界更新循环**，不是 tML 没把回调接到服务器端——
**驱动方式不用改**（`ModSystem` 的选择是对的），要拿服务器 tick 数据必须有玩家连进来。

顺带记录两个排查中踩的坑：

- `serverconfig.txt` 默认只开了 `priority=1`，`world=` 是注释掉的。注释里明写着
  "unless a world is specified, some settings … will be ignored"——不指定世界时服务器停在选世界界面，
  看起来"服务起来了"其实根本没进世界。
- tML 服务器的控制台**不认 `time` 命令**（回 `: Invalid command.`），
  想探世界时间在不在走不能靠它。

tick 驱动因此从 MonoMod 钩子改为官方 `ModSystem`（不依赖内部方法签名，客户端服务器一致），
并补了两条可观测性日志（首次采样确认、`GameUpdateCount` 恒定时告警）——
没有这类日志，「装好了但不动」只能靠猜。服务器侧的 tick 数据留 issue 跟踪，等有玩家连接再验。

### 真机实跑记录（2026-10-29，客户端 + 游戏内 Build + Reload）

带全套大模组（Calamity / Thorium / Fargowiltas / HEROsMod …）进游戏实测，日志实证整条链路：

```
[trit] 已从模组包取出原生内核：...\TritRuntime\native\b32c030ac9ff813d\trit_rt.dll
[trit] 运行时装配完成 mode=Native abi=3 caps=0xBFF arbitration=Majority
[trit] 自检 snapshot=1 commit(applied=1,seq=1) ... => PASS
[trit] 钩子安装完成：3/3（tick 驱动由 ModSystem 提供，不占钩子名额）
[trit] 世界投影开始采样，game_tick=1
[trit] 快照 #23 id=26 tick=6900 log_seq=1957772 applied=549
[trit] 统计 tick=6600 mode=Native counters[...] net_total=2202
```

即：随包原生库取出落盘 → 装载期自检 PASS → 采样/提交/快照/统计全程无一条 `Guarded` 报错。

这一轮顺带查掉的四个真问题：

| # | 现象（日志原文） | 根因 | 处理 |
|---|---|---|---|
| 1 | 游戏内 `Build` 报 **70 条 CS8632 警告**，本地 `dotnet build` 却是 0 警告 | tML 游戏内编译**不读** csproj 的 `<Nullable>`，源码里所有 `?` 注解被静默忽略 | 每个 `.cs` 首行加 `#nullable enable`（csproj 的属性对游戏内编译无效）；CI 加一步逐文件检查防回退 |
| 2 | `WARN [FNA]: Image loading failed: unknown image type`（打包期） | `icon_small.png` 最初按 80×80 出的，规格要求 **30×30**；但换成正经 30×30、甚至换成 `icon_workshop.png` 的内容仍然报——**与文件内容无关，是按文件名触发的**（tML 2026.8.3.0 自身行为；其它模组根本不带这个文件，所以只有我们会撞上） | 图标仍按规格出（icon 80×80 / icon_small 30×30 / icon_workshop 512×512），警告定性为 tML 侧噪声，记录在案 |
| 3 | `WARN: Failed to load icon_small.png. Reason: does not exist` | 同上，缺文件时人人都报（Calamity / Thorium / HEROsMod 全在报） | 已补文件，本模组不再报这条 |
| 4 | `TritRuntimeMod AssemblyLoadContext still using memory`（卸载后） | 与别的模组那条 `mod class still using memory` 不同，是 ALC 级。**已用实验否掉头号假设**：见下 | 已知未修：只影响开发期热重载，功能与内存均无影响（内核与钩子在 `Unload` 里都已释放） |

**关于 #4，做过一次定向实验（结论是"不是我们"）：** 头号怀疑对象是「原生库句柄没 `NativeLibrary.Free`」——
`NativeApi.Dispose` 刻意不卸载模块（卸载后别的实例持有的函数指针会悬空），而 `NativeLibrary.Load`
会把模块绑到**调用方程序集所在的 ALC**，`trit_rt.dll` 正是从兼容壳里加载的。
于是写了最小复现（可收集 ALC → 在其中 `Load(trit_rt.dll)` → `Unload()` → 20 轮强制 GC → 弱引用是否还活）：

| 组 | 操作 | ALC 卸载成功 |
|---|---|---|
| A | 基线：不加载原生库 | ✅ 卸载（**证明探针有分辨力**） |
| B | 加载后**从不** `Free` | ✅ 卸载 |
| C | 加载后 `Free` | ✅ 卸载 |
| D | 显式绑 ALC 后不 `Free` | ✅ 卸载 |

四组句柄都是同一个真实模块基址（`0x7FFFAA140000`），加载确实发生了。
**结论：保留原生库句柄不阻止 ALC 卸载，"刻意不 Free"的决定因此仍然成立**——
Free 了也不会改善，只会引入悬垂函数指针的风险。剩下的是 tML 自己的缓存与 GC 时机，不在我们能改的范围。

（第一版探针四组全报"仍存活"，是**探针本身的错**：`Run` 的局部变量 `Assembly`/`MethodInfo`
在栈帧里就把 ALC 钉住了。改成收集 GC 在栈帧退出之后做，基线立刻变 False，实验才有分辨力。
这个坑值得记：验证"能不能卸载"时，验证代码自己往往就是那个引用。）

另外日志里出现过一次 `KeyNotFoundException: CalamityMod.NPCs.CalamityGlobalNPC`，
栈顶有 `TritRuntimeMod.TritHooks`——**不是本模组的问题**：异常来自 `CalamityMod...AresBody.AI()`，
只是我们的钩子先调 `orig` 所以出现在栈上；tML 以「静默捕获异常」处理掉了，未影响游戏。

## 落地顺序对照

| 步骤 | 状态 |
|---|---|
| 1. C# Shim + `Mod.Call` 契约 | ✅ 20 个命令，强类型 + 字符串两套入口 |
| 2. Rust 最小内核 | ✅ 路由 / 事务 / 快照 / 差分 / 仲裁 / PRNG / 编解码 / 影子 |
| 3. 1~2 个热点 Harmony 钩子 + 影子差分 | ✅（用 tML 的 `On_*` 而非 Harmony，见下）`On_NPC.AI` / `On_Projectile.Update` / `On_NetMessage.SendData`；tick 驱动用官方 `ModSystem.PostUpdateEverything`（`Main.Update` 在服务器端不触发，实测） |
| 4. 一致后切默认、保留回退 | ⏳ 未切：当前**只观测不回写**，不接管任何原版逻辑 |
| 5. 依赖图调度 / 网络压缩 / 回滚 / 热重载 | 🟡 调度与回滚的内核能力已就绪；网络压缩只有观测统计 |

## 三进制语义

`-1` 减少 / 移除，`0` 不变 / 弃权 / 未驱动（总线高阻），`+1` 增加 / 设置。

`Ternary::index()` 是 0/1/2，与 C# 侧 `TritDelta{Neg=0, Zero=1, Pos=2}` 完全一致；
`apply_delta(current, delta) = clamp(index(current) + index(delta) - 1, 0, 2)`。

在总线语义下：0 个驱动 → 高阻；1 个驱动 → 其值；≥2 个驱动 → 冲突（即使同向），
一次性列出该车道全部驱动。这是跨模组写意图仲裁的基础。

## 几个容易踩的坑（已固化到代码里）

1. **钩子形态**：本机 tModLoader 1.4.4.9 生成的是 `Terraria.On_*` / `Terraria.IL_*`（各 1036 个），
   不是旧文档里的 `On.Terraria.*`。后者在本版本**编译不过**。签名全部由 `tools/SignatureProbe` 反射导出。
2. **附加程序集**：tML 只从 `.tmod` 内的 `lib/<名字>.dll` 加载附加程序集（要配 `build.txt` 的
   `dllReferences`）。references 拷贝到输出目录并不够——漏了这步，运行时就是 `TypeLoadException`。
   原生 DLL 不能走这条路（`dllReferences` 只认托管程序集），只能当普通资源打包后运行时取出落盘。
3. **原生库必须按实例绑定，不能靠静态 `DllImport`**：
   `.NET` 会**缓存** DllImport 的首次解析结果，`SetDllImportResolver` 又只能装一次、
   只存一个句柄。所以「先加载 trit_rt，再 `Load(别处的 dll)`」会**静默用回第一个模块**——
   指定路径形同虚设，ABI 校验读的也是别的模块的版本。
   现在所有带句柄的调用都走 `NativeApi`（每个实例持有自己那份模块的导出函数指针）。
   详见 [Issue #3](https://github.com/bfxh/trit-tml/issues/3)。
4. **原生库文件名不能改**：`SetDllImportResolver` 登记的名字必须等于 `DllImport` 声明里的常量。
   若把库落盘成 `<hash>_trit_rt.dll` 并从路径推导登记名，就与常量 `"trit_rt"` 对不上，
   解析器永不命中，表现为 `Unable to load DLL (0x8007007E)`。要版本隔离就改**目录**名，别改文件名。
   （按实例绑定之后这条约束变松了：导出函数名与文件名无关，`NativeApi` 只认「这个模块有没有
   `tt_rt_*` 导出」。但兼容壳里残留的进程级静态 `DllImport` 仍受这条约束。）
5. **服务器不调用 `Main.Update`**：专用服务器走 `Main.DedServ` 自己的循环，
   `On_Main.Update` 挂上去不会触发；且无玩家连接时世界更新循环压根不跑（连 `ModSystem` 回调也不到）。
   需要每 tick 驱动就走官方 `ModSystem`，别 hook 内部方法。
6. **多 mod 不能抢彼此的任务结果**：旁路任务池是全局的，多个 mod 共用。
   `JOB_POLL` 支持按 `owner` 过滤，且**只出队真正返回的那几条**——
   其他 owner 的结果留在队列里。过滤与截断的顺序是「先过滤再截断」，
   否则队头被别人的任务占满时，该 owner 的结果会被挤掉。
7. **tML 游戏内编译不读 csproj 的 `<Nullable>`**：本地 `dotnet build` 0 警告，
   游戏内 `Build + Reload` 却能报出 70 条 CS8632——因为 tML 用自己的编译参数编源码，
   csproj 里的可空上下文没带过去，源码里所有 `?` 注解被当成无效注解忽略。
   要两边都干净，只能在**每个 `.cs` 文件里**写 `#nullable enable`。
8. **`icon_small.png` 必须 30×30**（bestiary 过滤图标，与 80×80 的 `icon.png` 不同规格）；
   tML 打包时会为它打一条 `Image loading failed: unknown image type`——实测与文件内容无关
   （换成任何合法 PNG 都报），属 tML 侧噪声，不影响打包结果。
9. **错误日志不能复用成功路径的节流槽**：`MaybeReportStats` 原本先 `Throttle(ref lastStatsLogAt)`
   决定"打不打统计"，再 `try { Stats() } catch { Throttle(ref lastStatsLogAt) }` 决定"打不打失败告警"——
   同一个槽在几微秒前刚被推进到"现在"，catch 里那次必然返回 false，
   结果是**非 verbose 下"读取统计失败"永远打不出来**。错误日志必须各用独立的节流器。
10. **设置里"采样上限"必须两头都夹**：`WorldBridge` 按 `new EntityState[MaxNpcs]` 开定长数组，
    装载期一次性分配。只夹负数、不夹上界的话，设置文件里一个 `2000000000` 就是 80 GB 的分配请求，
    表现为"模组本次会话不提供服务"——根因却只是一个数字打错。


## 不做什么

- 不替换 tModLoader 的 C#/CLR/Unity 主循环。
- 不修改 `third_party/trit`（子模块，只调用）。
- 不在当前阶段改变任何游戏行为（只观测、只记录、不提交回写）。
- 不为了"性能"跳过 orig —— 每个钩子都恰好调用一次 `orig`。

## 许可

MIT，见 [LICENSE](LICENSE)。
