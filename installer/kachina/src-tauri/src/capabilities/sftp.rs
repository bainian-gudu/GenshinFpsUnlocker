// ====================================================================
// SFTP 下载中间件
//
// 拦截 sftp:// URL，并将文件内容包装为 HTTP 响应，
// 复用 SshMiddleware 的共享 SSH 连接池。
//
// 相关实现：URL format:
//   sftp://host:port/remote/path#user=xxx&pass=yyy&fingerprint=sha256hex
// ====================================================================

use std::collections::HashMap;
use std::sync::Arc;
use std::time::Instant;

use async_stream::try_stream;
use bytes::Bytes;
use futures::TryStreamExt;
use http::header::{ACCEPT_RANGES, CONTENT_LENGTH, CONTENT_RANGE, RANGE};
use reqwest_middleware::{Middleware, Next};
use russh_sftp::client::SftpSession;
use tokio::io::{AsyncReadExt, AsyncSeekExt};
use tracing::{debug, warn};

use super::ssh::{
    is_recoverable_ssh_error, mw_err, normalize_hex, percent_decode, ActiveStreamGuard, PoolKey,
    SshMiddleware, SshPoolInner, SshUrlParts,
};

// ====================================================================
// URL 解析
// ====================================================================

struct SftpUrlParts {
    user: String,
    pass: String,
    host: String,
    port: u16,
    fingerprint: String,
    remote_path: String,
}

impl SftpUrlParts {
    fn pool_key(&self) -> PoolKey {
        PoolKey {
            host: self.host.to_ascii_lowercase(),
            port: self.port,
            user: self.user.clone(),
            fingerprint: self.fingerprint.clone(),
        }
    }

    /// 构造 `SshUrlParts`，以复用 `SshMiddleware::get_session`。
    fn as_ssh_url_parts(&self) -> SshUrlParts {
        SshUrlParts {
            ssh_user: self.user.clone(),
            ssh_pass: self.pass.clone(),
            ssh_host: self.host.clone(),
            ssh_port: self.port,
            fingerprint: self.fingerprint.clone(),
            // get_session 不使用此字段，但结构体要求提供
            internal_host: String::new(),
            internal_port: 0,
            http_path_and_query: String::new(),
        }
    }
}

fn parse_sftp_url(url: &reqwest::Url) -> anyhow::Result<SftpUrlParts> {
    anyhow::ensure!(url.scheme() == "sftp", "not an sftp:// URL");

    let host_raw = url
        .host_str()
        .ok_or_else(|| anyhow::anyhow!("sftp URL missing host"))?;
    // 相关处理说明
    let host = host_raw
        .strip_prefix('[')
        .and_then(|s| s.strip_suffix(']'))
        .unwrap_or(host_raw)
        .to_string();
    let port = url.port().unwrap_or(22);

    let fragment = url.fragment().unwrap_or("");
    let mut user = String::new();
    let mut pass = String::new();
    let mut fingerprint = String::new();

    for pair in fragment.split('&') {
        if let Some((k, v)) = pair.split_once('=') {
            match k {
                "user" => user = percent_decode(v)?,
                "pass" => pass = percent_decode(v)?,
                "fingerprint" => fingerprint = normalize_hex(v),
                _ => {}
            }
        }
    }

    anyhow::ensure!(!user.is_empty(), "sftp URL missing user= in fragment");
    anyhow::ensure!(
        !fingerprint.is_empty(),
        "sftp URL missing fingerprint= in fragment"
    );

    // 对远程路径进行百分号解码并校验
    let raw_path = url.path().to_string();
    let remote_path = percent_decode(&raw_path).unwrap_or(raw_path);
    anyhow::ensure!(
        remote_path.len() > 1,
        "sftp URL path must refer to a file, not root"
    );

    Ok(SftpUrlParts {
        user,
        pass,
        host,
        port,
        fingerprint,
        remote_path,
    })
}

// ====================================================================
// SFTP 会话 连接池
// ====================================================================

/// SFTP 会话按（PoolKey，SSH 连接标识）缓存。
/// 因此同一主机可以建立多个 SSH 连接，每个连接都有
/// 独立的 SFTP 会话，从而利用多连接并行下载，
/// 同时遵守 SSH 连接池的 MAX_STREAMS_PER_SESSION 限制。
type SftpCacheKey = (PoolKey, usize); // usize = Arc::as_ptr() 的 SSH active_streams 计数器

struct SftpSessionEntry {
    session: Arc<SftpSession>,
    last_used: Instant,
}

// ====================================================================
// 相关实现：SFTP middleware
// ====================================================================

const SFTP_SESSION_TIMEOUT: u64 = 60; // 秒

pub struct SftpMiddleware {
    ssh_pool: Arc<SshPoolInner>,
    sftp_pool: tokio::sync::Mutex<HashMap<SftpCacheKey, SftpSessionEntry>>,
}

impl SftpMiddleware {
    pub fn new(ssh_pool: Arc<SshPoolInner>) -> Self {
        Self {
            ssh_pool,
            sftp_pool: tokio::sync::Mutex::new(HashMap::new()),
        }
    }

    // ---- 连接池 helpers -----------------------------------------------

    fn sweep_sftp_pool(&self, pool: &mut HashMap<SftpCacheKey, SftpSessionEntry>) {
        let now = Instant::now();
        let idle = self.ssh_pool.idle_timeout;
        pool.retain(|_k, entry| now.duration_since(entry.last_used) < idle);
    }

    async fn evict_sftp(&self, key: &PoolKey) {
        // 移除此主机的全部 SFTP 会话（涵盖所有 SSH 连接）
        self.sftp_pool
            .lock()
            .await
            .retain(|(k, _conn_id), _| k != key);
    }

    /// 获取或创建 SFTP 会话。
    ///
    /// 始终先调用 `SshMiddleware::get_session`，该方法会：
    /// - 遵守 `MAX_STREAMS_PER_SESSION` 限制，按需轮换到新的 SSH 连接
    /// - 返回与该 SSH 连接对应的 `ActiveStreamGuard`
    ///
    /// 随后按 SSH 连接标识缓存 SFTP 会话，因此：
    /// - 将并发下载分散到多个 SSH 连接
    /// - 每个 SSH 连接至多拥有一个 SFTP 会话（通道）
    /// - 守卫始终对应 SFTP 会话所使用的 SSH 连接
    async fn get_or_create_sftp_session(
        &self,
        parts: &SftpUrlParts,
    ) -> anyhow::Result<(Arc<SftpSession>, ActiveStreamGuard)> {
        // 步骤 1: Always 获取 SSH 处理 (respects MAX_STREAMS, 可能 rotate connections)
        let ssh_mw = SshMiddleware::with_pool(Arc::clone(&self.ssh_pool));
        let ssh_parts = parts.as_ssh_url_parts();
        let (ssh_handle, stream_guard) = ssh_mw.get_session(&ssh_parts).await?;

        // 连接标识：该 SSH 连接的 active_streams 计数器的唯一指针
        let conn_id = Arc::as_ptr(&stream_guard.counter) as usize;
        let cache_key = (parts.pool_key(), conn_id);

        // 步骤 2：检查指定 SSH 连接的 SFTP 缓存
        {
            let mut sftp_sessions = self.sftp_pool.lock().await;
            self.sweep_sftp_pool(&mut sftp_sessions);
            if let Some(entry) = sftp_sessions.get_mut(&cache_key) {
                entry.last_used = Instant::now();
                // get_session 已经保护此 SSH 连接 ✓
                return Ok((Arc::clone(&entry.session), stream_guard));
            }
        }
        // 已释放锁

        // 步骤 3: 创建 SFTP 会话 (无 锁 held)
        let channel = ssh_handle.channel_open_session().await?;
        channel.request_subsystem(true, "sftp").await?;
        let sftp = SftpSession::new(channel.into_stream()).await?;
        // russh-sftp 默认超时为 10 秒，对慢速网络过短
        sftp.set_timeout(SFTP_SESSION_TIMEOUT).await;
        let sftp = Arc::new(sftp);

        // 步骤 4: Race-安全 插入
        {
            let mut sftp_sessions = self.sftp_pool.lock().await;
            if let Some(entry) = sftp_sessions.get_mut(&cache_key) {
                // 其他任务已为此连接创建会话，复用该会话
                entry.last_used = Instant::now();
                return Ok((Arc::clone(&entry.session), stream_guard));
            }
            sftp_sessions.insert(
                cache_key,
                SftpSessionEntry {
                    session: Arc::clone(&sftp),
                    last_used: Instant::now(),
                },
            );
        }

        Ok((sftp, stream_guard))
    }

    // ---- 范围 parsing ----------------------------------------------

    /// 解析仅包含单个范围的 `Range` 请求头。
    /// 成功时返回 `Some((start, Option<end>))`。
    /// 对于多范围（逗号分隔）、后缀范围（-N）或
    /// 无法解析的值返回 `None`，调用方应返回 416 响应。
    fn parse_single_range(header_value: &str) -> Option<(u64, Option<u64>)> {
        let s = header_value.strip_prefix("bytes=")?;
        // 拒绝多范围请求
        if s.contains(',') {
            return None;
        }
        let (start_s, end_s) = s.split_once('-')?;
        // 拒绝“-500”这样的后缀范围
        if start_s.is_empty() {
            return None;
        }
        let start: u64 = start_s.parse().ok()?;
        let end = if end_s.is_empty() {
            None
        } else {
            Some(end_s.parse::<u64>().ok()?)
        };
        Some((start, end))
    }

    /// 构造 416“请求范围无法满足”响应。
    fn build_416_response(total_size: u64) -> anyhow::Result<reqwest::Response> {
        let http_resp = http::Response::builder()
            .status(416)
            .header(CONTENT_RANGE, format!("bytes */{total_size}"))
            .header(CONTENT_LENGTH, 0)
            .body(reqwest::Body::from(vec![]))
            .map_err(|e| anyhow::anyhow!("SFTP: failed to build 416 response: {e}"))?;
        Ok(reqwest::Response::from(http_resp))
    }

    // ---- core SFTP 下载 -----------------------------------------

    async fn sftp_request_once(
        sftp: &Arc<SftpSession>,
        stream_guard: ActiveStreamGuard,
        remote_path: &str,
        range_header: Option<&str>,
    ) -> anyhow::Result<reqwest::Response> {
        // 通过 stat 获取总大小，供 Content-Range 和未指定结束位置的范围使用
        let metadata = sftp.metadata(remote_path).await?;
        let total_size = metadata
            .size
            .ok_or_else(|| anyhow::anyhow!("SFTP: server did not return file size"))?;

        // 计算范围；对无效范围或多范围请求返回 416
        let (offset, limit_len, status) = if let Some(range_val) = range_header {
            match Self::parse_single_range(range_val) {
                Some((start, end_opt)) => {
                    let end = end_opt.unwrap_or(total_size.saturating_sub(1));
                    if start > end || start >= total_size {
                        return Self::build_416_response(total_size);
                    }
                    let clamped_end = end.min(total_size - 1);
                    (start, clamped_end - start + 1, 206u16)
                }
                None => {
                    // 无法解析、多范围或后缀范围请求均返回 416
                    warn!(
                        range = range_val,
                        "SFTP: rejecting unsupported Range header"
                    );
                    return Self::build_416_response(total_size);
                }
            }
        } else {
            (0, total_size, 200)
        };

        // 打开 + 定位
        let mut file = sftp
            .open_with_flags(remote_path, russh_sftp::protocol::OpenFlags::READ)
            .await?;
        if offset > 0 {
            file.seek(std::io::SeekFrom::Start(offset)).await?;
        }

        debug!(offset, limit_len, status, total_size, "SFTP: serving file");

        // 流式响应体：捕获的守卫用于保持 SSH/SFTP 存活
        let sftp_clone = Arc::clone(sftp);
        let body_stream = try_stream! {
            let _guard = stream_guard;     // 保持 SSH 连接池 条目 存活
            let _session = sftp_clone;     // 保持 SftpSession 存活
            let mut buf = vec![0u8; 256 * 1024]; // 相关实现：256 KB chunks
            let mut remaining = limit_len;
            while remaining > 0 {
                let to_read = (remaining as usize).min(buf.len());
                let n = file.read(&mut buf[..to_read]).await?;
                if n == 0 {
                    break;
                }
                remaining -= n as u64;
                yield Bytes::copy_from_slice(&buf[..n]);
            }
            if remaining > 0 {
                Err::<Bytes, std::io::Error>(std::io::Error::new(
                    std::io::ErrorKind::UnexpectedEof,
                    format!(
                        "SFTP: short read — expected {limit_len} bytes, got {}",
                        limit_len - remaining
                    ),
                ))?;
            }
        };

        // 构建 HTTP 响应
        let mut builder = http::Response::builder()
            .status(status)
            .header(CONTENT_LENGTH, limit_len)
            .header(ACCEPT_RANGES, "bytes");

        if status == 206 {
            let end = offset + limit_len - 1;
            builder = builder.header(CONTENT_RANGE, format!("bytes {offset}-{end}/{total_size}"));
        }

        let http_resp = builder
            .body(reqwest::Body::wrap_stream(
                body_stream.map_err(|e: std::io::Error| e),
            ))
            .map_err(|e| anyhow::anyhow!("SFTP: failed to build response: {e}"))?;

        Ok(reqwest::Response::from(http_resp))
    }

    /// 顶层请求处理器：先尝试一次，仅在可恢复的传输错误后重试。
    async fn sftp_request(
        &self,
        req: reqwest::Request,
    ) -> reqwest_middleware::Result<reqwest::Response> {
        let parts =
            parse_sftp_url(req.url()).map_err(|e| mw_err(format!("SFTP URL parse: {e}")))?;
        let key = parts.pool_key();

        let range_header = req
            .headers()
            .get(RANGE)
            .and_then(|v| v.to_str().ok())
            .map(String::from);

        // 第一次尝试
        match self.try_sftp(&parts, range_header.as_deref()).await {
            Ok(resp) => return Ok(resp),
            Err(e) if Self::is_recoverable_transport_error(&e) => {
                warn!("SFTP: recoverable error, retrying: {e:#}");
                self.evict_sftp(&key).await;
                SshMiddleware::with_pool(Arc::clone(&self.ssh_pool)).evict(&key);
            }
            Err(e) => return Err(mw_err(format!("SFTP: {e:#}"))),
        }

        // 重试
        self.try_sftp(&parts, range_header.as_deref())
            .await
            .map_err(|e| mw_err(format!("SFTP retry: {e:#}")))
    }

    async fn try_sftp(
        &self,
        parts: &SftpUrlParts,
        range_header: Option<&str>,
    ) -> anyhow::Result<reqwest::Response> {
        let (sftp, guard) = self.get_or_create_sftp_session(parts).await?;
        Self::sftp_request_once(&sftp, guard, &parts.remote_path, range_header).await
    }

    fn is_recoverable_transport_error(err: &anyhow::Error) -> bool {
        // 检查 用于 russh transport errors
        if let Some(ssh_err) = err.downcast_ref::<russh::Error>() {
            return is_recoverable_ssh_error(ssh_err);
        }
        // 检查 用于 SFTP-级别 transport errors
        if let Some(sftp_err) = err.downcast_ref::<russh_sftp::client::error::Error>() {
            return matches!(
                sftp_err,
                russh_sftp::client::error::Error::IO(_) | russh_sftp::client::error::Error::Timeout
            );
        }
        // 检查 I/O 错误
        if let Some(io_err) = err.downcast_ref::<std::io::Error>() {
            return matches!(
                io_err.kind(),
                std::io::ErrorKind::ConnectionReset
                    | std::io::ErrorKind::ConnectionAborted
                    | std::io::ErrorKind::BrokenPipe
                    | std::io::ErrorKind::TimedOut
            );
        }
        false
    }
}

#[async_trait::async_trait]
impl Middleware for SftpMiddleware {
    async fn handle(
        &self,
        req: reqwest::Request,
        extensions: &mut http::Extensions,
        next: Next<'_>,
    ) -> reqwest_middleware::Result<reqwest::Response> {
        if req.url().scheme() != "sftp" {
            return next.run(req, extensions).await;
        }
        self.sftp_request(req).await
    }
}
