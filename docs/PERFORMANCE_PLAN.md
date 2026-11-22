# 性能与内存优化方案：加载链路 + Rust 三进制/二进制编码

> **状态**：方案文档，本轮不含任何代码改动。
> **基线数据来源**：2026-11-17 02:08–02:13 的 `tModLoader-Logs/client.log`，以及本机已装模组体积扫描。
> **前置文档**：[TAKEOVER_PLAN.md](./TAKEOVER_PLAN.md)（接管档位）、[ABI.md](./ABI.md)（线协议）。

---

## 0. 结论先行

按「收益 ÷ 风险」排序，真正值得做的只有三件事：

| 优先级 | 问题 | 现状 | 目标 | 风险 |
|---|---|---|---|---|
| **P0** | 稳态每 tick 的托管分配 | 每次 tick 至少 3 个 `List`/`Dictionary` + 若干 records | **稳态 0 B/tick** | 极低，纯复用缓冲 |
| **P0** | 系统内存被其它进程吃光 | 全进程 12.7 GB / 15.4 GB，可用仅 2.6 GB | 可用 ≥ 4 GB | 无（关程序/减 mod） |
| **P1** | 三进制世界投影按 1 字节/trit + `BTreeMap` | 实际约 8–10 B/trit，且 fork 再克隆一份 | **2 bit/trit**，内存 ↓ 30–40× | 中，需要改快照格式 |
| **P1** | `STATE_PUSH` 每条 83 字节 + 每 tick 重建 `BTreeMap` | 200 实体 ≈ 16.6 KB/tick、200 次堆分配/tick | **≤ 32 B/条、0 次分配/tick** | 中，需要协议 v2 |
| **P2** | `refresh_world()` 每 tick 整份 clone + 重建 `BTreeSet` | O(n) 拷贝 ×2 + 每键 `Vec<bool>` | 增量刷新 / 分页 SoA | 中高 |
| **P2** | 首次加载 21.7 s | JIT + 资产解码 + IL hook 一次性成本 | ≤ 12 s | 低（主要是等待上游） |

**一句话**：本模组自身只占 4.4 MB，不是内存元凶；真正臃肿的是「系统里别的进程 + 同时加载的大 mod 资产」，而 Rust 内核的问题是**长期的分配抖动和编码密度**，不是当前绝对占用。

---

## 1. 实测基线（可复现）

### 1.1 加载耗时

| 场景 | 日志行 | 耗时 |
|---|---|---|
| 冷启动首次加载（启用全部 mod） | `Mod Load Completed in 21697ms` | **21.7 s** |
| 同一批 mod 热重载 | `Mod Load Completed in 641ms` | **641 ms** |
| 启用新 mod 后重载 | `Mod Load Completed in 2372ms` | 2.37 s |
| 只留少量 mod 重载 | `Mod Load Completed in 1245ms` | 1.25 s |

**推断**：21.7 s 里约 **97% 是一次性成本**（程序集 JIT、纹理/音效解码、IL hook 织入、模组静态构造）。热重载 641 ms 说明「读文件 + 建 mod 对象」本身很便宜。因此加载优化的重点不是 IO，而是**减少首帧前的同步工作量**。

### 1.2 资产阻塞告警（53 次）

`spent Xms blocking on asset loading. Avoid using AssetRequestMode.ImmediateLoad` 共出现 **53 次**，Top 15：

| 模组 | 单次阻塞 |
|---|---|
| CalamityMod | 472 ms / 325 ms / 266 ms |
| Luminance | 447 ms / 199 ms |
| CalValEX | 369 ms |
| CalamityEntropyLegacy | 329 ms / 91 ms |
| BossChecklist | 252 ms |
| ImproveGame | 193 ms |
| FargowiltasSouls | 156 ms |
| HEROsMod | 120 ms / 97 ms |
| CatalystMod | 114 ms |
| miningcracks_take_on_luiafk | 97 ms |
| **TritRuntimeMod** | **21 / 28 / 27 ms** |

这些是**串行累加**的：把 Top 10 相加就超过 2 s，且全部发生在主线程/加载线程的同步路径上。

### 1.3 内存

| 指标 | 数值 |
|---|---|
| tML 进程物理内存 | 2.1 – 2.4 GB |
| tML 虚拟内存 | 3.0 – 3.1 GB |
| 全系统进程占用 | 12.7 – 12.8 GB |
| 系统可用 | **2.6 GB** |
| 物理内存总量 | 15.4 GB |
| Mods 目录总占用 | 0.17 GB（含子目录 `ImproveGame`） |
| 单文件最大 | `CalamityEntropyLegacy.tmod` **169.68 MB** |

按加载会话统计的模组内存：

| 会话 | Top 模组 |
|---|---|
| 02:09:48 | TritRuntimeMod **4.4 MB** |
| 02:12:04 | FargowiltasSouls **224.2 MB**、NecromancerRise 45.3 MB、Fargowiltas 12.4 MB |
| 02:13:46 | NecromancerRise 54.2 MB、HEROsMod 9.5 MB、Luminance 5.3 MB |

**结论**：模组加起来最多约 280 MB，tML 的 2.2 GB 里大头是**原版资源 + FNA/音频 + JIT 代码 + 世界数据**。所以「内存臃肿」的第一责任方在系统侧（12.7 GB 全进程），第二责任方是**同时加载的大 mod 组合**，不在本模组。

> 换机建议（最直接）：24 核 / 15.4 GB 属于「CPU 过剩、内存偏紧」的典型配置。开 tML + Calamity 全家桶时，浏览器/Electron 应用会直接抢走 6–10 GB。先把可用内存拉到 4 GB 以上，比任何代码优化都有效。

### 1.4 新日志补充实测（02:13–02:21，含 3 次进程内重载）

| 观察项 | 数值 | 含义 |
|---|---|---|
| `Mod Load Completed` | 4 次（3 次重载） | 全程未重启进程 |
| tML 物理内存 | 2.1 GB → **2.4 GB** | 3 次重载净增约 300 MB |
| tML 虚拟内存 | 3.0 GB → 3.1 GB | 同上 |
| `AssemblyLoadContext still using memory` | **99 条 = 33 个模组 × 3 次** | 每次热重载整套模组程序集都卸不掉 |
| 本模组日志占比 | **24,285 / 34,141 行 = 71.1%** | 7 分钟写出 3.85 MB，其中「派发」23,877 行 |
| 校准样本 | `autoSamples=1892`、`autoConsistent=1465` | 一致率 **77.4%**（未达 95% 升级线） |
| 日志上限 | `maxLogEntries=1048576` | 按 5 B/条 ≈ **5 MB** 常驻 |
| 详细日志开关 | `verboseLogging=true`、`logJobResults=true` | 逐 tick 打日志的直接原因 |

两个新结论：

1. **热重载是内存泄漏源**：33 个模组（包括本模组、CalamityMod、Luminance 等）在每次重载后 ALC 仍被持有。这不是单个模组的问题，而是 tML 热重载的共性行为。**换 mod 后重启进程比 Reload Mods 更省内存**；反复重载 3 次就多占约 300 MB。
2. **逐 tick 日志是当前最大的可控分配/IO 源**：23,877 行「派发」日志来自 `WorldBridge.PublishAndStep` 里未节流的逐 intent 循环，每行一次字符串插值 + 一次文件写。已按 P0 改为「每 60 tick 一条汇总」（保留 `first=sub/ev` 诊断字段），详见 §7 P0。

---

## 2. 安装 / 加载链路拆解与优化

### 2.1 tML 的加载阶段

```
启动进程
  → Finding Mods...            ← 扫描 Mods 目录 + 读 .tmod 头（重复出现 9 次）
  → 解析依赖 / 排序 Load Order
  → 逐 mod: 加载程序集 → 静态构造 → Load()
  → 资产生成（纹理 → GPU、音效 → XACT）
  → IL hook / 内容注册（GlobalNPC / GlobalItem / GlobalProjectile）
  → PostSetupContent → 配方 → Finalize
  → Mod Load Completed in Xms
```

三个可优化点：

1. **Finding Mods 重复扫描**：单次冷启动里出现 9 次 `Finding Mods...`，每次都要打开目录、读 `.tmod` 头、算依赖。Mods 目录越小越快；`CalamityEntropyLegacy.tmod` 单个 170 MB，头部扫描 + 后续解包都不便宜。
2. **资产在加载期同步解码**：53 次告警全部来自 `ImmediateLoad`。calling mod 把纹理强行在 `Load()` 阶段加载，直接阻塞加载线程。
3. **IL hook 织入**：每个 `GlobalNPC`/`GlobalItem` 实例都会生成跳板。`GlobalNPC registration stats. Count: 2, Slots per Entity: 310` 说明**每个 NPC 实体背后挂了 310 个槽位**（见 §3.2）。

### 2.2 本模组（TritRuntimeMod）可做的加载优化

| 优化项 | 现状 | 做法 | 预期收益 |
|---|---|---|---|
| 资产阻塞 21–28 ms | 源码内**没有**任何 `AssetRequestMode` / `ModContent.Request` / `GetTexture`；图标 `icon.png` 0.8 KB、`icon_small.png` 0.4 KB | 先加计时打点确认是谁在加载（图标/`icon_workshop.png` 5.3 KB/`lib/Native` 探测），再决定精简 | ≤ 5 ms |
| 原生库抽取 | `NativeLibraryStager` 已按 `SHA256` 前 8 字节 + 文件长度做缓存，命中即跳过写盘 | **不动**。已是最优形态 | — |
| 装配期工作 | `Attach()` → 自检 → 装钩子，全部在 `Load()` 内 | 自检可挪到**首次进世界**（`OnWorldLoad`）后台跑；`Load()` 只做 DLL 装载 + 配置烘焙 | 首次加载 −20~50 ms |
| 配置 JSON | 一次性 `ArrayBufferWriter(128)` | 无需优化 | — |
| 程序集体积 | `.tmod` 1.26 MB，DLL `trit_rt.dll` 2.39 MB | 发布 profile 已开 LTO/size 的话保持；否则加 `strip = "symbols"`、`panic = "abort"`（需确认 FFI 安全） | `.tmod` −10~20% |
| 图标 | 3 张 PNG 合计 6.5 KB | `icon_workshop.png` 仅创意工坊上传用，打包可排除 | 加载 −几 ms |

> 关键判断：**本模组不是加载瓶颈**。21–28 ms 相对 21.7 s 只占 0.13%，不值得我们为它做大改；但它便宜，顺手做掉即可。

### 2.3 玩家侧（不改代码就能立刻生效）

1. **删掉不用的 `.tmod`**：`CalamityEntropyLegacy.tmod` 170 MB 即使不启用也要被扫描；不用就移出 `Mods`。
2. **Mods 目录与 Steam 同盘**：避免跨盘/网络盘，减少 `Finding Mods` 的随机读。
3. **减少同时启用的大 mod**：CalamityMod + CalamityEntropyLegacy + CalValEX + FargowiltasSouls 四个一起开，光资产阻塞就 ≥ 1.3 s、内存 ≥ 300 MB。
4. **不要用 `ImmediateLoad` 类功能**：部分优化/UI mod（BossChecklist、ImproveGame）会在加载期拉全量纹理，这是上游问题，可去 issue 反馈。

---

## 3. 其他 mod 的优化空间

### 3.1 体积 / 内存账

| 模组 | `.tmod` | 加载后 RAM | 备注 |
|---|---|---|---|
| CalamityEntropyLegacy | 169.68 MB | — | 单文件最大，扫描/解包成本高 |
| FargowiltasSouls | 小 | **224.2 MB** | 内存第一大户 |
| NecromancerRise | 小 | 45.3 / 54.2 MB | 稳定中高位 |
| Fargowiltas | 小 | 12.4 MB | — |
| HEROsMod | 0.06 MB | 9.5 MB | 体积小、内存不小 |
| Luminance | — | 5.3 MB | 同时有 447 ms 资产阻塞 |
| TritRuntimeMod | 1.26 MB | **4.4 MB** | 本模组，可忽略 |

### 3.2 `GlobalNPC 310 slots/entity` 是什么

`GlobalNPC registration stats. Count: 2, Slots per Entity: 310` 的含义：tML 为**每个 NPC 实体**预留了 310 个 `GlobalNPC` 槽位。tML 对每个实体做 `foreach (GlobalNPC g in npc.ModNPC...)` 时，就是遍历这 310 个槽。后果：

- 每实体内存 ≈ 310 × 指针/实例引用（约 2.5 KB/实体）；
- 每 tick 的 AI 钩子分发要扫 310 个槽（多数为 null 但仍有分支）；
- 世界里有 200 个 NPC 时，就是 6 万个槽的遍历。

**能做什么**：

| 角色 | 建议 |
|---|---|
| 模组作者 | 把多个 `GlobalNPC` 合并成一个（用 `switch` 分发），不要为每个小功能各开一个 `GlobalNPC`；优先用 `ModSystem` + `PostUpdateNPCs` 批量处理 |
| 玩家 | 无法直接改，但可通过「减少同时启用的小功能 mod」间接降低槽位数（每个 mod 的 `GlobalNPC` 都会叠加） |
| 诊断 | 用 `/trit mem` 对照 tML 的 `Mods using the most RAM`，先砍 RAM 最高的那个 |

### 3.3 大 mod 的共性问题（给上游的清单）

1. **加载期 `ImmediateLoad`**：CalamityMod、Luminance、BossChecklist、ImproveGame 都是重灾区。改为 `AssetRequestMode.AsyncLoad` + `OnContentLoaded` 后再取。
2. **纹理尺寸失控**：大量 4K 贴图直接进内存，即使未被绘制。建议 `Texture2D` 延迟到首次绘制时加载（`Asset<Texture2D>` 按需 `Value`）。
3. **音效重复**：多个 mod 各自打包同一 BGM（Calamity 系常见）。建议用 `MusicLoader` 共享或降低码率。
4. **每 tick 的 LINQ / `ToList()`**：AI 热路径里出现 `.Where().ToList()` 是常见 GC 来源。
5. **`ModSystem` 空实现膨胀**：每个 mod 都注册 `PostUpdateEverything` 等钩子，tML 会全部调用，即使方法体为空。

### 3.4 组合建议（按收益排序）

| 场景 | 建议 |
|---|---|
| 只想玩 NecromancerRise + 本模组 | 只启用这两个 + Luminance；内存可降到 ~1.9 GB |
| 想玩 Calamity | CalamityMod + CalamityEntropyLegacy 二选一（两者资产高度重叠） |
| 内存告急（可用 < 2 GB） | 关掉 FargowiltasSouls（224 MB）收益最大 |

---

## 4. 内存账本与治理

### 4.1 账本拆解（tML 2.2 GB）

| 项目 | 估算 | 可控性 |
|---|---|---|
| 原版纹理 / 音效 / 世界 | ~1.0–1.3 GB | 低（引擎固定） |
| tML 运行时 + JIT 代码 | ~400–600 MB | 低（可用 `DOTNET_` 参数微调） |
| 已加载模组资产 | 50–300 MB | **高**（启用/禁用） |
| 本模组内核（Rust） | 4.4 MB | 高（但基数太小） |
| 日志 / 快照 | 5–15 MB | **高**（`maxLogEntries` / `memoryCapacityMb`） |

### 4.2 本模组的内存旋钮

| 设置 | 默认 | 说明 |
|---|---|---|
| `maxLogEntries` | `1 << 20`（约 5 MB，按 5 字节/条） | 直接决定回滚窗口与常驻内存；桌面可降到 `1 << 18`（≈1.3 MB） |
| `memoryCapacityMb` | 512 | 任务池内存上限；单人游戏用 128 足够 |
| `workerCount` | 0 = 自动 | 24 核机器上会自动开很多 worker，但模组实际负载很小，固定 2–4 更省 |
| `snapshotIntervalTicks` | 300 | 快照越频繁，日志/快照内存越高 |

建议提供一档「省内存」预设（`memoryCapacityMb=128`、`maxLogEntries=1<<18`、`workerCount=2`），在低内存机器上自动启用。

### 4.3 .NET 运行时参数（环境变量）

| 变量 | 建议值 | 作用 |
|---|---|---|
| `DOTNET_GCHeapHardLimit` | `0x60000000`（1.5 GB） | 硬上限，防止 GC 无限扩张；设太小会 OOM，需实测 |
| `DOTNET_gcServer` | `0` | 客户端用工作站 GC 更省内存（服务端才用 Server GC） |
| `DOTNET_TieredPGO` | `1` | 默认已开，保持 |
| `DOTNET_TC_QuickJitForLoops` | `1` | 加速首次 JIT（热循环先快速编译） |

> 注意：这些要通过启动器/系统环境变量设置，不能由 mod 自己改（进程启动后只读）。

---

## 5. Rust 内核：三进制 + 二进制编码优化

### 5.1 现状开销模型

| 结构 | 实现 | 单元开销 | 问题 |
|---|---|---|---|
| `Runtime.world` | `BTreeMap<u32, Ternary>` | 值 1 B，但含节点/指针/对齐后**实际 8–10 B/元素** | 稀疏 B 树，每个节点一次分配 |
| `Runtime.event_payloads` | `BTreeMap<u32, Vec<Ternary>>` | 内层 `Vec` 再分配 | 小事件也各占一个堆块 |
| `WorldProjectionFork.snapshot` | `BTreeMap<u32, Ternary>`（**clone**） | 主投影的 2 倍 | `refresh_world()` 每 tick 整份 clone |
| `WorldProjectionFork.history` | `BTreeMap<u32, Vec<bool>>` | 每键一个 `Vec` 头（24 B）+ 数据 | 键多时分配次数爆炸 |
| `entity_states` | `BTreeMap<(u8,i32), EntityState>` | 键 8 B + `NpcState` 84 B + 节点开销 ≈ **100+ B/条** | 每 tick 重建整棵树 |
| `STATE_PUSH` 线格式 | 定长 83 B/条 | 见 `wire.rs` | 每 tick 200 条 ≈ 16.6 KB；60 tps ≈ **1 MB/s** 拷贝 |
| `op_state_push` | 新建 `BTreeMap` 后整体替换 | 每条 1 次节点分配 | 200 次分配/tick = **12,000 次/秒** |
| `refresh_world` | `snapshot.clone()` + 重建 `BTreeSet` + `keys()` 复制 `Vec` | 3 次 O(n) 分配/tick | 纯浪费 |
| `predict_all` | 返回新 `Vec<(u32,Ternary,f32)>` | 1 次 O(n) 分配/tick | 可写入复用缓冲 |
| `op_action_plan` | 打包成 `Vec<u8>`（key 4 + trit 1 + conf 4 = 9 B/intent） | 1 次 O(n) 分配/tick | 对齐浪费 3 B/条 |

**为什么现在还没炸**：`Takeover` 默认 `ObserveOnly`，`op_state_push` 在 `takeover_fork == None` 时直接返回空（严格零开销）。这些成本只有在 L1/L2 或自动档校准时才会显现——所以是「长期隐患」而非「当前故障」。

### 5.2 三进制打包：从 1 B/trit 到 0.2 B/trit

三种可选编码，按用途分工：

| 编码 | 密度 | 随机访问 | 适用 |
|---|---|---|---|
| **2 bit/trit** | 4 trit/byte，0.25 B/trit | O(1) 位运算 | **热路径**（world / entity 状态） |
| **5 trit/byte** | `3^5 = 243 < 256`，0.2 B/trit | 需 `/3` 或查表 | 日志、快照等顺序读写 |
| **20 trit/u32** | `3^20 = 3,486,784,401 < 2^32`，0.2 B/trit | 需 `/3` 20 次或查表 | 紧凑存储 + SIMD 友好的折中 |
| **40 trit/u64** | `3^40 ≈ 1.216e19 < 2^64`，0.2 B/trit | 需 40 次除法/查表 | 归档 |

> 理论下界是 `log2(3) ≈ 1.585 bit/trit`。2 bit 有 26% 冗余，但换来 O(1) 访问，**热路径应当选 2 bit**。

示意实现（热路径）：

```rust
/// 4 trits/byte，每 trit 2 bit：0b00=Neg, 0b01=Zero, 0b10=Pos, 0b11=保留。
#[derive(Clone, Default)]
pub struct TritPacked {
    words: Vec<u64>,   // 32 trits / u64
    len: usize,
}

impl TritPacked {
    #[inline]
    pub fn get(&self, i: usize) -> Ternary {
        let w = self.words[i >> 5];
        let shift = (i & 31) * 2;
        Ternary::from_bits(((w >> shift) & 0b11) as u8)
    }

    #[inline]
    pub fn set(&mut self, i: usize, v: Ternary) {
        let word = &mut self.words[i >> 5];
        let shift = (i & 31) * 2;
        *word = (*word & !(0b11u64 << shift)) | ((v.bits() as u64) << shift);
    }

    /// 三态批量合并：a ∨ b（16 trits 一次），用于事件合并。
    #[inline]
    pub fn or_words(a: u64, b: u64) -> u64 { /* 2-bit 通道内的 max 语义 */ }
}
```

5 trit/byte 的编解码（顺序读写）：

```rust
const P3: [u32; 5] = [1, 3, 9, 27, 81]; // 3^0..3^4

#[inline]
pub fn pack5(t: &[Ternary]) -> Vec<u8> {
    t.chunks(5).map(|c| {
        c.iter().enumerate()
            .map(|(i, v)| v.to_digit() as u8 * P3[i] as u8)
            .sum::<u8>()
    }).collect()
}
```

**预期收益**：1 万个 trit 的 world 投影从约 80–100 KB（`BTreeMap` 实际占用）降到 **2.5 KB**（2 bit），约 **30–40×**。

### 5.3 世界投影：`BTreeMap` → 分页 SoA

`LaneMap` 的键是 `(kind << 28) | (slot << 12) | field`，天然稀疏，不能直接当数组下标。推荐两级结构：

```rust
/// 第一级：把稀疏 key 映成稠密 lane id（只在建表/销毁时更新）。
struct LaneIndex {
    key_to_id: HashMap<u32, u32>,   // 仅结构变化时改动
    id_to_key: Vec<u32>,
}

/// 第二级：按 lane id 连续存放 2-bit 值，分页避免扩容拷贝。
struct TritWorld {
    lanes: LaneIndex,
    pages: Vec<Box<[u64; 16]>>,    // 每页 1024 trits = 256 B
}
```

要点：

- **热路径不再有树**：`get/set` 是两次索引 + 位运算，无指针追逐、无分配。
- **fork 不再整份 clone**：fork 只需 `Vec<Box<[u64]>>` 的 `clone`（连续内存 memcpy，比 B 树快一个数量级），或直接用 `Arc<[u64]>` + 写时复制。
- `history: BTreeMap<u32, Vec<bool>>` 改为**按 lane id 的环形位图**（`Vec<u64>` + head 索引），彻底消除每键 `Vec` 分配。

### 5.4 `STATE_PUSH` v2：83 B → ≤ 32 B

现状 83 B 的字段账：

| 字段 | 字节 |
|---|---|
| `kind u8` | 1 |
| `slot i32` | 4 |
| `type i32` | 4 |
| `velocity_x/y f32` | 8 |
| `position_x/y f32` | 8 |
| `direction i32` | 4 |
| `life i32` | 4 |
| `life_max i32` | 4 |
| `target i32` | 4 |
| `ai[8] f32` | 32 |
| `walk_speed f32` | 4 |
| `target_distance f32` | 4 |
| `tile_below u8` | 1 |
| `on_ground u8` | 1 |
| **合计** | **83** |

v2 布局（量化 + 位域 + 按需字段）：

```
u8  kind
u16 slot                // 槽位上限 65535，足够
u16 type                // NPC/Projectile 类型 < 65535
i16 vel_x_q             // vel × 256，±128 px/tick 精度 1/256
i16 vel_y_q
i16 pos_dx_q            // 相对玩家/镜头中心的偏移，绝对位置由 C# 侧补
i16 pos_dy_q
i8  dir
u8  flags               // bit0 on_ground, bit1 tile_below, bit2 active, bit3 has_target
i16 target_dist_q
u8  ai_mask             // 哪几个 ai 槽非零
[i16; popcount(ai_mask)] // 量化后的 ai 值
```

- 固定部分 **17 B**；多数 NPC 只用到 2 个 ai 槽 → 平均 **21 B**，比 83 B **↓ 75%**。
- 可选「增量模式」：每 tick 只发变了的实体，配一个 `BitSet`（200 实体 = 25 B），世界静止时接近 0 字节。
- `life/life_max/walk_speed` 由内核按 `type` 查表推导，不必每帧传输。

配套的 Rust 侧存储也换掉 `BTreeMap`：

```rust
struct EntityTable {
    npc: Vec<NpcState>,          // 按 slot 直接索引
    npc_active: BitSet,
    proj: Vec<ProjectileState>,
    proj_active: BitSet,
}
```

- **0 次堆分配/tick**（容量按最大值预分配）；
- 遍历用 `BitSet::iter_ones()`，比 B 树迭代快且无分支；
- 单条 `NpcState` 84 B（比现在 ~100+ B 含节点开销更小），且连续内存对缓存友好。

### 5.5 快照 / 事务日志

| 优化 | 做法 | 收益 |
|---|---|---|
| 差分快照 | 只存与上一张快照的 `(lane_id, old, new)`，用 varint 编码 lane_id | 世界稳定时快照体积 ↓ 90%+ |
| 列式日志 | `keys: Vec<u32>` + `deltas: TritPacked` + `tick: Vec<u32>` 分开存 | 回滚时顺序扫描，无指针追逐 |
| 压缩 | 快照页用 `zstd` level 1（或 `lz4`） | 文本/稀疏数据 3–5× |
| mmap | 快照落盘用 `memmap2`，避免 `Vec` 双份拷贝 | 峰值内存 ↓ 1× 快照大小 |

### 5.6 FFI 零拷贝

现状（`ffi.rs`）：

- `tt_rt_push_states` 接收 `const u8* + len`，内部 `Reader` 解析后建表 —— **解析不可避免，但可以复用缓冲**；
- `tt_rt_action_plan` 返回 `Vec<u8>`，C# 侧再 `Marshal.Copy` 一份 —— **两次拷贝**。

方案：

1. **调用方提供输出缓冲**：`tt_rt_action_plan(rt, out_ptr, out_cap) -> usize`，Rust 直接写进 C# 传入的 `ArrayPool<byte>` 租用数组（`fixed` 固定），省掉中间 `Vec`。
2. **输入侧零拷贝**：C# 直接传 `List<EntitySnapshot>` 的 `CollectionsMarshal.AsSpan`，序列化在 C# 侧用 `stackalloc`/池化缓冲完成；Rust 侧只解析。
3. **ActionPlan 编码瘦身**：`key u32 + trit u8 + conf f32` = 9 B → `key u32 + conf u16 (1/65535) + delta u8` = **7 B**；或 SoA（keys / confs / deltas 三块连续），便于 SIMD 过滤 `conf < threshold`。

### 5.7 临时分配回收

| 位置 | 现状 | 改法 |
|---|---|---|
| `predict_all()` | 每次返回新 `Vec` | 传入 `&mut Vec<...>` 复用；或写入 `ActionPlanBuf` 结构体 |
| `keys()` | 每次 `Vec<u32>` 拷贝 | 返回 `&BTreeSet` 的迭代器 / 直接遍历内部字段 |
| `refresh_world()` | `clone()` + 重建 `BTreeSet` | 增量更新（只同步变化的 lane） |
| `op_action_plan` | `Vec<u8>` | 复用 `self.plan_buf`（`clear()` 后 `extend`） |

### 5.8 SIMD / 位运算

- **2-bit 打包天然 SIMD 友好**：32 trits 在一个 `u64` 里，一次位运算处理 32 个值；合并/取反/统计可直接用按位操作。
- **一致性统计**：`consistent` 用 `BitSet`，`count_ones()` 一条指令算一批，避免逐条计数。
- **AVX2**：对 `Vec<u8>` 形态的 trit 做 `_mm256_shuffle_epi8` 查表解码，可再快 2–4×（仅在批量扫描时值得）。

---

## 6. C# 侧热路径优化

### 6.1 现状

| 位置 | 每 tick 分配 | 优化 |
|---|---|---|
| `EntityTakeover.PushEntityStates()` | `new List<EntitySnapshot>(256)`（约 15 KB） | 提为字段，`Clear()` 复用；容量按世界最大实体数预分配 |
| `EntityTakeover.PrepareKernelFirstCache()` | `new Dictionary<ulong, List<...>>` + 每个 bucket 一个 `List` | 提为字段；用 `Dictionary.Clear()` + 复用内部 `List`（池化），或改用 `List<bucket>` 线性查找（实体 < 500 时更快） |
| `EntityTakeover.AfterOrig()` | `new List<(uint,bool)>(plan.Count)` | 提为字段，`Clear()` 复用 |
| `TritHooks` | 每 tick 若干装箱/闭包 | 检查是否有 `foreach` 中的 lambda 捕获，改为 `for` |
| `TritRuntimeMod.BuildRuntimeConfigJson()` | 每次 `ArrayBufferWriter` | 仅装配期调用一次，**不动** |

### 6.2 目标：稳态 0 B/tick

验证方式：

```csharp
// 世界内跑 600 tick，比较差值
long before = GC.GetAllocatedBytesForCurrentThread();
// ... 600 ticks ...
long after = GC.GetAllocatedBytesForCurrentThread();
Logger.Info($"[trit] 每 tick 托管分配 ≈ {(after - before) / 600.0:F1} B");
```

验收线：**< 64 B/tick**（允许偶发装箱）。当前预计在 **10–20 KB/tick** 量级，差距 2–3 个数量级。

### 6.3 Hook 层

- `On_NPC.AI` / `On_Projectile.Update` 里只做「查表 + 写字段」，禁止：
  - `string` 拼接 / 插值；
  - LINQ；
  - `try/catch`（异常路径不热，但 `try` 块本身会阻止内联）；
  - 闭包捕获（`() => ...`）。
- `KernelFirst` 的 bucket 查询用 `Dictionary.TryGetValue` 可以，但如果实体数 < 512，改为**按 slot 的数组**（`List<...>?[]`，索引 `slot`）更快且零哈希。

### 6.4 设置写盘

`TakeoverAutoPilot.Persist` 目前按 `PersistIntervalSeconds = 3600` 节流，可接受；但 `Flush()` 在 `ResetWorldSession` 与 `Teardown` 都会触发，**退出世界时可能连续写两次**。建议加「dirty 标记」，无变化不写。

---

## 7. 分阶段落地与验收

### P0（零风险，先做）

1. ✅ C# 三处缓冲提为字段并复用（`PushEntityStates` / `PrepareKernelFirstCache` / `AfterOrig` / `RunCalibration`），另加 `PublishIf` 单元素载荷复用。
2. ✅ 删除逐 tick 的逐条 intent 日志，改为每 60 tick 一条汇总（含 `first=sub/ev`）；`verboseLogging` 可保持开启。
3. ✅ 设置加「省内存」预设：`autoMemoryPreset`（默认开）在物理内存 &lt; 8 GiB 时把 `memoryCapacityMb` / `maxLogEntries` / `workerCount` 自动降档（只降不升）。
4. ✅ `logJobResults` 的接管明细日志加 60 tick 节流，避免升级到 L1 后逐 tick 刷屏。
5. 文档化玩家侧建议（§2.3 / §3.4），并入 README。
6. 给资产加载 21–28 ms 加计时打点，定位来源。
7. ✅ 文档补充新日志实测：热重载 ALC 泄漏（33 mod × 3 次）与日志占比 71% 的证据（§1.4）。

**验收**：`GC.GetAllocatedBytesForCurrentThread` 差值 < 1 KB/tick；`dotnet-counters` 显示 Gen0 GC 频率下降 ≥ 80%。

### P1（协议 v2，需一起改 C#/Rust）

1. `STATE_PUSH` v2（量化 + `u16 slot` + `ai_mask`），C# 与 Rust 同步升级，ABI 版本 +1。
2. 世界投影 `BTreeMap` → `TritWorld`（分页 2-bit）；`history` → 环形位图。
3. `refresh_world` 改增量；`predict_all` / `keys()` / `op_action_plan` 复用缓冲。
4. FFI 输出缓冲化（`tt_rt_action_plan` 写入 caller buffer）。

**验收**（用 `examples/mem_bench.rs` 对照）：
- 1 万 lane 的 world 投影内存 ≤ 4 KB（当前约 100 KB）；
- `STATE_PUSH` 200 实体 ≤ 4.5 KB/tick（当前 16.6 KB）；
- 内核每 tick 堆分配 = 0；
- 60 tick 长跑 RSS 波动 < 2 MB。

### P2（结构性）

1. `entity_states` → SoA + `BitSet`，遍历用 `iter_ones`。
2. 快照差分 + varint + zstd；落盘改 mmap。
3. 首次加载自检挪到 `OnWorldLoad` 后台；`Load()` 只保留必要路径。
4. 可选：`DOTNET_` 参数模板 + 启动器脚本。

**验收**：
- 首次 `Mod Load Completed` ≤ 12 s（当前 21.7 s）；
- 热重载 ≤ 300 ms（当前 641 ms）；
- 本模组 `Mods using the most RAM` ≤ 5 MB（当前 4.4 MB，允许略增）；
- 世界内 10 分钟 RSS 增长 < 10 MB（无泄漏）。

### 指标总表

| 指标 | 现状 | P0 后 | P1 后 | P2 后 |
|---|---|---|---|---|
| C# 每 tick 分配 | 10–20 KB | < 1 KB | < 64 B | < 64 B |
| Gen0 GC / 分钟 | 高 | ↓80% | 基本消失 | 基本消失 |
| `STATE_PUSH` 字节/条 | 83 | 83 | ≤ 32 | ≤ 21（增量更少） |
| world 投影 B/trit | ~8–10 | ~8–10 | 0.25 | 0.25 |
| 内核每 tick 分配 | 数百次 | 数百次 | 0 | 0 |
| 首次加载 | 21.7 s | 21.5 s | 21 s | ≤ 12 s |
| 资产阻塞（本模组） | 21–28 ms | ≤ 5 ms | ≤ 5 ms | ≤ 5 ms |
| 系统可用内存 | 2.6 GB | ≥ 4 GB | ≥ 4 GB | ≥ 4 GB |

---

## 8. 风险与回滚

| 变更 | 风险 | 回滚方式 |
|---|---|---|
| C# 缓冲复用 | 复用缓冲若被外部持有会读到脏数据 | 只在方法内使用，不暴露引用；`Clear()` 后立即填满 |
| `STATE_PUSH` v2 | 协议不兼容，旧 DLL 解析失败 | ABI 版本 +1；`RuntimeShim.Attach` 校验 `abi`，不匹配直接回退托管实现 |
| 2-bit 打包 | 位运算 bug 导致静默错值 | 保留 `debug_assert` 逐项对照旧编码；单测覆盖全 3 种值 × 32 边界 |
| 增量刷新 | 漏同步导致 fork 世界漂移 | 每 N tick 强制全量校验一次（类似校准） |
| 首次加载延后自检 | 自检失败时玩家已进世界 | 失败时立即降级到 `ObserveOnly` 并禁用钩子 |

**总原则**：每一层优化都必须保留「旧路径 + 能力位」。Rust 侧沿用现有 `takeover_fork == None` 的零开销降级，C# 侧沿用 `KernelTakeoverConfigured` 门控。任何优化出问题，回到 `ObserveOnly` 就是安全的。

---

## 9. 附录：怎么测

```powershell
# 1) Rust 长跑内存（已有工具）
cargo run --release -p trit-rt --example mem_bench -- --ticks 60000
cargo run --release -p trit-rt --example mem_bench -- --log-entries 262144

# 2) C# 托管分配（temp 脚本，不进仓库）
dotnet-counters monitor -p <tModLoader PID> System.Runtime

# 3) 游戏内
/trit mem
/trit status
/trit takeover stats

# 4) 加载耗时
#    看 client.log 的 "Mod Load Completed in Xms" 与 "blocking on asset loading"
```

> 备注：`mem_bench` 的负载（每 tick 1600 条变化、300 tick 一快照）与 `WorldBridge` 真实路径一致，可直接作为 P1 的前后对照基线。
