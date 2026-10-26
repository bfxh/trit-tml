//! **trit-rt**：tModLoader 旁路三进制运行时内核。
//!
//! 定位：不替换 tModLoader 的 C# 兼容壳，只接管它内部「调度、事务、序列化、
//! 热点计算」这一层可判定、可回退、可差分的内核。Rust 侧只处理纯数据、
//! 句柄、事务与动作意图；Unity/tModLoader 对象始终由 C# 主线程持有。
//!
//! 模块地图：
//! - [`wire`]：二进制线格式（与 C# 兼容壳逐字节对齐）+ opcode/能力位表；
//! - [`router`]：事件路由（依赖图 + 脏标记 + 批处理，静态顺序由 trit-schedule 计算）；
//! - [`txn`]：三进制事务日志（提交 / 回滚 / 重放）；
//! - [`arbiter`]：跨模组写冲突仲裁（严格 = trit-core 多驱动总线，多数 = 计票）；
//! - [`snapshot`]：世界三进制张量投影的快照 / 差分；
//! - [`prng`]：确定性 PRNG（splitmix64，可逐位移植到 C#）；
//! - [`runtime`]：装配体，`dispatch(opcode, payload)` 是唯一入口；
//! - [`pool`]：并行任务池（扫描任务在 Rust 工作线程跑，游戏主线程只提交与回收）；
//! - [`scan`]：扫描任务本体（直方图 / 模式匹配 / 密度热点 / 点积，可分步可抢占）；
//! - [`gpu`]：GPU 后端（能力探测 + 自检对拍 + 自动回退 CPU）；
//! - [`budget`]：全局资源预算（太多 mod 时不让任一 mod 独占旁路层）；
//! - [`ffi`]：C ABI（`tt_rt_*`），panic 全拦，只回状态码。
//!
//! 三进制语义贯穿全 crate：`False = -1`（减少/移除）、`Unknown = 0`
//! （不变/弃权/未驱动）、`True = +1`（增加/设置）。
//!
//! 依赖说明：三进制原语全部来自本地库 `third_party/trit`（bfxh/trit 子模块，
//! 路径依赖、只读调用），本 crate 不重新实现其任何算法。

pub mod arbiter;
pub mod budget;
pub mod error;
pub mod ffi;
pub mod gpu;
pub mod pool;
pub mod prng;
pub mod router;
pub mod runtime;
pub mod scan;
pub mod snapshot;
pub mod txn;
pub mod wire;

pub use error::RtError;
pub use gpu::{GpuConfig, GpuPolicy, GpuStatus};
pub use pool::{JobPool, ParallelConfig};
pub use runtime::{RuntimeConfig, TritRuntime};
pub use scan::{JobRecord, JobState, ScanKind, ScanRequest};
pub use wire::{op, query};
