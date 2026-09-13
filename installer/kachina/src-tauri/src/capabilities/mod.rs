//! kachina-installer 的 H3（基于 QUIC 的 HTTP/3）能力管理。
//!
//! 此模块提供：
//! - `init()`：启动时检查 H3 是否可用（Win11+、无代理、MsQuic 正常）
//! - `is_h3_available()` / `disable_h3()`：运行时 H3 状态管理
//! - `DynamicUaMiddleware`：注入 User-Agent，并在 H3 可用时加入 h3/enabled
//! - `H3FallbackMiddleware`：拦截 http3:// URL，并在失败时回退

pub(crate) mod h3;
pub(crate) mod sftp;
pub(crate) mod ssh;

use self::h3::H3Middleware;
use async_trait::async_trait;
use http::Extensions;
use reqwest::{Request, Response};
use reqwest_middleware::{Middleware, Next, Result};
use std::sync::atomic::{AtomicBool, Ordering};
use std::time::Duration;

// ══════════════════════════════════════════════════════════════════════════════
// 全局 H3 可用状态
// ══════════════════════════════════════════════════════════════════════════════

static H3_AVAILABLE: AtomicBool = AtomicBool::new(false);

/// 返回当前会话是否可以使用 H3。
pub fn is_h3_available() -> bool {
    H3_AVAILABLE.load(Ordering::Relaxed)
}

/// 在当前会话中永久禁用 H3（可重复调用）。
/// 首次 H3 连接失败时调用。
pub fn disable_h3() {
    H3_AVAILABLE.store(false, Ordering::Relaxed);
    tracing::warn!("[H3] Disabled for this session");
}

// ══════════════════════════════════════════════════════════════════════════════
// 启动探测
// ══════════════════════════════════════════════════════════════════════════════

/// 启动时探测 H3 支持情况，返回 H3 是否可用。
/// 在内部设置 `H3_AVAILABLE`。
pub fn init() -> bool {
    let ok = probe_h3_support();
    H3_AVAILABLE.store(ok, Ordering::Relaxed);
    ok
}

fn probe_h3_support() -> bool {
    // 1. Win11+ 检查 (MsQuic QUIC 使用 Schannel requires 构建 >= 22000)
    let (major, minor, build) = nt_version::get();
    let build_num = build & 0xffff;
    if !(major == 10 && minor == 0 && build_num >= 22000) {
        tracing::info!(
            "[H3] Not Win11 (build={}, need 22000+), disabled",
            build_num
        );
        return false;
    }

    // 2. 检查系统代理：H3 不支持通过代理连接
    if has_system_proxy() {
        tracing::info!("[H3] System proxy detected, disabled");
        return false;
    }

    // 3. MsQuic availability 探测 — 创建 Registration 到 验证 DLL + Schannel
    use h3_msquic_async::msquic_async::msquic;
    match msquic::Registration::new(&msquic::RegistrationConfig::default()) {
        Ok(_reg) => {
            tracing::info!("[H3] Probe succeeded, enabled");
            true
        }
        Err(e) => {
            tracing::info!("[H3] Probe failed: {:?}, disabled", e);
            false
        }
    }
}

fn has_system_proxy() -> bool {
    windows_registry::CURRENT_USER
        .open(r"Software\Microsoft\Windows\CurrentVersion\Internet Settings")
        .ok()
        .and_then(|k| k.get_u32("ProxyEnable").ok())
        .map(|v| v != 0)
        .unwrap_or(false)
}

// ══════════════════════════════════════════════════════════════════════════════
// 动态 用户-Agent middleware
// ══════════════════════════════════════════════════════════════════════════════

/// 注入动态 User-Agent 请求头的中间件。
/// H3 可用时加入 "h3/enabled"，供控制端识别。
pub struct DynamicUaMiddleware;

impl Default for DynamicUaMiddleware {
    fn default() -> Self {
        Self::new()
    }
}

impl DynamicUaMiddleware {
    pub fn new() -> Self {
        Self
    }
}

#[async_trait]
impl Middleware for DynamicUaMiddleware {
    async fn handle(
        &self,
        mut req: Request,
        ext: &mut Extensions,
        next: Next<'_>,
    ) -> Result<Response> {
        let ua = ua_string();
        if let Ok(value) = http::HeaderValue::from_str(&ua) {
            req.headers_mut().insert(http::header::USER_AGENT, value);
        }
        next.run(req, ext).await
    }
}

/// 生成 User-Agent 字符串，并按需附加 h3/enabled 后缀。
pub fn ua_string() -> String {
    let (major, minor, build) = nt_version::get();
    let cpu_cores = num_cpus::get();
    let wv2ver = tauri::webview_version().unwrap_or_else(|_| "Unknown".to_string());

    let mut ua = format!(
        "KachinaInstaller/{} Webview2/{} Windows/{}.{}.{} Threads/{}",
        env!("CARGO_PKG_VERSION"),
        wv2ver,
        major,
        minor,
        build & 0xffff,
        cpu_cores
    );

    ua.push_str(" ssh/enabled");
    ua.push_str(" sftp/enabled");

    if is_h3_available() {
        ua.push_str(" h3/enabled");
    }

    ua
}

// ══════════════════════════════════════════════════════════════════════════════
// H3 回退 middleware
// ══════════════════════════════════════════════════════════════════════════════

/// 拦截 `http3://` URL 并交由 H3Middleware 处理的中间件。
/// 任何 H3 请求失败后，永久禁用当前会话的 H3 并返回错误。
pub struct H3FallbackMiddleware {
    h3: H3Middleware,
}

impl H3FallbackMiddleware {
    pub fn new(idle_timeout: Duration) -> anyhow::Result<Self> {
        let h3 = H3Middleware::new(idle_timeout)?;
        Ok(Self { h3 })
    }

    /// 获取内部 H3Middleware 的引用，用于关闭、发现等操作。
    pub fn inner(&self) -> &H3Middleware {
        &self.h3
    }
}

#[async_trait]
impl Middleware for H3FallbackMiddleware {
    async fn handle(&self, req: Request, ext: &mut Extensions, next: Next<'_>) -> Result<Response> {
        // 仅 intercept http3:// 方案
        if req.url().scheme() != "http3" {
            return next.run(req, ext).await;
        }

        // 尝试发起 H3 请求
        match self.h3.h3_request(req).await {
            Ok(resp) => Ok(resp),
            Err(e) => {
                // 首次 H3 失败后禁用当前会话的 H3
                disable_h3();
                Err(e)
            }
        }
    }
}
