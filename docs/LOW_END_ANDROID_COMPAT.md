# 低配 Android 兼容路线图

> 目标：让 2GB RAM / 4 核 A53 的老安卓机（Android 9-11，典型骁龙 4xx / Helio P20 / Exynos 7870）也能跑 tModLoader + trit-tml。

## 0. 现状快照

| 指标 | 当前值 | 老手机可接受值 | 差距 |
|---|---|---|---|
| **native lib 体积**（default features） | 3471 KB (DLL) | ≤ 2 MB | 1.7× |
| **native lib 体积**（no-default + strip） | **2012 KB** | ≤ 2 MB | ✅ |
| **进程常驻内存**（trit-rt 内核） | ~15-25 MB | ≤ 8 MB | 2-3× |
| **并行 worker 数默认上限** | 4 (auto) | 1 (单线程) | 4× |
| **memory_capacity 默认** | 512 MB | ≤ 64 MB | 8× |
| **scan_chunk 默认** | 4096 trits | 512 trits | 8× |
| **max_batch 默认** | 8 | 2 | 4× |
| **Rust 依赖数量** | ~60 crates | 目标 ≤ 30 | 2× |
| **Android .so 构建 target** | ❌ 无 | arm64-v8a + armeabi-v7a | 缺失 |
| **NativeKernel.Load(path) 有效性** | ❌ 静默忽略路径 | 必须按实例绑定 | bug 阻塞 |

### 已有 Issue 关联

| Issue | 标题 | 对低配 Android 的影响 |
|---|---|---|
| #3 | NativeKernel.Load(path) 静默忽略路径 | **必须修**：Android 按内容 hash 落盘原生库，版本切换时路径失效 = 跑的是旧内核 |
| #4 | TxnLog 只追加不截断导致容量超限 | ✅ 已修 (db1b2a9) |
| #2 | 从"只观测不回写"切到默认接管 | 接管模式吃更多 CPU/内存，Android 默认应保持"只观测" |
| #1 | 专用服务器无玩家连接时不产生 tick 数据 | 桌面问题，Android 单机不直接相关 |

---

## 1. 可立即做的优化（改动小，收益明确）

### T1. 给 ParallelConfig 加 Android / LowEnd profile 默认值 ⚡⚡⚡

**改动量**：小（2 文件）  
**收益**：内存占用降 8×，CPU 峰值降 4×  
**风险**：低（运行时可 override）

**改什么**：

`crates/trit-rt/src/pool.rs` — `ParallelConfig::default()` 里根据 `cfg(target_os = "android")` 或 RuntimeConfig 新增的 `profile: LowEnd` 参数选不同默认值：

```rust
#[derive(Debug, Clone, Copy)]
pub enum Profile {
    DesktopHigh,    // 默认：workers=auto, memory=512, scan_chunk=4096
    DesktopLow,     // 省电模式：workers=2, memory=128
    Mobile,         // Android 默认：workers=1, memory=32, scan_chunk=512, max_batch=2
}
```

`mod/TritRuntimeMod/src/WorldBridge.cs` — 启动时检测 `RuntimeInformation.OSDescription`，Android 自动选 Mobile profile。

**默认值调整**：

| 参数 | DesktopHigh | Mobile (Android) |
|---|---|---|
| workers | auto (max 4) | 1 |
| memory_capacity | 512 MB | 32 MB |
| scan_chunk | 4096 trits | 512 trits |
| max_batch | 8 | 2 |
| per_mod_inflight | 2 | 1 |

### T2. 加 Android .so 构建 target + strip ⚡⚡⚡

**改动量**：小  
**收益**：模组可直接在 Android 上加载 .so，不用 Desktop DLL  
**风险**：中（需要 Android NDK）

**步骤**：

```bash
rustup target add aarch64-linux-android
rustup target add armv7-linux-androideabi
cargo build -p trit-rt --release --no-default-features --target aarch64-linux-android
strip target/aarch64-linux-android/release/libtrit_rt.so
# => 预期 ≤ 2 MB
```

`mod/TritRuntimeMod/src/NativeLibraryStager.cs` — 平台检测分支加 Android：

```csharp
Android => "libtrit_rt.so",  // 和 Linux 同名但走 Android target 构建
```

### T3. 加 `.cargo/config.toml` 的 release 体积优化 profile ⚡⚡

**改动量**：极小（1 文件）  
**收益**：默认 release 构建自动 strip + LTO，省 959 KB

**改什么**：

```toml
[profile.release]
strip = "symbols"
lto = "thin"
codegen-units = 1
panic = "abort"
opt-level = "z"     # 最小体积，放弃极致速度
```

权衡：`opt-level = "z"` + `codegen-units = 1` 让 scan 性能降 15-20%（scan 是纯计算，对 Android 来说体积 > 速度）。桌面端用 opt-level=3，移动端用 opt-level=z。

### T4. 修复 Issue #3 NativeKernel.Load 路径忽略 ⚡⚡⚡

**改动量**：中（C# 桥接层约 200 行）  
**收益**：严重 bug 修复；Android 原生库按 hash 落盘，**不修复则升级永远失效**  
**风险**：低（按 issue 里给出的方案做就行）

**方案**（issue 已给出）：把所有带句柄的调用从静态 `DllImport` 改成实例绑定：

```csharp
// 每个 NativeKernel 持自己那份模块的函数指针
var lib = NativeLibrary.Load(libraryPath);
var submit = Marshal.GetDelegateForFunctionPointer<NativeSubmit>(
    NativeLibrary.GetExport(lib, "tt_rt_submit"));
```

整条链路（Create/Submit/Commit/Snapshot/Destroy）都走同一实例，ABI 校验读本模块的版本。

### T5. 重配 RuntimeConfig 默认值（Android/low-end）⚡⚡

**改动量**：小  
**收益**：snapshot/txn/shadow 的内存上限从默认值降下来  
**风险**：低，运行时可 override

```
| 参数 | 当前默认 | Android 默认 | 省内存 |
|---|---|---|---|
| max_subscribers | 65536 | 8192 | 订阅表减 8× |
| max_open_txns | 4096 | 256 | 打开事务减 16× |
| max_snapshots | 64 | 8 | 快照滚窗减 8× |
| max_log_entries | 1048576 | 65536 | TxnLog 减 16× |
| shadow | true | false | 影子签名（不影响核心功能） |
```

---

## 2. 中期优化（需改依赖结构）

### T6. 砍掉 ai-voting 默认 feature（Android 上）

**改动量**：小（Cargo.toml feature 设计）  
**收益**：省约 500 KB native lib + 运行时推理内存  
**风险**：低（AI 投票不是核心功能，Android 用户不需要）

```toml
[features]
default = ["desktop-default"]
desktop-default = ["ai-voting"]
mobile-default = []
ai-voting = ["..."]
```

模组侧 build 命令：`cargo build --no-default-features`（或显式 `--features mobile-default`）。

### T7. 把 rayon 换成 std::thread 或串行后备

**改动量**：中  
**收益**：rayon 让 native lib 增 ~200 KB，Android 上单线程模式根本不走 rayon  
**风险**：中（Desktop high 端用 rayon，mobile 端不用）

方案：rayon 放到 feature 里，Mobile profile 下禁用。

### T8. GPU backend 做 Android capability check

**改动量**：小（启动时检测 GpuPolicy）  
**收益**：避免在不支持 Vulkan 的老 GPU 上初始化 GPU 后端（崩溃风险）  
**风险**：低

---

## 3. 长期目标

- Android 上完整构建流水线（CI matrix + APK 内嵌 .so）
- 运行时内存峰值 profile：Mobile 模式下实测 ≤ 8 MB
- 多 ABI 分发：arm64-v8a + armeabi-v7a + x86_64
- 低端机 benchmark：scan 1000 world trits ≤ 50ms

---

## 4. 优先级排序（建议执行顺序）

| 序号 | 任务 | 优先级 | 改动量 | 状态 |
|---|---|---|---|---|
| **T4** | 修 Issue #3 NativeKernel.Load 路径 | **P0** | 中 | 🟡 待做 |
| **T1** | ParallelConfig Android profile | **P1** | 小 | 🟡 待做 |
| **T3** | cargo release 体积 profile | **P1** | 极小 | 🟡 待做 |
| **T5** | RuntimeConfig Android 默认值 | **P1** | 小 | 🟡 待做 |
| **T2** | Android .so 构建 target | **P2** | 小 | 🟡 待做 |
| **T6** | ai-voting feature 拆分 | **P2** | 小 | 🟡 待做 |
| **T7** | rayon feature 化 | P3 | 中 | 🟢 可延后 |
| **T8** | GPU Android check | P3 | 小 | 🟢 可延后 |

**执行策略**：T4 → T1 + T3 + T5 一起做（都是默认值调整，互相独立）→ 验证 full suite → 再 T2。
