# HoYoEnhance — 构建脚本
# 依赖策略：
#   - 应用 DLL / Stub（静态 CRT + 内嵌 MinHook）等 → 打进安装载荷（自带）
#   - 无 Node / Python 等语言运行时依赖
#   - .NET Desktop Runtime / VCRedist → Kachina 安装器按 runtimes 配置处理
# 安装器：只有一种 —— Kachina（installer/kachina 源码快照 → kachina-builder）
#         产物 GenshinFpsUnlocker.Install.{ver}.exe，安装目录含 uninst.exe / update.exe
#         宿主自身不再有 --install / --uninstall 等任何自带安装卸载路径
# 默认：主程序 FDD（包体小）。离线全量：.\build.ps1 -SelfContained
#
# 用法：
#   .\build.ps1                     # 编译 + 打包（首次会从源码构建 kachina-builder）
#   .\build.ps1 -Configuration Release
#   .\build.ps1 -SelfContained
#   .\build.ps1 -SkipSetup          # 只编 Host/Stub/UI，不打包
#   .\build.ps1 -SkipKachinaBuild   # 打包，但要求 installer\tools\kachina-builder.exe 已存在
#   .\build.ps1 -ForceKachinaBuild  # 强制重建 kachina-builder
#   .\build.ps1 -Install            # 编完后启动 Install.exe（若已生成）
#   .\installer\pack.ps1            # 只打包（dist\ 已存在时）

param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [string]$Generator = "",
    [switch]$Install,
    [switch]$SelfContained,
    [switch]$SkipSetup,
    [switch]$SkipKachinaBuild,
    [switch]$ForceKachinaBuild
)

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $Root

$hostSelfContained = [bool]$SelfContained
$hostLabel = if ($hostSelfContained) { "self-contained" } else { "framework-dependent" }
Write-Host "==> Host publish mode: $hostLabel" -ForegroundColor Cyan

Write-Host "==> Building Web UI (Vite)" -ForegroundColor Cyan
$uiDir = Join-Path $Root "src/Ui"
$uiDist = Join-Path $uiDir "dist/index.html"
$npm = Get-Command npm -ErrorAction SilentlyContinue
if ($npm) {
    Push-Location $uiDir
    try {
        if (-not (Test-Path (Join-Path $uiDir "node_modules"))) {
            & npm install --no-fund --no-audit
            if ($LASTEXITCODE -ne 0) { throw "npm install failed" }
        }
        & npm run build
        if ($LASTEXITCODE -ne 0) { throw "npm run build failed" }
    } finally { Pop-Location }
    if (-not (Test-Path $uiDist)) { throw "UI dist missing: $uiDist" }
    Write-Host "    UI: $uiDist" -ForegroundColor Green
} elseif (Test-Path $uiDist) {
    Write-Host "    npm not found — using prebuilt src/Ui/dist" -ForegroundColor DarkYellow
} else {
    throw "npm not found and src/Ui/dist missing. Install Node.js or commit a prebuilt UI."
}

Write-Host "==> Building FpsUnlockerStub.dll" -ForegroundColor Cyan
# 勿用 build/：仓库历史上有过 Build/（Kachina 配置），Windows 路径大小写不敏感会冲突；
# 且 build/ 已在 .gitignore 里。中间产物统一放 out/
$StubBuild = Join-Path $Root "out/stub"
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
    Get-ChildItem $dist -Force | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
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

Write-Host "==> Building StarRailStub.dll" -ForegroundColor Cyan
# 星穹铁道模块与原神模块完全独立：只共用 src/Common 下的扫描器与 IPC 协议，
# 业务代码各自维护；中间产物同样放 out/ 下的独立目录。
$StarRailStubBuild = Join-Path $Root "out/stub-starrail"
New-Item -ItemType Directory -Force -Path $StarRailStubBuild | Out-Null

$starRailCmakeArgs = @("-S", "src/StubStarRail", "-B", $StarRailStubBuild)
if ($Generator) {
    $starRailCmakeArgs += @("-G", $Generator)
}
& cmake @starRailCmakeArgs
if ($LASTEXITCODE -ne 0) { throw "star rail cmake configure failed" }

& cmake --build $StarRailStubBuild --config $Configuration
if ($LASTEXITCODE -ne 0) { throw "star rail cmake build failed" }

$starRailCandidates = @(
    (Join-Path $StarRailStubBuild "bin/StarRailStub.dll"),
    (Join-Path $StarRailStubBuild "bin/$Configuration/StarRailStub.dll"),
    (Join-Path $StarRailStubBuild "$Configuration/StarRailStub.dll"),
    (Join-Path $StarRailStubBuild "StarRailStub.dll")
)
$starRailStub = $starRailCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $starRailStub) {
    throw "StarRailStub.dll not found after build. Searched: $($starRailCandidates -join ', ')"
}
Copy-Item $starRailStub (Join-Path $dist "StarRailStub.dll") -Force

# 确保 Web UI 在 publish 输出中（csproj Content 可能因路径/条件漏拷）
$uiDistDir = Join-Path $uiDir "dist"
$uiOut = Join-Path $dist "ui"
if (Test-Path (Join-Path $uiDistDir "index.html")) {
    if (Test-Path $uiOut) { Remove-Item $uiOut -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $uiOut | Out-Null
    Copy-Item (Join-Path $uiDistDir "*") $uiOut -Recurse -Force
    Write-Host "    UI copied -> $uiOut" -ForegroundColor Green
} else {
    Write-Warning "src/Ui/dist missing after build — host may fail to load UI"
}


foreach ($extra in @("LICENSE", "USER_AGREEMENT.txt", "config.example.json")) {
    $p = Join-Path $Root $extra
    if (Test-Path $p) {
        Copy-Item $p (Join-Path $dist $extra) -Force
    }
}
# BetterGI 同源应用图标（托盘/快捷方式旁路文件）
$iconSrc = Join-Path $Root "src/Host/Assets/app.ico"
if (Test-Path $iconSrc) {
    Copy-Item $iconSrc (Join-Path $dist "app.ico") -Force
}
# 位图版本统一用 WebP（安装器 / 文档里引用时按这个名字找）
$iconWebp = Join-Path $Root "src/Host/Assets/app.webp"
if (Test-Path $iconWebp) {
    Copy-Item $iconWebp (Join-Path $dist "app.webp") -Force
}

$installExePath = $null
if (-not $SkipSetup) {
    Write-Host "==> Packaging Kachina installer (installer/pack.ps1)" -ForegroundColor Cyan
    # 打包全部逻辑（含 kachina-builder 的构建）都在 installer/ 下，这里只做转发
    $packArgs = @{
        DistDir = $dist
        OutDir  = (Join-Path $Root "artifacts")
    }
    if ($SkipKachinaBuild)  { $packArgs.SkipKachinaBuild  = $true }
    if ($ForceKachinaBuild) { $packArgs.ForceKachinaBuild = $true }
    & (Join-Path $Root "installer/pack.ps1") @packArgs
    if ($LASTEXITCODE -ne 0) { throw "installer/pack.ps1 failed" }

    $installExePath = Get-ChildItem (Join-Path $Root "artifacts") `
        -Filter "GenshinFpsUnlocker.Install.*.exe" -File -ErrorAction SilentlyContinue |
        Select-Object -First 1 -ExpandProperty FullName
    if ($installExePath) {
        Write-Host "    Installer: $installExePath" -ForegroundColor Green
    }
}

Write-Host "==> Done (host=$hostLabel). Output: $dist\" -ForegroundColor Green
Get-ChildItem $dist | Format-Table Name, Length
Write-Host ""
Write-Host "Install: artifacts\GenshinFpsUnlocker.Install.{ver}.exe (Kachina，含 uninst/update)" -ForegroundColor Cyan
Write-Host "Note: 默认 FDD；安装器可按配置安装 .NET Desktop Runtime 9 + VCRedist。" -ForegroundColor DarkGray

if ($Install) {
    $gui = $installExePath
    if (-not $gui) {
        $gui = Get-ChildItem (Join-Path $Root "artifacts") -Filter "GenshinFpsUnlocker.Install.*.exe" -File -ErrorAction SilentlyContinue |
            Select-Object -First 1 -ExpandProperty FullName
    }
    if (-not $gui -or -not (Test-Path $gui)) {
        throw "Install exe missing: 去掉 -SkipSetup 重跑，或确认 installer\tools\kachina-builder.exe 可用"
    }
    Write-Host "==> Launching installer..." -ForegroundColor Cyan
    Start-Process -FilePath $gui -WorkingDirectory (Split-Path $gui) -Verb RunAs -Wait
}
