//! TritEnginePort — Terraria AI 纯函数端口。
//!
//! 把 Terraria 原版 `NPC.AI` 分支和 `Projectile.Update` 的核心逻辑翻译成 Rust 纯函数。
//! 不是历史外推预测（Phase 1 MVP），而是真的跑原版等价逻辑来算 delta。
//!
//! # 设计
//!
//! - 零第三方依赖：除 `serde` derive（可选），只依赖 Rust 标准库
//! - crate-type = `lib`：不编译独立 cdylib，被 trit-rt 静态链接进同一个 native DLL
//! - 纯函数接口：`npc_ai_step(state: &NpcState, rand: &mut Prng) -> NpcDelta`
//! - 未 port 的 type 走历史外推 fallback（由 trit-rt takeover 模块处理）
//!
//! # 置信度
//!
//! TritEnginePort 跑出来的 delta confidence = 1.0（引擎等价逻辑，理论上和原版完全一致）。
//! 历史外推 confidence 继续对没 port 的 type 用。

pub mod npc;
pub mod prng;
pub mod projectile;
pub mod registry;

// 顶层便捷入口 —— 按 type 分发给已 port 的具体分支
pub use registry::PORTED_NPC_TYPES;
pub use registry::PORTED_PROJECTILE_TYPES;
pub use registry::is_ported_npc_type;
pub use registry::is_ported_projectile_type;

/// NPC AI step 顶层入口。
///
/// 如果 `state.npc_type` 在已 port 列表里，跑引擎等价逻辑；
/// 否则返回 `NpcDelta::UNSET`（调用方 fallback 到历史外推）。
pub fn npc_ai_step(state: &npc::NpcState, rand: &mut prng::Prng) -> npc::NpcDelta {
    if registry::is_ported_npc_type(state.npc_type) {
        // 按 type 分发到具体 port 分支
        npc::dispatch(state, rand)
    } else {
        npc::NpcDelta::UNSET
    }
}

/// Projectile update step 顶层入口。
pub fn projectile_update_step(
    state: &projectile::ProjectileState,
    rand: &mut prng::Prng,
) -> projectile::ProjectileDelta {
    if registry::is_ported_projectile_type(state.proj_type) {
        projectile::dispatch(state, rand)
    } else {
        projectile::ProjectileDelta::UNSET
    }
}
