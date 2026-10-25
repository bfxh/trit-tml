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

# 2. C# 兼容壳 + 测试（107 个）
dotnet test dotnet/TritRuntime.Shim.Tests/TritRuntime.Shim.Tests.csproj

# 3. tModLoader 模组
dotnet build mod/TritRuntimeMod/TritRuntimeMod.csproj                        # 仅编译验证
dotnet build mod/TritRuntimeMod/TritRuntimeMod.csproj -p:BuildMod=true       # 打包 .tmod
```

模组细节、设置项、`Mod.Call` 用法见 [mod/TritRuntimeMod/README.md](mod/TritRuntimeMod/README.md)。

## 验证状态（诚实版）

| 项 | 状态 | 依据 |
|---|---|---|
| Rust 内核测试 | ✅ 42/42 | `cargo test -p trit-rt --release` |
| Rust clippy / fmt | ✅ 0 警告 / 干净 | `cargo clippy --all-targets`、`cargo fmt --check` |
| C# 兼容壳测试 | ✅ 110/110 | `dotnet test`，含原生 vs 托管 51 步逐字节对拍 |
| 原生 / 托管语义等价 | ✅ 快照字节、统计、PRNG 流、冲突诊断全部对拍 | `ParityTests` |
| 已知能力差异 | 仅 `Capabilities` 一处：托管回退无 AI 投票（`caps` 少 `0x100` 位、`abi=0`） | 测试中逐字节钉死 |
| 模组编译 | ✅ 0 警告 0 错误（Debug/Release） | `dotnet build` |
| 模组 `.tmod` 打包 | ✅ 走 tML 官方管线，含 `lib/TritRuntime.Shim.dll` + `lib/Native/trit_rt.dll` | `-p:BuildMod=true`（需先关游戏） |
| 模组**进游戏实跑**（客户端） | ✅ 已跑通（本机 tML 1.4.4.9） | 见下 |
| 模组**专用服务器实跑**（无头） | ✅ 装载 / 自检 / 原生库加载全通；`mode=Native` | 见下 |
| 服务器侧 tick 数据 | ❌ 未取得 | 无玩家连接时服务器不跑世界更新循环（引擎行为），见 issue |

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

服务器上跑 100+ 秒，一条 tick 日志都没有。逐层二分定位：

1. `Terraria.Main.Update(GameTime)` **在服务器端从不触发**（服务器走 `Main.DedServ` 自己的循环）
2. 换成官方 `ModSystem.PostUpdateEverything()` 后**依然不触发**；已确认 `TritWorldSystem`
   被 tML 实例化（`OnModLoad` 日志打出来了），回调却一次没到
3. 服务器 85 秒内日志零世界更新 → **无玩家连接时服务器不跑世界更新循环**，是引擎行为

tick 驱动因此从 MonoMod 钩子改为官方 `ModSystem`（不依赖内部方法签名，客户端服务器一致），
并补了两条可观测性日志（首次采样确认、`GameUpdateCount` 恒定时告警）——
没有这类日志，「装好了但不动」只能靠猜。服务器侧的 tick 数据留 issue 跟踪，等有玩家连接再验。

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

## 两个容易踩的坑（已固化到代码里）

1. **钩子形态**：本机 tModLoader 1.4.4.9 生成的是 `Terraria.On_*` / `Terraria.IL_*`（各 1036 个），
   不是旧文档里的 `On.Terraria.*`。后者在本版本**编译不过**。签名全部由 `tools/SignatureProbe` 反射导出。
2. **附加程序集**：tML 只从 `.tmod` 内的 `lib/<名字>.dll` 加载附加程序集（要配 `build.txt` 的
   `dllReferences`）。references 拷贝到输出目录并不够——漏了这步，运行时就是 `TypeLoadException`。
   原生 DLL 不能走这条路（`dllReferences` 只认托管程序集），只能当普通资源打包后运行时取出落盘。
3. **原生库文件名不能改**：`SetDllImportResolver` 登记的名字必须等于 `DllImport` 声明里的常量。
   若把库落盘成 `<hash>_trit_rt.dll` 并从路径推导登记名，就与常量 `"trit_rt"` 对不上，
   解析器永不命中，表现为 `Unable to load DLL (0x8007007E)`。要版本隔离就改**目录**名，别改文件名。
4. **服务器不调用 `Main.Update`**：专用服务器走 `Main.DedServ` 自己的循环，
   `On_Main.Update` 挂上去不会触发；且无玩家连接时世界更新循环压根不跑（连 `ModSystem` 回调也不到）。
   需要每 tick 驱动就走官方 `ModSystem`，别 hook 内部方法。

## 不做什么

- 不替换 tModLoader 的 C#/CLR/Unity 主循环。
- 不修改 `third_party/trit`（子模块，只调用）。
- 不在当前阶段改变任何游戏行为（只观测、只记录、不提交回写）。
- 不为了"性能"跳过 orig —— 每个钩子都恰好调用一次 `orig`。

## 许可

MIT，见 [LICENSE](LICENSE)。
