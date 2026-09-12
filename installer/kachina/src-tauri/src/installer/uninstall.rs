use anyhow::Context;
use std::{
    os::windows::process::CommandExt,
    path::{Path, PathBuf},
};
use tokio::io::{AsyncReadExt, AsyncSeekExt, AsyncWriteExt};
use windows::Win32::System::Threading::CREATE_NO_WINDOW;

use crate::utils::error::{return_ta_result, TAResult};

lazy_static::lazy_static!(
    pub static ref DELETE_SELF_ON_EXIT_PATH: std::sync::RwLock<Option<String>> = std::sync::RwLock::new(None);
);

pub fn run_clear_empty_dirs(path: &Path) -> Result<(), std::io::Error> {
    let entries = std::fs::read_dir(path)?;
    for entry in entries {
        let entry = entry?;
        let path = entry.path();
        if path.is_dir() {
            run_clear_empty_dirs(&path)?;
            let entries = std::fs::read_dir(&path)?;
            if entries.count() == 0 {
                std::fs::remove_dir(&path)?;
            }
        }
    }
    Ok(())
}

pub fn delete_dir_if_empty(path: &Path) -> Result<(), std::io::Error> {
    let entries = std::fs::read_dir(path)?;
    if entries.count() == 0 {
        std::fs::remove_dir(path)?;
    }
    Ok(())
}

pub async fn rm_list(key: Vec<PathBuf>) -> Vec<String> {
    let mut set = tokio::task::JoinSet::new();
    for path in key {
        set.spawn(tokio::task::spawn_blocking(move || {
            let path = Path::new(&path);
            if path.exists() {
                let res = std::fs::remove_file(path);
                if res.is_err() {
                    return Err(format!("Failed to remove file: {:?}", res.err()));
                }
            }
            Ok(())
        }));
    }
    let res = set.join_all().await;
    let errs: Vec<String> = res
        .into_iter()
        .filter_map(|r| r.err())
        .map(|e| e.to_string())
        .collect();
    errs
}

pub async fn clear_empty_dirs(key: String) -> anyhow::Result<()> {
    tokio::task::spawn_blocking(move || {
        let path = Path::new(&key);
        run_clear_empty_dirs(path)?;
        delete_dir_if_empty(path)?;
        Ok(())
    })
    .await
    .context("CLEAR_EMPTY_DIR_ERR")?
}

/// 卸载时需要回收的「安装期写入的注册表项」。
///
/// 由项目配置 `extraUninstallRegistry` 提供，典型用途是清理开机自启动
/// （`HKCU\Software\Microsoft\Windows\CurrentVersion\Run` 下的值）以及软件
/// 自己写入的其他注册表键值。ARP（添加/删除程序）卸载项由 `run_uninstall`
/// 按 `reg_name` 自动处理，不需要在这里重复声明。
#[derive(serde::Deserialize, serde::Serialize, Clone, Debug)]
pub struct RegistryCleanupItem {
    /// 根键：`HKCU` / `HKLM` / `HKCR` / `HKU`（也接受 `HKEY_CURRENT_USER` 等全称，大小写不敏感）
    pub hive: String,
    /// 子键路径，例如 `Software\Microsoft\Windows\CurrentVersion\Run`
    pub key: String,
    /// 指定后仅删除该键下的这一个值；留空 / 省略则递归删除整个子键
    #[serde(default)]
    pub value: Option<String>,
}

#[derive(serde::Deserialize, serde::Serialize, Clone, Debug)]
pub struct RunUninstallArgs {
    source: String,
    files: Vec<String>,
    user_data_path: Vec<String>,
    extra_uninstall_path: Vec<String>,
    reg_name: String,
    uninstall_name: String,
    /// 额外注册表清理（安装时写入的自启动等项）。旧版前端不会传，故给默认值。
    #[serde(default)]
    extra_uninstall_registry: Vec<RegistryCleanupItem>,
    /// 尽力删除的路径（安装期由宿主自建/改名的快捷方式等）。
    /// 与 `extra_uninstall_path` 的区别：删不掉只记日志，绝不让卸载失败。
    #[serde(default)]
    extra_uninstall_shortcuts: Vec<String>,
}
pub async fn run_uninstall_with_args(args: RunUninstallArgs) -> TAResult<Vec<String>> {
    run_uninstall(
        args.source,
        args.files,
        args.user_data_path,
        args.extra_uninstall_path,
        args.reg_name,
        args.uninstall_name,
        args.extra_uninstall_registry,
        args.extra_uninstall_shortcuts,
    )
    .await
}

/// 删整棵子键时禁止命中的「共享容器」键名（大写比较）。
///
/// 这些键下面挂着别的软件甚至系统自己的项，一旦 `remove_tree` 就是把别人的
/// 自启动、卸载登记、策略一起端掉 —— 配置里少写一个 `value` 字段就可能触发，
/// 所以在这里硬性拦掉。要删这些键下的东西，必须写明 `value`。
const REG_TREE_DENY_LEAVES: &[&str] = &[
    "RUN",
    "RUNONCE",
    "RUNSERVICES",
    "RUNSERVICESONCE",
    "UNINSTALL",
    "POLICIES",
    "EXPLORER",
    "WINLOGON",
    "SHELL",
    "ENVIRONMENT",
    "IMAGE FILE EXECUTION OPTIONS",
    "CLASSES",
    "SOFTWARE",
    "MICROSOFT",
    "WINDOWS",
    "CURRENTVERSION",
    "SYSTEM",
    "SERVICES",
    "DRIVERS",
    "SESSION MANAGER",
    "EXTENSIONS",
];

/// 注册表清理的安全阀：这条通道以卸载器权限（通常是管理员）运行，配置写错
/// 就可能删掉系统关键项，因此对路径深度和键名做白/黑名单校验。
///
/// - 删值：子键至少两级（不碰任何根键的直属项）
/// - 删整棵子键：至少三级，且最后一级不是共享容器（见 `REG_TREE_DENY_LEAVES`）
fn is_safe_registry_target(key_path: &str, value: Option<&str>) -> bool {
    let segments = key_path
        .split('\\')
        .filter(|s| !s.is_empty())
        .collect::<Vec<_>>();
    match value {
        Some(_) => segments.len() >= 2,
        None => {
            if segments.len() < 3 {
                return false;
            }
            let leaf = segments[segments.len() - 1].to_ascii_uppercase();
            !REG_TREE_DENY_LEAVES.contains(&leaf.as_str())
        }
    }
}

/// 对单个根键执行「删值」或「删整棵子键」。失败一律忽略：卸载不应因为某个
/// 注册表项不存在（或当前权限不足）而中断。
///
/// 调用前必须已过 `is_safe_registry_target`。
///
/// 注意 `windows_registry` 的 `LOCAL_MACHINE` / `CURRENT_USER` / `USERS` 等
/// 预定义根键本身就是 `&'static Key`，直接传即可，不要再取引用。
fn apply_registry_cleanup(root: &windows_registry::Key, key_path: &str, value: Option<&str>) {
    match value {
        Some(value) if !value.is_empty() => {
            if let Ok(key) = root.open(key_path) {
                let _ = key.remove_value(value);
            }
        }
        _ => {
            let _ = root.remove_tree(key_path);
        }
    }
}

/// 遍历 `HKEY_USERS` 下已加载的用户配置单元，对每个 SID 应用一次清理。
///
/// 卸载器通常以管理员身份运行，此时 `HKCU` 指向的是管理员账户，而安装时写入
/// 自启动的是发起卸载的登录用户，只删 `HKCU` 会漏掉。这里遍历 `HKEY_USERS`
/// 补齐；未加载的配置单元打不开，会被静默跳过。
fn apply_registry_cleanup_for_all_users(key_path: &str, value: Option<&str>) {
    // 先绑定，避免 `keys()` 借用一个语句结束就析构的临时值
    let users_root = windows_registry::USERS;
    let sids = match users_root.keys() {
        Ok(sids) => sids,
        Err(_) => return,
    };
    for sid in sids {
        // `*_Classes` 只是视图键；`.DEFAULT` / LocalSystem 不是普通用户
        if sid.ends_with("_Classes") || sid == ".DEFAULT" || sid == "S-1-5-18" {
            continue;
        }
        let sub_key = format!("{sid}\\{key_path}");
        apply_registry_cleanup(users_root, &sub_key, value);
    }
}

/// 路径本身或任一父级是重解析点（符号链接 / junction）就返回 true。
///
/// 顺着链接删可能删到链接指向的任意位置，因此这类路径一律不动。
/// 读不到属性时按「危险」处理。
fn has_reparse_point(path: &Path) -> bool {
    use std::os::windows::fs::MetadataExt;
    const FILE_ATTRIBUTE_REPARSE_POINT: u32 = 0x0000_0400;
    let mut current = Some(path);
    while let Some(p) = current {
        let md = match std::fs::symlink_metadata(p) {
            Ok(md) => md,
            // 路径（或某个父级）压根不存在：没什么可删的，交给后面的 exists() 判断
            Err(e) if e.kind() == std::io::ErrorKind::NotFound => return false,
            // 属性读不到就别动它
            Err(_) => return true,
        };
        if md.file_attributes() & FILE_ATTRIBUTE_REPARSE_POINT != 0 {
            return true;
        }
        current = p.parent();
    }
    false
}

/// 是否位于 `%SystemRoot%`（默认 `C:\Windows`）之内。
fn is_under_system_root(path: &Path) -> bool {
    let root = std::env::var("SystemRoot").unwrap_or_else(|_| "C:\\Windows".to_string());
    let root = root.trim_end_matches(['\\', '/']).to_ascii_lowercase();
    if root.is_empty() {
        return true;
    }
    let target = path.to_string_lossy().to_ascii_lowercase();
    target == root || target.starts_with(&format!("{root}\\"))
}

fn dir_leaf(path: Option<&Path>) -> Option<String> {
    path?.file_name()?.to_str().map(|s| s.to_string())
}

fn name_matches(allowed: &[String], name: &str) -> bool {
    allowed.iter().any(|n| n.eq_ignore_ascii_case(name))
}

/// 「尽力删除」通道的安全阀。
///
/// 这条通道的路径由配置 + 前端拼出来，而卸载器通常以管理员身份运行，
/// 因此只放行**明确属于本产品**的快捷方式，其余一律跳过：
///
/// - 必须是绝对路径，不含 `..`
/// - 路径本身与所有父级都不是符号链接 / junction
/// - 不落在 `%SystemRoot%` 内
/// - 目录：只允许「开始菜单 `Programs\` 下、名字属于本产品」的那一层
/// - 文件：只允许 `.lnk`，且位于某个 `Desktop\` 目录下，或位于
///   `Programs\<产品名>\` 之内
///
/// `allowed_names` 由调用方给出（`reg_name` 与安装目录名），用于判断
/// 「属于本产品」。
fn is_safe_shortcut_target(path: &Path, allowed_names: &[String]) -> bool {
    if !path.is_absolute() {
        return false;
    }
    if path
        .components()
        .any(|c| matches!(c, std::path::Component::ParentDir))
    {
        return false;
    }
    if has_reparse_point(path) || is_under_system_root(path) {
        return false;
    }
    let leaf = match dir_leaf(Some(path)) {
        Some(leaf) => leaf,
        None => return false,
    };
    let parent_leaf = dir_leaf(path.parent());
    let grandparent_leaf = dir_leaf(path.parent().and_then(|p| p.parent()));

    if path.is_dir() {
        return name_matches(allowed_names, &leaf)
            && parent_leaf
                .as_deref()
                .is_some_and(|p| p.eq_ignore_ascii_case("Programs"));
    }

    if !leaf.to_ascii_lowercase().ends_with(".lnk") {
        return false;
    }
    if parent_leaf
        .as_deref()
        .is_some_and(|p| p.eq_ignore_ascii_case("Desktop"))
    {
        return true;
    }
    parent_leaf
        .as_deref()
        .is_some_and(|p| name_matches(allowed_names, p))
        && grandparent_leaf
            .as_deref()
            .is_some_and(|g| g.eq_ignore_ascii_case("Programs"))
}

/// 路径相等比较：统一分隔符、去掉尾部斜杠、大小写不敏感（Windows 语义）。
fn path_eq(a: &Path, b: &Path) -> bool {
    let norm = |p: &Path| -> String {
        let s = p.to_string_lossy().replace('/', "\\");
        let s = s.trim_end_matches('\\').to_ascii_lowercase();
        if s.len() == 2 && s.as_bytes()[1] == b':' {
            format!("{s}\\")
        } else {
            s
        }
    };
    norm(a) == norm(b)
}

/// 是否恰好等于某个受保护的根目录（系统目录、Program Files、用户配置目录等）。
///
/// 允许删这些目录**下面**的产品子目录，但不允许删它们自己。
fn is_protected_root(path: &Path) -> bool {
    // 盘符根 / UNC 根：`C:\`、`C:`、`\\server\share`
    if path.parent().is_none() {
        return true;
    }
    const VARS: &[&str] = &[
        "SystemRoot",
        "SystemDrive",
        "ProgramFiles",
        "ProgramFiles(x86)",
        "ProgramW6432",
        "ProgramData",
        "USERPROFILE",
        "HOMEDRIVE",
        "HOMEPATH",
        "APPDATA",
        "LOCALAPPDATA",
        "PUBLIC",
        "TEMP",
        "TMP",
    ];
    for name in VARS {
        if let Ok(v) = std::env::var(name) {
            let v = v.trim();
            if !v.is_empty() && path_eq(path, Path::new(v)) {
                return true;
            }
        }
    }
    false
}

/// 「删除用户数据 / 额外卸载目录」通道的安全阀。
///
/// 这条通道的路径同样来自打包配置与前端拼接，且通常以管理员权限执行
/// `remove_dir_all`，配置写错一个字符就可能删掉整台机器的东西，因此除通用的
/// 形状校验（绝对路径、无 `..`、非符号链接、不在 `%SystemRoot%` 内）之外，
/// 还额外挡掉两类灾难性目标：
/// - 盘符根，以及只有一级的目录（`C:\Foo`）
/// - 任何受保护根目录本身（见 `is_protected_root`）
fn is_safe_delete_target(path: &Path) -> bool {
    if !path.is_absolute() {
        return false;
    }
    if path
        .components()
        .any(|c| matches!(c, std::path::Component::ParentDir))
    {
        return false;
    }
    if has_reparse_point(path) || is_under_system_root(path) {
        return false;
    }
    let depth = path
        .components()
        .filter(|c| matches!(c, std::path::Component::Normal(_)))
        .count();
    if depth < 2 {
        return false;
    }
    !is_protected_root(path)
}

/// 尽力删除一批路径：不存在则跳过，删不掉只记日志。
///
/// 用于安装期由宿主自建/改名的快捷方式（例如把 `GenshinFpsUnlocker.lnk`
/// 规范成中文显示名），这些路径可能因权限不足或桌面被 OneDrive 重定向而
/// 不可删，但绝不该因此让整个卸载失败。
///
/// 每个路径都要先过 `is_safe_shortcut_target`，不通过的一律跳过并记日志。
async fn rm_best_effort(paths: &[String], allowed_names: &[String]) {
    for pathstr in paths {
        let path = Path::new(pathstr);
        if !is_safe_shortcut_target(path, allowed_names) {
            tracing::warn!("跳过不安全的卸载清理路径: {pathstr}");
            continue;
        }
        if !path.exists() {
            continue;
        }
        let res = if path.is_dir() {
            tokio::fs::remove_dir_all(path)
                .await
                .map_err(|e| e.to_string())
        } else {
            tokio::fs::remove_file(path)
                .await
                .map_err(|e| e.to_string())
        };
        match res {
            Ok(()) => tracing::info!("已删除 {pathstr}"),
            Err(e) => tracing::warn!("删除失败（已忽略）{pathstr}: {e}"),
        }
    }
}

/// 清理安装期写入的注册表项（自启动等）。所有错误都被吞掉，仅记录日志。
pub fn clean_extra_registry(items: &[RegistryCleanupItem]) {
    for item in items {
        let key_path = item.key.trim().trim_matches('\\');
        if key_path.is_empty() {
            tracing::warn!("跳过空的注册表清理键: hive={}", item.hive);
            continue;
        }
        // `value` 写成空字符串是配置错误：绝不当成「删整棵子键」处理
        let value = match item.value.as_deref() {
            Some(v) => {
                let v = v.trim();
                if v.is_empty() {
                    tracing::warn!(
                        "跳过 value 为空的注册表清理项（要删整棵子键请省略 value 字段）: {}\\{}",
                        item.hive,
                        item.key
                    );
                    continue;
                }
                Some(v)
            }
            None => None,
        };
        if !is_safe_registry_target(key_path, value) {
            tracing::warn!(
                "跳过不安全的注册表清理项: {}\\{} (value={:?})",
                item.hive,
                key_path,
                value
            );
            continue;
        }
        match item.hive.trim().to_ascii_uppercase().as_str() {
            "HKLM" | "HKEY_LOCAL_MACHINE" => {
                tracing::info!("清理注册表 HKLM\\{key_path}");
                apply_registry_cleanup(windows_registry::LOCAL_MACHINE, key_path, value);
            }
            "HKCR" | "HKEY_CLASSES_ROOT" => {
                tracing::info!("清理注册表 HKCR\\{key_path}");
                apply_registry_cleanup(windows_registry::CLASSES_ROOT, key_path, value);
            }
            "HKU" | "HKEY_USERS" => {
                tracing::info!("清理注册表 HKU\\{key_path}");
                apply_registry_cleanup(windows_registry::USERS, key_path, value);
            }
            "HKCU" | "HKEY_CURRENT_USER" => {
                tracing::info!("清理注册表 HKCU\\{key_path}");
                apply_registry_cleanup(windows_registry::CURRENT_USER, key_path, value);
                apply_registry_cleanup_for_all_users(key_path, value);
            }
            other => {
                tracing::warn!("跳过无法识别的注册表根键: {other}");
            }
        }
    }
}

#[tauri::command]
pub async fn run_uninstall(
    source: String,
    files: Vec<String>,
    user_data_path: Vec<String>,
    extra_uninstall_path: Vec<String>,
    reg_name: String,
    uninstall_name: String,
    extra_uninstall_registry: Vec<RegistryCleanupItem>,
    extra_uninstall_shortcuts: Vec<String>,
) -> TAResult<Vec<String>> {
    let exe_path = std::env::current_exe().context("GET_EXE_PATH_ERR")?;
    // check if exe_path is in source
    if DELETE_SELF_ON_EXIT_PATH.read().unwrap().is_none() && exe_path.starts_with(&source) {
        let tmp_dir = std::env::temp_dir();
        let mut tmp_uninstaller_path = tmp_dir.join(format!(
            "kachina.uninst.{}.exe",
            chrono::Utc::now().timestamp()
        ));
        // try to move current exe to tmp_uninstaller_path
        let res = tokio::fs::rename(&exe_path, &tmp_uninstaller_path).await;
        if res.is_err() {
            // move fail, maybe exe and tempdir is not in the same partition
            // try move to parent dir
            let source_parent = Path::new(&source).parent();
            if let Some(source_parent) = source_parent {
                tmp_uninstaller_path = source_parent.join(format!(
                    "kachina.uninst.{}.exe",
                    chrono::Utc::now().timestamp()
                ));
                tokio::fs::rename(&exe_path, &tmp_uninstaller_path)
                    .await
                    .context("SELF_UNINSTALL_ERR")?;
            } else {
                return return_ta_result(
                    "Insecure uninstall: installer is in root dir".to_string(),
                    "INSECURE_UNINSTALL_ERR",
                );
            }
        }
        // write delete_on_exit value
        DELETE_SELF_ON_EXIT_PATH
            .write()
            .unwrap()
            .replace(tmp_uninstaller_path.to_string_lossy().to_string());
    }

    let mut delete_list = files
        .iter()
        .map(|f| Path::new(source.as_str()).join(f))
        .filter(|f| f.exists() && *f != exe_path)
        .collect::<Vec<_>>();
    if !exe_path.starts_with(&source) {
        // external uninstaller
        delete_list.push(Path::new(source.as_str()).join(uninstall_name));
    }
    let res = rm_list(delete_list).await;

    // 先尽力清理安装期由宿主自建/改名的快捷方式（失败不影响卸载）。
    // 允许的产品名取 reg_name 与安装目录名，用于安全阀判断「属于本产品」。
    let allowed_names = [
        Some(reg_name.clone()),
        Path::new(source.as_str())
            .file_name()
            .and_then(|s| s.to_str())
            .map(String::from),
    ]
    .into_iter()
    .flatten()
    .filter(|n| !n.trim().is_empty())
    .collect::<Vec<_>>();
    rm_best_effort(&extra_uninstall_shortcuts, &allowed_names).await;

    // delete user data
    // merge user_data_path and extra_uninstall_path
    let to_be_delete = [&user_data_path[..], &extra_uninstall_path[..]].concat();
    for pathstr in to_be_delete.iter() {
        let path = Path::new(pathstr);
        if !is_safe_delete_target(path) {
            tracing::warn!("跳过不安全的卸载目录: {pathstr}");
            continue;
        }
        if path.exists() {
            // check if is file or dir
            if path.is_file() {
                tokio::fs::remove_file(path)
                    .await
                    .map_err(|e| {
                        anyhow::anyhow!("Failed to remove user data file {}: {:?}", pathstr, e)
                    })
                    .context("RM_USERDATA_ERR")?;
            } else {
                tokio::fs::remove_dir_all(path)
                    .await
                    .map_err(|e| {
                        anyhow::anyhow!("Failed to remove user data folder {}: {:?}", pathstr, e)
                    })
                    .context("RM_USERDATA_ERR")?;
            }
        }
    }

    // recursively delete empty folders
    clear_empty_dirs(source).await?;

    // 清理安装期写入的注册表项（开机自启动等），见项目配置 extraUninstallRegistry
    clean_extra_registry(&extra_uninstall_registry);

    // delete registry - try both HKLM and HKCU since installation could have used either
    let reg_path = format!("SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\{reg_name}");
    let _ = windows_registry::LOCAL_MACHINE.remove_tree(&reg_path);
    let _ = windows_registry::CURRENT_USER.remove_tree(&reg_path);

    Ok(res)
}

pub fn delete_self_on_exit() {
    let path = DELETE_SELF_ON_EXIT_PATH.read().unwrap();
    if path.is_none() {
        return;
    }
    let path = path.as_ref().unwrap();
    // run the cmd file with window hidden
    #[allow(clippy::zombie_processes)]
    let _ = std::process::Command::new("cmd")
        .arg("/C")
        .arg("ping")
        .arg("127.0.0.1")
        .arg("-n")
        .arg("2")
        .arg("&")
        .arg("del")
        .arg("/f")
        .arg("/q")
        .arg(path)
        .creation_flags(CREATE_NO_WINDOW.0)
        .spawn()
        .unwrap();
}

#[derive(serde::Deserialize, serde::Serialize, Clone, Debug)]
pub struct CreateUninstallerArgs {
    source: String,
    uninstaller_name: String,
    updater_name: String,
}
pub async fn create_uninstaller_with_args(args: CreateUninstallerArgs) -> TAResult<()> {
    create_uninstaller(args.source, args.uninstaller_name, args.updater_name).await
}

#[tauri::command]
pub async fn create_uninstaller(
    source: String,
    uninstaller_name: String,
    updater_name: String,
) -> TAResult<()> {
    let source = Path::new(&source);
    let uninstaller_path = source.join(uninstaller_name);
    let updater_path = source.join(updater_name);
    let current_exe_path = std::env::current_exe().context("GET_EXE_PATH_ERR")?;
    let updater_is_self = current_exe_path == updater_path;
    if !updater_is_self {
        // else, overwrite uninstaller and updater
        let mut self_configured_mmap = crate::local::get_base_with_config().await?;
        let output_file = tokio::fs::File::create(&uninstaller_path)
            .await
            .context("CREATE_UNINSTALLER_ERR")?;
        let mut output = tokio::io::BufWriter::new(output_file);
        tokio::io::copy(&mut self_configured_mmap, &mut output)
            .await
            .context("CREATE_UNINSTALLER_ERR")?;
        // flush
        output.flush().await.context("CREATE_UNINSTALLER_ERR")?;
        // drop
        drop(output);
        // open again with rw
        clear_index_mark(&uninstaller_path).await?;
        // find
        tokio::fs::copy(&uninstaller_path, &updater_path)
            .await
            .context("CREATE_UPDATER_ERR")?;
    } else {
        // try modify updater, if fail, silently ignore
        let _ = clear_index_mark(&updater_path).await;
    }
    Ok(())
}
pub async fn clear_index_mark(path: &PathBuf) -> anyhow::Result<()> {
    // open again with rw
    let mut output_file = tokio::fs::OpenOptions::new()
        .read(true)
        .write(true)
        .open(&path)
        .await
        .context("SELF_UPDATE_ERR")?;
    // read first 256 bytes to buffer
    let mut buffer = [0u8; 256];
    output_file
        .read_exact(&mut buffer)
        .await
        .context("SELF_UPDATE_ERR")?;

    // check ! and K
    let mark_pos = buffer.windows(2).position(|w| w == b"!K".as_ref());
    if let Some(mark_pos) = mark_pos {
        // check if equals !KachinaInstaller!
        let mark_str = "!KachinaInstaller!";
        let mark_real = String::from_utf8_lossy(&buffer[mark_pos..mark_pos + mark_str.len()]);
        if mark_real == mark_str {
            let index_start = mark_pos + mark_str.len();
            // PE header replaced with index. Remove it.
            // write 5*4 bytes of 0 after index_start
            output_file
                .seek(tokio::io::SeekFrom::Start(index_start as u64))
                .await
                .context("SELF_UPDATE_ERR")?;
            let zero = [0u8; 5 * 4];
            output_file
                .write_all(&zero)
                .await
                .context("SELF_UPDATE_ERR")?;
        }
    }
    // close file
    output_file.flush().await.context("SELF_UPDATE_ERR")?;
    output_file.sync_all().await.context("SELF_UPDATE_ERR")?;
    drop(output_file);
    Ok(())
}
