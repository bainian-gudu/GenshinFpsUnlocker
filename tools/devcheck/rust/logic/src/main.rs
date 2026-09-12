//! kachina 卸载/打包逻辑的行为断言（devcheck 第 2 层）。
//!
//! 被测代码是 `src/gen/extracted.rs` —— 由 `tools/devcheck/devcheck.ps1` 按名字从
//! `installer/kachina/src-tauri/src/installer/uninstall.rs` 与 `builder/pack.rs`
//! 原样抽取（清单见 `tools/devcheck/lib/Generate.ps1`）。这里只 mock 两样东西：
//!
//! - `windows_registry`：记录调用，验证「删了什么、没删什么」
//! - `has_reparse_point` / `is_under_system_root`：Windows 专有 API，换成按路径名触发的桩
//!
//! 其余全是上游/本项目的真实代码。断言失败 → 进程退出码 1。

use std::path::{Path, PathBuf};

use std::sync::Mutex;

// ---------- windows-registry 0.5.3 的最小桩 ----------
pub mod windows_registry {
        pub struct Key {
        pub name: &'static str,
    }
    impl Key {
        pub const fn hive(name: &'static str) -> Key {
            Key { name }
        }
        pub fn open(&self, path: &str) -> Result<Key, Box<dyn std::error::Error>> {
            super::log(&format!("open {}\\{}", self.name, path));
            Ok(Key {
                name: Box::leak(format!("{}\\{}", self.name, path).into_boxed_str()),
            })
        }
        pub fn remove_tree(&self, path: &str) -> Result<(), Box<dyn std::error::Error>> {
            super::log(&format!("TREE {}\\{}", self.name, path));
            Ok(())
        }
        pub fn remove_value(&self, name: &str) -> Result<(), Box<dyn std::error::Error>> {
            super::log(&format!("VALUE {}\\{}", self.name, name));
            Ok(())
        }
        /// 只认 `ProfileImagePath`：返回 `<KCHECK_MOCK_PROFILE_BASE>\<SID>`。
        /// 用绝对路径（而不是 %SystemDrive%\Users\xxx）是为了在 Linux 上也能跑。
        pub fn get_string(&self, name: &str) -> Result<String, Box<dyn std::error::Error>> {
            super::log(&format!("GET {} {name}", self.name));
            if name != "ProfileImagePath" {
                return Err("no such value".into());
            }
            let base = std::env::var("KCHECK_MOCK_PROFILE_BASE").unwrap_or_else(|_| "/kcheck-none".to_string());
            let sid = self.name.rsplit('\\').next().unwrap_or("sid");
            Ok(format!("{base}/{sid}"))
        }
        pub fn keys(&self) -> Result<KeyIterator, Box<dyn std::error::Error>> {
            Ok(KeyIterator {
                items: vec![
                    "S-1-5-18".to_string(),
                    "S-1-5-21-111-222-333-1001".to_string(),
                    "S-1-5-21-111-222-333-1001_Classes".to_string(),
                    ".DEFAULT".to_string(),
                ],
                i: 0,
            })
        }
    }
    pub struct KeyIterator {
        items: Vec<String>,
        i: usize,
    }
    impl Iterator for KeyIterator {
        type Item = String;
        fn next(&mut self) -> Option<String> {
            if self.i < self.items.len() {
                let v = self.items[self.i].clone();
                self.i += 1;
                Some(v)
            } else {
                None
            }
        }
    }
    pub static CURRENT_USER: &Key = &Key::hive("HKCU");
    pub static LOCAL_MACHINE: &Key = &Key::hive("HKLM");
    pub static CLASSES_ROOT: &Key = &Key::hive("HKCR");
    pub static USERS: &Key = &Key::hive("HKU");
}

static CALLS: Mutex<Vec<String>> = Mutex::new(Vec::new());
fn log(s: &str) {
    CALLS.lock().unwrap().push(s.to_string());
}
fn take_calls() -> Vec<String> {
    std::mem::take(&mut *CALLS.lock().unwrap())
}

// ---------- 从源码原样抽取的被测代码 ----------
include!("gen/extracted.rs");

// ---------- 用例 ----------
use std::sync::atomic::{AtomicU32, Ordering};
static PASS: AtomicU32 = AtomicU32::new(0);
static FAIL: AtomicU32 = AtomicU32::new(0);
fn check(name: &str, ok: bool, detail: impl AsRef<str>) {
    if ok {
        PASS.fetch_add(1, Ordering::Relaxed);
        println!("  ok   {name}");
    } else {
        FAIL.fetch_add(1, Ordering::Relaxed);
        println!("  FAIL {name} :: {}", detail.as_ref());
    }
}

fn reg_target_cases() {
    println!("[1] 注册表安全阀 is_safe_registry_target");
    let cases: Vec<(&str, Option<&str>, bool)> = vec![
        // 正常：删 Run 下的自启动值
        (r"Software\Microsoft\Windows\CurrentVersion\Run", Some("App"), true),
        // 深度不够
        ("Software", Some("App"), false),
        (r"Software\App", None, false),
        // 共享容器一律不许整棵删
        (r"Software\Microsoft\Windows\CurrentVersion\Run", None, false),
        (r"Software\Microsoft\Windows\CurrentVersion\RunOnce", None, false),
        (r"Software\Microsoft\Windows\CurrentVersion\Uninstall", None, false),
        (r"Software\Microsoft\Windows\CurrentVersion\Policies", None, false),
        (r"Software\Microsoft\Windows\CurrentVersion\Explorer", None, false),
        (r"Software\Classes", None, false),
        // 单个 ARP 项 / 产品自己的键：允许
        (r"Software\Microsoft\Windows\CurrentVersion\Uninstall\MyApp", None, true),
        (r"Software\MyCompany\MyApp", None, true),
    ];
    for (k, v, want) in cases {
        let got = is_safe_registry_target(k, v);
        check(
            &format!("{k} value={v:?} => {want}"),
            got == want,
            format!("got {got}"),
        );
    }
}

fn clean_registry_cases() {
    println!("[2] clean_extra_registry 实际调用（含 value 为空的回归）");
    let items = vec![
        // 正常项：HKCU + 每个已加载 SID
        RegistryCleanupItem {
            hive: "HKCU".into(),
            key: r"Software\Microsoft\Windows\CurrentVersion\Run".into(),
            value: Some("GenshinFpsUnlocker".into()),
        },
        // value 为空字符串：必须整条跳过，绝不能变成 remove_tree
        RegistryCleanupItem {
            hive: "HKCU".into(),
            key: r"Software\Microsoft\Windows\CurrentVersion\Run".into(),
            value: Some("   ".into()),
        },
        // 不安全的整棵删除：必须跳过
        RegistryCleanupItem {
            hive: "HKLM".into(),
            key: r"Software\Microsoft\Windows\CurrentVersion\Run".into(),
            value: None,
        },
        // 未知根键：跳过
        RegistryCleanupItem {
            hive: "HKPD".into(),
            key: r"Software\Foo\Bar".into(),
            value: None,
        },
    ];
    let _ = take_calls();
    clean_extra_registry(&items);
    let calls = take_calls();
    let value_calls = calls.iter().filter(|c| c.starts_with("VALUE ")).count();
    let tree_calls = calls.iter().filter(|c| c.starts_with("TREE ")).count();
    check(
        "HKCU 自启动值：主 hive + 1 个真实 SID = 2 次删值",
        value_calls == 2,
        format!("{calls:?}"),
    );
    check(
        "全程没有任何 remove_tree（空 value / 共享容器都被拦）",
        tree_calls == 0,
        format!("{calls:?}"),
    );
    check(
        "跳过 S-1-5-18 / .DEFAULT / *_Classes",
        !calls.iter().any(|c| c.contains("S-1-5-18")
            || c.contains(".DEFAULT")
            || c.contains("_Classes")),
        format!("{calls:?}"),
    );
}

fn shortcut_cases() {
    println!("[3] 快捷方式安全阀 is_safe_shortcut_target");
    let tmp: PathBuf = std::env::temp_dir().join("kcheck-lnk");
    let _ = std::fs::remove_dir_all(&tmp);
    let programs = tmp.join("Start Menu").join("Programs");
    std::fs::create_dir_all(programs.join("GenshinFpsUnlocker")).unwrap();
    std::fs::create_dir_all(programs.join("OtherApp")).unwrap();
    std::fs::create_dir_all(tmp.join("Desktop")).unwrap();
    std::fs::create_dir_all(tmp.join("Downloads")).unwrap();
    std::fs::write(tmp.join("Desktop").join("原神帧率解锁.lnk"), b"x").unwrap();
    std::fs::write(tmp.join("Desktop").join("evil.exe"), b"x").unwrap();
    std::fs::write(tmp.join("Downloads").join("原神帧率解锁.lnk"), b"x").unwrap();
    std::fs::write(
        programs.join("GenshinFpsUnlocker").join("原神帧率解锁.lnk"),
        b"x",
    )
    .unwrap();

    let allowed = vec!["GenshinFpsUnlocker".to_string()];
    let p = |v: &Path| v.to_string_lossy().to_string();
    let cases: Vec<(String, bool)> = vec![
        (p(&tmp.join("Desktop").join("原神帧率解锁.lnk")), true),
        (
            p(&programs.join("GenshinFpsUnlocker").join("原神帧率解锁.lnk")),
            true,
        ),
        (p(&programs.join("GenshinFpsUnlocker")), true),
        // 别人的开始菜单程序目录
        (p(&programs.join("OtherApp")), false),
        (p(&programs.join("OtherApp").join("x.lnk")), false),
        // 非 Desktop / 非 Programs 下的 .lnk
        (p(&tmp.join("Downloads").join("原神帧率解锁.lnk")), false),
        // 扩展名不对
        (p(&tmp.join("Desktop").join("evil.exe")), false),
        (p(&tmp.join("Desktop")), false),
        // 相对路径
        ("Desktop\\原神帧率解锁.lnk".to_string(), false),
        ("../x/原神帧率解锁.lnk".to_string(), false),
        // 路径穿越
        (p(&tmp.join("Desktop").join("..").join("..").join("evil.lnk")), false),
        // 系统目录（桩：以 /windows/ 开头）
        ("/windows/system32/evil.lnk".to_string(), false),
        // 符号链接 / junction（桩：路径里含 REPARSE）
        (
            p(&tmp.join("REPARSE").join("Desktop").join("evil.lnk")),
            false,
        ),
        // 名字不属于本产品，且不在 Desktop / Programs\<产品名> 下
        ("/home/u/.config/autostart/app.desktop".to_string(), false),
    ];
    for (path, want) in cases {
        let got = is_safe_shortcut_target(Path::new(&path), &allowed);
        check(&format!("{path} => {want}"), got == want, format!("got {got}"));
    }
    let _ = std::fs::remove_dir_all(&tmp);
}

async fn rm_best_effort_cases() {
    println!("[4] rm_best_effort 只删安全路径");
    let tmp: PathBuf = std::env::temp_dir().join("kcheck-rm");
    let _ = std::fs::remove_dir_all(&tmp);
    let programs = tmp.join("Start Menu").join("Programs");
    std::fs::create_dir_all(programs.join("MyApp")).unwrap();
    std::fs::create_dir_all(tmp.join("Desktop")).unwrap();
    let good = programs.join("MyApp").join("MyApp.lnk");
    let desktop = tmp.join("Desktop").join("MyApp.lnk");
    let other = tmp.join("Desktop").join("keep.exe");
    std::fs::write(&good, b"x").unwrap();
    std::fs::write(&desktop, b"x").unwrap();
    std::fs::write(&other, b"x").unwrap();
    rm_best_effort(
        &[
            good.to_string_lossy().to_string(),
            desktop.to_string_lossy().to_string(),
            other.to_string_lossy().to_string(),
            "/windows/system32/cmd.lnk".to_string(),
        ],
        &["MyApp".to_string()],
    )
    .await;
    check("安全路径已删除", !good.exists() && !desktop.exists(), "");
    check("不安全路径原样保留", other.exists(), p(&other));
    let _ = std::fs::remove_dir_all(&tmp);
}

fn p(v: &Path) -> String {
    v.to_string_lossy().to_string()
}

fn agreement_wiring_case() {
    println!("[5] 协议内联到安装器（真实仓库配置）");
    // 仓库根：CARGO_MANIFEST_DIR = <repo>/tools/devcheck/rust/logic
    let repo = PathBuf::from(env!("CARGO_MANIFEST_DIR"))
        .ancestors()
        .nth(4)
        .expect("repo root")
        .to_path_buf();
    let cfg_path = repo.join("installer/kachina.config.json");
    let raw = std::fs::read_to_string(&cfg_path).expect("read config");
    let mut config: serde_json::Value = serde_json::from_str(&raw).expect("parse config");
    let before = config.get("agreement").is_some();
    resolve_agreement(&mut config, &cfg_path);
    let obj = config.as_object().unwrap();
    let ag = obj.get("agreement").and_then(|v| v.as_object());
    check("配置里原本没有内联 agreement", !before, "");
    check(
        "resolve_agreement 写出了 agreement 对象",
        ag.is_some(),
        "".to_string(),
    );
    if let Some(ag) = ag {
        let content = ag.get("content").and_then(|v| v.as_str()).unwrap_or("");
        let format = ag.get("format").and_then(|v| v.as_str()).unwrap_or("");
        let title = ag.get("title").and_then(|v| v.as_str()).unwrap_or("");
        let src = std::fs::read_to_string(repo.join("USER_AGREEMENT.txt"))
            .expect("read USER_AGREEMENT.txt");
        check("title == 用户协议", title == "用户协议", title);
        check("format == text", format == "text", format);
        check(
            "content 与 USER_AGREEMENT.txt 完全一致",
            content == src.replace("\r\n", "\n").trim_end(),
            format!("len={} vs {}", content.len(), src.trim_end().len()),
        );
        check("content 非空", content.len() > 100, content.len().to_string());
        // 内联后必须仍是合法 JSON（会被写进 uninst/inst 的 pack config）
        let dumped = serde_json::to_string(&config).expect("serialize");
        let reparsed: serde_json::Value = serde_json::from_str(&dumped).expect("reparse");
        check(
            "内联结果可重新序列化为合法 JSON",
            reparsed["agreement"]["content"] == serde_json::Value::String(content.to_string()),
            "",
        );
    }
    // 反例：agreementFile 指向不存在的文件时，不得写出 content。
    // resolve_agreement 内部会往 stderr 打一条 "Warning: failed to read agreementFile ..."，
    // 这正是我们要的行为，但光看日志会以为是故障 —— 所以先用 eprintln 把说明打到
    // 同一个流里，让它紧挨着那条 Warning（devcheck 是把 stdout / stderr 分别读完再拼的，
    // 用 println 打说明会跑到前半段去，跟 Warning 分家）。
    eprintln!("（预期告警 ↓ 反例用例：agreementFile 指向不存在的文件，应告警且不写出 content）");
    let mut bad: serde_json::Value = serde_json::from_str(
        r#"{"agreementFile":"../NO_SUCH_FILE.txt","agreementFormat":"text"}"#,
    )
    .unwrap();
    resolve_agreement(&mut bad, &cfg_path);
    let has_content = bad
        .get("agreement")
        .and_then(|v| v.get("content"))
        .and_then(|v| v.as_str())
        .is_some_and(|s| !s.is_empty());
    check("协议文件缺失时不写出 content（链接保持不可点）", !has_content, "");
}


fn delete_target_cases() {
    println!("[6] 用户数据 / 额外目录安全阀 is_safe_delete_target");
    let lad = std::env::temp_dir().join("kcheck-lad");
    std::env::set_var("LOCALAPPDATA", &lad);
    // 一律用平台相关的临时目录拼：写死 /tmp/... 在 Windows 上不是绝对路径
    // （没有盘符），会被「必须绝对路径」这条先拦掉，断言就测不到它想测的规则。
    let public = std::env::temp_dir().join("kcheck-public");
    std::env::set_var("PUBLIC", &public);
    // 受保护的 Shell 容器按「配置目录 + 相对组件」推出来，所以这里把三个变量
    // 都指到同一个假配置目录上（下面 [9]/[10] 还会再改 USERPROFILE，互不影响）
    let shell_profile = std::env::temp_dir().join("kcheck-shellprofile");
    std::env::set_var("USERPROFILE", &shell_profile);
    std::env::set_var("APPDATA", shell_profile.join("AppData").join("Roaming"));
    std::env::set_var("LOCALAPPDATA", &lad);
    let cases: Vec<(String, bool)> = vec![
        // 正常：产品自己的数据目录
        (p(&lad.join("GenshinFpsUnlocker")), true),
        (p(&lad.join("GenshinFpsUnlocker").join("logs")), true),
        // 受保护根本身
        (p(&lad), false),
        (p(&public), false),
        // 层级太浅 / 根
        ("/tmp".to_string(), false),
        ("/".to_string(), false),
        // 用户配置目录下面一层的 Shell 容器：产品目录一定在它们**下面**，
        // 配置里少写一段就会把桌面 / 文档 / 开始菜单整个端掉，必须拦
        (p(&shell_profile.join("Desktop")), false),
        (p(&shell_profile.join("Documents")), false),
        (p(&shell_profile.join("Downloads")), false),
        (p(&shell_profile.join("AppData")), false),
        (p(&shell_profile.join("AppData").join("Local")), false),
        (p(&shell_profile.join("AppData").join("Roaming")), false),
        (
            p(
                &shell_profile
                    .join("AppData")
                    .join("Roaming")
                    .join("Microsoft")
                    .join("Windows")
                    .join("Start Menu")
                    .join("Programs"),
            ),
            false,
        ),
        // 但容器下面一层的产品目录仍然放行（含 .lnk 文件）
        (p(&shell_profile.join("Documents").join("GenshinFpsUnlocker")), true),
        (
            p(&shell_profile.join("Desktop").join("GenshinFpsUnlocker.lnk")),
            true,
        ),
        // 形状不合法
        ("AppData/Local/X".to_string(), false),
        (
            p(&std::env::temp_dir().join("a").join("..").join("b")),
            false,
        ),
        // 系统目录（桩：以 /windows/ 开头）
        ("/windows/system32/drivers".to_string(), false),
        // 符号链接（桩：含 REPARSE）
        (p(&lad.join("REPARSE").join("x")), false),
    ];
    for (path, want) in cases {
        let got = is_safe_delete_target(Path::new(&path));
        check(&format!("{path} => {want}"), got == want, format!("got {got}"));
    }
}

fn path_eq_cases() {
    println!("[7] path_eq 归一化");
    let cases: Vec<(&str, &str, bool)> = vec![
        (r"C:\Users\Public", "C:/Users/Public/", true),
        (r"C:\Users\Public\", r"C:\users\PUBLIC", true),
        ("C:", r"C:\", true),
        (r"C:\a", r"C:\b", false),
        (r"C:\Program Files\App", r"C:\Program Files", false),
    ];
    for (a, b, want) in cases {
        let got = path_eq(Path::new(a), Path::new(b));
        check(&format!("{a} == {b} => {want}"), got == want, format!("got {got}"));
    }
}


fn expand_env_cases() {
    println!("[8] %VAR% 展开（配置里的用户数据路径）");
    // 同上：期望值必须是「本平台认得的绝对路径」，否则 is_safe_delete_target
    // 与断言里的 is_absolute() 在 Windows 上会假失败。
    let lad = std::env::temp_dir()
        .join("kcheck-expand")
        .join("AppData")
        .join("Local");
    let lad_str = p(&lad);
    std::env::set_var("KCHECK_LAD", &lad_str);
    let cases: Vec<(String, String)> = vec![
        (
            "%KCHECK_LAD%/GenshinFpsUnlocker".to_string(),
            format!("{lad_str}/GenshinFpsUnlocker"),
        ),
        // 未知变量原样保留：宁可少删，也不要拼出半个路径去删
        (
            "%KCHECK_NOPE%/GenshinFpsUnlocker".to_string(),
            "%KCHECK_NOPE%/GenshinFpsUnlocker".to_string(),
        ),
        ("C:\\a\\b".to_string(), "C:\\a\\b".to_string()),
        ("100% done".to_string(), "100% done".to_string()),
        ("a%%b".to_string(), "a%b".to_string()),
    ];
    for (input, want) in cases {
        let got = expand_env_vars(&input);
        check(&format!("{input:?} => {want:?}"), got == want, format!("got {got:?}"));
    }
    // 回归：不展开 %VAR% 的话，安全阀会因为「不是绝对路径」把整条跳过 ——
    // 这正是「勾了删除用户数据也一个字节没删」的根因。
    let expanded = expand_path_list(&[
        "%KCHECK_LAD%/GenshinFpsUnlocker".to_string(),
        "   ".to_string(),
    ]);
    check(
        "expand_path_list 展开变量并丢掉空项",
        expanded.len() == 1 && Path::new(&expanded[0]).is_absolute(),
        format!("{expanded:?}"),
    );
    let raw = is_safe_delete_target(Path::new("%KCHECK_LAD%/GenshinFpsUnlocker"));
    let done = is_safe_delete_target(Path::new(&expanded[0]));
    check("展开前被安全阀拦掉、展开后放行", !raw && done, format!("raw={raw} done={done}"));
}

fn profile_tail_cases() {
    println!("[9] 多用户清理的相对尾部 profile_relative_tail");
    let profile = std::env::temp_dir().join("kcheck-profile").join("alice");
    std::env::set_var("USERPROFILE", &profile);
    let cases: Vec<(PathBuf, bool)> = vec![
        (profile.join("AppData/Local/GenshinFpsUnlocker"), true),
        (profile.join("AppData/Roaming/GenshinFpsUnlocker"), true),
        (profile.join("Documents/GenshinFpsUnlocker"), true),
        (
            profile.join("AppData/Roaming/Microsoft/Windows/Start Menu/Programs/GenshinFpsUnlocker"),
            true,
        ),
        (profile.join("Desktop/原神帧率解锁.lnk"), true),
        // 桌面上的非 .lnk：别人桌面的文档一概不碰
        (profile.join("Desktop/notes.txt"), false),
        // 尾部只有一级（等于把整个容器目录端掉）：不碰
        (profile.join("AppData"), false),
        (profile.join("Documents"), false),
        (profile.join("Foo"), false),
        // 白名单外的容器：不碰
        (profile.join("Downloads/GenshinFpsUnlocker"), false),
        (profile.join("Videos/a/b"), false),
        // —— 误删除防线：尾巴本身是 Shell 容器时一律拒绝 ——
        // 重放是「把尾巴拼到每个用户目录上」，所以尾巴退化成容器就等于把所有人的
        // 开始菜单 / 文档 / 桌面端掉。现实中触发得到：前端拼的开始菜单文件夹是
        // `Programs\{appName}`，appName 为空就成了 `Programs` 本身。
        (
            profile.join("AppData/Roaming/Microsoft/Windows/Start Menu/Programs"),
            false,
        ),
        (profile.join("AppData/Roaming/Microsoft/Windows/Start Menu"), false),
        (profile.join("AppData/Roaming/Microsoft/Windows"), false),
        (profile.join("AppData/Roaming/Microsoft"), false),
        (profile.join("AppData/Roaming"), false),
        (profile.join("AppData/Local"), false),
        (profile.join("AppData/Local/Microsoft/Windows"), false),
        (profile.join("Documents/GenshinFpsUnlocker/logs"), true),
        // 叶子名大小写不敏感
        (profile.join("AppData/Local/PROGRAMS"), false),
        (profile.join("AppData/Local/Microsoft"), false),
        // AppData 下只有两级 = 直接挂在 Local/Roaming 那一层，只能是容器
        (profile.join("AppData/Whatever"), false),
        // 路径穿越
        (profile.join("AppData/../secret"), false),
        // 压根不在配置目录下（公共开始菜单、安装目录等）：与「哪个用户」无关
        (
            std::env::temp_dir().join("kcheck-elsewhere/AppData/Local/GenshinFpsUnlocker"),
            false,
        ),
    ];
    for (path, want) in cases {
        let got = profile_relative_tail(&path).is_some();
        check(&format!("{} => {want}", p(&path)), got == want, format!("got {got}"));
    }
}

async fn per_user_sweep_cases() {
    println!("[10] 多用户残留清理（ProfileList 扫描 + 尽力删除）");
    let base = std::env::temp_dir().join("kcheck-multiuser");
    let _ = std::fs::remove_dir_all(&base);
    let profiles = base.join("profiles");
    let alice = base.join("alice");
    std::env::set_var("USERPROFILE", &alice);
    std::env::set_var("KCHECK_MOCK_PROFILE_BASE", &profiles);
    std::fs::create_dir_all(&alice).unwrap();

    // mock 的 ProfileList 会给出这些 SID：一个真实用户 + 三个必须跳过的
    let roots = [
        ("user", profiles.join("S-1-5-21-111-222-333-1001")),
        ("system", profiles.join("S-1-5-18")),
        ("default", profiles.join(".DEFAULT")),
        ("classes", profiles.join("S-1-5-21-111-222-333-1001_Classes")),
    ];
    for (_, root) in &roots {
        let data = root.join("AppData/Local/GenshinFpsUnlocker");
        std::fs::create_dir_all(data.join("logs")).unwrap();
        std::fs::write(data.join("config.json"), b"{}").unwrap();
        std::fs::create_dir_all(root.join("Desktop")).unwrap();
        std::fs::write(root.join("Desktop/原神帧率解锁.lnk"), b"x").unwrap();
        std::fs::write(root.join("Desktop/keep.txt"), b"x").unwrap();
    }
    let other = profiles.join("S-1-5-21-111-222-333-1001");

    let configured = vec![
        p(&alice.join("AppData/Local/GenshinFpsUnlocker")),
        p(&alice.join("Desktop/原神帧率解锁.lnk")),
        p(&alice.join("Desktop/keep.txt")),
        p(&base.join("ProgramData/GenshinFpsUnlocker")),
    ];
    let targets = collect_all_users_cleanup_targets(&configured);
    let shown = format!("{targets:?}");
    check(
        "候选只落在真实用户的配置目录里",
        !targets.is_empty() && targets.iter().all(|t| t.starts_with(&other)),
        shown.clone(),
    );
    check(
        "跳过 S-1-5-18 / .DEFAULT / *_Classes",
        !targets.iter().any(|t| {
            t.starts_with(&profiles.join("S-1-5-18"))
                || t.starts_with(&profiles.join(".DEFAULT"))
                || t.starts_with(&profiles.join("S-1-5-21-111-222-333-1001_Classes"))
        }),
        shown.clone(),
    );
    check(
        "其它账户的数据目录进了候选",
        targets.iter().any(|t| path_eq(t, &other.join("AppData/Local/GenshinFpsUnlocker"))),
        shown.clone(),
    );
    check(
        "其它账户桌面上的产品 .lnk 进了候选",
        targets.iter().any(|t| path_eq(t, &other.join("Desktop/原神帧率解锁.lnk"))),
        shown.clone(),
    );
    check(
        "桌面上的非 .lnk 与配置目录外的路径都没进候选",
        !targets.iter().any(|t| t.to_string_lossy().contains("keep.txt")
            || t.to_string_lossy().contains("ProgramData")),
        shown,
    );

    clean_per_user_leftovers(&configured).await;
    check("其它账户的数据目录已删", !other.join("AppData/Local/GenshinFpsUnlocker").exists(), "");
    check("其它账户的桌面 .lnk 已删", !other.join("Desktop/原神帧率解锁.lnk").exists(), "");
    check("其它账户桌面上的别的文件原样保留", other.join("Desktop/keep.txt").exists(), "");
    for (tag, root) in &roots {
        if *tag == "user" {
            continue;
        }
        check(
            &format!("{tag} 配置目录原样保留"),
            root.join("AppData/Local/GenshinFpsUnlocker/config.json").exists(),
            p(root),
        );
    }

    // —— 误删除防线（端到端）：尾巴退化成 Shell 容器时，一个字节都不能删 ——
    // 每个用户目录下都建一份「开始菜单 Programs + 里面的别人家快捷方式」，
    // 只要重放逻辑失手，keep.lnk 就会消失，断言立刻抓到。
    let shell_tails = [
        "AppData/Roaming/Microsoft/Windows/Start Menu/Programs",
        "AppData/Roaming/Microsoft/Windows/Start Menu",
        "AppData/Roaming/Microsoft",
        "AppData/Local",
        "Documents",
        "Desktop",
    ];
    for (_, root) in &roots {
        for tail in &shell_tails {
            std::fs::create_dir_all(root.join(tail)).unwrap();
            std::fs::write(root.join(tail).join("keep.lnk"), b"x").unwrap();
        }
    }
    let shell_inputs: Vec<String> = shell_tails.iter().map(|t| p(&alice.join(t))).collect();
    let shell_targets = collect_all_users_cleanup_targets(&shell_inputs);
    check(
        "Shell 容器尾巴一个候选都不产生",
        shell_targets.is_empty(),
        format!("{shell_targets:?}"),
    );
    clean_per_user_leftovers(&shell_inputs).await;
    check(
        "所有用户的 Programs / Documents / Desktop 原样保留",
        roots.iter().all(|(_, r)| {
            shell_tails
                .iter()
                .all(|t| r.join(t).join("keep.lnk").exists())
        }),
        "",
    );

    // 正向对照：产品自己的开始菜单文件夹仍然要跨用户清掉（别把功能一起防没了）
    let product_sm = "AppData/Roaming/Microsoft/Windows/Start Menu/Programs/GenshinFpsUnlocker";
    for (_, root) in &roots {
        std::fs::create_dir_all(root.join(product_sm)).unwrap();
        std::fs::write(root.join(product_sm).join("卸载.lnk"), b"x").unwrap();
    }
    let sm_inputs = vec![p(&alice.join(product_sm))];
    check(
        "产品开始菜单文件夹进了候选",
        collect_all_users_cleanup_targets(&sm_inputs)
            .iter()
            .any(|t| path_eq(t, &other.join(product_sm))),
        "",
    );
    clean_per_user_leftovers(&sm_inputs).await;
    check(
        "其它账户开始菜单里的产品文件夹已删",
        !other.join(product_sm).exists(),
        p(&other.join(product_sm)),
    );
    check(
        "Programs 目录本身与别人的快捷方式仍在",
        other
            .join("AppData/Roaming/Microsoft/Windows/Start Menu/Programs/keep.lnk")
            .exists(),
        "",
    );
    let _ = std::fs::remove_dir_all(&base);
}

async fn temp_artifact_cases() {
    println!("[11] %TEMP% 里本安装器的残留 clean_installer_temp_files");
    let temp = std::env::temp_dir();
    let mine = [
        "KachinaInstaller.log",
        "Kachina.RuntimePackage.Microsoft.DotNet.DesktopRuntime.9.exe",
        "kachina.uninst.1700000000.exe",
        "kachina.MicrosoftEdgeWebview2Setup.exe",
    ];
    for name in mine {
        std::fs::write(temp.join(name), b"x").unwrap();
    }
    // 含 kachina 但不在白名单里的，必须原样保留
    let keep = ["kcheck-keep-kachina.txt", "Kachina.RuntimePackage.foo.txt", "kachina.exe"];
    for name in keep {
        std::fs::write(temp.join(name), b"x").unwrap();
    }
    check("认得安装器日志", is_installer_temp_artifact("KachinaInstaller.log"), "");
    check(
        "认得运行时安装包（安装失败时会留下几十 MB）",
        is_installer_temp_artifact("Kachina.RuntimePackage.Microsoft.VCRedist.2015+.x64.exe"),
        "",
    );
    check("认得卸载器自身副本", is_installer_temp_artifact("kachina.uninst.1700000000.exe"), "");
    check("认得 WebView2 引导器", is_installer_temp_artifact("kachina.MicrosoftEdgeWebview2Setup.exe"), "");
    check(
        "不按「名字里含 kachina 就删」",
        !keep.iter().any(|n| is_installer_temp_artifact(n)),
        format!("{keep:?}"),
    );

    // 正在运行的卸载器自身（skip 参数）不许删
    let self_path = temp.join("kachina.uninst.1700000000.exe");
    clean_installer_temp_files(Some(&p(&self_path))).await;
    check("白名单内的临时文件已删", !temp.join("KachinaInstaller.log").exists(), "");
    check(
        "运行时安装包已删",
        !temp.join("Kachina.RuntimePackage.Microsoft.DotNet.DesktopRuntime.9.exe").exists(),
        "",
    );
    check("正在运行的卸载器副本被跳过", self_path.exists(), "");
    check(
        "非白名单文件原样保留",
        keep.iter().all(|n| temp.join(n).exists()),
        format!("{keep:?}"),
    );
    for name in mine.iter().chain(keep.iter()) {
        let _ = std::fs::remove_file(temp.join(name));
    }
}

/// 「勾选了才删用户数据」这条契约由三处代码配合成立，任何一处被改掉都会变成
/// 「用户没同意也删」或者「用户同意了却没删」。这里直接对**真实仓库文件**做静态
/// 断言，把三处钉住（和 [5] 协议内联那组同一个套路）：
/// 1. 前端只在勾选时把 `userDataPath` 传下去，没勾传空数组；
/// 2. 配置里每条 `userDataPath` 都是「`%VAR%`/产品子目录」形状，不是容器本身；
/// 3. Rust 侧 `%VAR%` 展开发生在删除安全阀**之前**，且跨用户清理吃的就是同一份
///    `to_be_delete`（于是勾选语义自动跟随，不需要第二套开关）。
fn uninstall_consent_wiring_case() {
    println!("[12] 卸载勾选语义（真实仓库文件）");
    let repo = PathBuf::from(env!("CARGO_MANIFEST_DIR"))
        .ancestors()
        .nth(4)
        .expect("repo root")
        .to_path_buf();

    // —— 1) 前端 ——
    let app = std::fs::read_to_string(repo.join("installer/kachina/src/App.vue")).expect("read App.vue");
    let gated = app
        .split("user_data_path:")
        .nth(1)
        .and_then(|rest| rest.split("extra_uninstall_path:").next())
        .unwrap_or("");
    check("App.vue 里有 user_data_path 这一段", !gated.is_empty(), "");
    check(
        "未勾选「同时删除用户数据」时传空数组（= 一个数据目录都不删）",
        gated.contains("deleteUserData.value") && gated.contains(": []"),
        gated.trim().to_string(),
    );

    // —— 2) 配置 ——
    let cfg: serde_json::Value = serde_json::from_str(
        &std::fs::read_to_string(repo.join("installer/kachina.config.json")).expect("read config"),
    )
    .expect("parse config");
    let reg_name = cfg["regName"].as_str().unwrap_or("");
    let entries = cfg["userDataPath"].as_array().cloned().unwrap_or_default();
    check("userDataPath 非空", !entries.is_empty(), "");
    let bad = entries
        .iter()
        .filter_map(|v| v.as_str())
        .filter(|s| {
            let t = s.trim_end_matches(['/', '\\']);
            // 必须以环境变量开头（这样才会按用户展开）、至少两级、且末级是产品名
            !t.starts_with('%') || t.split(['/', '\\']).count() < 2 || !t.ends_with(reg_name)
        })
        .collect::<Vec<_>>();
    check(
        "每条 userDataPath 都是「%VAR%/…/{regName}」形状，不是容器本身",
        bad.is_empty() && !reg_name.is_empty(),
        format!("bad={bad:?} regName={reg_name:?}"),
    );

    // —— 3) Rust 侧顺序 ——
    let rs = std::fs::read_to_string(
        repo.join("installer/kachina/src-tauri/src/installer/uninstall.rs"),
    )
    .expect("read uninstall.rs");
    let expand_at = rs.find("let to_be_delete = expand_path_list(");
    let guard_at = rs.find("if !is_safe_delete_target(path)");
    check("run_uninstall 里先展开 %VAR%", expand_at.is_some(), format!("{expand_at:?}"));
    check("再走删除安全阀", guard_at.is_some(), format!("{guard_at:?}"));
    check(
        "展开在安全阀之前（顺序反了就等于没修「勾了也不删」）",
        matches!((expand_at, guard_at), (Some(a), Some(b)) if a < b),
        format!("{expand_at:?} vs {guard_at:?}"),
    );
    check(
        "跨用户清理吃的是同一份 to_be_delete（勾选语义自动跟随）",
        rs.contains("clean_per_user_leftovers(&to_be_delete)"),
        "",
    );
    check(
        "跨用户重放带 Shell 容器黑名单（防止把所有人的开始菜单端掉）",
        rs.contains("PER_USER_DENY_LEAVES"),
        "",
    );
}

#[tokio::main]
async fn main() {
    reg_target_cases();
    clean_registry_cases();
    shortcut_cases();
    rm_best_effort_cases().await;
    agreement_wiring_case();
    delete_target_cases();
    path_eq_cases();
    expand_env_cases();
    profile_tail_cases();
    per_user_sweep_cases().await;
    temp_artifact_cases().await;
    uninstall_consent_wiring_case();
    let (pass, fail) = (PASS.load(Ordering::Relaxed), FAIL.load(Ordering::Relaxed));
    println!("\n==== PASS {pass} / FAIL {fail} ====");
    if fail > 0 {
        std::process::exit(1);
    }
}
