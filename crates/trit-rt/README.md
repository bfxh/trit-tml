# trit-rt

tModLoader 旁路三进制运行时内核（Rust）。属于 [trit-tml](https://github.com/bfxh/trit-tml) 工作区。

定位：不替换 tModLoader 的 C# 兼容壳，只接管它内部「调度、事务、序列化、热点计算」这一层
可判定、可回退、可差分的内核。Rust 侧只处理纯数据、句柄、事务与动作意图；
Unity/tModLoader 对象始终由 C# 主线程持有。

## 模块地图

| 模块 | 职责 |
|---|---|
| `wire` | 二进制线格式（与 C# 兼容壳逐字节对齐）+ opcode / 能力位表 |
| `router` | 事件路由：依赖图 + 脏标记 + 批处理，静态顺序由 `trit-schedule` 计算 |
| `txn` | 三进制事务日志：提交 / 回滚 / 重放，只追加 |
| `arbiter` | 跨模组写冲突仲裁：严格（`trit-core` 多驱动总线）/ 多数（计票） |
| `snapshot` | 世界三进制张量投影的快照与差分 |
| `prng` | 确定性 PRNG（splitmix64，可逐位移植到 C#） |
| `runtime` | 装配体，`dispatch(opcode, payload)` 是唯一入口 |
| `ffi` | C ABI（`tt_rt_*`），panic 全拦，跨边界只回状态码 |

## 三进制语义

`-1` 减少 / 移除，`0` 不变 / 弃权 / 未驱动（总线高阻），`+1` 增加 / 设置。
`apply_delta(current, delta) = clamp(index(current) + index(delta) - 1, 0, 2)`。

总线语义：0 个驱动 → 高阻；1 个驱动 → 其值；≥2 个驱动 → 冲突（即使同向）。

## 依赖

三进制原语全部来自本地库 `third_party/trit`（[bfxh/trit](https://github.com/bfxh/trit) 子模块，
路径依赖、**只读调用**）。本 crate 不复制、不修改、不重新实现其任何算法。

## 构建与测试

```bash
cargo test -p trit-rt --release        # 42 个测试
cargo clippy -p trit-rt --all-targets  # 0 警告
cargo build --release -p trit-rt       # 产出 target/release/trit_rt.dll
cargo run --example ffi_smoke          # C ABI 冒烟
```

`trit_rt.dll` / `libtrit_rt.so` / `libtrit_rt.dylib` 由 C# 兼容壳（`dotnet/TritRuntime.Shim`）
通过 P/Invoke 调用；库缺失时兼容壳自动回退到语义等价的托管内核。
