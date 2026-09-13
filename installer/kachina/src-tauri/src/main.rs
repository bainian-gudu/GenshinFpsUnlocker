// 发布版 Windows 下禁止额外控制台窗口，禁止删除！！
#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

pub mod capabilities;
pub mod cli;
pub mod dfs;
pub mod fs;
pub mod installer;
pub mod ipc;
pub mod local;
pub mod module;
pub mod thirdparty;
pub mod utils;
use clap::Parser;
use cli::arg::{Command, InstallArgs};
use installer::uninstall::delete_self_on_exit;
use std::{sync::atomic::AtomicBool, time::Duration};
use tauri::{window::Color, WindowEvent};
use tauri_utils::{config::WindowEffectsConfig, WindowEffect};
use tracing_subscriber::prelude::*;

fn windows_text_scale_factor() -> f64 {
    use windows::UI::ViewManagement::UISettings;

    let scale = UISettings::new()
        .and_then(|settings| settings.TextScaleFactor())
        .unwrap_or(1.0);

    if scale.is_finite() && scale > 0.0 {
        scale
    } else {
        1.0
    }
}

lazy_static::lazy_static! {
    /// 不带中间件的原始 HTTP 客户端，供内部使用。
    pub(crate) static ref RAW_CLIENT: reqwest::Client = {
        reqwest::Client::builder()
            .user_agent(capabilities::ua_string()) // 由 DynamicUaMiddleware 按请求覆盖
            .gzip(true)
            .zstd(true)
            .read_timeout(Duration::from_secs(30))
            .connect_timeout(Duration::from_secs(5))
            .build()
            .unwrap()
    };

    /// 用于 API 调用的 HTTP 客户端，携带实时动态 UA。
    pub static ref API_CLIENT: reqwest_middleware::ClientWithMiddleware = {
        reqwest_middleware::ClientBuilder::new(RAW_CLIENT.clone())
            .with(capabilities::DynamicUaMiddleware::new())
            .build()
    };

    /// 用于下载的 HTTP 客户端，通过中间件支持 H3/QUIC。
    pub static ref DOWNLOAD_CLIENT: reqwest_middleware::ClientWithMiddleware = {
        let h3_ok = capabilities::is_h3_available();

        let mut builder = reqwest_middleware::ClientBuilder::new(RAW_CLIENT.clone())
            .with(capabilities::DynamicUaMiddleware::new());

        if h3_ok {
            match capabilities::H3FallbackMiddleware::new(Duration::from_secs(60)) {
                Ok(h3mw) => {
                    builder = builder.with(h3mw);
                    tracing::info!("[H3] H3FallbackMiddleware enabled");
                }
                Err(e) => {
                    tracing::warn!("[H3] Middleware init failed: {:#}, disabling", e);
                    capabilities::disable_h3();
                }
            }
        }

        // SSH 隧道和 SFTP 中间件共用同一个 SSH 连接池
        let ssh_pool = std::sync::Arc::new(
            capabilities::ssh::SshPoolInner::new(Duration::from_secs(300)),
        );

        // SSH 隧道中间件：通过 SSH direct-tcpip 通道处理 ssh+http:// URL
        builder = builder.with(capabilities::ssh::SshMiddleware::with_pool(ssh_pool.clone()));
        tracing::info!("[SSH] SshMiddleware enabled");

        // SFTP 下载中间件：通过 SSH SFTP 子系统处理 sftp:// URL
        builder = builder.with(capabilities::sftp::SftpMiddleware::new(ssh_pool));
        tracing::info!("[SFTP] SftpMiddleware enabled");

        builder.build()
    };

    /// 旧版别名，迁移完成后移除。
    pub static ref REQUEST_CLIENT: &'static reqwest_middleware::ClientWithMiddleware = &*API_CLIENT;
    pub static ref APP_BOOT_SIGNAL: AtomicBool = AtomicBool::new(false);
}

fn main() {
    use windows::Win32::System::Console::{AttachConsole, ATTACH_PARENT_PROCESS};
    let _ = unsafe { AttachConsole(ATTACH_PARENT_PROCESS) };

    let cli = cli::Cli::parse();
    let mut command = cli.command();
    let wv2ver = tauri::webview_version();
    if wv2ver.is_err() {
        command = Command::InstallWebview2;
    }
    // 本项目已移除上游的遥测（见 ../../LOCAL_PATCHES.md 第 7 节）：不初始化
    // 上报 客户端、不挂 sentry-tracing layer。日志只进本地控制台与
    // %TEMP%\KachinaInstaller.日志，一个字节都不外发。
    let info_filter = utils::InfoFilter {};

    // 在临时目录创建日志文件，失败时忽略
    let temp_dir = std::env::temp_dir();
    let log_file = temp_dir.join("KachinaInstaller.log");

    let console_layer = tracing_subscriber::fmt::layer().with_filter(utils::InfoFilter {});

    let registry = tracing_subscriber::registry().with(console_layer);

    // %TEMP% 对同会话的普通权限进程可写，而这个文件名是**固定**的：先在那儿放一个指向
    // `C:\Windows\...` 的符号链接，提权子进程（headless-uac 也走这段初始化）就会把日志
    // 追加进系统文件。路径本身或任一父级是重解析点时，只留控制台日志、不写文件。
    let log_target = if installer::uninstall::has_reparse_point(&log_file) {
        eprintln!(
            "日志路径上是符号链接/junction，跳过文件日志: {}",
            log_file.display()
        );
        None
    } else {
        std::fs::OpenOptions::new()
            .create(true)
            .append(true)
            .open(&log_file)
            .ok()
    };
    if let Some(file) = log_target {
        let file_layer = tracing_subscriber::fmt::layer()
            .with_writer(file)
            .with_ansi(false)
            .with_filter(info_filter);
        registry.with(file_layer).init();
    } else {
        registry.init();
    }

    // 在创建任何客户端前尽早初始化 H3/QUIC 探测
    capabilities::init();

    // 命令不是 Command::Install，可以是其他任意命令
    match command {
        Command::HeadlessUac(args) => {
            tracing::info!("KachinaInstaller started as UAC Thread");
            tokio::runtime::Builder::new_multi_thread()
                .enable_all()
                .build()
                .unwrap()
                .block_on(ipc::manager::uac_ipc_main(args));
        }
        Command::InstallWebview2 => {
            tracing::info!("KachinaInstaller started as Webview2 Installer");
            tokio::runtime::Builder::new_multi_thread()
                .enable_all()
                .build()
                .unwrap()
                .block_on(module::wv2::install_webview2());
        }
        Command::Install(install) => {
            tracing::info!("KachinaInstaller started");
            tokio::runtime::Builder::new_multi_thread()
                .enable_all()
                .build()
                .unwrap()
                .block_on(tauri_main(install));
        }
        Command::Other(_str) => {
            tracing::info!("KachinaInstaller started");
            tokio::runtime::Builder::new_multi_thread()
                .enable_all()
                .build()
                .unwrap()
                .block_on(tauri_main(InstallArgs {
                    target: None,
                    non_interactive: false,
                    silent: false,
                    online: false,
                    uninstall: false,
                    source: None,
                    dfs_extras: None,
                    mirrorc_cdk: None,
                }));
        }
    }
}

async fn tauri_main(args: InstallArgs) {
    tauri::async_runtime::set(tokio::runtime::Handle::current());
    let (major, minor, build) = nt_version::get();
    let build = (build & 0xffff) as u16;
    // 使用 22000 作为 Windows 11 的构建号
    let is_win11 = major == 10 && minor == 0 && build >= 22000;
    let is_win11_ = is_win11;

    // 将当前工作目录设置为临时目录
    let temp_dir = std::env::temp_dir();
    let res = std::env::set_current_dir(&temp_dir);
    if res.is_err() {
        rfd::MessageDialog::new()
            .set_title("错误")
            .set_description("无法访问临时文件夹")
            .show();
        return;
    }
    tauri::Builder::default()
        .invoke_handler(tauri::generate_handler![
            // 可直接运行的Command
            fs::is_dir_empty,
            dfs::get_dfs,
            dfs::get_http_with_range,
            dfs::http_get_request,
            // DFS2 命令
            dfs::get_dfs2_metadata,
            dfs::create_dfs2_session,
            dfs::get_dfs2_chunk_url,
            dfs::get_dfs2_batch_chunk_urls,
            dfs::end_dfs2_session,
            dfs::solve_dfs2_challenge,
            installer::log,
            installer::warn,
            installer::error,
            installer::launch,
            installer::launch_and_exit,
            installer::config::get_installer_config,
            installer::lnk::get_dirs,
            installer::registry::read_uninstall_metadata,
            installer::select_dir,
            installer::error_dialog,
            installer::confirm_dialog,
            installer::get_exe_version,
            // 相关实现：wincred
            utils::wincred::wincred_write,
            utils::wincred::wincred_read,
            utils::wincred::wincred_delete,
            // 相关实现：mirrorc
            thirdparty::mirrorc::get_mirrorc_status,
            // 新的托管操作
            ipc::manager::managed_operation,
        ])
        .manage(args)
        .manage(ipc::manager::ManagedElevate::new())
        .setup(move |app| {
            // 等待 5 秒检查窗口是否仍存活
            tokio::spawn({
                async move {
                    tokio::time::sleep(Duration::from_secs(5)).await;
                    if APP_BOOT_SIGNAL.load(std::sync::atomic::Ordering::SeqCst) {
                        tracing::info!("Webview2 is alive");
                        return;
                    }
                    rfd::MessageDialog::new()
                        .set_title("Kachina Installer")
                        .set_description("Initialization failed due to webview2 fault")
                        .set_level(rfd::MessageLevel::Error)
                        .show();
                    tracing::error!("Webview2 fault detected");
                    std::process::exit(1);
                }
            });
            let temp_dir_for_data = temp_dir.join("KachinaInstaller");

            let text_scale = windows_text_scale_factor();
            let base_width = 520.0;
            let base_height = 250.0;
            let scaled_width = base_width * text_scale;
            let scaled_height = base_height * text_scale;

            // 创建基础窗口构建器的辅助函数
            let create_window_builder = || {
                tauri::WebviewWindowBuilder::new(
                    app,
                    "main",
                    tauri::WebviewUrl::App("index.html".into()),
                )
                .title(" ")
                .resizable(false)
                .maximizable(false)
                .transparent(true)
                .inner_size(scaled_width, scaled_height)
                .center()
            };

            // 从当前 exe 提取图标
            let window_icon = utils::icon::get_exe_icon_for_tauri();

            // 创建构建器，并在提供图标时应用图标
            let mut main_window = create_window_builder();
            if let Some(icon) = window_icon {
                main_window = main_window.icon(icon).unwrap_or_else(|e| {
                    tracing::warn!("Failed to set window icon: {:?}", e);
                    create_window_builder()
                });
            }

            if !cfg!(debug_assertions) {
                main_window = main_window.data_directory(temp_dir_for_data).visible(false);
            }
            let main_window = main_window.build().unwrap();
            #[cfg(debug_assertions)]
            {
                let window = tauri::Manager::get_webview_window(app, "main");
                if let Some(window) = window {
                    window.open_devtools();
                }
            }
            if is_win11 {
                let _ = main_window.set_effects(Some(WindowEffectsConfig {
                    effects: vec![WindowEffect::Mica],
                    ..Default::default()
                }));
            } else {
                // mica 不可用时使用纯色背景。
                let _ = if utils::gui::is_dark_mode().unwrap_or(false) {
                    main_window.set_background_color(Some(Color(0, 0, 0, 255)))
                } else {
                    main_window.set_background_color(Some(Color(255, 255, 255, 255)))
                };
            }
            Ok(())
        })
        .on_window_event(move |window, event| {
            if let WindowEvent::ThemeChanged(theme) = event {
                if !is_win11_ {
                    match theme {
                        tauri::Theme::Dark => {
                            let _ = window.set_background_color(Some(Color(0, 0, 0, 255)));
                        }
                        tauri::Theme::Light => {
                            let _ = window.set_background_color(Some(Color(255, 255, 255, 255)));
                        }
                        _ => {}
                    }
                }
            }
            if let WindowEvent::CloseRequested { .. } = event {
                delete_self_on_exit();
            }
        })
        .run(tauri::generate_context!())
        .expect("error while running tauri application");
}
