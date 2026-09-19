<#
.SYNOPSIS
    用项目内的 kachina-installer 把 dist\ 打成安装器 / 更新器 / 便携包。

.DESCRIPTION
    这是本仓库唯一的打包入口。步骤与上游 README 一致：
      1. kachina-builder pack -c kachina.config.json -o <app>\GenshinFpsUnlocker.update.exe
      2. kachina-builder gen  -i <appDir> -m metadata.json -o hashed -r <repoId> -t <ver> -u <updater>
      3. kachina-builder pack -c kachina.config.json -m metadata.json -d hashed -o <app>.Install.<ver>.exe

    产物统一落到 artifacts\：
      GenshinFpsUnlocker.Install.<ver>.exe        离线安装器（含 uninst / update）
      GenshinFpsUnlocker-portable-win-x64.zip     便携包（内含 update.exe）
      GenshinFpsUnlocker_v<ver>.7z                便携 7z（检测到 7z 时才生成）

.PARAMETER DistDir
    宿主发布输出目录，默认 <repo>\dist（由根目录 build.ps1 生成）。

.PARAMETER OutDir
    产物目录，默认 <repo>\artifacts。

.PARAMETER Version
    版本号；留空则从 src\Host\GenshinFpsUnlocker.Host.csproj 的 <Version> 读取。

.PARAMETER SkipKachinaBuild
    不自动构建 kachina-builder（要求 installer\tools\kachina-builder.exe 已存在）。

.PARAMETER ForceKachinaBuild
    强制重新构建 kachina-builder。

.EXAMPLE
    pwsh build.ps1 -SkipSetup      # 只编译
    pwsh installer/pack.ps1        # 只打包（需要 tools\kachina-builder.exe 或本机具备 Rust/pnpm）
    pwsh build.ps1                 # 编译 + 打包（内部调用本脚本）
#>
[CmdletBinding()]
param(
    [string]$DistDir = "",
    [string]$OutDir = "",
    [string]$Version = "",
    [string]$RepoId = "bainian-gudu/GenshinFpsUnlocker",
    [int]$Jobs = 6,
    [switch]$SkipKachinaBuild,
    [switch]$ForceKachinaBuild
)

$ErrorActionPreference = "Stop"

$InstallerDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot     = Split-Path -Parent $InstallerDir
$AppName      = "GenshinFpsUnlocker"
$Config       = Join-Path $InstallerDir "kachina.config.json"
$ToolsDir     = Join-Path $InstallerDir "tools"
$Builder      = Join-Path $ToolsDir "kachina-builder.exe"

if (-not $DistDir) { $DistDir = Join-Path $RepoRoot "dist" }
if (-not $OutDir)  { $OutDir  = Join-Path $RepoRoot "artifacts" }

function Step([string]$msg) { Write-Host "==> $msg" -ForegroundColor Cyan }
function Ok([string]$msg)   { Write-Host "    $msg" -ForegroundColor Green }
# 勿用 $args（PowerShell 自动变量）
function Run([string]$exe, [string[]]$arguments, [string]$what) {
    Write-Host "    $exe $($arguments -join ' ')" -ForegroundColor DarkGray
    & $exe @arguments
    if ($LASTEXITCODE -ne 0) { throw "$what 失败（exit $LASTEXITCODE）" }
}

# ------------------------------------------------------------------ 前置校验
if (-not (Test-Path $Config)) { throw "缺少 Kachina 配置：$Config" }
if (-not (Test-Path (Join-Path $DistDir "$AppName.exe"))) {
    throw "未找到 $DistDir\$AppName.exe —— 请先执行根目录 build.ps1（或 -DistDir 指定发布目录）"
}
if (-not (Test-Path (Join-Path $DistDir "FpsUnlockerStub.dll"))) {
    Write-Warning "$DistDir 内没有 FpsUnlockerStub.dll，安装后无法注入"
}
if (-not (Test-Path (Join-Path $DistDir "StarRailStub.dll"))) {
    Write-Warning "$DistDir 内没有 StarRailStub.dll，星穹铁道的画面效果无法注入（帧率注册表解锁不受影响）"
}
if (-not (Test-Path (Join-Path $DistDir "ui\index.html"))) {
    Write-Warning "$DistDir\ui\index.html 缺失，安装后主界面会走原生兜底页"
}
if (-not $Version) {
    $csproj = Join-Path $RepoRoot "src\Host\GenshinFpsUnlocker.Host.csproj"
    $raw = Get-Content $csproj -Raw
    $Version = if ($raw -match "<Version>([^<]+)</Version>") { $Matches[1].Trim() } else { "1.0.0" }
}
Write-Host "==> 版本 $Version / 仓库 $RepoId" -ForegroundColor Cyan

# ------------------------------------------------------------------ kachina-builder
if ($SkipKachinaBuild) {
    if (-not (Test-Path $Builder)) {
        throw "installer\tools\kachina-builder.exe 不存在，且指定了 -SkipKachinaBuild"
    }
} else {
    # 交给 build-kachina.ps1 判断：builder 缺失、或 kachina 源码比它新才重建
    Step "检查 / 构建项目内 kachina-builder（installer\kachina）"
    $buildArgs = @{}
    if ($ForceKachinaBuild) { $buildArgs.Force = $true }
    & (Join-Path $InstallerDir "build-kachina.ps1") @buildArgs | Out-Null
}
if (-not (Test-Path $Builder)) { throw "kachina-builder.exe 构建失败：$Builder" }
Ok("kachina-builder: $Builder")

# ------------------------------------------------------------------ 暂存应用目录
# 注意不要用 build\（Windows 大小写不敏感，会和历史 Build\ 目录冲突）
$Work   = Join-Path $RepoRoot "out\kachina-pack"
$AppDir = Join-Path $Work $AppName
if (Test-Path $Work) { Remove-Item $Work -Recurse -Force }
New-Item -ItemType Directory -Force -Path $AppDir | Out-Null

Step "暂存应用目录 $AppDir"
Copy-Item (Join-Path $DistDir "*") $AppDir -Recurse -Force
# 这些不该进安装包
foreach ($junk in @("Setup", "$AppName.Install.*.exe", "$AppName.update.exe", "*.7z")) {
    Get-ChildItem $AppDir -Filter $junk -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force
}
foreach ($extra in @("USER_AGREEMENT.txt", "LICENSE", "config.example.json")) {
    $src = Join-Path $RepoRoot $extra
    if ((Test-Path $src) -and -not (Test-Path (Join-Path $AppDir $extra))) {
        Copy-Item $src (Join-Path $AppDir $extra) -Force
    }
}
Ok("已暂存 $((Get-ChildItem $AppDir -Recurse -File).Count) 个文件")

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

# ------------------------------------------------------------------ 1) 更新器
$UpdaterName = "$AppName.update.exe"
$UpdaterPath = Join-Path $AppDir $UpdaterName
Step "pack 更新器 → $UpdaterName"
Run $Builder @("pack", "-c", $Config, "-o", $UpdaterPath) "pack updater"

# ------------------------------------------------------------------ 2) metadata + hashed
Step "gen metadata / hashed"
Push-Location $Work
try {
    Run $Builder @(
        "gen", "-j", "$Jobs", "-i", $AppName,
        "-m", "metadata.json", "-o", "hashed",
        "-r", $RepoId, "-t", $Version,
        "-u", ".\$AppName\$UpdaterName"
    ) "gen"

    # -------------------------------------------------------------- 3) 离线安装器
    $InstallName = "$AppName.Install.$Version.exe"
    Step "pack 离线安装器 → $InstallName"
    Run $Builder @("pack", "-c", $Config, "-m", "metadata.json", "-d", "hashed", "-o", $InstallName) "pack install"

    Copy-Item (Join-Path $Work $InstallName) (Join-Path $OutDir $InstallName) -Force
    Ok("安装器 → $OutDir\$InstallName")
} finally {
    Pop-Location
}

# ------------------------------------------------------------------ 便携包
$PortableName = "$AppName-portable-win-x64"
$PortableDir  = Join-Path $OutDir $PortableName
if (Test-Path $PortableDir) { Remove-Item $PortableDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $PortableDir | Out-Null
Copy-Item (Join-Path $AppDir "*") $PortableDir -Recurse -Force

$zip = Join-Path $OutDir "$PortableName.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path $PortableDir -DestinationPath $zip -Force
Ok("便携 zip → $zip")

$seven = $null
foreach ($cand in @("7z", "$env:ProgramFiles\7-Zip\7z.exe", "${env:ProgramFiles(x86)}\7-Zip\7z.exe")) {
    if ($cand -eq "7z") {
        $cmd = Get-Command 7z -ErrorAction SilentlyContinue
        if ($cmd) { $seven = $cmd.Source; break }
    } elseif (Test-Path $cand) { $seven = $cand; break }
}
if ($seven) {
    $arc = Join-Path $OutDir "${AppName}_v$Version.7z"
    if (Test-Path $arc) { Remove-Item $arc -Force }
    & $seven a -t7z $arc $AppDir -mx=5 -mf=BCJ2 -r -y | Out-Null
    if ($LASTEXITCODE -eq 0) { Ok("便携 7z → $arc") } else { Write-Warning "7z 打包失败（exit $LASTEXITCODE）" }
} else {
    Write-Host "    未检测到 7z，跳过 .7z 便携包" -ForegroundColor DarkYellow
}

Write-Host ""
Step "打包完成，产物在 $OutDir"
Get-ChildItem $OutDir -File | Sort-Object Name | Format-Table Name, @{n = "MB"; e = { [math]::Round($_.Length / 1MB, 2) } } -AutoSize
