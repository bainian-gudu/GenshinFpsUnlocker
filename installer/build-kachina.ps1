<#
.SYNOPSIS
    从 installer/kachina（上游 kachina-installer 源码快照）构建 kachina-builder.exe。

.DESCRIPTION
    产物：installer\tools\kachina-builder.exe
    该文件是「打包器 CLI + 安装器 GUI 模板」的二进制拼接体，由上游 package.json
    的 build 脚本生成，本脚本只负责准备工具链并把产物拷到 tools\ 下。

    仅在 Windows 上可运行（上游依赖 MSVC、windows crate、Tauri/WebView2）。

.PARAMETER Force
    即使 tools\kachina-builder.exe 已存在也重新构建。

.PARAMETER Toolchain
    Rust 工具链，默认 nightly（上游 src-tauri\rust-toolchain.toml 指定）。

.EXAMPLE
    pwsh installer/build-kachina.ps1
    pwsh installer/build-kachina.ps1 -Force
#>
[CmdletBinding()]
param(
    [switch]$Force,
    [string]$Toolchain = "nightly"
)

$ErrorActionPreference = "Stop"

$InstallerDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot     = Split-Path -Parent $InstallerDir
$KachinaDir   = Join-Path $InstallerDir "kachina"
$ToolsDir     = Join-Path $InstallerDir "tools"
$BuilderOut   = Join-Path $ToolsDir "kachina-builder.exe"

# 上游 build 脚本的输出位置（target 三元组固定）
$TargetTriple  = "x86_64-win7-windows-msvc"
$ReleaseDir    = Join-Path $KachinaDir "src-tauri\target\$TargetTriple\release"
$BuiltBuilder  = Join-Path $ReleaseDir "kachina-builder.exe"

function Step([string]$msg) { Write-Host "==> $msg" -ForegroundColor Cyan }
function Ok([string]$msg)   { Write-Host "    $msg" -ForegroundColor Green }

function Require([string]$name, [string]$hint) {
    $cmd = Get-Command $name -ErrorAction SilentlyContinue
    if (-not $cmd) { throw "缺少 $name。$hint" }
    return $cmd.Source
}

if ($env:OS -ne "Windows_NT") {
    throw "kachina-builder 只能在 Windows 上构建（Tauri + MSVC + windows crate）。"
}

if (-not (Test-Path (Join-Path $KachinaDir "package.json"))) {
    throw "未找到 $KachinaDir\package.json —— 上游源码快照缺失。"
}

if ((Test-Path $BuilderOut) -and -not $Force) {
    Ok("已存在 $BuilderOut（用 -Force 重新构建）")
    return $BuilderOut
}

New-Item -ItemType Directory -Force -Path $ToolsDir | Out-Null

# ---------------------------------------------------------------- 工具链检查
Step "检查工具链"
$null = Require "rustup" "请安装 Rust：https://rustup.rs （需 MSVC 生成工具）"
$null = Require "cargo"  "rustup 安装后重开终端"
$null = Require "node"   "请安装 Node.js 20+"

Step "安装 Rust 工具链 $Toolchain + rust-src（build-std 需要）"
& rustup toolchain install $Toolchain --profile minimal
if ($LASTEXITCODE -ne 0) { throw "rustup toolchain install $Toolchain 失败" }
& rustup component add rust-src --toolchain $Toolchain
if ($LASTEXITCODE -ne 0) { throw "rustup component add rust-src 失败" }

# pnpm：优先 corepack（Node 自带），退回 npm 全局安装
$pnpm = Get-Command pnpm -ErrorAction SilentlyContinue
if (-not $pnpm) {
    Step "启用 pnpm（corepack）"
    $corepack = Get-Command corepack -ErrorAction SilentlyContinue
    if ($corepack) {
        & corepack enable
        & corepack prepare pnpm@10.17.0 --activate
    }
    $pnpm = Get-Command pnpm -ErrorAction SilentlyContinue
}
if (-not $pnpm) {
    Step "corepack 不可用，改用 npm 安装 pnpm"
    & npm install -g pnpm@10.17.0 --no-fund --no-audit
    if ($LASTEXITCODE -ne 0) { throw "npm install -g pnpm 失败" }
    $pnpm = Get-Command pnpm -ErrorAction SilentlyContinue
}
if (-not $pnpm) { throw "pnpm 不可用，无法构建 kachina-installer" }
Ok("pnpm: $($pnpm.Source)")

# ---------------------------------------------------------------- 构建
Push-Location $KachinaDir
try {
    Step "pnpm install（frozen-lockfile）"
    & pnpm install --frozen-lockfile
    if ($LASTEXITCODE -ne 0) { throw "pnpm install 失败" }

    Step "pnpm build（tauri build → $TargetTriple，-Z build-std，LTO；首次冷构建较慢）"
    # 上游 build 脚本内部用 cmd 内建 ren/del/copy /b 拼接 builder + installer，
    # 必须经由 pnpm 走 cmd.exe 执行，这里不要自己重排命令。
    & pnpm build
    if ($LASTEXITCODE -ne 0) { throw "pnpm build 失败" }
} finally {
    Pop-Location
}

if (-not (Test-Path $BuiltBuilder)) {
    # 兜底：个别环境下 tauri 会落到不带三元组的 target\release
    $alt = Join-Path $KachinaDir "src-tauri\target\release\kachina-builder.exe"
    if (Test-Path $alt) { $BuiltBuilder = $alt }
}
if (-not (Test-Path $BuiltBuilder)) {
    throw "构建结束但未找到 kachina-builder.exe（预期 $ReleaseDir）"
}

Copy-Item $BuiltBuilder $BuilderOut -Force
$sizeMb = [math]::Round((Get-Item $BuilderOut).Length / 1MB, 2)
Ok("kachina-builder → $BuilderOut ($sizeMb MB)")

# 自检：能打印帮助即认为可用
& $BuilderOut --help *> $null
if ($LASTEXITCODE -ne 0) { Write-Warning "kachina-builder --help 返回 $LASTEXITCODE（可能是拼接产物的正常行为）" }

return $BuilderOut
