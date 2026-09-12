# 生成 tools/devcheck/rust/*/src/gen 下的检查用源码（被 devcheck.ps1 dot-source）
#
# 生成物一律不入库（见 .gitignore 的 tools/devcheck/**/src/gen/）。

Set-StrictMode -Version Latest

# logic crate 要抽取并断言的 item 清单。
# 这里列的每一个都必须在源文件里找得到，否则 Get-RustItem 会抛错 ——
# 上游重命名/删除时 devcheck 会立刻失败，而不是静默少测。
$script:LogicItems = @(
    @{ Kind = 'struct'; Name = 'RegistryCleanupItem' }
    @{ Kind = 'const';  Name = 'REG_TREE_DENY_LEAVES' }
    @{ Kind = 'fn';     Name = 'is_safe_registry_target' }
    @{ Kind = 'fn';     Name = 'apply_registry_cleanup' }
    @{ Kind = 'fn';     Name = 'apply_registry_cleanup_for_all_users' }
    @{ Kind = 'fn';     Name = 'dir_leaf' }
    @{ Kind = 'fn';     Name = 'name_matches' }
    @{ Kind = 'fn';     Name = 'is_safe_shortcut_target' }
    @{ Kind = 'fn';     Name = 'path_eq' }
    @{ Kind = 'fn';     Name = 'is_protected_root' }
    @{ Kind = 'fn';     Name = 'is_safe_delete_target' }
    @{ Kind = 'fn';     Name = 'rm_best_effort' }
    @{ Kind = 'fn';     Name = 'clean_extra_registry' }
    @{ Kind = 'fn';     Name = 'expand_env_vars' }
    @{ Kind = 'fn';     Name = 'expand_path_list' }
    @{ Kind = 'const';  Name = 'PER_USER_CLEANUP_ROOTS' }
    @{ Kind = 'const';  Name = 'PER_USER_DENY_LEAVES' }
    @{ Kind = 'fn';     Name = 'profile_relative_tail' }
    @{ Kind = 'fn';     Name = 'loaded_profile_roots' }
    @{ Kind = 'fn';     Name = 'collect_all_users_cleanup_targets' }
    @{ Kind = 'fn';     Name = 'clean_per_user_leftovers' }
    @{ Kind = 'fn';     Name = 'is_installer_temp_artifact' }
    @{ Kind = 'fn';     Name = 'clean_installer_temp_files' }
)

# pack.rs 里只抽 resolve_agreement（整个文件依赖 builder 的一堆东西，不适合最小 crate）
$script:LogicPackItems = @(
    @{ Kind = 'fn'; Name = 'resolve_agreement' }
)

function Get-DevCheckRepoRoot {
    param([Parameter(Mandatory)][string]$ScriptRoot)
    # tools/devcheck/lib -> 仓库根
    return (Resolve-Path (Join-Path $ScriptRoot '..\..\..')).Path
}

# ---- typecheck：整文件 + 最小依赖 ----
function New-TypecheckGen {
    param(
        [Parameter(Mandatory)][string]$RepoRoot,
        [Parameter(Mandatory)][string]$DevCheckRoot
    )
    $kachina = Join-Path $RepoRoot 'installer/kachina/src-tauri/src'
    $genDir = Join-Path $DevCheckRoot 'rust/typecheck/src/gen'

    $uninstall = Read-RustSource -Path (Join-Path $kachina 'installer/uninstall.rs')
    $error = Read-RustSource -Path (Join-Path $kachina 'utils/error.rs')

    $header = @'
// 生成物，勿手改：由 tools/devcheck/devcheck.ps1 从 installer/kachina/src-tauri/src 复制。
// 与上游的唯一差别是去掉了 #[tauri::command]（本 crate 不依赖 tauri）。
'@

    Write-GeneratedFile -Path (Join-Path $genDir 'uninstall.rs') `
        -Content ($header + "`n" + (Remove-TauriCommandAttr -Text $uninstall.Text) + "`n")
    Write-GeneratedFile -Path (Join-Path $genDir 'utils_error.rs') `
        -Content ($header + "`n" + $error.Text)

    return @(
        (Join-Path $genDir 'uninstall.rs')
        (Join-Path $genDir 'utils_error.rs')
    )
}

# ---- logic：抽取 + mock，可在任意平台跑断言 ----
function New-LogicGen {
    param(
        [Parameter(Mandatory)][string]$RepoRoot,
        [Parameter(Mandatory)][string]$DevCheckRoot
    )
    $kachina = Join-Path $RepoRoot 'installer/kachina/src-tauri/src'
    $genDir = Join-Path $DevCheckRoot 'rust/logic/src/gen'

    $uninstall = Read-RustSource -Path (Join-Path $kachina 'installer/uninstall.rs')
    $pack = Read-RustSource -Path (Join-Path $kachina 'builder/pack.rs')

    $parts = [System.Collections.Generic.List[string]]::new()
    $parts.Add(@'
// 生成物，勿手改：由 tools/devcheck/devcheck.ps1 按名字从
// installer/kachina/src-tauri/src/{installer/uninstall.rs, builder/pack.rs} 抽取。
// 抽取规则见 tools/devcheck/lib/RustSource.ps1；找不到清单里的 item 会直接报错。
// 本文件被 src/main.rs 用 include! 展开到 crate 根，Path/PathBuf 由 main.rs 引入。

// ---- Windows 专有实现的跨平台桩（只影响这两个函数，其余全是上游原码）----
// 桩的语义：路径里含 REPARSE 视为符号链接；含 /windows/ 或以 c:\windows 开头视为系统目录。
// 用例里通过构造这样的路径来触发这两条分支。
fn has_reparse_point(path: &Path) -> bool {
    path.to_string_lossy().contains("REPARSE")
}

fn is_under_system_root(path: &Path) -> bool {
    // 与真实实现同样是**前缀**判定：真实版拿 %SystemRoot% 比前缀，桩用固定前缀。
    // 早先用 contains("/windows/")，会把开始菜单那种
    // `<用户>\AppData\Roaming\Microsoft\Windows\Start Menu\...` 也算成系统目录，
    // 于是「产品开始菜单文件夹要跨用户清掉」这条在 Linux 上根本测不到。
    let t = path.to_string_lossy().to_ascii_lowercase();
    t.starts_with("c:\\windows") || t.starts_with("/windows/")
}
'@)

    foreach ($item in $script:LogicItems) {
        $parts.Add((Get-RustItem -Text $uninstall.Text -Masked $uninstall.Masked `
                    -Kind $item.Kind -Name $item.Name -SourceName 'installer/uninstall.rs'))
    }
    foreach ($item in $script:LogicPackItems) {
        $parts.Add((Get-RustItem -Text $pack.Text -Masked $pack.Masked `
                    -Kind $item.Kind -Name $item.Name -SourceName 'builder/pack.rs'))
    }

    $out = Join-Path $genDir 'extracted.rs'
    Write-GeneratedFile -Path $out -Content (($parts -join "`n`n") + "`n")
    return @($out)
}
