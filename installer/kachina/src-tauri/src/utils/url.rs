use anyhow::Context;
use url::Url;

/// 移除 URL 的查询参数和片段，生成可用于日志的 URL，
/// 避免令牌、会话 ID 等敏感信息出现在日志中。
///
/// # 参数
/// * `url` - 需要净化的 URL
///
/// # 返回值
/// 仅包含协议、主机和路径的净化后 URL。
///
/// # 示例
/// ```
/// let sanitized = sanitize_url_for_logging("https://api.example.com/data?token=secret&id=123#section");
/// assert_eq!(sanitized, "https://api.example.com/data");
/// ```
pub fn sanitize_url_for_logging(url: &str) -> String {
    match Url::parse(url) {
        Ok(parsed) => {
            let mut sanitized = String::new();

            // 添加 scheme
            sanitized.push_str(parsed.scheme());
            sanitized.push_str("://");

            // 添加主机
            if let Some(host) = parsed.host_str() {
                sanitized.push_str(host);

                // 存在端口且不是默认端口时添加端口
                if let Some(port) = parsed.port() {
                    sanitized.push(':');
                    sanitized.push_str(&port.to_string());
                }
            }

            // 添加路径
            sanitized.push_str(parsed.path());

            sanitized
        }
        Err(_) => {
            // URL 解析失败时尝试手动提取基本组件
            if let Some(query_start) = url.find('?') {
                url[..query_start].to_string()
            } else if let Some(fragment_start) = url.find('#') {
                url[..fragment_start].to_string()
            } else {
                url.to_string()
            }
        }
    }
}

/// 为 reqwest HTTP 请求创建统一的错误上下文。
///
/// # 参数
/// * `function_name` - 发生错误的函数名
/// * `url` - 请求的 URL（会先净化）
/// * `error_type` - 错误类型（例如 "HTTP_REQUEST_ERR"、"HTTP_STATUS_ERR"）
///
/// # 返回值
/// 格式化后的错误上下文字符串。
pub fn create_reqwest_context(function_name: &str, url: &str, error_type: &str) -> String {
    let sanitized_url = sanitize_url_for_logging(url);
    format!("{} in {}: {}", error_type, function_name, sanitized_url)
}

/// 为 anyhow 错误添加 HTTP 上下文的扩展特征。
pub trait HttpContextExt<T> {
    /// 为 anyhow Result 添加 HTTP 请求上下文。
    fn with_http_context(self, function_name: &str, url: &str) -> anyhow::Result<T>;

    /// 为 anyhow Result 添加 HTTP 状态上下文。
    fn with_http_status_context(self, function_name: &str, url: &str) -> anyhow::Result<T>;

    /// 为 anyhow Result 添加通用 HTTP 错误上下文。
    fn with_http_error_context(
        self,
        function_name: &str,
        url: &str,
        error_type: &str,
    ) -> anyhow::Result<T>;
}

impl<T> HttpContextExt<T> for Result<T, reqwest::Error> {
    fn with_http_context(self, function_name: &str, url: &str) -> anyhow::Result<T> {
        self.context(create_reqwest_context(
            function_name,
            url,
            "HTTP_REQUEST_ERR",
        ))
    }

    fn with_http_status_context(self, function_name: &str, url: &str) -> anyhow::Result<T> {
        self.context(create_reqwest_context(
            function_name,
            url,
            "HTTP_STATUS_ERR",
        ))
    }

    fn with_http_error_context(
        self,
        function_name: &str,
        url: &str,
        error_type: &str,
    ) -> anyhow::Result<T> {
        self.context(create_reqwest_context(function_name, url, error_type))
    }
}

impl<T> HttpContextExt<T> for Result<T, reqwest_middleware::Error> {
    fn with_http_context(self, function_name: &str, url: &str) -> anyhow::Result<T> {
        self.map_err(|e| anyhow::anyhow!(e))
            .context(create_reqwest_context(
                function_name,
                url,
                "HTTP_REQUEST_ERR",
            ))
    }

    fn with_http_status_context(self, function_name: &str, url: &str) -> anyhow::Result<T> {
        self.map_err(|e| anyhow::anyhow!(e))
            .context(create_reqwest_context(
                function_name,
                url,
                "HTTP_STATUS_ERR",
            ))
    }

    fn with_http_error_context(
        self,
        function_name: &str,
        url: &str,
        error_type: &str,
    ) -> anyhow::Result<T> {
        self.map_err(|e| anyhow::anyhow!(e))
            .context(create_reqwest_context(function_name, url, error_type))
    }
}

impl<T> HttpContextExt<T> for anyhow::Result<T> {
    fn with_http_context(self, function_name: &str, url: &str) -> anyhow::Result<T> {
        self.context(create_reqwest_context(
            function_name,
            url,
            "HTTP_REQUEST_ERR",
        ))
    }

    fn with_http_status_context(self, function_name: &str, url: &str) -> anyhow::Result<T> {
        self.context(create_reqwest_context(
            function_name,
            url,
            "HTTP_STATUS_ERR",
        ))
    }

    fn with_http_error_context(
        self,
        function_name: &str,
        url: &str,
        error_type: &str,
    ) -> anyhow::Result<T> {
        self.context(create_reqwest_context(function_name, url, error_type))
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_sanitize_url_for_logging() {
        // 测试查询参数
        assert_eq!(
            sanitize_url_for_logging("https://api.example.com/data?token=secret&id=123"),
            "https://api.example.com/data"
        );

        // 测试片段
        assert_eq!(
            sanitize_url_for_logging("https://example.com/page#section"),
            "https://example.com/page"
        );

        // 同时测试查询参数和片段
        assert_eq!(
            sanitize_url_for_logging("https://api.example.com/data?key=value#top"),
            "https://api.example.com/data"
        );

        // 测试端口
        assert_eq!(
            sanitize_url_for_logging("https://api.example.com:8080/data?token=secret"),
            "https://api.example.com:8080/data"
        );

        // 测试干净 URL（无需修改）
        assert_eq!(
            sanitize_url_for_logging("https://api.example.com/data"),
            "https://api.example.com/data"
        );

        // 测试包含敏感信息的路径（路径的一部分，应保留）
        assert_eq!(
            sanitize_url_for_logging("https://api.example.com/users/123/profile?token=secret"),
            "https://api.example.com/users/123/profile"
        );
    }

    #[test]
    fn test_create_reqwest_context() {
        let context = create_reqwest_context(
            "get_user_data",
            "https://api.example.com/users?token=secret",
            "HTTP_REQUEST_ERR",
        );
        assert_eq!(
            context,
            "HTTP_REQUEST_ERR in get_user_data: https://api.example.com/users"
        );
    }
}
