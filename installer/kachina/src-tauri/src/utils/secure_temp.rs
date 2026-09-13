//! 「下载一个可执行文件、然后运行它」这条链路的落地与验签。
//!
//! 用到它的三处（.NET / VC++ 运行时、WebView2 引导器 ×2）都跑在**可能已提权**的进程里，
//! 而上游的写法是同一个反模式：`%TEMP%` 里的**固定文件名** + `File::create` /
//! `tokio::fs::write`（都是 CREATE_ALWAYS，会**跟随符号链接**）+ 下完不验签直接 `spawn`。
//! 于是同会话的普通权限进程可以：
//!
//! 1. 先在该路径放一个指向 `C:\Windows\System32\*` 的符号链接，让提权进程把下载的
//!    内容写进系统文件（任意写）；
//! 2. 或在「下载完成 → 启动安装」之间把文件换成自己的 exe（提权代码执行）；
//! 3. 或让下载源被劫持（镜像 / DNS / 中间人），拿到的就不是微软的东西。
//!
//! 这里三条一起堵：管理员专属目录 + 随机文件名 + 独占创建 + 执行前验微软签名。
//!
//! 详见 `installer/kachina/LOCAL_PATCHES.md` 第 8、9 节。

use std::path::{Path, PathBuf};

use anyhow::{Context, Result};
use tokio::io::AsyncWrite;
use windows::Win32::System::Threading::CREATE_NO_WINDOW;

/// 下载文件的落地目录：优先 `%SystemRoot%\Temp`（只有管理员 / SYSTEM 可写），
/// 取不到才退回用户自己的 `%TEMP%`（非提权场景）。
fn package_dir() -> PathBuf {
    if let Ok(root) = std::env::var("SystemRoot") {
        let dir = Path::new(&root).join("Temp");
        if dir.is_dir() {
            return dir;
        }
    }
    std::env::temp_dir()
}

/// 落地路径：目录见 `package_dir`，文件名带 UUID，不可预测。
pub fn package_path(prefix: &str) -> PathBuf {
    package_dir().join(format!("{prefix}.{}.exe", uuid::Uuid::new_v4()))
}

/// 独占创建目标文件：`create_new` 而不是 `File::create` / `tokio::fs::write`
/// （后两者是 CREATE_ALWAYS，路径已存在就**跟着符号链接覆盖**）。
/// 已存在＝有人在抢这个路径，直接失败。
pub async fn create_exclusive_file(path: &Path) -> Result<impl AsyncWrite> {
    let file = tokio::fs::OpenOptions::new()
        .write(true)
        .create_new(true)
        .open(path)
        .await
        .with_context(|| format!("CREATE_TARGET_FILE_ERR: {}", path.display()))?;
    Ok(tokio::io::BufWriter::new(file))
}

/// 验签结论的判定（纯函数，devcheck 的 logic 层对它跑断言）。
///
/// 只看 `Status` 不够：攻击者用自己申请的代码签名证书也能签出 `Valid`，
/// 所以还要求签名者确实是微软（.NET、VC++ 运行库与 WebView2 引导器都是
/// `CN=Microsoft Corporation, O=Microsoft Corporation, ...`）。
pub fn is_trusted_microsoft_signature(status: &str, subject: &str) -> bool {
    if !status.eq_ignore_ascii_case("Valid") {
        return false;
    }
    // Subject 形如：
    // `CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US`
    // 逐段**精确**比对（大小写不敏感、与顺序无关），不用子串匹配 —— 子串会把
    // `O=Not Microsoft Corporation Ltd` 这种冒名写法也算成微软。
    // CA/B 规则下 `CN` / `O` 都必须与通过验证的组织名一致，所以命中任意一个即可。
    subject.split(',').any(|part| {
        let seg = part.trim().to_ascii_lowercase();
        seg == "cn=microsoft corporation" || seg == "o=microsoft corporation"
    })
}

/// 读一个 exe 的 Authenticode 签名状态与签名者，返回 `(Status, Subject)`。
///
/// 走 PowerShell 的 `Get-AuthenticodeSignature` 而不是 `WinVerifyTrust` 的 FFI：
/// 那套 `WINTRUST_DATA` / `WTHelperGetProvSignerFromChain` 的 unsafe 代码在本仓库
/// 没法实机验证，写错一个字段就是运行时 UB；PowerShell 在 Win10+ 一定存在。
async fn query_authenticode(path: &Path) -> Result<(String, String)> {
    // 单引号里的 ' 要写成 ''（Windows 文件名允许单引号）
    let literal = path.display().to_string().replace('\'', "''");
    let script = format!(
        "$ErrorActionPreference='Stop';$s=Get-AuthenticodeSignature -LiteralPath '{literal}';\"$($s.Status)`t$($s.SignerCertificate.Subject)\""
    );
    let out = tokio::process::Command::new("powershell.exe")
        .args([
            "-NoProfile",
            "-NonInteractive",
            "-ExecutionPolicy",
            "Bypass",
            "-Command",
            &script,
        ])
        .creation_flags(CREATE_NO_WINDOW.0)
        .output()
        .await
        .context("RUNTIME_VERIFY_SPAWN_ERR")?;
    if !out.status.success() {
        return Err(anyhow::anyhow!(
            "RUNTIME_VERIFY_ERR: powershell exit={:?} stderr={}",
            out.status.code(),
            String::from_utf8_lossy(&out.stderr).trim()
        ));
    }
    let line = String::from_utf8_lossy(&out.stdout).trim().to_string();
    let mut parts = line.splitn(2, '\t');
    let status = parts.next().unwrap_or("").trim().to_string();
    let subject = parts.next().unwrap_or("").trim().to_string();
    if status.is_empty() {
        return Err(anyhow::anyhow!("RUNTIME_VERIFY_EMPTY"));
    }
    Ok((status, subject))
}

/// 执行前的最后一道关：验微软签名。
///
/// 不通过就把文件删掉并报错 —— 宁可不装这个运行时（宿主的 `RuntimePrerequisite`
/// 会引导用户自己去官网下），也不能让提权进程去跑一个来路不明的 exe。
pub async fn verify_microsoft_signed(path: &Path) -> Result<()> {
    let (status, subject) = query_authenticode(path).await?;
    if !is_trusted_microsoft_signature(&status, &subject) {
        let _ = tokio::fs::remove_file(path).await;
        return Err(anyhow::anyhow!(
            "RUNTIME_SIGNATURE_UNTRUSTED: status={status} subject={subject}"
        ));
    }
    tracing::info!("下载的安装包验签通过: {subject}");
    Ok(())
}
