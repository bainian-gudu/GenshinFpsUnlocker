# Build script for Windows (PowerShell / VS Developer Prompt)
# 1) FpsUnlockerStub.dll
# 2) GenshinFpsUnlocker.exe (host)
# 3) GenshinFpsUnlocker.Setup.exe (图形安装器 — 唯一安装方式)
# 4) 组装 dist\Setup\（安装器 + Payload）
# 5) 可选 -Install 启动图形安装器

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

Write-Host "==> Building GenshinFpsUnlocker host" -ForegroundColor Cyan
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
    "-o", $dist
)
if ($SelfContained) {
    $publishArgs += @("--self-contained", "true")
} else {
    $publishArgs += @("--self-contained", "false")
}
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
    Write-Host "==> Building GUI Setup" -ForegroundColor Cyan
    $SetupProj = Join-Path $Root "src/Setup/GenshinFpsUnlocker.Setup.csproj"
    $setupOut = Join-Path $Root "build/setup-publish"
    if (Test-Path $setupOut) { Remove-Item $setupOut -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $setupOut | Out-Null

    & dotnet @(
        "publish", $SetupProj,
        "-c", $Configuration,
        "-r", "win-x64",
        "-o", $setupOut,
        "--self-contained", "false"
    )
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

Write-Host "==> Done. Output: $dist\" -ForegroundColor Green
Get-ChildItem $dist | Format-Table Name, Length
Write-Host ""
Write-Host "Install: .\dist\Setup\GenshinFpsUnlocker.Setup.exe" -ForegroundColor Cyan

if ($Install) {
    $gui = Join-Path $dist "Setup\GenshinFpsUnlocker.Setup.exe"
    if (-not (Test-Path $gui)) { throw "GUI setup missing: $gui" }
    Write-Host "==> Launching installer..." -ForegroundColor Cyan
    Start-Process -FilePath $gui -WorkingDirectory (Join-Path $dist "Setup") -Verb RunAs -Wait
}
