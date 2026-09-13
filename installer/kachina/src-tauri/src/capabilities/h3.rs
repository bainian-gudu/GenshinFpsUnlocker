use async_trait::async_trait;
use bytes::{Buf, Bytes};
use futures::future::poll_fn;
use h3_msquic_async::msquic;
use h3_msquic_async::msquic_async;
use reqwest_middleware::{Middleware, Next};
use std::collections::HashMap;
use std::mem::ManuallyDrop;
use std::ops::Deref;
use std::sync::atomic::{AtomicUsize, Ordering};
use std::sync::Arc;
use std::time::{Duration, Instant};
use tokio::task::JoinHandle;
use tokio_util::sync::CancellationToken;
use tracing::{debug, trace, warn};

// 为方便用户重新导出
pub use h3_msquic_async::msquic_async::{CertValidator, PeerCertInfo};

// ============================================================
// 固定模式与配置
// ============================================================

/// 控制证书固定校验与系统证书验证之间的关系。
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum PinningMode {
    /// 无论系统是否信任证书，始终检查固定值（默认）。
    /// 即使 Schannel 信任该证书，固定值也必须匹配。
    Force,
    /// 仅在系统（Schannel）不信任证书时检查固定值。
    /// 系统信任证书时直接接受，不检查固定值。
    Add,
}

/// 固定校验的目标：SPKI 哈希或完整证书哈希。
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum PinTarget {
    /// SubjectPublicKeyInfo 的 DER 编码的 SHA-256 哈希。
    Spki([u8; 32]),
    /// 完整证书 DER 编码的 SHA-256 哈希。
    /// 对应命令：`openssl x509 -in cert.crt -outform DER | openssl dgst -sha256 -binary | xxd -p -c 32`
    Cert([u8; 32]),
}

/// 从 URL 片段解析得到的证书固定配置。
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub struct PinConfig {
    pub target: PinTarget,
    pub mode: PinningMode,
}

// ============================================================
// 基于 Windows CryptoAPI 的固定校验器
// ============================================================

#[cfg(target_os = "windows")]
mod win_pin {
    use super::*;
    use std::ffi::c_void;
    use tracing::info;
    use windows::Win32::Security::Cryptography::*;

    /// 根据 PCCERT_CONTEXT 指针，计算 SubjectPublicKeyInfo（SPKI）
    /// DER 编码的 SHA-256 哈希；任何步骤失败均返回 None。
    ///
    /// # 安全要求
    /// `certificate` 必须是有效的 PCCERT_CONTEXT 指针，且只能在
    /// MsQuic 的 PeerCertificateReceived 回调期间调用（受指针生命周期限制）。
    unsafe fn compute_spki_hash(certificate: *mut c_void) -> Option<[u8; 32]> {
        if certificate.is_null() {
            return None;
        }

        // 转换为 windows crate 的 CERT_CONTEXT（布局和对齐均正确）
        let cert_ctx = &*(certificate as *const CERT_CONTEXT);

        // pCertInfo 是 PCERT_INFO，已由 Schannel 解析，始终有效
        // 在回调期间
        let cert_info = cert_ctx.pCertInfo;
        if cert_info.is_null() {
            return None;
        }
        let spki = &(*cert_info).SubjectPublicKeyInfo;

        // 对 SubjectPublicKeyInfo 进行 DER 编码
        let mut spki_der_size: u32 = 0;
        if CryptEncodeObjectEx(
            X509_ASN_ENCODING,
            X509_PUBLIC_KEY_INFO,
            spki as *const _ as *const c_void,
            CRYPT_ENCODE_OBJECT_FLAGS(0),
            None,
            None,
            &mut spki_der_size,
        )
        .is_err()
        {
            debug!("[Pin] CryptEncodeObjectEx size query failed");
            return None;
        }

        let mut spki_der = vec![0u8; spki_der_size as usize];
        if CryptEncodeObjectEx(
            X509_ASN_ENCODING,
            X509_PUBLIC_KEY_INFO,
            spki as *const _ as *const c_void,
            CRYPT_ENCODE_OBJECT_FLAGS(0),
            None,
            Some(spki_der.as_mut_ptr() as *mut c_void),
            &mut spki_der_size,
        )
        .is_err()
        {
            debug!("[Pin] CryptEncodeObjectEx encode failed");
            return None;
        }
        spki_der.truncate(spki_der_size as usize);

        // 通过 CNG 计算 SHA-256 哈希（BCRYPT_SHA256_ALG_HANDLE 是预分配的伪句柄）
        let mut hash = [0u8; 32];
        if BCryptHash(BCRYPT_SHA256_ALG_HANDLE, None, &spki_der, &mut hash).is_err() {
            debug!("[Pin] BCryptHash (SPKI) failed");
            return None;
        }

        Some(hash)
    }

    /// 计算完整证书 DER 编码的 SHA-256 哈希，
    /// 输入为 PCCERT_CONTEXT 指针。
    ///
    /// 等价于以下命令：
    ///   openssl x509 -in cert.crt -outform DER | openssl dgst -sha256 -binary | xxd -p -c 32
    ///
    /// # 安全要求
    /// 与 `compute_spki_hash` 相同。
    unsafe fn compute_cert_hash(certificate: *mut c_void) -> Option<[u8; 32]> {
        if certificate.is_null() {
            return None;
        }

        let cert_ctx = &*(certificate as *const CERT_CONTEXT);

        // pbCertEncoded 与 cbCertEncoded 指定完整证书的 DER 编码
        if cert_ctx.pbCertEncoded.is_null() || cert_ctx.cbCertEncoded == 0 {
            return None;
        }
        let der =
            std::slice::from_raw_parts(cert_ctx.pbCertEncoded, cert_ctx.cbCertEncoded as usize);

        let mut hash = [0u8; 32];
        if BCryptHash(BCRYPT_SHA256_ALG_HANDLE, None, der, &mut hash).is_err() {
            debug!("[Pin] BCryptHash (cert) failed");
            return None;
        }

        Some(hash)
    }

    /// 根据 PinTarget 选择目标，对证书执行固定值校验。
    fn check_pin(info: &PeerCertInfo, config: &PinConfig) -> bool {
        let system_trusts = info.deferred_status.is_ok();

        match config.mode {
            PinningMode::Add if system_trusts => {
                debug!("[Pin] mode=add, system trusts, accepting");
                return true;
            }
            PinningMode::Add => {
                debug!(
                    "[Pin] mode=add, system rejects (status={:#x}), checking pin...",
                    info.deferred_status.0
                );
            }
            PinningMode::Force => {
                debug!(
                    "[Pin] mode=force, system_trusts={}, checking pin...",
                    system_trusts
                );
            }
        }

        match &config.target {
            PinTarget::Spki(expected) => {
                let hash = unsafe { compute_spki_hash(info.certificate) };
                match hash {
                    Some(h) if h == *expected => {
                        debug!("[Pin] SPKI pin MATCHED");
                        true
                    }
                    Some(h) => {
                        warn!(
                            "[Pin] SPKI pin MISMATCH! expected={}, got={}",
                            hex::encode(expected),
                            hex::encode(h)
                        );
                        false
                    }
                    None => {
                        warn!("[Pin] Failed to compute SPKI hash, rejecting");
                        false
                    }
                }
            }
            PinTarget::Cert(expected) => {
                let hash = unsafe { compute_cert_hash(info.certificate) };
                match hash {
                    Some(h) if h == *expected => {
                        debug!("[Pin] Cert pin MATCHED");
                        true
                    }
                    Some(h) => {
                        warn!(
                            "[Pin] Cert pin MISMATCH! expected={}, got={}",
                            hex::encode(expected),
                            hex::encode(h)
                        );
                        false
                    }
                    None => {
                        warn!("[Pin] Failed to compute cert hash, rejecting");
                        false
                    }
                }
            }
        }
    }

    /// 使用 PinConfig（目标和模式）的 CertValidator。
    pub struct PinValidator {
        pub config: PinConfig,
    }

    impl CertValidator for PinValidator {
        fn validate(&self, info: &PeerCertInfo) -> bool {
            check_pin(info, &self.config)
        }
    }

    /// 从服务器证书中发现的哈希值。
    #[derive(Debug, Clone, Default)]
    pub struct DiscoveredHashes {
        pub spki: Option<[u8; 32]>,
        pub cert: Option<[u8; 32]>,
    }

    /// 接受所有证书并捕获所计算哈希值的 CertValidator。
    /// 用于发现服务器证书的 SPKI/完整证书哈希。
    pub struct DiscoveryValidator {
        pub results: std::sync::Arc<std::sync::Mutex<DiscoveredHashes>>,
    }

    impl DiscoveryValidator {
        pub fn new() -> (Self, std::sync::Arc<std::sync::Mutex<DiscoveredHashes>>) {
            let results = std::sync::Arc::new(std::sync::Mutex::new(DiscoveredHashes::default()));
            (
                Self {
                    results: results.clone(),
                },
                results,
            )
        }
    }

    impl CertValidator for DiscoveryValidator {
        fn validate(&self, info: &PeerCertInfo) -> bool {
            let system_trusts = info.deferred_status.is_ok();
            info!(
                "[Discovery] system_trusts={}, deferred_status={:#x}, flags={:#x}",
                system_trusts, info.deferred_status.0, info.deferred_error_flags
            );

            let spki = unsafe { compute_spki_hash(info.certificate) };
            match spki {
                Some(h) => info!("[Discovery] SPKI SHA-256: {}", hex::encode(h)),
                None => warn!("[Discovery] Failed to compute SPKI hash"),
            }

            let cert = unsafe { compute_cert_hash(info.certificate) };
            match cert {
                Some(h) => info!("[Discovery] Cert SHA-256: {}", hex::encode(h)),
                None => warn!("[Discovery] Failed to compute cert hash"),
            }

            if let Ok(mut results) = self.results.lock() {
                results.spki = spki;
                results.cert = cert;
            }

            true // 发现模式下仍然接受
        }
    }
}

#[cfg(target_os = "windows")]
pub use win_pin::{DiscoveredHashes, DiscoveryValidator, PinValidator};

// ============================================================
// URL 片段解析器：#spki={hex}&cert={hex}&pinning_mode=force|add
// ============================================================

/// 从 URL 片段解析证书固定配置。
///
/// 支持的格式：
///   `#spki={hex64}`                           → PinConfig { target: Spki, mode: Force }
///   `#spki={hex64}&pinning_mode=add`          → PinConfig { target: Spki, mode: Add }
///   `#cert={hex64}`                           → PinConfig { target: Cert, mode: Force }
///   `#cert={hex64}&pinning_mode=force`        → PinConfig { target: Cert, mode: Force }
///   `#spki={hex64}&cert={hex64}&pinning_mode=force` → PinConfig { target: Cert, mode: Force }
///                                               (cert takes priority over spki)
fn parse_pin_from_fragment(url: &url::Url) -> Option<PinConfig> {
    let frag = url.fragment()?;

    let mut spki_hex: Option<&str> = None;
    let mut cert_hex: Option<&str> = None;
    let mut mode = PinningMode::Force; // 默认值

    for part in frag.split('&') {
        if let Some(val) = part.strip_prefix("spki=") {
            spki_hex = Some(val);
        } else if let Some(val) = part.strip_prefix("cert=") {
            cert_hex = Some(val);
        } else if let Some(val) = part.strip_prefix("pinning_mode=") {
            mode = match val {
                "add" => PinningMode::Add,
                _ => PinningMode::Force, // 未知值 → force（安全默认值）
            };
        }
    }

    // 证书优先于 SPKI
    let (hex_str, make_target): (&str, fn([u8; 32]) -> PinTarget) = if let Some(h) = cert_hex {
        (h, PinTarget::Cert)
    } else if let Some(h) = spki_hex {
        (h, PinTarget::Spki)
    } else {
        return None;
    };

    // 保护: SHA-256 hex 必须 为 exactly 64 chars, reject 尽早 到 avoid large alloc
    if hex_str.len() != 64 {
        return None;
    }
    let bytes = hex::decode(hex_str).ok()?;
    let hash = <[u8; 32]>::try_from(bytes.as_slice()).ok()?;
    Some(PinConfig {
        target: make_target(hash),
        mode,
    })
}

// ============================================================
// 修复 1：缩小 SendWrapper 的适用范围，使用专用新类型替代泛型
// ============================================================

#[repr(transparent)]
struct QuicRegistration(msquic::Registration);
unsafe impl Send for QuicRegistration {}
unsafe impl Sync for QuicRegistration {}
impl Deref for QuicRegistration {
    type Target = msquic::Registration;
    fn deref(&self) -> &Self::Target {
        &self.0
    }
}

#[repr(transparent)]
struct QuicConfiguration(msquic::Configuration);
unsafe impl Send for QuicConfiguration {}
unsafe impl Sync for QuicConfiguration {}
impl Deref for QuicConfiguration {
    type Target = msquic::Configuration;
    fn deref(&self) -> &Self::Target {
        &self.0
    }
}

// ============================================================
// 修复 5：使用活动流守卫，避免连接被当作空闲连接移除
// ============================================================

struct ActiveStreamGuard {
    counter: Arc<AtomicUsize>,
}

impl Drop for ActiveStreamGuard {
    fn drop(&mut self) {
        self.counter.fetch_sub(1, Ordering::Relaxed);
    }
}

// ============================================================
// 连接池条目
// ============================================================

pub type H3SendRequest = h3::client::SendRequest<h3_msquic_async::OpenStreams, Bytes>;

/// 连接池中的最大连接数。
const MAX_POOL_SIZE: usize = 32;

struct H3ConnEntry {
    send_request: H3SendRequest,
    cancel_token: CancellationToken,
    driver_handle: JoinHandle<()>,
    close_rx: std::sync::mpsc::Receiver<()>,
    last_used: Instant,
    active_streams: Arc<AtomicUsize>,
}

impl Drop for H3ConnEntry {
    fn drop(&mut self) {
        debug!("[H3ConnEntry] Cancelling and aborting H3 driver task");
        self.cancel_token.cancel();
        self.driver_handle.abort(); // P1-2：同时中止驱动任务
    }
}

// ============================================================
// 修复 2：H3Inner 通过 Arc 共享状态，在响应体存活期间保持连接
// ============================================================

type PoolKey = (String, u16, Option<PinConfig>);

/// 规范化主机名，确保连接池键匹配一致。
/// - 转为小写（DNS 不区分大小写）
/// - 移除 IPv6 方括号（url::Url 会自动添加）
fn normalize_host(host: &str) -> String {
    let h = host.to_ascii_lowercase();
    if h.starts_with('[') && h.ends_with(']') {
        h[1..h.len() - 1].to_string()
    } else {
        h
    }
}

struct H3Inner {
    registration: ManuallyDrop<QuicRegistration>,
    configuration: ManuallyDrop<QuicConfiguration>,
    pool: std::sync::Mutex<HashMap<PoolKey, H3ConnEntry>>,
    idle_timeout: Duration,
}

impl H3Inner {
    fn lock_pool(
        &self,
    ) -> Result<std::sync::MutexGuard<'_, HashMap<PoolKey, H3ConnEntry>>, reqwest_middleware::Error>
    {
        self.pool.lock().map_err(|_| {
            reqwest_middleware::Error::Middleware(anyhow::anyhow!("H3 pool mutex poisoned"))
        })
    }
}

impl Drop for H3Inner {
    fn drop(&mut self) {
        debug!("[H3Inner] Dropping, cleaning up resources");
        if let Ok(mut pool) = self.pool.lock() {
            pool.drain();
        }
        unsafe {
            ManuallyDrop::drop(&mut self.configuration);
            ManuallyDrop::drop(&mut self.registration);
        }
        debug!("[H3Inner] Dropped successfully");
    }
}

// ============================================================
// 相关实现：H3 Middleware
// ============================================================

pub struct H3Middleware {
    inner: Arc<H3Inner>,
}

impl H3Middleware {
    pub fn new(idle_timeout: Duration) -> anyhow::Result<Self> {
        let registration = msquic::Registration::new(&msquic::RegistrationConfig::default())?;

        let idle_ms = idle_timeout.as_millis().min(u64::MAX as u128) as u64;

        let alpn = [msquic::BufferRef::from("h3")];
        let settings = msquic::Settings::new()
            .set_IdleTimeoutMs(idle_ms)
            .set_PeerBidiStreamCount(100)
            .set_PeerUnidiStreamCount(100);

        let configuration = msquic::Configuration::open(&registration, &alpn, Some(&settings))?;
        let cred_config = msquic::CredentialConfig::new_client()
            .set_credential_flags(msquic::CredentialFlags::INDICATE_CERTIFICATE_RECEIVED)
            .set_credential_flags(msquic::CredentialFlags::DEFER_CERTIFICATE_VALIDATION);
        configuration.load_credential(&cred_config)?;

        Ok(Self {
            inner: Arc::new(H3Inner {
                registration: ManuallyDrop::new(QuicRegistration(registration)),
                configuration: ManuallyDrop::new(QuicConfiguration(configuration)),
                pool: std::sync::Mutex::new(HashMap::new()),
                idle_timeout,
            }),
        })
    }

    async fn get_send_request(
        &self,
        host: &str,
        port: u16,
        pin_config: Option<PinConfig>,
    ) -> Result<(H3SendRequest, ActiveStreamGuard), reqwest_middleware::Error> {
        let norm_host = normalize_host(host);
        let key = (norm_host.clone(), port, pin_config);

        // 检查 连接池 (hold std::sync::Mutex briefly, 发布 之前 .等待)
        {
            let mut pool = self.inner.lock_pool()?;

            // P1-1: Sweep 过期 connections 和 enforce max 连接池 大小
            let now = Instant::now();
            let keys_to_evict: Vec<PoolKey> = pool
                .iter()
                .filter_map(|(k, entry)| {
                    let dead = matches!(
                        entry.close_rx.try_recv(),
                        Ok(()) | Err(std::sync::mpsc::TryRecvError::Disconnected)
                    );
                    let driver_done = entry.driver_handle.is_finished();
                    let idle_expired =
                        now.duration_since(entry.last_used) > self.inner.idle_timeout;
                    let active = entry.active_streams.load(Ordering::Relaxed);
                    if dead || driver_done || (idle_expired && active == 0) {
                        Some(k.clone())
                    } else {
                        None
                    }
                })
                .collect();
            for k in keys_to_evict {
                debug!(host = %k.0, port = k.1, "[H3] Sweep: evicting stale connection");
                pool.remove(&k);
            }

            // 移除最旧条目，限制连接池的最大大小
            if pool.len() >= MAX_POOL_SIZE {
                let mut entries: Vec<_> =
                    pool.iter().map(|(k, e)| (k.clone(), e.last_used)).collect();
                entries.sort_by_key(|(_, last_used)| *last_used);
                let to_remove = pool.len() - MAX_POOL_SIZE + 1;
                let keys_to_evict: Vec<PoolKey> = entries
                    .into_iter()
                    .take(to_remove)
                    .map(|(k, _)| k)
                    .collect();
                for k in keys_to_evict {
                    debug!(host = %k.0, port = k.1, "[H3] Pool full: evicting oldest");
                    pool.remove(&k);
                }
            }

            if let Some(entry) = pool.get_mut(&key) {
                let dead = match entry.close_rx.try_recv() {
                    Ok(()) | Err(std::sync::mpsc::TryRecvError::Disconnected) => true,
                    Err(std::sync::mpsc::TryRecvError::Empty) => false,
                };
                let idle_expired =
                    Instant::now().duration_since(entry.last_used) > self.inner.idle_timeout;
                let driver_done = entry.driver_handle.is_finished();
                let active = entry.active_streams.load(Ordering::Relaxed);

                if dead || driver_done || (idle_expired && active == 0) {
                    let reason = if dead {
                        "dead"
                    } else if driver_done {
                        "driver_finished"
                    } else {
                        "idle"
                    };
                    debug!(host = %norm_host, port, reason, "[H3] Evicting stale connection");
                    pool.remove(&key);
                } else {
                    debug!(host = %norm_host, port, "[H3] Reusing existing connection");
                    entry.last_used = Instant::now();
                    entry.active_streams.fetch_add(1, Ordering::Relaxed);
                    let guard = ActiveStreamGuard {
                        counter: Arc::clone(&entry.active_streams),
                    };
                    return Ok((entry.send_request.clone(), guard));
                }
            }
        }
        // 已释放锁

        // 根据 PinConfig 为每个请求创建 CertValidator
        let cert_validator: Option<Arc<dyn CertValidator>> =
            pin_config.map(|cfg| Arc::new(PinValidator { config: cfg }) as Arc<dyn CertValidator>);

        debug!(host = %norm_host, port, pin = ?pin_config, "[H3] Creating new QUIC connection");
        let conn = msquic_async::Connection::new_with_cert_validator(
            &self.inner.registration,
            cert_validator,
        )
        .map_err(|e| {
            warn!(host = %norm_host, port, error = %e, "[H3] Connection::new failed");
            reqwest_middleware::Error::Middleware(e.into())
        })?;

        conn.start(&self.inner.configuration, host, port)
            .await
            .map_err(|e| {
                warn!(host = %norm_host, port, error = %e, "[H3] Connection::start failed");
                reqwest_middleware::Error::Middleware(e.into())
            })?;

        debug!(host = %norm_host, port, "[H3] QUIC connection established");

        let h3_conn = h3_msquic_async::Connection::new(conn);
        let (mut driver, send_request) = h3::client::new(h3_conn).await.map_err(|e| {
            warn!(host = %norm_host, port, error = %e, "[H3] h3 handshake failed");
            reqwest_middleware::Error::Middleware(anyhow::anyhow!("h3 handshake: {}", e))
        })?;

        let cancel_token = CancellationToken::new();
        let child_token = cancel_token.child_token();
        let (close_tx, close_rx) = std::sync::mpsc::channel();

        let driver_handle = tokio::spawn(async move {
            tokio::select! {
                _ = poll_fn(|cx| driver.poll_close(cx)) => {
                    close_tx.send(()).ok();
                    trace!("[driver] poll_close completed");
                }
                _ = child_token.cancelled() => {
                    trace!("[driver] Cancelled");
                }
            }
        });

        let new_entry = H3ConnEntry {
            send_request,
            cancel_token,
            driver_handle,
            close_rx,
            last_used: Instant::now(),
            active_streams: Arc::new(AtomicUsize::new(0)),
        };

        // 以竞态安全方式插入连接池
        {
            let mut pool = self.inner.lock_pool()?;
            if let Some(existing) = pool.get_mut(&key) {
                debug!(host = %norm_host, port, "[H3] Race: using existing connection");
                existing.last_used = Instant::now();
                existing.active_streams.fetch_add(1, Ordering::Relaxed);
                let guard = ActiveStreamGuard {
                    counter: Arc::clone(&existing.active_streams),
                };
                return Ok((existing.send_request.clone(), guard));
            }
            new_entry.active_streams.fetch_add(1, Ordering::Relaxed);
            let guard = ActiveStreamGuard {
                counter: Arc::clone(&new_entry.active_streams),
            };
            let cloned = new_entry.send_request.clone();
            pool.insert(key, new_entry);
            Ok((cloned, guard))
        }
    }

    /// 执行 H3/QUIC 请求。
    ///
    /// 注意：仅支持不带请求体的 GET 请求；传入的请求体会被忽略。
    /// 用于服务器提供内容的文件下载场景。
    pub async fn h3_request(
        &self,
        req: reqwest::Request,
    ) -> Result<reqwest::Response, reqwest_middleware::Error> {
        let method = req.method().clone();
        let headers = req.headers().clone();
        let original_url = req.url().clone();

        let host = original_url
            .host_str()
            .ok_or_else(|| {
                reqwest_middleware::Error::Middleware(anyhow::anyhow!("no host in URL"))
            })?
            .to_string();
        let port = original_url.port().unwrap_or(443);
        let path_and_query = match original_url.query() {
            Some(q) => format!("{}?{}", original_url.path(), q),
            None => original_url.path().to_string(),
        };
        // URI 的主机部分需要用方括号包裹 IPv6 地址，例如 https://[::1]:443/path
        // url.host_str() 返回的 IPv6 地址可能已包含方括号，因此先检查
        let authority_host = if host.starts_with('[') {
            host.clone() // 已经 has brackets
        } else if host.contains(':') {
            format!("[{}]", host) // IPv6 地址尚未包含方括号
        } else {
            host.clone() // IPv4 地址或主机名
        };
        let h3_uri = format!("https://{}:{}{}", authority_host, port, path_and_query);

        // 从 URL 片段解析固定配置：#spki={hex}&cert={hex}&pinning_mode=force|add
        let pin_config = parse_pin_from_fragment(&original_url);

        debug!(url = %original_url, h3_uri = %h3_uri, pin = ?pin_config, "[H3] Intercepted");

        let (mut send_request, stream_guard) =
            self.get_send_request(&host, port, pin_config).await?;

        let mut h3_req_builder = http::Request::builder()
            .method(method.as_str())
            .uri(&h3_uri);
        for (name, value) in headers.iter() {
            h3_req_builder = h3_req_builder.header(name, value);
        }
        let h3_req = h3_req_builder
            .body(())
            .map_err(|e| reqwest_middleware::Error::Middleware(e.into()))?;

        // 复制键，供错误路径使用
        let pool_key = (normalize_host(&host), port, pin_config);

        let mut stream = send_request.send_request(h3_req).await.map_err(|e| {
            warn!(host = %host, port, error = %e, "[H3] send_request failed, evicting");
            if let Ok(mut pool) = self.inner.pool.lock() {
                pool.remove(&pool_key);
            }
            reqwest_middleware::Error::Middleware(anyhow::anyhow!("h3 send: {}", e))
        })?;

        // P1-4：完成或 recv_response 出错时也移除连接
        stream.finish().await.map_err(|e| {
            warn!(host = %host, port, error = %e, "[H3] finish failed, evicting");
            if let Ok(mut pool) = self.inner.pool.lock() {
                pool.remove(&pool_key);
            }
            reqwest_middleware::Error::Middleware(anyhow::anyhow!("h3 finish: {}", e))
        })?;

        let h3_resp = stream.recv_response().await.map_err(|e| {
            warn!(host = %host, port, error = %e, "[H3] recv_response failed, evicting");
            if let Ok(mut pool) = self.inner.pool.lock() {
                pool.remove(&pool_key);
            }
            reqwest_middleware::Error::Middleware(anyhow::anyhow!("h3 recv_response: {}", e))
        })?;

        debug!(status = %h3_resp.status(), "[H3] Got response");

        let (parts, _) = h3_resp.into_parts();
        let inner_arc = Arc::clone(&self.inner);

        let byte_stream = async_stream::try_stream! {
            let _keep_inner = inner_arc;
            let _keep_sr = send_request;
            let _stream_guard = stream_guard;

            loop {
                match stream.recv_data().await {
                    Ok(Some(mut data)) => {
                        yield data.copy_to_bytes(data.remaining());
                    }
                    Ok(None) => break,
                    Err(e) => {
                        Err(std::io::Error::other(
                            format!("h3 recv_data error: {e}"),
                        ))?;
                    }
                }
            }
        };

        let mut builder = http::Response::builder().status(parts.status);
        for (name, value) in &parts.headers {
            builder = builder.header(name, value);
        }
        let pinned: std::pin::Pin<
            Box<dyn futures::Stream<Item = Result<Bytes, std::io::Error>> + Send>,
        > = Box::pin(byte_stream);
        let body = reqwest::Body::wrap_stream(pinned);
        let http_response = builder
            .body(body)
            .map_err(|e| reqwest_middleware::Error::Middleware(e.into()))?;

        let response: reqwest::Response = http_response.into();
        Ok(response)
    }

    #[allow(dead_code)]
    pub async fn shutdown(&self) {
        debug!("[H3] Shutting down...");
        let driver_handles: Vec<JoinHandle<()>> = {
            if let Ok(mut pool) = self.inner.pool.lock() {
                let mut handles = Vec::new();
                for (_, mut entry) in pool.drain() {
                    entry.cancel_token.cancel();
                    handles.push(std::mem::replace(
                        &mut entry.driver_handle,
                        tokio::spawn(async {}),
                    ));
                }
                handles
            } else {
                Vec::new()
            }
        };
        for handle in driver_handles {
            handle.abort();
        }
        debug!("[H3] Shut down complete.");
    }

    /// 发现远程服务器证书的 SPKI 和完整证书 SHA-256 哈希。
    /// 建立一次性 QUIC 连接，通过 DiscoveryValidator 提取哈希后关闭连接。
    pub async fn discover(
        &self,
        host: &str,
        port: u16,
    ) -> Result<DiscoveredHashes, reqwest_middleware::Error> {
        let (validator, hashes) = DiscoveryValidator::new();

        let conn = msquic_async::Connection::new_with_cert_validator(
            &self.inner.registration,
            Some(Arc::new(validator) as Arc<dyn CertValidator>),
        )
        .map_err(|e| reqwest_middleware::Error::Middleware(e.into()))?;

        conn.start(&self.inner.configuration, host, port)
            .await
            .map_err(|e| reqwest_middleware::Error::Middleware(e.into()))?;

        // 执行最小 H3 握手，确保触发证书回调
        let h3_conn = h3_msquic_async::Connection::new(conn);
        let (_driver, _send_request) = h3::client::new(h3_conn).await.map_err(|e| {
            reqwest_middleware::Error::Middleware(anyhow::anyhow!("h3 discover: {}", e))
        })?;

        let result = hashes
            .lock()
            .map_err(|_| reqwest_middleware::Error::Middleware(anyhow::anyhow!("mutex poisoned")))?
            .clone();

        Ok(result)
    }
}

#[async_trait]
impl Middleware for H3Middleware {
    async fn handle(
        &self,
        req: reqwest::Request,
        extensions: &mut http::Extensions,
        next: Next<'_>,
    ) -> Result<reqwest::Response, reqwest_middleware::Error> {
        if req.url().scheme() == "http3" {
            self.h3_request(req).await
        } else {
            next.run(req, extensions).await
        }
    }
}
