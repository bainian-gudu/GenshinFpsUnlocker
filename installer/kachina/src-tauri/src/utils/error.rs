// 此文件是 anyhow-tauri 库的一部分。

use crate::dfs::InsightItem;
use serde::Serialize;
use std::sync::{Arc, Mutex};

// 下载 错误 constants
pub const DOWNLOAD_STALLED: &str = "DOWNLOAD_STALLED";
pub const DOWNLOAD_TOO_SLOW: &str = "DOWNLOAD_TOO_SLOW";

// 扩展 anyhow::Error
#[derive(Debug)]
pub struct TACommandError {
    pub error: anyhow::Error,
    pub insight: Option<InsightItem>,
}
impl std::error::Error for TACommandError {}
impl std::fmt::Display for TACommandError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(f, "{:#}", self.error)
    }
}

// Tauri 命令的每个响应都需要能通过 serde 序列化为 JSON。
// 因此不能直接返回 anyhow 错误，以下代码提供序列化包装。
impl Serialize for TACommandError {
    fn serialize<S>(&self, serializer: S) -> std::result::Result<S::Ok, S::Error>
    where
        S: serde::Serializer,
    {
        #[derive(Serialize)]
        struct ErrorWithInsight {
            message: String,
            insight: Option<InsightItem>,
        }

        let response = ErrorWithInsight {
            message: format!("{:#}", self.error),
            insight: self.insight.clone(),
        };

        response.serialize(serializer)
    }
}

// 在 anyhow::Error 与 TACommandError 之间转换
impl From<anyhow::Error> for TACommandError {
    fn from(error: anyhow::Error) -> Self {
        Self {
            error,
            insight: None,
        }
    }
}

/// 将其用作命令的返回类型。
///
/// 使用示例：
/// ```
/// #[tauri::command]
/// fn test() -> anyhow_tauri::TAResult<String> {
///     Ok("No error thrown.".into())
/// }
/// ```
///
/// 更多示例见该库仓库的 `/demo/src-tauri/src/main.rs`。
pub type TAResult<T> = std::result::Result<T, TACommandError>;

pub trait IntoTAResult<T> {
    fn into_ta_result(self) -> TAResult<T>;
}

impl<T, E> IntoTAResult<T> for std::result::Result<T, E>
where
    E: Into<anyhow::Error>,
{
    /// 将可转换为 anyhow 错误的类型映射为 TACommandError，供命令调用返回。
    /// 用于简化调用方的错误处理。
    ///
    /// 使用示例：
    /// ```
    /// #[tauri::command]
    /// fn test_into_ta_result() -> anyhow_tauri::TAResult<String> {
    ///     function_that_succeeds().into_ta_result()
    ///     // 也可以写成：
    ///     // Ok(function_that_succeeds()?)
    /// }
    /// ```
    fn into_ta_result(self) -> TAResult<T> {
        self.map_err(|e| TACommandError {
            error: e.into(),
            insight: None,
        })
    }
}
impl<T> IntoTAResult<T> for anyhow::Error {
    /// 将 anyhow 错误映射为 TACommandError，供命令调用返回。
    /// 用于简化调用方的错误处理。
    ///
    /// 使用示例：
    /// ```
    /// #[tauri::command]
    /// fn test_into_ta_result() -> anyhow_tauri::TAResult<String> {
    ///     function_that_succeeds().into_ta_result()
    ///     // 也可以写成：
    ///     // Ok(function_that_succeeds()?)
    /// }
    /// ```
    fn into_ta_result(self) -> TAResult<T> {
        Err(TACommandError {
            error: self,
            insight: None,
        })
    }
}

pub trait IntoEmptyTAResult<T> {
    /// 用于创建 `Result<(), TACommandError>`（或 `TAResult<()>`）。
    ///
    /// 使用示例：
    /// ```
    /// #[tauri::command]
    /// fn test_into_ta_empty_result() -> anyhow_tauri::TAResult<()> {
    ///     anyhow::anyhow!("Showcase of the .into_ta_empty_result()").into_ta_empty_result()
    /// }
    /// ```
    fn into_ta_empty_result(self) -> TAResult<T>;
}
impl IntoEmptyTAResult<()> for anyhow::Error {
    fn into_ta_empty_result(self) -> TAResult<()> {
        Err(TACommandError {
            error: self,
            insight: None,
        })
    }
}

pub trait IntoAnyhow<T> {
    // 转换 TAResult<T> 到 anyhow::Result<T>
    fn into_anyhow(self) -> std::result::Result<T, anyhow::Error>;
}
impl<T> IntoAnyhow<T> for TAResult<T> {
    fn into_anyhow(self) -> std::result::Result<T, anyhow::Error> {
        self.map_err(|e| e.error)
    }
}

pub fn return_ta_result<T>(msg: String, ctx: &str) -> TAResult<T> {
    Err(TACommandError {
        error: anyhow::anyhow!(msg).context(ctx.to_string()),
        insight: None,
    })
}

pub fn return_anyhow_result<T>(msg: String, ctx: &str) -> anyhow::Result<T> {
    Err(anyhow::anyhow!(msg).context(ctx.to_string()))
}

impl TACommandError {
    pub fn new(error: anyhow::Error) -> Self {
        Self {
            error,
            insight: None,
        }
    }

    pub fn with_insight(error: anyhow::Error, insight: InsightItem) -> Self {
        Self {
            error,
            insight: Some(insight),
        }
    }

    pub fn with_insight_handle(
        error: anyhow::Error,
        insight_handle: Arc<Mutex<InsightItem>>,
    ) -> Self {
        let insight = if let Ok(insight) = insight_handle.lock() {
            Some(insight.clone())
        } else {
            None
        };

        Self { error, insight }
    }
}
