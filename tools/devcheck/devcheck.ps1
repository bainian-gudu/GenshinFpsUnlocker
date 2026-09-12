#!/usr/bin/env pwsh
<#
.SYNOPSIS
    本地快速体检：不跑完整构建，就把「编译期会炸的问题」提前抓出来。

.DESCRIPTION
    分层检查，越靠前越快、依赖越少：

      vendor kachina 只用仓库内源码：不是 submodule、快照完整、工作流与打包脚本里
             没有任何从上游（YuehaiTeam/kachina-installer）拉源码或下二进制的动作、
             git 依赖在 Cargo.lock 里锁到 commit、npm 依赖全部来自 registry
      ps1    所有 .ps1 的语法解析（PowerShell Parser，秒级，无依赖）
      gen    从 installer/kachina 源码生成检查用的 Rust/TS 源（秒级，无依赖）
      rust   kachina 卸载器逻辑的**类型检查**：整份 uninstall.rs + utils/error.rs 塞进
             一个最小依赖 crate，cargo check --target x86_64-pc-windows-msvc。
             不需要 tauri、不需要 Windows 机器，能抓到绝大多数 Rust 编译错误。
      logic  同一批函数的**行为断言**（mock windows-registry），任意平台可跑。
      front  agreement.ts / types.ts 的 tsc --strict 类型检查 + 全部 .vue 的
             @vue/compiler-sfc 编译 + 我们维护文件的 prettier 检查。
      host   src/Host 的 dotnet build（Release，EnableWindowsTargeting）。
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

    # 自检：故意往「生成物」里注入 5 个错误，确认每一层真的会报错。
    # 只改 tools/devcheck 下的生成文件与 _selftest 临时目录，不碰仓库源码。
    [switch]$SelfTest
)

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

# ---------------------------------------------------------------------------
# 基础设施
# ---------------------------------------------------------------------------
$script:Results = [System.Collections.Generic.List[object]]::new()
$script:SkipCount = 0

function Get-Tool {
    param([Parameter(Mandatory)][string]$Name)
    $cmd = Get-Command $Name -ErrorAction SilentlyContinue
    if ($cmd) {
        $src = $cmd.Source
        # Windows 上 npm/npx 会同时有 .ps1 与 .cmd；ProcessStartInfo 不能直接执行 .ps1
        if ($script:IsWin -and $src -like '*.ps1') {
            $cmdExe = Join-Path (Split-Path -Parent $src) ($Name + '.cmd')
            if (Test-Path -LiteralPath $cmdExe) { return $cmdExe }
        }
        return $src
    }
    $ext = if ($script:IsWin) { '.exe' } else { '' }
    foreach ($c in @((Join-Path $HOME ".cargo/bin/$Name$ext"), (Join-Path $HOME ".dotnet/$Name$ext"))) {
        if (Test-Path -LiteralPath $c) { return $c }
    }
    return $null
}

function Write-Step { param([string]$Text) Write-Host "── $Text" -ForegroundColor Cyan }
function Write-Ok   { param([string]$Text) Write-Host "   $Text" -ForegroundColor Green }
function Write-Info { param([string]$Text) Write-Host "   $Text" -ForegroundColor DarkGray }
function Write-Bad  { param([string]$Text) Write-Host "   $Text" -ForegroundColor Red }

function Skip-Layer { param([string]$Reason) throw [LayerSkipped]::new($Reason) }

function Invoke-Layer {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][scriptblock]$Body
    )
    Write-Step $Name
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $status = 'PASS'
    $detail = ''
    try {
        $raw = & $Body
        if ($null -ne $raw) { $detail = ($raw | Out-String).Trim() }
    }
    catch [LayerSkipped] {
        $status = 'SKIP'
        $detail = $_.Exception.Message
        $script:SkipCount++
        Write-Info "SKIP: $detail"
    }
    catch {
        $status = 'FAIL'
        $detail = $_.Exception.Message
        Write-Bad $detail
    }
    $sw.Stop()
    if ($status -eq 'PASS') {
        $suffix = if ($detail) { " — $detail" } else { '' }
        Write-Ok ("通过 ({0:n1}s){1}" -f $sw.Elapsed.TotalSeconds, $suffix)
    }
    $script:Results.Add([pscustomobject]@{
            Layer   = $Name.Split(' ')[0]
            Status  = $status
            Seconds = [math]::Round($sw.Elapsed.TotalSeconds, 1)
            Detail  = if ($detail.Length -gt 110) { $detail.Substring(0, 110) + '…' } else { $detail }
        })
}

# 跑外部命令：实时回显尾部输出，返回 (ExitCode, Output)
function Invoke-Native {
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [string[]]$Arguments = @(),
        [string]$WorkingDirectory = $RepoRoot,
        [int]$Tail = 40
    )
    Write-Info "`$ $(Split-Path -Leaf $FilePath) $($Arguments -join ' ')"
    $psi = [System.Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = $FilePath
    $psi.WorkingDirectory = $WorkingDirectory
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.UseShellExecute = $false
    foreach ($a in $Arguments) { $psi.ArgumentList.Add($a) }

    $proc = [System.Diagnostics.Process]::Start($psi)
    $stdout = $proc.StandardOutput.ReadToEnd()
    $stderr = $proc.StandardError.ReadToEnd()
    $proc.WaitForExit()

    $all = (($stdout + "`n" + $stderr).Trim())
    if ($all) {
        $lines = $all -split "`r?`n"
        $shown = if ($lines.Count -gt $Tail) { @("…(省略 $($lines.Count - $Tail) 行)") + $lines[-$Tail..-1] } else { $lines }
        foreach ($l in $shown) { Write-Host "   | $l" -ForegroundColor DarkGray }
    }
    return [pscustomobject]@{ ExitCode = $proc.ExitCode; Output = $all }
}

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
# 各层实现
# ---------------------------------------------------------------------------
function Test-VendoredSource {
    # kachina 必须是「仓库内的源码快照」：CI 与打包脚本都不许从上游
    # YuehaiTeam/kachina-installer 拉源码或下二进制，只能从本仓库构建。
    $notes = [System.Collections.Generic.List[string]]::new()

    # 1) 不是 submodule
    if (Test-Path -LiteralPath (Join-Path $RepoRoot '.gitmodules')) {
        throw '存在 .gitmodules —— kachina 必须是仓库内的源码快照，不是 submodule'
    }

    # 2) 快照完整（缺一个就说明 vendored 源码被误删，CI 会退化成「去别处找」）
    $required = @(
        'installer/kachina/package.json',
        'installer/kachina/pnpm-lock.yaml',
        'installer/kachina/src-tauri/Cargo.toml',
        'installer/kachina/src-tauri/Cargo.lock',
        'installer/kachina/src-tauri/src/installer/uninstall.rs',
        'installer/kachina/src-tauri/src/builder/pack.rs',
        'installer/kachina/src/App.vue',
        'installer/build-kachina.ps1',
        'installer/pack.ps1'
    )
    foreach ($r in $required) {
        if (-not (Test-Path -LiteralPath (Join-Path $RepoRoot $r))) { throw "kachina 源码快照不完整，缺 $r" }
    }
    $notes.Add("快照完整($($required.Count) 个关键文件)")

    # 3) 工作流与打包脚本里不许出现「从外部拉源码/下二进制」的动作
    #    只扫可执行内容：注释行（# 开头）跳过，避免误伤说明性文字
    $scan = @(Get-ChildItem -LiteralPath (Join-Path $RepoRoot '.github/workflows') -Filter '*.yml' -File)
    $scan += @(Get-ChildItem -LiteralPath (Join-Path $RepoRoot 'installer') -Filter '*.ps1' -File)
    $rootBuild = Join-Path $RepoRoot 'build.ps1'
    if (Test-Path -LiteralPath $rootBuild) { $scan += @(Get-Item -LiteralPath $rootBuild) }

    $badPatterns = @(
        'YuehaiTeam', 'kachina-installer\.git', 'releases/download', 'release-downloader',
        'git\s+clone', 'git\s+submodule', 'Invoke-WebRequest', 'Invoke-RestMethod',
        'DownloadFile', 'curl\s', 'wget\s'
    )
    $inBlockComment = $false
    foreach ($f in $scan) {
        $lineNo = 0
        foreach ($line in [System.IO.File]::ReadLines($f.FullName)) {
            $lineNo++
            $t = $line.Trim()
            if ($t -match '^<#' -or $inBlockComment) {
                $inBlockComment = -not ($t -match '#>')
                continue
            }
            if ($t.StartsWith('#')) { continue }
            foreach ($pat in $badPatterns) {
                if ($t -match $pat) {
                    throw "$($f.Name):$lineNo 出现从外部拉取的语句 [$pat]: $t"
                }
            }
        }
    }
    $notes.Add("$($scan.Count) 个工作流/脚本无外部拉取")

    # 4) build.yml 必须真的走「源码构建」这条路
    $buildYml = [System.IO.File]::ReadAllText((Join-Path $RepoRoot '.github/workflows/build.yml'))
    if ($buildYml -notmatch 'build-kachina\.ps1') {
        throw 'build.yml 没有调用 installer/build-kachina.ps1 —— kachina 必须从仓库内源码构建'
    }
    if ($buildYml -notmatch [regex]::Escape("hashFiles('installer/kachina/**')")) {
        throw 'build.yml 的 kachina 缓存 key 没有基于 installer/kachina 源码哈希'
    }
    $notes.Add('CI 从源码构建 + 源码哈希缓存')

    # 5) kachina 的 git 依赖必须在 Cargo.lock 里锁到 commit，且不得指向上游
    $cargoToml = [System.IO.File]::ReadAllText((Join-Path $RepoRoot 'installer/kachina/src-tauri/Cargo.toml'))
    $cargoLock = [System.IO.File]::ReadAllText((Join-Path $RepoRoot 'installer/kachina/src-tauri/Cargo.lock'))
    $gitDeps = @([regex]::Matches($cargoToml, 'git\s*=\s*"([^"]+)"') |
        ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)
    foreach ($g in $gitDeps) {
        if ($g -match 'YuehaiTeam') { throw "kachina 的 cargo 依赖指向上游仓库: $g" }
        $esc = [regex]::Escape($g)
        if ($cargoLock -notmatch "source = `"git\+$esc[^`"]*#[0-9a-f]{40}") {
            throw "git 依赖没有在 Cargo.lock 里锁定 commit（CI 可能拉到漂移的分支）: $g"
        }
    }
    $notes.Add("$($gitDeps.Count) 个 git 依赖已锁 commit")

    # 6) npm 依赖里不许有 git/http/file/link 形式（只能是 registry 版本）
    $pkg = Get-Content -LiteralPath (Join-Path $RepoRoot 'installer/kachina/package.json') -Raw | ConvertFrom-Json
    foreach ($section in @('dependencies', 'devDependencies')) {
        $node = $pkg.$section
        if (-not $node) { continue }
        foreach ($prop in $node.PSObject.Properties) {
            if ($prop.Value -match '^(git|git\+|https?|github|file|link|workspace):' -or
                $prop.Value -match 'YuehaiTeam') {
                throw "kachina 的 npm 依赖 $($prop.Name) 不是 registry 版本: $($prop.Value)"
            }
        }
    }
    $notes.Add('npm 依赖全部来自 registry')

    return ($notes -join '；')
}

function Test-Ps1Syntax {
    $files = @(Get-ChildItem -Path $RepoRoot -Recurse -Filter '*.ps1' -File |
        Where-Object { $_.FullName -notmatch '[\\/](node_modules|target|dist|bin|obj|gen|\.git)[\\/]' })
    $bad = 0
    foreach ($f in $files) {
        $tokens = $null; $errs = $null
        [System.Management.Automation.Language.Parser]::ParseFile($f.FullName, [ref]$tokens, [ref]$errs) | Out-Null
        if ($errs -and $errs.Count) {
            $bad++
            Write-Bad $f.FullName.Substring($RepoRoot.Length + 1)
            foreach ($e in $errs) { Write-Bad "   line $($e.Extent.StartLineNumber): $($e.Message)" }
        }
    }
    if ($bad) { throw "$bad / $($files.Count) 个 .ps1 有语法错误" }
    return "$($files.Count) 个 .ps1 语法通过"
}

function New-GenSources {
    $a = @(New-TypecheckGen -RepoRoot $RepoRoot -DevCheckRoot $DevCheckRoot)
    $b = @(New-LogicGen -RepoRoot $RepoRoot -DevCheckRoot $DevCheckRoot)

    # front：把要类型检查的 TS 原样搬到 front/gen/src（保持相对 import 结构）
    $frontGen = Join-Path $DevCheckRoot 'front/gen/src'
    New-Item -ItemType Directory -Path (Join-Path $frontGen 'utils') -Force | Out-Null
    Copy-Item (Join-Path $RepoRoot 'installer/kachina/src/types.ts') (Join-Path $frontGen 'types.ts') -Force
    Copy-Item (Join-Path $RepoRoot 'installer/kachina/src/utils/agreement.ts') (Join-Path $frontGen 'utils/agreement.ts') -Force
    # prettier 要用与 kachina 相同的配置，否则会按默认风格误报
    $rc = Join-Path $RepoRoot 'installer/kachina/.prettierrc'
    if (Test-Path -LiteralPath $rc) { Copy-Item $rc (Join-Path $DevCheckRoot 'front/.prettierrc') -Force }

    return "Rust $($a.Count + $b.Count) 个 + TS 2 个"
}

function Test-RustTypecheck {
    $cargo = Get-Tool 'cargo'
    if (-not $cargo) { Skip-Layer 'cargo 不在 PATH（https://rustup.rs）' }
    $target = 'x86_64-pc-windows-msvc'

    $rustup = Get-Tool 'rustup'
    if ($rustup) {
        $installed = ((& $rustup target list --installed) -join "`n")
        if ($installed -notmatch [regex]::Escape($target)) {
            if ($SkipInstall) { Skip-Layer "缺少 rustup target $target（去掉 -SkipInstall 可自动装）" }
            Write-Info "安装 rustup target $target …"
            & $rustup target add $target | Out-Null
        }
    }

    $r = Invoke-Native -FilePath $cargo `
        -Arguments @('check', '--target', $target, '--message-format', 'short') `
        -WorkingDirectory (Join-Path $DevCheckRoot 'rust/typecheck') -Tail 60
    if ($r.ExitCode -ne 0) { throw 'cargo check（x86_64-pc-windows-msvc）失败，见上方输出' }
    $warn = ([regex]::Matches($r.Output, 'warning:')).Count
    $what = if ($warn) { "（$warn 条 warning）" } else { '，0 warning' }
    return "uninstall.rs + utils/error.rs 在 $target 上类型检查通过$what"
}

function Test-RustLogic {
    $cargo = Get-Tool 'cargo'
    if (-not $cargo) { Skip-Layer 'cargo 不在 PATH（https://rustup.rs）' }
    $r = Invoke-Native -FilePath $cargo -Arguments @('run', '--quiet') `
        -WorkingDirectory (Join-Path $DevCheckRoot 'rust/logic') -Tail 90
    if ($r.ExitCode -ne 0) { throw '行为断言失败，见上方输出' }
    $summary = ($r.Output -split "`r?`n" | Where-Object { $_ -match '====' } | Select-Object -Last 1)
    if (-not $summary) { $summary = '断言全部通过' }
    return $summary.Trim()
}

function Test-Frontend {
    $node = Get-Tool 'node'
    if (-not $node) { Skip-Layer 'node 不在 PATH' }
    $npm = Get-Tool 'npm'
    if (-not $npm) { Skip-Layer 'npm 不在 PATH' }

    $front = Join-Path $DevCheckRoot 'front'
    if (-not (Test-Path (Join-Path $front 'node_modules'))) {
        if ($SkipInstall) { Skip-Layer 'front/node_modules 不存在（去掉 -SkipInstall 可自动 npm install）' }
        Write-Info '首次运行：安装前端最小依赖（typescript/dompurify/vue/@vue/compiler-sfc/prettier）…'
        $r = Invoke-Native -FilePath $npm -Arguments @('install', '--no-audit', '--no-fund', '--prefer-offline') `
            -WorkingDirectory $front -Tail 10
        if ($r.ExitCode -ne 0) { throw 'npm install 失败' }
    }

    $npx = Get-Tool 'npx'
    if (-not $npx) {
        $ext = if ($script:IsWin) { '.cmd' } else { '' }
        $npx = Join-Path (Split-Path -Parent $npm) "npx$ext"
    }

    $checks = @(
        @{ What = 'tsc --noEmit（agreement.ts / types.ts）'; Exe = $npx; Args = @('tsc', '--noEmit', '-p', 'tsconfig.json') }
        @{ What = '.vue 单文件组件编译'; Exe = $node; Args = @('sfccheck.mjs') }
        # 只查我们自己写的文件：types.ts / App.vue 是上游代码，本身就不满足仓库的
        # prettier 风格，查它们只会天天误报（要格式化请在 installer/kachina 里用上游的工具链）。
        @{ What = 'prettier --check'; Exe = $npx; Args = @('prettier', '--check', 'gen/src/utils/agreement.ts') }
    )
    foreach ($c in $checks) {
        $r = Invoke-Native -FilePath $c.Exe -Arguments $c.Args -WorkingDirectory $front -Tail 30
        if ($r.ExitCode -ne 0) { throw "$($c.What) 失败" }
    }
    return 'tsc --strict / 全部 .vue SFC / prettier 均通过'
}

function Test-Host {
    $dotnet = Get-Tool 'dotnet'
    if (-not $dotnet) { Skip-Layer 'dotnet 不在 PATH（.NET 9 SDK）' }
    $proj = Join-Path $RepoRoot 'src/Host/GenshinFpsUnlocker.Host.csproj'
    if (-not (Test-Path -LiteralPath $proj)) { throw "找不到 $proj" }
    $r = Invoke-Native -FilePath $dotnet `
        -Arguments @('build', $proj, '-c', 'Release', '-p:EnableWindowsTargeting=true', '--nologo', '-v', 'q') `
        -WorkingDirectory $RepoRoot -Tail 30
    if ($r.ExitCode -ne 0) { throw 'dotnet build 失败' }
    $warnLine = ($r.Output -split "`r?`n" | Where-Object { $_ -match 'Warning' } | Select-Object -First 1)
    if ($warnLine) { return $warnLine.Trim() }
    return 'Host 构建通过'
}

function Test-Ui {
    $npm = Get-Tool 'npm'
    if (-not $npm) { Skip-Layer 'npm 不在 PATH' }
    $ui = Join-Path $RepoRoot 'src/Ui'
    if (-not (Test-Path (Join-Path $ui 'node_modules'))) {
        Skip-Layer 'src/Ui/node_modules 不存在（先 cd src/Ui && npm install）'
    }
    $r = Invoke-Native -FilePath $npm -Arguments @('run', 'build') -WorkingDirectory $ui -Tail 30
    if ($r.ExitCode -ne 0) { throw 'src/Ui 构建失败' }
    return 'src/Ui vite build 通过'
}

# ---------------------------------------------------------------------------
# -SelfTest：证明这套检查不是空壳
#
# 做法：先正常生成一次，然后往**生成物**里注入错误（仓库源码一个字都不改），
# 逐个确认对应层会失败。任何一层「注入了错误却没报错」= 自检失败。
# ---------------------------------------------------------------------------
function Invoke-SelfTest {
    $front = Join-Path $DevCheckRoot 'front'
    $tmpDir = Join-Path $DevCheckRoot '_selftest'
    $cases = [System.Collections.Generic.List[object]]::new()

    function Add-Case {
        param([string]$Name, [scriptblock]$Mutate, [scriptblock]$Run, [scriptblock]$Cleanup = {})
        $cases.Add([pscustomobject]@{ Name = $Name; Mutate = $Mutate; Run = $Run; Cleanup = $Cleanup })
    }

    # --- 0a) vendor：出现 .gitmodules 就必须报错（kachina 不能是 submodule）---
    $gitmodules = Join-Path $RepoRoot '.gitmodules'
    Add-Case 'vendor 层能抓到 kachina 变成 submodule' `
        -Mutate {
            Set-Content -Path $gitmodules -Encoding utf8 -Value @'
[submodule "installer/kachina"]
	path = installer/kachina
	url = https://example.invalid/upstream.git
'@
        } `
        -Run { Test-VendoredSource } `
        -Cleanup { if (Test-Path -LiteralPath $gitmodules) { Remove-Item -LiteralPath $gitmodules -Force } }

    # --- 0b) vendor：工作流里出现从外部拉取的动作就必须报错 ---
    $badWorkflow = Join-Path $RepoRoot '.github/workflows/zz-devcheck-selftest.yml'
    Add-Case 'vendor 层能抓到工作流从上游拉取' `
        -Mutate {
            Set-Content -Path $badWorkflow -Encoding utf8 -Value @'
name: selftest
on: workflow_dispatch
jobs:
  x:
    runs-on: ubuntu-latest
    steps:
      - run: Invoke-WebRequest https://example.invalid/kachina-builder.exe -OutFile installer/tools/kachina-builder.exe
'@
        } `
        -Run { Test-VendoredSource } `
        -Cleanup { if (Test-Path -LiteralPath $badWorkflow) { Remove-Item -LiteralPath $badWorkflow -Force } }

    # --- 1) ps1：临时放一个语法错误的 .ps1 进仓库 ---
    Add-Case 'ps1 层能抓到 PowerShell 语法错误' `
        -Mutate {
            New-Item -ItemType Directory -Path $tmpDir -Force | Out-Null
            Set-Content -Path (Join-Path $tmpDir 'broken.ps1') -Value 'if ($x { Write-Host "unclosed" ' -Encoding utf8
        } `
        -Run { Test-Ps1Syntax }

    # --- 2) rust：往生成的 uninstall.rs 里塞一个类型错误 ---
    Add-Case 'rust 层能抓到 Rust 类型错误' `
        -Mutate {
            $f = Join-Path $DevCheckRoot 'rust/typecheck/src/gen/uninstall.rs'
            Add-Content -Path $f -Value "`nfn _devcheck_selftest() { let _x: u32 = `"不是数字`"; }" -Encoding utf8
        } `
        -Run { Test-RustTypecheck }

    # --- 3) logic：把注册表安全阀的深度要求改成 0 ---
    Add-Case 'logic 层能抓到安全阀被放宽' `
        -Mutate {
            $f = Join-Path $DevCheckRoot 'rust/logic/src/gen/extracted.rs'
            $t = [System.IO.File]::ReadAllText($f)
            $t2 = $t.Replace('Some(_) => segments.len() >= 2,', 'Some(_) => segments.len() >= 0,')
            if ($t2 -eq $t) { throw '注入失败：没找到 is_safe_registry_target 的深度判断（上游改了？）' }
            Write-GeneratedFile -Path $f -Content $t2
        } `
        -Run { Test-RustLogic }

    # --- 4) front/ts：往生成的 agreement.ts 里塞一个类型错误 ---
    Add-Case 'front 层能抓到 TypeScript 类型错误' `
        -Mutate {
            $f = Join-Path $front 'gen/src/utils/agreement.ts'
            Add-Content -Path $f -Value "`nexport const _devcheckSelfTest: number = 'not a number';" -Encoding utf8
        } `
        -Run {
            $npx = Get-Tool 'npx'
            if (-not $npx) { Skip-Layer 'npx 不在 PATH' }
            $r = Invoke-Native -FilePath $npx -Arguments @('tsc', '--noEmit', '-p', 'tsconfig.json') -WorkingDirectory $front -Tail 10
            if ($r.ExitCode -ne 0) { throw 'tsc 报错（符合预期）' }
        }

    # --- 5) front/sfc：一个 template 不闭合的 .vue ---
    Add-Case 'front 层能抓到 .vue 模板错误' `
        -Mutate {
            $d = Join-Path $front '_selftest'
            New-Item -ItemType Directory -Path $d -Force | Out-Null
            Set-Content -Path (Join-Path $d 'Broken.vue') -Encoding utf8 -Value @'
<template>
  <div class="x">
    <span>未闭合
</template>
<script setup lang="ts">
const a: number = 1;
</script>
'@
        } `
        -Run {
            $node = Get-Tool 'node'
            if (-not $node) { Skip-Layer 'node 不在 PATH' }
            $r = Invoke-Native -FilePath $node -Arguments @('sfccheck.mjs', '_selftest') -WorkingDirectory $front -Tail 10
            if ($r.ExitCode -ne 0) { throw 'SFC 编译报错（符合预期）' }
        }

    Write-Step 'selftest 注入错误自检'
    $caught = 0; $missed = 0; $skipped = 0
    foreach ($c in $cases) {
        # 每次都从干净的生成物开始
        New-GenSources | Out-Null
        try {
            & $c.Mutate
        }
        catch {
            $missed++
            Write-Bad "  ✗ $($c.Name) —— 注入失败: $($_.Exception.Message)"
            continue
        }
        $outcome = 'CAUGHT'
        try {
            & $c.Run | Out-Null
            $outcome = 'MISSED'
        }
        catch [LayerSkipped] { $outcome = 'SKIPPED' }
        catch { $outcome = 'CAUGHT' }
        try { & $c.Cleanup } catch { }

        switch ($outcome) {
            'CAUGHT'  { $caught++;  Write-Ok "  ✓ $($c.Name)" }
            'SKIPPED' { $skipped++; Write-Info "  - $($c.Name)（缺工具链，跳过）" }
            'MISSED'  { $missed++;  Write-Bad "  ✗ $($c.Name) —— 注入了错误却没报错，这层是空壳！" }
        }
    }

    # 收尾：删掉临时目录并恢复干净的生成物
    foreach ($d in @($tmpDir, (Join-Path $front '_selftest'))) {
        if (Test-Path -LiteralPath $d) { Remove-Item -LiteralPath $d -Recurse -Force }
    }
    foreach ($f in @((Join-Path $RepoRoot '.gitmodules'),
                     (Join-Path $RepoRoot '.github/workflows/zz-devcheck-selftest.yml'))) {
        if (Test-Path -LiteralPath $f) { Remove-Item -LiteralPath $f -Force }
    }
    New-GenSources | Out-Null

    Write-Host ''
    if ($missed) {
        Write-Host "✗ 自检失败：$missed 个注入错误没被抓到" -ForegroundColor Red
        return $false
    }
    Write-Host "✓ 自检通过：$caught 个注入错误全部被抓到$(if ($skipped) { "，$skipped 个因缺工具链跳过" } else { '' })" -ForegroundColor Green
    return $true
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

$validLayers = @('all', 'vendor', 'ps1', 'gen', 'rust', 'logic', 'front', 'host', 'ui')
$requested = @($Layer -split '[,\s]+' | Where-Object { $_ })
if (-not $requested.Count) { $requested = @('all') }
foreach ($r in $requested) {
    if ($validLayers -notcontains $r) { throw "未知的层 '$r'，可选: $($validLayers -join ', ')" }
}
$wanted = if ($requested -contains 'all') { @('vendor', 'ps1', 'gen', 'rust', 'logic', 'front', 'host') } else { $requested }
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
        'front' { Invoke-Layer 'front TS 类型 / SFC / 格式'   { Test-Frontend } }
        'host'  { Invoke-Layer 'host  .NET Host 构建'         { Test-Host } }
        'ui'    { Invoke-Layer 'ui    Web UI 构建'            { Test-Ui } }
    }
}

Write-Host ''
Write-Host '════ 汇总 ════' -ForegroundColor White
$script:Results | Format-Table -AutoSize | Out-String -Width 160 | Write-Host

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
