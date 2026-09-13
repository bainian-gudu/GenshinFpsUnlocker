use anyhow::{Context, Result};
use windows::Win32::System::Threading::CREATE_NO_WINDOW;

use crate::fs::{create_http_stream, create_local_stream, progressed_copy};
use crate::utils::secure_temp;

pub async fn install_runtime(
    tag: String,
    offset: Option<usize>,
    size: Option<usize>,
    notify: impl Fn(serde_json::Value) + std::marker::Send + 'static,
) -> Result<String> {
    // 如果标签以 Microsoft.DotNet 开头，安装 .NET 运行时
    if tag.starts_with("Microsoft.DotNet") {
        return install_dotnet(tag, offset, size, notify).await;
    }
    if tag.starts_with("Microsoft.VCRedist") {
        return install_vcredist(tag, offset, size, notify).await;
    }
    // 否则不支持
    Err(anyhow::anyhow!("UNSUPPORTED_RUNTIME"))
}

/*
 * 安装 .NET 运行时包。
 * 支持的标签：
 * Microsoft.DotNet.DesktopRuntime.*
 * Microsoft.DotNet.Runtime.*
 * * 可以是数字“8”或“8.0.1”。
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
    // 通过运行 dotnet --list-runtimes 检查运行时是否已安装
    let cmd = tokio::process::Command::new("dotnet")
        .arg("--list-runtimes")
        .creation_flags(CREATE_NO_WINDOW.0)
        .output()
        .await;
    // 已安装则继续；检查失败则返回错误
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
    // 落地目录 / 随机文件名 / 独占创建 / 执行前验签：全部见 utils/secure_temp.rs
    let installer_path = secure_temp::package_path(&format!("Kachina.RuntimePackage.{tag}"));
    let (mut stream, len) = if offset.is_some() || size.is_some() {
        // 运行时已打包，只需解压并运行
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
        // 如果 vernum 是发布版本，获取实际版本
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
        // 获取实际下载 URL
        let url = runtime.1.replace("$", &vernum);
        let (stream, len, _insight) = create_http_stream(&url, 0, 0, true)
            .await
            .context("RUNTIME_DOWNLOAD_ERR")?;
        (stream, len.try_into().unwrap_or(0))
    };
    let mut target = secure_temp::create_exclusive_file(&installer_path).await?;
    let progress_noti = move |downloaded: usize| {
        notify(serde_json::json!((downloaded, len)));
    };
    let copied = progressed_copy(&mut stream, &mut target, progress_noti).await;
    // 关闭流
    drop(stream);
    drop(target);
    if let Err(e) = copied {
        // 半个安装包留在管理员目录里没意义，删掉
        let _ = tokio::fs::remove_file(&installer_path).await;
        return Err(e);
    }
    secure_temp::verify_microsoft_signed(&installer_path).await?;
    // 使用 /passive /norestart 运行安装器
    let mut cmd = tokio::process::Command::new(&installer_path)
        .arg("/passive")
        .arg("/norestart")
        .spawn()
        .context("RUNTIME_INSTALL_START_ERR")?;
    let status = cmd.wait().await.context("RUNTIME_INSTALL_WAIT_ERR")?;
    if !status.success() {
        return Err(anyhow::anyhow!("RUNTIME_INSTALL_FAILED"));
    }
    // 删除安装器
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
    // 检查注册表中是否已安装
    if check_vcredist(&reg) {
        return Ok("ALREADY_INSTALLED".to_string());
    }
    // 落地目录 / 随机文件名 / 独占创建 / 执行前验签：全部见 utils/secure_temp.rs
    let installer_path = secure_temp::package_path(&format!("Kachina.RuntimePackage.{tag}"));
    let (mut stream, len) = if offset.is_some() || size.is_some() {
        // 运行时已打包，只需解压并运行
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
    let mut target = secure_temp::create_exclusive_file(&installer_path).await?;
    let progress_noti = move |downloaded: usize| {
        notify(serde_json::json!((downloaded, len)));
    };
    let copied = progressed_copy(&mut stream, &mut target, progress_noti).await;
    // 关闭流
    drop(stream);
    drop(target);
    if let Err(e) = copied {
        let _ = tokio::fs::remove_file(&installer_path).await;
        return Err(e);
    }
    secure_temp::verify_microsoft_signed(&installer_path).await?;
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
