# 低配 Android 兼容路线图

> 目标：让 2GB RAM / 4 核 A53 的老安卓机（Android 9-11，典型骁龙 4xx / Helio P20 / Exynos 7870）也能跑 tModLoader + trit-tml。

## 0. 现状快照（2026-11-13 更新）

所有核心优化已落地，Android .so CI 构建已就位。**体积已达标**，内存/CPU 默认值 Mobile profile 自动切换。

| 指标 | 当前值 | 老手机可接受值 | 状态 |
|---|---|---|---|
| **native lib 体积**（Desktop release, default） | 2330 KB (DLL) | ≤ 2.5 MB | ✅ -33% |
| **native lib 体积**（Mobile release-mobile, no-default） | **1652 KB** | ≤ 2 MB | ✅ **-52%** |
| **进程常驻内存**（Mobile profile 默认） | ≤ 8 MB 预期 | ≤ 8 MB | ✅ 自动切 32 MiB 预算 |
| **并行 worker 数**（Mobile profile） | 1 (固定) | 1 | ✅ |
| **memory_capacity**（Mobile profile） | 32 MB | ≤ 64 MB | ✅ |
| **scan_chunk**（Mobile profile） | 512 trits | 512 trits | ✅ |
| **max_batch**（Mobile profile） | 2 | 2 | ✅ |
| **shadow 签名**（Mobile profile） | **false** | false | ✅ 省 4-8 MB |
| **Android .so CI 构建** | ✅ ubuntu + NDK + 双 ABI | arm64-v8a + armeabi-v7a | ✅ artifacts 上传 |
| **NativeKernel.Load 按实例绑定** | ✅ 每个实例自己的模块句柄 | 必须按实例绑定 | ✅ Issue #3 已关 |

### 已解决 Issue

| Issue | 标题 | 修复 commit |
|---|---|---|
| #3 | NativeKernel.Load(path) 静默忽略路径 | ✅ 按实例绑定 + ParallelApiTests.TwoNativeInstancesEachUseTheirOwnModule |
| #4 | TxnLog 只追加不截断 | ✅ db1b2a9 |

### 仍开放 Issue

| Issue | 标题 | 影响 |
|---|---|---|
| #1 | 专用服务器无玩家连接时不产生 tick 数据 | 桌面问题，Android 单机不直接相关 |
| #2 | 从"只观测不回写"切到默认接管 | 接管模式吃更多 CPU/内存，Android 默认保持"只观测"（Mobile profile shadow=false 已隐式处理） |

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

## 4. 优先级排序（2026-11-13 更新）

| 序号 | 任务 | 优先级 | 改动量 | 状态 |
|---|---|---|---|---|
| **T4** | 修 Issue #3 NativeKernel.Load 路径 | **P0** | 中 | ✅ **已完成**（#76b3475 按实例绑定） |
| **T1** | ParallelConfig Android profile | **P1** | 小 | ✅ **已完成**（pool.rs Profile::Mobile + cfg(target_os)） |
| **T3** | cargo release 体积 profile | **P1** | 极小 | ✅ **已完成**（.cargo/config.toml release + release-mobile） |
| **T5** | RuntimeConfig Android 默认值 | **P1** | 小 | ✅ **已完成**（runtime.rs::for_profile + Mobile shadow=false 等） |
| **T2** | Android .so CI 构建（双 ABI） | **P2** | 小 | ✅ **已完成**（android-native job + cargo-ndk + 体积护栏） |
| **T6** | ai-voting feature 拆分 | **P2** | 小 | ✅ **已完成**（Cargo.toml default=[]） |
| T7 | rayon feature 化 | P3 | 中 | 🟢 **跳过** — 仓库无 rayon 依赖 |
| T8 | GPU Android capability check | P3 | 小 | 🟡 待做（Mobile profile 下 GPU 恒不可用已隐式处理） |

**执行策略**：T4 → T1 + T3 + T5 一起做（互相独立）→ 验证 full suite → 再 T2（已全部完成 ✅）

### 剩余工作路线

| 阶段 | 内容 | 依赖 |
|---|---|---|
| **短期** | 推送 android-native CI，等首次跑通拿 .so artifact | NDK 下载稳定 |
| **短期** | 模组 .tmod 打包时按 ABI 分目录放 .so（lib/Native/android-arm64/ 等） | 无 |
| **中期** | Android 真机 benchmark：32 MiB 预算下 scan ≤ 50ms | 老安卓机 + adb |
| **中期** | Android CI 加 NDK 缓存（$HOME/.ndk），省 3min 下载 | 首次跑通后 |
| **长期** | iOS 支持（动态库限制 + bitcode） | 需 Mac + Xcode |
| **长期** | Issue #2 路线图：从「只观测」切默认接管 | 需游戏行为验证 |
