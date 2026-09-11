# 原神帧率解锁 — 构建脚本
# 依赖策略：
#   - 应用 DLL / Stub（静态 CRT + 内嵌 MinHook）等 → 打进 Payload（自带）
#   - 无 Node / Python 等语言运行时依赖
#   - .NET Desktop Runtime → 默认不打包；安装器下载官方 .exe 并静默安装
#   - 安装器 exe 始终 self-contained（无 .NET 也能跑向导）
# 默认：主程序 FDD（包体小）。离线全量：.\build.ps1 -SelfContained
#
# 用法：
#   .\build.ps1
#   .\build.ps1 -Configuration Release
#   .\build.ps1 -SelfContained
#   .\build.ps1 -SkipSetup
#   .\build.ps1 -Install

param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [string]$Generator = "",
    [switch]$Install,
    [switch]$SelfContained,
    [switch]$SkipSetup
)

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $Root

# 主程序：默认 FDD；-SelfContained 时 SC
$hostSelfContained = [bool]$SelfContained
# 安装器：始终 SC（保证无运行库机器可启动安装向导）
$setupSelfContained = $true

$hostLabel = if ($hostSelfContained) { "self-contained" } else { "framework-dependent" }
Write-Host "==> Host publish mode: $hostLabel" -ForegroundColor Cyan
Write-Host "==> Setup publish mode: self-contained (always)" -ForegroundColor Cyan

Write-Host "==> Building FpsUnlockerStub.dll" -ForegroundColor Cyan
$StubBuild = Join-Path $Root "build/stub"
New-Item -ItemType Directory -Force -Path $StubBuild | Out-Null

$cmakeArgs = @("-S", "src/Stub", "-B", $StubBuild)
if ($Generator) {
    $cmakeArgs += @("-G", $Generator)
}
& cmake @cmakeArgs
if ($LASTEXITCODE -ne 0) { throw "cmake configure failed" }

& cmake --build $StubBuild --config $Configuration
if ($LASTEXITCODE -ne 0) { throw "cmake build failed" }

Write-Host "==> Building GenshinFpsUnlocker host ($hostLabel, win-x64)" -ForegroundColor Cyan
$HostProj = Join-Path $Root "src/Host/GenshinFpsUnlocker.Host.csproj"
$dist = Join-Path $Root "dist"
if (Test-Path $dist) {
    Get-ChildItem $dist -Force | Where-Object { $_.Name -ne "Setup" } | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
}
New-Item -ItemType Directory -Force -Path $dist | Out-Null

$publishArgs = @(
    "publish", $HostProj,
    "-c", $Configuration,
    "-r", "win-x64",
    "-o", $dist,
    "--self-contained", $(if ($hostSelfContained) { "true" } else { "false" }),
    "-p:PublishSingleFile=false",
    "-p:IncludeNativeLibrariesForSelfExtract=true",
    "-p:PublishTrimmed=false"
)
& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet publish host failed" }

$candidates = @(
    (Join-Path $StubBuild "bin/FpsUnlockerStub.dll"),
    (Join-Path $StubBuild "bin/$Configuration/FpsUnlockerStub.dll"),
    (Join-Path $StubBuild "$Configuration/FpsUnlockerStub.dll"),
    (Join-Path $StubBuild "FpsUnlockerStub.dll")
)
$stub = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $stub) {
    throw "FpsUnlockerStub.dll not found after build. Searched: $($candidates -join ', ')"
}
Copy-Item $stub (Join-Path $dist "FpsUnlockerStub.dll") -Force

if (-not $SkipSetup) {
    Write-Host "==> Building GUI Setup (self-contained, win-x64)" -ForegroundColor Cyan
    $SetupProj = Join-Path $Root "src/Setup/GenshinFpsUnlocker.Setup.csproj"
    $setupOut = Join-Path $Root "build/setup-publish"
    if (Test-Path $setupOut) { Remove-Item $setupOut -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $setupOut | Out-Null

    $setupArgs = @(
        "publish", $SetupProj,
        "-c", $Configuration,
        "-r", "win-x64",
        "-o", $setupOut,
        "--self-contained", "true",
        "-p:PublishSingleFile=false",
        "-p:PublishTrimmed=false"
    )
    & dotnet @setupArgs
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish setup failed" }

    $setupDist = Join-Path $dist "Setup"
    if (Test-Path $setupDist) { Remove-Item $setupDist -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $setupDist | Out-Null
    New-Item -ItemType Directory -Force -Path (Join-Path $setupDist "Payload") | Out-Null

    Copy-Item (Join-Path $setupOut "*") $setupDist -Recurse -Force
    Get-ChildItem $dist -Force | Where-Object { $_.Name -ne "Setup" } | ForEach-Object {
        Copy-Item $_.FullName (Join-Path $setupDist "Payload") -Recurse -Force
    }

    $setupExe = Join-Path $setupDist "GenshinFpsUnlocker.Setup.exe"
    if (Test-Path $setupExe) {
        Copy-Item $setupExe (Join-Path $dist "GenshinFpsUnlocker.Setup.exe") -Force
        Write-Host "    Installer: $setupExe" -ForegroundColor Green
        Write-Host "    Payload:   $setupDist\Payload\" -ForegroundColor Green
    } else {
        throw "Setup exe not found after publish"
    }
}

Write-Host "==> Done (host=$hostLabel, setup=self-contained). Output: $dist\" -ForegroundColor Green
Get-ChildItem $dist | Format-Table Name, Length
Write-Host ""
Write-Host "Install: .\dist\Setup\GenshinFpsUnlocker.Setup.exe" -ForegroundColor Cyan
Write-Host "Note: 默认 FDD 主程序；安装器在需要时下载官方 .exe 静默安装 .NET 9 Desktop Runtime（无 Node/Python 依赖）。" -ForegroundColor DarkGray

if ($Install) {
    $gui = Join-Path $dist "Setup\GenshinFpsUnlocker.Setup.exe"
    if (-not (Test-Path $gui)) { throw "GUI setup missing: $gui" }
    Write-Host "==> Launching installer..." -ForegroundColor Cyan
    Start-Process -FilePath $gui -WorkingDirectory (Join-Path $dist "Setup") -Verb RunAs -Wait
}
