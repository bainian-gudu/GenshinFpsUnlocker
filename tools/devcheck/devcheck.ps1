#!/usr/bin/env pwsh
<#
.SYNOPSIS
    本地快速体检：不跑完整构建，就把「编译期会炸的问题」提前抓出来。

.DESCRIPTION
    分层检查，越靠前越快、依赖越少：

      vendor kachina 只用仓库内源码：不是 submodule、快照完整、工作流与打包脚本里
             没有任何从上游（YuehaiTeam/kachina-installer）拉源码或下二进制的动作、
             git 依赖在 Cargo.lock 里锁到 commit、npm 依赖全部来自 registry、
             遥测（Sentry 错误上报 + cocogoat 使用统计）没有被加回来
      ps1    所有 .ps1 的语法解析（PowerShell Parser，秒级，无依赖）
      gen    从 installer/kachina 源码生成检查用的 Rust/TS 源（秒级，无依赖）
      rust   kachina 卸载器逻辑的**类型检查**：整份 uninstall.rs + utils/error.rs 塞进
             一个最小依赖 crate，cargo check --target x86_64-pc-windows-msvc。
             不需要 tauri、不需要 Windows 机器，能抓到绝大多数 Rust 编译错误。
      logic  同一批函数的**行为断言**（mock windows-registry），任意平台可跑。
      native vendored rcedit-sys 的 C++（rescle.cc / librcedit.cpp）真用 MSVC 编一遍。
             只在有 cl.exe 的机器上跑，其它平台 SKIP。
     front  agreement.ts / types.ts 的 tsc --strict 类型检查 + 全部 .vue 的
             @vue/compiler-sfc 编译 + 我们维护文件的 prettier 检查。
      host   src/Host 的 dotnet build（Release，EnableWindowsTargeting）。只证明它编得过，
             不证明它算得对。
      hosttest Host 行为断言（ProcessRunner：退出码 / 超时 / 管道排空上限；
             GameLocator：自定义目录快扫、目录剪枝、快捷方式目标反推）。
             自包含测试台，不依赖 xunit；非 Windows 上只做编译验证后 SKIP。
      ui     src/Ui 的 vite 构建（**不在 all 里**，需要先 npm install）。

    任何一层失败 → 退出码 1。缺工具链的层标记 SKIP 并给出提示（不算失败）。

.EXAMPLE
    pwsh tools/devcheck/devcheck.ps1
.EXAMPLE
    pwsh tools/devcheck/devcheck.ps1 -Layer rust,logic
.EXAMPLE
    pwsh tools/devcheck/devcheck.ps1 -Fix      # 只对 kachina 的两个 .rs 跑 rustfmt

.NOTES
    首次运行会下载：rustup target x86_64-pc-windows-msvc、front/node_modules、
    两个 crate 的 cargo 依赖。-SkipInstall 禁止一切自动安装（缺什么就 SKIP）。
#>
[CmdletBinding()]
param(
    # 逗号或空格分隔的层名。故意用 [string] 而不是 [string[]]：
    # `pwsh -File devcheck.ps1 -Layer rust,logic` 用数组类型会把 "rust,logic" 当成一个值。
    [string]$Layer = 'all',

    # 只跑 rustfmt（写入）修正我们维护的 Rust 文件格式，然后退出
    [switch]$Fix,

    # 不自动安装任何东西（rustup target / npm install）
    [switch]$SkipInstall,

    # 自检：故意注入 15 个错误，确认每一层真的会报错。
    # 5 个只改 tools/devcheck 下的生成文件与 _selftest 临时目录；9 个会临时创建/改写
    # 仓库内的文件（.gitmodules、假工作流、registry.rs、rescle.cc、utils/mod.rs、
    # Cargo.toml、一个临时 .ts、Import-DevCmd.ps1、ProcessRunner.cs、GameLocator.Helpers.cs）。
    # 自检持有仓库改动锁，并在磁盘上留备份：中断后下次运行会先恢复再开工。
    [switch]$SelfTest
)

# npm / npx / node 自身会打 DEP0040（punycode）、DEP0169（url.parse）这类
# 弃用告警，跟本仓库无关，只会把真正需要看的输出淹掉。子进程继承这个变量。
$env:NODE_NO_WARNINGS = '1'

if ($PSVersionTable.PSVersion.Major -lt 7) {
    throw "devcheck 需要 PowerShell 7+（当前 $($PSVersionTable.PSVersion)）。Windows PowerShell 5.1 请用: pwsh -File tools/devcheck/devcheck.ps1"
}

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

# 层内部主动跳过用（类必须在使用前定义）
class LayerSkipped : System.Exception {
    LayerSkipped([string]$message) : base($message) {}
}

$script:IsWin = ($env:OS -eq 'Windows_NT')
$DevCheckRoot = $PSScriptRoot
$RepoRoot = (Resolve-Path (Join-Path $DevCheckRoot '..\..')).Path
$KachinaSrc = Join-Path $RepoRoot 'installer/kachina/src-tauri/src'

. (Join-Path $DevCheckRoot 'lib/RustSource.ps1')
. (Join-Path $DevCheckRoot 'lib/Generate.ps1')
. (Join-Path $DevCheckRoot 'lib/Common.ps1')
. (Join-Path $DevCheckRoot 'lib/Layers.ps1')
. (Join-Path $DevCheckRoot 'lib/CiScripts.ps1')
. (Join-Path $DevCheckRoot 'lib/SelfTest.ps1')

# ---------------------------------------------------------------------------
# -Fix：只做 rustfmt
# ---------------------------------------------------------------------------
if ($Fix) {
    $rustfmt = Get-Tool 'rustfmt'
    if (-not $rustfmt) { throw 'rustfmt 不在 PATH（rustup component add rustfmt）' }
    $targets = @((Join-Path $KachinaSrc 'installer/uninstall.rs'), (Join-Path $KachinaSrc 'builder/pack.rs'))
    foreach ($t in $targets) {
        $r = Invoke-Native -FilePath $rustfmt -Arguments @('--edition', '2021', $t)
        if ($r.ExitCode -ne 0) { throw "rustfmt 失败: $t" }
    }
    Write-Ok "已格式化 $($targets.Count) 个文件"
    return
}

# ---------------------------------------------------------------------------
# 执行
# ---------------------------------------------------------------------------
if ($SelfTest) {
    Write-Host ''
    Write-Host "devcheck 自检 — 仓库根 $RepoRoot" -ForegroundColor White
    Write-Host ''
    $ok = Invoke-SelfTest
    if (-not $ok) { exit 1 }
    exit 0
}

$validLayers = @('all', 'vendor', 'ps1', 'gen', 'rust', 'logic', 'native', 'front', 'host', 'hosttest', 'ui', 'ci')
$requested = @($Layer -split '[,\s]+' | Where-Object { $_ })
if (-not $requested.Count) { $requested = @('all') }
foreach ($r in $requested) {
    if ($validLayers -notcontains $r) { throw "未知的层 '$r'，可选: $($validLayers -join ', ')" }
}
$wanted = if ($requested -contains 'all') { @('vendor', 'ps1', 'gen', 'rust', 'logic', 'native', 'front', 'host', 'hosttest', 'ci') } else { $requested }
# gen 是 rust/logic/front 的前置
if (($wanted -contains 'rust' -or $wanted -contains 'logic' -or $wanted -contains 'front') -and ($wanted -notcontains 'gen')) {
    $wanted = @('gen') + $wanted
}

Write-Host ''
Write-Host "devcheck — 仓库根 $RepoRoot" -ForegroundColor White
Write-Host "层      $($wanted -join ', ')" -ForegroundColor White
Write-Host ''

foreach ($l in $wanted) {
    switch ($l) {
        'vendor' { Invoke-Layer 'vendor kachina 只用仓库内源码' { Test-VendoredSource } }
        'ps1'   { Invoke-Layer 'ps1   PowerShell 脚本语法'    { Test-Ps1Syntax } }
        'gen'   { Invoke-Layer 'gen   生成检查用源码'         { New-GenSources } }
        'rust'  { Invoke-Layer 'rust  kachina 类型检查 msvc'  { Test-RustTypecheck } }
        'logic' { Invoke-Layer 'logic kachina 行为断言'       { Test-RustLogic } }
        'native' { Invoke-Layer 'native vendored C++ (MSVC)'  { Test-NativeDeps } }
        'front' { Invoke-Layer 'front TS 类型 / SFC / 格式'   { Test-Frontend } }
        'host'  { Invoke-Layer 'host  .NET Host 构建'         { Test-Host } }
        'hosttest' { Invoke-Layer 'hosttest Host 行为断言'   { Test-HostTest } }
        'ui'    { Invoke-Layer 'ui    Web UI 构建'            { Test-Ui } }
        'ci'    { Invoke-Layer 'ci    CI 脚本行为'            { Test-CiScripts } }
    }
}

Write-Host ''
Write-Host '════ 汇总 ════' -ForegroundColor White
$script:Results | Format-Table -AutoSize | Out-String -Width 200 | Write-Host

$failed = @($script:Results | Where-Object { $_.Status -eq 'FAIL' })
if ($failed.Count) {
    Write-Host "✗ $($failed.Count) 层失败: $(($failed | ForEach-Object Layer) -join ', ')" -ForegroundColor Red
    exit 1
}
if ($script:SkipCount) {
    Write-Host "✓ 通过（$($script:SkipCount) 层因缺工具链跳过）" -ForegroundColor Yellow
    exit 0
}
Write-Host '✓ 全部通过' -ForegroundColor Green
exit 0
