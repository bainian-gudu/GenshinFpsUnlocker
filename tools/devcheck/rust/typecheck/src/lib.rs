//! 生成式类型检查 crate 的根模块。
//!
//! `src/gen/` 下的文件由 `tools/devcheck/devcheck.ps1 -Layer rust` 从
//! `installer/kachina/src-tauri/src/` 生成（不要手改，也不会入库）：
//!
//! - `gen/uninstall.rs` —— `installer/uninstall.rs` 原样复制，只去掉 `#[tauri::command]`
//! - `gen/utils_error.rs` —— `utils/error.rs` 原样复制
//!
//! 本文件负责把这两个文件挂到与上游相同的模块路径上（`crate::utils::error`、
//! `crate::installer::uninstall`），并为上游那两个重量级依赖提供最小桩：
//! `crate::dfs::InsightItem`（只用到类型本身）与 `crate::local::get_base_with_config`
//! （只用到「返回一个能 AsyncRead 的东西」）。
//!
//! 桩与上游不一致时会直接编译失败，所以这个 crate 顺便也盯着上游签名变化。
#![allow(
    dead_code,
    unused_imports,
    unused_variables,
    unused_mut,
    unused_assignments,
    clippy::all
)]

/// 对应上游 `src/dfs.rs` 的 `InsightItem`（字段需保持一致）。
pub mod dfs {
    use serde::{Deserialize, Serialize};

    #[derive(Clone, Deserialize, Serialize, Debug)]
    pub struct InsightItem {
        pub url: String,
        pub ttfb: u32,
        pub time: u32,
        pub size: u32,
        pub error: Option<String>,
        #[serde(default)]
        pub range: Vec<(u32, u32)>,
        #[serde(default)]
        pub mode: Option<String>,
    }
}

#[path = "gen/utils_error.rs"]
pub mod gen_utils_error;

// 上游这里有个 `pub mod sentry { capture_anyhow }` 桩：`utils/error.rs` 序列化时会顺手
// 把错误上报到 Sentry，`super::sentry` 指向 crate 根。本项目已把 Sentry 连依赖一起拔掉
// （installer/kachina/LOCAL_PATCHES.md 第 7 节），error.rs 里那句调用也没了，所以桩不需要。

/// 让 `use crate::utils::error::{return_ta_result, TAResult}` 能解析到真实文件。
pub mod utils {
    pub use crate::gen_utils_error as error;
}

/// 对应上游 `src/local.rs`：真实实现要 mmap 自身并解析内嵌索引，
/// 这里只需满足 `uninstall.rs` 的用法（`tokio::io::copy(&mut reader, ..)`）。
pub mod local {
    pub async fn get_base_with_config() -> anyhow::Result<tokio::io::Empty> {
        Ok(tokio::io::empty())
    }
}

// 注意：这里不能写成 `pub mod installer { #[path = "../gen/uninstall.rs"] ... }`，
// rustc 拼出来的路径需要中间目录真实存在（src/installer/ 并不存在）→ ENOENT。
// 所以先在 crate 根挂上生成文件，再用 re-export 拼出上游的模块路径。
#[path = "gen/uninstall.rs"]
pub mod gen_uninstall;

pub mod installer {
    pub use crate::gen_uninstall as uninstall;
}
