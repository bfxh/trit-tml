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
| C# 兼容壳测试 | ✅ 107/107 | `dotnet test`，含原生 vs 托管 51 步逐字节对拍 |
| 原生 / 托管语义等价 | ✅ 快照字节、统计、PRNG 流、冲突诊断全部对拍 | `ParityTests` |
| 已知能力差异 | 仅 `Capabilities` 一处：托管回退无 AI 投票（`caps` 少 `0x100` 位、`abi=0`） | 测试中逐字节钉死 |
| 模组编译 | ✅ 0 警告 0 错误（Debug/Release） | `dotnet build` |
| 模组 `.tmod` 打包 | ✅ 走 tML 官方管线，含 `lib/TritRuntime.Shim.dll` | `-p:BuildMod=true` |
| 模组**进游戏实跑** | ❌ 未验证 | 需要 Steam 启动 tModLoader；装载期自检会把结果写日志 |

最后一项是当前最大的缺口：`.tmod` 已产出并落到 `Mods/`，但还没有一次真实的游戏内运行记录。
`RuntimeSelfCheck` 就是为这一步准备的——首次启动会在日志里打印
`[trit] 自检 snapshot=... => PASS`，失败也只是降级不装钩子。

## 落地顺序对照

| 步骤 | 状态 |
|---|---|
| 1. C# Shim + `Mod.Call` 契约 | ✅ 20 个命令，强类型 + 字符串两套入口 |
| 2. Rust 最小内核 | ✅ 路由 / 事务 / 快照 / 差分 / 仲裁 / PRNG / 编解码 / 影子 |
| 3. 1~2 个热点 Harmony 钩子 + 影子差分 | ✅（用 tML 的 `On_*` 而非 Harmony，见下）`On_Main.Update` / `On_NPC.AI` / `On_Projectile.Update` / `On_NetMessage.SendData` |
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

## 不做什么

- 不替换 tModLoader 的 C#/CLR/Unity 主循环。
- 不修改 `third_party/trit`（子模块，只调用）。
- 不在当前阶段改变任何游戏行为（只观测、只记录、不提交回写）。
- 不为了"性能"跳过 orig —— 每个钩子都恰好调用一次 `orig`。

## 许可

MIT，见 [LICENSE](LICENSE)。
