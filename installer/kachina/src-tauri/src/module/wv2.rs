use std::ptr::null_mut;

use windows::{
    core::{HRESULT, PCWSTR},
    Win32::{
        Foundation::{HWND, LPARAM, S_OK, WPARAM},
        System::LibraryLoader::GetModuleHandleW,
        UI::{
            Controls::{
                TASKDIALOGCONFIG, TASKDIALOG_NOTIFICATIONS, TDE_CONTENT,
                TDF_SHOW_MARQUEE_PROGRESS_BAR, TDF_USE_HICON_MAIN, TDM_SET_PROGRESS_BAR_MARQUEE,
                TDM_UPDATE_ELEMENT_TEXT, TDN_CREATED, TDN_DESTROYED,
            },
            WindowsAndMessaging::{LoadIconW, SendMessageW, WM_CLOSE},
        },
    },
};

use crate::{
    utils::{secure_temp, url::HttpContextExt},
    REQUEST_CLIENT,
};

pub struct SendableHwnd(pub *mut Option<HWND>);
unsafe impl Send for SendableHwnd {}
unsafe impl Sync for SendableHwnd {}
impl SendableHwnd {
    pub fn as_isize(&self) -> isize {
        self.0 as isize
    }
}

pub async fn install_webview2() {
    unsafe {
        let _ = windows::Win32::UI::WindowsAndMessaging::SetProcessDPIAware();
    }
    let title = "安装 WebView2 运行时";
    let heading = "当前系统缺少 WebView2 运行时，正在安装...";
    let content = "正在下载安装程序...";
    let title_utf16_nul = title
        .encode_utf16()
        .chain(std::iter::once(0))
        .collect::<Vec<u16>>();
    let heading_utf16_nul = heading
        .encode_utf16()
        .chain(std::iter::once(0))
        .collect::<Vec<u16>>();
    let content_utf16_nul = content
        .encode_utf16()
        .chain(std::iter::once(0))
        .collect::<Vec<u16>>();
    let mut dialog_hwnd: Option<HWND> = None;
    let ptr_dialog_hwnd = SendableHwnd(&mut dialog_hwnd as *mut Option<HWND>);
    unsafe extern "system" fn callback(
        hwnd: HWND,
        msg: TASKDIALOG_NOTIFICATIONS,
        _w_param: WPARAM,
        _l_param: LPARAM,
        lp_ref_data: isize,
    ) -> HRESULT {
        let conf = lp_ref_data as *mut std::option::Option<windows::Win32::Foundation::HWND>;
        match msg {
            TDN_CREATED => {
                (*conf).replace(hwnd);
                SendMessageW(
                    hwnd,
                    TDM_SET_PROGRESS_BAR_MARQUEE.0 as u32,
                    Some(WPARAM(1)),
                    Some(LPARAM(1)),
                );
            }
            TDN_DESTROYED => {
                if (*conf).is_some() {
                    (*conf).take();
                    std::process::exit(1);
                }
            }
            _ => {}
        };
        S_OK
    }
    tokio::task::spawn_blocking(move || {
        // 获取当前进程的 HICON
        let hmodule = unsafe { GetModuleHandleW(PCWSTR(null_mut())).unwrap() };
        let hicon = unsafe {
            LoadIconW(
                Some(hmodule.into()),
                windows::Win32::UI::WindowsAndMessaging::IDI_APPLICATION,
            )
        };

        let config: TASKDIALOGCONFIG = TASKDIALOGCONFIG {
            cbSize: u32::try_from(std::mem::size_of::<TASKDIALOGCONFIG>()).unwrap(),
            hInstance: unsafe { GetModuleHandleW(PCWSTR(std::ptr::null())).unwrap().into() },
            pszWindowTitle: PCWSTR(title_utf16_nul.as_ptr()),
            pszMainInstruction: PCWSTR(heading_utf16_nul.as_ptr()),
            pszContent: PCWSTR(content_utf16_nul.as_ptr()),
            dwFlags: TDF_SHOW_MARQUEE_PROGRESS_BAR | TDF_USE_HICON_MAIN,
            pfCallback: Some(callback),
            lpCallbackData: ptr_dialog_hwnd.as_isize(),
            dwCommonButtons: windows::Win32::UI::Controls::TDCBF_CANCEL_BUTTON,
            Anonymous1: windows::Win32::UI::Controls::TASKDIALOGCONFIG_0 {
                hMainIcon: if let Ok(hicon) = hicon {
                    hicon
                } else {
                    windows::Win32::UI::WindowsAndMessaging::HICON(null_mut())
                },
            },
            ..TASKDIALOGCONFIG::default()
        };
        let _ =
            unsafe { windows::Win32::UI::Controls::TaskDialogIndirect(&config, None, None, None) };
    });
    // 使用 reqwest 下载安装器
    let wv2_url = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";
    let res = REQUEST_CLIENT
        .get(wv2_url)
        .send()
        .await
        .with_http_context("install_webview2", wv2_url);
    if let Err(e) = res {
        let hwnd = dialog_hwnd.take();
        if let Some(hwnd) = hwnd {
            unsafe {
                SendMessageW(hwnd, WM_CLOSE, Some(WPARAM(0)), Some(LPARAM(0)));
            }
        }
        rfd::MessageDialog::new()
            .set_title("出错了")
            .set_description(format!("WebView2 运行时下载失败: {e}"))
            .set_level(rfd::MessageLevel::Error)
            .show();
        std::process::exit(0);
    }
    let res = res.unwrap();
    let wv2_installer_blob = res
        .bytes()
        .await
        .with_http_context("install_webview2", wv2_url);
    if let Err(e) = wv2_installer_blob {
        let hwnd = dialog_hwnd.take();
        if let Some(hwnd) = hwnd {
            unsafe {
                SendMessageW(hwnd, WM_CLOSE, Some(WPARAM(0)), Some(LPARAM(0)));
            }
        }
        rfd::MessageDialog::new()
            .set_title("出错了")
            .set_description(format!("WebView2 运行时下载失败: {e}"))
            .set_level(rfd::MessageLevel::Error)
            .show();
        std::process::exit(0);
    }
    let wv2_installer_blob = wv2_installer_blob.unwrap();
    // 落地目录 / 随机文件名 / 独占创建 / 执行前验签：全部见 utils/secure_temp.rs。
    // 上游这里是 %TEMP% 里的固定文件名 + tokio::fs::write（CREATE_ALWAYS，跟随符号
    // 链接），而且下完不验签就 创建 —— 同一个会话的普通权限进程既能把下载内容引进
    // 系统文件，也能在安装启动前把文件换掉。
    let installer_path = secure_temp::package_path("kachina.MicrosoftEdgeWebview2Setup");
    let res = async {
        use tokio::io::AsyncWriteExt;
        let mut file = secure_temp::create_exclusive_file(&installer_path).await?;
        file.write_all(&wv2_installer_blob).await?;
        file.flush().await?;
        drop(file);
        secure_temp::verify_microsoft_signed(&installer_path).await
    }
    .await;
    if let Err(e) = res {
        let _ = tokio::fs::remove_file(&installer_path).await;
        let hwnd = dialog_hwnd.take();
        if let Some(hwnd) = hwnd {
            unsafe {
                SendMessageW(hwnd, WM_CLOSE, Some(WPARAM(0)), Some(LPARAM(0)));
            }
        }
        rfd::MessageDialog::new()
            .set_title("出错了")
            .set_description(format!("WebView2 运行时安装程序写入或校验失败: {e}"))
            .set_level(rfd::MessageLevel::Error)
            .show();
        std::process::exit(0);
    }
    // 修改对话框内容
    let content = "正在安装 WebView2 运行时...";
    let content_utf16_nul = content
        .encode_utf16()
        .chain(std::iter::once(0))
        .collect::<Vec<u16>>();
    if let Some(hwnd) = dialog_hwnd.as_ref() {
        unsafe {
            SendMessageW(
                *hwnd,
                TDM_UPDATE_ELEMENT_TEXT.0 as u32,
                Some(WPARAM(TDE_CONTENT.0.try_into().unwrap())),
                Some(LPARAM(content_utf16_nul.as_ptr() as isize)),
            );
        }
    }
    // 运行安装器
    let status = tokio::process::Command::new(installer_path.clone())
        .arg("/install")
        .status()
        .await;
    if let Err(e) = status {
        let hwnd = dialog_hwnd.take();
        if let Some(hwnd) = hwnd {
            unsafe {
                SendMessageW(hwnd, WM_CLOSE, Some(WPARAM(0)), Some(LPARAM(0)));
            }
        }
        rfd::MessageDialog::new()
            .set_title("出错了")
            .set_description(format!("WebView2 运行时安装失败: {e}"))
            .set_level(rfd::MessageLevel::Error)
            .show();
        std::process::exit(0);
    }
    let status = status.unwrap();
    let _ = tokio::fs::remove_file(installer_path).await;
    if status.success() {
        // 关闭对话框
        let hwnd = dialog_hwnd.take();
        if let Some(hwnd) = hwnd {
            unsafe {
                SendMessageW(hwnd, WM_CLOSE, Some(WPARAM(0)), Some(LPARAM(0)));
            }
        }
        let _ = tokio::process::Command::new(std::env::current_exe().unwrap()).spawn();
        // 删除安装器
    } else {
        let hwnd = dialog_hwnd.take();
        if let Some(hwnd) = hwnd {
            unsafe {
                SendMessageW(hwnd, WM_CLOSE, Some(WPARAM(0)), Some(LPARAM(0)));
            }
        }
        rfd::MessageDialog::new()
            .set_title("出错了")
            .set_description("WebView2 运行时安装失败")
            .set_level(rfd::MessageLevel::Error)
            .show();
        std::process::exit(0);
    }
}
