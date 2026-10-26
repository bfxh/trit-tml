# trit_rt C ABI

跨 FFI 只传字节和句柄，panic 全部 `catch_unwind` 拦住，永远只向调用方返回状态码。

缓冲区协议：响应写进 `out`（容量 `cap`）。空间不足时把**所需长度**以 `u64` 小端写进
`out` 前 8 字节（要求 `cap >= 8`）并回 `TT_ERR_BUFFER`，调用方扩容后重试即可。
成功时响应统一装框 `[u32 payload_len][payload]`。

- `ABI_VERSION = 2`（v2 新增并行任务池 / GPU / 全局预算三组能力）
- `RUNTIME_VERSION = 0x00_01_00_00`

## 状态码

| 常量 | 值 | 含义 |
| --- | --- | --- |
| `TT_OK` | 0 | 成功 |
| `TT_ERR_BAD_HANDLE` | -1 | 句柄为空或不存在 |
| `TT_ERR_BAD_OP` | -2 | opcode 为 0 或保留值 |
| `TT_ERR_BUFFER` | -3 | 输出缓冲区不足；所需长度已写入 `out` 前 8 字节 |
| `TT_ERR_CAPACITY` | -4 | 超过容量上限（含预算/资源配额放不下） |
| `TT_ERR_CONFLICT` | -5 | 冲突仲裁未通过 |
| `TT_ERR_PANIC` | -6 | Rust 侧 panic 被拦住 |
| `TT_ERR_DECODE` | -7 | 线格式解码失败 |
| `TT_ERR_NOT_FOUND` | -8 | 事务 / 快照 / 任务不存在 |
| `TT_ERR_STATE` | -9 | 状态非法（重复提交等） |
| `TT_ERR_ARG` | -10 | 参数非法 |

## 导出函数

| 函数 | 说明 |
| --- | --- |
| `tt_rt_abi_version()` / `tt_rt_version()` | 版本 |
| `tt_rt_create(config, len) -> handle` / `tt_rt_destroy(handle)` | 生命周期；`config` 为配置 JSON（见下） |
| `tt_rt_submit(handle, opcode, payload, len, out, cap)` | 通用分发（v2 的新 opcode 全走这条路） |
| `tt_rt_step` / `tt_rt_snapshot` / `tt_rt_diff` / `tt_rt_capabilities` / `tt_rt_last_error` | 既有便捷入口 |

## opcode 表

| opcode | 名称 | 载荷 | 响应 |
| --- | --- | --- | --- |
| `0x01..0x13` | 既有（订阅/事务/快照/差分/PRNG/影子/AI） | 二进制 | 二进制 |
| `0x14` | `SCAN_SUBMIT` | **JSON** | **JSON** `{"job": <u64>}` |
| `0x15` | `JOB_POLL` | **JSON** `{"limit":n,"owner":"..."}` | **JSON** 已完成任务列表（取出即出队，可按 owner 过滤） |
| `0x16` | `JOB_CANCEL` | **JSON** | **JSON** `{"cancelled": <u64>}` |
| `0x17` | `GPU_STATUS` | **JSON** | **JSON** GPU 后端状态 |
| `0x18` | `BUDGET` | **JSON** | **JSON** 全局预算快照 |
| `0x19` | `METRICS` | **JSON** | **JSON** `{"prometheus": "<text>"}` |

> v2 新增的 6 个 opcode 一律用 **UTF-8 JSON**（既有 opcode 保持二进制不变，避免破坏兼容）。
> 空载荷等价于 `{}`。

## 能力位

| 位 | 名称 | 含义 |
| --- | --- | --- |
| `1<<0` | `ROUTER` | 事件路由 |
| `1<<1` | `TXN_LOG` | 三进制事务日志 |
| `1<<2` | `SNAPSHOT` | 快照 |
| `1<<3` | `DIFF` | 差分 |
| `1<<4` | `ARBITRATE` | 冲突仲裁 |
| `1<<5` | `PRNG` | 确定性 PRNG |
| `1<<6` | `CODEC` | 序列化编解码 |
| `1<<7` | `SHADOW` | 影子差分 |
| `1<<8` | `AI` | AI 决策投票 |
| `1<<9` | `PARALLEL` | 并行任务池（v2） |
| `1<<10` | `GPU` | GPU 后端**可用**（构建带了 `gpu` feature **且**本机探测通过；v2） |
| `1<<11` | `BUDGET` | 全局资源预算（v2） |

`GPU` 位只有在**运行时探测成功**后才置位；构建未带 feature、无 adapter、自检对拍不一致
时都不置位——调用方据此决定是否展示 GPU 开关。

## 配置 JSON（`tt_rt_create`）

`#[serde(default)]`：缺省字段用默认值，旧配置文件不受影响。

```json
{
  "seed": 0,
  "max_subscribers": 65536,
  "max_open_txns": 4096,
  "max_snapshots": 64,
  "max_log_entries": 1048576,
  "arbitration": "majority",
  "shadow": true,
  "parallel": {
    "workers": 0,              // 0 = 自动：min(可用并行度, 4)，至少 1
    "cpu_capacity": 0,          // 0 = 自动：等于 workers（每个任务默认占 1 个 CPU 单位）
    "memory_capacity": 512,     // 全局内存预算（单位 1 MiB）
    "per_mod_inflight": 2,      // 同一 owner 同时在跑的任务上限（防独占）
    "scan_chunk": 4096,         // 扫描任务每步处理的元素数
    "max_batch": 8              // 单轮最多派发的任务数
  },
  "gpu": {
    "policy": "auto",           // auto | on | off
    "min_trits": 1048576        // 小于该规模走 CPU（dispatch 延迟占主导，GPU 反而更慢）
  }
}
```

## `0x14 SCAN_SUBMIT`

提交一个扫描任务到旁路任务池，**立即返回**，任务在 Rust 工作线程上跑，游戏主线程从不等待。

请求：

```json
{
  "owner": "SomeMod",          // 计费/配额归属（必填，空串/纯空白归为 "unknown"）
  "kind": "pattern",           // histogram | pattern | density | dot
  "trits": [0,1,2,0],          // 每个元素一个 trit：0=Neg(-1) 1=Zero(0) 2=Pos(+1)
  "pattern": [2,2,1],          // pattern / dot 用；其余 kind 忽略；允许 null（等价空数组）
  "threshold": 3,              // pattern：命中最低分；0/缺省 = 满分（pattern 中非零元素的个数）
  "chunk": 4096,               // 分步粒度；<=0 用配置里的 scan_chunk；上限 1<<20
  "oneshot": false,            // true = 不分步、不可抢占，走 run_runtime 闭包并发
  "priority": 0,               // 越大越优先（抢占时先保留高优先级）
  "top": 16                    // density：返回密度最高的块数上限；<=0 = 16
}
```

响应：`{"job": 42}`。任务 id 单调递增，从 1 开始。

**载荷非法时不返回错误**，而是**产生一个 `failed` 任务**（`state` = `failed`，
`error` 里写明原因，如「非法 trit 值」「未知扫描 kind」）。
调用方总能通过 `JOB_POLL` 拿到一条带说明的结果，不会「提交后石沉大海」。

**载荷放不下全局容量**（单个任务的需求超过全局 `memory_capacity`）也**不返回错误**，
而是产生一条 `failed` 任务（原因写明需求与全局容量）。这属于「放不下、等也没用」，
但**不让整个 opcode 失败**——同一份请求在原生与托管两条路径上必须给出**逐字节相同**
的 `failed` 记录（`error` 文案、字段顺序、字符编码都一致），否则影子差分一直报假分歧。
`rejected` 计数照记（见 `0x18 BUDGET`），但 `SCAN_SUBMIT` 本身回 `{"job": <id>}`。

**owner 归一化口径**（提交侧与轮询侧必须用同一个函数）：
去首尾空白 → 为空则归为 `"unknown"`。否则「以空 owner 提交」的任务用
`"unknown"` 过滤就找不到，两条路径会记到两个不同的账本里，预算快照对不上。

kind 语义（**Zero 记 0，Neg 记 -1，Pos 记 +1**）：

- `histogram`：统计 `trits` 里 neg / zero / pos 各多少个。
  结果 `{"histogram":{"neg":n,"zero":n,"pos":n}}`
- `pattern`：在 `trits` 上以 `pattern` 为窗口滑动打分，分数 = 窗口与 pattern 的三值点积；
  `score >= threshold` 记为命中。大任务（元素数 ≥ `gpu.min_trits` 且 GPU 可用）走
  **一次三值矩阵乘**（A = 所有窗口的双导轨打包，B = 单列 pattern），否则 CPU 分块逐步扫。
  结果 `{"matches":[{"index":i,"score":s}],"scanned":n,"backend":"gpu|cpu"}`
- `density`：把 `trits` 按 `chunk` 分块，统计每块非零密度，返回密度最高的 `top` 块。
  结果 `{"blocks":[{"block":i,"index":x,"nonzero":k,"density":f}],"blocks_total":n}`
- `dot`：`trits` 与 `pattern` 的三值点积（长度不等按较短者截断）。
  结果 `{"dot":123,"length":n}`

## `0x15 JOB_POLL`

取出已完成的任务结果（**取出即出队**，不会重复返回）。

请求：

```json
{"limit": 16, "owner": "SomeMod"}
```

- `limit`：最多取几条；`0` / 缺省 / **负数** = 全部；
- `owner`：归属过滤。**缺省或 `null` = 不限归属**；给了就只取该 owner 的。
  空串归一化为 `"unknown"`（与提交侧同口径，见下）。

响应：

```json
{"jobs":[
  {"id":42,"owner":"SomeMod","kind":"pattern","state":"done",
   "result":{ "...": "见上" },"error":"","steps":7,"backend":"cpu"}
]}
```

`state`：`done` / `cancelled` / `failed`。`result` 在 `failed` 时为空对象。

### owner 过滤：为什么必须先过滤再截断

多 mod 场景下，每个 mod 只该看到自己的结果。过滤语义有两个要点：

1. **只出队真正返回的那几条**。其他 owner 的结果**留在队列里**，
   下一个 owner 轮询时照样能取到。
2. **先按 owner 过滤，再取前 `limit` 条**。反过来（先取前 `limit` 条再筛）
   会让队头被别人的任务占满，结果该 owner 的任务被挤掉——本该属于它的结果一条都拿不到。

### 两阶段语义（跨 FFI 调用时必须遵守）

C ABI 的缓冲区协议规定「响应装不下 → 回 `TT_ERR_BUFFER` → 调用方扩容后重试」。
若响应写出前就把结果出队，重试时队列已空，任务结果**永久丢失**。

因此跨 FFI 的路径必须：**窥视 → 确认交付成功 → 再按 id 出队**。
`JOB_POLL` 是**流式语义**，不进「同请求结果缓存」（缓存重放会返回陈旧数据）。

### 非法请求的语义

- `SCAN_SUBMIT` 的**载荷非法**（trit 值不是 0/1/2、kind 未知、pattern/dot 缺 pattern、
  载荷放不下全局容量）→ **产生一个 `failed` 任务**，而不是让整个 opcode 失败。
  这样调用方总能通过 `JOB_POLL` 拿到一条带 `error` 说明的结果，不会「提交后石沉大海」。
- `JOB_POLL` 的**请求非法**（载荷不是合法 JSON）→ 返回 `TT_ERR_DECODE`。

### 数值参数的符号约定

所有数值参数一律**有符号**接收并夹取，**不因负数或越界而让整个请求失败**：

| 参数 | 异常值的处理 | 上限 / 备注 |
|---|---|---|
| `limit` | `<= 0` = 全部 | — |
| `chunk` | `<= 0` = 用任务池默认粒度（4096） | `1 << 20`（`MAX_SCAN_CHUNK`） |
| `top` | `<= 0` = 16 | — |
| `threshold` | `<= 0` = 满分（pattern 中非零元素个数） | — |
| `priority` | 超 `int32` 范围 → 按 `0` | 有符号 32 位 |
| `oneshot` | 非 bool（数字/字符串/null）→ `false` | — |
| `trits` / `pattern` 元素 | `< 0` 或 `> 2` → 钳到 `255`，随后由校验判为「非法 trit 值」并失败 | 元素为 `u8` |
| `owner` | 缺省 / `null` / 空串 / 纯空白 → `"unknown"` | 提交侧与轮询侧同一函数归一化 |
| `kind` | 缺省 / `null` / 空串 → 未知种类 → `failed`（`kind` 字段写 `"unknown"`） | 已知 4 种 + `unknown` |

**`error` 文案不带任何前缀**（原生与托管都只写原因正文，如「非法 trit 值：每个元素只能是 0、1 或 2」）。
之前原生侧曾带「输入非法：」前缀，已移除——前缀会让影子差分在每条错误上逐字节对不上。

托管回退内核（`ManagedKernel`）必须与原生侧**逐项对齐**，
否则影子差分（逐字节对拍）会一直报假分歧。

### JSON 键序、浮点格式与字符编码

`JOB_POLL` / `BUDGET` / `GPU_STATUS` / `METRICS` 以及每个 `result` 对象的**字节级一致性**
是影子差分的前提。两侧必须服从以下三条：

1. **键序 = 书写顺序**：Rust 侧用 `serde_json` 的 `preserve_order` feature（键按 struct
   字段声明序 / `Map` 插入序输出），C# 侧用 `Utf8JsonWriter` 按写入序输出。
   任意一侧重排键都会让同一份响应在两条路径上字节不同。**新增字段只能追加在末尾。**
2. **浮点统一定点 6 位小数**：`density` 等浮点值写成 `1.000000` 这样的定点文本，
   而不是 `1.0` / `1e-6` 这类最短表示。跨平台、跨实现完全确定，避免浮点最短表示算法差异。
3. **中文字符不转义**：`Utf8JsonWriter` 必须配 `JavaScriptEncoder.UnsafeRelaxedJsonEscaping`，
   输出原始 UTF-8（与 serde_json 一致）。否则「非法 trit 值」会被写成
   `\u975e\u6cd5...`，每条中文错误都触发假分歧。

> 这三条是**硬约束**：任何一侧改了键序、浮点格式或转义策略，都必须同步另一侧，
> 并跑 `ParallelApiTests.ManagedAndNativeScanResultsAgreeWhenV2NativeIsAvailable`
> （字节级对拍）确认无回归。

## `0x16 JOB_CANCEL`

请求 `{"job": 42}` 取消单个任务，或 `{"owner":"SomeMod"}` 取消该 owner 的全部排队任务。
已在跑的任务在**步骤边界**退出（协作式取消，不强杀线程）。响应 `{"cancelled": n}`。

## `0x17 GPU_STATUS`

请求：

```json
{"policy":"auto", "probe": false, "trits": 1048576}
```

- `policy`：`auto|on|off`，给出即切换策略（缺省不改）；
- `probe`：`true` 强制重新探测（首次调用与策略切换时自动探测一次）；
- `trits`：探测用的向量规模（缺省 1 Mi）。

响应：

```json
{"available":true,"enabled":true,"policy":"auto","adapter":"NVIDIA GeForce RTX 4060 Ti",
 "backend":"Vulkan","built_with_gpu":true,"reason":"",
 "cpu_dot":-123,"gpu_dot":-123,"matches":true,
 "min_trits":1048576,"ns_per_trit":0.0067,
 "jobs":3,"gpu_jobs":1,"fallbacks":0}
```

- `available`：构建带 `gpu` feature **且** adapter 拿到 **且** CPU/GPU 自检对拍一致；
- `enabled`：`available` 且策略允许；
- `fallbacks`：GPU 结果与 CPU 对拍不一致、或 GPU 调用返回 `None` 而回退 CPU 的次数
  （**任何一次不一致都会永久关闭 GPU**，并把原因写进 `reason`）。

## `0x18 BUDGET`

全局预算快照（旁路层的"全局占用"账本）：

```json
{"workers":4,"cpu_capacity":4,"memory_capacity":512,"per_mod_inflight":2,
 "inflight":1,"queued":3,
 "mods":[{"owner":"SomeMod","inflight":1,"queued":0,"submitted":10,
          "completed":9,"cancelled":0,"rejected":0,"steps":123}],
 "totals":{"submitted":10,"completed":9,"cancelled":0,"rejected":0,"steps":123},
 "last_metrics":{"started":1,"completed":1,"failed":0,"cancelled":0,
                 "deferred":0,"preemptions":0,"steps":7,"peak_cpu":1,"peak_memory":1}}
```

- `rejected`：单任务资源需求超过全局容量，产生一条 `failed` 任务（不再返回 `TT_ERR_CAPACITY`；
  opcode 本身仍成功，回 `{"job": <id>}`）。`TT_ERR_CAPACITY` 仅用于既有事务/快照/路由路径。
- `queued`：超过 `per_mod_inflight` 而排队等待的（**不拒绝、不丢**，下一轮再派）。

## `0x19 METRICS`

请求 `{}`，响应 `{"prometheus": "..."}`：最近一轮任务池执行的
`RuntimeMetrics`（上游 `trit-schedule` 的 Prometheus/OpenMetrics 文本，前缀 `trit_schedule`）。

## 执行语义（必须遵守的四条）

1. **游戏主线程永不阻塞**：扫描任务只入队；`JOB_POLL` 只取已完成的。
2. **可抢占、协作式取消**：长扫描实现 `ResumableTask`，在 `chunk` 边界返回 `Yield`；
   高优先级任务放不下槽位时抢占低优先级（状态保留，之后从原状态继续）。
   取消只在步骤边界生效，不强杀线程。
3. **三值语义统一**：`0=Neg(-1)`、`1=Zero(0)`、`2=Pos(+1)`，与既有 `ShadowRecord` 一致。
4. **原生库的加载与调用必须按实例绑定**：句柄（运行时句柄、任务 id）只在
   **创建它的那个模块实例**内有效。加载多份 `trit_rt` 时，
   每一个 `NativeKernel` 都必须只调用自己加载的那份模块的导出函数，
   不能依赖任何进程级的静态 `DllImport`（`.NET` 会缓存 DllImport 的首次解析结果，
   后续指定路径会被静默忽略）。详见 [Issue #3](https://github.com/bfxh/trit-tml/issues/3)。

