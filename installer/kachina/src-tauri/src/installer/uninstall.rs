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
        // 安全阀：这份清单是前端把 `latest_meta.deletes`（可能来自**网络元数据**）拼上
        // 安装目录得到的，而执行删除的是提权进程。上游直接 remove_file，一条
        // `..\..\..\Windows\System32\x.dll` 就能以管理员权限删任意文件。
        // 与 userDataPath / extraUninstallPath 共用同一套判定：必须绝对、不含 `..`、
        // 路径与父级都不是符号链接、不在 %SystemRoot% 内、不是受保护的根目录。
        if !is_safe_delete_target(&path) {
            tracing::warn!("跳过不安全的删除目标（rm_list）: {}", path.display());
            continue;
        }
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
pub fn has_reparse_point(path: &Path) -> bool {
    use std::os::windows::fs::MetadataExt;
    const FILE_ATTRIBUTE_REPARSE_POINT: u32 = 0x0000_0400;
    let mut current = Some(path);
    while let Some(p) = current {
        let md = match std::fs::symlink_metadata(p) {
            Ok(md) => md,
            // 路径（或某个父级）压根不存在：没什么可删的，交给后面的 存在() 判断
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

/// 路径归一化（仅用于比较）：统一分隔符、去掉尾部斜杠、转小写（Windows 语义）。
/// 盘符根（`C:`）补成 `C:\\`，免得前缀比较时把 `C:\\Foo` 判成不在 `C:` 里面。
fn normalize_path_for_compare(p: &Path) -> String {
    let s = p.to_string_lossy().replace('/', "\\");
    let s = s.trim_end_matches('\\').to_ascii_lowercase();
    if s.len() == 2 && s.as_bytes()[1] == b':' {
        format!("{s}\\")
    } else {
        s
    }
}

/// 路径相等比较：归一化后按字符串比（大小写不敏感、`/` 与 `\\` 等价）。
pub(crate) fn path_eq(a: &Path, b: &Path) -> bool {
    normalize_path_for_compare(a) == normalize_path_for_compare(b)
}

/// `child` 是否**严格落在** `parent` 里面（相等不算）。
///
/// 不用 `Path::starts_with`：它区分大小写，而注册表里的 `InstallLocation` 与
/// `current_exe()` 的大小写完全可能不一致（`C:\\Program Files` vs `c:\\program files`），
/// 一旦比不上就会把「自己的卸载器」误判成「外部卸载器」，转而去删正在运行的自己。
/// 也不用裸字符串前缀：`C:\\Foo` 会「以 `C:\\F` 开头」，必须按分隔符边界比。
pub(crate) fn path_starts_with(child: &Path, parent: &Path) -> bool {
    let c = normalize_path_for_compare(child);
    let p = normalize_path_for_compare(parent);
    if p.is_empty() || c.len() <= p.len() {
        return false;
    }
    if p.ends_with('\\') {
        // 盘符根 / UNC 根：归一化后已带尾部分隔符
        return c.starts_with(&p);
    }
    c.starts_with(&format!("{p}\\"))
}

/// 「安装目录内文件清单」通道的安全阀。
///
/// `files` 来自注册表的 `InstallerMeta`，而 `InstallerMeta` 在**非提权安装**时写在
/// HKCU —— 同账户的中等完整性进程就能改它；卸载却通常是提权跑的，于是这份清单会以
/// 管理员权限逐个 `remove_file`。上游直接 `source.join(f)`，两个洞：
/// `f` 是绝对路径时 `join` 会把 `source` 整个丢掉，`f` 带 `..` 时能逃出安装目录 ——
/// 「删自己装的文件」就变成了「删任意文件」。这里要求：相对路径、无盘符/根前缀、
/// 无 `..` 与 `.`、拼完仍落在安装目录内。不通过的一律跳过并记日志。
pub(crate) fn is_safe_relative_member(base: &Path, entry: &str) -> bool {
    let bytes = entry.as_bytes();
    if bytes.is_empty() || entry.contains('\0') {
        return false;
    }
    // 显式挡 Windows 形状（`\...`、`/...`、`C:...`）：靠 is_absolute()/components()
    // 判断会随宿主平台变化，而这套逻辑要在 Linux 上跑断言。
    if bytes[0] == b'\\' || bytes[0] == b'/' || (bytes.len() >= 2 && bytes[1] == b':') {
        return false;
    }
    // 显式挡 `..` / `.` 段：`Path::components()` 的分隔符语义随宿主平台变化
    // （Linux 上 `..\..\x` 是**一个**普通文件名，看不出 ParentDir），而这条判定
    // 必须在两个平台上都成立 —— devcheck 的 logic 层在 Linux 上跑，CI 两边都跑。
    if entry
        .split(['/', '\\'])
        .any(|seg| seg == ".." || seg == ".")
    {
        return false;
    }
    let p = Path::new(entry);
    if p.is_absolute() {
        return false;
    }
    let mut segments = 0usize;
    for c in p.components() {
        // 只允许普通路径段：一次挡掉 `..`、`.` 以及任何前缀/根组件
        if !matches!(c, std::path::Component::Normal(_)) {
            return false;
        }
        segments += 1;
    }
    if segments == 0 {
        return false;
    }
    path_starts_with(&base.join(p), base)
}

/// 是否恰好等于某个受保护的根目录（系统目录、Program Files、用户配置目录，
/// 以及用户配置目录下面的 Shell 容器）。
///
/// 允许删这些目录**下面**的产品子目录，但不允许删它们自己。
fn is_protected_root(path: &Path) -> bool {
    // 盘符根 / UNC 根：`C:\`、`C:`、`\\服务器\share`
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

    // 用户配置目录**下面一层**的 Shell 容器同样受保护：`%USERPROFILE%\Desktop`、
    // `%APPDATA%\Microsoft\Windows\Start Menu\Programs` 这些是「一整片东西」，
    // 配置里少写一段就会把用户的桌面 / 开始菜单整个端掉。产品自己的目录一定在
    // 它们下面至少一层（`…\Documents\GenshinFpsUnlocker`、`…\Programs\GenshinFpsUnlocker`、
    // `…\Desktop\GenshinFpsUnlocker.lnk`），所以这条不影响正常清理。
    // 组件用切片而不是拼好的字符串：`Path::join("AppData\\Local")` 在非 Windows 上
    // 会变成一个带反斜杠的**单一**组件，devcheck 的 logic 层就跑不了了。
    const SHELL_DIRS: &[(&str, &[&str])] = &[
        ("USERPROFILE", &["Desktop"]),
        ("USERPROFILE", &["Documents"]),
        ("USERPROFILE", &["Downloads"]),
        ("USERPROFILE", &["Music"]),
        ("USERPROFILE", &["Pictures"]),
        ("USERPROFILE", &["Videos"]),
        ("USERPROFILE", &["AppData"]),
        ("USERPROFILE", &["AppData", "Local"]),
        ("USERPROFILE", &["AppData", "LocalLow"]),
        ("USERPROFILE", &["AppData", "Roaming"]),
        ("APPDATA", &["Microsoft"]),
        ("APPDATA", &["Microsoft", "Windows"]),
        ("APPDATA", &["Microsoft", "Windows", "Start Menu"]),
        (
            "APPDATA",
            &["Microsoft", "Windows", "Start Menu", "Programs"],
        ),
        (
            "APPDATA",
            &["Microsoft", "Windows", "Start Menu", "Programs", "Startup"],
        ),
        ("LOCALAPPDATA", &["Microsoft"]),
        ("LOCALAPPDATA", &["Microsoft", "Windows"]),
        ("LOCALAPPDATA", &["Programs"]),
        ("PUBLIC", &["Desktop"]),
    ];
    for (var, rel) in SHELL_DIRS {
        let Ok(v) = std::env::var(var) else { continue };
        let v = v.trim();
        if v.is_empty() {
            continue;
        }
        let mut full = PathBuf::from(v);
        for component in *rel {
            full.push(component);
        }
        if path_eq(path, &full) {
            return true;
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
pub(crate) fn is_safe_delete_target(path: &Path) -> bool {
    if !path.is_absolute() {
        return false;
    }
    // `..` 既按 组件 判、也按文本判：`Path::components()` 的分隔符语义随宿主平台
    // 变化（Linux 上 `..\..\x` 是一个普通文件名），而这套判定要在两个平台上都成立
    // —— devcheck 的 logic 层在 Linux 上跑，CI 两边都跑。
    if path
        .to_string_lossy()
        .split(['/', '\\'])
        .any(|seg| seg == "..")
    {
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

/// 展开 `%VAR%` 形式的环境变量。
///
/// 注册表里的 `ProfileImagePath` 常写成 `%SystemDrive%\Users\xxx`（REG_EXPAND_SZ），
/// 不展开就不能当路径用。未知变量原样保留：宁可少删，也不要拼出半个路径去删。
fn expand_env_vars(input: &str) -> String {
    let mut out = String::with_capacity(input.len());
    let mut rest = input;
    loop {
        let (head, tail) = match rest.split_once('%') {
            Some(v) => v,
            None => {
                out.push_str(rest);
                break;
            }
        };
        out.push_str(head);
        match tail.split_once('%') {
            // 只剩前半个 %：后面的原样输出
            None => {
                out.push('%');
                out.push_str(tail);
                break;
            }
            Some((name, after)) => {
                if name.is_empty() {
                    // "%%" 当成一个字面 %
                    out.push('%');
                    rest = &tail[1..];
                    continue;
                }
                match std::env::var(name) {
                    Ok(value) => out.push_str(&value),
                    Err(_) => {
                        out.push('%');
                        out.push_str(name);
                        out.push('%');
                    }
                }
                rest = after;
            }
        }
    }
    out
}

/// 批量展开路径里的 `%VAR%`，顺手去空白与空项。
fn expand_path_list(paths: &[String]) -> Vec<String> {
    paths
        .iter()
        .map(|p| expand_env_vars(p.trim()))
        .filter(|p| !p.is_empty())
        .collect()
}

/// 允许做「多用户清理」的每用户容器（相对 `%USERPROFILE%` 的第一级目录）。
///
/// 只有落在这些容器下面的路径，才会被映射到别的用户配置目录去删。这样即使
/// 配置里写了安装目录、`ProgramData` 或某个不相干的路径，也不会被当成
/// 「每个用户都有一份」而到处删。
const PER_USER_CLEANUP_ROOTS: &[&str] = &["AppData", "Documents", "Desktop"];

/// 跨用户重放时禁止命中的「Shell 容器」名（大写比较）。
///
/// 重放的做法是把「相对用户目录的尾巴」拼到每一个用户目录上，所以尾巴本身一旦是
/// 容器而不是产品目录，后果就是**所有用户**的开始菜单 / 文档 / 桌面被整个端掉。
/// 现实中触发得到：`extra_uninstall_path` 里的开始菜单文件夹是前端拼的
/// `Programs\{appName}`，`appName` 万一是空串，尾巴就退化成
/// `AppData\Roaming\Microsoft\Windows\Start Menu\Programs`。
/// 产品自己的目录名（`GenshinFpsUnlocker`）、中文快捷方式名（`原神帧率解锁.lnk`）
/// 都不在这张表里，所以功能不受影响 —— 与注册表那边的 `REG_TREE_DENY_LEAVES`
/// 同一个思路：命中即拒绝 + 记日志，绝不让卸载失败。
const PER_USER_DENY_LEAVES: &[&str] = &[
    "APPDATA",
    "LOCAL",
    "LOCALLOW",
    "ROAMING",
    "MICROSOFT",
    "WINDOWS",
    "START MENU",
    "PROGRAMS",
    "STARTUP",
    "SYSTEM",
    "SYSTEM32",
    "TEMP",
    "TMP",
    "CACHE",
    "CLASSES",
    "SOFTWARE",
    "USERS",
    "PUBLIC",
    "PROFILE",
    "DESKTOP",
    "DOCUMENTS",
    "DOWNLOADS",
    "MUSIC",
    "PICTURES",
    "VIDEOS",
    "TEMPLATES",
    "FAVORITES",
    "CONTACTS",
    "LINKS",
    "SAVED GAMES",
    "SEARCHES",
    "3D OBJECTS",
    "ONEDRIVE",
];

/// 取路径相对当前用户配置目录（`%USERPROFILE%`）的尾部，并校验它确实落在
/// 允许清理的每用户容器里；不满足返回 `None`（说明这条路径与「哪个用户」无关，
/// 例如公共开始菜单、安装目录本身）。
///
/// 额外限制，避免把配置目录整个端掉：
/// - 尾部至少两级（不接受 `%USERPROFILE%\Foo` 这种直接挂在配置目录下的）
/// - `Desktop` 下只放行 `.lnk`（别人桌面上的文档一概不碰）
fn profile_relative_tail(path: &Path) -> Option<PathBuf> {
    let profile = std::env::var_os("USERPROFILE").map(PathBuf::from)?;
    if profile.as_os_str().is_empty() {
        return None;
    }
    let tail = path.strip_prefix(profile.as_path()).ok()?;
    if tail
        .components()
        .any(|c| matches!(c, std::path::Component::ParentDir))
    {
        return None;
    }
    let first = match tail.components().next() {
        Some(std::path::Component::Normal(s)) => s.to_str()?,
        _ => return None,
    };
    if !PER_USER_CLEANUP_ROOTS
        .iter()
        .any(|r| r.eq_ignore_ascii_case(first))
    {
        return None;
    }
    if tail.components().count() < 2 {
        return None;
    }
    if first.eq_ignore_ascii_case("Desktop")
        && !tail
            .to_string_lossy()
            .to_ascii_lowercase()
            .ends_with(".lnk")
    {
        return None;
    }
    // 叶子名不能是 Shell 容器（见 PER_USER_DENY_LEAVES 的注释）
    let leaf = tail.components().next_back()?.as_os_str().to_str()?;
    if PER_USER_DENY_LEAVES
        .iter()
        .any(|d| d.eq_ignore_ascii_case(leaf))
    {
        return None;
    }
    // AppData 下至少三级：两级就意味着直接挂在 `AppData\Local` / `AppData\Roaming`
    // 这一层，那一层只可能是容器本身。Documents / Desktop 下两级是正常形状
    // （`Documents\GenshinFpsUnlocker`、`Desktop\xx.lnk`），不受这条限制。
    if first.eq_ignore_ascii_case("AppData") && tail.components().count() < 3 {
        return None;
    }
    Some(tail.to_path_buf())
}

/// 枚举本机已加载的用户配置目录（`ProfileList\<SID>\ProfileImagePath`）。
///
/// 卸载器通常以管理员身份运行，`%LOCALAPPDATA%` 指向的是**执行卸载的账户**；
/// 当初安装/使用本软件的可能是另一个账户。注册表清理已经按 `HKEY_USERS` 补齐了
/// （见 `apply_registry_cleanup_for_all_users`），文件这边靠这个函数补齐。
/// 未加载的配置单元打不开，静默跳过。
fn loaded_profile_roots() -> Vec<PathBuf> {
    const PROFILE_LIST: &str = r"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList";
    let list = match windows_registry::LOCAL_MACHINE.open(PROFILE_LIST) {
        Ok(key) => key,
        Err(_) => return Vec::new(),
    };
    let sids = match list.keys() {
        Ok(sids) => sids,
        Err(_) => return Vec::new(),
    };
    let mut roots: Vec<PathBuf> = Vec::new();
    for sid in sids {
        // `*_Classes` 只是视图键；`.DEFAULT` / LocalSystem 不是普通登录用户
        if sid.ends_with("_Classes") || sid == ".DEFAULT" || sid == "S-1-5-18" {
            continue;
        }
        let key = match list.open(&sid) {
            Ok(key) => key,
            Err(_) => continue,
        };
        let raw = match key.get_string("ProfileImagePath") {
            Ok(value) => value,
            Err(_) => continue,
        };
        let expanded = expand_env_vars(raw.trim());
        if expanded.is_empty() {
            continue;
        }
        let root = PathBuf::from(expanded);
        if !root.is_absolute() {
            continue;
        }
        if !roots.iter().any(|r| path_eq(r, &root)) {
            roots.push(root);
        }
    }
    roots
}

/// 把「当前用户展开后的数据目录 / 快捷方式」映射到所有已加载用户配置目录下的
/// 同一相对位置，得到还需要补删的候选路径。
///
/// 每个候选都要过 `is_safe_delete_target`，并且必须真实存在；文件只放行 `.lnk`，
/// 目录不限（数据目录里可能有 webview2 缓存等任意内容）。
fn collect_all_users_cleanup_targets(paths: &[String]) -> Vec<PathBuf> {
    let mut tails: Vec<PathBuf> = Vec::new();
    for pathstr in paths {
        if let Some(tail) = profile_relative_tail(Path::new(pathstr)) {
            if !tails.iter().any(|t| t == &tail) {
                tails.push(tail);
            }
        }
    }
    if tails.is_empty() {
        return Vec::new();
    }
    let mut out: Vec<PathBuf> = Vec::new();
    for root in loaded_profile_roots() {
        for tail in &tails {
            let candidate = root.join(tail);
            if !is_safe_delete_target(&candidate) {
                tracing::warn!("跳过不安全的多用户清理路径: {}", candidate.display());
                continue;
            }
            let is_lnk = tail
                .to_string_lossy()
                .to_ascii_lowercase()
                .ends_with(".lnk");
            if !candidate.is_dir() && !(is_lnk && candidate.is_file()) {
                continue;
            }
            if !out.iter().any(|o| path_eq(o, &candidate)) {
                out.push(candidate);
            }
        }
    }
    out
}

/// 多用户残留清理：尽力而为，删不掉只记日志，绝不让卸载失败。
async fn clean_per_user_leftovers(paths: &[String]) {
    for target in collect_all_users_cleanup_targets(paths) {
        // 别叫 display：tracing 的宏会把 `{display}` 当成 字段::display 函数
        let target_str = target.display().to_string();
        let res = if target.is_dir() {
            tokio::fs::remove_dir_all(&target)
                .await
                .map_err(|e| e.to_string())
        } else {
            tokio::fs::remove_file(&target)
                .await
                .map_err(|e| e.to_string())
        };
        match res {
            Ok(()) => tracing::info!("已清理其它账户的残留 {target_str}"),
            Err(e) => tracing::warn!("清理其它账户的残留失败（已忽略）{target_str}: {e}"),
        }
    }
}

/// `%TEMP%` 里属于本安装器（Kachina）的文件名白名单。
///
/// 只认这几个固定形状，绝不按「含 kachina 就删」这种模糊规则来：
/// - `KachinaInstaller.log`：安装/卸载日志，一直在追加，从来没人删
/// - `Kachina.RuntimePackage.<tag>.exe`：.NET / VCRedist 运行时安装包
///   （安装成功会删，失败时 return Err 就留在临时目录里，几十 MB）
/// - `kachina.MicrosoftEdgeWebview2Setup.exe`：WebView2 引导安装器
/// - `kachina.uninst.<时间戳>.exe`：卸载器把自己挪到临时目录后的副本
///   （正常由 `delete_self_on_exit` 删，失败时留下）
fn is_installer_temp_artifact(name: &str) -> bool {
    let lower = name.to_ascii_lowercase();
    lower == "kachinainstaller.log"
        || lower == "kachina.microsoftedgewebview2setup.exe"
        || (lower.starts_with("kachina.runtimepackage.") && lower.ends_with(".exe"))
        || (lower.starts_with("kachina.uninst.") && lower.ends_with(".exe"))
}

/// 清理 `%TEMP%` 里本安装器留下的东西。`skip` 是正在运行的卸载器自身
/// （删不掉也不该删，交给 `delete_self_on_exit`）。失败只记日志。
async fn clean_installer_temp_files(skip: Option<&str>) {
    let temp = std::env::temp_dir();
    let mut entries = match tokio::fs::read_dir(&temp).await {
        Ok(entries) => entries,
        Err(e) => {
            tracing::warn!("读取临时目录失败（已忽略）{}: {e}", temp.display());
            return;
        }
    };
    while let Ok(Some(entry)) = entries.next_entry().await {
        let path = entry.path();
        let name = match path.file_name().and_then(|s| s.to_str()) {
            Some(name) => name.to_string(),
            None => continue,
        };
        if !is_installer_temp_artifact(&name) {
            continue;
        }
        if let Some(self_path) = skip {
            if path_eq(&path, Path::new(self_path)) {
                continue;
            }
        }
        // 只删文件：同名目录不是本安装器造的
        match entry.file_type().await {
            Ok(ft) if ft.is_file() => {}
            _ => continue,
        }
        match tokio::fs::remove_file(&path).await {
            Ok(()) => tracing::info!("已删除临时文件 {}", path.display()),
            Err(e) => tracing::warn!("删除临时文件失败（已忽略）{}: {e}", path.display()),
        }
    }
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
    let source_path: &Path = Path::new(source.as_str());
    // 检查 exe_path 是否位于 source 中（大小写不敏感，见 path_starts_with）
    if DELETE_SELF_ON_EXIT_PATH.read().unwrap().is_none()
        && path_starts_with(&exe_path, source_path)
    {
        let tmp_dir = std::env::temp_dir();
        let mut tmp_uninstaller_path = tmp_dir.join(format!(
            "kachina.uninst.{}.exe",
            chrono::Utc::now().timestamp()
        ));
        // 尝试将当前 exe 移动到 tmp_uninstaller_path
        let res = tokio::fs::rename(&exe_path, &tmp_uninstaller_path).await;
        if res.is_err() {
            // 移动失败，可能是 exe 与临时目录不在同一分区
            // 尝试移动到父目录
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
        // 写入 delete_on_exit 值
        DELETE_SELF_ON_EXIT_PATH
            .write()
            .unwrap()
            .replace(tmp_uninstaller_path.to_string_lossy().to_string());
    }

    let mut delete_list: Vec<PathBuf> = Vec::new();
    for f in files.iter() {
        if !is_safe_relative_member(source_path, f) {
            tracing::warn!("跳过安装目录外的文件清单条目: {f}");
            continue;
        }
        let p = source_path.join(f);
        if p.exists() && !path_eq(&p, &exe_path) {
            delete_list.push(p);
        }
    }
    if !path_starts_with(&exe_path, source_path) {
        // 外部卸载器
        if is_safe_relative_member(source_path, &uninstall_name) {
            delete_list.push(source_path.join(&uninstall_name));
        } else {
            tracing::warn!("跳过不安全的卸载器名: {uninstall_name}");
        }
    }
    let res = rm_list(delete_list).await;

    // 先尽力清理安装期由宿主自建/改名的快捷方式（失败不影响卸载）。
    // 允许的产品名取 reg_name 与安装目录名，用于安全阀判断「属于本产品」。
    let allowed_names = [
        Some(reg_name.clone()),
        source_path
            .file_name()
            .and_then(|s| s.to_str())
            .map(String::from),
    ]
    .into_iter()
    .flatten()
    .filter(|n| !n.trim().is_empty())
    .collect::<Vec<_>>();
    // 快捷方式路径同样可能带 %VAR%（前端只展开 ${INSTALL_PATH} / ${APP_NAME}）
    let extra_shortcuts = expand_path_list(&extra_uninstall_shortcuts);
    rm_best_effort(&extra_shortcuts, &allowed_names).await;

    // 删除用户数据
    // 合并 user_data_path 与 extra_uninstall_path
    //
    // 配置里允许写 `%LOCALAPPDATA%/GenshinFpsUnlocker` 这种带环境变量的路径，而前端
    // 的 replacePathEnvirables 只展开 `${INSTALL_PATH}` / `${APP_NAME}`，不碰 `%VAR%`。
    // 不在这里展开的话，下面的安全阀会因为「不是绝对路径」把整条跳过 —— 结果就是
    // 用户勾了「删除配置与日志」也一个字节都没删（配置.json / 日志 / webview2 全留下）。
    // 失败语义：这里的错误**不再提前返回**，只记进 `fatal`，等注册表清理跑完再抛。
    // 上游是直接 `?`：用户数据删失败（文件被占用等）时，安装目录的文件已经删了、
    // 卸载器副本也已移到 %TEMP% 且关窗即自删，ARP 项却还留着指向一个不存在的 exe，
    // 「应用和功能」里就永远卸不掉了 —— 只能手工删注册表。宁可让用户看到「数据目录
    // 没删干净」的报错，也要保证 ARP 项和自启动项一定被清掉。
    let mut fatal: Option<anyhow::Error> = None;
    let to_be_delete = expand_path_list(&[&user_data_path[..], &extra_uninstall_path[..]].concat());
    for pathstr in to_be_delete.iter() {
        let path = Path::new(pathstr);
        if !is_safe_delete_target(path) {
            tracing::warn!("跳过不安全的卸载目录: {pathstr}");
            continue;
        }
        if !path.exists() {
            continue;
        }
        // 检查是文件还是目录
        let rm = if path.is_file() {
            tokio::fs::remove_file(path).await.map_err(|e| {
                anyhow::anyhow!("Failed to remove user data file {}: {:?}", pathstr, e)
            })
        } else {
            tokio::fs::remove_dir_all(path).await.map_err(|e| {
                anyhow::anyhow!("Failed to remove user data folder {}: {:?}", pathstr, e)
            })
        };
        if let Err(e) = rm.context("RM_USERDATA_ERR") {
            tracing::error!("删除用户数据失败（继续清理注册表）: {e:#}");
            if fatal.is_none() {
                fatal = Some(e);
            }
        }
    }

    // 多用户补齐：上面那轮删的是「执行卸载的账户」的数据目录 / 快捷方式，
    // 其它登录过的账户（当初安装、使用本软件的那个）按 ProfileList 再扫一遍。
    // 注册表那边早就这么做了（见 apply_registry_cleanup_for_all_users）。
    clean_per_user_leftovers(&to_be_delete).await;

    // %TEMP% 里本安装器留下的运行时安装包 / 日志：安装成功会删，失败就留着
    let self_tmp = DELETE_SELF_ON_EXIT_PATH.read().unwrap().clone();
    clean_installer_temp_files(self_tmp.as_deref()).await;

    // 递归删除空目录
    if let Err(e) = clear_empty_dirs(source.clone()).await {
        tracing::error!("清理空目录失败（继续清理注册表）: {e:#}");
        if fatal.is_none() {
            fatal = Some(e);
        }
    }

    // 清理安装期写入的注册表项（开机自启动等），见项目配置 extraUninstallRegistry
    clean_extra_registry(&extra_uninstall_registry);

    // 删除注册表——HKLM 与 HKCU 都尝试，因为安装可能使用任一项
    let reg_path = format!("SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\{reg_name}");
    let _ = windows_registry::LOCAL_MACHINE.remove_tree(&reg_path);
    let _ = windows_registry::CURRENT_USER.remove_tree(&reg_path);

    if let Some(e) = fatal {
        return Err(e.into());
    }
    Ok(res)
}

pub fn delete_self_on_exit() {
    let path = DELETE_SELF_ON_EXIT_PATH.read().unwrap();
    if path.is_none() {
        return;
    }
    let path = path.as_ref().unwrap();
    // 隐藏窗口运行 cmd 文件
    #[allow(clippy::zombie_processes)]
    if let Err(e) = std::process::Command::new("cmd")
        // 保持 Shell 命令固定：安装目录可能包含“&”
        // 或“%”。展开带引号的环境变量，可避免
        // 这些字符被当作命令语法解释。
        .env("KACHINA_DELETE_SELF_TARGET", path)
        .raw_arg("/C ping 127.0.0.1 -n 2 >NUL & del /f /q \"%KACHINA_DELETE_SELF_TARGET%\"")
        .creation_flags(CREATE_NO_WINDOW.0)
        .spawn()
    {
        tracing::warn!("启动自删除Command失败: {e}");
    }
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
    if !source.is_absolute()
        || !is_safe_relative_member(source, &uninstaller_name)
        || !is_safe_relative_member(source, &updater_name)
    {
        return Err(anyhow::anyhow!("Invalid installer output path")
            .context("CREATE_UNINSTALLER_ERR")
            .into());
    }
    let uninstaller_path = source.join(uninstaller_name);
    let updater_path = source.join(updater_name);
    // 不跟随过期或遭篡改安装目录中的 junction 或符号链接，
    // 否则 create() 和 copy() 都可能在提权后写入
    // 安装 根目录 while running 提权.
    if has_reparse_point(&uninstaller_path) || has_reparse_point(&updater_path) {
        return Err(anyhow::anyhow!("Installer output path is a reparse point")
            .context("CREATE_UNINSTALLER_ERR")
            .into());
    }
    let current_exe_path = std::env::current_exe().context("GET_EXE_PATH_ERR")?;
    let updater_is_self = path_eq(&current_exe_path, &updater_path);
    if !updater_is_self {
        // 否则覆盖卸载器和更新器
        let mut self_configured_mmap = crate::local::get_base_with_config().await?;
        let output_file = tokio::fs::File::create(&uninstaller_path)
            .await
            .context("CREATE_UNINSTALLER_ERR")?;
        let mut output = tokio::io::BufWriter::new(output_file);
        tokio::io::copy(&mut self_configured_mmap, &mut output)
            .await
            .context("CREATE_UNINSTALLER_ERR")?;
        // 刷新
        output.flush().await.context("CREATE_UNINSTALLER_ERR")?;
        // 释放
        drop(output);
        // 以读写模式再次打开
        clear_index_mark(&uninstaller_path).await?;
        // 查找
        tokio::fs::copy(&uninstaller_path, &updater_path)
            .await
            .context("CREATE_UPDATER_ERR")?;
    } else {
        // 尝试修改更新器，失败时静默忽略
        let _ = clear_index_mark(&updater_path).await;
    }
    Ok(())
}
pub async fn clear_index_mark(path: &PathBuf) -> anyhow::Result<()> {
    // 以读写模式再次打开
    let mut output_file = tokio::fs::OpenOptions::new()
        .read(true)
        .write(true)
        .open(&path)
        .await
        .context("SELF_UPDATE_ERR")?;
    // 读取前 256 个字节到缓冲区
    let mut buffer = [0u8; 256];
    output_file
        .read_exact(&mut buffer)
        .await
        .context("SELF_UPDATE_ERR")?;

    // 检查 ! 和 K
    let mark_pos = buffer.windows(2).position(|w| w == b"!K".as_ref());
    if let Some(mark_pos) = mark_pos {
        // 检查是否等于 !KachinaInstaller!
        let mark_str = "!KachinaInstaller!";
        let mark_real = String::from_utf8_lossy(&buffer[mark_pos..mark_pos + mark_str.len()]);
        if mark_real == mark_str {
            let index_start = mark_pos + mark_str.len();
            // PE 头部已被索引替换，将其移除。
            // 在 index_start 后写入 5*4 个零字节
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
    // 关闭文件
    output_file.flush().await.context("SELF_UPDATE_ERR")?;
    output_file.sync_all().await.context("SELF_UPDATE_ERR")?;
    drop(output_file);
    Ok(())
}
