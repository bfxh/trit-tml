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

#[cfg(test)]
mod tests {
    use super::*;
    use crate::arbiter::LaneDispute;
    use crate::ffi;

    fn dispute(key: u32, txn: u64) -> LaneDispute {
        LaneDispute {
            key,
            drivers: vec![(txn, trit_core::value::Ternary::True)],
        }
    }

    /// 覆盖 `RtError` 全部 9 个变体，避免新增变体时漏配状态码。
    fn all_variants() -> Vec<RtError> {
        vec![
            RtError::BadHandle(7),
            RtError::Capacity("满".into()),
            RtError::Decode("坏".into()),
            RtError::TxnNotFound(1),
            RtError::SnapshotNotFound(2),
            RtError::SubNotFound(3),
            RtError::TxnState("已关".into()),
            RtError::Conflict {
                count: 0,
                disputes: vec![],
            },
            RtError::InvalidInput("环".into()),
        ]
    }

    #[test]
    fn every_variant_maps_to_a_defined_status_code() {
        let expected = [
            ffi::TT_ERR_BAD_HANDLE,
            ffi::TT_ERR_CAPACITY,
            ffi::TT_ERR_DECODE,
            ffi::TT_ERR_NOT_FOUND,
            ffi::TT_ERR_NOT_FOUND,
            ffi::TT_ERR_NOT_FOUND,
            ffi::TT_ERR_STATE,
            ffi::TT_ERR_CONFLICT,
            ffi::TT_ERR_ARG,
        ];
        for (err, code) in all_variants().into_iter().zip(expected) {
            assert_eq!(err.status_code(), code, "{err:?} 状态码映射错误");
            // 状态码必须落在健壮性矩阵约定的 [-10, 0] 集合内，且不是 PANIC。
            assert!((-10..=0).contains(&code));
            assert_ne!(code, ffi::TT_ERR_PANIC);
        }
    }

    #[test]
    fn three_not_found_variants_share_one_code() {
        // TxN/Snapshot/Sub 三类「找不到」都折叠成 NOT_FOUND，供 C# 统一处理。
        for err in [
            RtError::TxnNotFound(1),
            RtError::SnapshotNotFound(2),
            RtError::SubNotFound(3),
        ] {
            assert_eq!(err.status_code(), ffi::TT_ERR_NOT_FOUND);
        }
    }

    #[test]
    fn reason_strips_display_prefix_for_free_text_variants() {
        // Display 带「解码失败：」这类前缀，但 reason() 必须返回裸原因——
        // 影子差分逐字节比对任务记录里的 error 字段，前缀差一字符就一直报假分歧。
        let cases = [
            (RtError::Capacity("订阅超限".into()), "订阅超限"),
            (RtError::Decode("长度不足".into()), "长度不足"),
            (RtError::TxnState("重复提交".into()), "重复提交"),
            (RtError::InvalidInput("依赖成环".into()), "依赖成环"),
        ];
        for (err, bare) in cases {
            assert_ne!(err.reason(), err.to_string(), "{err:?} 应去掉前缀");
            assert_eq!(err.reason(), bare);
        }
    }

    #[test]
    fn reason_keeps_full_display_for_structured_variants() {
        // 结构化变体没有「带前缀的裸原因」概念，reason 直接等于 Display。
        for err in [
            RtError::BadHandle(7),
            RtError::TxnNotFound(1),
            RtError::SnapshotNotFound(2),
            RtError::SubNotFound(3),
        ] {
            assert_eq!(err.reason(), err.to_string(), "{err:?} 应保留完整 Display");
        }
    }

    #[test]
    fn conflict_reason_reports_dispute_count_and_is_clone_eq() {
        let err = RtError::Conflict {
            count: 2,
            disputes: vec![dispute(10, 1), dispute(20, 2)],
        };
        assert_eq!(err.status_code(), ffi::TT_ERR_CONFLICT);
        assert!(err.to_string().contains('2'), "冲突文案应含争议车道数");
        assert_eq!(err.reason(), err.to_string());
        // Clone + PartialEq：parity 对拍会克隆错误做比对。
        assert_eq!(err.clone(), err);
    }
}
