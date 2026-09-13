use anyhow::{Context, Result};
use std::path::Path;
use windows::{
    core::GUID,
    Win32::UI::Shell::{
        FOLDERID_Desktop, FOLDERID_Documents, FOLDERID_Downloads, FOLDERID_LocalAppData,
        FOLDERID_LocalAppDataLow, FOLDERID_Profile, FOLDERID_RoamingAppData, SHGetKnownFolderPath,
        KF_FLAG_DEFAULT,
    },
};

pub fn get_dir(dir: &GUID) -> Result<String> {
    let pwstr = unsafe {
        SHGetKnownFolderPath(dir, KF_FLAG_DEFAULT, None)
            .map(|pwstr| pwstr.to_string().context("INTERNAL_ERROR"))
            .context("GET_KNOWNFOLDER_ERR")??
    };
    Ok(pwstr)
}

pub fn get_userprofile() -> Result<String> {
    // GetUserProfileDirectoryW 需要有效的用户令牌句柄。传入
    // 默认或空句柄（旧实现的做法）会导致
    // ERROR_INVALID_HANDLE 错误，并让私有目录检查失效。
    // Shell 为交互用户解析 Profile 已知目录，
    // 不需要令牌句柄，因此在 UAC 提权后也能正常使用。
    get_dir(&FOLDERID_Profile)
}

fn path_is_equal_or_child(path: &Path, parent: &str) -> bool {
    let path = path.to_string_lossy().replace('/', "\\");
    let parent = parent.trim_end_matches(['\\', '/']).replace('/', "\\");
    if parent.is_empty() {
        return false;
    }
    let path = path.to_ascii_lowercase();
    let parent = parent.to_ascii_lowercase();
    path == parent || path.starts_with(&(parent + "\\"))
}

pub fn in_private_folder(path: &Path) -> bool {
    let path_ids = vec![
        FOLDERID_LocalAppData,
        FOLDERID_LocalAppDataLow,
        FOLDERID_RoamingAppData,
        FOLDERID_Desktop,
        FOLDERID_Documents,
        FOLDERID_Downloads,
    ];
    // 前 检查 userprofile
    let userprofile = get_userprofile();
    if let Ok(userprofile) = userprofile {
        if path_is_equal_or_child(path, &userprofile) {
            return true;
        }
    }
    // 然后 检查 known 目录
    for id in path_ids {
        let known_folder = get_dir(&id);
        if let Ok(known_folder) = known_folder {
            if path_is_equal_or_child(path, &known_folder) {
                return true;
            }
        }
    }
    false
}
