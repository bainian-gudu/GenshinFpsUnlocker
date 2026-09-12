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
        // 系统目录（桩：包含 /windows/）
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
    std::env::set_var("PUBLIC", "/tmp/kcheck-public");
    let cases: Vec<(String, bool)> = vec![
        // 正常：产品自己的数据目录
        (p(&lad.join("GenshinFpsUnlocker")), true),
        (p(&lad.join("GenshinFpsUnlocker").join("logs")), true),
        // 受保护根本身
        (p(&lad), false),
        ("/tmp/kcheck-public".to_string(), false),
        // 层级太浅 / 根
        ("/tmp".to_string(), false),
        ("/".to_string(), false),
        // 形状不合法
        ("AppData/Local/X".to_string(), false),
        ("/tmp/a/../b".to_string(), false),
        // 系统目录（桩：含 /windows/）
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

#[tokio::main]
async fn main() {
    reg_target_cases();
    clean_registry_cases();
    shortcut_cases();
    rm_best_effort_cases().await;
    agreement_wiring_case();
    delete_target_cases();
    path_eq_cases();
    let (pass, fail) = (PASS.load(Ordering::Relaxed), FAIL.load(Ordering::Relaxed));
    println!("\n==== PASS {pass} / FAIL {fail} ====");
    if fail > 0 {
        std::process::exit(1);
    }
}
