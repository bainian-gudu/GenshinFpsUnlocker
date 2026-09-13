pub mod acl;
pub mod dir;
pub mod error;
pub mod gui;
pub mod hash;
pub mod icon;
pub mod metadata;
pub mod progressed_read;
pub mod secure_temp;
pub mod uac;
pub mod url;
pub mod wincred;

/// 日志过滤：只放行 INFO 及以上（ERROR / WARN / INFO）。
///
/// 上游把它放在 `utils/sentry.rs` 里（和 Sentry 的 breadcrumb 过滤配套）；本项目
/// 移除了遥测，但这个过滤器是**本地**控制台与 `%TEMP%\KachinaInstaller.log`
/// 两个 layer 在用的，所以搬到这儿保留。
pub struct InfoFilter {}

impl InfoFilter {
    fn is_enabled(&self, metadata: &tracing::Metadata<'_>) -> bool {
        metadata.level() <= &tracing::Level::INFO
    }
}

impl<S> tracing_subscriber::layer::Filter<S> for InfoFilter {
    fn enabled(
        &self,
        metadata: &tracing::Metadata<'_>,
        _: &tracing_subscriber::layer::Context<'_, S>,
    ) -> bool {
        self.is_enabled(metadata)
    }
}
