# TritRuntimeMod —— tModLoader 旁路三进制运行时（模组侧）

tModLoader 的 C# / CLR / Unity 主循环**原样保留**。这个模组只做旁路：

```
其他模组 ──原样调用──> tModLoader C# API
                          │
                          ├─> (不变) 原运行时继续跑
                          │
                          └─> Terraria.On_* 钩子（只观测）
                                    │  每 tick 采样：NPC / Projectile / Player 状态变化
                                    ▼
                              C# Runtime Shim（薄层）
                                    │  Mod.Call 契约 + 能力协商 + 影子差分
                                    ▼
                          Rust 三进制内核 trit_rt.dll
                          （事件路由 / 事务日志 / 快照 / 差分 / PRNG）
                                    │  纯数据 + 句柄，由主线程提交
                                    ▼
                              绝无回写游戏状态
```

原生库不可用时自动回退到**语义逐字节等价**的托管内核（同一套 `Requests` / `KernelResponseMapper`，
有 107 个测试钉住，含原生 vs 托管 51 步逐字节对拍）。回退不抛异常、不改变 API 表面。

## 编译

```bash
# 仅编译验证（不触发 .tmod 打包）
dotnet build mod/TritRuntimeMod/TritRuntimeMod.csproj

# 完整打包成 .tmod（走 tML 自己的构建管线）
dotnet build mod/TritRuntimeMod/TritRuntimeMod.csproj -p:BuildMod=true
```

`TMLInstallDir` 默认指向本机的 `D:\SteamLibrary\steamapps\common\tModLoader\`，
换机器用 `-p:TMLInstallDir="E:\...\tModLoader\"` 覆盖（必须带结尾分隔符）。

原生内核先构建：

```bash
cargo build --release -p trit-rt     # 产物 target/release/trit_rt.dll，会自动拷到输出目录
```

## 原生库放哪

按以下顺序探测，找不到就回退托管内核：

1. `TritRuntimeMod.settings.json` 里的 `libraryPath`
2. 环境变量 `TRIT_RT_LIBRARY`
3. 兼容壳目录 / 进程入口目录（即 tML 安装根目录）

把 `trit_rt.dll` 丢进 tML 安装根目录是最省事的做法。

## 设置

首次运行会在 tML 存档根目录生成 `TritRuntimeMod.settings.json`：

| 键 | 默认 | 含义 |
|---|---|---|
| `enableHooks` | `true` | 是否安装观测钩子 |
| `shadowMode` | `false` | 双内核逐字节差分（上线切换前的必经阶段，约双倍内核开销） |
| `forceManaged` | `false` | 强制托管内核（诊断用） |
| `libraryPath` | `""` | 原生库显式路径 |
| `maxNpcs` / `maxProjectiles` / `maxPlayers` | 256 / 256 / 16 | 每 tick 采样上限 |
| `snapshotIntervalTicks` | 300 | 快照间隔，0 = 关闭 |
| `statsIntervalTicks` | 600 | 统计输出间隔，0 = 关闭 |
| `verboseLogging` | `false` | 明细日志 |
| `maxConsecutiveHookFailures` | 5 | 连续异常多少次后停用观测层 |

设置解析失败一律退回默认值并在日志里说明原因——旁路层的设置不允许阻止游戏启动。

## 其他模组怎么用

只需要 tML 原生的 `Mod.Call`，**不要**引用本模组的类型：

```csharp
Mod mod = ModLoader.GetMod("TritRuntimeMod");

// 能力协商：先问能不能，再决定是否接管
var caps = mod.Call("TritRuntime.Capabilities");

// 事务：一批写意图要么全落地要么全失败
ulong txn = (ulong)mod.Call("TritRuntime.BeginTransaction");
mod.Call("TritRuntime.SubmitIntent", txn, laneKey, -1);   // -1/0/+1，或 "Neg"/"Zero"/"Pos"
mod.Call("TritRuntime.SubmitIntent", txn, laneKey2, 1);
var commit = mod.Call("TritRuntime.Commit", txn);

// 仲裁：把多方的写意图放在一起算冲突
var arbitrated = mod.Call("TritRuntime.Arbitrate", intents);

// 快照 / 差分 / 回滚
ulong snap = (ulong)mod.Call("TritRuntime.Snapshot");
var diff = mod.Call("TritRuntime.Diff", snap, otherSnap);
mod.Call("TritRuntime.Rollback", snap);

// 确定性 PRNG（同 seed 同 tick 同序列，可回放）
var trits = mod.Call("TritRuntime.NextRandom", 8);

// 事件路由
ulong sub = (ulong)mod.Call("TritRuntime.Subscribe", modId, eventId, priority, deps);
mod.Call("TritRuntime.Publish", eventId, payload);
mod.Call("TritRuntime.Step", tick);
```

命令名大小写不敏感，`TritRuntime.` 前缀可省略。参数个数与类型不符会抛 `ArgumentException`；
运行时未装配时抛 `InvalidOperationException`。**调用方要按 tML 惯例 try/catch**——
本层保证"要么返回结果，要么抛可预期异常"，绝不静默返回错值。

## 车道键编码（ABI）

一个 `uint`：`[31..28] 种类 | [27..12] 槽位 | [11..0] 字段`

- 种类：`1` NPC、`2` Projectile、`3` Player、`0xF` 保留给自检探针
- 字段：`0x001` life、`0x002` lifeMax、`0x003` active、`0x004` mana、
  `0x005/0x006` position.X/Y、`0x007/0x008` velocity.X/Y、`0x009` direction

三值的语义是**增量方向**，不是绝对值：`-1` 减少、`0` 不变/不驱动、`+1` 增加。
相等不产生意图（`0` 是"未驱动"即总线高阻，提交它只会制造噪音）。

## 自检

装载时会跑一次端到端闭环（`RuntimeSelfCheck`）：快照 → 事务提交 → Step → 差分 →
回滚 → PRNG → 快照字节 → 统计。探针车道 `kind=0xF` 与世界投影不相交，闭环结束回滚到基线。

成功后日志里会有：

```
[trit] 自检 snapshot=1 commit(applied=1,seq=1) step(intents=0) snapshot2=2
       diff(0xF0FF0FFF=Pos) rollback=ok prng=[Pos,Zero,Neg,Pos]
       snapshot_bytes=... stats=10 => PASS
```

**自检失败会怎样**：只记 error 日志、不装钩子，游戏继续跑，`Mod.Call` 仍然可用（只读能力）。
绝不会因为旁路层出问题而让游戏起不来。

## 已知边界

- **只观测，不回写**。当前版本不接管任何原版逻辑，不改变游戏行为。这是刻意的第一步：
  先把"可观测、可差分、可回退"做实，再谈接管。
- **事务日志只追加**。自检与历史提交的条目会留在日志里，`replay_from` 会看到它们。
- **托管回退没有 AI 投票能力**（三值权重网络推理留在 Rust 侧），能力位图里会如实反映。
- **截断**：NPC 按槽位索引取前 `maxNpcs` 个，超出部分不投影（避免车道爆炸）。
- 钩子形态 `Terraria.On_*` 是本机 tML 1.4.4.9 实测结果（1036 个 `On_` + 1036 个 `IL_`）；
  旧的 `On.Terraria.*` 写法在本版本**编译不过**，别照抄老文档。
