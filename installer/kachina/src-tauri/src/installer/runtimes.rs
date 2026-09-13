use std::path::{Path, PathBuf};

use anyhow::{Context, Result};
use tokio::io::AsyncWrite;
use windows::Win32::System::Threading::CREATE_NO_WINDOW;

use crate::fs::{create_http_stream, create_local_stream, progressed_copy};

/// 运行时安装包的落地目录：优先 `%SystemRoot%\Temp`（只有管理员 / SYSTEM 可写），
/// 取不到才退回用户自己的 `%TEMP%`（非提权安装时）。
///
/// 为什么不用 `%TEMP%`：装运行时的这一步通常是**提权**跑的，而 `%TEMP%` 对同会话的
/// 普通权限进程可写。上游把安装包写成 `%TEMP%\Kachina.RuntimePackage.{tag}.exe`
/// 这种**固定名字**，普通权限的攻击者就能：① 先在该路径放一个指向
/// `C:\Windows\System32\xxx` 的符号链接，让提权进程把下载内容写进系统文件；
/// ② 或在「下载完成 → 启动安装」之间把文件换成自己的 exe。两者都等于管理员权限的
/// 任意写 / 代码执行。换成管理员专属目录 + 随机文件名 + 独占创建后，这两条都断了。
fn runtime_package_dir() -> PathBuf {
    if let Ok(root) = std::env::var("SystemRoot") {
        let dir = Path::new(&root).join("Temp");
        if dir.is_dir() {
            return dir;
        }
    }
    std::env::temp_dir()
}

/// 落地路径：目录见 `runtime_package_dir`，文件名带 UUID，不可预测。
fn runtime_package_path(tag: &str) -> PathBuf {
    runtime_package_dir().join(format!(
        "Kachina.RuntimePackage.{tag}.{}.exe",
        uuid::Uuid::new_v4()
    ))
}

/// 独占创建目标文件：`create_new` 而不是 `fs.rs` 里的 `File::create`
/// （后者是 CREATE_ALWAYS，路径已存在就**跟着符号链接覆盖**）。
/// 已存在＝有人在抢这个路径，直接失败。
async fn create_exclusive_target_file(path: &Path) -> Result<impl AsyncWrite> {
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
/// 所以还要求签名者确实是微软（.NET 与 VC++ 运行库的安装包都是
/// `CN=Microsoft Corporation, O=Microsoft Corporation, ...`）。
fn is_trusted_runtime_signature(status: &str, subject: &str) -> bool {
    if !status.eq_ignore_ascii_case("Valid") {
        return false;
    }
    // Subject 形如：
    // `CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US`
    // 逐段**精确**比对（大小写不敏感、与顺序无关），不用子串匹配 —— 子串会把
    // `O=Not Microsoft Corporation Ltd` 这种冒名写法也算成微软。
    // CA/B 规则下 `CN` / `O` 都必须与通过验证的组织名一致，所以命中任意一个即可。
    let mut matched = false;
    for part in subject.split(',') {
        let seg = part.trim().to_ascii_lowercase();
        if seg == "cn=microsoft corporation" || seg == "o=microsoft corporation" {
            matched = true;
        }
    }
    matched
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

/// 下载完、执行前的最后一道关：验微软签名。
///
/// 不通过就把文件删掉并报错 —— 宁可不装运行时（宿主的 `RuntimePrerequisite` 会引导
/// 用户自己去官网下），也不能让提权进程去跑一个来路不明的 exe。
async fn verify_runtime_package(path: &Path) -> Result<()> {
    let (status, subject) = query_authenticode(path).await?;
    if !is_trusted_runtime_signature(&status, &subject) {
        let _ = tokio::fs::remove_file(path).await;
        return Err(anyhow::anyhow!(
            "RUNTIME_SIGNATURE_UNTRUSTED: status={status} subject={subject}"
        ));
    }
    tracing::info!("运行时安装包验签通过: {subject}");
    Ok(())
}

pub async fn install_runtime(
    tag: String,
    offset: Option<usize>,
    size: Option<usize>,
    notify: impl Fn(serde_json::Value) + std::marker::Send + 'static,
) -> Result<String> {
    // if tag startswith Microsoft.DotNet, install .NET runtime
    if tag.starts_with("Microsoft.DotNet") {
        return install_dotnet(tag, offset, size, notify).await;
    }
    if tag.starts_with("Microsoft.VCRedist") {
        return install_vcredist(tag, offset, size, notify).await;
    }
    // else not supported
    Err(anyhow::anyhow!("UNSUPPORTED_RUNTIME"))
}

/*
 * Install .NET runtime package
 * Supported tags:
 * Microsoft.DotNet.DesktopRuntime.*
 * Microsoft.DotNet.Runtime.*
 * * may be number '8' or '8.0.1'
 */
pub async fn install_dotnet(
    tag: String,
    offset: Option<usize>,
    size: Option<usize>,
    notify: impl Fn(serde_json::Value) + std::marker::Send + 'static,
) -> Result<String> {
    let tag_without_version = tag.split('.').take(3).collect::<Vec<&str>>().join(".");
    let runtime = match tag_without_version.as_str() {
        "Microsoft.DotNet.DesktopRuntime" => (
            "https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/$/latest.version",
            "https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/$/windowsdesktop-runtime-$-win-x64.exe",
            "Microsoft.WindowsDesktop.App",
        ),
        "Microsoft.DotNet.Runtime" => (
            "https://builds.dotnet.microsoft.com/dotnet/Runtime/$/latest.version",
            "https://builds.dotnet.microsoft.com/dotnet/Runtime/$/dotnet-runtime-$-win-x64.exe",
            "Microsoft.NETCore.App",
        ),
        _ => {
            return Err(anyhow::anyhow!("UNSUPPORTED_DOTNET_RUNTIME"));
        }
    };
    // check if runtime is installed by running dotnet --list-runtimes
    let cmd = tokio::process::Command::new("dotnet")
        .arg("--list-runtimes")
        .creation_flags(CREATE_NO_WINDOW.0)
        .output()
        .await;
    // if installed, continue; if check failed, return error
    if let Ok(output) = cmd {
        if output.status.success() {
            let stdout = String::from_utf8_lossy(&output.stdout);
            let version_primary = tag
                .split('.')
                .nth(3)
                .ok_or_else(|| anyhow::anyhow!("INVALID_DOTNET_VERSION"))?;
            let query_name = format!("{} {}", runtime.2, version_primary);
            if stdout.contains(&query_name) {
                return Ok("ALREADY_INSTALLED".to_string());
            }
        }
    }
    // download to tmp folder（目录与文件名见 runtime_package_dir / runtime_package_path）
    let installer_path = runtime_package_path(&tag);
    let (mut stream, len) = if offset.is_some() || size.is_some() {
        // runtime packed, just extract and run
        let stream = create_local_stream(offset.unwrap(), size.unwrap(), true)
            .await
            .context("RUNTIME_EXTRACT_ERR")?;
        tracing::info!(
            "Extracted {} installer from local stream, offset: {}, size: {}",
            tag,
            offset.unwrap(),
            size.unwrap()
        );
        (stream, size.unwrap())
    } else {
        let mut vernum = tag.split('.').skip(3).collect::<Vec<&str>>().join(".");
        // if vernum is release version, get real version
        if vernum.len() == 1 || vernum.len() == 2 {
            let relver = if vernum.len() == 1 {
                format!("{vernum}.0")
            } else {
                vernum.clone()
            };
            let url = runtime.0.replace("$", &relver);
            let resp = reqwest::get(&url)
                .await
                .context("RUNTIME_VERSION_FETCH_ERR")?;
            if !resp.status().is_success() {
                return Err(anyhow::anyhow!("RUNTIME_VERSION_API_ERR"));
            }
            let text = resp.text().await.context("RUNTIME_VERSION_READ_ERR")?;
            vernum = text.trim().to_string();
        }
        // get real download url
        let url = runtime.1.replace("$", &vernum);
        let (stream, len, _insight) = create_http_stream(&url, 0, 0, true)
            .await
            .context("RUNTIME_DOWNLOAD_ERR")?;
        (stream, len.try_into().unwrap_or(0))
    };
    let mut target = create_exclusive_target_file(&installer_path).await?;
    let progress_noti = move |downloaded: usize| {
        notify(serde_json::json!((downloaded, len)));
    };
    let copied = progressed_copy(&mut stream, &mut target, progress_noti).await;
    // close streams
    drop(stream);
    drop(target);
    if let Err(e) = copied {
        // 半个安装包留在管理员目录里没意义，删掉
        let _ = tokio::fs::remove_file(&installer_path).await;
        return Err(e);
    }
    verify_runtime_package(&installer_path).await?;
    // run installer with /passive /norestart
    let mut cmd = tokio::process::Command::new(&installer_path)
        .arg("/passive")
        .arg("/norestart")
        .spawn()
        .context("RUNTIME_INSTALL_START_ERR")?;
    let status = cmd.wait().await.context("RUNTIME_INSTALL_WAIT_ERR")?;
    if !status.success() {
        return Err(anyhow::anyhow!("RUNTIME_INSTALL_FAILED"));
    }
    // remove installer
    let _ = tokio::fs::remove_file(&installer_path).await;
    Ok("NEWLY_INSTALLED".to_string())
}

pub fn check_vcredist(reg: &str) -> bool {
    let key = windows_registry::LOCAL_MACHINE.options().read().open(reg);
    if let Ok(key) = key {
        let installed = key.get_u32("Installed");
        if let Ok(installed) = installed {
            if installed == 1 {
                return true;
            }
        }
    }
    false
}

pub async fn install_vcredist(
    tag: String,
    offset: Option<usize>,
    size: Option<usize>,
    notify: impl Fn(serde_json::Value) + std::marker::Send + 'static,
) -> Result<String> {
    let x64_prefix = "SOFTWARE\\Microsoft\\VisualStudio\\14.0\\VC\\Runtimes\\";
    let x86_prefix = "SOFTWARE\\Wow6432Node\\Microsoft\\VisualStudio\\14.0\\VC\\Runtimes\\";
    let (url, reg) = match tag.as_str() {
        "Microsoft.VCRedist.2015+.x64" => (
            "https://aka.ms/vs/17/release/vc_redist.x64.exe",
            format!("{}{}", x64_prefix, "x64"),
        ),
        "Microsoft.VCRedist.2015+.x86" => (
            "https://aka.ms/vs/17/release/vc_redist.x86.exe",
            format!("{}{}", x86_prefix, "x86"),
        ),
        _ => {
            return Err(anyhow::anyhow!("UNSUPPORTED_TAG"));
        }
    };
    // check registry for already installed
    if check_vcredist(&reg) {
        return Ok("ALREADY_INSTALLED".to_string());
    }
    // download to tmp folder（目录与文件名见 runtime_package_dir / runtime_package_path）
    let installer_path = runtime_package_path(&tag);
    let (mut stream, len) = if offset.is_some() || size.is_some() {
        // runtime packed, just extract and run
        let stream = create_local_stream(offset.unwrap(), size.unwrap(), true)
            .await
            .context("RUNTIME_EXTRACT_ERR")?;
        tracing::info!(
            "Extracted {} installer from local stream, offset: {}, size: {}",
            tag,
            offset.unwrap(),
            size.unwrap()
        );
        (stream, size.unwrap())
    } else {
        let (stream, len, _insight) = create_http_stream(url, 0, 0, true)
            .await
            .context("RUNTIME_DOWNLOAD_ERR")?;
        (stream, len.try_into().unwrap_or(0))
    };
    let mut target = create_exclusive_target_file(&installer_path).await?;
    let progress_noti = move |downloaded: usize| {
        notify(serde_json::json!((downloaded, len)));
    };
    let copied = progressed_copy(&mut stream, &mut target, progress_noti).await;
    // close streams
    drop(stream);
    drop(target);
    if let Err(e) = copied {
        let _ = tokio::fs::remove_file(&installer_path).await;
        return Err(e);
    }
    verify_runtime_package(&installer_path).await?;
    let mut cmd = tokio::process::Command::new(&installer_path)
        .arg("/install")
        .arg("/quiet")
        .arg("/norestart")
        .spawn()
        .context("RUNTIME_INSTALL_START_ERR")?;
    let status = cmd.wait().await.context("RUNTIME_INSTALL_WAIT_ERR")?;
    if !status.success() {
        return Err(anyhow::anyhow!("RUNTIME_INSTALL_FAILED"));
    }
    let _ = tokio::fs::remove_file(installer_path).await;
    Ok("NEWLY_INSTALLED".to_string())
}
