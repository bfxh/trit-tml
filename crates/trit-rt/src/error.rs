//! 运行时错误类型。所有错误都能映射成一个 C ABI 状态码，
//! 让 C# 兼容壳按状态码决定「回退到原 C# 路径」还是「上报诊断」。

use crate::arbiter::LaneDispute;
use thiserror::Error;

/// 运行时错误。
#[derive(Debug, Clone, PartialEq, Eq, Error)]
pub enum RtError {
    /// 句柄为 0 或不存在。
    #[error("句柄不存在：{0}")]
    BadHandle(u64),

    /// 超过容量上限（订阅数 / 并发事务数 / 快照数）。
    #[error("容量超限：{0}")]
    Capacity(String),

    /// 线格式解码失败（长度不足、非法字节、版本不符）。
    #[error("解码失败：{0}")]
    Decode(String),

    /// 事务不存在或已关闭。
    #[error("事务不存在：{0}")]
    TxnNotFound(u64),

    /// 快照不存在。
    #[error("快照不存在：{0}")]
    SnapshotNotFound(u64),

    /// 订阅不存在。
    #[error("订阅不存在：{0}")]
    SubNotFound(u64),

    /// 事务状态非法（重复提交 / 回滚已提交事务）。
    #[error("事务状态非法：{0}")]
    TxnState(String),

    /// 冲突仲裁未通过；携带争议条数与全部争议车道（每个车道列出所有肇事方）。
    #[error("冲突未仲裁：{count} 条车道争议")]
    Conflict {
        /// 争议车道数。
        count: usize,
        /// 全部争议车道。
        disputes: Vec<LaneDispute>,
    },

    /// 输入非法（依赖成环、维度不符、越界参数）。
    #[error("输入非法：{0}")]
    InvalidInput(String),
}

impl RtError {
    /// 对应的 C ABI 状态码（见 [`crate::ffi`] 的 `TT_*` 常量）。
    pub fn status_code(&self) -> i32 {
        match self {
            Self::BadHandle(_) => crate::ffi::TT_ERR_BAD_HANDLE,
            Self::Capacity(_) => crate::ffi::TT_ERR_CAPACITY,
            Self::Decode(_) => crate::ffi::TT_ERR_DECODE,
            Self::TxnNotFound(_) | Self::SnapshotNotFound(_) | Self::SubNotFound(_) => {
                crate::ffi::TT_ERR_NOT_FOUND
            }
            Self::TxnState(_) => crate::ffi::TT_ERR_STATE,
            Self::Conflict { .. } => crate::ffi::TT_ERR_CONFLICT,
            Self::InvalidInput(_) => crate::ffi::TT_ERR_ARG,
        }
    }

    /// **去掉类型前缀后的原始原因**。
    ///
    /// `Display` 会带「输入非法：」这类前缀，但任务记录里的 `error` 字段
    /// 必须与托管回退内核**逐字相同**——托管侧的文案没有前缀。
    /// 影子差分是逐字节比对响应体的，前缀差一个字符就等于一直报假分歧。
    pub fn reason(&self) -> String {
        match self {
            Self::Capacity(reason)
            | Self::Decode(reason)
            | Self::TxnState(reason)
            | Self::InvalidInput(reason) => reason.clone(),
            _ => self.to_string(),
        }
    }
}
