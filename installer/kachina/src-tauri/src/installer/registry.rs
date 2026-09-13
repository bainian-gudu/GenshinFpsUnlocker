use crate::utils::{
    error::{IntoTAResult, TAResult},
    uac::check_elevated,
};
use anyhow::{Context, Result};
use serde_json::Value;

#[derive(serde::Deserialize, serde::Serialize, Debug, Clone)]
pub struct WriteRegistryParams {
    pub reg_name: String,
    pub name: String,
    pub version: String,
    pub exe: String,
    pub source: String,
    pub uninstaller: String,
    pub metadata: String,
    pub size: u64,
    pub publisher: String,
}

pub async fn write_registry_with_params(params: WriteRegistryParams) -> TAResult<()> {
    write_registry(
        params.reg_name,
        params.name,
        params.version,
        params.exe,
        params.source,
        params.uninstaller,
        params.metadata,
        params.size,
        params.publisher,
    )
    .await
}

#[tauri::command]
pub async fn write_registry(
    reg_name: String,
    name: String,
    version: String,
    exe: String,
    source: String,
    uninstaller: String,
    metadata: String,
    size: u64,
    publisher: String,
) -> TAResult<()> {
    write_registry_raw(
        reg_name,
        name,
        version,
        exe,
        source,
        uninstaller,
        metadata,
        size,
        publisher,
    )
    .await
    .into_ta_result()
}
pub async fn write_registry_raw(
    reg_name: String,
    name: String,
    version: String,
    exe: String,
    source: String,
    uninstaller: String,
    metadata: String,
    size: u64,
    publisher: String,
) -> Result<()> {
    let elevated = check_elevated().unwrap_or(false);
    let hive = if elevated {
        windows_registry::LOCAL_MACHINE
    } else {
        windows_registry::CURRENT_USER
    };

    let key_path = format!("SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\{reg_name}");

    let key = hive.create(&key_path).context("OPEN_REG_ERR")?;
    {
        key.set_string("DisplayName", &name)?;
        key.set_string("DisplayVersion", &version)?;
        // 路径带空格（默认就装在 `Program Files\` 下），ARP 的命令行必须加引号，
        // 否则「应用和功能」的卸载按钮要靠 CreateProcess 的逐级猜测才能碰对。
        key.set_string("UninstallString", &format!("\"{uninstaller}\""))?;
        // 静默卸载（winget / 自动化脚本走这个值）。**只能用短选项**：
        // `cli::InstallArgs` 中这几个选项只声明了 `short`（-U / -S / -I），
        // clap 不会凭空生成长名，写 `--uninstall` 会直接以退出码 2 报
        // 「unexpected argument」，卸载根本不发生。
        // -U 进卸载流程（其实卸载器靠自身文件名判定，这里显式带上更稳），
        // -S 跑完自动关窗，-I 不弹任何询问；
        // 用户数据默认保留（前端 deleteUserData 初值是 false），符合静默卸载的预期。
        key.set_string(
            "QuietUninstallString",
            &format!("\"{uninstaller}\" -U -S -I"),
        )?;
        key.set_string("InstallLocation", &source)?;
        key.set_string("DisplayIcon", &exe)?;
        key.set_string("Publisher", &publisher)?;
        key.set_u32("EstimatedSize", (size as u32) / 1024)?;
        key.set_u32("NoModify", 1u32)?;
        key.set_u32("NoRepair", 1u32)?;
        key.set_string("InstallerMeta", &metadata)?;
        Ok::<(), anyhow::Error>(())
    }
    .context("WRITE_REG_ERR")
}

#[tauri::command]
pub async fn read_uninstall_metadata(reg_name: String) -> TAResult<Value> {
    let key_path = format!("SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\{reg_name}");

    // 先尝试 HKLM，不存在时再尝试 HKCU
    let key = windows_registry::LOCAL_MACHINE
        .options()
        .read()
        .open(&key_path)
        .or_else(|_| {
            windows_registry::CURRENT_USER
                .options()
                .read()
                .open(&key_path)
        })
        .context("GET_INSTALLMETA_ERR")?;

    let metadata: String = key
        .get_string("InstallerMeta")
        .context("GET_INSTALLMETA_ERR")?;

    let metadata: Value = serde_json::from_str(&metadata).context("GET_INSTALLMETA_ERR")?;
    Ok(metadata)
}
